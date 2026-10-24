using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using UnityEngine;

namespace FerrySim.Sim.Tests
{
	internal static class TestSupport
	{
		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		/// <summary>Where these tests leave verification artifacts: artifacts/unity/bridge.</summary>
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

		/// <summary>A fresh, empty folder under artifacts/unity/bridge, kept after the run as evidence.</summary>
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
	}

	/// <summary>Collects the state after every step and the controls applied in it.</summary>
	internal sealed class StepLog : IStepObserver
	{
		public readonly List<VesselState> States = new List<VesselState>();
		public readonly List<ControlInputs> Controls = new List<ControlInputs>();
		public readonly List<long> StepsSeen = new List<long>();

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			StepsSeen.Add(step);
			Controls.Add(controls);
		}

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry) => States.Add(state);
	}

	/// <summary>A control source that records which steps it was asked for.</summary>
	internal sealed class ScriptedControls : IControlInputSource
	{
		private readonly System.Func<long, ControlInputs> _script;

		public ScriptedControls(System.Func<long, ControlInputs> script) => _script = script;

		public List<long> Requested { get; } = new List<long>();

		public ControlInputs ControlsForStep(long step)
		{
			Requested.Add(step);
			return _script(step);
		}
	}
}
