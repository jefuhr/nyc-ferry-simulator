using System;
using System.Globalization;
using System.Threading;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// Plays a telemetry CSV (CLI or Unity run) back through <see cref="IVesselTelemetrySource"/> so the
	/// instruments can be developed against realistic motion without the simulation bridge. Like the real
	/// fixed-step source it holds the most recent row instead of interpolating, so what the panel shows is
	/// exactly a recorded sample. It follows the contract's run lifecycle:
	/// <list type="bullet">
	/// <item><see cref="HasTelemetry"/> is false until a recording is loaded, before its first row and after its end.</item>
	/// <item><see cref="RunId"/> changes whenever a playback starts: a load, a loop back to the start or a seek backwards.</item>
	/// <item><see cref="Contact"/> latches every row with contact up to the one shown, so rows skipped between frames
	/// still count. A row at time t is the state after physics step round(100 t) - 1, numbered as the bridge numbers them.</item>
	/// <item>The scenario start comes from the CSV's <c>scenario_start_utc</c> header (Unity runs). CLI runs carry none,
	/// so the inspector setting applies and <see cref="ScenarioStartIsFallback"/> says so. A start chosen through
	/// <see cref="StartScenario"/> (the dev scene's scenario picker) takes precedence until the next load.</item>
	/// </list>
	/// Parameters come from <c>vessels/&lt;id&gt;.json</c> for the vessel in the CSV header.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class FakeTelemetrySource : MonoBehaviour, IVesselTelemetrySource
	{
		private static long _lastRunId;

		[Tooltip("Telemetry CSV to play: absolute, or relative to the repository root.")]
		[SerializeField]
		private string csvPath = UIRepoPaths.SampleCrashStopCsv;

		[Tooltip("Scenario start in UTC (ISO 8601) for CSVs without a scenario_start_utc header (CLI runs). The panel marks the clock ASSUMED START while it applies.")]
		[SerializeField]
		private string scenarioStartUtc = "2026-06-15T13:00:00Z";

		[SerializeField]
		private bool playOnStart = true;

		[SerializeField]
		private bool loop = true;

		[SerializeField]
		[Min(0f)]
		private float playbackSpeed = 1f;

		private DateTimeOffset _fallbackStart = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);
		private DateTimeOffset? _chosenStart;
		private ContactLatch[] _latches;
		private int _index = -1;
		private bool _inside;

		public TelemetryRecording Recording { get; private set; }

		/// <summary>Recording time being shown, seconds (the CSV's time_s), clamped to the recording.</summary>
		public double PlaybackTime { get; private set; }

		public bool IsPlaying { get; set; }

		public bool Loop
		{
			get => loop;
			set => loop = value;
		}

		public float PlaybackSpeed
		{
			get => playbackSpeed;
			set => playbackSpeed = Mathf.Max(0f, value);
		}

		/// <summary>Why the last load failed; null after a successful load.</summary>
		public string LoadError { get; private set; }

		/// <summary>Why the vessel parameters are missing; null when they loaded.</summary>
		public string ParametersProblem { get; private set; }

		/// <summary>True while the row shown is inside the recording: false before a load, before the first row and after the end.</summary>
		public bool HasTelemetry => Recording != null && _inside;

		/// <summary>The current playback's run; 0 before the first load. Never reused within a process.</summary>
		public long RunId { get; private set; }

		public ContactLatch Contact { get; private set; } = ContactLatch.None;

		/// <summary>True while <see cref="ScenarioStartUtc"/> is the fallback setting because the recording carries no start.</summary>
		public bool ScenarioStartIsFallback { get; private set; } = true;

		public VesselParameters Parameters { get; private set; }

		public VesselState State { get; private set; }

		public VesselTelemetry Telemetry { get; private set; }

		public ControlInputs Controls { get; private set; }

		public DateTimeOffset ScenarioStartUtc { get; private set; } = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);

		private void Awake()
		{
			SetScenarioStart(scenarioStartUtc);
			if (Recording == null && !string.IsNullOrEmpty(csvPath))
			{
				TryLoadCsv(csvPath);
			}

			IsPlaying = playOnStart;
		}

		private void Update()
		{
			if (IsPlaying && Recording != null)
			{
				Advance(Time.deltaTime * playbackSpeed);
			}
		}

		/// <summary>Loads a CSV and the parameters of the vessel it names. On failure the previous recording stays.</summary>
		public bool TryLoadCsv(string path)
		{
			TelemetryRecording recording;
			try
			{
				recording = TelemetryCsv.Load(UIRepoPaths.Resolve(path));
			}
			catch (Exception error) when (error is TelemetryCsvException || error is System.IO.IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				LoadError = error.Message;
				Debug.LogWarning($"FakeTelemetrySource: {error.Message}");
				return false;
			}

			var parameters = VesselFiles.TryLoad(recording.VesselId, out var problem);
			if (problem != null)
			{
				Debug.LogWarning($"FakeTelemetrySource: {problem} Instruments use generic scales.");
			}

			csvPath = path;
			Load(recording, parameters);
			ParametersProblem = problem;
			return true;
		}

		/// <summary>Plays <paramref name="recording"/> from its start as a new run. <paramref name="parameters"/> may be null.</summary>
		public void Load(TelemetryRecording recording, VesselParameters parameters)
		{
			Recording = recording ?? throw new ArgumentNullException(nameof(recording));
			Parameters = parameters;
			ParametersProblem = null;
			LoadError = null;
			_latches = Latches(recording);
			_chosenStart = null;
			ApplyScenarioStart();
			StartRun();
			Seek(recording.StartTime);
		}

		/// <summary>
		/// Plays the recording again from its first row as a new run, with the clock starting at
		/// <paramref name="scenarioStartUtc"/>: a start someone chose, so the panel does not mark it ASSUMED START.
		/// </summary>
		public void StartScenario(DateTimeOffset scenarioStartUtc)
		{
			_chosenStart = scenarioStartUtc.ToUniversalTime();
			ApplyScenarioStart();
			if (Recording != null)
			{
				StartRun();
				Seek(Recording.StartTime);
				IsPlaying = true;
			}
		}

		/// <summary>Sets the fallback scenario start used while the recording carries none. False, and no change, if it does not parse.</summary>
		public bool SetScenarioStart(string isoUtc)
		{
			if (!DateTimeOffset.TryParse(isoUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
			{
				return false;
			}

			scenarioStartUtc = isoUtc;
			_fallbackStart = start;
			ApplyScenarioStart();
			return true;
		}

		/// <summary>
		/// Shows the most recent row at or before <paramref name="time"/>, clamped to the recording; outside it
		/// there is no telemetry. Going back to an earlier row replays from there, which is a new run.
		/// </summary>
		public void Seek(double time)
		{
			if (Recording == null || double.IsNaN(time))
			{
				return;
			}

			PlaybackTime = Math.Max(Recording.StartTime, Math.Min(Recording.EndTime, time));
			var index = Recording.IndexAtOrBefore(PlaybackTime);
			if (index < _index)
			{
				StartRun();
			}

			_index = index;
			_inside = time >= Recording.StartTime && time <= Recording.EndTime;
			var sample = Recording.Sample(index);
			State = sample.State;
			Telemetry = sample.Telemetry;
			Controls = sample.Controls;
			Contact = _latches[index];
		}

		/// <summary>Moves playback on by <paramref name="seconds"/>, wrapping to a new run when looping or ending the run at the end.</summary>
		public void Advance(double seconds)
		{
			if (Recording == null)
			{
				return;
			}

			var time = PlaybackTime + seconds;
			if (time > Recording.EndTime)
			{
				if (loop && Recording.Duration > 0.0)
				{
					time = Recording.StartTime + (time - Recording.StartTime) % Recording.Duration;
					StartRun();
				}
				else
				{
					// Seek clamps playback to the last row; past it the run is over.
					IsPlaying = false;
				}
			}

			Seek(time);
		}

		private void StartRun()
		{
			RunId = Interlocked.Increment(ref _lastRunId);
			_index = -1;
		}

		private void ApplyScenarioStart()
		{
			var recorded = Recording?.ScenarioStartUtc;
			ScenarioStartIsFallback = _chosenStart == null && recorded == null;
			ScenarioStartUtc = _chosenStart ?? recorded ?? _fallbackStart;
		}

		/// <summary>The contact latch at each row: every row with a positive force counts, as the bridge counts steps.</summary>
		private static ContactLatch[] Latches(TelemetryRecording recording)
		{
			var latches = new ContactLatch[recording.Count];
			var latch = ContactLatch.None;
			for (var i = 0; i < recording.Count; i++)
			{
				var force = recording.ContactForce[i];
				if (force > 0.0)
				{
					var step = ContactMonitor.StepEndingAt(recording.Time[i]);
					var peak = force > latch.PeakContactForce;
					latch = new ContactLatch(latch.StepsWithContact + 1, step, force,
						peak ? force : latch.PeakContactForce, peak ? step : latch.PeakContactStep);
				}

				latches[i] = latch;
			}

			return latches;
		}
	}
}
