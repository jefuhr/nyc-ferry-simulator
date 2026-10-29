using System;
using System.Collections;
using System.IO;
using CesiumForUnity;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Profiling;

namespace FerrySim.World.HarborNavigation.Tests.PlayMode
{
	public class NavigationPlayTests
	{
		[UnityTest]
		public IEnumerator RegistrationAfterStartTracksNightDayAndFog()
		{
			var root = new GameObject("late navigation");
			var sun = root.AddComponent<SunController>();
			var atmosphere = root.AddComponent<Atmosphere>(); atmosphere.Configure(sun, null);
			yield return null;
			var point = SignalLight.Create(root.transform, "nominal bridge", Vector3.zero, Quaternion.identity, SignalColour.Green, LightSector.AllRound, 10);
			var aids = new AidLights(); atmosphere.RegisterNavigationBatch(aids, new[] { point });
			sun.SetUtc(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc)); sun.Apply(); atmosphere.RefreshNavigation(3);
			Assert.That(point.On, Is.True);
			sun.SetUtc(new DateTime(2026, 6, 15, 16, 0, 0, DateTimeKind.Utc)); sun.Apply(); atmosphere.RefreshNavigation(3);
			Assert.That(point.On, Is.False);
			sun.VisibilityMetres = 500; sun.Apply(); atmosphere.RefreshNavigation(3);
			Assert.That(point.On, Is.True); Assert.That(RenderSettings.fog, Is.True);
			// Disposing an old batch must not remove its replacement.
			atmosphere.UnregisterNavigationBatch(new AidLights()); Assert.That(atmosphere.Aids, Is.SameAs(aids));
			UnityEngine.Object.Destroy(root); yield return null;
		}

		[Test, Explicit("Local real-export acceptance; requires copied harbor runtime outputs.")]
		public void RealPackCountsEvidenceAndAllocation()
		{
			var path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../data/processed/harbor/runtime"));
			var pack = HarborRuntimePackJson.Load(path);
			var root = new GameObject("real navigation");
			try
			{
				var geo = root.AddComponent<CesiumGeoreference>(); geo.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
				var camera = new GameObject("allocation camera").AddComponent<Camera>(); camera.transform.SetParent(root.transform, false);
				var sun = root.AddComponent<SunController>();
				var atmosphere = root.AddComponent<Atmosphere>(); atmosphere.Configure(sun, null);
				var world = root.AddComponent<HarborNavigationWorld>(); world.Build(pack, geo, new DatumWater(pack), atmosphere);
				Assert.That(world.Barges.Count, Is.EqualTo(28)); Assert.That(world.Labels.Count, Is.EqualTo(24));
				Assert.That(world.Aids.Count, Is.EqualTo(285)); Assert.That(world.BoundaryAidsOmitted, Is.EqualTo(2));
				Assert.That(world.EstimatedCharacters, Is.EqualTo(5)); Assert.That(world.Spans.Count, Is.EqualTo(193));
				Assert.That(world.Queries, Is.SameAs(pack.Queries)); Assert.That(root.GetComponentsInChildren<Collider>(true), Is.Empty);
				var report = new System.Text.StringBuilder("barges=28 labels=24 aids=285 spans=193 estimated_characters=5\ninstrument=GC.Alloc current thread; bytes unavailable in editor Mono\n");
				foreach (var mode in new[] { "day", "night", "fog" })
				{
					sun.SetUtc(new DateTime(2026, 6, 15, mode == "night" ? 4 : 16, 0, 0, DateTimeKind.Utc));
					sun.VisibilityMetres = mode == "fog" ? 200 : 0; sun.Apply();
					void Tick(int i)
					{
						camera.transform.position = world.Labels[0].transform.position + Vector3.right * (i % 2 == 0 ? 100 : 20000);
						world.Tick(i * .037, camera, i % 2 == 0 ? "87" : "137", sun.IsNight, sun.VisibilityMetres);
						atmosphere.RefreshNavigation(i * .037);
					}
					for (var i = 0; i < 30; i++) Tick(i);
					var count = MeasureAllocations(() => { for (var i = 0; i < 100; i++) Tick(i); }, out var calibration);
					report.Append(mode).Append(": positive_control_samples=").Append(calibration).Append(" gc_alloc_samples=").Append(count).Append('\n');
					TestContext.WriteLine(mode + ": GC.Alloc positive control=" + calibration + "; measured samples=" + count);
					Assert.That(count, Is.Zero, mode + ": changing tide, labels, camera visibility, aids and bridge lights");
				}
				var artifact = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/harbor-review-fixes")); Directory.CreateDirectory(artifact);
				File.WriteAllText(Path.Combine(artifact, "navigation-real-pack.txt"), report + "omitted=" + string.Join(",", world.OmittedSpanIds) + "\nedge_anchored=" + string.Join(",", world.EdgeAnchoredSpanIds));
			}
			finally { UnityEngine.Object.DestroyImmediate(root); }
		}

		private static int MeasureAllocations(Action action, out int calibration)
		{
			var chars = new char[16];
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true;
				var control = new string(chars);
				recorder.enabled = false;
				calibration = recorder.sampleBlockCount;
				GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1), "GC.Alloc must see the deliberate string allocation");
				recorder.enabled = true;
				action();
				recorder.enabled = false;
				return recorder.sampleBlockCount;
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}
		private sealed class DatumWater : IWaterSurface
		{
			private readonly HarborRuntimePack _pack;
			public DatumWater(HarborRuntimePack pack) { _pack = pack; }
			public double EllipsoidalHeight(double north, double east, double time)
			{
				if (!_pack.Geoid.TrySample(north, east, out var height)) throw new ArgumentException("Outside datum");
				return height + Math.Sin(time / 10) * .5;
			}
		}
	}
}
