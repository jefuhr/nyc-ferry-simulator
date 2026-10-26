using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;

namespace FerrySim.Cli
{
	/// <summary>
	/// Where a run starts and which way it heads: <c>--start-north-m</c> with <c>--start-east-m</c> (world metres), or
	/// <c>--start-lat</c> with <c>--start-lon</c> (WGS84 degrees, through <see cref="LocalTangentPlane.World"/>), and
	/// <c>--start-heading-deg</c> (or <c>--heading-deg</c>, its older name). Without them a run starts at the world origin
	/// heading north, as it always has. A latitude and longitude must lie on the origin's side of the Earth, as well as
	/// within <see cref="Range"/> of it along each world axis.
	/// </summary>
	public sealed class StartPosition
	{
		public const string NorthOption = "start-north-m";
		public const string EastOption = "start-east-m";
		public const string HeadingOption = "start-heading-deg";
		public const string LatitudeOption = "start-lat";
		public const string LongitudeOption = "start-lon";

		/// <summary>The older name of <see cref="HeadingOption"/>.</summary>
		public const string LegacyHeadingOption = "heading-deg";

		/// <summary>Farthest start from the origin along either axis, m. The harbor tiles lie within 16 km of it.</summary>
		public const double Range = 100000.0;

		/// <summary>The options this class reads, for the commands' option lists.</summary>
		public static readonly string[] Options = { NorthOption, EastOption, HeadingOption, LatitudeOption, LongitudeOption, LegacyHeadingOption };

		private StartPosition(double north, double east, double headingDegrees, List<KeyValuePair<string, string>> header)
		{
			North = north;
			East = east;
			HeadingDegrees = headingDegrees;
			Header = header;
		}

		/// <summary>North of the run's first position, m.</summary>
		public double North { get; }

		/// <summary>East of the run's first position, m.</summary>
		public double East { get; }

		/// <summary>The initial heading, degrees true.</summary>
		public double HeadingDegrees { get; }

		/// <summary>Header lines naming the start position; none for a run that starts at the origin by default.</summary>
		public IReadOnlyList<KeyValuePair<string, string>> Header { get; }

		/// <summary>The origin, heading <paramref name="headingDegrees"/>: where runs start without start options.</summary>
		public static StartPosition AtOrigin(double headingDegrees) => new StartPosition(0.0, 0.0, headingDegrees, new List<KeyValuePair<string, string>>());

		/// <summary>The start the options ask for, checked before any file is read.</summary>
		public static StartPosition Parse(ParsedArguments parsed)
		{
			if (parsed.Has(HeadingOption) && parsed.Has(LegacyHeadingOption))
			{
				throw new UsageException($"--{LegacyHeadingOption} and --{HeadingOption} are the same thing; give one.");
			}

			var heading = parsed.Has(HeadingOption)
				? parsed.GetDouble(HeadingOption, 0.0, 0.0, 360.0)
				: parsed.GetDouble(LegacyHeadingOption, 0.0, 0.0, 360.0);
			var metres = Pair(parsed, NorthOption, EastOption);
			var degrees = Pair(parsed, LatitudeOption, LongitudeOption);
			if (metres && degrees)
			{
				throw new UsageException($"Give the start as --{NorthOption} and --{EastOption} or as --{LatitudeOption} and --{LongitudeOption}, not both.");
			}

			var header = new List<KeyValuePair<string, string>>();
			if (degrees)
			{
				var latitude = parsed.GetDouble(LatitudeOption, 0.0, -90.0, 90.0);
				var longitude = parsed.GetDouble(LongitudeOption, 0.0, -180.0, 180.0);
				var position = new GeodeticPosition(latitude, longitude);
				var world = LocalTangentPlane.World.ToLocal(position);
				if (!(OnTheNearSide(position) && Math.Abs(world.North) <= Range && Math.Abs(world.East) <= Range))
				{
					throw new UsageException($"--{LatitudeOption} {Maneuvers.Number(latitude)} --{LongitudeOption} {Maneuvers.Number(longitude)} is more than {Range / 1000.0:0} km from the world origin.");
				}

				header.Add(Maneuvers.Pair("start_lat", Maneuvers.Number(latitude)));
				header.Add(Maneuvers.Pair("start_lon", Maneuvers.Number(longitude)));
				header.Add(Maneuvers.Pair("start_north_m", Maneuvers.Number(world.North)));
				header.Add(Maneuvers.Pair("start_east_m", Maneuvers.Number(world.East)));
				return new StartPosition(world.North, world.East, heading, header);
			}

			if (!metres)
			{
				return new StartPosition(0.0, 0.0, heading, header);
			}

			var north = parsed.GetDouble(NorthOption, 0.0, -Range, Range);
			var east = parsed.GetDouble(EastOption, 0.0, -Range, Range);
			header.Add(Maneuvers.Pair("start_north_m", Maneuvers.Number(north)));
			header.Add(Maneuvers.Pair("start_east_m", Maneuvers.Number(east)));
			return new StartPosition(north, east, heading, header);
		}

		// True when both options are given, false when neither is; one alone is a usage error.
		private static bool Pair(ParsedArguments parsed, string first, string second)
		{
			if (parsed.Has(first) != parsed.Has(second))
			{
				throw new UsageException($"--{first} and --{second} go together.");
			}

			return parsed.Has(first);
		}

		// True when the point's own up direction has an upward part in the world frame. The world plane keeps only north and
		// east, so it folds the far side of the Earth back over the near side (the antipode lands 42 km north of the origin);
		// on the near side each world position stands for one point.
		private static bool OnTheNearSide(GeodeticPosition position)
		{
			var origin = LocalTangentPlane.World.Origin;
			var latitude = Angles.ToRadians(position.LatitudeDegrees);
			var originLatitude = Angles.ToRadians(origin.LatitudeDegrees);
			var longitude = Angles.ToRadians(position.LongitudeDegrees - origin.LongitudeDegrees);
			return Math.Sin(latitude) * Math.Sin(originLatitude) + Math.Cos(latitude) * Math.Cos(originLatitude) * Math.Cos(longitude) > 0.0;
		}
	}
}
