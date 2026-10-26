using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// Runs without traffic must write exactly the recordings they wrote before traffic existed. This test records three fixed
	/// runs through the runner (calm, uniform and a real pack day) with scripted controls, renames each pair to a fixed name
	/// (the wall-clock stamp in the run's name is the only thing that varies) and keeps them under
	/// <c>artifacts/traffic-unity/recordings/&lt;label&gt;/</c>, the label from <c>FERRYSIM_RECORDING_LABEL</c> (default
	/// <c>current</c>). Run once with the label <c>base</c> on the code before traffic, then every later run compares its files
	/// with those byte for byte. Without a <c>base</c> folder it only records. Failure mode guarded: a non-traffic recording
	/// that changes by a byte.
	/// </summary>
	public class RecordingBaselineTests
	{
		private const string PackName = "lower-east-river/2025-07-14";

		private GameObject _host;

		private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		private static string Folder(string label) => Path.Combine(RepoRoot, "artifacts", "traffic-unity", "recordings", label);

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		private sealed class Script : IControlInputSource
		{
			public ControlInputs ControlsForStep(long step) => new ControlInputs(0.3 + 0.2 * Math.Sin(step * 0.003), 0.35 - 0.1 * Math.Cos(step * 0.002), step > 600 ? 0.4 : -0.1);
		}

		private static ScenarioSettings Scenario(string name, ScenarioEnvironment environment, string start)
		{
			var position = LocalTangentPlane.World.ToGeodetic(-200.0, 300.0);
			return new ScenarioSettings
			{
				name = name,
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = 35.0,
				startSpeedKnots = 3.0,
				scenarioStartUtc = start,
				environment = environment,
			};
		}

		private void Record(ScenarioSettings scenario, string folder, string fixedName)
		{
			_host = new GameObject("baseline recording runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			var scratch = Path.Combine(folder, "scratch");
			runner.RecordingDirectory = scratch;
			runner.TelemetrySampleHz = 10;
			runner.Initialize(scenario, new Script(), VesselParametersJson.Load(Path.Combine(RepoRoot, "vessels", "t-class.json")));
			for (var frame = 0; frame < 150; frame++)
			{
				runner.AdvanceFrame(0.0217);
			}

			runner.EndRun();
			Assert.That(runner.RecordingError, Is.Null);
			var telemetry = runner.LastRecordingPath;
			var stamped = Path.GetFileNameWithoutExtension(telemetry);
			var inputs = Path.Combine(scratch, stamped + RunRecorder.InputsSuffix);
			Assert.That(Directory.GetFiles(scratch), Is.EquivalentTo(new[] { telemetry, inputs }), "a run without traffic writes exactly its telemetry and its control trace");
			File.WriteAllText(Path.Combine(folder, fixedName + ".csv"), File.ReadAllText(telemetry).Replace(stamped, fixedName), new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(folder, fixedName + RunRecorder.InputsSuffix), File.ReadAllText(inputs).Replace(stamped, fixedName), new UTF8Encoding(false));
			Directory.Delete(scratch, true);
			Object.DestroyImmediate(_host);
			_host = null;
		}

		[Test]
		public void RunsWithoutTraffic_WriteTheSameBytesAsBeforeTraffic()
		{
			var label = System.Environment.GetEnvironmentVariable("FERRYSIM_RECORDING_LABEL");
			label = string.IsNullOrEmpty(label) ? "current" : label;
			var folder = Folder(label);
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, true);
			}

			Directory.CreateDirectory(folder);
			Record(Scenario("baseline calm", new ScenarioEnvironment(), "2025-07-14T16:00:00Z"), folder, "calm");
			Record(Scenario("baseline uniform", new ScenarioEnvironment { calm = false, currentSpeedKnots = 1.5, currentTowardDegrees = 40, windSpeedKnots = 12, windFromDegrees = 250, depthMetres = 9 },
				"2025-07-14T16:00:00Z"), folder, "uniform");
			var hasPack = EnvironmentPacks.Default.DayFolder(PackName) != null;
			if (hasPack)
			{
				Record(Scenario("baseline pack", new ScenarioEnvironment { pack = PackName }, "2025-07-14T16:00:00.25Z"), folder, "pack");
			}

			var listing = new StringBuilder();
			var names = new List<string>(Directory.GetFiles(folder));
			names.Sort(StringComparer.Ordinal);
			foreach (var path in names)
			{
				using (var sha = SHA256.Create())
				{
					listing.Append(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant()).Append("  ").Append(Path.GetFileName(path)).Append('\n');
				}
			}

			File.WriteAllText(Path.Combine(folder, "SHA256SUMS"), listing.ToString());
			TestContext.WriteLine($"{label}: {(hasPack ? "calm, uniform and pack" : "calm and uniform (no pack built)")} recordings in {folder}\n{listing}");
			var baseline = Folder("base");
			if (label == "base" || !Directory.Exists(baseline))
			{
				return;
			}

			foreach (var name in new[] { "calm.csv", "calm.inputs.csv", "uniform.csv", "uniform.inputs.csv", "pack.csv", "pack.inputs.csv" })
			{
				var before = Path.Combine(baseline, name);
				var after = Path.Combine(folder, name);
				if (!File.Exists(before) && !File.Exists(after))
				{
					continue;
				}

				Assert.That(File.Exists(after), Is.True, $"{name}: recorded before traffic, not now");
				Assert.That(File.ReadAllBytes(after), Is.EqualTo(File.ReadAllBytes(before)), $"{name}: the same bytes as before traffic");
			}
		}
	}
}
