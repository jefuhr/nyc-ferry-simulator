using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Attribution for the budget: what each part of the display costs at 1080p, by varying one thing at a time over
	// short runs on the same synthetic harbor. Writes artifacts/chartplotter/frame-cost-variants.json; it asserts only
	// that the runs happened, so the numbers explain the gate's tail rather than gate anything themselves.
	public class FrameCostVariantsPlayModeTests
	{
		private const double VariantSeconds = 8.0;
		private PlotterRig _rig;

		/// <summary>Every profiler marker this editor offers, so the cost is attributed without guessing names; the frame's own counters are skipped.</summary>
		private static List<(string name, ProfilerRecorder recorder)> AllMarkers()
		{
			var handles = new List<ProfilerRecorderHandle>();
			ProfilerRecorderHandle.GetAvailable(handles);
			var result = new List<(string, ProfilerRecorder)>();
			foreach (var handle in handles)
			{
				var description = ProfilerRecorderHandle.GetDescription(handle);
				if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds || (description.Flags & MarkerFlags.Counter) != 0)
				{
					continue;
				}

				result.Add((description.Name, new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.StartImmediately)));
			}

			return result;
		}

		[UnityTest, Timeout(600000)]
		public IEnumerator Variants_At1080p_AttributeTheCost()
		{
			_rig = new PlotterRig(1920, 1080, new Rect(8, 8, 1264, 704));
			var previousVSync = QualitySettings.vSyncCount;
			var previousFrameRate = Application.targetFrameRate;
			QualitySettings.vSyncCount = 0;
			Application.targetFrameRate = -1;
			var report = new StringBuilder("{\n\t\"seconds_per_variant\": " + VariantSeconds.ToString(CultureInfo.InvariantCulture) + ",\n\t\"measure\": \"frame_ms is the profiler Main Thread counter when available (frame_delta_* beside it); markers are per-frame means of the named profiler samples on the main thread\",\n\t\"variants\": {\n");
			var first = true;
			try
			{
				foreach (var variant in new[] { "control (display disabled)", "north-up, no targets", "north-up, 120 targets", "course-up turning, 120 targets", "course-up turning, 120 targets, canvas held unrotated", "course-up turning, 120 targets, labels not repositioned", "north-up, 120 targets, 4 NM (no soundings)", "north-up, 120 targets, list changing every frame" })
				{
					_rig.Targets.Clear();
					_rig.Display.enabled = !variant.StartsWith("control");
					if (variant.Contains("120 targets"))
					{
						for (var t = 0; t < 120; t++)
						{
							_rig.Targets.Add("T" + t, 100000 + t, -1800 + t * 25, -1500 + (t * 37) % 3000, 1.5 * Math.Sin(t), 1.5 * Math.Cos(t), 20 + t % 9, 6, t % 5 != 0);
						}
					}

					if (_rig.Display.enabled)
					{
						_rig.View.SpanNauticalMiles = variant.Contains("4 NM") ? 4.0 : 2.0;
						_rig.View.Orientation = variant.StartsWith("course-up") ? ChartOrientation.CourseUp : ChartOrientation.NorthUp;
						_rig.View.DiagnosticRotateCanvas = !variant.Contains("canvas held");
						_rig.View.DiagnosticRepositionLabels = !variant.Contains("not repositioned");
					}

					var turning = variant.StartsWith("course-up");
					var fastClock = variant.Contains("list changing");
					var frames = new FrameSampler(variant);
					var tick = new FrameSampler(variant + " tick");
					var recorders = AllMarkers();
					var markerTotals = new double[recorders.Count];
					var markerMax = new double[recorders.Count];
					var frame = 0;
					for (; frame < 60; frame++)
					{
						_rig.Own.Place(frame * (fastClock ? 1.0 : 0.1), -1000 + frame * 0.003, 0, 30 + (turning ? frame * 0.1 : 0.0), 6);
						yield return null;
					}

					var started = Time.realtimeSinceStartupAsDouble;
					while (Time.realtimeSinceStartupAsDouble - started < VariantSeconds)
					{
						_rig.Own.Place(frame * (fastClock ? 1.0 : 0.1), -1000 + frame * 0.003, 0, 30 + (turning ? frame * 0.1 : 0.0), 6);
						frame++;
						yield return null;
						frames.Sample();
						tick.Milliseconds.Add(_rig.Display.enabled ? _rig.Display.LastRefreshMilliseconds : 0.0);
						tick.WallClockSeconds.Add(0);
						for (var m = 0; m < recorders.Count; m++)
						{
							if (!recorders[m].recorder.Valid) continue;
							var ms = recorders[m].recorder.LastValue / 1e6;
							markerTotals[m] += ms;
							if (ms > markerMax[m]) markerMax[m] = ms;
						}
					}

					var markerText = new StringBuilder();
					for (var m = 0; m < recorders.Count; m++)
					{
						var mean = markerTotals[m] / frames.Count;
						if (mean < 0.02 || mean > 1000.0) { recorders[m].recorder.Dispose(); continue; }
						markerText.Append(markerText.Length > 0 ? ", " : string.Empty).Append('"').Append(recorders[m].name.Replace("\\", "/").Replace("\"", "'")).Append("\": { \"mean_ms\": ").Append((markerTotals[m] / frames.Count).ToString("F3", CultureInfo.InvariantCulture)).Append(", \"max_ms\": ").Append(markerMax[m].ToString("F3", CultureInfo.InvariantCulture)).Append(" }");
						recorders[m].recorder.Dispose();
					}

					report.Append(first ? string.Empty : ",\n").Append("\t\t\"").Append(variant).Append("\": { \"frame_ms\": ").Append(frames.Json()).Append(", \"tick_ms\": ").Append(tick.Json())
						.Append(", \"targets_drawn\": ").Append(_rig.Display.enabled ? _rig.View.Targets.TargetsDrawn : 0).Append(", \"layer_vertices\": ").Append(_rig.Display.enabled ? _rig.View.Targets.LastVertices : 0)
						.Append(", \"sounding_labels\": ").Append(_rig.Display.enabled ? _rig.View.Canvas.SoundingsShown : 0).Append(", \"markers\": { ").Append(markerText).Append(" } }");
					first = false;
					Assert.That(frames.Count, Is.GreaterThan(100), variant);
				}
			}
			finally
			{
				QualitySettings.vSyncCount = previousVSync;
				Application.targetFrameRate = previousFrameRate;
			}

			report.Append("\n\t}\n}\n");
			Directory.CreateDirectory(PlotterRig.ArtifactFolder);
			File.WriteAllText(Path.Combine(PlotterRig.ArtifactFolder, "frame-cost-variants.json"), report.ToString());
			TestContext.WriteLine(report.ToString());
		}
	}
}
