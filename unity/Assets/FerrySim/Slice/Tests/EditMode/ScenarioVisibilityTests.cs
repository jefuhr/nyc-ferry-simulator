using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// A scenario's visibility (<see cref="ScenarioSettings.visibilityMetres"/>), its recording key and its replay.
	/// Failure modes guarded: a default that is not unlimited; a recording of a run that did not ask for fog that is no
	/// longer byte for byte what it was (an extra header line, or other telemetry); a nonsense visibility (negative, not a
	/// number, infinite or a few metres) accepted; a recorded visibility that a replay loses; and fog that changes the
	/// physics.
	/// </summary>
	public class ScenarioVisibilityTests
	{
		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		private static ScenarioSettings OffPier11(double visibility) => new ScenarioSettings
		{
			name = "visibility",
			vesselFile = "vessels/t-class.json",
			startLatitude = 40.7032,
			startLongitude = -74.0060,
			startHeadingDegrees = 60.0,
			startSpeedKnots = 4.0,
			scenarioStartUtc = "2025-07-21T02:00:00Z",
			environment = new ScenarioEnvironment { calm = true },
			visibilityMetres = visibility,
		};

		[Test]
		public void TheDefault_IsUnlimited()
		{
			var settings = new ScenarioSettings();

			Assert.That(settings.visibilityMetres, Is.EqualTo(MeteorologicalVisibility.Unlimited));
			Assert.That(MeteorologicalVisibility.IsLimited(settings.visibilityMetres), Is.False);
			Assert.DoesNotThrow(() => settings.Validate());
			Assert.That(ScenarioAsset.FromJson("{\"vesselFile\": \"vessels/t-class.json\"}").visibilityMetres, Is.EqualTo(0.0), "a JSON scenario without the field");
			Assert.That(ScenarioAsset.FromJson("{\"visibilityMetres\": 500}").visibilityMetres, Is.EqualTo(500.0));
		}

		[TestCase(-1.0)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		[TestCase(9.9)]
		public void ANonsenseVisibility_IsRefused_NamingTheField(double visibility)
		{
			var settings = OffPier11(visibility);

			var error = Assert.Throws<ArgumentException>(() => settings.Validate());
			Assert.That(error.Message, Does.Contain("visibilityMetres"));
		}

		[TestCase(0.0)]
		[TestCase(10.0)]
		[TestCase(200.0)]
		[TestCase(50000.0)]
		public void ZeroOrTenMetresAndUp_IsAccepted(double visibility)
		{
			Assert.DoesNotThrow(() => OffPier11(visibility).Validate());
		}

		[Test]
		public void ARunWithoutFog_RecordsNoVisibility_AndOneWithFogAddsOneLineAndNothingElse()
		{
			var vessel = SliceTestData.LoadTClass();
			var clear = Record(OffPier11(MeteorologicalVisibility.Unlimited), vessel, SliceTestData.ArtifactFolder("visibility-clear"));
			var fog = Record(OffPier11(500.0), vessel, SliceTestData.ArtifactFolder("visibility-fog"));

			// The file names (a wall-clock stamp) are the only other thing two recordings may not share.
			var clearLines = WithoutFileNames(File.ReadAllLines(clear));
			var fogLines = WithoutFileNames(File.ReadAllLines(fog));
			Assert.That(clearLines.Any(line => line.StartsWith("# " + ScenarioSettings.VisibilityKey + ":", StringComparison.Ordinal)), Is.False, "no key for a run that did not ask for fog");
			var added = fogLines.Except(clearLines).ToArray();
			Assert.That(added, Is.EqualTo(new[] { "# " + ScenarioSettings.VisibilityKey + ": 500" }), "fog adds its key and changes no other line, telemetry included");
			Assert.That(fogLines.Where(line => !line.StartsWith("# " + ScenarioSettings.VisibilityKey, StringComparison.Ordinal)), Is.EqualTo(clearLines));
			Assert.That(WithoutFileNames(File.ReadAllLines(Inputs(clear))), Is.EqualTo(WithoutFileNames(File.ReadAllLines(Inputs(fog)))), "the same control trace and state digest");

			Assert.That(RecordedRun.Load(clear).VisibilityMetres, Is.EqualTo(MeteorologicalVisibility.Unlimited));
			Assert.That(RecordedRun.Load(fog).VisibilityMetres, Is.EqualTo(500.0));
		}

		[Test]
		public void AReplay_RestoresTheRecordedVisibility()
		{
			var vessel = SliceTestData.LoadTClass();
			var run = RecordedRun.Load(Record(OffPier11(200.0), vessel, SliceTestData.ArtifactFolder("visibility-replay")));
			var runner = _host.GetComponent<VesselSimulationRunner>();

			runner.InitializeReplay(run, vessel);

			Assert.That(runner.IsReplaying, Is.True);
			Assert.That(runner.Replay, Is.SameAs(run));
			Assert.That(RunVisibility.Of(runner), Is.EqualTo(200.0));
			runner.Initialize(OffPier11(MeteorologicalVisibility.Unlimited), new ConstantControls(ControlInputs.Neutral), vessel);
			Assert.That(runner.Replay, Is.Null);
			Assert.That(RunVisibility.Of(runner), Is.EqualTo(MeteorologicalVisibility.Unlimited));
		}

		[Test]
		public void AnOldRecording_WithoutTheKey_IsUnlimited()
		{
			var vessel = SliceTestData.LoadTClass();
			var path = Record(OffPier11(500.0), vessel, SliceTestData.ArtifactFolder("visibility-legacy"));
			File.WriteAllLines(path, File.ReadAllLines(path).Where(line => !line.StartsWith("# " + ScenarioSettings.VisibilityKey, StringComparison.Ordinal)));

			Assert.That(RecordedRun.Load(path).VisibilityMetres, Is.EqualTo(MeteorologicalVisibility.Unlimited));
		}

		[Test]
		public void ARecordedVisibilityThatIsNotADistance_IsRefused()
		{
			var vessel = SliceTestData.LoadTClass();
			var path = Record(OffPier11(500.0), vessel, SliceTestData.ArtifactFolder("visibility-bad"));
			File.WriteAllLines(path, File.ReadAllLines(path).Select(line => line.StartsWith("# " + ScenarioSettings.VisibilityKey, StringComparison.Ordinal) ? "# " + ScenarioSettings.VisibilityKey + ": thick" : line));

			var error = Assert.Throws<FormatException>(() => RecordedRun.Load(path));
			Assert.That(error.Message, Does.Contain(ScenarioSettings.VisibilityKey));
		}

		private static string Inputs(string telemetryPath) =>
			Path.Combine(Path.GetDirectoryName(telemetryPath) ?? ".", Path.GetFileNameWithoutExtension(telemetryPath) + RunRecorder.InputsSuffix);

		private static string[] WithoutFileNames(string[] lines) =>
			lines.Where(line => !line.StartsWith("# param.inputs:", StringComparison.Ordinal) && !line.StartsWith("# ferrysim control trace recorded by Unity", StringComparison.Ordinal)).ToArray();

		private string Record(ScenarioSettings scenario, VesselParameters vessel, string folder)
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_host = new GameObject("visibility runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordingDirectory = folder;
			runner.Initialize(scenario, new ConstantControls(new ControlInputs(0.4, 0.3, 0.1)), vessel);
			for (var frame = 0; frame < 100; frame++)
			{
				runner.AdvanceFrame(0.02);
			}

			runner.EndRun();
			Assert.That(runner.RecordingError, Is.Null);
			return runner.LastRecordingPath;
		}
	}
}
