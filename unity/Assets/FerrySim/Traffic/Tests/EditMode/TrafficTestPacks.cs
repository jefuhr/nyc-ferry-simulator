using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// Small environment and traffic packs in a temporary folder laid out like data/processed, every value chosen so a test can
	/// check it by hand: still water and air over a flat bed 12 m down (so a hull goes where its controls send it), and traffic
	/// vessels on straight lines. Made up for the tests; not NOAA or AIS data.
	/// </summary>
	internal sealed class TrafficTestPacks : IDisposable
	{
		public const string Region = "test-harbor";
		public const string Date = "2025-07-14";
		public const string OtherDate = "2025-07-15";

		/// <summary>The scenario start the tests use: 16:00 UTC (12:00 EDT), 43200 s after the day's epoch.</summary>
		public const string Start = "2025-07-14T16:00:00Z";

		public const double StartPackTime = 43200.0;

		/// <summary>The learner's start: the world origin, heading north.</summary>
		public static readonly HorizontalVector Origin = new HorizontalVector(0.0, 0.0);

		public TrafficTestPacks()
		{
			Root = Path.Combine(Path.GetTempPath(), "ferrysim-traffic-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
			EnvironmentPackWriter.Write(Environment(Date), Path.Combine(Root, Region, "env"));
			EnvironmentPackWriter.Write(Environment(OtherDate), Path.Combine(Root, Region, "env"));
		}

		public string Root { get; }

		public EnvironmentPacks EnvironmentPacks => new EnvironmentPacks(Root);

		public TrafficPacks TrafficPacks => new TrafficPacks(Root);

		public static string PackName(string date) => Region + "/" + date;

		/// <summary>Writes a traffic pack of the tracks for <paramref name="date"/> and returns it as loaded back.</summary>
		public TrafficPack WriteTraffic(string date, params Track[] tracks)
		{
			var folder = TrafficPackWriter.Write(Pack(date, tracks), Path.Combine(Root, "harbor", "traffic", date));
			return TrafficPackJson.Load(folder);
		}

		/// <summary>A scenario at the origin heading north at rest, at <see cref="Start"/>, on the day's environment pack and its traffic.</summary>
		public static ScenarioSettings Scenario(string trafficDay = Date, string leaveOut = ScenarioTraffic.Off, int mmsi = 0, string environmentDate = Date)
		{
			var position = LocalTangentPlane.World.ToGeodetic(Origin.North, Origin.East);
			return new ScenarioSettings
			{
				name = "traffic test",
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = 0.0,
				scenarioStartUtc = Start,
				environment = new ScenarioEnvironment { pack = PackName(environmentDate) },
				traffic = new ScenarioTraffic { day = trafficDay, leaveOut = leaveOut, leaveOutMmsi = mmsi },
			};
		}

		public void Dispose()
		{
			if (Directory.Exists(Root))
			{
				Directory.Delete(Root, true);
			}
		}

		/// <summary>A vessel's reports, every field valid unless set NaN, MMSI order not needed.</summary>
		public sealed class Track
		{
			public int Mmsi;
			public string Name = "TEST VESSEL";
			public TrafficClass Class = TrafficClass.TugTow;
			public double Length = 30.0;
			public double Beam = 10.0;
			public bool Moving = true;
			public bool Ferry;
			public readonly List<double[]> Reports = new List<double[]>();

			/// <summary>Reports every <paramref name="step"/> s from pack time <paramref name="t0"/>, on a straight line at constant velocity.</summary>
			public static Track Straight(int mmsi, double t0, double north, double east, double speed, double course, int count, double step = 60.0)
			{
				var track = new Track { Mmsi = mmsi };
				for (var k = 0; k < count; k++)
				{
					var t = k * step;
					track.Reports.Add(new[] { t0 + t, north + speed * Math.Cos(course) * t, east + speed * Math.Sin(course) * t, speed, Angles.WrapTwoPi(course), Angles.WrapTwoPi(course) });
				}

				return track;
			}
		}

		private static TrafficPack Pack(string date, Track[] tracks)
		{
			var sorted = new List<Track>(tracks);
			sorted.Sort((a, b) => a.Mmsi.CompareTo(b.Mmsi));
			var vessels = new List<TrafficVessel>();
			var vessel = new List<int>();
			var segment = new List<int>();
			var time = new List<double>();
			var north = new List<double>();
			var east = new List<double>();
			var sog = new List<float>();
			var cog = new List<float>();
			var heading = new List<float>();
			for (var i = 0; i < sorted.Count; i++)
			{
				var track = sorted[i];
				var s = 0;
				for (var k = 0; k < track.Reports.Count; k++)
				{
					var r = track.Reports[k];
					if (k > 0 && r[0] - track.Reports[k - 1][0] > TrafficPack.SegmentGap)
					{
						s++;
					}

					vessel.Add(i);
					segment.Add(s);
					time.Add(r[0]);
					north.Add(r[1]);
					east.Add(r[2]);
					sog.Add((float)r[3]);
					cog.Add((float)r[4]);
					heading.Add((float)r[5]);
				}

				vessels.Add(new TrafficVessel(i, track.Mmsi, track.Name, 52, track.Class, track.Length, track.Beam, 3.0, track.Reports[0][0], track.Reports[track.Reports.Count - 1][0],
					track.Reports.Count, s + 1, track.Moving, track.Ferry));
			}

			var day = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
			return new TrafficPack(date, EnvironmentPackJson.NewYorkMidnight(day), -3600.0, 90000.0, vessels,
				new TrafficReports(vessel.ToArray(), segment.ToArray(), time.ToArray(), north.ToArray(), east.ToArray(), sog.ToArray(), cog.ToArray(), heading.ToArray()), string.Empty);
		}

		/// <summary>Still water and air over a flat bed 12 m down, all day.</summary>
		private static EnvironmentPack Environment(string date)
		{
			var day = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
			var epoch = EnvironmentPackJson.NewYorkMidnight(day);
			const double half = 3000.0;
			const double cell = 50.0;
			var cells = (int)(2.0 * half / cell) + 1;
			var bed = new float[cells * cells];
			for (var i = 0; i < bed.Length; i++)
			{
				bed[i] = -12.0f;
			}

			var depth = new DepthSet(Region, "NAVD88", 20.0, new[] { new DepthGrid("slice", -half, -half, cell, cells, cells, bed) },
				new[] { new DepthSource("synthetic.tif", new string('a', 64), "flat synthetic bed") }, string.Empty);
			var times = new double[] { -3600.0, 90000.0 };
			var zero = new double[] { 0.0, 0.0 };
			var position = LocalTangentPlane.World.ToGeodetic(0.0, 0.0);
			var current = new CurrentStation("C1", "Current C1", "harmonic-prediction", position.LatitudeDegrees, position.LongitudeDegrees, 0.0, 0.0, 1.5, "C1.json", new string('b', 64),
				new VectorSeries(times, zero, zero));
			var gauge = new WaterLevelStation("G1", "Gauge G1", position.LatitudeDegrees, position.LongitudeDegrees, 0.0, 0.0, "G1.json", new string('c', 64),
				new ScalarSeries(times, zero), new[] { false, false });
			return new EnvironmentPack(Region, date, epoch, -3600.0, 90000.0, depth, new[] { current }, new[] { gauge }, new WindStation[0], new[] { "synthetic traffic test pack" }, string.Empty);
		}
	}
}
