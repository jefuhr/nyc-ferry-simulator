using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using Unity.Profiling;

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
		private static readonly ProfilerMarker RowMarker = new ProfilerMarker("FerrySim.TelemetryCsv.FormatRow");
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

		public static string NfuColumnRow => ColumnRow + ",nfu_jog";

		/// <summary>The <c>#</c> header comment block and the column row, each line ending in <c>\n</c>.</summary>
		public static string FormatHeader(IEnumerable<KeyValuePair<string, string>> header, bool includeJog = false)
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

			builder.Append(includeJog ? NfuColumnRow : ColumnRow).Append('\n');
			return builder.ToString();
		}

		/// <summary>Writes the same row through caller-owned storage, including its newline.</summary>
		public static void WriteRow(TextWriter writer, char[] buffer, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls, bool includeJog = false)
		{
			using var profile = RowMarker.Auto();
			var row = new RowBuffer(buffer);
			var port = telemetry.Port;
			var starboard = telemetry.Starboard;
			row.Fixed(state.Time, "F3");
			row.Fixed(state.North, "F3");
			row.Fixed(state.East, "F3");
			row.Fixed(Angles.ToDegrees(state.Heading), "F4");
			row.Fixed(state.Surge, "F4");
			row.Fixed(state.Sway, "F4");
			row.Fixed(Angles.ToDegrees(state.YawRate), "F4");
			row.Fixed(Units.MetresPerSecondToKnots(telemetry.SpeedOverGround), "F4");
			row.Fixed(Angles.ToDegrees(Angles.WrapTwoPi(telemetry.CourseOverGround)), "F3");
			row.Fixed(Units.MetresPerSecondToKnots(telemetry.SpeedThroughWater), "F4");
			row.Fixed(Units.RadiansPerSecondToRpm(port.EngineSpeed), "F2");
			row.Fixed(Units.RadiansPerSecondToRpm(starboard.EngineSpeed), "F2");
			row.Fixed(Units.RadiansPerSecondToRpm(port.ShaftSpeed), "F2");
			row.Fixed(Units.RadiansPerSecondToRpm(starboard.ShaftSpeed), "F2");
			row.Gear(port.Gear); row.Gear(starboard.Gear);
			row.Fixed(Angles.ToDegrees(telemetry.PortRudderAngle), "F3");
			row.Fixed(Angles.ToDegrees(telemetry.StarboardRudderAngle), "F3");
			row.Fixed(telemetry.DepthUnderKeel, "F3");
			row.Fixed(telemetry.ContactForce, "F1");
			row.Fixed(controls.PortLever, "F4");
			row.Fixed(controls.StarboardLever, "F4");
			row.Fixed(controls.Helm, "F4", includeJog);
			if (includeJog) row.Fixed(controls.Jog, "F4", false);
			row.Write(writer);
		}

		/// <summary>ControlTrace's exact formats, without temporary per-value strings.</summary>
		public static void WriteTraceRow(TextWriter writer, char[] buffer, long step, in ControlInputs controls, bool includeJog = false)
		{
			var row = new RowBuffer(buffer);
			row.Number(SimulationClock.TimeAtStep(step), "0.00"); row.Character(',');
			row.RoundTrip(controls.PortLever);
			row.RoundTrip(controls.StarboardLever);
			if (!includeJog && controls.Jog != 0.0) throw new InvalidOperationException("A four-column control trace cannot store non-zero jog.");
			row.RoundTrip(controls.Helm, includeJog);
			if (includeJog) row.RoundTrip(controls.Jog, false);
			row.Write(writer);
		}

		/// <summary>TrafficEventLog's exact fields and quoting, through reusable character storage.</summary>
		public static void WriteTrafficRow(TextWriter writer, char[] buffer, in TrafficEvent trafficEvent, TrafficVessel vessel)
		{
			var row = new RowBuffer(buffer);
			row.Integer(trafficEvent.Step);
			row.RoundTrip(trafficEvent.Time, lowerNaN: true);
			row.Text(TrafficEventLog.KindName(trafficEvent.Kind)); row.Character(',');
			row.Integer(trafficEvent.Mmsi); row.Integer(trafficEvent.StartStep); row.Integer(trafficEvent.LastStep);
			row.RoundTrip(trafficEvent.Range, lowerNaN: true);
			row.RoundTrip(trafficEvent.Bearing, lowerNaN: true);
			row.RoundTrip(trafficEvent.Cpa, lowerNaN: true);
			row.RoundTrip(trafficEvent.Tcpa, lowerNaN: true);
			row.RoundTrip(trafficEvent.RelativeSpeed, lowerNaN: true);
			row.RoundTrip(trafficEvent.MinimumRange, lowerNaN: true);
			row.RoundTrip(trafficEvent.MinimumCpa, lowerNaN: true);
			if (vessel != null) row.Text(TrafficClasses.Name(vessel.Class));
			row.Character(',');
			if (vessel != null)
			{
				row.RoundTrip(vessel.Hull.Length, lowerNaN: true);
				row.RoundTrip(vessel.Hull.Beam, lowerNaN: true);
			}
			else { row.Character(','); row.Character(','); }
			row.Quoted(vessel?.Name ?? string.Empty);
			row.Write(writer);
		}

		/// <summary>
		/// One data row (no line ending): the state and telemetry at this instant and the controls that start
		/// applying now, formatted exactly as the CLI's <c>CsvRecorder.Add</c> does.
		/// </summary>
		public static string FormatRow(in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls, bool includeJog = false)
		{
			using var profile = RowMarker.Auto();
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
			Append(builder, controls.Helm, "F4", last: !includeJog);
			if (includeJog) Append(builder, controls.Jog, "F4", last: true);
			return builder.ToString();
		}

		/// <summary>Header values as the CLI writes them: up to six decimals, no trailing zeros.</summary>
		public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

		public static string GearName(GearState gear) => gear.ToString().ToLowerInvariant();

		/// <summary>Splits a telemetry CSV into its header comments and data lines, checking the format version and columns.</summary>
		public static void Split(IReadOnlyList<string> lines, string label, out Dictionary<string, string> header, out List<string> rows) =>
			Split(lines, label, out header, out rows, out _);

		public static void Split(IReadOnlyList<string> lines, string label, out Dictionary<string, string> header, out List<string> rows, out bool hasJog)
		{
			hasJog = false;
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

			if (index >= lines.Count || (lines[index] != ColumnRow && lines[index] != NfuColumnRow))
			{
				throw new FormatException($"{label}: expected the column row '{ColumnRow}' after the header comments.");
			}

			hasJog = lines[index] == NfuColumnRow;
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

		private struct RowBuffer
		{
			private readonly char[] _characters;
			private int _length;
			public RowBuffer(char[] characters)
			{
				_characters = characters ?? throw new ArgumentNullException(nameof(characters));
				_length = 0;
			}
			public void Character(char value)
			{
				Require(1); _characters[_length++] = value;
			}
			public void Text(string value)
			{
				Require(value.Length); value.CopyTo(0, _characters, _length, value.Length); _length += value.Length;
			}
			public void Number(double value, string format)
			{
				if (!value.TryFormat(_characters.AsSpan(_length), out var count, format.AsSpan(), CultureInfo.InvariantCulture))
					throw new ArgumentException("The reusable recording row buffer is too small.");
				_length += count;
			}
			public void Fixed(double value, string format, bool comma = true)
			{
				if (double.IsNaN(value) || double.IsInfinity(value))
					throw new InvalidOperationException("The model produced a non-finite value; refusing to write it to the CSV.");
				var start = _length;
				Number(value, format);
				if (BitConverter.DoubleToInt64Bits(value) < 0 && _characters[start] != '-')
				{
					Require(1); Array.Copy(_characters, start, _characters, start + 1, _length - start);
					_characters[start] = '-'; _length++;
				}
				if (comma) Character(',');
			}
			public void RoundTrip(double value, bool comma = true, bool lowerNaN = false)
			{
				if (lowerNaN && double.IsNaN(value)) Text("nan");
				else
				{
					value += 0.0;
					var start = _length;
					Number(value, "R");
					if (!double.Parse(_characters.AsSpan(start, _length - start), NumberStyles.Float, CultureInfo.InvariantCulture).Equals(value))
					{
						_length = start; Number(value, "G17");
					}
				}
				if (comma) Character(',');
			}
			public void Integer(long value)
			{
				if (!value.TryFormat(_characters.AsSpan(_length), out var count, default, CultureInfo.InvariantCulture))
					throw new ArgumentException("The reusable recording row buffer is too small.");
				_length += count; Character(',');
			}
			public void Gear(GearState value)
			{
				switch (value)
				{
					case GearState.Neutral: Text("neutral"); break;
					case GearState.Ahead: Text("ahead"); break;
					case GearState.Astern: Text("astern"); break;
					case GearState.Shifting: Text("shifting"); break;
					default: Integer((int)value); return;
				}
				Character(',');
			}
			public void Quoted(string text)
			{
				var quote = text.IndexOf(',') >= 0 || text.IndexOf('"') >= 0 ||
					(text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])));
				if (!quote) { Text(text); return; }
				Character('"');
				foreach (var character in text)
				{
					Character(character);
					if (character == '"') Character('"');
				}
				Character('"');
			}
			public void Write(TextWriter writer)
			{
				Character('\n'); writer.Write(_characters, 0, _length);
			}
			private void Require(int count)
			{
				if (count > _characters.Length - _length)
					throw new ArgumentException("The reusable recording row buffer is too small.");
			}
		}
	}
}
