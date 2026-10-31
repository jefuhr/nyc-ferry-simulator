using System;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Sim;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace FerrySim.World.HarborNavigation.Tests
{
	public class NavigationOriginTests
	{
		private static readonly ProfilerMarker ShiftMarker = new ProfilerMarker("FerrySim.Navigation.OriginShift");
		[Test, Explicit("Real runtime pack; isolated main-thread origin-shift timing and allocation, not a player budget verdict")]
		public void RealNavigationOriginShiftCostAndAllocation()
		{
			var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var pack = HarborRuntimePackJson.Load(Path.Combine(repo, "data/processed/harbor/runtime"));
			var root = new GameObject("origin cost fixture");
			try
			{
				var geo = root.AddComponent<CesiumGeoreference>(); geo.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
				var world = root.AddComponent<HarborNavigationWorld>(); world.Build(pack, geo, new DatumWater(pack), null);
				var anchors = root.GetComponentsInChildren<CesiumGlobeAnchor>().Length;
				void Shift(int i)
				{
					ShiftMarker.Begin();
					geo.SetOriginLongitudeLatitudeHeight(i % 2 == 0 ? -74.014 : -74.015, 40.7, 0);
					ShiftMarker.End();
				}
				for (var i = 0; i < 30; i++) Shift(i);
				// Collect individual calls immediately, rather than the legacy Recorder previous-frame aggregate.
				using var recorder = ProfilerRecorder.StartNew(ShiftMarker, 200, ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
				var elapsed = new double[200];
				for (var i = 0; i < elapsed.Length; i++)
				{
					var start = System.Diagnostics.Stopwatch.GetTimestamp();
					Shift(i);
					elapsed[i] = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				}
				recorder.Stop();
				var samples = recorder.Count; var nanoseconds = 0L;
				for (var i = 0; i < samples; i++) nanoseconds += recorder.GetSample(i).Value;
				var allocations = NavigationTests.MeasureAllocations(() => { for (var i = 0; i < 100; i++) Shift(i); });
				var csv = new StringBuilder("shift,main_thread_ms,anchors\n");
				for (var i = 0; i < elapsed.Length; i++) csv.Append(i).Append(',').Append(elapsed[i].ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(anchors).Append('\n');
				var folder = Path.Combine(repo, "artifacts/labels-origin-shift"); Directory.CreateDirectory(folder);
				File.WriteAllText(Path.Combine(folder, "origin-cost-" + anchors + ".csv"), csv.ToString());
				Array.Sort(elapsed);
				TestContext.WriteLine("anchors=" + anchors + "; shifts=200; median_ms=" + elapsed[100].ToString("R", CultureInfo.InvariantCulture) + "; max_ms=" + elapsed[199].ToString("R", CultureInfo.InvariantCulture) + "; marker_samples=" + samples + "; marker_ns=" + nanoseconds + "; GC.Alloc=" + allocations);
				Assert.That(allocations, Is.Zero);
				Assert.That(samples, Is.EqualTo(200), "profiler marker observes every measured shift");
				Assert.That(nanoseconds, Is.GreaterThan(0), "profiler marker records nonzero main-thread elapsed time");
				Assert.That(anchors, Is.LessThanOrEqualTo(128), "static navigation shares regional anchors; floating barges retain their own");
			}
			finally { UnityEngine.Object.DestroyImmediate(root); }
		}

		[Test, Explicit("Real runtime pack; every drawn aid at every landing origin, with lamps and bridge mesh points retained")]
		public void RealStaticNavigationKeepsPoseThroughEveryLandingOrigin()
		{
			var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
			var pack = HarborRuntimePackJson.Load(Path.Combine(repo, "data/processed/harbor/runtime"));
			var root = new GameObject("static navigation precision");
			try
			{
				var geo = root.AddComponent<CesiumGeoreference>(); geo.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
				var world = root.AddComponent<HarborNavigationWorld>(); world.Build(pack, geo, new DatumWater(pack), null);
				var objects = new System.Collections.Generic.List<Transform>();
				foreach (var aid in world.Aids) objects.Add(aid.transform);
				foreach (var span in world.Spans) if (span.Drawn) objects.Add(span.transform);
				for (var i = 0; i < world.Lights.Count; i++) objects.Add(world.Lights.Lamp(i));
				foreach (var light in world.BridgeSignals) objects.Add(light.transform);
				var samples = new[] { Vector3.zero, new Vector3(4, 3, 13), new Vector3(-4, 18, -13) };
				var reference = new double3[objects.Count, samples.Length];
				for (var i = 0; i < objects.Count; i++) for (var j = 0; j < samples.Length; j++)
					reference[i, j] = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)objects[i].TransformPoint(samples[j]));
				var worst = 0.0;
				foreach (var stop in pack.Stops)
				{
					var origin = LocalTangentPlane.World.ToGeodetic(stop.Position.North, stop.Position.East);
					geo.SetOriginLongitudeLatitudeHeight(origin.LongitudeDegrees, origin.LatitudeDegrees, 0);
					world.Tick(0, null, stop.Id, true, 0);
					foreach (var aid in world.Aids)
					{
						var ll = LocalTangentPlane.World.ToGeodetic(aid.Source.Position.North, aid.Source.Position.East);
						Assert.That(pack.Geoid.TrySample(aid.Source.Position.North, aid.Source.Position.East, out var height), Is.True);
						var ecef = geo.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(ll.LongitudeDegrees, ll.LatitudeDegrees, height));
						var expected = geo.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
						var error = math.distance((double3)(float3)aid.transform.position, expected);
						worst = Math.Max(worst, error);
						Assert.That(error, Is.LessThan(.01), aid.Source.Id + " at " + stop.Id);
					}
					for (var i = 0; i < objects.Count; i++) for (var j = 0; j < samples.Length; j++)
					{
						var ecef = geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)objects[i].TransformPoint(samples[j]));
						var error = math.distance(reference[i, j], ecef);
						worst = Math.Max(worst, error);
						Assert.That(error, Is.LessThan(.01), objects[i].name + " sample " + j + " at " + stop.Id);
					}
				}
				TestContext.WriteLine("24 landing origins; 285 independent aid positions and " + objects.Count + " static object/lamp/signal frames x 3 points per origin; worst_m=" + worst.ToString("R", CultureInfo.InvariantCulture));
			}
			finally { UnityEngine.Object.DestroyImmediate(root); }
		}

		private sealed class DatumWater : IWaterSurface
		{
			private readonly HarborRuntimePack _pack;
			public DatumWater(HarborRuntimePack pack) { _pack = pack; }
			public double EllipsoidalHeight(double north, double east, double time)
			{
				if (!_pack.Geoid.TrySample(north, east, out var height)) throw new ArgumentException("Outside datum");
				return height;
			}
		}
	}
}
