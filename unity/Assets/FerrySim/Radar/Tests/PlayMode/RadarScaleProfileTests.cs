using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Radar;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Radar.Tests.PlayMode
{
	public sealed class RadarScaleProfileTests
	{
		private Scene _scene;
		[UnityTearDown] public IEnumerator Unload() { if (_scene.IsValid()) yield return SceneManager.UnloadSceneAsync(_scene); }
		[UnityTest, Explicit("Real resident pack all-scale performance; select exact method in a fresh process.")]
		public IEnumerator AllScalesWithMovingObserverAndTraffic()
		{
			var repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../..")); var harbor = Path.Combine(repo, "data/processed/harbor"); var folder = Path.Combine(repo, "artifacts/radar");
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime")); var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			var before = GC.GetTotalMemory(true); var geometry = RadarScene.FromPacks(chart, runtime); var managed = GC.GetTotalMemory(true) - before;
			var engine = new RadarEngine(geometry); var traffic = new TrafficRun(TrafficPackJson.Load(Path.Combine(harbor, "traffic/2025-07-16")), DateTimeOffset.Parse("2025-07-16T16:18:00Z", CultureInfo.InvariantCulture), TrafficLeaveOut.Off, 0);
			var targets = new RadarTarget[traffic.Pack.Vessels.Count];
			_scene = SceneManager.CreateScene("Radar scale performance additive"); var host = new GameObject("Scale evidence"); SceneManager.MoveGameObjectToScene(host, _scene);
			var target = new RenderTexture(1920, 1080, 0); target.Create(); var panel = WheelhousePanelSettings.Create(target, Color.black);
			var doc = host.AddComponent<UIDocument>(); doc.panelSettings = panel; var view = new RadarView(engine); doc.rootVisualElement.Add(view);
			var names = new[] { "PreLateUpdate.UIElementsUpdatePanels", "PostLateUpdate.UIElementsRepaintPanels", "PostLateUpdate.UIElementsRenderBatchModeOffscreen" };
			var recorders = new Recorder[names.Length]; for (var i = 0; i < names.Length; i++) { recorders[i] = Recorder.Get(names[i]); recorders[i].FilterToCurrentThread(); recorders[i].enabled = true; }
			var samples = new double[512]; var csv = new StringBuilder("range_nm,p50_tick_plus_ui_ms,p95_tick_plus_ui_ms,max_tick_plus_ui_ms\n");
			var failures = new StringBuilder();
			try
			{
				for (var scale = 0; scale < RadarScale.Count; scale++)
				{
					engine.SetRange(scale); engine.Reset();
					for (var i = 0; i < 572; i++)
					{
						var time = i / 60.0; var frame = new RadarFrame(351.061 + time * 4, 848.434 + time * 2, .5, .5, 9, 5);
						var start = Stopwatch.GetTimestamp(); var count = RadarController.SampleTraffic(traffic, time, targets); engine.Advance(time, frame, targets, count); view.Refresh(frame.Heading);
						var tick = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
						yield return null;
						if (i >= 60) { for (var r = 0; r < recorders.Length; r++) tick += recorders[r].elapsedNanoseconds / 1000000.0; samples[i - 60] = tick; }
					}
					Array.Sort(samples); csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:F6},{2:F6},{3:F6}\n", engine.Scale.RangeMetres / 1852, samples[256], samples[486], samples[511]);
					if (samples[486] > 1.5) failures.Append(engine.Scale.RangeMetres / 1852).Append(" nm exceeds 1.5 ms p95; ");
				}
				File.WriteAllText(Path.Combine(folder, "all-scales.csv"), csv.ToString());
				File.WriteAllText(Path.Combine(folder, "radar-index-memory.txt"), "Derived radar index managed delta after GC=" + managed + " bytes; cells=" + geometry.SpatialCellCount + "; features=" + geometry.FeatureCount + ".\nApproximate editor managed measurement, excluding the shared chart pack and not process peak memory.\nAll-scale profile uses a moving Pier 11 observer with fixed test tide +0.5 m and wind 5 m/s, real geometry and 2025-07-16 12:18 traffic.\n");
				File.WriteAllText(Path.Combine(folder, "all-scales-scope.txt"), "Coordinator: all-seven-ranges check is stress evidence, separate from the steady-state acceptance gate.\n" + (failures.Length == 0 ? "All scale p95 measurements are at or below 1.5 ms.\n" : failures.ToString() + "\n"));
			}
			finally
			{
				for (var i = 0; i < recorders.Length; i++) { recorders[i].enabled = false; recorders[i].CollectFromAllThreads(); }
				view.Dispose(); Object.Destroy(host); Object.Destroy(panel); target.Release(); Object.Destroy(target);
			}
		}
	}
}
