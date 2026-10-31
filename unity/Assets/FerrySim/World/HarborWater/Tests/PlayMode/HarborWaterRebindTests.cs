using System;
using System.Collections;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.World;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.HarborWater.Tests
{
	public class HarborWaterRebindTests
	{
		private static HarborWaterService Day(int day, double gaugeEast = 500, bool reverse = false, double datumOffset = 0, byte mask = 1)
		{
			var grid = new HarborGeoidLattice(0, 0, 250, 3, 3,
				new[] { -32 + datumOffset, -31.9, -31.8, -31.9, -31.8, -31.7, -31.8, -31.7, -31.6 }, new byte[] { mask, 1, 1, 1 });
			var times = new double[] { -3600, 0, 3600, 90000 };
			var origin = LocalTangentPlane.World.ToGeodetic(0, 0);
			var remote = LocalTangentPlane.World.ToGeodetic(500, gaugeEast);
			var gauges = new[]
			{
				new WaterLevelStation("a", "a", origin.LatitudeDegrees, origin.LongitudeDegrees, 0, 0, "synthetic", "", new ScalarSeries(times, new double[] { day - 15, day - 14, day - 13, day - 14 }), new bool[4]),
				new WaterLevelStation("b", "b", remote.LatitudeDegrees, remote.LongitudeDegrees, 500, gaugeEast, "synthetic", "", new ScalarSeries(times, new double[] { day - 13, day - 12, day - 11, day - 12 }), new bool[4]),
			};
			if (reverse) Array.Reverse(gauges);
			var depth = new DepthSet("harbor", "NAVD88", 20, new[] { new DepthGrid("synthetic", 0, 0, 500, 2, 2, new float[] { -10, -10, -10, -10 }) }, Array.Empty<DepthSource>(), "");
			var currents = new[] { new CurrentStation("c", "c", "harmonic-prediction", origin.LatitudeDegrees, origin.LongitudeDegrees, 0, 0, null, "synthetic", "", new VectorSeries(times, new double[4], new double[4])) };
			var epoch = new DateTimeOffset(2025, 7, day, 4, 0, 0, TimeSpan.Zero);
			var pack = new EnvironmentPack("harbor", "2025-07-" + day, epoch, -3600, 90000, depth, currents, gauges, Array.Empty<WindStation>(), Array.Empty<string>(), "");
			return new HarborWaterService(new HarborDatum(grid), RunEnvironment.ForPack(pack, epoch.AddMinutes(day - 14)));
		}

		private static HarborWaterPatch Patch(Transform root, HarborWaterService service)
		{
			var child = new GameObject("water rebind patch");
			child.transform.SetParent(root, false);
			var patch = child.AddComponent<HarborWaterPatch>();
			patch.Configure(service, 0, 0, 4, 4, null);
			return patch;
		}

		[UnityTest]
		public IEnumerator Reconfigure_EqualLayoutsReuseMeshAndUseTheNewDaysTide()
		{
			var root = new GameObject("water rebind");
			root.AddComponent<CesiumGeoreference>();
			try
			{
				var first = Day(14);
				var next = Day(15);
				Assert.That(next.Datum.Lattice, Is.Not.SameAs(first.Datum.Lattice), "layout equality is by value");
				var patch = Patch(root.transform, first);
				var mesh = patch.Mesh;
				var before = mesh.vertices;
				patch.Configure(next, 0, 0, 4, 4, null);
				Assert.That(patch.Mesh, Is.SameAs(mesh), "matching datum, gauges and cell layout reuse the baked mesh");
				var cold = Patch(root.transform, next);
				foreach (var time in new[] { 0.0, 123.0, 3600.0 })
				{
					patch.UpdateWater(time);
					cold.UpdateWater(time);
					Assert.That(patch.Mesh.vertices, Is.EqualTo(cold.Mesh.vertices), "the new observer, levels and start-time offset match a cold build exactly");
				}
				Assert.That(Vector3.Distance(before[0], patch.Mesh.vertices[0]), Is.GreaterThan(.5f), "the previous day's tide was replaced");
			}
			finally { UnityEngine.Object.Destroy(root); }
			yield return null;
		}

		[UnityTest]
		public IEnumerator Reconfigure_ChangedDatumGaugeOrCellLayoutRebuildsLikeAColdPatch()
		{
			var root = new GameObject("water rebind mismatches");
			root.AddComponent<CesiumGeoreference>();
			try
			{
				foreach (var next in new[] { Day(15, gaugeEast: 499), Day(15, reverse: true), Day(15, datumOffset: .01), Day(15, mask: 0) })
				{
					var patch = Patch(root.transform, Day(14));
					var before = patch.Mesh;
					patch.Configure(next, 0, 0, 4, 4, null);
					Assert.That(patch.Mesh, Is.Not.SameAs(before), "same counts cannot hide different datum nodes/mask or gauge coordinates/order");
					var cold = Patch(root.transform, next);
					patch.UpdateWater(123);
					cold.UpdateWater(123);
					Assert.That(patch.Mesh.vertices, Is.EqualTo(cold.Mesh.vertices));
					Assert.That(patch.Mesh.triangles, Is.EqualTo(cold.Mesh.triangles));
				}
				var moved = Patch(root.transform, Day(14));
				var oldMesh = moved.Mesh;
				moved.Configure(Day(15), 1, 0, 3, 4, null);
				Assert.That(moved.Mesh, Is.Not.SameAs(oldMesh), "changed patch coordinates or shape rebuild");
			}
			finally { UnityEngine.Object.Destroy(root); }
			yield return null;
		}
	}
}
