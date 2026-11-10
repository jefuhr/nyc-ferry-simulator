using System;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Harbor.Player.Journeys;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests.Landings
{
	/// <summary>Failure modes: a declared corridor hides unknown/shallow water, an obstacle or an overhanging hull; supplied poses disagree.</summary>
	public class LandingApproachTests
	{
		private static JObject Data() => JObject.Parse(@"{
			'name':'synthetic-arrival','departure_stop':'test','arrival_stop':'test','date':'2025-07-14','minute':0,
			'arrival_blocked':false,'points':[[60,0]],'required_spans':[],
			'arrival_face':{'middle':[100,0],'normal':[-1,0],'segment_ids':[41]},
			'start':{'mode':'approach-only','position_ne_m':[0,0],'heading_rad':0,
				'corridor_ne_m':[[-20,-20],[80,-20],[80,20],[-20,20],[-20,-20]]}
		}");

		private static DataEnvironment Water(float bed)
		{
			var values = Enumerable.Repeat(-30f, 16 * 12).ToArray(); values[7 * 12 + 4] = bed;
			var grid = new DepthGrid("synthetic", -40, -40, 10, 16, 12, values);
			var depth = new DepthSet("synthetic", "NAVD88", 20, new[] { grid }, Array.Empty<DepthSource>(), "");
			var times = new[] { 0.0, 600.0 };
			var current = new CurrentStation("current", "Synthetic", "synthetic", 40.7, -74.015, 0, 0, null, "synthetic", "",
				new VectorSeries(times, new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 }));
			var level = new WaterLevelStation("level", "Synthetic", 40.7, -74.015, 0, 0, "synthetic", "",
				new ScalarSeries(times, new[] { 0.0, 0.0 }), new[] { false, false });
			var epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);
			return new DataEnvironment(new EnvironmentPack("synthetic", "2025-07-14", epoch, 0, 600, depth,
				new[] { current }, new[] { level }, Array.Empty<WindStation>(), Array.Empty<string>(), ""), epoch);
		}

		[TestCase(-30f, null), TestCase(0f, "shallower"), TestCase(float.NaN, "unknown depth")]
		public void CorridorPreflightChecksInteriorDepthBeforeDriving(float bed, string expected)
		{
			var vessel = VesselParametersJson.Load(System.IO.Path.Combine(LandingVideoScenarioTests.Root, "vessels/t-class.json"));
			var problem = HarborApproach.Problem(HarborJourney.Parse(Data()), vessel, Water(bed), Array.Empty<ContactSegment>(), out var minimum);
			if (expected == null) { Assert.That(problem, Is.Null); Assert.That(minimum, Is.GreaterThan(20)); }
			else Assert.That(problem, Does.Contain(expected));
		}

		[Test]
		public void CorridorRejectsAnObstacleAndAHullOutsideItsSides()
		{
			var vessel = VesselParametersJson.Load(System.IO.Path.Combine(LandingVideoScenarioTests.Root, "vessels/t-class.json"));
			var obstacle = new ContactSegment(new HorizontalVector(30, 30), new HorizontalVector(30, -30), new ContactSurface(1e6, 5e4, .3, .25), 73);
			Assert.That(HarborApproach.Problem(HarborJourney.Parse(Data()), vessel, Water(-30), new[] { obstacle }, out _), Does.Contain("segment 73"));
			var data = Data(); data["start"]["corridor_ne_m"] = JArray.Parse("[[-20,-1],[80,-1],[80,1],[-20,1],[-20,-1]]");
			Assert.That(HarborApproach.Problem(HarborJourney.Parse(data), vessel, Water(-30), Array.Empty<ContactSegment>(), out _), Does.Contain("hull leaves"));
		}

		[Test]
		public void ApproachRejectsMismatchAndLeavingTheCorridor()
		{
			var route = HarborJourney.Parse(Data()); var state = VesselState.Planar(0, 1, 0, 0);
			Assert.Throws<ArgumentException>(() => new HarborJourneyControls(route, () => state, () => false, 26));
			state = VesselState.Planar(0, 0, 0, .1);
			Assert.Throws<ArgumentException>(() => new HarborJourneyControls(route, () => state, () => false, 26));
			state = VesselState.Planar(0, 0, 0, 0);
			var controls = new HarborJourneyControls(route, () => state, () => false, 26);
			state = VesselState.Planar(0, 0, 25, 0);
			Assert.Throws<InvalidOperationException>(() => controls.ControlsForStep(1));
		}

		[Test]
		public void MalformedCorridorsAndNonFinitePosesAreRejected()
		{
			foreach (var ring in new[] { "[[-20,-20],[80,-20],[80,20],[-20,20]]", "[[-20,-20],[80,-20],[0,0],[80,20],[-20,20],[-20,-20]]", "[[0,0],[1,0],[2,0],[0,0]]" })
			{
				var data = Data(); data["start"]["corridor_ne_m"] = JArray.Parse(ring);
				Assert.Throws<FormatException>(() => HarborJourney.Parse(data));
			}
			var invalid = Data(); invalid["start"]["heading_rad"] = double.NaN;
			Assert.Throws<FormatException>(() => HarborJourney.Parse(invalid));
			invalid = Data(); invalid["start"]["position_ne_m"][0] = double.PositiveInfinity;
			Assert.Throws<FormatException>(() => HarborJourney.Parse(invalid));
		}
	}
}
