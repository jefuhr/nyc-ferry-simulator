using System;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// A whistle tone made in code, no audio asset: the fundamental <see cref="FogSignalSchedule.WhistleHertz"/> with its
	/// second to fourth harmonics at 1/2, 1/3 and 1/6 of its amplitude (a reed horn's bright tone), one
	/// <see cref="FogSignalSchedule.ProlongedBlastSeconds"/> blast with 80 ms of attack and 150 ms of release.
	/// </summary>
	public static class WhistleTone
	{
		public const int SampleRate = 44100;

		public const double AttackSeconds = 0.08;

		public const double ReleaseSeconds = 0.15;

		public const float Amplitude = 0.5f;

		/// <summary>One blast's samples, mono, in [-1, 1].</summary>
		public static float[] Samples(double hertz, double seconds)
		{
			var count = (int)Math.Round(seconds * SampleRate);
			var samples = new float[count];
			var harmonics = new[] { 1.0, 0.5, 1.0 / 3.0, 1.0 / 6.0 };
			var norm = 0.0;
			foreach (var h in harmonics)
			{
				norm += h;
			}

			for (var i = 0; i < count; i++)
			{
				var t = i / (double)SampleRate;
				var value = 0.0;
				for (var k = 0; k < harmonics.Length; k++)
				{
					value += harmonics[k] * Math.Sin(2.0 * Math.PI * hertz * (k + 1) * t);
				}

				var envelope = Math.Min(1.0, Math.Min(t / AttackSeconds, (seconds - t) / ReleaseSeconds));
				samples[i] = (float)(Amplitude * Math.Max(0.0, envelope) * value / norm);
			}

			return samples;
		}

		/// <summary>The blast as an AudioClip.</summary>
		public static AudioClip Clip()
		{
			var samples = Samples(FogSignalSchedule.WhistleHertz, FogSignalSchedule.ProlongedBlastSeconds);
			var clip = AudioClip.Create($"Fog signal whistle {FogSignalSchedule.WhistleHertz:0} Hz", samples.Length, 1, SampleRate, false);
			clip.SetData(samples, 0);
			return clip;
		}
	}

	/// <summary>
	/// The learner's fog signal (Rule 35, <see cref="FogSignalSchedule"/>): records each signal on the run's physics steps
	/// (<see cref="Recorder"/>, so a replay sounds the same signals at the same times) and sounds it on a 3D
	/// <see cref="AudioSource"/> here, the whistle's place on the vessel. The sound follows the run's clock: it pauses with
	/// the run and stops when the run ends. <see cref="Follow"/> attaches it to a runner.
	/// </summary>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(100)]
	public sealed class FogSignal : MonoBehaviour
	{
		private readonly FogSignalRecorder _recorder = new FogSignalRecorder(MeteorologicalVisibility.Unlimited);
		private VesselSimulationRunner _runner;
		private FixedStepDriver _driver;
		private AudioSource _source;
		private AudioClip _clip;
		private bool _sourcePaused;

		/// <summary>The current run's signals so far.</summary>
		public FogSignalRecorder Recorder => _recorder;

		public AudioSource Source => _source;

		/// <summary>True while a blast sounds at the run's current time.</summary>
		public bool IsSounding { get; private set; }

		/// <summary>Seconds into the current blast, while <see cref="IsSounding"/>.</summary>
		public double BlastSeconds { get; private set; }

		/// <summary>Follows <paramref name="runner"/>'s runs; null stops following. Call before or after a run starts.</summary>
		public void Follow(VesselSimulationRunner runner)
		{
			if (ReferenceEquals(runner, _runner))
			{
				return;
			}

			if (_runner != null)
			{
				_runner.RunStarted -= OnRunStarted;
				_runner.RunEnded -= OnRunEnded;
			}

			Detach();
			_runner = runner;
			if (_runner != null)
			{
				_runner.RunStarted += OnRunStarted;
				_runner.RunEnded += OnRunEnded;
				if (_runner.IsInitialized)
				{
					OnRunStarted();
				}
			}
		}

		private void Awake()
		{
			_source = gameObject.AddComponent<AudioSource>();
			_source.playOnAwake = false;
			_source.loop = false;
			_source.spatialBlend = 1f;
			_source.rolloffMode = AudioRolloffMode.Logarithmic;
			_source.minDistance = 15f;
			_source.maxDistance = 3000f;
			_source.dopplerLevel = 0f;
			_clip = WhistleTone.Clip();
			if (_clip == null)
			{
				Debug.LogWarning("[FerrySim] The fog signal's whistle tone could not be made (no audio?); the signals are still recorded.", this);
			}

			_source.clip = _clip;
		}

		private void OnDestroy()
		{
			Follow(null);
			if (_clip != null)
			{
				Destroy(_clip);
			}
		}

		private void OnRunStarted()
		{
			Detach();
			_recorder.Clear();
			_recorder.VisibilityMetres = RunVisibility.Of(_runner);
			_driver = _runner.Driver;
			_driver?.AddObserver(_recorder);
		}

		private void OnRunEnded() => Silence();

		private void Detach()
		{
			_driver?.RemoveObserver(_recorder);
			_driver = null;
			Silence();
		}

		// After the runner's step (execution order -100), like the atmosphere.
		private void Update() => Refresh();

		/// <summary>Sounds, pauses or stops the whistle for the run's current time. Every frame; allocation-free.</summary>
		public void Refresh()
		{
			if (_runner == null || !_runner.IsRunActive || _driver == null || _recorder.Events.Count == 0)
			{
				Silence();
				return;
			}

			var last = _recorder.Events[_recorder.Events.Count - 1];
			var since = _driver.SimulationTime - last.Time;
			IsSounding = FogSignalSchedule.IsBlasting(last.Kind, since);
			if (!IsSounding)
			{
				BlastSeconds = 0.0;
				Stop();
				return;
			}

			var secondBlast = FogSignalSchedule.ProlongedBlastSeconds + FogSignalSchedule.TwoBlastGapSeconds;
			BlastSeconds = since >= secondBlast ? since - secondBlast : since;
			if (_runner.IsPaused)
			{
				if (_source.isPlaying)
				{
					_source.Pause();
					_sourcePaused = true;
				}

				return;
			}

			if (_sourcePaused)
			{
				_source.UnPause();
				_sourcePaused = false;
			}

			if (_clip != null && !_source.isPlaying && BlastSeconds < _clip.length - 0.05f)
			{
				_source.time = (float)BlastSeconds;
				_source.Play();
			}
		}

		private void Silence()
		{
			IsSounding = false;
			BlastSeconds = 0.0;
			Stop();
		}

		private void Stop()
		{
			_sourcePaused = false;
			if (_source != null && _source.isPlaying)
			{
				_source.Stop();
			}
		}
	}
}
