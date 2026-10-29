using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.HarborWater.Tests
{
	/// <summary>Explicit real-export checks, separate from the small committed fixtures and the default repository gate.</summary>
	public class HarborWaterAcceptanceTests
	{
		private static string RepoRoot => Directory.GetParent(Application.dataPath).Parent.FullName;
		private static string Output => Path.Combine(RepoRoot, "artifacts/harbor-runtime");

		[Test, Explicit("Requires the copied harbor environment and runtime export.")]
		public void RealHarbor_SurfaceAnchorsSeamsAndAllocation()
		{
			var harbor = Path.Combine(RepoRoot, "data/processed/harbor");
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var pack = EnvironmentPackJson.Load(Path.Combine(harbor, "env/days/2025-07-14"));
			var service = new HarborWaterService(new HarborDatum(runtime.Geoid), RunEnvironment.ForPack(pack, pack.EpochUtc));
			Directory.CreateDirectory(Output);
			var contacts = JArray.Parse(runtime.ContactsJson);
			var landings = contacts.GroupBy(row => (string)row["stop_id"]).Select(group =>
			{
				var face = group.First(row => (bool? )row["primary_face"] == true);
				return (Id: "landing-" + group.Key, North: ((double)face["start_ne_m"][0] + (double)face["end_ne_m"][0]) / 2, East: ((double)face["start_ne_m"][1] + (double)face["end_ne_m"][1]) / 2);
			}).ToArray();
			Assert.That(landings.Length, Is.EqualTo(24));
			var knots = pack.WaterLevelStations.SelectMany(gauge => Enumerable.Range(0, gauge.Level.Count).Select(gauge.Level.Time)).Where(time => time >= 0 && time <= 86400).Distinct().OrderBy(time => time).ToArray();
			var pier = landings.First(point => point.Id == "landing-87");
			var distant = landings.OrderByDescending(point => Math.Pow(point.North - pier.North, 2) + Math.Pow(point.East - pier.East, 2)).First();
			var surfaceTimes = Extremes(service, pier.North, pier.East, knots).Concat(SpreadExtremes(service, pier, distant, knots)).Distinct().ToArray();
			var surface = CheckSurface(service, pack, surfaceTimes);
			var seam = CheckSeams(service, pack, surfaceTimes);
			var positions = landings.Concat(pack.Depth.Grids.SelectMany(grid => new[] { (Id: "corner", North: grid.OriginNorth, East: grid.OriginEast), (Id: "corner", North: grid.OriginNorth, East: grid.EndEast), (Id: "corner", North: grid.EndNorth, East: grid.OriginEast), (Id: "corner", North: grid.EndNorth, East: grid.EndEast), })).Distinct().ToArray();
			var anchors = CheckAnchors(service, positions, knots);
			var upload = CheckUpdates(service, pack);
			File.WriteAllText(Path.Combine(Output, "water-summary.csv"), "metric,value\n" + Csv("surface_samples", surface.Samples) + Csv("surface_max_error_m", surface.Error) + Csv("uniform_cell_m", HarborWaterPatch.Spacing) + Csv("refined_cells", 0) + Csv("anchor_positions", positions.Length) + Csv("landing_midpoints", landings.Length) + Csv("anchor_max_error_m", anchors) + Csv("edge_max_gap_m", seam) + Csv("patches", upload.Patches) + Csv("vertices", upload.Vertices) + Csv("update_ms", upload.Milliseconds) + Csv("update_GC_Alloc_markers", upload.Allocations));
			TestContext.WriteLine(File.ReadAllText(Path.Combine(Output, "water-summary.csv")));
			Assert.That(surface.Error, Is.LessThan(.02), "full 100 m lattice, both local tide extremes and spatial-spread extremes");
			Assert.That(anchors, Is.LessThan(.01), "all landing midpoints and grid corners, each local tide extreme, four headings, 0/120 m origin shifts");
			Assert.That(seam, Is.LessThan(.001), "identical shared-edge coordinates in adjacent local float frames");
			Assert.That(upload.Allocations, Is.Zero, "calibrated GC.Alloc including Mesh.SetVertices");
		}

		private static double[] Extremes(HarborWaterService service, double north, double east, double[] knots)
		{
			return new[]
			{
				knots.OrderBy(time => service.Environment.WaterLevel(north, east, time)).First(),
				knots.OrderByDescending(time => service.Environment.WaterLevel(north, east, time)).First(),
			};
		}

		private static double[] SpreadExtremes(HarborWaterService service, (string Id, double North, double East) near, (string Id, double North, double East) far, double[] knots)
		{
			double Spread(double time) => service.Environment.WaterLevel(near.North, near.East, time) - service.Environment.WaterLevel(far.North, far.East, time);
			return new[]
			{
				knots.OrderBy(Spread).First(),
				knots.OrderByDescending(Spread).First()
			};
		}

		private static (long Samples, double Error) CheckSurface(HarborWaterService service, EnvironmentPack pack, double[] times)
		{
			var nodes = new WaterNode[4];
			var weights = new double[4 * service.GaugeCount];
			var levels = new double[service.GaugeCount];
			var vertices = new Vector3[4];
			var maximum = 0.0;
			long samples = 0;
			using var csv = new StreamWriter(Path.Combine(Output, "water-surface.csv"));
			csv.WriteLine("tile,time_s,north_m,east_m,max_error_m");
			foreach (var grid in pack.Depth.Grids)
			{
				var frame = WaterFrame.AtNorthEast((grid.OriginNorth + grid.EndNorth) / 2, (grid.OriginEast + grid.EndEast) / 2);
				for (var north = grid.OriginNorth; north < grid.EndNorth; north += 100)
				{
					for (var east = grid.OriginEast; east < grid.EndEast; east += 100)
					{
						Assert.That(service.CellCovered(north, east, 100), Is.True, grid.Id);
						nodes[0] = service.BakeNode(north, east, frame, weights, 0);
						nodes[1] = service.BakeNode(north, east + 100, frame, weights, service.GaugeCount);
						nodes[2] = service.BakeNode(north + 100, east, frame, weights, 2 * service.GaugeCount);
						nodes[3] = service.BakeNode(north + 100, east + 100, frame, weights, 3 * service.GaugeCount);
						foreach (var time in times)
						{
							service.SampleGaugeLevels(time, levels);
							service.UpdateNodes(nodes, weights, levels, vertices);
							var a = frame.ToEcef(vertices[0]) - frame.OriginEcef;
							var b = frame.ToEcef(vertices[1]) - frame.OriginEcef;
							var c = frame.ToEcef(vertices[2]) - frame.OriginEcef;
							var d = frame.ToEcef(vertices[3]) - frame.OriginEcef;
							var error = 0.0;
							for (var row = 1; row < 4; row++)
							{
								for (var column = 1; column < 4; column++)
								{
									var u = column / 4.0;
									var v = row / 4.0;
									var interpolated = u + v <= 1 ? a * (1 - u - v) + b * u + c * v : d * (u + v - 1) + b * (1 - v) + c * (1 - u);
									var n = north + v * 100;
									var e = east + u * 100;
									var exact = ExactEcef(n, e, service.EllipsoidalHeight(n, e, time));
									error = Math.Max(error, math.distance(frame.OriginEcef + interpolated, exact));
									samples++;
								}
							}

							maximum = Math.Max(maximum, error);
							csv.Write(Csv(grid.Id, time, north, east, error));
						}
					}
				}
			}

			return (samples, maximum);
		}

		private static double CheckSeams(HarborWaterService service, EnvironmentPack pack, double[] times)
		{
			var maximum = 0.0;
			var nodes = new WaterNode[2];
			var weights = new double[2 * service.GaugeCount];
			var levels = new double[service.GaugeCount];
			var vertices = new Vector3[2];
			for (var i = 0; i < pack.Depth.Grids.Count; i++)
			{
				var a = pack.Depth.Grids[i];
				var frameA = WaterFrame.AtNorthEast((a.OriginNorth + a.EndNorth) / 2, (a.OriginEast + a.EndEast) / 2);
				for (var j = i + 1; j < pack.Depth.Grids.Count; j++)
				{
					var b = pack.Depth.Grids[j];
					var frameB = WaterFrame.AtNorthEast((b.OriginNorth + b.EndNorth) / 2, (b.OriginEast + b.EndEast) / 2);
					for (var north = Math.Max(a.OriginNorth, b.OriginNorth); north <= Math.Min(a.EndNorth, b.EndNorth); north += 100)
					{
						for (var east = Math.Max(a.OriginEast, b.OriginEast); east <= Math.Min(a.EndEast, b.EndEast); east += 100)
						{
							nodes[0] = service.BakeNode(north, east, frameA, weights, 0);
							nodes[1] = service.BakeNode(north, east, frameB, weights, service.GaugeCount);
							foreach (var time in times)
							{
								service.SampleGaugeLevels(time, levels);
								service.UpdateNodes(nodes, weights, levels, vertices);
								maximum = Math.Max(maximum, math.distance(frameA.ToEcef(vertices[0]), frameB.ToEcef(vertices[1])));
							}
						}
					}
				}
			}

			return maximum;
		}

		private static double CheckAnchors(HarborWaterService service, (string Id, double North, double East)[] positions, double[] knots)
		{
			var root = new GameObject("harbor-water-precision");
			var geo = root.AddComponent<CesiumGeoreference>();
			var hull = new GameObject("floating-anchor");
			hull.transform.SetParent(root.transform, false);
			var anchor = hull.AddComponent<CesiumGlobeAnchor>();
			anchor.detectTransformChanges = false;
			anchor.adjustOrientationForGlobeWhenMoving = false;
			var maximum = 0.0;
			try
			{
				using var csv = new StreamWriter(Path.Combine(Output, "water-anchors.csv"));
				csv.WriteLine("id,north_m,east_m,time_s,heading_deg,origin_shift_m,center_error_m,near_field_error_m");
				foreach (var point in positions)
				{
					foreach (var time in Extremes(service, point.North, point.East, knots))
					{
						var ll = LocalTangentPlane.World.ToGeodetic(point.North, point.East);
						var height = service.EllipsoidalHeight(point.North, point.East, time);
						var expected = ExactEcef(point.North, point.East, height);
						var frame = WaterFrame.AtNorthEast(point.North, point.East);
						foreach (var heading in new[]
						{
							0,
							90,
							180,
							270
						}

						)
						{
							anchor.longitudeLatitudeHeight = new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, height);
							anchor.rotationEastUpNorth = Quaternion.Euler(0, heading, 0);
							foreach (var shift in new[]
							{
								0,
								120
							}

							)
							{
								var origin = LocalTangentPlane.World.ToGeodetic(point.North + shift, point.East);
								geo.SetOriginLongitudeLatitudeHeight(origin.LongitudeDegrees, origin.LatitudeDegrees, height);
								var center = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)hull.transform.position);
								var local = new Vector3(4, 1, 30);
								var rotated = Quaternion.Euler(0, heading, 0) * local;
								var vertex = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)hull.transform.TransformPoint(local));
								var centerError = math.distance(center, expected);
								var vertexError = math.distance(vertex, expected + frame.East * rotated.x + frame.Up * rotated.y + frame.North * rotated.z);
								maximum = Math.Max(maximum, Math.Max(centerError, vertexError));
								csv.Write(Csv(point.Id, point.North, point.East, time, heading, shift, centerError, vertexError));
							}
						}
					}
				}
			}
			finally
			{
				Object.DestroyImmediate(root);
			}

			return maximum;
		}

		private static (int Patches, int Vertices, double Milliseconds, int Allocations) CheckUpdates(HarborWaterService service, EnvironmentPack pack)
		{
			var root = new GameObject("harbor-water-update-cost");
			root.AddComponent<CesiumGeoreference>();
			var patches = new List<HarborWaterPatch>();
			try
			{
				foreach (var grid in pack.Depth.Grids)
				{
					var child = new GameObject(grid.Id);
					child.transform.SetParent(root.transform, false);
					var patch = child.AddComponent<HarborWaterPatch>();
					patch.Configure(service, (int)(grid.OriginNorth / 100), (int)(grid.OriginEast / 100), 40, 40, null);
					patches.Add(patch);
				}

				for (var frame = 0; frame < 10; frame++)
				{
					for (var i = 0; i < patches.Count; i++)
					{
						patches[i].UpdateWater(frame);
					}
				}

				HarborWaterTests.CalibrateAllocationRecorder();
				var timer = new Stopwatch();
				var recorder = HarborWaterTests.BeginAllocations();
				timer.Start();
				for (var frame = 0; frame < 300; frame++)
				{
					for (var i = 0; i < patches.Count; i++)
					{
						patches[i].UpdateWater(30000 + frame / 60.0);
					}
				}

				timer.Stop();
				var allocations = HarborWaterTests.EndAllocations(recorder);
				return (patches.Count, patches.Sum(patch => patch.NodeCount), timer.Elapsed.TotalMilliseconds / 300, allocations);
			}
			finally
			{
				Object.DestroyImmediate(root);
			}
		}

		private static double3 ExactEcef(double north, double east, double height)
		{
			var ll = LocalTangentPlane.World.ToGeodetic(north, east);
			return CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, height));
		}

		private static string Csv(params object[] fields) => string.Join(",", fields.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))) + "\n";
	}
}
