using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FerrySim.Core.Traffic
{
	/// <summary>One row of a traffic event log, as read back.</summary>
	public sealed class TrafficEventRecord
	{
		public TrafficEventKind Kind { get; internal set; }

		/// <summary>Physics steps since the run started: the event is at the state after them.</summary>
		public long Step { get; internal set; }

		public double Time { get; internal set; }

		public int Mmsi { get; internal set; }

		/// <summary>The episode's first step and its last step with the condition (the event's own step for a start).</summary>
		public long StartStep { get; internal set; }

		public long LastStep { get; internal set; }

		public double Range { get; internal set; }

		public double Bearing { get; internal set; }

		public double Cpa { get; internal set; }

		public double Tcpa { get; internal set; }

		public double RelativeSpeed { get; internal set; }

		public double MinimumRange { get; internal set; }

		public double MinimumCpa { get; internal set; }

		/// <summary>The pack's class name, for example <c>tug-tow</c>.</summary>
		public string ClassName { get; internal set; }

		/// <summary>The hull the simulator gave the vessel, m.</summary>
		public double Length { get; internal set; }

		public double Beam { get; internal set; }

		/// <summary>The AIS name, empty when AIS gave none.</summary>
		public string Name { get; internal set; }

		/// <summary>The row exactly as written.</summary>
		public string Line { get; internal set; }
	}

	/// <summary>A traffic event log read back: its header, its event rows, its open-episode snapshot and its closing lines.</summary>
	public sealed class TrafficEventLogFile
	{
		internal TrafficEventLogFile(Dictionary<string, string> header, List<TrafficEventRecord> records, List<TrafficEventRecord> open, Dictionary<string, string> trailer)
		{
			Header = header;
			Records = records;
			Open = open;
			Trailer = trailer;
		}

		/// <summary>The <c># key: value</c> lines before the rows.</summary>
		public IReadOnlyDictionary<string, string> Header { get; }

		/// <summary>The events, in the order they happened (no snapshot rows).</summary>
		public IReadOnlyList<TrafficEventRecord> Records { get; }

		/// <summary>
		/// The end-of-recording snapshot: one row per episode still open when the recording ended (<c>alarm-open</c>,
		/// <c>collision-open</c>, <c>alongside-open</c>), with its least range and CPA so far. Empty for a log without one.
		/// </summary>
		public IReadOnlyList<TrafficEventRecord> Open { get; }

		/// <summary>
		/// The <c># key: value</c> lines after the rows (<c>events</c>, <c>open_episodes</c>, <c>steps</c>,
		/// <c>encounter_digest</c>); empty for a log cut short.
		/// </summary>
		public IReadOnlyDictionary<string, string> Trailer { get; }

		/// <summary>
		/// True when the recorder wrote the open-episode snapshot (its closing lines count it, even as 0); false for a log cut
		/// short and for logs written before the snapshot existed.
		/// </summary>
		public bool HasOpenSnapshot => Trailer.ContainsKey(TrafficEventLog.OpenKey);

		/// <summary>The recorded encounter digest, or null for a log cut short.</summary>
		public string Digest => Trailer.TryGetValue(TrafficEventLog.DigestKey, out var digest) ? digest : null;
	}

	/// <summary>
	/// The traffic event log a run with traffic records beside its telemetry (<c>&lt;run&gt;.traffic.csv</c>, Sim/README.md):
	/// <c># key: value</c> header lines (format, pack, digest, the vessel left out, the alarm limits), the column row, one row per
	/// <see cref="TrafficEvent"/> in the order they happened, every number at full precision, then the snapshot of the episodes
	/// still open when the recording ended (<see cref="TrafficMonitor.ReportOpen"/>), and closing lines with the event count, the
	/// open-episode count, the step count and the monitor's encounter digest. A replay compares its own rows with these character
	/// for character. LF line ends, UTF-8, invariant culture.
	/// </summary>
	public static class TrafficEventLog
	{
		public const string FormatKey = "ferrysim-traffic-events";

		public const string FormatVersion = "1";

		/// <summary>The log's file name ends in this, after the run's name.</summary>
		public const string Suffix = ".traffic.csv";

		public const string EventsKey = "events";

		/// <summary>The closing line that counts the open-episode snapshot's rows.</summary>
		public const string OpenKey = "open_episodes";

		public const string StepsKey = "steps";

		public const string DigestKey = "encounter_digest";

		public const string Columns = "step,time_s,event,mmsi,start_step,last_step,range_m,bearing_rad,cpa_m,tcpa_s,relative_speed_mps,min_range_m,min_cpa_m,class,length_m,beam_m,name";

		private static readonly string[] Kinds = { "alarm", "alarm-end", "collision", "collision-end", "alongside", "alongside-end", "alarm-open", "collision-open", "alongside-open" };

		/// <summary>
		/// The event's name in the log: <c>alarm</c>, <c>alarm-end</c>, <c>collision</c>, <c>collision-end</c>, <c>alongside</c> or
		/// <c>alongside-end</c>; the snapshot's <c>alarm-open</c>, <c>collision-open</c> or <c>alongside-open</c>.
		/// </summary>
		public static string KindName(TrafficEventKind kind) => Kinds[(int)kind];

		/// <summary>The header lines, the given pairs after the format line, then the column row.</summary>
		public static string FormatHeader(IEnumerable<KeyValuePair<string, string>> header)
		{
			var builder = new StringBuilder();
			builder.Append("# ").Append(FormatKey).Append(": ").Append(FormatVersion).Append('\n');
			foreach (var pair in header)
			{
				builder.Append("# ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
			}

			builder.Append(Columns).Append('\n');
			return builder.ToString();
		}

		/// <summary>The header's limit lines for <paramref name="limits"/>.</summary>
		public static List<KeyValuePair<string, string>> LimitsHeader(TrafficAlarmLimits limits) => new List<KeyValuePair<string, string>>
		{
			new KeyValuePair<string, string>("cpa_alarm_m", Number(limits.Cpa)),
			new KeyValuePair<string, string>("tcpa_alarm_s", Number(limits.Tcpa)),
			new KeyValuePair<string, string>("closing_speed_mps", Number(limits.ClosingSpeed)),
			new KeyValuePair<string, string>("alarm_clear_hold_s", Number(limits.ClearHold)),
			new KeyValuePair<string, string>("collision_hold_s", Number(limits.CollisionHold)),
			new KeyValuePair<string, string>("alongside_margin_m", Number(limits.AlongsideMargin)),
		};

		/// <summary>One event's row, without its line end.</summary>
		public static string FormatRow(in TrafficEvent e, TrafficVessel vessel)
		{
			var builder = new StringBuilder(160);
			builder.Append(e.Step.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(Number(e.Time)).Append(',');
			builder.Append(KindName(e.Kind)).Append(',');
			builder.Append(e.Mmsi.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(e.StartStep.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(e.LastStep.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(Number(e.Range)).Append(',');
			builder.Append(Number(e.Bearing)).Append(',');
			builder.Append(Number(e.Cpa)).Append(',');
			builder.Append(Number(e.Tcpa)).Append(',');
			builder.Append(Number(e.RelativeSpeed)).Append(',');
			builder.Append(Number(e.MinimumRange)).Append(',');
			builder.Append(Number(e.MinimumCpa)).Append(',');
			builder.Append(vessel != null ? TrafficClasses.Name(vessel.Class) : string.Empty).Append(',');
			builder.Append(vessel != null ? Number(vessel.Hull.Length) : string.Empty).Append(',');
			builder.Append(vessel != null ? Number(vessel.Hull.Beam) : string.Empty).Append(',');
			builder.Append(Quote(vessel?.Name ?? string.Empty));
			return builder.ToString();
		}

		/// <summary>The closing lines: the event count, the open-episode count, the step count and the encounter digest.</summary>
		public static string FormatTrailer(long events, long openEpisodes, long steps, string digest) =>
			"# " + EventsKey + ": " + events.ToString(CultureInfo.InvariantCulture) + "\n" +
			"# " + OpenKey + ": " + openEpisodes.ToString(CultureInfo.InvariantCulture) + "\n" +
			"# " + StepsKey + ": " + steps.ToString(CultureInfo.InvariantCulture) + "\n" +
			"# " + DigestKey + ": " + digest + "\n";

		/// <summary>
		/// The closing lines of a log from before the open-episode snapshot: the event count, the step count and the encounter
		/// digest.
		/// </summary>
		public static string FormatTrailer(long events, long steps, string digest) =>
			"# " + EventsKey + ": " + events.ToString(CultureInfo.InvariantCulture) + "\n" +
			"# " + StepsKey + ": " + steps.ToString(CultureInfo.InvariantCulture) + "\n" +
			"# " + DigestKey + ": " + digest + "\n";

		/// <summary>
		/// The shortest text that parses back to exactly <paramref name="value"/> (−0 written as 0, NaN as <c>nan</c>), as the
		/// recordings' control traces write numbers.
		/// </summary>
		public static string Number(double value)
		{
			if (double.IsNaN(value))
			{
				return "nan";
			}

			value += 0.0;
			var text = value.ToString("R", CultureInfo.InvariantCulture);
			return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).Equals(value) ? text : value.ToString("G17", CultureInfo.InvariantCulture);
		}

		/// <summary>Reads a log's lines. Throws <see cref="FormatException"/>, naming <paramref name="label"/>, for anything malformed.</summary>
		public static TrafficEventLogFile Parse(IReadOnlyList<string> lines, string label)
		{
			if (lines == null)
			{
				throw new ArgumentNullException(nameof(lines));
			}

			var header = new Dictionary<string, string>(StringComparer.Ordinal);
			var trailer = new Dictionary<string, string>(StringComparer.Ordinal);
			var records = new List<TrafficEventRecord>();
			var open = new List<TrafficEventRecord>();
			var index = 0;
			for (; index < lines.Count && lines[index].StartsWith("#", StringComparison.Ordinal); index++)
			{
				Comment(lines[index], header);
			}

			if (!header.TryGetValue(FormatKey, out var version) || version != FormatVersion)
			{
				throw new FormatException($"{label}: not a traffic event log (no '# {FormatKey}: {FormatVersion}' line).");
			}

			if (index >= lines.Count || lines[index] != Columns)
			{
				throw new FormatException($"{label}: the column row must be '{Columns}'.");
			}

			for (index++; index < lines.Count; index++)
			{
				var line = lines[index];
				if (line.Length == 0)
				{
					continue;
				}

				if (line.StartsWith("#", StringComparison.Ordinal))
				{
					Comment(line, trailer);
					continue;
				}

				if (trailer.Count > 0)
				{
					throw new FormatException($"{label}: line {index + 1} follows the closing lines.");
				}

				var row = Row(line, label, index + 1);
				if (IsOpen(row.Kind))
				{
					open.Add(row);
				}
				else if (open.Count > 0)
				{
					throw new FormatException($"{label}: line {index + 1} is an event after the open episodes; the snapshot comes last.");
				}
				else
				{
					records.Add(row);
				}
			}

			return new TrafficEventLogFile(header, records, open, trailer);
		}

		private static void Comment(string line, Dictionary<string, string> into)
		{
			var text = line.Substring(1).Trim();
			var colon = text.IndexOf(':');
			if (colon > 0)
			{
				into[text.Substring(0, colon).Trim()] = text.Substring(colon + 1).Trim();
			}
		}

		private static TrafficEventRecord Row(string line, string label, int number)
		{
			var fields = Split(line, label, number);
			if (fields.Count != 17)
			{
				throw new FormatException($"{label}: line {number} has {fields.Count} fields; a traffic event row has 17.");
			}

			var kind = Array.IndexOf(Kinds, fields[2]);
			if (kind < 0)
			{
				throw new FormatException($"{label}: line {number} has event '{fields[2]}'; it must be alarm, alarm-end, collision, collision-end, alongside, alongside-end, alarm-open, collision-open or alongside-open.");
			}

			return new TrafficEventRecord
			{
				Line = line,
				Step = Long(fields[0], "step", label, number),
				Time = Double(fields[1], "time_s", label, number),
				Kind = (TrafficEventKind)kind,
				Mmsi = (int)Long(fields[3], "mmsi", label, number),
				StartStep = Long(fields[4], "start_step", label, number),
				LastStep = Long(fields[5], "last_step", label, number),
				Range = Double(fields[6], "range_m", label, number),
				Bearing = Double(fields[7], "bearing_rad", label, number),
				Cpa = Double(fields[8], "cpa_m", label, number),
				Tcpa = Double(fields[9], "tcpa_s", label, number),
				RelativeSpeed = Double(fields[10], "relative_speed_mps", label, number),
				MinimumRange = Double(fields[11], "min_range_m", label, number),
				MinimumCpa = Double(fields[12], "min_cpa_m", label, number),
				ClassName = fields[13],
				Length = fields[14].Length == 0 ? double.NaN : Double(fields[14], "length_m", label, number),
				Beam = fields[15].Length == 0 ? double.NaN : Double(fields[15], "beam_m", label, number),
				Name = fields[16],
			};
		}

		private static bool IsOpen(TrafficEventKind kind) => kind == TrafficEventKind.AlarmOpen || kind == TrafficEventKind.CollisionOpen || kind == TrafficEventKind.AlongsideOpen;

		private static long Long(string text, string field, string label, int number) =>
			long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
				? value
				: throw new FormatException($"{label}: line {number} {field} '{text}' is not a whole number.");

		private static double Double(string text, string field, string label, int number)
		{
			if (text == "nan")
			{
				return double.NaN;
			}

			return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
				? value
				: throw new FormatException($"{label}: line {number} {field} '{text}' is not a number.");
		}

		/// <summary>A name in CSV: quoted (quotes doubled) when it has a comma, a quote or spaces at its ends.</summary>
		private static string Quote(string text)
		{
			if (text.IndexOf(',') < 0 && text.IndexOf('"') < 0 && text.Trim().Length == text.Length)
			{
				return text;
			}

			return "\"" + text.Replace("\"", "\"\"") + "\"";
		}

		private static List<string> Split(string line, string label, int number)
		{
			var fields = new List<string>();
			var field = new StringBuilder();
			var quoted = false;
			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];
				if (quoted)
				{
					if (c == '"')
					{
						if (i + 1 < line.Length && line[i + 1] == '"')
						{
							field.Append('"');
							i++;
						}
						else
						{
							quoted = false;
						}
					}
					else
					{
						field.Append(c);
					}
				}
				else if (c == '"' && field.Length == 0)
				{
					quoted = true;
				}
				else if (c == ',')
				{
					fields.Add(field.ToString());
					field.Clear();
				}
				else
				{
					field.Append(c);
				}
			}

			if (quoted)
			{
				throw new FormatException($"{label}: line {number} has an unclosed quote.");
			}

			fields.Add(field.ToString());
			return fields;
		}
	}
}
