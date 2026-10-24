using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// The CLI's telemetry CSV schema (core/dotnet/FerrySim.Cli/TelemetryCsv.cs), reproduced here because
	/// Unity does not compile the CLI: the same <c># ferrysim-csv: 1</c> header comment, the same columns in
	/// the same order and the same number formats, so <c>ferrysim summarize</c> and
	/// <c>tools/plot_maneuver.py</c> read Unity runs unchanged. A test checks the columns against the CLI source.
	/// </summary>
	public static class TelemetryCsv
	{
		public const string FormatVersion = "1";

		public static readonly string[] ColumnNames =
		{
			"time_s", "north_m", "east_m", "heading_deg", "surge_mps", "sway_mps", "yaw_rate_degps",
			"sog_kn", "cog_deg", "stw_kn",
			"port_engine_rpm", "stbd_engine_rpm", "port_shaft_rpm", "stbd_shaft_rpm", "port_gear", "stbd_gear",
			"port_rudder_deg", "stbd_rudder_deg", "depth_under_keel_m", "contact_force_n",
			"port_lever", "starboard_lever", "helm",
		};

		public static string ColumnRow => string.Join(",", ColumnNames);

		/// <summary>The <c>#</c> header comment block and the column row, each line ending in <c>\n</c>.</summary>
		public static string FormatHeader(IEnumerable<KeyValuePair<string, string>> header)
		{
			var builder = new StringBuilder();
			builder.Append("# ferrysim-csv: ").Append(FormatVersion).Append('\n');
			if (header != null)
			{
				foreach (var pair in header)
				{
					builder.Append("# ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
				}
			}

			builder.Append(ColumnRow).Append('\n');
			return builder.ToString();
		}

		/// <summary>
		/// One data row (no line ending): the state and telemetry at this instant and the controls that start
		/// applying now, formatted exactly as the CLI's <c>CsvRecorder.Add</c> does.
		/// </summary>
		public static string FormatRow(in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
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
			return builder.ToString();
		}

		/// <summary>Header values as the CLI writes them: up to six decimals, no trailing zeros.</summary>
		public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

		public static string GearName(GearState gear) => gear.ToString().ToLowerInvariant();

		/// <summary>Splits a telemetry CSV into its header comments and data lines, checking the format version and columns.</summary>
		public static void Split(IReadOnlyList<string> lines, string label, out Dictionary<string, string> header, out List<string> rows)
		{
			header = new Dictionary<string, string>(StringComparer.Ordinal);
			rows = new List<string>();
			var index = 0;
			for (; index < lines.Count && lines[index].StartsWith("#", StringComparison.Ordinal); index++)
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
				throw new FormatException($"{label}: not a ferrysim telemetry CSV (missing '# ferrysim-csv:' header comment).");
			}

			if (version != FormatVersion)
			{
				throw new FormatException($"{label}: unsupported ferrysim-csv version {version}.");
			}

			if (index >= lines.Count || lines[index] != ColumnRow)
			{
				throw new FormatException($"{label}: expected the column row '{ColumnRow}' after the header comments.");
			}

			for (index++; index < lines.Count; index++)
			{
				if (!string.IsNullOrWhiteSpace(lines[index]))
				{
					rows.Add(lines[index]);
				}
			}
		}

		private static void Append(StringBuilder builder, double value, string format, bool last = false)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new InvalidOperationException("The model produced a non-finite value; refusing to write it to the CSV.");
			}

			var text = value.ToString(format, CultureInfo.InvariantCulture);
			// CoreCLR (the CLI) keeps the sign of -0.0 and of a negative value that rounds to zero ("-0.000");
			// Mono drops it. Keep it, so the same states give byte-identical files from both.
			if (BitConverter.DoubleToInt64Bits(value) < 0 && text[0] != '-')
			{
				builder.Append('-');
			}

			builder.Append(text);
			if (!last)
			{
				builder.Append(',');
			}
		}
	}
}
