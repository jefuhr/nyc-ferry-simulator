using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>Small traffic packs built in memory, every value chosen so a test can be checked by hand. Not AIS data.</summary>
	public static class TrafficFixtures
	{
		public const string Date = "2025-07-14";

		/// <summary>Local midnight on 2025-07-14 in New York, as UTC.</summary>
		public static readonly DateTimeOffset Epoch = new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero);

		/// <summary>A report: time (s since the epoch), position, SOG (m/s), COG and heading (rad); NaN for what AIS left out.</summary>
		public readonly struct Report
		{
			public Report(double time, double north, double east, double sog, double cog, double heading)
			{
				Time = time;
				North = north;
				East = east;
				Sog = sog;
				Cog = cog;
				Heading = heading;
			}

			public double Time { get; }
			public double North { get; }
			public double East { get; }
			public double Sog { get; }
			public double Cog { get; }
			public double Heading { get; }
		}

		public sealed class Track
		{
			public int Mmsi;
			public string Name = "SYNTHETIC";
			public TrafficClass Class = TrafficClass.TugTow;
			public double Length = 30.0;
			public double Beam = 10.0;
			public double Draft = 3.0;
			public bool Moving = true;
			public bool Ferry;
			public List<Report> Reports = new List<Report>();
		}

		/// <summary>Reports every <paramref name="step"/> s on a straight line at constant velocity, every field valid.</summary>
		public static Track Straight(int mmsi, double t0, double north, double east, double speed, double course, int count, double step = 60.0)
		{
			var track = new Track { Mmsi = mmsi };
			for (var k = 0; k < count; k++)
			{
				var t = k * step;
				track.Reports.Add(new Report(t0 + t, north + speed * Math.Cos(course) * t, east + speed * Math.Sin(course) * t, speed, course, course));
			}

			return track;
		}

		/// <summary>A pack of the tracks, in increasing MMSI order (the format's order).</summary>
		public static TrafficPack Pack(params Track[] tracks)
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
					if (k > 0 && r.Time - track.Reports[k - 1].Time > TrafficPack.SegmentGap)
					{
						s++;
					}

					vessel.Add(i);
					segment.Add(s);
					time.Add(r.Time);
					north.Add(r.North);
					east.Add(r.East);
					sog.Add((float)r.Sog);
					cog.Add((float)Angles.WrapTwoPi(r.Cog));
					heading.Add(double.IsNaN(r.Heading) ? float.NaN : (float)Angles.WrapTwoPi(r.Heading));
				}

				vessels.Add(new TrafficVessel(i, track.Mmsi, track.Name, 31, track.Class, track.Length, track.Beam, track.Draft,
					track.Reports[0].Time, track.Reports[track.Reports.Count - 1].Time, track.Reports.Count, s + 1, track.Moving, track.Ferry));
			}

			var reports = new TrafficReports(vessel.ToArray(), segment.ToArray(), time.ToArray(), north.ToArray(), east.ToArray(), sog.ToArray(), cog.ToArray(), heading.ToArray());
			return new TrafficPack(Date, Epoch, -3600.0, 90000.0, vessels, reports, new string('0', 64));
		}

		/// <summary>A rectangle outline, body axes, for collision tests that do not need a vessel file.</summary>
		public static HullOutline Box(double length, double beam) =>
			new HullOutline(new[] { 0.5 * length, 0.5 * length, -0.5 * length, -0.5 * length }, new[] { -0.5 * beam, 0.5 * beam, 0.5 * beam, -0.5 * beam });
	}
}
