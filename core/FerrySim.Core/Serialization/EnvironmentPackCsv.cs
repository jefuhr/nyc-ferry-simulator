using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FerrySim.Core.Env;

namespace FerrySim.Core.Serialization
{
	/// <summary>
	/// The series files of an environment pack (ferrysim-env/1): UTF-8, comma-separated, a header row, <c>\n</c> line
	/// ends, rows grouped by station with strictly increasing <c>t_s</c>. Numbers are parsed with the invariant culture
	/// and must be finite; a gap is a missing row, never a placeholder.
	/// </summary>
	internal static class EnvironmentPackCsv
	{
		public const string CurrentsHeader = "station_id,t_s,north_mps,east_mps";
		public const string WaterLevelsHeader = "station_id,t_s,level_navd88_m,source";
		public const string WindHeader = "station_id,t_s,north_mps,east_mps,gust_mps";

		private const NumberStyles Number = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

		/// <summary>One station's rows from a series file.</summary>
		public sealed class Rows
		{
			public readonly List<double> Times = new List<double>();
			public readonly List<double> First = new List<double>();
			public readonly List<double> Second = new List<double>();
			public readonly List<double> Third = new List<double>();
			public readonly List<bool> Flags = new List<bool>();
		}

		/// <summary>
		/// Rows per station of <paramref name="stationIds"/>, in file order. Every listed station must have rows and every
		/// row's station must be listed.
		/// </summary>
		public static Dictionary<string, Rows> Read(string path, byte[] bytes, string header, IReadOnlyList<string> stationIds)
		{
			string text;
			try
			{
				text = new UTF8Encoding(false, true).GetString(bytes);
			}
			catch (DecoderFallbackException)
			{
				throw new EnvironmentPackException(path, "is not valid UTF-8.");
			}

			if (text.IndexOf('\r') >= 0)
			{
				throw new EnvironmentPackException(path, "has a carriage return; lines must end with \\n only.");
			}

			var lines = text.Split('\n');
			var count = lines.Length;
			if (count > 0 && lines[count - 1].Length == 0)
			{
				count--;
			}

			if (count == 0 || lines[0] != header)
			{
				throw new EnvironmentPackException(path, $"the header must be exactly '{header}'.");
			}

			var columns = header.Split(',').Length;
			var rows = new Dictionary<string, Rows>(StringComparer.Ordinal);
			foreach (var id in stationIds)
			{
				rows[id] = new Rows();
			}

			var finished = new HashSet<string>(StringComparer.Ordinal);
			string current = null;
			for (var i = 1; i < count; i++)
			{
				var line = i + 1;
				var fields = lines[i].Split(',');
				if (fields.Length != columns)
				{
					throw new EnvironmentPackException(path, $"line {line} has {fields.Length} fields; the header has {columns}.");
				}

				var id = fields[0];
				if (!rows.TryGetValue(id, out var station))
				{
					throw new EnvironmentPackException(path, $"line {line}: station '{id}' is not in the manifest.");
				}

				if (id != current)
				{
					if (current != null)
					{
						finished.Add(current);
					}

					if (finished.Contains(id))
					{
						throw new EnvironmentPackException(path, $"line {line}: station '{id}' appears again after other stations; rows must be grouped by station.");
					}

					current = id;
				}

				var time = Parse(path, line, "t_s", fields[1]);
				if (station.Times.Count > 0 && !(time > station.Times[station.Times.Count - 1]))
				{
					throw new EnvironmentPackException(path, $"line {line}: t_s {fields[1]} does not increase for station '{id}'.");
				}

				station.Times.Add(time);
				if (header == WaterLevelsHeader)
				{
					station.First.Add(Parse(path, line, "level_navd88_m", fields[2]));
					station.Flags.Add(fields[3] == "predicted" ? true :
						fields[3] == "observed" ? false : throw new EnvironmentPackException(path, $"line {line}: source must be observed or predicted, found '{fields[3]}'."));
				}
				else
				{
					station.First.Add(Parse(path, line, "north_mps", fields[2]));
					station.Second.Add(Parse(path, line, "east_mps", fields[3]));
					if (header == WindHeader)
					{
						var gust = fields[4].Length == 0 ? double.NaN : Parse(path, line, "gust_mps", fields[4]);
						if (gust < 0.0)
						{
							throw new EnvironmentPackException(path, $"line {line}: gust_mps is negative.");
						}

						station.Third.Add(gust);
					}
				}
			}

			foreach (var id in stationIds)
			{
				if (rows[id].Times.Count == 0)
				{
					throw new EnvironmentPackException(path, $"station '{id}' is in the manifest but has no rows.");
				}
			}

			return rows;
		}

		public static byte[] WriteCurrents(IReadOnlyList<CurrentStation> stations)
		{
			var builder = new StringBuilder(CurrentsHeader).Append('\n');
			foreach (var station in Sorted(stations))
			{
				var series = station.Velocity;
				for (var i = 0; i < series.Count; i++)
				{
					var v = series.Value(i);
					Row(builder, station.Id, series.Time(i)).Append(',').Append(Format(v.North)).Append(',').Append(Format(v.East)).Append('\n');
				}
			}

			return Encoding.UTF8.GetBytes(builder.ToString());
		}

		public static byte[] WriteWaterLevels(IReadOnlyList<WaterLevelStation> stations)
		{
			var builder = new StringBuilder(WaterLevelsHeader).Append('\n');
			foreach (var station in Sorted(stations))
			{
				var series = station.Level;
				for (var i = 0; i < series.Count; i++)
				{
					Row(builder, station.Id, series.Time(i)).Append(',').Append(Format(series.Value(i))).Append(',')
						.Append(station.IsPredicted(i) ? "predicted" : "observed").Append('\n');
				}
			}

			return Encoding.UTF8.GetBytes(builder.ToString());
		}

		public static byte[] WriteWind(IReadOnlyList<WindStation> stations)
		{
			var builder = new StringBuilder(WindHeader).Append('\n');
			foreach (var station in Sorted(stations))
			{
				var series = station.Velocity;
				for (var i = 0; i < series.Count; i++)
				{
					var v = series.Value(i);
					var gust = station.Gust(i);
					Row(builder, station.Id, series.Time(i)).Append(',').Append(Format(v.North)).Append(',').Append(Format(v.East)).Append(',')
						.Append(double.IsNaN(gust) ? string.Empty : Format(gust)).Append('\n');
				}
			}

			return Encoding.UTF8.GetBytes(builder.ToString());
		}

		private static StringBuilder Row(StringBuilder builder, string id, double time) => builder.Append(id).Append(',').Append(Format(time));

		private static string Format(double value) => EnvironmentPackDigests.PythonFloat(value);

		private static List<T> Sorted<T>(IReadOnlyList<T> stations) where T : EnvironmentStation
		{
			var sorted = new List<T>(stations);
			sorted.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
			return sorted;
		}

		private static double Parse(string path, int line, string column, string text)
		{
			if (!double.TryParse(text, Number, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new EnvironmentPackException(path, $"line {line}: {column} '{text}' is not a finite number.");
			}

			return value;
		}
	}
}
