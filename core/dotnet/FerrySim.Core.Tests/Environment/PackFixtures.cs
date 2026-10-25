using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Synthetic environment packs built in memory to the ferrysim-env/1 layout: 6-minute series over the standard
	/// coverage window, station latitudes and longitudes consistent with their world positions. Values are made up for
	/// the tests; nothing here is real NOAA data.
	/// </summary>
	public static class PackFixtures
	{
		public const string Region = "lower-east-river";
		public const string Date = "2025-07-14";
		public const double CoverageStart = -3600.0;
		public const double CoverageEnd = 90000.0;
		public const double Cadence = 360.0;

		/// <summary>Local midnight in New York on 2025-07-14 (daylight time, UTC−4).</summary>
		public static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);

		/// <summary>Sample times from <paramref name="start"/> to <paramref name="end"/> inclusive, every <paramref name="step"/> s.</summary>
		public static double[] Times(double start = CoverageStart, double end = CoverageEnd, double step = Cadence)
		{
			var count = (int)Math.Round((end - start) / step) + 1;
			var times = new double[count];
			for (var i = 0; i < count; i++)
			{
				times[i] = start + i * step;
			}

			return times;
		}

		public static DepthGrid Grid(string id, double originNorth, double originEast, double cell, int rows, int columns, Func<int, int, float> bed)
		{
			var values = new float[rows * columns];
			for (var r = 0; r < rows; r++)
			{
				for (var c = 0; c < columns; c++)
				{
					values[r * columns + c] = bed(r, c);
				}
			}

			return new DepthGrid(id, originNorth, originEast, cell, rows, columns, values);
		}

		/// <summary>A grid with the same bed elevation everywhere.</summary>
		public static DepthGrid FlatGrid(string id, double originNorth, double originEast, double cell, int rows, int columns, float bed) =>
			Grid(id, originNorth, originEast, cell, rows, columns, (r, c) => bed);

		public static DepthSet Depth(double outsideDepth, params DepthGrid[] grids) =>
			new DepthSet(Region, "NAVD88", outsideDepth, grids, new[] { new DepthSource("depth_merged_navd88.tif", new string('a', 64), "synthetic") }, string.Empty);

		public static CurrentStation Current(string id, double north, double east, Func<double, HorizontalVector> velocity, double[] times = null)
		{
			times = times ?? Times();
			var n = new double[times.Length];
			var e = new double[times.Length];
			for (var i = 0; i < times.Length; i++)
			{
				var v = velocity(times[i]);
				n[i] = v.North;
				e[i] = v.East;
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new CurrentStation(id, "Current " + id, "harmonic-prediction", position.LatitudeDegrees, position.LongitudeDegrees, north, east, 1.5,
				id + "_predictions.json", new string('b', 64), new VectorSeries(times, n, e));
		}

		/// <summary>A current station with the same velocity all day.</summary>
		public static CurrentStation SteadyCurrent(string id, double north, double east, HorizontalVector velocity) => Current(id, north, east, t => velocity);

		public static WaterLevelStation Gauge(string id, double north, double east, Func<double, double> level, double[] times = null)
		{
			times = times ?? Times();
			var levels = new double[times.Length];
			var predicted = new bool[times.Length];
			for (var i = 0; i < times.Length; i++)
			{
				levels[i] = level(times[i]);
				predicted[i] = i % 7 == 3;
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new WaterLevelStation(id, "Gauge " + id, position.LatitudeDegrees, position.LongitudeDegrees, north, east,
				id + "_water_level.json", new string('c', 64), new ScalarSeries(times, levels), predicted);
		}

		public static WindStation Wind(string id, double north, double east, Func<double, HorizontalVector> velocity, double[] times = null)
		{
			times = times ?? Times();
			var n = new double[times.Length];
			var e = new double[times.Length];
			var gusts = new double[times.Length];
			for (var i = 0; i < times.Length; i++)
			{
				var v = velocity(times[i]);
				n[i] = v.North;
				e[i] = v.East;
				gusts[i] = i % 2 == 0 ? double.NaN : 1.5 * v.Magnitude;
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new WindStation(id, "Wind " + id, position.LatitudeDegrees, position.LongitudeDegrees, north, east, null,
				id + "_wind.json", new string('d', 64), new VectorSeries(times, n, e), gusts);
		}

		public static EnvironmentPack Pack(DepthSet depth, IReadOnlyList<CurrentStation> currents, IReadOnlyList<WaterLevelStation> gauges,
			IReadOnlyList<WindStation> winds, IReadOnlyList<string> notes = null, DateTimeOffset? epoch = null, string date = Date) =>
			new EnvironmentPack(Region, date, epoch ?? Epoch, CoverageStart, CoverageEnd, depth, currents, gauges, winds, notes ?? Array.Empty<string>(), string.Empty);

		/// <summary>
		/// Calm deep water around the origin: one 2 km square grid of 5 m cells at 15 m below NAVD88, one still current
		/// station, a gauge at NAVD88 zero and still air.
		/// </summary>
		public static EnvironmentPack DeepStillWater() =>
			Pack(Depth(20.0, FlatGrid("slice", -1000.0, -1000.0, 5.0, 401, 401, -15.0f)),
				new[] { SteadyCurrent("C1", 0.0, 0.0, HorizontalVector.Zero) },
				new[] { Gauge("G1", 0.0, 0.0, t => 0.0) },
				new[] { Wind("W1", 0.0, 0.0, t => HorizontalVector.Zero) });
	}
}
