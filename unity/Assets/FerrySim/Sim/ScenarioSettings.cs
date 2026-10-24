using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;

namespace FerrySim.Sim
{
	/// <summary>
	/// A small scenario: which vessel, where it starts, when (UTC) and in what water and air. Authored as a
	/// <see cref="ScenarioAsset"/> or as JSON with the same field names. Degrees and knots appear only here,
	/// at the authoring boundary; everything handed to the core is SI.
	/// </summary>
	[Serializable]
	public sealed class ScenarioSettings
	{
		public string name = "BridgeDev";

		/// <summary>Vessel parameter file, relative to the repository root (or StreamingAssets in a player), or absolute.</summary>
		public string vesselFile = "vessels/t-class.json";

		/// <summary>WGS84 latitude of the reference point at the start, decimal degrees.</summary>
		public double startLatitude = WorldOrigin.LatitudeDegrees;

		/// <summary>WGS84 longitude of the reference point at the start, decimal degrees.</summary>
		public double startLongitude = WorldOrigin.LongitudeDegrees;

		/// <summary>True heading at the start, degrees clockwise from north.</summary>
		public double startHeadingDegrees;

		/// <summary>Speed through the water along the heading at the start, knots. Zero starts at rest.</summary>
		public double startSpeedKnots;

		/// <summary>Absolute UTC start of the scenario, ISO 8601 (for example 2026-06-01T13:00:00Z).</summary>
		public string scenarioStartUtc = "2026-06-01T13:00:00Z";

		public ScenarioEnvironment environment = new ScenarioEnvironment();

		public DateTimeOffset StartUtc
		{
			get
			{
				if (!DateTimeOffset.TryParse(scenarioStartUtc, CultureInfo.InvariantCulture,
						DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
				{
					throw new FormatException($"Scenario '{name}': scenarioStartUtc '{scenarioStartUtc}' is not an ISO 8601 date and time.");
				}

				return start;
			}
		}

		/// <summary>World position of the start point, through the shared tangent plane (never a flat lat/lon scaling).</summary>
		public HorizontalVector StartPosition => LocalTangentPlane.World.ToLocal(new GeodeticPosition(startLatitude, startLongitude));

		public double StartHeading => Angles.WrapTwoPi(Angles.ToRadians(startHeadingDegrees));

		/// <summary>
		/// The initial state: at the start point and heading, moving at <see cref="startSpeedKnots"/> through the
		/// water plus the current (the CLI's convention), with machinery at rest in neutral.
		/// </summary>
		public VesselState InitialState(IEnvironment world)
		{
			var position = StartPosition;
			var heading = StartHeading;
			var current = world.SampleCurrent(position.North, position.East, 0.0);
			var ground = HorizontalVector.FromSpeedToward(Units.KnotsToMetresPerSecond(startSpeedKnots), heading) + current;
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var surge = ground.North * cos + ground.East * sin;
			var sway = -ground.North * sin + ground.East * cos;
			return VesselState.Planar(0.0, position.North, position.East, heading, surge, sway);
		}

		public void Validate()
		{
			var problems = new List<string>();
			if (string.IsNullOrWhiteSpace(vesselFile))
			{
				problems.Add("vesselFile is empty");
			}

			if (!IsFinite(startLatitude) || startLatitude < -90.0 || startLatitude > 90.0)
			{
				problems.Add("startLatitude must be within [-90, 90]");
			}

			if (!IsFinite(startLongitude) || startLongitude < -180.0 || startLongitude > 180.0)
			{
				problems.Add("startLongitude must be within [-180, 180]");
			}

			if (!IsFinite(startHeadingDegrees))
			{
				problems.Add("startHeadingDegrees must be a number");
			}

			if (!IsFinite(startSpeedKnots) || startSpeedKnots < 0.0)
			{
				problems.Add("startSpeedKnots must be zero or positive");
			}

			if (environment == null)
			{
				problems.Add("environment is missing");
			}
			else
			{
				environment.Validate(problems);
			}

			if (problems.Count > 0)
			{
				throw new ArgumentException($"Scenario '{name}': " + string.Join("; ", problems) + ".");
			}

			_ = StartUtc;
		}

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}

	/// <summary>
	/// Uniform water and air for now: calm, or a current, a wind and a depth that are the same everywhere and
	/// at all times. Directions follow docs/conventions.md: the current is the direction the water flows toward,
	/// the wind the direction it blows from.
	/// </summary>
	[Serializable]
	public sealed class ScenarioEnvironment
	{
		/// <summary>Still water and air, 50 m deep (<see cref="UniformEnvironment.Calm"/>); the other fields are ignored.</summary>
		public bool calm = true;

		public double currentSpeedKnots;

		/// <summary>Direction the current flows toward, degrees true (NOAA "set").</summary>
		public double currentTowardDegrees;

		public double windSpeedKnots;

		/// <summary>Direction the wind blows from, degrees true (meteorological).</summary>
		public double windFromDegrees;

		public double depthMetres = 50.0;

		public HorizontalVector Current => calm
			? UniformEnvironment.Calm.Current
			: HorizontalVector.FromSpeedToward(Units.KnotsToMetresPerSecond(currentSpeedKnots), Angles.ToRadians(currentTowardDegrees));

		public HorizontalVector Wind => calm
			? UniformEnvironment.Calm.Wind
			: HorizontalVector.FromSpeedFrom(Units.KnotsToMetresPerSecond(windSpeedKnots), Angles.ToRadians(windFromDegrees));

		public double Depth => calm ? UniformEnvironment.Calm.Depth : depthMetres;

		public UniformEnvironment Create() => new UniformEnvironment(Current, Wind, Depth);

		/// <summary>
		/// The same environment in the CLI's <c>--env</c> syntax, so <c>ferrysim run --env "&lt;spec&gt;"</c>
		/// rebuilds it from the same numbers. Recorded in the telemetry CSV header.
		/// </summary>
		public string SpecText()
		{
			if (calm)
			{
				return "calm";
			}

			return "current=" + TelemetryCsv.Number(currentSpeedKnots) + "kn@" + TelemetryCsv.Number(currentTowardDegrees) +
				",wind=" + TelemetryCsv.Number(windSpeedKnots) + "kn@from" + TelemetryCsv.Number(windFromDegrees) +
				",depth=" + TelemetryCsv.Number(depthMetres) + "m";
		}

		public void Validate(List<string> problems)
		{
			if (calm)
			{
				return;
			}

			if (!(currentSpeedKnots >= 0.0) || double.IsInfinity(currentSpeedKnots) || !(windSpeedKnots >= 0.0) || double.IsInfinity(windSpeedKnots))
			{
				problems.Add("current and wind speeds must be zero or positive");
			}

			if (!InCircle(currentTowardDegrees) || !InCircle(windFromDegrees))
			{
				problems.Add("current and wind directions must be within [0, 360] degrees");
			}

			if (!(depthMetres > 0.0) || double.IsInfinity(depthMetres))
			{
				problems.Add("depthMetres must be positive");
			}
		}

		private static bool InCircle(double degrees) => degrees >= 0.0 && degrees <= 360.0;
	}
}
