using System;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// The fixed-step bridge. Owns one <see cref="ISimulation"/> from <see cref="VesselDynamicsFactory"/> and
	/// advances it in exact <see cref="SimulationClock.FixedTimeStep"/> steps from a time accumulator in
	/// <c>Update</c> (not Unity's 0.02 s <c>FixedUpdate</c>), then draws the vessel interpolated between the
	/// last two physics states. Every run is recorded for replay and debrief. UI and cameras read it through
	/// <see cref="IVesselTelemetrySource"/>.
	/// </summary>
	[DefaultExecutionOrder(-100)]
	[DisallowMultipleComponent]
	public sealed class VesselSimulationRunner : MonoBehaviour, IVesselTelemetrySource
	{
		private const double DropReportIntervalSeconds = 5.0;
		private static long _nextRunId;

		[Tooltip("Scenario to start from. Ignored when a scenario JSON file is set.")]
		[SerializeField]
		private ScenarioAsset scenario;

		[Tooltip("Optional scenario JSON, relative to the repository root (or StreamingAssets in a player).")]
		[SerializeField]
		private string scenarioJsonFile = string.Empty;

		[Tooltip("Component supplying the controls (for example HelmInputController). Empty: the first IControlInputSource on this GameObject.")]
		[SerializeField]
		private MonoBehaviour controlSource;

		[Tooltip("Most physics steps one frame may run; a slower frame drops the excess time and logs it.")]
		[SerializeField]
		[Min(1)]
		private int maxStepsPerFrame = FixedStepDriver.DefaultMaxStepsPerFrame;

		[SerializeField]
		private bool recordRuns = true;

		[Tooltip("Where runs are recorded (never inside Assets). Relative to the Unity project in the editor, persistent data in a player. Empty: artifacts/unity/runs in the editor.")]
		[SerializeField]
		private string recordingDirectory = string.Empty;

		[Tooltip("Telemetry CSV rows per simulated second; must divide 100.")]
		[SerializeField]
		private int telemetrySampleHz = 10;

		[Tooltip("Optional: a recorded run's telemetry CSV to play back (and verify) instead of live controls.")]
		[SerializeField]
		private string replayRecording = string.Empty;

		[Tooltip("Flat placement only: put the render origin at the start position so the vessel starts at Unity (0, 0, 0).")]
		[SerializeField]
		private bool recenterRenderOriginOnStart = true;

		[Tooltip("Flat placement only: Unity height of the calm water surface in the parent frame.")]
		[SerializeField]
		private float waterSurfaceHeight;

		[Tooltip("Transform that is moved to the vessel pose. Empty: this GameObject's transform.")]
		[SerializeField]
		private Transform renderTarget;

		private FixedStepDriver _driver;
		private ISimulationFactory _factory;
		private IWorldGeometry _worldGeometry = EmptyWorldGeometry.Instance;
		private IVesselPlacement _placement;
		private TangentPlanePlacement _flatPlacement;
		private UniformEnvironment _environment;
		private VesselState _initialState;
		private RunRecorder _recorder;
		private RecordedRun _replay;
		private ReplayVerifier _replayVerifier;
		private ReplayResult _lastReplayResult;
		private readonly ContactObserver _contactObserver = new ContactObserver();
		private int _runNumber;
		private bool _runActive;
		private double _lastDropReportTime = double.NegativeInfinity;
		private double _unreportedDrop;
		private int _unreportedDropFrames;

		/// <summary>Raised after a run starts: on initialization and on every reset.</summary>
		public event Action RunStarted;

		/// <summary>Raised when a run ends (reset, replay end or shutdown), after its recording is closed.</summary>
		public event Action RunEnded;

		/// <summary>Raised for every frame that hit the per-frame step guard.</summary>
		public event Action<FrameAdvance> FrameDropped;

		public VesselParameters Parameters { get; private set; }

		public VesselState State => _driver != null ? _driver.CurrentState : default;

		public VesselTelemetry Telemetry => _driver != null ? _driver.Simulation.Telemetry : default;

		public ControlInputs Controls => _driver != null ? _driver.LastControls : ControlInputs.Neutral;

		public DateTimeOffset ScenarioStartUtc { get; private set; }

		public bool HasTelemetry => _runActive && _driver != null && _driver.StepCount > 0 && !_driver.Faulted;

		public long RunId { get; private set; }

		public ContactLatch Contact => _contactObserver.Contact;

		public bool IsInitialized => _driver != null;

		public bool IsPaused { get; private set; }

		/// <summary>Only a new run/reset can reactivate an ended run, including after disable/re-enable.</summary>
		public bool IsRunActive => _runActive;

		/// <summary>True while playing back a recorded run instead of live controls.</summary>
		public bool IsReplaying => _replay != null;

		public FixedStepDriver Driver => _driver;

		public ScenarioSettings Scenario { get; private set; }

		/// <summary>Pose drawn this frame: interpolated between the last two physics states.</summary>
		public VesselPose RenderPose { get; private set; }

		public IVesselPlacement Placement => _placement;

		/// <summary>The flat placement in use, or null when another <see cref="IVesselPlacement"/> places the vessel.</summary>
		public TangentPlanePlacement FlatPlacement => _flatPlacement;

		public IControlInputSource ControlSource => _driver?.Controls;

		/// <summary>The recorder of the current run, or null when not recording.</summary>
		public RunRecorder Recorder => _recorder;

		/// <summary>Telemetry CSV of the most recently finished recording.</summary>
		public string LastRecordingPath { get; private set; }

		/// <summary>Replay comparison so far, when replaying.</summary>
		public ReplayResult ReplayResult => _replayVerifier?.Result ?? _lastReplayResult;

		public bool ReplayCompleted { get; private set; }

		/// <summary>Why the current recording is missing/incomplete, retained for the debrief readout.</summary>
		public string RecordingError { get; private set; }

		/// <summary>The factory used for new runs; <see cref="VesselDynamicsFactory"/> unless replaced before initialization.</summary>
		public ISimulationFactory Factory
		{
			get => _factory ??= new VesselDynamicsFactory();
			set => _factory = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>
		/// Fixed structures the hull can touch (landing faces, piers), built by the world from the data pipeline.
		/// Open water unless set; takes effect at the next run start. A replay of a run needs the same geometry:
		/// recordings carry its <see cref="WorldGeometryDigest"/>, and <see cref="InitializeReplay"/> refuses another.
		/// </summary>
		public IWorldGeometry WorldGeometry
		{
			get => _worldGeometry;
			set => _worldGeometry = value ?? EmptyWorldGeometry.Instance;
		}

		public bool RecordRuns
		{
			get => recordRuns;
			set => recordRuns = value;
		}

		public string RecordingDirectory
		{
			get => SimPaths.ResolveRecordingDirectory(string.IsNullOrEmpty(recordingDirectory) ? SimPaths.DefaultRecordingDirectory : recordingDirectory);
			set => recordingDirectory = value;
		}

		public int TelemetrySampleHz
		{
			get => telemetrySampleHz;
			set => telemetrySampleHz = value;
		}

		public int MaxStepsPerFrame
		{
			get => maxStepsPerFrame;
			set
			{
				maxStepsPerFrame = value;
				if (_driver != null)
				{
					_driver.MaxStepsPerFrame = value;
				}
			}
		}

		/// <summary>
		/// Starts a run from <paramref name="settings"/>. Controls come from <paramref name="controls"/>, else the
		/// inspector's control source, else the first <see cref="IControlInputSource"/> on this GameObject.
		/// </summary>
		public void Initialize(ScenarioSettings settings, IControlInputSource controls = null, VesselParameters parameters = null)
		{
			if (settings == null)
			{
				throw new ArgumentNullException(nameof(settings));
			}

			settings.Validate();
			EndRun();
			_replay = null;
			_replayVerifier = null;
			Scenario = settings;
			Parameters = parameters ?? LoadVessel(settings.vesselFile);
			ScenarioStartUtc = settings.StartUtc;
			_environment = settings.environment.Create();
			_initialState = settings.InitialState(_environment);
			StartRun(controls ?? FindControlSource());
		}

		/// <summary>Plays back <paramref name="run"/> from its recorded start, checking every row as it goes. Not re-recorded.</summary>
		public void InitializeReplay(RecordedRun run, VesselParameters parameters = null)
		{
			if (run == null)
			{
				throw new ArgumentNullException(nameof(run));
			}

			if (run.Header.TryGetValue(WorldGeometryDigest.HeaderKey, out var recordedGeometry) &&
				!string.Equals(recordedGeometry, WorldGeometryDigest.Of(_worldGeometry), StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					$"{Path.GetFileName(run.TelemetryPath)} was recorded against world geometry {recordedGeometry}, but this runner has " +
					$"{WorldGeometryDigest.Of(_worldGeometry) ?? _worldGeometry.GetType().FullName}; set the same WorldGeometry before replaying.");
			}

			EndRun();
			Scenario = null;
			Parameters = parameters ?? LoadVessel(run.VesselFile);
			ScenarioStartUtc = run.ScenarioStartUtc;
			_environment = run.Environment;
			_initialState = run.InitialState;
			_replay = run;
			StartRun(run.Inputs);
		}

		/// <summary>Ends the current run (closing its recording) and starts a new one from the scenario start.</summary>
		public void ResetRun()
		{
			if (!IsInitialized)
			{
				return;
			}

			var controls = _driver.Controls;
			EndRun();
			StartRun(controls);
		}

		public void Pause() => IsPaused = true;

		public void Resume() => IsPaused = false;

		public void TogglePause() => IsPaused = !IsPaused;

		/// <summary>
		/// Advances one frame of <paramref name="deltaTime"/> seconds unless paused. <c>Update</c> calls this with
		/// the frame time; tests may call it directly with simulated frame times.
		/// </summary>
		public FrameAdvance AdvanceFrame(double deltaTime)
		{
			if (!IsInitialized || !_runActive || IsPaused)
			{
				return new FrameAdvance(0, 0.0);
			}

			var advance = _driver.Advance(deltaTime);
			if (_recorder?.Failure != null)
			{
				// Detach after the observer loop so other observers still see every step in this frame.
				_driver.RemoveObserver(_recorder);
				ReportRecordingFailure(_recorder.Failure);
				_recorder = null;
			}
			if (advance.Dropped)
			{
				ReportDrop(advance);
			}

			if (_driver.Faulted)
			{
				Debug.LogError($"[FerrySim] The model produced a non-finite state at t={_driver.SimulationTime:F2} s; the run is stopped.", this);
				Pause();
				EndRun();
			}
			else if (_replay != null && _driver.StepCount >= _replay.StepCount)
			{
				FinishReplay();
			}

			return advance;
		}

		/// <summary>Ends the current run: closes its recording. The vessel stays where it is until the next run starts.</summary>
		public void EndRun()
		{
			if (_driver == null)
			{
				return;
			}

			var wasActive = _runActive;
			_runActive = false;
			if (_replayVerifier != null)
			{
				_driver.RemoveObserver(_replayVerifier);
				_lastReplayResult = _replayVerifier.Result;
				_replayVerifier = null;
			}

			if (_recorder != null)
			{
				_driver.RemoveObserver(_recorder);
				try
				{
					_recorder.Finish(_driver.StepCount, _driver.CurrentState, _driver.Simulation.Telemetry);
					if (_recorder.Failure == null) LastRecordingPath = _recorder.TelemetryPath;
					else ReportRecordingFailure(_recorder.Failure);
				}
				catch (Exception exception)
				{
					ReportRecordingFailure(exception);
				}
				finally { _recorder.Dispose(); }

				_recorder = null;
			}

			if (wasActive)
			{
				RunEnded?.Invoke();
			}
		}

		private void Start()
		{
			if (IsInitialized)
			{
				return;
			}

			try
			{
				if (!string.IsNullOrEmpty(replayRecording))
				{
					InitializeReplay(RecordedRun.Load(SimPaths.Resolve(replayRecording)));
				}
				else if (!string.IsNullOrEmpty(scenarioJsonFile))
				{
					Initialize(ScenarioAsset.LoadJson(scenarioJsonFile));
				}
				else if (scenario != null)
				{
					Initialize(scenario.Settings);
				}
				else
				{
					Debug.LogWarning("[FerrySim] VesselSimulationRunner has no scenario; call Initialize or assign one.", this);
				}
			}
			catch (Exception exception)
			{
				Debug.LogException(exception, this);
				enabled = false;
			}
		}

		private void Update()
		{
			if (!IsInitialized)
			{
				return;
			}

			AdvanceFrame(FrameTime());
			ApplyRenderPose();
		}

		/// <summary>
		/// Simulated time owed for this frame. Unity clamps <c>Time.deltaTime</c> to <c>Time.maximumDeltaTime</c>
		/// (a third of a second), which would hide most of a long hitch; the unscaled frame time keeps the
		/// accounting honest and the step guard still caps the work. A capture frame rate (tests, recording)
		/// fixes <c>Time.deltaTime</c> instead, so it is used as is.
		/// </summary>
		private static double FrameTime() =>
			Time.captureDeltaTime > 0f ? Time.deltaTime : (double)Time.unscaledDeltaTime * Time.timeScale;

		private void OnDisable() => EndRun();

		private void OnApplicationQuit() => EndRun();

		private void OnValidate()
		{
			maxStepsPerFrame = Math.Max(1, maxStepsPerFrame);
			if (telemetrySampleHz < 1 || telemetrySampleHz > SimulationClock.StepsPerSecond || SimulationClock.StepsPerSecond % telemetrySampleHz != 0)
			{
				telemetrySampleHz = 10;
			}
		}

		/// <summary>Moves the render target to the interpolated pose. Runs every frame after stepping, also while paused.</summary>
		public void ApplyRenderPose()
		{
			if (!IsInitialized)
			{
				return;
			}

			RenderPose = VesselPose.Interpolate(_driver.PreviousState, _driver.CurrentState, _driver.InterpolationFactor);
			_placement.Place(renderTarget != null ? renderTarget : transform, RenderPose, _driver.SimulationTime);
		}

		private void StartRun(IControlInputSource controls)
		{
			_lastReplayResult = null;
			ReplayCompleted = false;
			RecordingError = null;
			var simulation = Factory.Create(Parameters, _environment, _worldGeometry, _initialState);
			RunId = System.Threading.Interlocked.Increment(ref _nextRunId);
			_contactObserver.Reset();
			if (_driver == null)
			{
				_driver = new FixedStepDriver(simulation, controls, maxStepsPerFrame);
				_driver.AddObserver(_contactObserver);
			}
			else
			{
				_driver.Controls = controls;
				_driver.Restart(simulation);
			}

			_driver.StepLimit = _replay != null ? _replay.StepCount : (long?)null;
			_placement = FindPlacement();
			IsPaused = false;
			_runNumber++;
			_unreportedDrop = 0.0;
			_unreportedDropFrames = 0;
			if (_replay != null)
			{
				_replayVerifier = new ReplayVerifier(_replay);
				_driver.AddObserver(_replayVerifier);
			}
			else if (recordRuns)
			{
				StartRecording(simulation);
			}

			_runActive = true;
			ApplyRenderPose();
			RunStarted?.Invoke();
		}

		private void StartRecording(ISimulation simulation)
		{
			try
			{
				var header = RunRecorder.BuildHeader(Scenario, Parameters, simulation, Factory.GetType().FullName, _initialState, _environment, telemetrySampleHz);
				header.Add(new System.Collections.Generic.KeyValuePair<string, string>(
					"world_geometry", _worldGeometry is EmptyWorldGeometry ? "none" : _worldGeometry.GetType().FullName));
				var geometryDigest = WorldGeometryDigest.Of(_worldGeometry);
				if (geometryDigest != null)
				{
					header.Add(new System.Collections.Generic.KeyValuePair<string, string>(WorldGeometryDigest.HeaderKey, geometryDigest));
				}
				_recorder = RunRecorder.Create(RecordingDirectory, UniqueRunName(), telemetrySampleHz, header);
				_driver.AddObserver(_recorder);
			}
			catch (Exception exception)
			{
				_recorder = null;
				ReportRecordingFailure(exception);
			}
		}

		private void ReportRecordingFailure(Exception exception)
		{
			RecordingError = exception.Message;
			Debug.LogWarning($"[FerrySim] Recording incomplete: {RecordingError}. Physics continues; this recording cannot be used as a complete replay.", this);
		}

		private string UniqueRunName()
		{
			var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			var baseName = $"{stamp}-{Parameters.Id}-run{_runNumber:D2}";
			var name = baseName;
			for (var suffix = 2; File.Exists(Path.Combine(RecordingDirectory, name + ".csv")); suffix++)
			{
				name = $"{baseName}-{suffix}";
			}

			return name;
		}

		private void FinishReplay()
		{
			var result = _replayVerifier.Finish(_driver.StepCount, _driver.CurrentState, _driver.Simulation.Telemetry);
			ReplayCompleted = true;
			if (result.Matches)
			{
				Debug.Log($"[FerrySim] Replay of {Path.GetFileName(_replay.TelemetryPath)}: {result}", this);
			}
			else
			{
				Debug.LogError($"[FerrySim] Replay of {Path.GetFileName(_replay.TelemetryPath)}: {result}", this);
			}

			Pause();
			EndRun();
		}

		private void ReportDrop(FrameAdvance advance)
		{
			FrameDropped?.Invoke(advance);
			_unreportedDrop += advance.DroppedTime;
			_unreportedDropFrames++;
			var now = Time.realtimeSinceStartupAsDouble;
			if (now - _lastDropReportTime < DropReportIntervalSeconds)
			{
				return;
			}

			Debug.LogWarning(
				$"[FerrySim] Frame fell behind: dropped {_unreportedDrop:F2} s of simulated time over {_unreportedDropFrames} frame(s) " +
				$"(max {_driver.MaxStepsPerFrame} steps per frame; last frame {Time.unscaledDeltaTime * 1000f:F0} ms). The simulation runs slower than real time while this lasts.",
				this);
			_lastDropReportTime = now;
			_unreportedDrop = 0.0;
			_unreportedDropFrames = 0;
		}

		private IControlInputSource FindControlSource()
		{
			if (controlSource is IControlInputSource assigned)
			{
				return assigned;
			}

			if (TryGetComponent<IControlInputSource>(out var local))
			{
				return local;
			}

			Debug.LogWarning("[FerrySim] No control source found; the vessel runs with neutral controls.", this);
			return new ConstantControls(ControlInputs.Neutral);
		}

		private IVesselPlacement FindPlacement()
		{
			if (TryGetComponent<IVesselPlacement>(out var custom))
			{
				_flatPlacement = null;
				return custom;
			}

			_flatPlacement ??= new TangentPlanePlacement();
			_flatPlacement.WaterSurfaceHeight = waterSurfaceHeight;
			if (recenterRenderOriginOnStart)
			{
				_flatPlacement.RenderOriginNorth = _initialState.North;
				_flatPlacement.RenderOriginEast = _initialState.East;
			}

			return _flatPlacement;
		}

		/// <summary>One observer for the driver's lifetime, updated after every completed finite physics step.</summary>
		private sealed class ContactObserver : IStepObserver
		{
			public ContactLatch Contact { get; private set; } = ContactLatch.None;

			public void Reset() => Contact = ContactLatch.None;

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
			{
				var force = telemetry.ContactForce;
				if (!(force > 0.0)) return;
				var step = stepCount - 1;
				var peak = force > Contact.PeakContactForce;
				Contact = new ContactLatch(Contact.StepsWithContact + 1, step, force,
					peak ? force : Contact.PeakContactForce, peak ? step : Contact.PeakContactStep);
			}
		}

		private static VesselParameters LoadVessel(string vesselFile)
		{
			var path = SimPaths.Resolve(vesselFile);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException($"Vessel file not found: {vesselFile} (looked at {path})", path);
			}

			return VesselParametersJson.Load(path);
		}
	}
}
