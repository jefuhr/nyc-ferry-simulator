using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CesiumForUnity;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using FerrySim.UI;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>Loads the integrated slice scene in play mode and finds its parts. Nothing here is a fake.</summary>
	internal sealed class SliceScene
	{
		public const string ScenePath = "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity";

		/// <summary>The landing the contact tests use: South Williamsburg (stop 8), an ENC outline with a pipeline-verified water side.</summary>
		public const string ContactTestStopId = "8";

		public const string BuildCommand = "-executeMethod FerrySim.Slice.Authoring.SliceSceneBuilder.BuildFromCommandLine";

		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string ArtifactDirectory
		{
			get
			{
				var path = Path.Combine(RepoRoot, "artifacts", "unity");
				Directory.CreateDirectory(path);
				return path;
			}
		}

		public SliceBootstrap Bootstrap { get; private set; }

		public VesselSimulationRunner Runner { get; private set; }

		public WheelhouseUI UI { get; private set; }

		public WheelhouseView View => UI.View;

		public WheelhousePanel Panel => UI.View.Panel;

		public HudOverlay Hud => UI.View.Hud;

		public CameraRig Rig { get; private set; }

		public Camera Camera { get; private set; }

		public WaterSurface Water { get; private set; }

		public CesiumGeoreference Georeference { get; private set; }

		public CesiumVesselPlacement Placement { get; private set; }

		public HelmInputController Helm { get; private set; }

		public LandingGeometry Landings => Bootstrap.Landings;

		public VesselParameters Vessel => Runner.Parameters;

		public static string FreshFolder(string name)
		{
			var path = Path.Combine(ArtifactDirectory, "slice", name);
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}

			Directory.CreateDirectory(path);
			return path;
		}

		public IEnumerator Load()
		{
#if UNITY_EDITOR
			Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", ScenePath)), Is.True,
				$"The integrated scene {ScenePath} is missing; build it with {BuildCommand}.");
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
			Assert.Fail("The slice PlayMode tests load the scene through the editor.");
#endif
			yield return null;
			yield return null;
			Bootstrap = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(Bootstrap, Is.Not.Null, "the scene has its SliceBootstrap");
			Runner = Bootstrap.Runner;
			UI = Bootstrap.WheelhouseUI;
			Rig = Bootstrap.CameraRig;
			Camera = Rig.GetComponent<Camera>();
			Water = Object.FindFirstObjectByType<WaterSurface>();
			Georeference = Object.FindFirstObjectByType<CesiumGeoreference>();
			Placement = Runner.GetComponent<CesiumVesselPlacement>();
			Helm = Runner.GetComponent<HelmInputController>();
			Assert.That(Bootstrap.LoadError, Is.Null, "the landing faces loaded");
			Assert.That(Runner.IsInitialized, Is.True, "the scene's runner started its scenario");
			Assert.That(UI.View, Is.Not.Null);
		}

		/// <summary>Starts a new run on the scene's runner (same vessel) with the given scenario and controls.</summary>
		public void Restart(ScenarioSettings scenario, IControlInputSource controls, string recordingFolder = null)
		{
			Runner.RecordRuns = recordingFolder != null;
			if (recordingFolder != null)
			{
				Runner.RecordingDirectory = recordingFolder;
			}

			Runner.Initialize(scenario, controls, Runner.Parameters);
		}

		/// <summary>Fixes every frame at <paramref name="steps"/> physics steps (Time.captureDeltaTime) and lets the runner take them.</summary>
		public void StepsPerFrame(int steps)
		{
			Runner.MaxStepsPerFrame = Math.Max(Runner.MaxStepsPerFrame, steps);
			Time.captureDeltaTime = (float)(steps * SimulationClock.FixedTimeStep);
		}

		public static void ResetTime() => Time.captureDeltaTime = 0f;

		/// <summary>
		/// Unloads the slice scene after a test, so later fixtures (the UI's checks that nothing is left running among
		/// them) start without its runner and UI. An empty scene takes its place, since the last scene cannot unload.
		/// </summary>
		public static IEnumerator Unload()
		{
			ResetTime();
			var slice = SceneManager.GetSceneByPath(ScenePath);
			if (!slice.IsValid() || !slice.isLoaded)
			{
				yield break;
			}

			SceneManager.SetActiveScene(SceneManager.CreateScene("slice test teardown " + Time.frameCount));
			yield return SceneManager.UnloadSceneAsync(slice);
		}

		/// <summary>A bow-on approach to the contact test landing's first face, at rest, calm water.</summary>
		public ScenarioSettings Approach(double bowClearance, string name = "slice approach") =>
			LandingApproach.BowOn(ContactTestFace(), Vessel, bowClearance, name, Runner.Scenario?.vesselFile ?? "vessels/t-class.json", "2026-06-15T16:00:00Z");

		public LandingSegment ContactTestFace()
		{
			var faces = Landings.ForStop(ContactTestStopId);
			Assert.That(faces, Is.Not.Empty, $"stop {ContactTestStopId} has no usable face:\n{Landings.Describe()}");
			return faces[0];
		}

		public IEnumerator FramesUntil(Func<bool> done, int maxFrames, string what)
		{
			for (var frame = 0; frame < maxFrames && !done(); frame++)
			{
				yield return null;
			}

			Assert.That(done(), Is.True, $"timed out after {maxFrames} frames waiting for {what}");
		}

		public static IEnumerator Frames(int count)
		{
			for (var i = 0; i < count; i++)
			{
				yield return null;
			}
		}
	}

	/// <summary>
	/// Scripted helm for a landing: ahead idle on both engines until the speed over ground reaches the target, neutral
	/// above the band so the boat coasts, and once the hull touches the face ahead idle again to hold the bow on it,
	/// as crews land on a barge. Reads the runner's state before each step, so the trace it produces is deterministic.
	/// </summary>
	internal sealed class LandingApproachControls : IControlInputSource
	{
		/// <summary>Inside the ahead idle detent: t-class.json clutchInLever 0.12 to throttleStartLever 0.2.</summary>
		public const double IdleAhead = 0.15;

		private readonly VesselSimulationRunner _runner;
		private readonly double _targetMetresPerSecond;
		private bool _coasting;

		public LandingApproachControls(VesselSimulationRunner runner, double targetKnots)
		{
			_runner = runner;
			_targetMetresPerSecond = Units.KnotsToMetresPerSecond(targetKnots);
		}

		/// <summary>The first step that applied the hold, or -1.</summary>
		public long HoldFromStep { get; private set; } = -1;

		/// <summary>Speed over ground at the start of that step, m/s.</summary>
		public double SpeedAtContact { get; private set; } = double.NaN;

		public ControlInputs ControlsForStep(long step)
		{
			if (step == 0)
			{
				_coasting = false;
				HoldFromStep = -1;
				SpeedAtContact = double.NaN;
			}

			if (HoldFromStep < 0 && _runner.Contact.StepsWithContact > 0)
			{
				HoldFromStep = step;
				SpeedAtContact = _runner.Telemetry.SpeedOverGround;
			}

			if (HoldFromStep >= 0)
			{
				return new ControlInputs(IdleAhead, IdleAhead, 0.0);
			}

			var speed = _runner.Telemetry.SpeedOverGround;
			if (speed >= _targetMetresPerSecond + Units.KnotsToMetresPerSecond(0.1))
			{
				_coasting = true;
			}
			else if (speed < _targetMetresPerSecond - Units.KnotsToMetresPerSecond(0.05))
			{
				_coasting = false;
			}

			return _coasting ? ControlInputs.Neutral : new ControlInputs(IdleAhead, IdleAhead, 0.0);
		}
	}

	/// <summary>Everything each physics step touched: per-segment contact, read from the core after the step.</summary>
	internal sealed class ContactLog : IStepObserver
	{
		private const ulong OffsetBasis = 14695981039346656037UL;
		private const ulong Prime = 1099511628211UL;

		private readonly Func<VesselDynamicsSimulation> _simulation;
		private ulong _hash = OffsetBasis;

		public ContactLog(Func<VesselDynamicsSimulation> simulation)
		{
			_simulation = simulation;
		}

		public HashSet<int> SegmentsTouched { get; } = new HashSet<int>();

		public List<long> ContactSteps { get; } = new List<long>();

		public double PeakForce { get; private set; }

		public double DeepestCompression { get; private set; }

		public bool BottomedOut { get; private set; }

		/// <summary>FNV-1a over every bit of every step's contact force and per-segment contact details.</summary>
		public string Digest => "fnv1a64:" + _hash.ToString("x16");

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
		}

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
		{
			var simulation = _simulation();
			Mix(telemetry.ContactForce);
			Mix(simulation.ContactCount);
			if (telemetry.ContactForce > 0.0)
			{
				ContactSteps.Add(stepCount - 1);
				PeakForce = Math.Max(PeakForce, telemetry.ContactForce);
			}

			for (var i = 0; i < simulation.ContactCount; i++)
			{
				var contact = simulation.GetContact(i);
				SegmentsTouched.Add(contact.SegmentId);
				DeepestCompression = Math.Max(DeepestCompression, contact.Compression);
				BottomedOut |= contact.BottomedOut;
				Mix(contact.SegmentId);
				Mix(contact.NormalForce);
				Mix(contact.FrictionForce);
				Mix(contact.Compression);
				Mix(contact.ContactLength);
				Mix(contact.BottomedOut ? 1 : 0);
			}
		}

		private void Mix(double value) => Mix(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

		private void Mix(int value) => Mix(unchecked((ulong)(uint)value));

		private void Mix(ulong bits)
		{
			for (var shift = 0; shift < 64; shift += 8)
			{
				_hash ^= (bits >> shift) & 0xFFUL;
				_hash = unchecked(_hash * Prime);
			}
		}
	}

	/// <summary>The real factory, remembering the simulation it made so an observer can read its contact details.</summary>
	internal sealed class RememberingFactory : ISimulationFactory
	{
		private readonly VesselDynamicsFactory _factory = new VesselDynamicsFactory();

		public VesselDynamicsSimulation Last { get; private set; }

		public ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState) =>
			Last = (VesselDynamicsSimulation)_factory.Create(parameters, environment, initialState);

		public ISimulation Create(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState) =>
			Last = (VesselDynamicsSimulation)_factory.Create(parameters, environment, geometry, initialState);
	}
}
