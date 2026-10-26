using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>A transit's route: rows of <c>north_m,east_m</c>, world metres, steered in file order.</summary>
	public sealed class Waypoints
	{
		public const string HeaderRow = "north_m,east_m";

		private readonly HorizontalVector[] _points;

		private Waypoints(HorizontalVector[] points)
		{
			_points = points;
		}

		public int Count => _points.Length;

		public HorizontalVector this[int index] => _points[index];

		/// <summary>The route as the CSV header records it: <c>north,east;north,east;…</c>.</summary>
		public string Text
		{
			get
			{
				var builder = new StringBuilder();
				for (var i = 0; i < _points.Length; i++)
				{
					builder.Append(i > 0 ? ";" : string.Empty).Append(Maneuvers.Number(_points[i].North)).Append(',').Append(Maneuvers.Number(_points[i].East));
				}

				return builder.ToString();
			}
		}

		public static Waypoints Load(string path)
		{
			if (!File.Exists(path))
			{
				throw new CliException($"Waypoints not found: {path}");
			}

			return Parse(File.ReadAllLines(path), path);
		}

		/// <summary>Parses the route, skipping blank and <c>#</c> lines; numbers must be finite and within <see cref="StartPosition.Range"/>.</summary>
		public static Waypoints Parse(string[] lines, string label = "waypoints")
		{
			var points = new List<HorizontalVector>();
			var sawHeader = false;
			for (var i = 0; i < lines.Length; i++)
			{
				var line = lines[i].Trim();
				if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
				{
					continue;
				}

				if (!sawHeader)
				{
					if (line.Replace(" ", string.Empty) != HeaderRow)
					{
						throw new CliException($"{label}: line {i + 1}: expected the header row '{HeaderRow}'.");
					}

					sawHeader = true;
					continue;
				}

				var cells = line.Split(',');
				if (cells.Length != 2)
				{
					throw new CliException($"{label}: line {i + 1}: expected 2 columns, found {cells.Length}.");
				}

				points.Add(new HorizontalVector(Cell(cells[0], label, i, "north_m"), Cell(cells[1], label, i, "east_m")));
			}

			if (!sawHeader)
			{
				throw new CliException($"{label}: missing the header row '{HeaderRow}'.");
			}

			if (points.Count == 0)
			{
				throw new CliException($"{label}: no waypoints.");
			}

			return new Waypoints(points.ToArray());
		}

		private static double Cell(string text, string label, int lineIndex, string column)
		{
			if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
				double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > StartPosition.Range)
			{
				throw new CliException($"{label}: line {lineIndex + 1}: {column} '{text.Trim()}' is not a number within {StartPosition.Range:0} m of the origin.");
			}

			return value;
		}
	}

	/// <summary>A controller that reports what it found once the run is over, for the CSV header.</summary>
	internal interface IReportsResult
	{
		/// <summary>Header lines, written after the run with a <c>result.</c> prefix.</summary>
		IEnumerable<KeyValuePair<string, string>> Result { get; }
	}

	/// <summary>
	/// Steers through waypoints in turn with both levers held. Line-of-sight guidance: the course is the leg's bearing
	/// turned toward the leg by atan(cross-track error / <see cref="LookaheadMetres"/>), so a cross current that pushes
	/// the vessel off is steered back. A heading hold follows that course: helm = <see cref="HeadingGain"/> × heading
	/// error − <see cref="RateGain"/> × yaw rate, within ±1. The first leg runs from where the vessel starts; a waypoint
	/// is reached within the arrival radius or once the vessel is past the line through it square to its leg, and the
	/// run ends at the last waypoint (or at the cap or fixed duration).
	/// </summary>
	internal sealed class TransitController : ManeuverController, IReportsResult
	{
		public const double DefaultCap = 3600.0;
		public const double LookaheadMetres = 200.0;
		public const double HeadingGain = 2.0;
		public const double RateGain = 8.0;

		private readonly double _hold;
		private readonly Waypoints _waypoints;
		private readonly double _arrive;
		private HorizontalVector _legStart;
		private bool _started;
		private int _next;
		private double _arrivedAt = double.NaN;

		public TransitController(double hold, Waypoints waypoints, double arriveMetres, double? fixedDuration)
			: base(fixedDuration, DefaultCap)
		{
			_hold = hold;
			_waypoints = waypoints;
			_arrive = arriveMetres;
		}

		public IEnumerable<KeyValuePair<string, string>> Result
		{
			get
			{
				yield return Maneuvers.Pair("waypoints_reached", _next.ToString(CultureInfo.InvariantCulture));
				yield return Maneuvers.Pair("arrived", _next == _waypoints.Count ? "yes" : "no");
				yield return Maneuvers.Pair("arrived_s", double.IsNaN(_arrivedAt) ? "not reached" : Maneuvers.Number(_arrivedAt));
			}
		}

		protected override ControlInputs Decide(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			var position = new HorizontalVector(state.North, state.East);
			if (!_started)
			{
				_legStart = position;
				_started = true;
			}

			var helm = 0.0;
			while (_next < _waypoints.Count)
			{
				var target = _waypoints[_next];
				var leg = target - _legStart;
				var length = leg.Magnitude;
				var offset = position - _legStart;
				var along = length > 0.0 ? (offset.North * leg.North + offset.East * leg.East) / length : length;
				if ((position - target).Magnitude <= _arrive || along >= length)
				{
					_legStart = target;
					_next++;
					continue;
				}

				// Positive to starboard of the leg, looking along it.
				var cross = (-offset.North * leg.East + offset.East * leg.North) / length;
				var course = Math.Atan2(leg.East, leg.North) + Math.Atan2(-cross, LookaheadMetres);
				var error = Angles.WrapPi(course - state.Heading);
				helm = Math.Max(-1.0, Math.Min(1.0, HeadingGain * error - RateGain * state.YawRate));
				break;
			}

			if (_next == _waypoints.Count && double.IsNaN(_arrivedAt))
			{
				_arrivedAt = elapsed;
			}

			return new ControlInputs(_hold, _hold, helm);
		}

		protected override bool IsComplete(double elapsed, VesselState state, VesselTelemetry telemetry) => _next == _waypoints.Count;
	}
}
