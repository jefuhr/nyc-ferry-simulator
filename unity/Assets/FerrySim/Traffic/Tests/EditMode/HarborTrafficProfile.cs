using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;

namespace FerrySim.Traffic.Tests
{
	public partial class HarborTrafficTests
	{
		[Test, Category("HarborRealData")]
		public void LargestRoster_BusiestHarborMinute_Profile()
		{
			var root = SimPaths.Resolve("data/processed");
			var index = Path.Combine(root, "harbor", "index.json");
			if (!File.Exists(index)) Assert.Ignore("Optional real harbor index is not present; synthetic fixtures still run.");
			var rectangles = new List<TrafficBounds>();
			foreach (var tile in JObject.Parse(File.ReadAllText(index))["tiles"])
			{
				if (!(bool)tile["active"]) continue;
				var b = tile["bounds_local_m"];
				rectangles.Add(new TrafficBounds((double)b[0], (double)b[2], (double)b[1], (double)b[3]));
			}
			var area = new TrafficTileArea(rectangles.ToArray());
			string largest = null;
			var roster = 0;
			foreach (var manifest in Directory.GetFiles(Path.Combine(root, "harbor", "traffic"), "manifest.json", SearchOption.AllDirectories))
			{
				var count = (int)JObject.Parse(File.ReadAllText(manifest))["counts"]["vessels"];
				if (count > roster || count == roster && string.CompareOrdinal(manifest, largest) < 0) { largest = manifest; roster = count; }
			}
			var pack = TrafficPackJson.Load(Path.GetDirectoryName(largest));
			var replay = new TrafficReplay(pack);
			var busiest = 0; var most = -1;
			for (var minute = 0; minute < 1440; minute++)
			{
				var count = 0;
				for (var v = 0; v < pack.Vessels.Count; v++)
					if (replay.TrySample(v, minute * 60, out var s) && area.Contains(s.North, s.East)) count++;
				if (count > most) { busiest = minute; most = count; }
			}
			_runner.TrafficPacks = new TrafficPacks(root);
			_runner.EnvironmentPacks = new EnvironmentPacks(root);
			var scenario = TrafficTestPacks.Scenario(pack.Date);
			scenario.scenarioStartUtc = pack.EpochUtc.AddSeconds(busiest * 60).ToString("O");
			scenario.environment.pack = "lower-east-river/" + pack.Date;
			_runner.Initialize(scenario, new ConstantControls(ControlInputs.Neutral));
			var managedBefore = GC.GetTotalMemory(true);
			var memoryBefore = Profiler.GetTotalAllocatedMemoryLong();
			var buildWatch = Stopwatch.StartNew();
			_view.ConnectHarbor(_runner, _geo, _water, area, _camera);
			buildWatch.Stop();
			var managedDelta = GC.GetTotalMemory(true) - managedBefore;
			var memoryDelta = Profiler.GetTotalAllocatedMemoryLong() - memoryBefore;
			var result = new JObject
			{
				["scope"] = "Unity editor; synthetic spatial water; real verified AIS; no terrain/skyline rendering",
				["strategy"] = "build all MayEnter-eligible hull/light hierarchies",
				["day"] = pack.Date, ["digest"] = pack.Digest, ["roster"] = roster,
				["busiest_minute_local"] = busiest, ["present_in_tile_union"] = most,
				["built_hulls"] = _view.Hulls.Count, ["build_ms"] = buildWatch.Elapsed.TotalMilliseconds,
				["managed_build_delta_bytes"] = managedDelta, ["unity_allocated_build_delta_bytes"] = memoryDelta,
				["lattice_bytes"] = _view.LatticeBytes, ["render_meshes"] = _view.MeshCount,
				["render_materials"] = _view.MaterialCount,
			};
			var sun = _world.AddComponent<SunController>();
			sun.SetUtc(new DateTime(2025, 7, 16, 6, 0, 0, DateTimeKind.Utc)); sun.Apply();
			_view.Sun = sun;
			result["day_4km"] = MeasurePlacement(false, 4000);
			result["night_4km"] = MeasurePlacement(true, 4000);
			result["fog_4km"] = MeasurePlacement(false, 4000, 200);
			result["all_eligible_shown_stress"] = MeasurePlacement(true, 100000);
			var monitor = _runner.Traffic.StartRun(HullOutline.FromContact(VesselParametersJson.Load(SimPaths.Resolve("vessels/t-class.json"))), TrafficAlarmLimits.Default);
			var samples = new double[1000];
			var watch = new Stopwatch();
			var own = _runner.State;
			for (var i = 0; i < 100; i++) monitor.Update(i + 1, own, null);
			var monitorAllocations = AllocationSamples(() =>
			{
				for (var i = 0; i < samples.Length; i++)
				{
					watch.Restart(); monitor.Update(i + 101, own, null); watch.Stop();
					samples[i] = watch.Elapsed.TotalMilliseconds;
				}
			}, out var calibration);
			Array.Sort(samples);
			result["core_monitor_separate_fixture"] = new JObject { ["p95_ms"] = samples[949], ["gc_alloc_samples"] = monitorAllocations, ["positive_control_samples"] = calibration, ["steps"] = 1000 };
			result["allocation_instrument"] = "GC.Alloc recorder, current thread; a deliberate string allocation must give one sample; bytes unavailable in editor Mono";
			var folder = SimPaths.Resolve("artifacts/harbor-traffic"); Directory.CreateDirectory(folder);
			File.WriteAllText(Path.Combine(folder, "profile.json"), result.ToString() + "\n");
			TestContext.WriteLine(result.ToString());
			Assert.That((double)result["day_4km"]["p95_ms"], Is.LessThanOrEqualTo(1));
			Assert.That((double)result["night_4km"]["p95_ms"], Is.LessThanOrEqualTo(1));
			foreach (var mode in new[] { "day_4km", "night_4km", "fog_4km", "all_eligible_shown_stress", "core_monitor_separate_fixture" })
				Assert.That((int)result[mode]["gc_alloc_samples"], Is.Zero, mode);
		}

		private JObject MeasurePlacement(bool lights, float range, double visibility = 0)
		{
			_runner.ResetRun();
			_view.Sun.SetUtc(new DateTime(2025, 7, 16, lights ? 6 : 16, 0, 0, DateTimeKind.Utc));
			_view.Sun.VisibilityMetres = visibility;
			_view.Sun.Apply();
			_view.NavigationLightsEnabled = true;
			_view.ViewRange = range;
			for (var i = 0; i < 30; i++) _view.Place();
			var samples = new double[1000];
			var churn = 0; var missing = 0; var activations = 0;
			var count = AllocationSamples(() =>
			{
				for (var i = 0; i < samples.Length; i++)
				{
					_runner.AdvanceFrame(1.0 / 60);
					_view.Place();
					samples[i] = _view.LastMilliseconds;
					churn += _view.PoolChurn; missing += _view.MissingTargets; activations += _view.ActivationChanges;
				}
			}, out var calibration);
			Array.Sort(samples);
			return new JObject { ["p50_ms"] = samples[499], ["p95_ms"] = samples[949], ["max_ms"] = samples[999],
				["frames"] = samples.Length, ["gc_alloc_samples"] = count, ["positive_control_samples"] = calibration, ["pool_churn"] = churn, ["activation_changes"] = activations, ["missing_targets"] = missing,
				["active"] = _view.ActiveCount, ["shown_hulls"] = _view.ShownCount, ["lights_drawn"] = _view.LightsDrawn };
		}

		private static int AllocationSamples(Action action, out int calibration)
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
	}
}
