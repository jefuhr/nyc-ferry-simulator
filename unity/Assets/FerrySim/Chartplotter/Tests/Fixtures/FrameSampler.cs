using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace FerrySim.Chartplotter.Tests
{
	/// <summary>
	/// Per-frame main-thread samples for the phase 2 budget (the coordinator's definition, 2026-10-29): over a moving
	/// 1080p steady-state run of at least 60 s, with a control run on the same route and settings, the display's cost is
	/// each frame's time less the control's median; p95 at or under 1.5 ms, p99 at or under 2.5 ms, and every frame over
	/// 5 ms explained by the control run's host stall (the same wall-clock second, or within one frame of it). Stall frames
	/// are marked, never removed. Frame times are <c>Time.unscaledDeltaTime</c>, each tagged with its wall-clock second.
	/// </summary>
	public sealed class FrameSampler
	{
		public const double BudgetP95 = 1.5;
		public const double BudgetP99 = 2.5;
		public const double StallMilliseconds = 5.0;

		public readonly List<double> Milliseconds = new List<double>();
		public readonly List<double> FrameMilliseconds = new List<double>();
		public readonly List<long> WallClockSeconds = new List<long>();
		private static ProfilerRecorder _mainThread;

		public string Name { get; }

		public FrameSampler(string name) => Name = name;

		/// <summary>The profiler's main-thread frame time counter, started once; invalid where the counter is unavailable.</summary>
		public static ProfilerRecorder MainThread
		{
			get
			{
				if (!_mainThread.Valid)
				{
					_mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
				}

				return _mainThread;
			}
		}

		/// <summary>True when the main-thread counter is what <see cref="Milliseconds"/> holds; false when it fell back to the frame delta.</summary>
		public static bool MainThreadAvailable => MainThread.Valid && MainThread.LastValue > 0;

		/// <summary>Records the frame just finished: the main thread's time (the frame delta when the counter is unavailable), and the frame delta beside it.</summary>
		public void Sample()
		{
			var frame = Time.unscaledDeltaTime * 1000.0;
			Milliseconds.Add(MainThreadAvailable ? MainThread.LastValue / 1e6 : frame);
			FrameMilliseconds.Add(frame);
			WallClockSeconds.Add(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		}

		public int Count => Milliseconds.Count;

		public double Percentile(double fraction)
		{
			if (Milliseconds.Count == 0) return 0;
			var sorted = new List<double>(Milliseconds);
			sorted.Sort();
			return sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(fraction * (sorted.Count - 1)))];
		}

		public double Median => Percentile(0.5);

		public double Max => Percentile(1.0);

		/// <summary>The wall-clock seconds of this run's frames over the stall threshold: the host stall, when it landed.</summary>
		public HashSet<long> StallSeconds()
		{
			var seconds = new HashSet<long>();
			for (var i = 0; i < Count; i++)
			{
				if (Milliseconds[i] > StallMilliseconds) seconds.Add(WallClockSeconds[i]);
			}

			return seconds;
		}

		/// <summary>
		/// Frames over the stall threshold, split into those explained by the control's stall (the same wall-clock second of
		/// the minute, or an adjacent frame to such a frame) and those not.
		/// </summary>
		public void Stalls(FrameSampler control, out int explained, out int unexplained, out List<int> unexplainedFrames)
		{
			var stallSecondsOfMinute = new HashSet<long>();
			foreach (var second in control.StallSeconds()) stallSecondsOfMinute.Add(second % 60);
			explained = 0;
			unexplained = 0;
			unexplainedFrames = new List<int>();
			for (var i = 0; i < Count; i++)
			{
				if (Milliseconds[i] <= StallMilliseconds) continue;
				var ok = false;
				for (var j = Math.Max(0, i - 1); j <= Math.Min(Count - 1, i + 1) && !ok; j++)
				{
					ok = stallSecondsOfMinute.Contains(WallClockSeconds[j] % 60);
				}

				if (ok) explained++;
				else { unexplained++; unexplainedFrames.Add(i); }
			}
		}

		/// <summary>The display's cost per frame: this run's frames less the control's median, as a sampler of its own.</summary>
		public FrameSampler LessControl(FrameSampler control)
		{
			var cost = new FrameSampler(Name + " less control median");
			var median = control.Median;
			for (var i = 0; i < Count; i++)
			{
				cost.Milliseconds.Add(Milliseconds[i] - median);
				cost.FrameMilliseconds.Add(i < FrameMilliseconds.Count ? FrameMilliseconds[i] : Milliseconds[i]);
				cost.WallClockSeconds.Add(WallClockSeconds[i]);
			}

			return cost;
		}

		public string Json()
		{
			var invariant = CultureInfo.InvariantCulture;
			var over = 0;
			foreach (var ms in Milliseconds) if (ms > StallMilliseconds) over++;
			var frames = new List<double>(FrameMilliseconds);
			frames.Sort();
			var frameText = frames.Count == 0 ? string.Empty : string.Format(invariant, ", \"frame_delta_p50\": {0:F3}, \"frame_delta_p95\": {1:F3}, \"frame_delta_p99\": {2:F3}",
				frames[frames.Count / 2], frames[Math.Min(frames.Count - 1, (int)Math.Floor(0.95 * (frames.Count - 1)))], frames[Math.Min(frames.Count - 1, (int)Math.Floor(0.99 * (frames.Count - 1)))]);
			return string.Format(invariant, "{{ \"frames\": {0}, \"p50\": {1:F3}, \"p95\": {2:F3}, \"p99\": {3:F3}, \"max\": {4:F3}, \"over_5ms\": {5}{7}, \"stall_seconds_utc\": [{6}] }}",
				Count, Percentile(0.5), Percentile(0.95), Percentile(0.99), Max, over, Join(StallSeconds()), frameText);
		}

		/// <summary>The gate's verdict and numbers for a run against its control, as JSON.</summary>
		public static string Verdict(FrameSampler with, FrameSampler control, FrameSampler tick, out bool pass)
		{
			var cost = with.LessControl(control);
			with.Stalls(control, out var explained, out var unexplained, out var unexplainedFrames);
			pass = cost.Percentile(0.95) <= BudgetP95 && cost.Percentile(0.99) <= BudgetP99 && unexplained == 0;
			var builder = new StringBuilder();
			builder.Append("{ \"pass\": ").Append(pass ? "true" : "false");
			builder.Append(", \"measure\": \"").Append(MainThreadAvailable ? "profiler Main Thread counter per frame (CPU main thread); frame_delta_* is Time.unscaledDeltaTime beside it" : "Time.unscaledDeltaTime (the Main Thread counter was unavailable)").Append("\"");
			builder.Append(", \"budget\": { \"p95_ms\": 1.5, \"p99_ms\": 2.5, \"stall_ms\": 5 }");
			builder.Append(", \"display_cost_ms\": ").Append(cost.Json());
			builder.Append(", \"frames_with_display_ms\": ").Append(with.Json());
			builder.Append(", \"control_ms\": ").Append(control.Json());
			if (tick != null) builder.Append(", \"display_tick_ms\": ").Append(tick.Json());
			builder.Append(", \"frames_over_5ms_explained_by_control_stall\": ").Append(explained);
			builder.Append(", \"frames_over_5ms_unexplained\": ").Append(unexplained);
			builder.Append(", \"unexplained_frame_indices\": [").Append(Join(unexplainedFrames)).Append("]");
			builder.Append(" }");
			return builder.ToString();
		}

		private static string Join<T>(IEnumerable<T> values)
		{
			var builder = new StringBuilder();
			foreach (var value in values)
			{
				if (builder.Length > 0) builder.Append(", ");
				builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
			}

			return builder.ToString();
		}
	}
}
