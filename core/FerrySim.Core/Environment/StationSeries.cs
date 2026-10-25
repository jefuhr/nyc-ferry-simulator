using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Env
{
	/// <summary>The three kinds of station series in an environment pack.</summary>
	public enum EnvironmentSeries
	{
		Current = 0,
		WaterLevel = 1,
		Wind = 2,
	}

	/// <summary>
	/// How a station's series is read between and beyond its samples (ferrysim-env/1). Linear across gaps of up to
	/// <see cref="MaximumInterpolatedGap"/>; across a longer gap, the nearer sample (the earlier one at the midpoint);
	/// before the first sample and after the last, that sample. Times are seconds since the pack's epoch.
	/// </summary>
	public static class SeriesTime
	{
		/// <summary>Longest gap between two samples that is interpolated, s. A longer gap holds the nearer sample.</summary>
		public const double MaximumInterpolatedGap = 1800.0;

		/// <summary>
		/// The samples a time reads: the value is v[lower] + fraction (v[upper] − v[lower]). When the time holds one sample,
		/// lower and upper are the same and fraction is 0.
		/// </summary>
		public static void Locate(double[] times, double time, out int lower, out int upper, out double fraction)
		{
			var last = times.Length - 1;
			if (!(time > times[0]))
			{
				lower = 0;
				upper = 0;
				fraction = 0.0;
				return;
			}

			if (time >= times[last])
			{
				lower = last;
				upper = last;
				fraction = 0.0;
				return;
			}

			// times[low] <= time < times[high].
			var low = 0;
			var high = last;
			while (high - low > 1)
			{
				var middle = (low + high) >> 1;
				if (times[middle] <= time)
				{
					low = middle;
				}
				else
				{
					high = middle;
				}
			}

			var gap = times[high] - times[low];
			if (gap <= MaximumInterpolatedGap)
			{
				lower = low;
				upper = high;
				fraction = (time - times[low]) / gap;
				return;
			}

			var nearer = time - times[low] <= times[high] - time ? low : high;
			lower = nearer;
			upper = nearer;
			fraction = 0.0;
		}

		/// <summary>
		/// The parts of the window [<paramref name="start"/>, <paramref name="end"/>] where the series holds a sample instead
		/// of interpolating: before its first sample, after its last, and across gaps longer than
		/// <see cref="MaximumInterpolatedGap"/>. In time order.
		/// </summary>
		public static List<SeriesGap> HeldSpans(EnvironmentSeries series, string stationId, double[] times, double start, double end)
		{
			var spans = new List<SeriesGap>();
			var last = times.Length - 1;
			if (times[0] > start)
			{
				spans.Add(new SeriesGap(series, stationId, start, Math.Min(times[0], end)));
			}

			for (var i = 0; i < last; i++)
			{
				if (times[i + 1] - times[i] > MaximumInterpolatedGap && times[i + 1] > start && times[i] < end)
				{
					spans.Add(new SeriesGap(series, stationId, Math.Max(times[i], start), Math.Min(times[i + 1], end)));
				}
			}

			if (times[last] < end)
			{
				spans.Add(new SeriesGap(series, stationId, Math.Max(times[last], start), end));
			}

			return spans;
		}

		internal static double[] Times(IReadOnlyList<double> times, string what)
		{
			if (times == null || times.Count == 0)
			{
				throw new ArgumentException($"{what} needs at least one sample.");
			}

			var copy = new double[times.Count];
			for (var i = 0; i < copy.Length; i++)
			{
				copy[i] = times[i];
				if (!IsFinite(copy[i]))
				{
					throw new ArgumentException($"{what}: time {i} is not a finite number.");
				}

				if (i > 0 && !(copy[i] > copy[i - 1]))
				{
					throw new ArgumentException($"{what}: times must increase strictly, but sample {i} at {copy[i]} s follows {copy[i - 1]} s.");
				}
			}

			return copy;
		}

		internal static double[] Values(IReadOnlyList<double> values, int count, string what, bool allowNaN = false)
		{
			if (values == null || values.Count != count)
			{
				throw new ArgumentException($"{what} needs one value per time ({count}).");
			}

			var copy = new double[count];
			for (var i = 0; i < count; i++)
			{
				copy[i] = values[i];
				if (!IsFinite(copy[i]) && !(allowNaN && double.IsNaN(copy[i])))
				{
					throw new ArgumentException($"{what}: value {i} is not a finite number.");
				}
			}

			return copy;
		}

		internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}

	/// <summary>
	/// A span of one station's series that holds a sample instead of interpolating (see <see cref="SeriesTime"/>).
	/// Times are seconds since the pack's epoch.
	/// </summary>
	public sealed class SeriesGap
	{
		public SeriesGap(EnvironmentSeries series, string stationId, double start, double end)
		{
			Series = series;
			StationId = stationId;
			Start = start;
			End = end;
		}

		public EnvironmentSeries Series { get; }

		public string StationId { get; }

		/// <summary>Start of the held span, s since the pack's epoch.</summary>
		public double Start { get; }

		/// <summary>End of the held span, s since the pack's epoch.</summary>
		public double End { get; }

		public double Duration => End - Start;

		public override string ToString() => $"{Series} {StationId}: held from {Start:F0} s to {End:F0} s";
	}

	/// <summary>A station's scalar series (water level), sampled as <see cref="SeriesTime"/> describes.</summary>
	public sealed class ScalarSeries
	{
		private readonly double[] _times;
		private readonly double[] _values;

		public ScalarSeries(IReadOnlyList<double> times, IReadOnlyList<double> values)
		{
			_times = SeriesTime.Times(times, "Series");
			_values = SeriesTime.Values(values, _times.Length, "Series");
		}

		public int Count => _times.Length;

		/// <summary>First sample time, s since the pack's epoch.</summary>
		public double StartTime => _times[0];

		/// <summary>Last sample time, s since the pack's epoch.</summary>
		public double EndTime => _times[_times.Length - 1];

		public double Time(int index) => _times[index];

		public double Value(int index) => _values[index];

		/// <summary>The value at a time, s since the pack's epoch.</summary>
		public double Sample(double time)
		{
			SeriesTime.Locate(_times, time, out var lower, out var upper, out var fraction);
			if (lower == upper)
			{
				return _values[lower];
			}

			return _values[lower] + fraction * (_values[upper] - _values[lower]);
		}

		internal double[] TimesArray => _times;
	}

	/// <summary>
	/// A station's velocity series (current or wind): world north and east components, m/s, pointing the way the medium
	/// moves. The components are interpolated, never speed and direction, so a reversing current passes through slack.
	/// </summary>
	public sealed class VectorSeries
	{
		private readonly double[] _times;
		private readonly double[] _north;
		private readonly double[] _east;

		public VectorSeries(IReadOnlyList<double> times, IReadOnlyList<double> north, IReadOnlyList<double> east)
		{
			_times = SeriesTime.Times(times, "Series");
			_north = SeriesTime.Values(north, _times.Length, "Series north component");
			_east = SeriesTime.Values(east, _times.Length, "Series east component");
		}

		public int Count => _times.Length;

		/// <summary>First sample time, s since the pack's epoch.</summary>
		public double StartTime => _times[0];

		/// <summary>Last sample time, s since the pack's epoch.</summary>
		public double EndTime => _times[_times.Length - 1];

		public double Time(int index) => _times[index];

		public HorizontalVector Value(int index) => new HorizontalVector(_north[index], _east[index]);

		/// <summary>The velocity at a time, s since the pack's epoch.</summary>
		public HorizontalVector Sample(double time)
		{
			SeriesTime.Locate(_times, time, out var lower, out var upper, out var fraction);
			if (lower == upper)
			{
				return new HorizontalVector(_north[lower], _east[lower]);
			}

			return new HorizontalVector(
				_north[lower] + fraction * (_north[upper] - _north[lower]),
				_east[lower] + fraction * (_east[upper] - _east[lower]));
		}

		internal double[] TimesArray => _times;
	}
}
