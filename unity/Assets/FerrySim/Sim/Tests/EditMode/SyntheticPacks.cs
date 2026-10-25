using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// Small environment packs (ferrysim-env/1) written to a temporary folder in the pipeline's layout, with values chosen
	/// so a test can work out every sample by hand: a uniform current, a water level rising at a fixed rate, a steady wind
	/// and a flat bed on one grid around the origin. Made up for the tests; nothing here is real NOAA data.
	/// </summary>
	internal sealed class SyntheticPacks : IDisposable
	{
		public const string Region = "test-harbor";
		public const string SummerDate = "2025-07-14";
		public const string WinterDate = "2025-01-14";
		public const double CoverageStart = -3600.0;
		public const double CoverageEnd = 90000.0;
		public const double Cadence = 360.0;

		/// <summary>Bed elevation of the grid, m NAVD88.</summary>
		public const double Bed = -12.0;

		/// <summary>Half the side of the square grid around the origin, m.</summary>
		public const double GridHalfSide = 1000.0;

		/// <summary>The water level at the epoch, m NAVD88, and how fast it rises, m/s.</summary>
		public const double LevelAtEpoch = 0.25;

		public const double LevelRate = 2.0e-5;

		public static readonly HorizontalVector Current = new HorizontalVector(0.3, -0.4);
		public static readonly HorizontalVector Wind = new HorizontalVector(-2.0, 1.5);

		public SyntheticPacks()
		{
			Root = Path.Combine(Path.GetTempPath(), "ferrysim-packs-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
		}

		/// <summary>The folder laid out like data/processed: &lt;root&gt;/&lt;region&gt;/env/...</summary>
		public string Root { get; }

		public EnvironmentPacks Packs => new EnvironmentPacks(Root);

		public static string Name(string date) => Region + "/" + date;

		public static double Level(double packTime) => LevelAtEpoch + LevelRate * packTime;

		/// <summary>Writes a day under <see cref="Root"/> (or another root) and returns its day folder.</summary>
		public string Write(string date, bool withWind = true, double levelOffset = 0.0, string root = null) =>
			EnvironmentPackWriter.Write(Pack(date, withWind, levelOffset), Path.Combine(root ?? Root, Region, "env"));

		public static EnvironmentPack Pack(string date, bool withWind = true, double levelOffset = 0.0)
		{
			var day = DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
			var epoch = EnvironmentPackJson.NewYorkMidnight(day);
			var cells = (int)(2.0 * GridHalfSide / 5.0) + 1;
			var bed = new float[cells * cells];
			for (var i = 0; i < bed.Length; i++)
			{
				bed[i] = (float)Bed;
			}

			var depth = new DepthSet(Region, "NAVD88", 20.0, new[] { new DepthGrid("slice", -GridHalfSide, -GridHalfSide, 5.0, cells, cells, bed) },
				new[] { new DepthSource("synthetic.tif", new string('a', 64), "flat synthetic bed") }, string.Empty);
			var times = Times();
			var winds = new List<WindStation>();
			if (withWind)
			{
				winds.Add(MakeWind("W1", 200.0, -300.0, times));
			}

			return new EnvironmentPack(Region, date, epoch, CoverageStart, CoverageEnd, depth,
				new[] { MakeCurrent("C1", 100.0, 50.0, times) },
				new[] { MakeGauge("G1", -150.0, 120.0, times, levelOffset) },
				winds, new[] { "synthetic test pack" }, string.Empty);
		}

		public static double[] Times()
		{
			var count = (int)Math.Round((CoverageEnd - CoverageStart) / Cadence) + 1;
			var times = new double[count];
			for (var i = 0; i < count; i++)
			{
				times[i] = CoverageStart + i * Cadence;
			}

			return times;
		}

		public void Dispose()
		{
			if (Directory.Exists(Root))
			{
				Directory.Delete(Root, true);
			}
		}

		private static CurrentStation MakeCurrent(string id, double north, double east, double[] times)
		{
			var n = new double[times.Length];
			var e = new double[times.Length];
			for (var i = 0; i < times.Length; i++)
			{
				n[i] = Current.North;
				e[i] = Current.East;
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new CurrentStation(id, "Current " + id, "harmonic-prediction", position.LatitudeDegrees, position.LongitudeDegrees, north, east, 1.5,
				id + ".json", new string('b', 64), new VectorSeries(times, n, e));
		}

		private static WaterLevelStation MakeGauge(string id, double north, double east, double[] times, double offset)
		{
			var levels = new double[times.Length];
			var predicted = new bool[times.Length];
			for (var i = 0; i < times.Length; i++)
			{
				levels[i] = Level(times[i]) + offset;
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new WaterLevelStation(id, "Gauge " + id, position.LatitudeDegrees, position.LongitudeDegrees, north, east, id + ".json", new string('c', 64),
				new ScalarSeries(times, levels), predicted);
		}

		private static WindStation MakeWind(string id, double north, double east, double[] times)
		{
			var n = new double[times.Length];
			var e = new double[times.Length];
			var gusts = new double[times.Length];
			for (var i = 0; i < times.Length; i++)
			{
				n[i] = Wind.North;
				e[i] = Wind.East;
				gusts[i] = double.NaN;
			}

			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new WindStation(id, "Wind " + id, position.LatitudeDegrees, position.LongitudeDegrees, north, east, null, id + ".json", new string('d', 64),
				new VectorSeries(times, n, e), gusts);
		}
	}
}
