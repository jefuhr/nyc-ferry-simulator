using System;
using FerrySim.Core.Contracts.Geodesy;

namespace FerrySim.World.Tiles3D
{
	/// <summary>
	/// The probe's scripted camera flight, in world (north, east) metres of docs/conventions.md and WGS84 ellipsoidal heights:
	/// low off Wall St/Pier 11, under-bridge height up the East River past the Brooklyn and Williamsburg bridges, then a climb
	/// over Hunters Point that turns to look south-west across the whole harbor. Each leg takes <see cref="LegSeconds"/>, so
	/// the camera crosses tiles at speeds from about 90 to 350 m/s and loads every level of detail. NAVD88 zero is about 32 m
	/// below the ellipsoid here; the heights are camera heights, not datum conversions.
	/// </summary>
	public static class ProbePath
	{
		public const double LegSeconds = 10.0;

		public readonly struct Waypoint
		{
			public Waypoint(string name, double north, double east, double height, double headingDegrees, double pitchDegrees)
			{
				Name = name;
				North = north;
				East = east;
				Height = height;
				HeadingDegrees = headingDegrees;
				PitchDegrees = pitchDegrees;
			}

			public string Name { get; }

			public double North { get; }

			public double East { get; }

			/// <summary>Above the WGS84 ellipsoid, metres.</summary>
			public double Height { get; }

			/// <summary>Clockwise from true north.</summary>
			public double HeadingDegrees { get; }

			/// <summary>Up is positive: -10 looks 10 degrees below the horizon.</summary>
			public double PitchDegrees { get; }
		}

		public static readonly Waypoint[] Waypoints =
		{
			new Waypoint("Upper Bay", -777.0, -211.0, -10.0, 35.0, -4.0),
			new Waypoint("Wall St/Pier 11", 351.0, 749.0, 20.0, 45.0, -8.0),
			new Waypoint("Brooklyn Bridge", 678.0, 1530.0, 40.0, 60.0, -10.0),
			new Waypoint("Williamsburg Bridge", 1478.0, 3617.0, 60.0, 20.0, -10.0),
			new Waypoint("over Hunters Point", 4665.0, 4645.0, 600.0, 220.0, -14.0),
		};

		public static double DurationSeconds => LegSeconds * (Waypoints.Length - 1);

		/// <summary>The camera's pose <paramref name="seconds"/> into the flight: linear between waypoints, the heading the short way round.</summary>
		public static Waypoint Sample(double seconds)
		{
			var t = Math.Max(0.0, Math.Min(DurationSeconds, seconds)) / LegSeconds;
			var leg = Math.Min((int)Math.Floor(t), Waypoints.Length - 2);
			var f = t - leg;
			var a = Waypoints[leg];
			var b = Waypoints[leg + 1];
			var turn = ((b.HeadingDegrees - a.HeadingDegrees) % 360.0 + 540.0) % 360.0 - 180.0;
			var heading = ((a.HeadingDegrees + turn * f) % 360.0 + 360.0) % 360.0;
			return new Waypoint(f < 1.0 ? a.Name : b.Name, a.North + (b.North - a.North) * f, a.East + (b.East - a.East) * f,
				a.Height + (b.Height - a.Height) * f, heading, a.PitchDegrees + (b.PitchDegrees - a.PitchDegrees) * f);
		}

		/// <summary>The flight's length over the ground, metres.</summary>
		public static double LengthMetres()
		{
			var total = 0.0;
			for (var i = 1; i < Waypoints.Length; i++)
			{
				total += Math.Sqrt(Math.Pow(Waypoints[i].North - Waypoints[i - 1].North, 2) + Math.Pow(Waypoints[i].East - Waypoints[i - 1].East, 2));
			}

			return total;
		}

		/// <summary>Latitude and longitude of a world position through the core's tangent plane.</summary>
		public static GeodeticPosition Geodetic(Waypoint waypoint) => LocalTangentPlane.World.ToGeodetic(waypoint.North, waypoint.East);
	}
}
