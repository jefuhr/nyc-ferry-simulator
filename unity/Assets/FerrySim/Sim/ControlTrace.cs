using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// A step-indexed control trace in the CLI's <c>ferrysim run --inputs</c> format: rows of
	/// <c>time_s,port_lever,starboard_lever,helm</c>, each applying from its time until the next row, neutral
	/// before the first. Times sit on the 10 ms step grid and values are written so they read back to the
	/// same doubles, which is what makes a replay reproduce a recorded run exactly.
	/// </summary>
	public sealed class ControlTrace : IControlInputSource
	{
		public const string HeaderRow = "time_s,port_lever,starboard_lever,helm";

		private readonly List<long> _steps = new List<long>();
		private readonly List<ControlInputs> _controls = new List<ControlInputs>();

		public int Count => _steps.Count;

		/// <summary>Step of the last row; a replay of the whole trace ends here.</summary>
		public long EndStep => _steps.Count == 0 ? 0 : _steps[_steps.Count - 1];

		public double EndTime => SimulationClock.TimeAtStep(EndStep);

		public long StepAt(int row) => _steps[row];

		public ControlInputs ControlsAt(int row) => _controls[row];

		/// <summary>Adds a row; steps must increase.</summary>
		public void Append(long step, in ControlInputs controls)
		{
			if (step < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(step), "Trace steps cannot be negative.");
			}

			if (_steps.Count > 0 && step <= _steps[_steps.Count - 1])
			{
				throw new ArgumentException($"Trace steps must increase: {step} after {_steps[_steps.Count - 1]}.", nameof(step));
			}

			_steps.Add(step);
			_controls.Add(FixedStepDriver.Canonical(controls));
		}

		/// <summary>The controls in effect during the step that starts at <paramref name="step"/>.</summary>
		public ControlInputs At(long step)
		{
			int low = 0, high = _steps.Count - 1, found = -1;
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

		public ControlInputs ControlsForStep(long step) => At(step);

		/// <summary>One data row: time on the step grid and lossless lever and helm values.</summary>
		public static string FormatRow(long step, in ControlInputs controls)
		{
			var builder = new StringBuilder(64);
			builder.Append(FormatTime(step)).Append(',');
			builder.Append(FormatValue(controls.PortLever)).Append(',');
			builder.Append(FormatValue(controls.StarboardLever)).Append(',');
			builder.Append(FormatValue(controls.Helm));
			return builder.ToString();
		}

		public static string FormatTime(long step) =>
			SimulationClock.TimeAtStep(step).ToString("0.00", CultureInfo.InvariantCulture);

		/// <summary>The shortest text that parses back to exactly <paramref name="value"/>.</summary>
		public static string FormatValue(double value)
		{
			// −0.0 becomes +0.0: some runtimes read "-0" back as +0.
			value += 0.0;
			var text = value.ToString("R", CultureInfo.InvariantCulture);
			if (double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).Equals(value))
			{
				return text;
			}

			// "R" is not always round-trippable on older runtimes; 17 significant digits always are.
			return value.ToString("G17", CultureInfo.InvariantCulture);
		}

		/// <summary>Writes the trace: optional <c>#</c> comment lines, the header row and every row.</summary>
		public void Write(TextWriter writer, IEnumerable<string> comments = null)
		{
			if (comments != null)
			{
				foreach (var comment in comments)
				{
					writer.Write("# ");
					writer.Write(comment);
					writer.Write('\n');
				}
			}

			writer.Write(HeaderRow);
			writer.Write('\n');
			for (var i = 0; i < _steps.Count; i++)
			{
				writer.Write(FormatRow(_steps[i], _controls[i]));
				writer.Write('\n');
			}
		}

		public static ControlTrace Load(string path)
		{
			if (!File.Exists(path))
			{
				throw new FileNotFoundException($"Control trace not found: {path}", path);
			}

			return Parse(File.ReadAllLines(path), path);
		}

		/// <summary>
		/// Reads a trace with the CLI's rules: blank and <c>#</c> lines are skipped, the header row comes first,
		/// values are in [−1, 1], times are non-negative, round to the nearest 10 ms step and must increase.
		/// </summary>
		public static ControlTrace Parse(IReadOnlyList<string> lines, string label = "trace")
		{
			var trace = new ControlTrace();
			var sawHeader = false;
			for (var i = 0; i < lines.Count; i++)
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
						throw new FormatException($"{label}: line {i + 1}: expected the header row '{HeaderRow}'.");
					}

					sawHeader = true;
					continue;
				}

				var cells = line.Split(',');
				if (cells.Length != 4)
				{
					throw new FormatException($"{label}: line {i + 1}: expected 4 columns, found {cells.Length}.");
				}

				var time = Cell(cells[0], label, i, "time_s");
				var port = Cell(cells[1], label, i, "port_lever");
				var starboard = Cell(cells[2], label, i, "starboard_lever");
				var helm = Cell(cells[3], label, i, "helm");
				if (time < 0.0)
				{
					throw new FormatException($"{label}: line {i + 1}: time_s cannot be negative.");
				}

				if (!InRange(port) || !InRange(starboard) || !InRange(helm))
				{
					throw new FormatException($"{label}: line {i + 1}: lever and helm values must be within [-1, 1].");
				}

				var step = (long)Math.Round(time * SimulationClock.StepsPerSecond);
				if (trace.Count > 0 && step <= trace.EndStep)
				{
					throw new FormatException($"{label}: line {i + 1}: time_s must increase by at least one 10 ms step per row.");
				}

				trace.Append(step, new ControlInputs(port, starboard, helm));
			}

			if (!sawHeader)
			{
				throw new FormatException($"{label}: missing the header row '{HeaderRow}'.");
			}

			if (trace.Count == 0)
			{
				throw new FormatException($"{label}: no data rows.");
			}

			return trace;
		}

		private static bool InRange(double value) => value >= -1.0 && value <= 1.0;

		private static double Cell(string text, string label, int lineIndex, string column)
		{
			if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
				double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new FormatException($"{label}: line {lineIndex + 1}: {column} '{text.Trim()}' is not a number.");
			}

			return value;
		}
	}
}
