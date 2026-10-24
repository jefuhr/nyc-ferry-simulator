using System;
using System.Collections.Generic;

namespace FerrySim.UI
{
	/// <summary>A closed interval of data values on one plot axis.</summary>
	public readonly struct AxisRange
	{
		public AxisRange(double min, double max)
		{
			Min = min;
			Max = max;
		}

		public double Min { get; }

		public double Max { get; }

		public double Span => Max - Min;

		public override string ToString() => $"[{Min}, {Max}]";
	}

	/// <summary>A point of a plotted series in data units (time or metres on x).</summary>
	public readonly struct PlotPoint
	{
		public PlotPoint(double x, double y)
		{
			X = x;
			Y = y;
		}

		public double X { get; }

		public double Y { get; }
	}

	/// <summary>Axis ranges, tick steps and data-to-pixel mapping for the debrief plots.</summary>
	public static class PlotScale
	{
		/// <summary>Smallest span, in data units, a track plot shows when the vessel never moved.</summary>
		public const double MinimumTrackSpan = 10.0;

		/// <summary>A 1, 2 or 5 times a power of ten step giving about <paramref name="targetTicks"/> intervals.</summary>
		public static double NiceStep(double span, int targetTicks)
		{
			if (!(span > 0.0) || double.IsInfinity(span) || targetTicks < 1)
			{
				return 1.0;
			}

			var raw = span / targetTicks;
			var magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(raw)));
			var normalized = raw / magnitude;
			var nice = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
			return nice * magnitude;
		}

		/// <summary>
		/// The data range widened outward to whole steps. A flat series is widened around its value so it
		/// still gets an axis; no data at all gives [0, 1].
		/// </summary>
		public static AxisRange NiceRange(double min, double max, int targetTicks, out double step)
		{
			if (!IsFinite(min) || !IsFinite(max))
			{
				min = 0.0;
				max = 1.0;
			}

			if (min > max)
			{
				var swap = min;
				min = max;
				max = swap;
			}

			if (max - min <= 1e-12 * Math.Max(1.0, Math.Abs(max)))
			{
				var pad = Math.Abs(min) > 0.0 ? Math.Abs(min) * 0.1 : 1.0;
				min -= pad;
				max += pad;
			}

			step = NiceStep(max - min, targetTicks);
			return new AxisRange(Math.Floor(min / step + 1e-9) * step, Math.Ceiling(max / step - 1e-9) * step);
		}

		/// <summary>The finite minimum and maximum over several series; NaN bounds when there are no finite values.</summary>
		public static AxisRange DataRange(IEnumerable<IReadOnlyList<double>> series)
		{
			var min = double.PositiveInfinity;
			var max = double.NegativeInfinity;
			foreach (var values in series)
			{
				if (values == null)
				{
					continue;
				}

				for (var i = 0; i < values.Count; i++)
				{
					var value = values[i];
					if (IsFinite(value))
					{
						min = Math.Min(min, value);
						max = Math.Max(max, value);
					}
				}
			}

			return min <= max ? new AxisRange(min, max) : new AxisRange(double.NaN, double.NaN);
		}

		/// <summary>Multiples of <paramref name="step"/> inside the range, rounded so 0.1 * 3 prints as 0.3.</summary>
		public static IReadOnlyList<double> Ticks(AxisRange range, double step)
		{
			var ticks = new List<double>();
			if (!(step > 0.0) || !IsFinite(range.Min) || !IsFinite(range.Max) || range.Span / step > 1000.0)
			{
				return ticks;
			}

			var decimals = Math.Min(15, TickDecimals(step) + 2);
			var first = Math.Ceiling(range.Min / step - 1e-9);
			for (var k = first; k * step <= range.Max + step * 1e-9; k++)
			{
				var value = Math.Round(k * step, decimals);
				ticks.Add(value == 0.0 ? 0.0 : value);
			}

			return ticks;
		}

		/// <summary>Decimal places a tick label needs for this step.</summary>
		public static int TickDecimals(double step) =>
			step > 0.0 && IsFinite(step) ? Math.Max(0, -(int)Math.Floor(Math.Log10(step) + 1e-9)) : 0;

		/// <summary>Linear map from data to pixels. Pass a larger <paramref name="pixelAtMin"/> to flip a y axis.</summary>
		public static float ToPixel(double value, AxisRange range, float pixelAtMin, float pixelAtMax)
		{
			if (!(range.Span > 0.0))
			{
				return (pixelAtMin + pixelAtMax) * 0.5f;
			}

			return (float)(pixelAtMin + (value - range.Min) / range.Span * (pixelAtMax - pixelAtMin));
		}

		public static double FromPixel(float pixel, AxisRange range, float pixelAtMin, float pixelAtMax)
		{
			var pixels = pixelAtMax - pixelAtMin;
			if (Math.Abs(pixels) < 1e-6f)
			{
				return range.Min;
			}

			return range.Min + (pixel - pixelAtMin) / (double)pixels * range.Span;
		}

		/// <summary>
		/// Widens the shorter axis so a metre is the same number of pixels both ways (a track must not look
		/// stretched), keeping the data centred.
		/// </summary>
		public static void EqualAspect(AxisRange x, AxisRange y, float width, float height, out AxisRange equalX, out AxisRange equalY)
		{
			width = Math.Max(1f, width);
			height = Math.Max(1f, height);
			var perPixel = Math.Max(x.Span / width, y.Span / height);
			if (!(perPixel > 0.0) || double.IsInfinity(perPixel))
			{
				perPixel = MinimumTrackSpan / Math.Min(width, height);
			}

			var centreX = IsFinite(x.Min + x.Max) ? (x.Min + x.Max) * 0.5 : 0.0;
			var centreY = IsFinite(y.Min + y.Max) ? (y.Min + y.Max) * 0.5 : 0.0;
			equalX = new AxisRange(centreX - perPixel * width * 0.5, centreX + perPixel * width * 0.5);
			equalY = new AxisRange(centreY - perPixel * height * 0.5, centreY + perPixel * height * 0.5);
		}

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}

	/// <summary>Turns long recordings into a bounded number of plotted points without losing peaks.</summary>
	public static class PlotDecimation
	{
		/// <summary>
		/// Splits a heading or course series (degrees) into runs that never cross north, so the plot breaks
		/// the line at the wrap instead of drawing it across the whole axis. Runs are [start, end).
		/// </summary>
		public static List<(int start, int end)> CompassRuns(IReadOnlyList<double> degrees, int start, int end)
		{
			var runs = new List<(int, int)>();
			if (end <= start)
			{
				return runs;
			}

			var runStart = start;
			for (var i = start + 1; i < end; i++)
			{
				if (Math.Abs(degrees[i] - degrees[i - 1]) > 180.0)
				{
					runs.Add((runStart, i));
					runStart = i;
				}
			}

			runs.Add((runStart, end));
			return runs;
		}

		/// <summary>
		/// Appends at most 2 * <paramref name="buckets"/> + 2 points for rows [start, end): the first and
		/// last rows, and the minimum and maximum of each x bucket in time order. Short series pass
		/// through unchanged.
		/// </summary>
		public static void Decimate(
			IReadOnlyList<double> x, IReadOnlyList<double> y, int start, int end, double xMin, double xMax, int buckets, List<PlotPoint> output)
		{
			var count = end - start;
			if (count <= 0)
			{
				return;
			}

			buckets = Math.Max(1, buckets);
			if (count <= 2 * buckets + 2 || !(xMax > xMin))
			{
				for (var i = start; i < end; i++)
				{
					output.Add(new PlotPoint(x[i], y[i]));
				}

				return;
			}

			output.Add(new PlotPoint(x[start], y[start]));
			var bucket = -1;
			var minIndex = -1;
			var maxIndex = -1;
			for (var i = start + 1; i < end - 1; i++)
			{
				var b = (int)((x[i] - xMin) / (xMax - xMin) * buckets);
				b = Math.Max(0, Math.Min(buckets - 1, b));
				if (b != bucket)
				{
					Flush(x, y, minIndex, maxIndex, output);
					bucket = b;
					minIndex = i;
					maxIndex = i;
					continue;
				}

				if (y[i] < y[minIndex])
				{
					minIndex = i;
				}

				if (y[i] > y[maxIndex])
				{
					maxIndex = i;
				}
			}

			Flush(x, y, minIndex, maxIndex, output);
			output.Add(new PlotPoint(x[end - 1], y[end - 1]));
		}

		private static void Flush(IReadOnlyList<double> x, IReadOnlyList<double> y, int minIndex, int maxIndex, List<PlotPoint> output)
		{
			if (minIndex < 0)
			{
				return;
			}

			var firstIndex = Math.Min(minIndex, maxIndex);
			var secondIndex = Math.Max(minIndex, maxIndex);
			output.Add(new PlotPoint(x[firstIndex], y[firstIndex]));
			if (secondIndex != firstIndex)
			{
				output.Add(new PlotPoint(x[secondIndex], y[secondIndex]));
			}
		}
	}
}
