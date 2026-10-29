using System;
using System.IO;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.HarborWater;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Harbor.Tests.Integration
{
	/// <summary>
	/// Failure modes of the composition root: a guessed water height before a run or outside the datum; an old run's
	/// water surviving a new start; an audit of another depth set marking berths; an audit mark overriding the crew-answer
	/// note of a face without a start pose; inactive tiles becoming traffic coverage; a hold without an explanation; the
	/// 74 MB depth set read twice for the preview and the start.
	/// </summary>
	public class HarborIntegrationTests
	{
		private const string Audit = @"{
			""schema"": ""ferrysim-harbor-landing-audit/1"",
			""depth_digest"": ""b58ed9ef82109126c6ed6bfc7243754f4c6578fc6b16706efcea77b59aaa9eaa"",
			""rows_per_hull"": 700,
			""berths"": [
				{""stop_id"": ""87"", ""berth_id"": ""enc:SOUTH"", ""eligible"": true, ""hulls"": [
					{""hull"": ""t-class"", ""rows"": 700, ""accepted"": 467}, {""hull"": ""k-class"", ""rows"": 700, ""accepted"": 598}]},
				{""stop_id"": ""11"", ""berth_id"": ""enc:MAIN"", ""eligible"": true, ""hulls"": [
					{""hull"": ""t-class"", ""rows"": 700, ""accepted"": 700}, {""hull"": ""k-class"", ""rows"": 700, ""accepted"": 700}]},
				{""stop_id"": ""137"", ""berth_id"": ""enc:FACE"", ""eligible"": false, ""hulls"": [
					{""hull"": ""t-class"", ""rows"": 700, ""accepted"": 0}, {""hull"": ""k-class"", ""rows"": 700, ""accepted"": 0}]}
			]
		}";

		private static HarborDatum Datum() =>
			new HarborDatum(new HarborGeoidLattice(0, 0, 250, 3, 3, new double[] { -32, -31.9, -31.8, -31.9, -31.8, -31.7, -31.8, -31.7, -31.6 }, new byte[] { 1, 1, 1, 1 }));

		/// <summary>A run's water as HarborWaterTests builds it: gauge a at the origin reads -1, 0, +1, 0 m across the day.</summary>
		private static HarborWaterService Synthetic()
		{
			var times = new double[] { -3600, 0, 3600, 90000 };
			var origin = LocalTangentPlane.World.ToGeodetic(0, 0);
			var remote = LocalTangentPlane.World.ToGeodetic(500, 500);
			var gauges = new[]
			{
				new WaterLevelStation("a", "a", origin.LatitudeDegrees, origin.LongitudeDegrees, 0, 0, "synthetic", "", new ScalarSeries(times, new double[] { -1, 0, 1, 0 }), new bool[4]),
				new WaterLevelStation("b", "b", remote.LatitudeDegrees, remote.LongitudeDegrees, 500, 500, "synthetic", "", new ScalarSeries(times, new double[] { 1, 2, 3, 2 }), new bool[4]),
			};
			var depth = new DepthSet("harbor", "NAVD88", 20, new[] { new DepthGrid("synthetic", 0, 0, 500, 2, 2, new float[] { -10, -10, -10, -10 }) }, Array.Empty<DepthSource>(), "");
			var currents = new[]
			{
				new CurrentStation("c", "c", "harmonic-prediction", origin.LatitudeDegrees, origin.LongitudeDegrees, 0, 0, null, "synthetic", "", new VectorSeries(times, new double[4], new double[4]))
			};
			var epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);
			var pack = new EnvironmentPack("harbor", "2025-07-14", epoch, -3600, 90000, depth, currents, gauges, Array.Empty<WindStation>(), Array.Empty<string>(), "");
			return new HarborWaterService(Datum(), RunEnvironment.ForPack(pack, epoch));
		}

		[Test]
		public void Forwarder_BeforeTheFirstRun_IsTheDatumAlone_AndRefusesOutsideIt()
		{
			var water = new HarborWaterForwarder(Datum());
			Assert.That(water.Current, Is.Null);
			Assert.That(water.EllipsoidalHeight(0, 0, 1234), Is.EqualTo(-32), "NAVD88 zero: no tide before a run");
			Assert.That(water.EllipsoidalHeight(250, 250, 0), Is.EqualTo(-31.8).Within(1e-12));
			Assert.Throws<InvalidOperationException>(() => water.EllipsoidalHeight(-1, 0, 0), "nothing extrapolates");
			Assert.Throws<InvalidOperationException>(() => water.EllipsoidalHeight(600, 600, 0));
		}

		[Test]
		public void Forwarder_TakesEachRunsWater_AndForwardsToTheLatest()
		{
			var water = new HarborWaterForwarder(Datum());
			var first = Synthetic();
			water.Bind(first);
			Assert.That(water.Current, Is.SameAs(first));
			Assert.That(water.Generation, Is.EqualTo(1));
			Assert.That(water.EllipsoidalHeight(0, 0, 0), Is.EqualTo(first.EllipsoidalHeight(0, 0, 0)));
			Assert.That(water.EllipsoidalHeight(0, 0, 0), Is.EqualTo(-32), "gauge a reads 0 at time 0");
			Assert.That(water.EllipsoidalHeight(0, 0, 3600), Is.EqualTo(-31), "gauge a reads +1 m an hour later: the tide is the run's");
			var second = Synthetic();
			water.Bind(second);
			Assert.That(water.Current, Is.SameAs(second));
			Assert.That(water.Generation, Is.EqualTo(2));
			Assert.Throws<ArgumentNullException>(() => water.Bind(null));
		}

		[Test]
		public void LandingAudit_MarksBerthsWithAStartPose_AndLeavesTheCrewFacesToTheCatalog()
		{
			var audit = HarborLandingAudit.Parse(Audit, "fixture audit");
			Assert.That(audit.DepthDigest, Is.EqualTo("b58ed9ef82109126c6ed6bfc7243754f4c6578fc6b16706efcea77b59aaa9eaa"));
			Assert.That(audit.RowsPerHull, Is.EqualTo(700));
			Assert.That(audit.Berths.Count, Is.EqualTo(3));
			Assert.That(audit.Marks.Count, Is.EqualTo(2), "no mark for the face without a start pose");
			Assert.That(audit.Marks[0].StopId, Is.EqualTo("87"));
			Assert.That(audit.Marks[0].BerthId, Is.EqualTo("enc:SOUTH"));
			Assert.That(audit.Marks[0].Note, Is.EqualTo("Landing audit: afloat T 467/700 · K 598/700"));
			Assert.That(audit.Marks[0].Refusal, Is.Null, "the audit refuses nothing; the catalog refuses aground starts per tide");
			Assert.That(audit.Marks[1].Note, Is.EqualTo("Landing audit: afloat at every sampled tide"));
			Assert.That(audit.Marks[1].Applies("11", "enc:MAIN"), Is.True);
			Assert.That(audit.Marks[1].Applies("11", "enc:OTHER"), Is.False);
			Assert.That(audit.Berths[2].Eligible, Is.False);
			Assert.That(audit.Berths[2].Note, Is.Empty);
		}

		[TestCase("\"schema\": \"ferrysim-harbor-landing-audit/1\"", "\"schema\": \"ferrysim-harbor-landing-audit/2\"", "schema")]
		[TestCase("\"rows_per_hull\": 700", "\"rows_per_hull\": 0", "rows_per_hull")]
		[TestCase("\"accepted\": 467", "\"accepted\": 701", "701 accepted of 700")]
		[TestCase("\"eligible\": false", "\"eligible\": \"no\"", "boolean 'eligible'")]
		[TestCase("\"depth_digest\": \"b58ed9ef82109126c6ed6bfc7243754f4c6578fc6b16706efcea77b59aaa9eaa\"", "\"depth_digest\": \"b58e\"", "depth_digest")]
		public void LandingAudit_RefusesAnotherSchemaOrABrokenCount_NamingTheFile(string from, string to, string reason)
		{
			var error = Assert.Throws<FormatException>(() => HarborLandingAudit.Parse(Audit.Replace(from, to), "fixture audit"));
			Assert.That(error.Message, Does.StartWith("fixture audit: "));
			Assert.That(error.Message, Does.Contain(reason));
		}

		[Test]
		public void TrafficCoverage_TakesActiveTilesOnly_InSourceOrder_AndRefusesNone()
		{
			const string index = @"{""tiles"": [
				{""id"": ""tile_0_0"", ""active"": true, ""bounds_local_m"": [0, 0, 4000, 4000]},
				{""id"": ""tile_0_1"", ""active"": false, ""bounds_local_m"": [0, 4000, 4000, 8000]},
				{""id"": ""tile_1_0"", ""active"": true, ""bounds_local_m"": [4000, 0, 8000, 4000]}]}";
			var area = HarborTrafficCoverage.Parse(index, "fixture index");
			Assert.That(area.Extent.MinNorth, Is.EqualTo(0));
			Assert.That(area.Extent.MaxNorth, Is.EqualTo(8000));
			Assert.That(area.Extent.MinEast, Is.EqualTo(0));
			Assert.That(area.Extent.MaxEast, Is.EqualTo(4000), "the inactive tile's east extent is not coverage");
			Assert.That(area.Contains(2000, 2000), Is.True);
			Assert.That(area.Contains(2000, 6000), Is.False, "a hole stays a hole");
			Assert.That(area.Contains(6000, 2000), Is.True);
			var none = Assert.Throws<FormatException>(() => HarborTrafficCoverage.Parse(index.Replace("\"active\": true", "\"active\": false"), "fixture index"));
			Assert.That(none.Message, Is.EqualTo("fixture index: no active tiles."));
			Assert.Throws<FormatException>(() => HarborTrafficCoverage.Parse("{\"tiles\": [{\"id\": \"x\", \"active\": true, \"bounds_local_m\": [0, 0, 1]}]}", "fixture index"));
		}

		[Test]
		public void HoldStatus_ExplainsEachHold_AndNothingForNone()
		{
			Assert.That(HarborBootstrap.HoldStatus(RunHold.None), Is.Null);
			Assert.That(HarborBootstrap.HoldStatus(RunHold.OutsideCoveredWater), Does.StartWith("Run held").And.Contain("left the water").And.Contain("new run"));
			Assert.That(HarborBootstrap.HoldStatus(RunHold.BeyondDayData), Does.StartWith("Run held").And.Contain("run out").And.Contain("new run"));
			Assert.That(HarborBootstrap.HoldStatus(RunHold.OutsideCoveredWater), Is.Not.EqualTo(HarborBootstrap.HoldStatus(RunHold.BeyondDayData)));
		}

		[Test, Category("HarborRealData")]
		public void SharedDepth_ReadsTheVerifiedDepthSetOnce_AcrossDays_AndMatchesTheShippedAudit()
		{
			var root = Path.Combine(SimPaths.RepoRoot, EnvironmentPacks.ProcessedFolder);
			var runtime = Path.Combine(root, "harbor", "runtime");
			if (!Directory.Exists(runtime) || !Directory.Exists(Path.Combine(root, "harbor", "env", "days")))
			{
				Assert.Ignore("The harbor runtime export and environment packs are not copied to " + Path.Combine(root, "harbor") + ".");
			}

			var packs = new EnvironmentPacks(root);
			var first = packs.LoadHarborDay("harbor/2025-07-14");
			var second = packs.LoadHarborDay("harbor/2025-07-15");
			Assert.That(first.Depth, Is.SameAs(second.Depth), "one resident verified depth set");
			var preview = new SharedDepthEnvironments(packs).Get(packs.ReadDay("harbor/2025-07-16"), out var problem);
			Assert.That(problem, Is.Null);
			Assert.That(preview.Pack.Depth, Is.SameAs(first.Depth));
			var pack = HarborRuntimePackJson.Load(runtime);
			Assert.That(pack.EnvironmentDepthDigest, Is.EqualTo(first.Depth.Digest), "the runtime export links the adopted depth set");
			var audit = HarborLandingAudit.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Harbor/Data/landing-audit.json")), HarborIntegrationFactory.AuditName);
			Assert.That(audit.DepthDigest, Is.EqualTo(pack.EnvironmentDepthDigest), "the shipped audit was assessed on the adopted depth set");
			Assert.That(audit.Berths.Count, Is.EqualTo(26));
			Assert.That(audit.Marks.Count, Is.EqualTo(23));
			var area = HarborTrafficCoverage.Load(Path.Combine(root, "harbor", "index.json"));
			Assert.That(area.Contains(351, 748), Is.True, "Pier 11 is covered");
			Assert.That(area.Contains(2000, 10000), Is.False, "tile_0_2 is not an active tile");
		}
	}
}
