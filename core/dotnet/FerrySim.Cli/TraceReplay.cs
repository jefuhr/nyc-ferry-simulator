using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>
	/// A control trace: rows of <c>time_s,port_lever,starboard_lever,helm</c>. Each row applies from its
	/// time until the next row; before the first row the controls are neutral. Times are rounded to the
	/// nearest 10 ms physics step.
	/// </summary>
	public sealed class TraceInputs
	{
		public const string HeaderRow = "time_s,port_lever,starboard_lever,helm";

		private readonly long[] _steps;
		private readonly ControlInputs[] _controls;

		private TraceInputs(long[] steps, ControlInputs[] controls)
		{
			_steps = steps;
			_controls = controls;
		}

		public int Count => _steps.Length;

		/// <summary>Time of the last row, seconds; the replay ends there unless a duration is given.</summary>
		public double EndTime => SimulationClock.TimeAtStep(_steps[_steps.Length - 1]);

		/// <summary>The controls in effect during the physics step that starts at <paramref name="step"/>.</summary>
		public ControlInputs At(long step)
		{
			// Rows are few and replay is sequential, but a binary search keeps this independent of call order.
			int low = 0, high = _steps.Length - 1, found = -1;
			while (low <= high)
			{
				var mid = (low + high) / 2;
				if (_steps[mid] <= step)
				{
					found = mid;
					low = mid + 1;
				}
				else
				{
					high = mid - 1;
				}
			}

			return found < 0 ? ControlInputs.Neutral : _controls[found];
		}

		public static TraceInputs Load(string path)
		{
			if (!File.Exists(path))
			{
				throw new CliException($"Input trace not found: {path}");
			}

			return Parse(File.ReadAllLines(path), path);
		}

		public static TraceInputs Parse(string[] lines, string label = "trace")
		{
			var steps = new List<long>();
			var controls = new List<ControlInputs>();
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
				if (cells.Length != 4)
				{
					throw new CliException($"{label}: line {i + 1}: expected 4 columns, found {cells.Length}.");
				}

				var time = Cell(cells[0], label, i, "time_s");
				var port = Cell(cells[1], label, i, "port_lever");
				var starboard = Cell(cells[2], label, i, "starboard_lever");
				var helm = Cell(cells[3], label, i, "helm");
				if (time < 0.0)
				{
					throw new CliException($"{label}: line {i + 1}: time_s cannot be negative.");
				}

				foreach (var value in new[] { port, starboard, helm })
				{
					if (value < -1.0 || value > 1.0)
					{
						throw new CliException($"{label}: line {i + 1}: lever and helm values must be within [-1, 1].");
					}
				}

				var step = (long)Math.Round(time * SimulationClock.StepsPerSecond);
				if (steps.Count > 0 && step <= steps[steps.Count - 1])
				{
					throw new CliException($"{label}: line {i + 1}: time_s must increase by at least one 10 ms step per row.");
				}

				steps.Add(step);
				controls.Add(new ControlInputs(port, starboard, helm));
			}

			if (!sawHeader)
			{
				throw new CliException($"{label}: missing the header row '{HeaderRow}'.");
			}

			if (steps.Count == 0)
			{
				throw new CliException($"{label}: no data rows.");
			}

			return new TraceInputs(steps.ToArray(), controls.ToArray());
		}

		private static double Cell(string text, string label, int lineIndex, string column)
		{
			if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
				double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new CliException($"{label}: line {lineIndex + 1}: {column} '{text.Trim()}' is not a number.");
			}

			return value;
		}
	}

	/// <summary>Plays a <see cref="TraceInputs"/> trace back and finishes at the given time.</summary>
	internal sealed class TraceController : IController
	{
		private readonly TraceInputs _trace;
		private readonly double _duration;

		public TraceController(TraceInputs trace, double duration)
		{
			_trace = trace;
			_duration = duration;
		}

		public bool Finished { get; private set; }

		public ControlInputs Command(double elapsed, VesselState state, VesselTelemetry telemetry)
		{
			var step = (long)Math.Round(elapsed * SimulationClock.StepsPerSecond);
			Finished = step >= (long)Math.Round(_duration * SimulationClock.StepsPerSecond);
			return _trace.At(step);
		}
	}
}
