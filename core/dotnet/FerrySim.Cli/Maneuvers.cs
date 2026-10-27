using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>A maneuver ready to run: its recorded parameters and a factory for its controller.</summary>
	internal sealed class ManeuverPlan
	{
		public ManeuverPlan(List<KeyValuePair<string, string>> parameters, Func<double, IController> create)
		{
			Parameters = parameters;
			Create = create;
		}

		public List<KeyValuePair<string, string>> Parameters { get; }

		/// <summary>Builds the controller; the argument is the lever position the vessel settled at (ignored when starting at rest).</summary>
		public Func<double, IController> Create { get; }
	}

	/// <summary>One standard maneuver: its name, options and how its command line becomes a controller.</summary>
	internal sealed class ManeuverSpec
	{
		public ManeuverSpec(string name, string summary, bool startsAtRest, string[] options, Func<ParsedArguments, double?, double, ManeuverPlan> build,
			bool calmWaterLever = false)
		{
			Name = name;
			Summary = summary;
			StartsAtRest = startsAtRest;
			Options = options;
			Build = build;
			CalmWaterLever = calmWaterLever;
		}

		public string Name { get; }

		public string Summary { get; }

		/// <summary>True when the vessel starts dead in the water (no settling at a test speed in the run's water).</summary>
		public bool StartsAtRest { get; }

		/// <summary>
		/// True when a manoeuvre that starts at rest holds the lever that gives the test speed (<c>--speed-kn</c>) through
		/// water in calm deep water, found before the run as the settled manoeuvres find theirs.
		/// </summary>
		public bool CalmWaterLever { get; }

		/// <summary>Maneuver-specific option names (without dashes).</summary>
		public string[] Options { get; }

		/// <summary>Takes the arguments and the fixed duration (if any) and returns the plan.</summary>
		public Func<ParsedArguments, double?, double, ManeuverPlan> Build { get; }
	}

	internal static class Maneuvers
	{
		public const double DefaultRudderDegrees = 10.0;
		public const double DefaultPivotLever = 0.5;

		public static readonly ManeuverSpec[] All =
		{
			new ManeuverSpec(
				"turning-circle",
				"hard-over helm at the test speed, levers held, until the heading has changed past 360 degrees",
				false,
				new[] { "direction", "helm" },
				BuildTurningCircle),
			new ManeuverSpec(
				"zigzag",
				"helm reversed each time the heading change reaches the check angle (10/10 by default)",
				false,
				new[] { "first", "angle-deg", "rudder-deg", "max-rudder-deg", "helm" },
				BuildZigzag),
			new ManeuverSpec(
				"crash-stop",
				"both levers to full astern from the test speed, helm amidships",
				false,
				Array.Empty<string>(),
				(args, duration, hardOver) => BuildStop(-1.0, duration)),
			new ManeuverSpec(
				"pivot",
				"split engines at rest: port ahead and starboard astern by default",
				true,
				new[] { "direction", "lever" },
				BuildPivot),
			new ManeuverSpec(
				"acceleration",
				"from rest to full ahead on both levers until the speed is steady",
				true,
				Array.Empty<string>(),
				BuildAcceleration),
			new ManeuverSpec(
				"stopping",
				"both levers to neutral from the test speed, helm amidships",
				false,
				Array.Empty<string>(),
				(args, duration, hardOver) => BuildStop(0.0, duration)),
			new ManeuverSpec(
				"transit",
				"from rest through --waypoints in turn, both levers at the test speed's calm-water lever, steered by a heading hold",
				true,
				new[] { "waypoints", "arrive-m" },
				BuildTransit,
				calmWaterLever: true),
		};

		public const double DefaultArriveMetres = 50.0;

		public static ManeuverSpec Find(string name)
		{
			foreach (var spec in All)
			{
				if (spec.Name == name)
				{
					return spec;
				}
			}

			return null;
		}

		private static ManeuverPlan BuildTurningCircle(ParsedArguments args, double? duration, double hardOverDegrees)
		{
			var direction = args.GetChoice("direction", "starboard", "starboard", "port");
			var helm = args.GetDouble("helm", 1.0, 0.0, 1.0);
			var sign = direction == "starboard" ? 1.0 : -1.0;
			var parameters = new List<KeyValuePair<string, string>>
			{
				Pair("direction", direction),
				Pair("helm", Number(helm)),
			};
			return new ManeuverPlan(parameters, hold => new TurningCircleController(hold, sign * helm, duration));
		}

		private static ManeuverPlan BuildZigzag(ParsedArguments args, double? duration, double hardOverDegrees)
		{
			var first = args.GetChoice("first", "starboard", "starboard", "port");
			var angle = args.GetDouble("angle-deg", 10.0, 1.0, 90.0);
			var rudder = args.GetDouble("rudder-deg", DefaultRudderDegrees, 1.0, 90.0);
			var maxRudder = args.GetDouble("max-rudder-deg", hardOverDegrees, 1.0, 90.0);
			if (rudder > maxRudder)
			{
				throw new UsageException($"--rudder-deg ({Number(rudder)}) cannot exceed --max-rudder-deg ({Number(maxRudder)}).");
			}

			// The control model owns the helm-to-rudder mapping; assuming it is linear up to the maximum rudder angle.
			var helm = args.GetDouble("helm", rudder / maxRudder, 0.0, 1.0);
			var sign = first == "starboard" ? 1.0 : -1.0;
			var parameters = new List<KeyValuePair<string, string>>
			{
				Pair("first", first),
				Pair("check_deg", Number(angle)),
				Pair("rudder_deg", Number(rudder)),
				Pair("max_rudder_deg", Number(maxRudder)),
				Pair("helm", Number(helm)),
			};
			return new ManeuverPlan(parameters, hold => new ZigzagController(hold, helm, Angles.ToRadians(angle), sign, duration));
		}

		private static ManeuverPlan BuildStop(double lever, double? duration)
		{
			var parameters = new List<KeyValuePair<string, string>>
			{
				Pair("lever", Number(lever)),
				Pair("stop_speed_mps", Number(StopController.StopSpeedMetresPerSecond)),
			};
			return new ManeuverPlan(parameters, hold => new StopController(lever, duration));
		}

		private static ManeuverPlan BuildPivot(ParsedArguments args, double? duration, double hardOverDegrees)
		{
			var direction = args.GetChoice("direction", "starboard", "starboard", "port");
			var lever = args.GetDouble("lever", DefaultPivotLever, 0.05, 1.0);
			// Port ahead with starboard astern swings the bow to starboard.
			var port = direction == "starboard" ? lever : -lever;
			var controls = new ControlInputs(port, -port, 0.0);
			var length = duration ?? HoldController.DefaultPivotSeconds;
			var parameters = new List<KeyValuePair<string, string>>
			{
				Pair("direction", direction),
				Pair("lever", Number(lever)),
				Pair("duration_s", Number(length)),
			};
			return new ManeuverPlan(parameters, hold => new HoldController(controls, length, false, length));
		}

		private static ManeuverPlan BuildAcceleration(ParsedArguments args, double? duration, double hardOverDegrees)
		{
			var controls = new ControlInputs(1.0, 1.0, 0.0);
			var parameters = new List<KeyValuePair<string, string>> { Pair("lever", "1") };
			return new ManeuverPlan(parameters, hold => new HoldController(controls, duration, true, HoldController.AccelerationCap));
		}

		private static ManeuverPlan BuildTransit(ParsedArguments args, double? duration, double hardOverDegrees)
		{
			var file = args.GetString("waypoints");
			if (file == null)
			{
				throw new UsageException("transit needs --waypoints <file.csv>, rows of north_m,east_m in world metres.");
			}

			var arrive = args.GetDouble("arrive-m", DefaultArriveMetres, 1.0, 1000.0);
			var waypoints = Waypoints.Load(file);
			var parameters = new List<KeyValuePair<string, string>>
			{
				Pair("waypoints_file", System.IO.Path.GetFileName(file)),
				Pair("waypoints", waypoints.Text),
				Pair("arrive_m", Number(arrive)),
				Pair("lookahead_m", Number(TransitController.LookaheadMetres)),
				Pair("heading_gain", Number(TransitController.HeadingGain)),
				Pair("rate_gain_s", Number(TransitController.RateGain)),
			};
			return new ManeuverPlan(parameters, hold => new TransitController(hold, waypoints, arrive, duration));
		}

		public static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

		public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
	}
}
