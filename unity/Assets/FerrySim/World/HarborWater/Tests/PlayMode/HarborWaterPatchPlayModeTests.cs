using System;
using System.Collections;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.World;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.HarborWater.Tests
{
	/// <summary>Exercises the real mesh component across tide updates and a floating-origin shift.</summary>
	public class HarborWaterPatchPlayModeTests
	{
		internal static HarborWaterService Synthetic()
		{
			var grid = new HarborGeoidLattice(0, 0, 250, 3, 3, new double[] { -32, -31.9, -31.8, -31.9, -31.8, -31.7, -31.8, -31.7, -31.6 }, new byte[] { 1, 1, 1, 1 });
			var times = new double[]
			{
				-3600,
				0,
				3600,
				90000
			};
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
			return new HarborWaterService(new HarborDatum(grid), RunEnvironment.ForPack(pack, epoch));
		}

		[UnityTest]
		public IEnumerator Patches_ShareEdgeNodes_AndStayAnchoredThroughTideAndOriginShift()
		{
			var service = Synthetic();
			var root = new GameObject("water-playmode");
			var geo = root.AddComponent<CesiumGeoreference>();
			var origin = LocalTangentPlane.World.ToGeodetic(200, 200);
			geo.SetOriginLongitudeLatitudeHeight(origin.LongitudeDegrees, origin.LatitudeDegrees, -32);
			HarborWaterPatch Make(int firstEast)
			{
				var child = new GameObject("water-patch");
				child.transform.SetParent(root.transform, false);
				var patch = child.AddComponent<HarborWaterPatch>();
				patch.Configure(service, 0, firstEast, 2, 2, null);
				return patch;
			}

			var a = Make(0);
			var b = Make(2);
			try
			{
				yield return null;
				Assert.That(a.NodeCount, Is.EqualTo(9), "a 2x2 cell patch uses nine shared vertices");
				Assert.That(a.Mesh.triangles.Length, Is.EqualTo(24));
				Assert.That(root.GetComponentsInChildren<Collider>(), Is.Empty, "water never supplies physics depth or contacts");
				foreach (var time in new[]
				{
					0.0,
					3600.0
				}

				)
				{
					a.UpdateWater(time);
					b.UpdateWater(time);
					foreach (var shift in new[]
					{
						0,
						120
					}

					)
					{
						var shifted = LocalTangentPlane.World.ToGeodetic(200 + shift, 200);
						geo.SetOriginLongitudeLatitudeHeight(shifted.LongitudeDegrees, shifted.LatitudeDegrees, -32);
						var av = a.Mesh.vertices;
						var bv = b.Mesh.vertices;
						var shared = 0;
						foreach (var p in av)
						{
							var ecef = a.Frame.ToEcef(p);
							var actual = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)a.transform.TransformPoint(p));
							Assert.That(math.distance(actual, ecef), Is.LessThan(.001));
							foreach (var q in bv)
							{
								var other = b.Frame.ToEcef(q);
								if (math.distance(ecef, other) < .001)
								{
									var actualOther = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)b.transform.TransformPoint(q));
									Assert.That(math.distance(actual, actualOther), Is.LessThan(.001));
									shared++;
								}
							}
						}

						Assert.That(shared, Is.EqualTo(3), "both patches use the same three common edge coordinates");
					}
				}
			}
			finally
			{
				UnityEngine.Object.Destroy(root);
			}

			yield return null;
		}
	}
}
