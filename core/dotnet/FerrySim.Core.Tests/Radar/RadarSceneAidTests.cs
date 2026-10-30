using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Radar;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Core.World.Chart;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Radar
{
	/// <summary>Review item 4: aids outside active coverage stay in the data but are never drawn (decision 0005), by the radar too.</summary>
	public class RadarSceneAidTests
	{
		private static HarborChartPack EmptyChart(string runtimeDigest)
		{
			var layers = new List<ChartFeatureLayer>();
			for (var i = 0; i < 8; i++) layers.Add(new ChartFeatureLayer(new List<ChartFeature>(), new Dictionary<long, int[]>()));
			return new HarborChartPack(new string('c', 64), runtimeDigest, layers, HarborValue.Null);
		}

		private static HarborAid Aid(string id, double north, double east) =>
			new HarborAid(id, id, new HorizontalVector(north, east), HarborValue.FromFields(new Dictionary<string, HarborValue> { { "properties", HarborValue.FromFields(new Dictionary<string, HarborValue>()) } }));

		private static HarborRuntimePack Runtime(HarborGeometry coverage, params HarborAid[] aids) =>
			new HarborRuntimePack("synthetic", new string('a', 64), new string('b', 64), aids, new HarborBridge[0], new HarborStop[0], new HarborBarge[0],
				new HarborGeoidLattice(-2500, -2500, 5000, 2, 2, new double[] { 0, 0, 0, 0 }, new byte[] { 1 }), coverage, new HarborFootprint[0], HarborValue.Null, "[]");

		private static HarborGeometry Box(double n0, double e0, double n1, double e1) =>
			new HarborGeometry("Polygon", new[] { new HarborPolygon(new IReadOnlyList<HorizontalVector>[] { new[] { new HorizontalVector(n0, e0), new HorizontalVector(n0, e1), new HorizontalVector(n1, e1), new HorizontalVector(n1, e0), new HorizontalVector(n0, e0) } }) },
				new IReadOnlyList<HorizontalVector>[0], new HorizontalVector[0]);

		[Test]
		public void AnAidOutsideActiveCoverageIsNotARadarTarget()
		{
			var runtime = Runtime(Box(-2500, -2500, 2500, 2500), Aid("in:1", 100, 200), Aid("in:2", -2000, 2400), Aid("out:1", 5000, 5000), Aid("out:2", -2501, 0));
			var scene = RadarScene.FromPacks(EmptyChart(runtime.Digest), runtime);
			Assert.That(scene.AidTargets.Count, Is.EqualTo(2), "the two boundary aids are excluded like the chartplotter's BuildRuntime does");
			Assert.That(scene.AidTargets.Any(a => a.North == 5000 && a.East == 5000), Is.False);
			Assert.That(scene.AidTargets.Any(a => a.North == -2501), Is.False);
			Assert.That(scene.AidTargets.Any(a => a.North == 100 && a.East == 200), Is.True);
		}

		[Test]
		public void WithoutACoveragePolygonEveryAidIsATarget()
		{
			var runtime = Runtime(new HarborGeometry("Polygon", new HarborPolygon[0], new IReadOnlyList<HorizontalVector>[0], new HorizontalVector[0]), Aid("a", 0, 0), Aid("b", 9000, 9000));
			Assert.That(RadarScene.FromPacks(EmptyChart(runtime.Digest), runtime).AidTargets.Count, Is.EqualTo(2));
		}

		[Test, Category("HarborRealData")]
		public void TheRealPacksTwoBoundaryAidsAreAbsentFromTheRadar()
		{
			var harbor = Path.Combine(RepoPaths.Root, "data/processed/harbor");
			if (!Directory.Exists(Path.Combine(harbor, "chart"))) Assert.Ignore("The harbor data is not copied.");
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			var scene = RadarScene.FromPacks(chart, runtime);
			foreach (var id in new[] { "0226022294660032", "02260222946E0032" })
			{
				var aid = runtime.Aids.Single(a => a.Id == id || a.SourceId == id);
				Assert.That(scene.AidTargets.Count(a => a.North == aid.Position.North && a.East == aid.Position.East), Is.Zero, "contract-excluded aid " + id + " must not echo");
			}

			Assert.That(scene.AidTargets.Count, Is.EqualTo(runtime.Aids.Count - 2), "every other aid stays: " + runtime.Aids.Count + " in the pack");
		}
	}
}
