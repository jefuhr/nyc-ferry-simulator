using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	public sealed class TelemetryCsvException : Exception
	{
		public TelemetryCsvException(string message)
			: base(message)
		{
		}
	}

	/// <summary>
	/// Reads the ferrysim telemetry CSV, format version 1, as written by the CLI
	/// (core/dotnet/FerrySim.Cli/TelemetryCsv.cs), by the Unity run recorder and by
	/// <see cref="TelemetryCsvWriter"/>: <c># key: value</c> header comments starting with
	/// <c># ferrysim-csv: 1</c>, a column row, then one row per sample. Columns are found by name, so extra
	/// columns are ignored except the optional <see cref="DepthAlarmThresholdColumn"/>; kn, deg and rpm
	/// columns are converted back to SI on load. Any malformed cell, and a malformed
	/// <see cref="ScenarioStartKey"/> header, is an error naming the file, never a silent 0 or default.
	/// </summary>
	public static class TelemetryCsv
	{
		public const string FormatVersion = "1";
		public const string FormatKey = "ferrysim-csv";

		/// <summary>Header holding the scenario's absolute start (ISO 8601 UTC), written by the Unity run recorder. CLI runs have none.</summary>
		public const string ScenarioStartKey = "scenario_start_utc";

		/// <summary>
		/// Optional column: the shallow-water alarm threshold in force at each sample, metres under the keel.
		/// When a recording carries it, the debrief replays the alarms the learner actually had.
		/// </summary>
		public const string DepthAlarmThresholdColumn = "depth_alarm_threshold_m";

		private static readonly string[] ScenarioStartFormats = { "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" };

		private static readonly string[] Names =
		{
			"time_s", "north_m", "east_m", "heading_deg", "surge_mps", "sway_mps", "yaw_rate_degps",
			"sog_kn", "cog_deg", "stw_kn",
			"port_engine_rpm", "stbd_engine_rpm", "port_shaft_rpm", "stbd_shaft_rpm", "port_gear", "stbd_gear",
			"port_rudder_deg", "stbd_rudder_deg", "depth_under_keel_m", "contact_force_n",
			"port_lever", "starboard_lever", "helm",
		};

		/// <summary>The required columns, in the order the CLI writes them.</summary>
		public static IReadOnlyList<string> Columns => Names;

		public static TelemetryRecording Load(string path)
		{
			if (!File.Exists(path))
			{
				throw new TelemetryCsvException($"Telemetry CSV not found: {path}");
			}

			return Parse(File.ReadAllText(path), Path.GetFileName(path));
		}

		public static TelemetryRecording Parse(string text, string label)
		{
			if (text == null)
			{
				throw new ArgumentNullException(nameof(text));
			}

			if (text.Length > 0 && text[0] == '﻿')
			{
				text = text.Substring(1);
			}

			var lines = text.Split('\n');
			var header = new List<KeyValuePair<string, string>>();
			var index = 0;
			for (; index < lines.Length; index++)
			{
				var line = lines[index].TrimEnd('\r');
				if (!line.StartsWith("#", StringComparison.Ordinal))
				{
					break;
				}

				var comment = line.Substring(1).Trim();
				var colon = comment.IndexOf(':');
				if (colon > 0)
				{
					header.Add(new KeyValuePair<string, string>(comment.Substring(0, colon).Trim(), comment.Substring(colon + 1).Trim()));
				}
			}

			var version = Find(header, FormatKey);
			if (version == null)
			{
				throw new TelemetryCsvException($"{label}: not a ferrysim telemetry CSV (missing the '# {FormatKey}:' header comment).");
			}

			if (version != FormatVersion)
			{
				throw new TelemetryCsvException($"{label}: unsupported {FormatKey} version {version} (this build reads version {FormatVersion}).");
			}

			var start = Find(header, ScenarioStartKey);
			if (start != null && !TryParseScenarioStart(start, out _))
			{
				throw new TelemetryCsvException($"{label}: header {ScenarioStartKey} '{start}' is not an ISO 8601 UTC time such as 2026-06-01T13:00:00Z.");
			}

			while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
			{
				index++;
			}

			if (index >= lines.Length)
			{
				throw new TelemetryCsvException($"{label}: no column row after the header comments.");
			}

			var names = lines[index].TrimEnd('\r').Split(',');
			index++;
			var column = new int[Names.Length];
			for (var c = 0; c < Names.Length; c++)
			{
				column[c] = Array.IndexOf(names, Names[c]);
				if (column[c] < 0)
				{
					throw new TelemetryCsvException($"{label}: missing column '{Names[c]}'.");
				}
			}

			var thresholdColumn = Array.IndexOf(names, DepthAlarmThresholdColumn);
			var builder = new TelemetryRecordingBuilder(label, header);
			for (; index < lines.Length; index++)
			{
				var line = lines[index].TrimEnd('\r');
				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}

				var cells = line.Split(',');
				var lineNumber = index + 1;
				if (cells.Length != names.Length)
				{
					throw new TelemetryCsvException($"{label}: line {lineNumber} has {cells.Length} cells, expected {names.Length}.");
				}

				var row = new Row(cells, column, label, lineNumber);
				var time = row.Number(0);
				if (builder.Count > 0 && !(time > builder.LastTime))
				{
					throw new TelemetryCsvException($"{label}: line {lineNumber}: time {cells[column[0]]} s does not follow the previous row; times must strictly increase.");
				}

				var state = new VesselState(
					time, row.Number(1), row.Number(2), 0.0, 0.0, 0.0, Angles.ToRadians(row.Number(3)),
					row.Number(4), row.Number(5), 0.0, 0.0, 0.0, Angles.ToRadians(row.Number(6)));
				var telemetry = new VesselTelemetry(
					new EngineTelemetry(Units.RpmToRadiansPerSecond(row.Number(10)), Units.RpmToRadiansPerSecond(row.Number(12)), row.Gear(14), double.NaN),
					new EngineTelemetry(Units.RpmToRadiansPerSecond(row.Number(11)), Units.RpmToRadiansPerSecond(row.Number(13)), row.Gear(15), double.NaN),
					Angles.ToRadians(row.Number(16)),
					Angles.ToRadians(row.Number(17)),
					Units.KnotsToMetresPerSecond(row.Number(9)),
					Units.KnotsToMetresPerSecond(row.Number(7)),
					Angles.ToRadians(row.Number(8)),
					row.Number(18),
					row.Number(19));
				var controls = new ControlInputs(row.Number(20), row.Number(21), row.Number(22));
				if (thresholdColumn >= 0)
				{
					builder.Add(state, telemetry, controls, row.Threshold(thresholdColumn));
				}
				else
				{
					builder.Add(state, telemetry, controls);
				}
			}

			if (builder.Count == 0)
			{
				throw new TelemetryCsvException($"{label}: no data rows.");
			}

			return builder.Build();
		}

		/// <summary>Lowercase gear name as the CLI writes it.</summary>
		public static string GearName(GearState gear) => gear.ToString().ToLowerInvariant();

		/// <summary>
		/// Parses a <see cref="ScenarioStartKey"/> value: ISO 8601 date and time with optional fraction, in UTC
		/// ("Z"), with an offset (converted to UTC) or with neither (taken as UTC, as the key says).
		/// </summary>
		public static bool TryParseScenarioStart(string text, out DateTimeOffset start) =>
			DateTimeOffset.TryParseExact(text?.Trim(), ScenarioStartFormats, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start);

		private static string Find(List<KeyValuePair<string, string>> header, string key)
		{
			foreach (var pair in header)
			{
				if (pair.Key == key)
				{
					return pair.Value;
				}
			}

			return null;
		}

		private readonly struct Row
		{
			private readonly string[] _cells;
			private readonly int[] _column;
			private readonly string _label;
			private readonly int _line;

			public Row(string[] cells, int[] column, string label, int line)
			{
				_cells = cells;
				_column = column;
				_label = label;
				_line = line;
			}

			public double Number(int required)
			{
				var cell = _cells[_column[required]];
				if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
					double.IsNaN(value) || double.IsInfinity(value))
				{
					throw new TelemetryCsvException($"{_label}: line {_line}, column {Names[required]}: '{cell}' is not a finite number.");
				}

				// Keep the sign of "-0.000" (a small negative value rounded by the CLI) so rows re-render identically.
				return value == 0.0 && cell.TrimStart().StartsWith("-", StringComparison.Ordinal) ? -0.0 : value;
			}

			/// <summary>The optional alarm threshold cell at <paramref name="cellIndex"/>: finite metres, 0 or more.</summary>
			public double Threshold(int cellIndex)
			{
				var cell = _cells[cellIndex];
				if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
					double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
				{
					throw new TelemetryCsvException($"{_label}: line {_line}, column {DepthAlarmThresholdColumn}: '{cell}' is not a threshold in metres (a finite number, 0 or more).");
				}

				return value;
			}

			public GearState Gear(int required)
			{
				var cell = _cells[_column[required]].Trim();
				switch (cell.ToLowerInvariant())
				{
					case "neutral":
						return GearState.Neutral;
					case "ahead":
						return GearState.Ahead;
					case "astern":
						return GearState.Astern;
					case "shifting":
						return GearState.Shifting;
					default:
						throw new TelemetryCsvException($"{_label}: line {_line}, column {Names[required]}: unknown gear '{cell}' (expected neutral, ahead, astern or shifting).");
				}
			}
		}
	}
}
