using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Serialization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	/// <summary>
	/// The refresh profile of the plotter on a real route over the real chart (v0.3.1 hardening): own ship driven along
	/// the harness's Astoria and Soundview routes (tools/harbor-benchmark/routes.json, the same waypoints) at the
	/// shipping placement on a 1920×1080 panel, every frame's refresh time recorded with its static-regeneration
	/// attribution (box queries, landing, aid and sounding label placement, hiding the rest, the label layout, and the
	/// mesh generation at the repaint). Writes artifacts/plotter-cpa-controls/refresh-profile-&lt;route&gt;.csv and a JSON
	/// summary, so a change to the regeneration can be judged before and after on the same frames.
	/// </summary>
	public class RouteRefreshProfileTests
	{
		private const int Width = 1920, Height = 1080;

		/// <summary>Metres own ship advances per frame: about one regeneration every 50 frames at 1 NM, so a route gives a few dozen.</summary>
		private const double MetresPerFrame = 12.0;

		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));

		private static string Folder => Path.Combine(Repo, "artifacts", "plotter-cpa-controls");

		// routes.json (3dbcbf4): astoria, departure 87 (Pier 11) to 89, [north_m, east_m] waypoints; soundview, 87 to 112.
		private static readonly double[][] Astoria =
		{
			new[] { 390.0, 980.0 }, new[] { 655.389088667065, 1554.351536923543 }, new[] { 772.5633492278282, 2074.4010171879554 },
			new[] { 1050.0, 3000.0 }, new[] { 1509.0524077816262, 3622.523737710356 }, new[] { 3100.0, 4250.0 },
			new[] { 4500.0, 3920.0 }, new[] { 5400.0, 4330.0 }, new[] { 6435.617970237682, 4901.648870088835 },
			new[] { 7900.0, 6000.0 }, new[] { 8450.0, 6510.0 }, new[] { 8200.0, 6440.0 },
			new[] { 7813.072175775193, 6634.484538339417 },
		};

		private static readonly double[][] Soundview =
		{
			new[] { 390.0, 980.0 }, new[] { 655.389088667065, 1554.351536923543 }, new[] { 772.5633492278282, 2074.4010171879554 },
			new[] { 1050.0, 3000.0 }, new[] { 1509.0524077816262, 3622.523737710356 }, new[] { 3100.0, 4250.0 },
			new[] { 4500.0, 3920.0 }, new[] { 5400.0, 4330.0 }, new[] { 6435.617970237682, 4901.648870088835 },
			new[] { 7900.0, 6000.0 }, new[] { 8450.0, 6510.0 }, new[] { 8725.0, 6785.0 },
			new[] { 8780.0, 7240.0 }, new[] { 9168.072426306007, 7847.481775394962 }, new[] { 10300.0, 8760.0 },
			new[] { 11400.0, 9785.0 }, new[] { 11400.0, 9900.0 }, new[] { 11050.0, 10800.0 },
			new[] { 10650.0, 12200.0 }, new[] { 11150.0, 13400.0 }, new[] { 11444.44341614822, 13942.837735575748 },
		};

		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest, Explicit("Needs the copied harbor data; run by exact name for the refresh profile."), Category("HarborRealData"), Timeout(1800000)]
		public IEnumerator AstoriaRoute_NorthUp1NM_RefreshProfile()
		{
			yield return Profile("astoria-north-up-1nm", Astoria, ChartOrientation.NorthUp, 1.0);
		}

		[UnityTest, Explicit("Needs the copied harbor data; run by exact name for the refresh profile."), Category("HarborRealData"), Timeout(1800000)]
		public IEnumerator AstoriaRoute_CourseUp1NM_RefreshProfile()
		{
			yield return Profile("astoria-course-up-1nm", Astoria, ChartOrientation.CourseUp, 1.0);
		}

		[UnityTest, Explicit("Needs the copied harbor data; run by exact name for the refresh profile."), Category("HarborRealData"), Timeout(1800000)]
		public IEnumerator AstoriaRoute_NorthUpHalfNM_RefreshProfile()
		{
			yield return Profile("astoria-north-up-0.5nm", Astoria, ChartOrientation.NorthUp, 0.5);
		}

		[UnityTest, Explicit("Needs the copied harbor data; run by exact name for the refresh profile."), Category("HarborRealData"), Timeout(1800000)]
		public IEnumerator SoundviewRoute_NorthUp1NM_RefreshProfile()
		{
			yield return Profile("soundview-north-up-1nm", Soundview, ChartOrientation.NorthUp, 1.0);
		}

		private IEnumerator Profile(string label, double[][] route, ChartOrientation orientation, double span)
		{
			var harbor = Path.Combine(Repo, "data", "processed", "harbor");
			foreach (var folder in new[] { "runtime", "chart" })
			{
				if (!Directory.Exists(Path.Combine(harbor, folder))) Assert.Ignore("The harbor data is not copied: missing " + Path.Combine(harbor, folder) + ".");
			}

			Directory.CreateDirectory(Folder);
			var runtime = HarborRuntimePackJson.Load(Path.Combine(harbor, "runtime"));
			var chart = HarborChartPackJson.Load(Path.Combine(harbor, "chart"), runtime);
			// The shipping placement: 560×400 reference pixels on the 1280×720 design, drawn at 1.5× on a 1080p panel.
			_rig = new PlotterRig(Width, Height, new Rect(8, 8, 560, 400), syntheticChart: false);
			_rig.Display.Connect(_rig.Own, chart, runtime, _rig.Targets);
			// A few synthetic targets near the start so the list and the moving layer have work; they fall behind as the route goes.
			for (var t = 0; t < 12; t++)
			{
				_rig.Targets.Add("T" + t, 300000 + t, route[0][0] - 400 + t * 70, route[0][1] - 300 + t * 50, 1.5 * Math.Sin(t), 1.5 * Math.Cos(t), 20 + t % 5, 6, t % 3 != 0);
			}

			var view = _rig.View;
			view.SpanNauticalMiles = span;
			view.Orientation = orientation;
			var previousVSync = QualitySettings.vSyncCount;
			var previousFrameRate = Application.targetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			var invariant = CultureInfo.InvariantCulture;
			var csv = new StringBuilder("frame,north_m,east_m,heading_deg,refresh_ms,frame_ms,regen,placement_ms,query_ms,stop_labels_ms,aid_labels_ms,sounding_labels_ms,label_end_ms,label_layout_ms,mesh_ms,vertices,soundings,aid_labels,stop_labels,refused,refresh_alloc_samples,frame_alloc_samples,gc_collections,generation_ms,generating,commit,upload_ms,clock_second,sounding_text_changes\n");
			// The calibrated GC.Alloc recorder: a deliberate string allocation must give exactly one sample (the positive control).
			var recorder = Recorder.Get("GC.Alloc");
			recorder.enabled = false;
			recorder.FilterToCurrentThread();
			var chars = new char[16];
			recorder.enabled = true;
			var control = new string(chars);
			recorder.enabled = false;
			GC.KeepAlive(control);
			var calibration = recorder.sampleBlockCount;
			Assert.That(calibration, Is.EqualTo(1), "GC.Alloc sees the deliberate string allocation");
			var regenRefreshAllocations = 0;
			var regenFrameAllocations = 0;
			var steadyRefreshAllocations = 0;
			var collections = 0;
			var refresh = new List<double>();
			var frames = new List<double>();
			var regenRefresh = new List<double>();
			var regenFrame = new List<double>();
			var regenMesh = new List<double>();
			var regenPlacement = new List<double>();
			var steadyRefresh = new List<double>();
			var steadyFrame = new List<double>();
			try
			{
				_rig.Own.Place(0, route[0][0], route[0][1], 0, 6);
				for (var i = 0; i < 30; i++) yield return null;
				var renders = view.Canvas.RenderCount;
				var requests = view.Canvas.RequestCount;
				var frame = 0;
				var time = 0.0;
				for (var leg = 1; leg < route.Length; leg++)
				{
					var dn = route[leg][0] - route[leg - 1][0];
					var de = route[leg][1] - route[leg - 1][1];
					var length = Math.Sqrt(dn * dn + de * de);
					var heading = Math.Atan2(de, dn) * 180.0 / Math.PI;
					var steps = (int)Math.Ceiling(length / MetresPerFrame);
					for (var s = 0; s < steps; s++)
					{
						var f = Math.Min(1.0, (s + 1.0) / steps);
						time += 2.0; // 12 m per frame at 6 kn (3.09 m/s) is about 4 s of run; 2 s keeps the track and the list moving.
						_rig.Own.Place(time, route[leg - 1][0] + dn * f, route[leg - 1][1] + de * f, heading, 6);
						// The refresh is called here under the recorder (the display's own LateUpdate refresh then finds nothing new to do but costs its share too);
						// the whole frame (layout, text, the repaint's mesh generation) is recorded across the yield.
						var gcBefore = GC.CollectionCount(0);
						recorder.enabled = true;
						_rig.Display.Refresh();
						recorder.enabled = false;
						var refreshAllocations = recorder.sampleBlockCount;
						// Read the refresh's figures now: the display's own LateUpdate refresh (cheap, nothing new to do) overwrites them before the next frame.
						var r = view.LastRefreshMilliseconds;
						var regen = view.Canvas.RequestCount != requests;
						requests = view.Canvas.RequestCount;
						var placement = view.LastStaticPlacementMilliseconds;
						var queryMs = view.Canvas.LastQueryMilliseconds;
						var stopMs = view.Canvas.LastStopLabelMilliseconds;
						var aidMs = view.Canvas.LastAidLabelMilliseconds;
						var soundingMs = view.Canvas.LastSoundingLabelMilliseconds;
						var endMs = view.Canvas.LastLabelEndMilliseconds;
						var layoutMs = view.LastLabelLayoutMilliseconds;
						var generationMs = view.Canvas.LastGenerationMilliseconds;
						recorder.enabled = true;
						yield return null;
						recorder.enabled = false;
						var frameAllocations = recorder.sampleBlockCount;
						var gcDelta = GC.CollectionCount(0) - gcBefore;
						collections += gcDelta;
						frame++;
						var frameMs = Time.unscaledDeltaTime * 1000.0;
						var meshed = view.Canvas.RenderCount != renders;
						renders = view.Canvas.RenderCount;
						var clockSecond = DateTime.UtcNow.TimeOfDay.TotalSeconds % 60.0;
						refresh.Add(r);
						frames.Add(frameMs);
						if (regen)
						{
							regenRefresh.Add(r);
							regenFrame.Add(frameMs);
							regenPlacement.Add(placement);
							regenRefreshAllocations += refreshAllocations;
							regenFrameAllocations += frameAllocations;
						}
						else
						{
							steadyRefresh.Add(r);
							steadyFrame.Add(frameMs);
							steadyRefreshAllocations += refreshAllocations;
						}

						if (meshed) regenMesh.Add(view.Canvas.LastRenderMilliseconds);
						csv.Append(frame).Append(',').Append(_rig.Own.State.North.ToString("F1", invariant)).Append(',').Append(_rig.Own.State.East.ToString("F1", invariant)).Append(',')
							.Append(heading.ToString("F1", invariant)).Append(',').Append(r.ToString("F3", invariant)).Append(',').Append(frameMs.ToString("F2", invariant)).Append(',')
							.Append(regen ? 1 : 0).Append(',')
							.Append((regen ? placement : 0.0).ToString("F3", invariant)).Append(',')
							.Append((regen ? queryMs : 0.0).ToString("F3", invariant)).Append(',')
							.Append((regen ? stopMs : 0.0).ToString("F3", invariant)).Append(',')
							.Append((regen ? aidMs : 0.0).ToString("F3", invariant)).Append(',')
							.Append((regen ? soundingMs : 0.0).ToString("F3", invariant)).Append(',')
							.Append((regen ? endMs : 0.0).ToString("F3", invariant)).Append(',')
							.Append((regen ? layoutMs : 0.0).ToString("F3", invariant)).Append(',')
							.Append((meshed ? view.Canvas.LastRenderMilliseconds : 0.0).ToString("F3", invariant)).Append(',')
							.Append(meshed ? view.Canvas.LastVertices : 0).Append(',')
							.Append(view.Canvas.SoundingsShown).Append(',').Append(view.Canvas.AidLabelsShown).Append(',').Append(view.Canvas.StopLabelsShown).Append(',').Append(view.Canvas.LabelsRefused).Append(',')
							.Append(refreshAllocations).Append(',').Append(frameAllocations).Append(',').Append(gcDelta).Append(',')
							.Append(generationMs.ToString("F3", invariant)).Append(',').Append(view.Canvas.Generating ? 1 : 0).Append(',').Append(meshed ? 1 : 0).Append(',')
							.Append((meshed ? view.Canvas.LastUploadMilliseconds : 0.0).ToString("F3", invariant)).Append(',').Append(clockSecond.ToString("F1", invariant)).Append(',')
							.Append(meshed ? view.Canvas.LastSoundingTextChanges : 0).Append('\n');
					}
				}
			}
			finally
			{
				recorder.CollectFromAllThreads();
				QualitySettings.vSyncCount = previousVSync;
				Application.targetFrameRate = previousFrameRate;
			}

			File.WriteAllText(Path.Combine(Folder, "refresh-profile-" + label + ".csv"), csv.ToString());
			var summary = "{\n" +
				"\t\"label\": \"" + label + "\",\n" +
				"\t\"last_generation_vertices_by_pass\": \"" + view.Canvas.LastPhaseVertices + "\",\n" +
				"\t\"last_generation_vertices\": " + view.Canvas.LastVertices + ",\n" +
				"\t\"staged_bytes\": " + view.Canvas.StagedBytes + ",\n" +
				"\t\"frames\": " + refresh.Count + ",\n" +
				"\t\"regenerations\": " + regenRefresh.Count + ",\n" +
				"\t\"refresh_ms\": " + Stats(refresh) + ",\n" +
				"\t\"frame_ms\": " + Stats(frames) + ",\n" +
				"\t\"regeneration_refresh_ms\": " + Stats(regenRefresh) + ",\n" +
				"\t\"regeneration_frame_ms\": " + Stats(regenFrame) + ",\n" +
				"\t\"regeneration_placement_ms\": " + Stats(regenPlacement) + ",\n" +
				"\t\"regeneration_mesh_ms\": " + Stats(regenMesh) + ",\n" +
				"\t\"steady_refresh_ms\": " + Stats(steadyRefresh) + ",\n" +
				"\t\"steady_frame_ms\": " + Stats(steadyFrame) + ",\n" +
				"\t\"gc_alloc\": { \"positive_control\": " + calibration + ", \"regeneration_refresh_samples\": " + regenRefreshAllocations + ", \"regeneration_frame_samples\": " + regenFrameAllocations + ", \"steady_refresh_samples\": " + steadyRefreshAllocations + ", \"gen0_collections\": " + collections + " }\n" +
				"}\n";
			File.WriteAllText(Path.Combine(Folder, "refresh-profile-" + label + ".json"), summary);
			TestContext.WriteLine(summary);
			Assert.That(regenRefresh.Count, Is.GreaterThan(3), "the route regenerated the static chart a few times");
		}

		private static string Stats(List<double> values)
		{
			if (values.Count == 0) return "{ \"count\": 0 }";
			var sorted = new List<double>(values);
			sorted.Sort();
			var invariant = CultureInfo.InvariantCulture;
			double sum = 0;
			foreach (var v in sorted) sum += v;
			return "{ \"count\": " + sorted.Count +
				", \"mean\": " + (sum / sorted.Count).ToString("F3", invariant) +
				", \"p50\": " + Percentile(sorted, 0.5).ToString("F3", invariant) +
				", \"p90\": " + Percentile(sorted, 0.9).ToString("F3", invariant) +
				", \"p99\": " + Percentile(sorted, 0.99).ToString("F3", invariant) +
				", \"max\": " + sorted[sorted.Count - 1].ToString("F3", invariant) + " }";
		}

		private static double Percentile(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * (sorted.Count - 1)))];
	}
}
