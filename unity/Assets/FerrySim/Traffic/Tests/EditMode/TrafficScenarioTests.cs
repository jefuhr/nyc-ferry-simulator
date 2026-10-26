using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// The scenario's <c>traffic</c> field. Failure modes guarded: an unknown traffic day, one that is not the environment pack's
	/// day, or traffic on calm or uniform water accepted; a leave-out that does not parse, or an MMSI that is not in the pack,
	/// accepted; a refused scenario that ends the run already going; the field lost in JSON; a scenario without it given traffic.
	/// </summary>
	public class TrafficScenarioTests
	{
		private TrafficTestPacks _packs;
		private GameObject _host;

		[SetUp]
		public void SetUp()
		{
			_packs = new TrafficTestPacks();
			_packs.WriteTraffic(TrafficTestPacks.Date, TrafficTestPacks.Track.Straight(366000001, 43000.0, 500.0, 0.0, 3.0, Math.PI, 20));
		}

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_packs.Dispose();
		}

		private static ArgumentException Refused(ScenarioSettings scenario, TrafficTestPacks packs) =>
			Assert.Throws<ArgumentException>(() => scenario.Validate(packs.EnvironmentPacks, packs.TrafficPacks));

		[Test]
		public void AScenarioWithoutTraffic_HasNone()
		{
			var scenario = TrafficTestPacks.Scenario(trafficDay: string.Empty);
			Assert.That(scenario.traffic.IsOn, Is.False);
			Assert.That(scenario.CreateTraffic(_packs.TrafficPacks), Is.Null);
			Assert.That(new ScenarioSettings().traffic.IsOn, Is.False, "the default has no traffic");
			Assert.That(ScenarioAsset.FromJson("{\"name\": \"no traffic\"}").traffic.IsOn, Is.False, "JSON without the field");
			scenario.Validate(_packs.EnvironmentPacks, _packs.TrafficPacks);
		}

		[Test]
		public void TheField_RoundTripsThroughJson()
		{
			var scenario = TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.ByMmsi, mmsi: 366000001);
			var back = new ScenarioSettings();
			JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(scenario), back);
			Assert.That((back.traffic.day, back.traffic.leaveOut, back.traffic.leaveOutMmsi), Is.EqualTo((TrafficTestPacks.Date, "mmsi", 366000001)));
			Assert.That(back.traffic.LeaveOutMode, Is.EqualTo(TrafficLeaveOut.Mmsi));
		}

		[Test]
		public void TheTrafficDay_MustBeARealDayWithAPack_AndTheEnvironmentPacksDay()
		{
			var notADay = Refused(TrafficTestPacks.Scenario(trafficDay: "2025-7-14"), _packs);
			Assert.That(notADay.Message, Does.Contain("traffic day '2025-7-14' is not yyyy-MM-dd"));
			var otherDay = Refused(TrafficTestPacks.Scenario(trafficDay: TrafficTestPacks.OtherDate), _packs);
			Assert.That(otherDay.Message, Does.Contain("traffic day 2025-07-15 is not the environment pack's day (test-harbor/2025-07-14)"));
			var calm = TrafficTestPacks.Scenario();
			calm.environment = new ScenarioEnvironment();
			Assert.That(Refused(calm, _packs).Message, Does.Contain("needs the environment pack of the same day, but the scenario's water and air are calm"));
			var uniform = TrafficTestPacks.Scenario();
			uniform.environment = new ScenarioEnvironment { calm = false, depthMetres = 10.0 };
			Assert.That(Refused(uniform, _packs).Message, Does.Contain("are uniform"));
			// 2025-07-15 has an environment pack but no traffic pack: not found, naming the place looked.
			var onTheOtherDay = TrafficTestPacks.Scenario(trafficDay: TrafficTestPacks.OtherDate, environmentDate: TrafficTestPacks.OtherDate);
			onTheOtherDay.scenarioStartUtc = "2025-07-15T16:00:00Z";
			var unknown = Refused(onTheOtherDay, _packs);
			Assert.That(unknown.Message, Does.Contain("traffic pack '2025-07-15' was not found").And.Contain(Path.Combine(_packs.Root, "harbor", "traffic", "2025-07-15", "manifest.json")));
			TrafficTestPacks.Scenario().Validate(_packs.EnvironmentPacks, _packs.TrafficPacks);
		}

		[Test]
		public void TheLeaveOut_MustParse()
		{
			Assert.That(Refused(TrafficTestPacks.Scenario(leaveOut: "mine"), _packs).Message, Does.Contain("traffic leaveOut 'mine' must be off, mmsi or auto"));
			Assert.That(Refused(TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.ByMmsi, mmsi: 0), _packs).Message, Does.Contain("leaveOutMmsi 0 must be an MMSI"));
			var missing = TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.ByMmsi, mmsi: 123456789);
			missing.Validate(_packs.EnvironmentPacks, _packs.TrafficPacks);
			var error = Assert.Throws<ArgumentException>(() => missing.CreateTraffic(_packs.TrafficPacks));
			Assert.That(error.Message, Does.Contain("no vessel with MMSI 123456789"));
		}

		[Test]
		public void TheRunner_RefusesATrafficScenarioThatCannotStart_AndKeepsItsRun()
		{
			_host = new GameObject("traffic scenario runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = false;
			runner.EnvironmentPacks = _packs.EnvironmentPacks;
			runner.TrafficPacks = _packs.TrafficPacks;
			runner.Initialize(TrafficTestPacks.Scenario(), new ConstantControls(ControlInputs.Neutral));
			runner.AdvanceFrame(0.5);
			var run = runner.RunId;
			Assert.That(runner.Traffic, Is.Not.Null);
			var noPack = TrafficTestPacks.Scenario(trafficDay: TrafficTestPacks.OtherDate, environmentDate: TrafficTestPacks.OtherDate);
			noPack.scenarioStartUtc = "2025-07-15T16:00:00Z";
			Assert.That(Assert.Throws<ArgumentException>(() => runner.Initialize(noPack)).Message, Does.Contain("traffic pack '2025-07-15' was not found"));
			Assert.Throws<ArgumentException>(() => runner.Initialize(TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.ByMmsi, mmsi: 123456789)));
			// A pack that does not verify: the start is refused naming the file.
			var bin = Path.Combine(_packs.Root, "harbor", "traffic", TrafficTestPacks.Date, "reports.bin");
			var bytes = File.ReadAllBytes(bin);
			bytes[10] ^= 1;
			File.WriteAllBytes(bin, bytes);
			var corrupt = Assert.Throws<InvalidOperationException>(() => runner.Initialize(TrafficTestPacks.Scenario()));
			Assert.That(corrupt.Message, Does.StartWith(bin).And.Contain("SHA-256"));
			Assert.That(runner.RunId, Is.EqualTo(run), "the run that was going goes on");
			Assert.That(runner.IsRunActive, Is.True);
			Assert.That(runner.Traffic, Is.Not.Null);
		}
	}
}
