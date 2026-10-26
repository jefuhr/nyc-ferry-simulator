using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Traffic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Core.Serialization
{
	/// <summary>
	/// Writes a <see cref="TrafficPack"/> as a ferrysim-traffic/1 day folder that <see cref="TrafficPackJson.Load"/> accepts: the
	/// roster, the 44-byte records, the pipeline's layout file, and a manifest with the file inventory and the digest. For tests
	/// and synthetic scenario fixtures; real packs come from the data pipeline (<c>python -m ferrydata.traffic build</c>), whose
	/// provenance fields this writer marks as synthetic.
	/// </summary>
	public static class TrafficPackWriter
	{
		/// <summary>The pipeline's <c>reports.json</c>, byte for byte.</summary>
		public const string LayoutJson =
			"{\n\t\"byte_order\": \"little-endian\",\n\t\"record_size_bytes\": 44,\n\t\"fields\": [\n" +
			"\t\t{\n\t\t\t\"name\": \"vessel_index\",\n\t\t\t\"type\": \"int32\",\n\t\t\t\"offset_bytes\": 0\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"segment\",\n\t\t\t\"type\": \"int32\",\n\t\t\t\"offset_bytes\": 4\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"t_s\",\n\t\t\t\"type\": \"float64\",\n\t\t\t\"offset_bytes\": 8\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"north_m\",\n\t\t\t\"type\": \"float64\",\n\t\t\t\"offset_bytes\": 16\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"east_m\",\n\t\t\t\"type\": \"float64\",\n\t\t\t\"offset_bytes\": 24\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"sog_mps\",\n\t\t\t\"type\": \"float32\",\n\t\t\t\"offset_bytes\": 32\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"cog_rad\",\n\t\t\t\"type\": \"float32\",\n\t\t\t\"offset_bytes\": 36\n\t\t},\n" +
			"\t\t{\n\t\t\t\"name\": \"heading_rad\",\n\t\t\t\"type\": \"float32\",\n\t\t\t\"offset_bytes\": 40\n\t\t}\n\t],\n" +
			"\t\"missing_float\": \"IEEE 754 quiet NaN\",\n\t\"angles\": \"radians clockwise from true north\",\n" +
			"\t\"positions\": \"NED horizontal metres via ferrydata.geodesy.to_local at 40.7000 N, -74.0150 E\"\n}\n";

		/// <summary>Writes the pack into <paramref name="dayFolder"/> (created if needed) and returns the folder.</summary>
		public static string Write(TrafficPack pack, string dayFolder)
		{
			if (pack == null)
			{
				throw new ArgumentNullException(nameof(pack));
			}

			Directory.CreateDirectory(dayFolder);
			var files = new[] { Roster(pack), Records(pack.Reports), Encoding.UTF8.GetBytes(LayoutJson) };
			for (var i = 0; i < files.Length; i++)
			{
				File.WriteAllBytes(Path.Combine(dayFolder, TrafficPackJson.Files[i]), files[i]);
			}

			var epoch = pack.EpochUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
			var inventory = new JArray();
			for (var i = 0; i < files.Length; i++)
			{
				inventory.Add(new JObject { ["name"] = TrafficPackJson.Files[i], ["sha256"] = TrafficPackJson.Sha256Hex(files[i]), ["size_bytes"] = files[i].LongLength });
			}

			var byClass = new SortedDictionary<string, long>(StringComparer.Ordinal);
			foreach (var vessel in pack.Vessels)
			{
				var name = TrafficClasses.Name(vessel.Class);
				byClass[name] = byClass.TryGetValue(name, out var n) ? n + 1 : 1;
			}

			var classes = new JObject();
			foreach (var pair in byClass)
			{
				classes[pair.Key] = pair.Value;
			}

			var box = TrafficPackJson.HarborBox;
			var manifest = new JObject
			{
				["format"] = TrafficPack.Format,
				["date"] = pack.Date,
				["epoch_utc"] = epoch,
				["bbox"] = new JObject { ["south"] = box[0], ["north"] = box[1], ["west"] = box[2], ["east"] = box[3] },
				["source"] = new JObject { ["name"] = "synthetic", ["sha256"] = null },
				["source_cadence"] = "synthetic reports written by FerrySim.Core.Serialization.TrafficPackWriter; not AIS data",
				["sources"] = new JArray(),
				["time_coverage"] = new JObject
				{
					["requested_from_s"] = -3600.0, ["requested_to_s"] = 90000.0,
					["covered_from_s"] = double.IsNaN(pack.CoverageFrom) ? -3600.0 : pack.CoverageFrom,
					["covered_to_s"] = double.IsNaN(pack.CoverageTo) ? 90000.0 : pack.CoverageTo,
					["missing_intervals"] = new JArray(),
				},
				["drop_accounting"] = "synthetic: nothing dropped",
				["counts"] = new JObject
				{
					["vessels"] = pack.Vessels.Count, ["reports"] = pack.Reports.Count, ["source_reports"] = pack.Reports.Count,
					["dropped_reports"] = new JObject
					{
						["impossible_position"] = 0, ["outside_bbox"] = 0, ["invalid_mmsi"] = 0, ["invalid_time"] = 0, ["duplicate"] = 0, ["position_jump"] = 0, ["outside_time_window"] = 0,
					},
					["vessels_by_class"] = classes,
				},
				["cleaning"] = new JObject(),
				["class_mapping"] = new JObject(),
				["ferry_candidates"] = new JObject { ["source"] = "synthetic", ["sha256"] = null, ["subsets"] = new JArray("T", "K") },
				["files"] = inventory,
				["digest"] = TrafficPackJson.Digest(files, epoch),
			};
			using (var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" })
			using (var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented, Indentation = 1, IndentChar = '\t' })
			{
				manifest.WriteTo(json);
				json.Flush();
				File.WriteAllBytes(Path.Combine(dayFolder, TrafficPackJson.ManifestFile), new UTF8Encoding(false).GetBytes(writer + "\n"));
			}

			return dayFolder;
		}

		private static byte[] Roster(TrafficPack pack)
		{
			var builder = new StringBuilder(TrafficPackJson.VesselColumns).Append('\n');
			foreach (var v in pack.Vessels)
			{
				builder.Append(v.Index.ToString(CultureInfo.InvariantCulture)).Append(',');
				builder.Append(v.Mmsi.ToString(CultureInfo.InvariantCulture)).Append(',');
				builder.Append(Quote(v.Name)).Append(',');
				builder.Append(Optional(v.AisType)).Append(',');
				builder.Append(TrafficClasses.Name(v.Class)).Append(',');
				builder.Append(Optional(v.Length)).Append(',');
				builder.Append(Optional(v.Beam)).Append(',');
				builder.Append(Optional(v.Draft)).Append(',');
				builder.Append(Number(v.FirstTime)).Append(',');
				builder.Append(Number(v.LastTime)).Append(',');
				builder.Append(v.Reports.ToString(CultureInfo.InvariantCulture)).Append(',');
				builder.Append(v.Segments.ToString(CultureInfo.InvariantCulture)).Append(',');
				builder.Append(v.Moving ? "True" : "False").Append(',');
				builder.Append(v.FerryCandidate ? "True" : "False").Append('\n');
			}

			return new UTF8Encoding(false).GetBytes(builder.ToString());
		}

		private static byte[] Records(TrafficReports reports)
		{
			var bytes = new byte[44 * reports.Count];
			for (var i = 0; i < reports.Count; i++)
			{
				var at = 44 * i;
				Int32(bytes, at, reports.Vessel(i));
				Int32(bytes, at + 4, reports.Segment(i));
				Int64(bytes, at + 8, BitConverter.DoubleToInt64Bits(reports.Time(i)));
				Int64(bytes, at + 16, BitConverter.DoubleToInt64Bits(reports.North(i)));
				Int64(bytes, at + 24, BitConverter.DoubleToInt64Bits(reports.East(i)));
				Int32(bytes, at + 32, BitConverter.SingleToInt32Bits((float)reports.SpeedOverGround(i)));
				Int32(bytes, at + 36, BitConverter.SingleToInt32Bits((float)reports.CourseOverGround(i)));
				Int32(bytes, at + 40, BitConverter.SingleToInt32Bits((float)reports.Heading(i)));
			}

			return bytes;
		}

		private static void Int32(byte[] bytes, int at, int value)
		{
			bytes[at] = (byte)value;
			bytes[at + 1] = (byte)(value >> 8);
			bytes[at + 2] = (byte)(value >> 16);
			bytes[at + 3] = (byte)(value >> 24);
		}

		private static void Int64(byte[] bytes, int at, long value)
		{
			Int32(bytes, at, (int)value);
			Int32(bytes, at + 4, (int)(value >> 32));
		}

		// pandas writes the roster with float_format "%.17g"; G17 is the same text for the values a pack holds.
		private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

		private static string Optional(double value) => double.IsNaN(value) ? string.Empty : Number(value);

		private static string Quote(string text) =>
			text.IndexOf(',') < 0 && text.IndexOf('"') < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
	}
}
