using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>The telemetry CSV schema: SI columns with unit suffixes, plus kn and deg convenience columns.</summary>
	public static class TelemetryColumns
	{
		public const string FormatVersion = "1";

		public static readonly string[] Names =
		{
			"time_s", "north_m", "east_m", "heading_deg", "surge_mps", "sway_mps", "yaw_rate_degps",
			"sog_kn", "cog_deg", "stw_kn",
			"port_engine_rpm", "stbd_engine_rpm", "port_shaft_rpm", "stbd_shaft_rpm", "port_gear", "stbd_gear",
			"port_rudder_deg", "stbd_rudder_deg", "depth_under_keel_m", "contact_force_n",
			"port_lever", "starboard_lever", "helm",
		};

		/// <summary>Columns holding text (gear names); every other column is a number.</summary>
		public static bool IsText(string name) => name == "port_gear" || name == "stbd_gear";
	}

	/// <summary>Collects telemetry rows in memory and renders the CSV with its <c>#</c> header comment.</summary>
	public sealed class CsvRecorder
	{
		private readonly List<double> _times = new List<double>();
		private readonly List<string> _lines = new List<string>();

		public int Count => _lines.Count;

		public double LastTime => _times.Count == 0 ? double.NaN : _times[_times.Count - 1];

		/// <summary>Records the state and telemetry at this instant and the controls that start applying now.</summary>
		public void Add(VesselState state, VesselTelemetry telemetry, ControlInputs controls)
		{
			var port = telemetry.Port;
			var starboard = telemetry.Starboard;
			var builder = new StringBuilder(256);
			Append(builder, state.Time, "F3");
			Append(builder, state.North, "F3");
			Append(builder, state.East, "F3");
			Append(builder, Angles.ToDegrees(state.Heading), "F4");
			Append(builder, state.Surge, "F4");
			Append(builder, state.Sway, "F4");
			Append(builder, Angles.ToDegrees(state.YawRate), "F4");
			Append(builder, Units.MetresPerSecondToKnots(telemetry.SpeedOverGround), "F4");
			Append(builder, Angles.ToDegrees(Angles.WrapTwoPi(telemetry.CourseOverGround)), "F3");
			Append(builder, Units.MetresPerSecondToKnots(telemetry.SpeedThroughWater), "F4");
			Append(builder, Units.RadiansPerSecondToRpm(port.EngineSpeed), "F2");
			Append(builder, Units.RadiansPerSecondToRpm(starboard.EngineSpeed), "F2");
			Append(builder, Units.RadiansPerSecondToRpm(port.ShaftSpeed), "F2");
			Append(builder, Units.RadiansPerSecondToRpm(starboard.ShaftSpeed), "F2");
			builder.Append(GearName(port.Gear)).Append(',');
			builder.Append(GearName(starboard.Gear)).Append(',');
			Append(builder, Angles.ToDegrees(telemetry.PortRudderAngle), "F3");
			Append(builder, Angles.ToDegrees(telemetry.StarboardRudderAngle), "F3");
			Append(builder, telemetry.DepthUnderKeel, "F3");
			Append(builder, telemetry.ContactForce, "F1");
			Append(builder, controls.PortLever, "F4");
			Append(builder, controls.StarboardLever, "F4");
			Append(builder, controls.Helm, "F4", last: true);
			_times.Add(state.Time);
			_lines.Add(builder.ToString());
		}

		/// <summary>Drops rows recorded before <paramref name="time"/>; used to keep only the lead-in to a maneuver.</summary>
		public void TrimBefore(double time)
		{
			var keepFrom = 0;
			while (keepFrom < _times.Count && _times[keepFrom] < time)
			{
				keepFrom++;
			}

			_times.RemoveRange(0, keepFrom);
			_lines.RemoveRange(0, keepFrom);
		}

		public string Render(IEnumerable<KeyValuePair<string, string>> header)
		{
			var builder = new StringBuilder();
			builder.Append("# ferrysim-csv: ").Append(TelemetryColumns.FormatVersion).Append('\n');
			foreach (var pair in header)
			{
				builder.Append("# ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
			}

			builder.Append(string.Join(",", TelemetryColumns.Names)).Append('\n');
			foreach (var line in _lines)
			{
				builder.Append(line).Append('\n');
			}

			return builder.ToString();
		}

		public void Write(string path, IEnumerable<KeyValuePair<string, string>> header)
		{
			var directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(path, Render(header), new UTF8Encoding(false));
		}

		private static string GearName(GearState gear) => gear.ToString().ToLowerInvariant();

		private static void Append(StringBuilder builder, double value, string format, bool last = false)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new CliException("The model produced a non-finite value; refusing to write it to the CSV.");
			}

			builder.Append(value.ToString(format, CultureInfo.InvariantCulture));
			if (!last)
			{
				builder.Append(',');
			}
		}
	}

	/// <summary>A telemetry CSV read back for summarizing and plotting.</summary>
	public sealed class TelemetryTable
	{
		private readonly Dictionary<string, double[]> _columns;

		private TelemetryTable(Dictionary<string, string> header, Dictionary<string, double[]> columns, int rows)
		{
			Header = header;
			_columns = columns;
			RowCount = rows;
		}

		public IReadOnlyDictionary<string, string> Header { get; }

		public int RowCount { get; }

		public double[] Time => Column("time_s");

		public double[] North => Column("north_m");

		public double[] East => Column("east_m");

		public double[] HeadingDeg => Column("heading_deg");

		public double[] YawRateDegps => Column("yaw_rate_degps");

		public double[] StwKn => Column("stw_kn");

		public double[] PortLever => Column("port_lever");

		public double[] StarboardLever => Column("starboard_lever");

		public double[] Helm => Column("helm");

		public double[] Column(string name) =>
			_columns.TryGetValue(name, out var values) ? values : throw new CliException($"CSV has no '{name}' column.");

		public string HeaderValue(string key, string fallback = null) => Header.TryGetValue(key, out var value) ? value : fallback;

		public double HeaderDouble(string key, double fallback)
		{
			if (Header.TryGetValue(key, out var text) &&
				double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
			{
				return value;
			}

			return fallback;
		}

		public static TelemetryTable Load(string path)
		{
			if (!File.Exists(path))
			{
				throw new CliException($"CSV file not found: {path}");
			}

			return Parse(File.ReadAllLines(path), path);
		}

		public static TelemetryTable Parse(string[] lines, string label = "CSV")
		{
			var header = new Dictionary<string, string>(StringComparer.Ordinal);
			var index = 0;
			for (; index < lines.Length && lines[index].StartsWith("#", StringComparison.Ordinal); index++)
			{
				var comment = lines[index].Substring(1).Trim();
				var colon = comment.IndexOf(':');
				if (colon > 0)
				{
					header[comment.Substring(0, colon).Trim()] = comment.Substring(colon + 1).Trim();
				}
			}

			if (!header.TryGetValue("ferrysim-csv", out var version))
			{
				throw new CliException($"{label}: not a ferrysim telemetry CSV (missing '# ferrysim-csv:' header comment).");
			}

			if (version != TelemetryColumns.FormatVersion)
			{
				throw new CliException($"{label}: unsupported ferrysim-csv version {version}.");
			}

			if (index >= lines.Length)
			{
				throw new CliException($"{label}: no column row after the header comment.");
			}

			var names = lines[index++].Split(',');
			foreach (var required in TelemetryColumns.Names)
			{
				if (Array.IndexOf(names, required) < 0)
				{
					throw new CliException($"{label}: missing column '{required}'.");
				}
			}

			var data = new List<double>[names.Length];
			for (var c = 0; c < names.Length; c++)
			{
				data[c] = new List<double>();
			}

			var rows = 0;
			for (; index < lines.Length; index++)
			{
				if (string.IsNullOrWhiteSpace(lines[index]))
				{
					continue;
				}

				var cells = lines[index].Split(',');
				if (cells.Length != names.Length)
				{
					throw new CliException($"{label}: line {index + 1} has {cells.Length} cells, expected {names.Length}.");
				}

				for (var c = 0; c < names.Length; c++)
				{
					if (TelemetryColumns.IsText(names[c]))
					{
						continue;
					}

					if (!double.TryParse(cells[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
					{
						throw new CliException($"{label}: line {index + 1}, column {names[c]}: '{cells[c]}' is not a number.");
					}

					data[c].Add(value);
				}

				rows++;
			}

			var columns = new Dictionary<string, double[]>(StringComparer.Ordinal);
			for (var c = 0; c < names.Length; c++)
			{
				if (!TelemetryColumns.IsText(names[c]))
				{
					columns[names[c]] = data[c].ToArray();
				}
			}

			return new TelemetryTable(header, columns, rows);
		}
	}
}
