using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using UnityEngine;

namespace FerrySim.Sim.PlayModeTests
{
	internal static class PlayModeSupport
	{
		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string ArtifactsDir
		{
			get
			{
				var path = Path.Combine(RepoRoot, "artifacts", "unity", "bridge");
				Directory.CreateDirectory(path);
				return path;
			}
		}

		public static string FixturePath(string name) => Path.Combine(Application.dataPath, "FerrySim", "Sim", "Tests", "Fixtures", name);

		public static VesselParameters LoadTClass() => VesselParametersJson.Load(Path.Combine(RepoRoot, "vessels", "t-class.json"));

		public static string FreshArtifactFolder(string name)
		{
			var path = Path.Combine(ArtifactsDir, name);
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}

			Directory.CreateDirectory(path);
			return path;
		}

		/// <summary>Yields frames until <paramref name="done"/> or <paramref name="maxFrames"/>; frame time comes from <c>Time.captureDeltaTime</c>.</summary>
		public static IEnumerator Frames(Func<bool> done, int maxFrames)
		{
			for (var frame = 0; frame < maxFrames && !done(); frame++)
			{
				yield return null;
			}
		}

		/// <summary>A runner on a fresh GameObject, initialized before its Start so the inspector path is skipped.</summary>
		public static VesselSimulationRunner NewRunner(string name, ScenarioSettings scenario, IControlInputSource controls, VesselParameters vessel, string recordingFolder, int sampleHz = 10)
		{
			var runner = new GameObject(name).AddComponent<VesselSimulationRunner>();
			runner.TelemetrySampleHz = sampleHz;
			runner.RecordRuns = recordingFolder != null;
			if (recordingFolder != null)
			{
				runner.RecordingDirectory = recordingFolder;
			}

			runner.Initialize(scenario, controls, vessel);
			return runner;
		}
	}

	/// <summary>Collects the state after every step and the controls applied in it.</summary>
	internal sealed class StepLog : IStepObserver
	{
		public readonly List<VesselState> States = new List<VesselState>();
		public readonly List<ControlInputs> Controls = new List<ControlInputs>();

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) => Controls.Add(controls);

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry) => States.Add(state);
	}
}
