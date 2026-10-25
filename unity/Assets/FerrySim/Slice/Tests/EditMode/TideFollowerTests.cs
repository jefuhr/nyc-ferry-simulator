using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The drawn water follows the run's tide (<see cref="TideFollower"/>). Failure modes guarded: the water left at the
	/// scene's level on a pack day, or set after the placement read it (a frame late); the level of another place or time
	/// than the vessel's last step; a calm run after a pack run left at the pack's tide; and a follower that keeps
	/// following after it is disposed.
	/// </summary>
	public class TideFollowerTests
	{
		private const string Pack = "lower-east-river/2025-07-20";

		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		private static ScenarioSettings OffPier11(string startUtc, string pack)
		{
			var position = LocalTangentPlane.World.ToGeodetic(250.0, 700.0);
			return new ScenarioSettings
			{
				name = "tide follower",
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = 49.0,
				scenarioStartUtc = startUtc,
				environment = new ScenarioEnvironment { pack = pack },
			};
		}

		[Test]
		public void TheDrawnWater_StandsAtThePacksLevelAtTheVessel_BeforeEveryPlacement_AndGoesBackForCalmWater()
		{
			if (!Directory.Exists(Path.Combine(SliceTestData.RepoRoot, "data/processed/lower-east-river/env/days/2025-07-20")))
			{
				Assert.Ignore("The environment packs are not built.");
			}

			_host = new GameObject("tide follower (test)");
			var water = new GameObject("water").AddComponent<WaterSurface>();
			water.transform.SetParent(_host.transform, false);
			water.WaterLevelNavd88 = 0.0;
			var runner = new GameObject("runner").AddComponent<VesselSimulationRunner>();
			runner.transform.SetParent(_host.transform, false);
			runner.RecordRuns = false;
			var levels = new System.Collections.Generic.List<double>();
			var tide = new TideFollower(runner, water);
			// Added after the follower, so it sees what the placement sees.
			runner.BeforePlacement += () => levels.Add(water.WaterLevelNavd88);

			runner.Initialize(OffPier11("2025-07-20T21:06:00Z", Pack), new ConstantControls(ControlInputs.Neutral), SliceTestData.LoadTClass());
			var atStart = runner.RunEnvironment.WaterLevel(runner.State.North, runner.State.East, 0.0);
			Assert.That(levels, Has.Count.EqualTo(1));
			Assert.That(levels[0], Is.EqualTo(atStart), "set before the run's first placement");
			Assert.That(atStart, Is.GreaterThan(0.8), "17:06 EDT is high water at The Battery that day");

			for (var frame = 0; frame < 30; frame++)
			{
				runner.AdvanceFrame(0.5);
				runner.ApplyRenderPose();
			}

			var state = runner.State;
			Assert.That(water.WaterLevelNavd88, Is.EqualTo(runner.RunEnvironment.WaterLevel(state.North, state.East, runner.Driver.SimulationTime)));
			Assert.That(water.WaterLevelNavd88, Is.EqualTo(runner.Environment.WaterLevelNavd88));
			Assert.That(water.transform.localPosition.y, Is.EqualTo((float)water.WaterLevelNavd88), "the surface is raised to the level");

			runner.Initialize(new ScenarioSettings { name = "calm after a pack" }, new ConstantControls(ControlInputs.Neutral), SliceTestData.LoadTClass());
			Assert.That(water.WaterLevelNavd88, Is.EqualTo(tide.BaseLevel));
			Assert.That(tide.BaseLevel, Is.EqualTo(0.0));

			tide.Dispose();
			runner.Initialize(OffPier11("2025-07-20T21:06:00Z", Pack), new ConstantControls(ControlInputs.Neutral), SliceTestData.LoadTClass());
			Assert.That(water.WaterLevelNavd88, Is.EqualTo(0.0), "a disposed follower no longer moves the water");
		}
	}
}
