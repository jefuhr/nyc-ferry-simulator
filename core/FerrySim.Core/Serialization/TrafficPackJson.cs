using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Traffic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Core.Serialization
{
	/// <summary>A traffic pack file that is missing, malformed or does not match its manifest. The message starts with the file's path.</summary>
	public sealed class TrafficPackException : Exception
	{
		public TrafficPackException(string file, string message, Exception inner = null)
			: base(file + ": " + message, inner)
		{
			File = file;
		}

		/// <summary>The file the problem is in.</summary>
		public string File { get; }
	}

	/// <summary>
	/// Loads a traffic pack day folder (ferrysim-traffic/1, <c>data/processed/harbor/traffic/&lt;date&gt;</c>) and verifies it, as
	/// the environment packs are verified: the manifest (format, date, a New York midnight epoch, the harbor area, every key
	/// known), the file inventory with each file's byte size and SHA-256, the pack digest over the three files and the epoch,
	/// the record layout as <c>reports.json</c> publishes it (which the reader then follows), the counts, the roster and every
	/// report (<see cref="TrafficPack"/>'s checks). Anything that does not match is refused with a <see cref="TrafficPackException"/>
	/// naming the file. Numbers are parsed with the invariant culture.
	/// </summary>
	public static class TrafficPackJson
	{
		public const string ManifestFile = "manifest.json";

		public const string VesselsFile = "vessels.csv";

		public const string ReportsFile = "reports.bin";

		public const string LayoutFile = "reports.json";

		/// <summary>The three data files, in digest order.</summary>
		public static readonly IReadOnlyList<string> Files = new[] { VesselsFile, ReportsFile, LayoutFile };

		/// <summary>The roster's columns, in order.</summary>
		public const string VesselColumns = "index,mmsi,name,ais_type,class,length_m,beam_m,draft_m,first_t_s,last_t_s,reports,segments,moving,ferry_candidate";

		/// <summary>The format's area, <c>ferrydata.slices.AIS_HARBOR_BBOX</c>: south, north, west, east, degrees.</summary>
		public static readonly IReadOnlyList<double> HarborBox = new[] { 40.50, 40.90, -74.10, -73.75 };

		private const string EpochFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

		private static readonly string[] ManifestKeys =
		{
			"format", "date", "epoch_utc", "bbox", "source", "source_cadence", "sources", "time_coverage", "drop_accounting", "counts",
			"cleaning", "class_mapping", "ferry_candidates", "files", "digest",
		};

		private static readonly string[] DropReasons = { "impossible_position", "outside_bbox", "invalid_mmsi", "invalid_time", "duplicate", "position_jump", "outside_time_window" };

		// The record fields the reader needs, with the types it reads them as.
		private static readonly string[] FieldNames = { "vessel_index", "segment", "t_s", "north_m", "east_m", "sog_mps", "cog_rad", "heading_rad" };
		private static readonly string[][] FieldTypes =
		{
			new[] { "int32" }, new[] { "int32" }, new[] { "float64" }, new[] { "float64" }, new[] { "float64" },
			new[] { "float32", "float64" }, new[] { "float32", "float64" }, new[] { "float32", "float64" },
		};

		/// <summary>
		/// The pack digest: SHA-256 over vessels.csv, reports.bin and reports.json in that order, each as its name, NUL, its
		/// bytes, NUL, then the epoch text and a NUL.
		/// </summary>
		public static string Digest(IReadOnlyList<byte[]> files, string epochUtc)
		{
			if (files == null || files.Count != Files.Count)
			{
				throw new ArgumentException("The traffic digest covers exactly vessels.csv, reports.bin and reports.json.");
			}

			using (var sha = SHA256.Create())
			{
				for (var i = 0; i < Files.Count; i++)
				{
					var name = Encoding.UTF8.GetBytes(Files[i]);
					sha.TransformBlock(name, 0, name.Length, null, 0);
					sha.TransformBlock(new byte[1], 0, 1, null, 0);
					sha.TransformBlock(files[i], 0, files[i].Length, null, 0);
					sha.TransformBlock(new byte[1], 0, 1, null, 0);
				}

				var epoch = Encoding.UTF8.GetBytes(epochUtc + "\0");
				sha.TransformFinalBlock(epoch, 0, epoch.Length);
				return Hex(sha.Hash);
			}
		}

		/// <summary>Loads and verifies a day folder.</summary>
		public static TrafficPack Load(string dayFolder)
		{
			if (string.IsNullOrEmpty(dayFolder))
			{
				throw new ArgumentException("A traffic pack day folder is required.", nameof(dayFolder));
			}

			var manifestPath = Path.Combine(dayFolder, ManifestFile);
			var manifestBytes = ReadFile(manifestPath);
			var manifest = new Node(manifestPath, ParseObject(manifestPath, manifestBytes), "manifest");
			var format = manifest.Text("format");
			if (format != TrafficPack.Format)
			{
				throw manifest.Error("format", $"must be {TrafficPack.Format}, found '{format}'.");
			}

			var date = manifest.Text("date");
			var epochText = manifest.Text("epoch_utc");
			var epoch = Epoch(manifest, date, epochText);
			CheckBox(manifest.Object("bbox"));

			// Integrity first: every file against its size and hash, then the digest over the files and the epoch.
			var fileNodes = manifest.Objects("files");
			if (fileNodes.Count != Files.Count)
			{
				throw manifest.Error("files", $"must list exactly {string.Join(", ", Files)}, in that order.");
			}

			var bytes = new byte[Files.Count][];
			for (var i = 0; i < Files.Count; i++)
			{
				var node = fileNodes[i];
				var name = node.Text("name");
				if (name != Files[i])
				{
					throw node.Error("name", $"must be {Files[i]} (the files are {string.Join(", ", Files)}, in that order), found '{name}'.");
				}

				var path = Path.Combine(dayFolder, name);
				bytes[i] = ReadFile(path);
				var size = node.Long("size_bytes");
				if (bytes[i].LongLength != size)
				{
					throw new TrafficPackException(path, $"has {bytes[i].LongLength} bytes, but manifest.json lists {size}: the file or manifest.json has changed.");
				}

				var expected = node.Hex("sha256");
				var actual = Sha256Hex(bytes[i]);
				if (actual != expected)
				{
					throw new TrafficPackException(path, $"SHA-256 is {actual}, but manifest.json lists {expected}: the file or manifest.json has changed.");
				}

				node.Finish();
			}

			var recorded = manifest.Hex("digest");
			var computed = Digest(bytes, epochText);
			if (computed != recorded)
			{
				throw manifest.Error("digest", $"is {recorded}, but the digest of vessels.csv, reports.bin, reports.json and the epoch is {computed}.");
			}

			var counts = manifest.Object("counts");
			var vesselCount = counts.Long("vessels");
			var reportCount = counts.Long("reports");
			var sourceReports = counts.Long("source_reports");
			var dropped = counts.Object("dropped_reports");
			long droppedTotal = 0;
			foreach (var reason in DropReasons)
			{
				droppedTotal += dropped.Long(reason);
			}

			dropped.Finish();
			if (sourceReports != reportCount + droppedTotal)
			{
				throw counts.Error("source_reports", $"is {sourceReports}, but {reportCount} kept and {droppedTotal} dropped reports make {reportCount + droppedTotal}.");
			}

			var byClass = counts.Object("vessels_by_class");
			var classCounts = byClass.Counts();
			counts.Finish();

			var coverage = manifest.Object("time_coverage");
			coverage.Number("requested_from_s");
			coverage.Number("requested_to_s");
			var coveredFrom = coverage.NullableNumber("covered_from_s");
			var coveredTo = coverage.NullableNumber("covered_to_s");
			foreach (var gap in coverage.Objects("missing_intervals"))
			{
				gap.Number("from_s");
				gap.Number("to_s");
				gap.Text("reason");
				gap.Finish();
			}

			coverage.Finish();
			manifest.Present("source");
			manifest.Text("source_cadence");
			manifest.Present("sources");
			manifest.Text("drop_accounting");
			manifest.Present("cleaning");
			manifest.Present("class_mapping");
			manifest.Present("ferry_candidates");
			manifest.Finish(ManifestKeys);

			var layoutPath = Path.Combine(dayFolder, LayoutFile);
			var layout = Layout.Read(layoutPath, bytes[2]);
			var reportsPath = Path.Combine(dayFolder, ReportsFile);
			var reports = layout.Decode(reportsPath, bytes[1]);
			if (reports.Count != reportCount)
			{
				throw new TrafficPackException(reportsPath, $"holds {reports.Count} reports, but manifest.json counts {reportCount}.");
			}

			var vesselsPath = Path.Combine(dayFolder, VesselsFile);
			var vessels = ReadVessels(vesselsPath, bytes[0]);
			if (vessels.Count != vesselCount)
			{
				throw new TrafficPackException(vesselsPath, $"lists {vessels.Count} vessels, but manifest.json counts {vesselCount}.");
			}

			var rosterClasses = new SortedDictionary<string, long>(StringComparer.Ordinal);
			foreach (var vessel in vessels)
			{
				var name = TrafficClasses.Name(vessel.Class);
				rosterClasses[name] = rosterClasses.TryGetValue(name, out var n) ? n + 1 : 1;
			}

			if (!SameCounts(rosterClasses, classCounts))
			{
				throw byClass.Error("", "does not match the classes vessels.csv lists.");
			}

			try
			{
				return new TrafficPack(date, epoch, coveredFrom ?? double.NaN, coveredTo ?? double.NaN, vessels, reports, recorded);
			}
			catch (ArgumentException exception)
			{
				throw new TrafficPackException(reportsPath, exception.Message, exception);
			}
		}

		/// <summary>Lower-case hex SHA-256 of some bytes.</summary>
		public static string Sha256Hex(byte[] bytes)
		{
			using (var sha = SHA256.Create())
			{
				return Hex(sha.ComputeHash(bytes));
			}
		}

		private static bool SameCounts(SortedDictionary<string, long> a, Dictionary<string, long> b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}

			foreach (var pair in a)
			{
				if (!b.TryGetValue(pair.Key, out var value) || value != pair.Value)
				{
					return false;
				}
			}

			return true;
		}

		private static void CheckBox(Node box)
		{
			var values = new[] { box.Number("south"), box.Number("north"), box.Number("west"), box.Number("east") };
			box.Finish();
			for (var i = 0; i < values.Length; i++)
			{
				if (values[i] != HarborBox[i])
				{
					throw box.Error("", $"is south {values[0]}, north {values[1]}, west {values[2]}, east {values[3]}; the format's area is south {HarborBox[0]}, north {HarborBox[1]}, west {HarborBox[2]}, east {HarborBox[3]}.");
				}
			}
		}

		private static DateTimeOffset Epoch(Node manifest, string date, string epochText)
		{
			if (date.Length != 10 || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
			{
				throw manifest.Error("date", $"must be YYYY-MM-DD, found '{date}'.");
			}

			if (!DateTimeOffset.TryParseExact(epochText, EpochFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var epoch))
			{
				throw manifest.Error("epoch_utc", $"must be UTC as YYYY-MM-DDTHH:MM:SSZ, found '{epochText}'.");
			}

			if (day.Year < 2007)
			{
				throw manifest.Error("date", "must be 2007 or later; the reader knows New York's daylight-saving rule from 2007.");
			}

			var midnight = EnvironmentPackJson.NewYorkMidnight(day);
			if (epoch.UtcTicks != midnight.UtcTicks)
			{
				throw manifest.Error("epoch_utc", $"is {epochText}, but local midnight in New York on {date} is {midnight.UtcDateTime.ToString(EpochFormat, CultureInfo.InvariantCulture)}.");
			}

			return midnight;
		}

		private static List<TrafficVessel> ReadVessels(string path, byte[] bytes)
		{
			string text;
			try
			{
				text = new UTF8Encoding(false, true).GetString(bytes);
			}
			catch (DecoderFallbackException)
			{
				throw new TrafficPackException(path, "is not valid UTF-8.");
			}

			if (text.IndexOf('\r') >= 0)
			{
				throw new TrafficPackException(path, "has CR characters; the format's line end is LF.");
			}

			var lines = text.Split('\n');
			if (lines.Length < 1 || lines[0] != VesselColumns)
			{
				throw new TrafficPackException(path, $"line 1 must be '{VesselColumns}'.");
			}

			if (lines[lines.Length - 1].Length != 0)
			{
				throw new TrafficPackException(path, "does not end with a line end.");
			}

			var vessels = new List<TrafficVessel>();
			for (var i = 1; i < lines.Length - 1; i++)
			{
				var number = i + 1;
				var fields = SplitCsv(lines[i], path, number);
				if (fields.Count != 14)
				{
					throw new TrafficPackException(path, $"line {number} has {fields.Count} fields; the roster has 14 columns.");
				}

				if (!TrafficClasses.TryParse(fields[4], out var vesselClass))
				{
					throw new TrafficPackException(path, $"line {number} class '{fields[4]}' is not one of the format's classes.");
				}

				try
				{
					vessels.Add(new TrafficVessel(
						(int)Whole(fields[0], "index", path, number, 0, int.MaxValue),
						(int)Whole(fields[1], "mmsi", path, number, 1, 999999999),
						fields[2],
						Optional(fields[3], "ais_type", path, number),
						vesselClass,
						Optional(fields[5], "length_m", path, number),
						Optional(fields[6], "beam_m", path, number),
						Optional(fields[7], "draft_m", path, number),
						Required(fields[8], "first_t_s", path, number),
						Required(fields[9], "last_t_s", path, number),
						(int)Whole(fields[10], "reports", path, number, 1, int.MaxValue),
						(int)Whole(fields[11], "segments", path, number, 1, int.MaxValue),
						Flag(fields[12], "moving", path, number),
						Flag(fields[13], "ferry_candidate", path, number)));
				}
				catch (ArgumentException exception)
				{
					throw new TrafficPackException(path, $"line {number}: {exception.Message}", exception);
				}
			}

			return vessels;
		}

		private static long Whole(string text, string field, string path, int line, long minimum, long maximum)
		{
			if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum)
			{
				return value;
			}

			throw new TrafficPackException(path, $"line {line} {field} '{text}' is not a whole number from {minimum} to {maximum}.");
		}

		private static double Required(string text, string field, string path, int line)
		{
			if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) && !double.IsInfinity(value))
			{
				return value;
			}

			throw new TrafficPackException(path, $"line {line} {field} '{text}' is not a finite number.");
		}

		private static double Optional(string text, string field, string path, int line) => text.Length == 0 ? double.NaN : Required(text, field, path, line);

		private static bool Flag(string text, string field, string path, int line)
		{
			if (text == "True")
			{
				return true;
			}

			if (text == "False")
			{
				return false;
			}

			throw new TrafficPackException(path, $"line {line} {field} '{text}' must be True or False.");
		}

		/// <summary>One CSV line as pandas writes it: fields quoted when they hold a comma or a quote, quotes doubled.</summary>
		private static List<string> SplitCsv(string line, string path, int number)
		{
			var fields = new List<string>();
			var field = new StringBuilder();
			var quoted = false;
			var wasQuoted = false;
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
				else if (c == '"' && field.Length == 0 && !wasQuoted)
				{
					quoted = true;
					wasQuoted = true;
				}
				else if (c == ',')
				{
					fields.Add(field.ToString());
					field.Clear();
					wasQuoted = false;
				}
				else if (wasQuoted)
				{
					throw new TrafficPackException(path, $"line {number} has text after a closing quote.");
				}
				else
				{
					field.Append(c);
				}
			}

			if (quoted)
			{
				throw new TrafficPackException(path, $"line {number} has an unclosed quote.");
			}

			fields.Add(field.ToString());
			return fields;
		}

		private static byte[] ReadFile(string path)
		{
			try
			{
				return File.ReadAllBytes(path);
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				throw new TrafficPackException(path, "cannot be read: " + exception.Message, exception);
			}
		}

		private static JObject ParseObject(string path, byte[] bytes)
		{
			string text;
			try
			{
				text = new UTF8Encoding(false, true).GetString(bytes);
			}
			catch (DecoderFallbackException)
			{
				throw new TrafficPackException(path, "is not valid UTF-8.");
			}

			try
			{
				using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
				{
					var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
					if (reader.Read())
					{
						throw new TrafficPackException(path, "has text after the JSON object.");
					}

					return token as JObject ?? throw new TrafficPackException(path, "must hold a JSON object.");
				}
			}
			catch (JsonException exception)
			{
				throw new TrafficPackException(path, "is not valid JSON: " + exception.Message, exception);
			}
		}

		private static string Hex(byte[] bytes)
		{
			var builder = new StringBuilder(bytes.Length * 2);
			foreach (var b in bytes)
			{
				builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
			}

			return builder.ToString();
		}

		/// <summary>The record layout <c>reports.json</c> publishes, which the reader follows.</summary>
		private sealed class Layout
		{
			private readonly int _size;
			private readonly int[] _offset;
			private readonly int[] _width;

			private Layout(int size, int[] offset, int[] width)
			{
				_size = size;
				_offset = offset;
				_width = width;
			}

			public static Layout Read(string path, byte[] bytes)
			{
				var node = new Node(path, ParseObject(path, bytes), "layout");
				var order = node.Text("byte_order");
				if (order != "little-endian")
				{
					throw node.Error("byte_order", $"must be little-endian, found '{order}'.");
				}

				var size = node.Long("record_size_bytes");
				if (size < 1 || size > 4096)
				{
					throw node.Error("record_size_bytes", $"must be 1 to 4096, found {size}.");
				}

				var offset = new int[FieldNames.Length];
				var width = new int[FieldNames.Length];
				var seen = new bool[FieldNames.Length];
				var used = new bool[size];
				var fields = node.Objects("fields");
				foreach (var field in fields)
				{
					var name = field.Text("name");
					var type = field.Text("type");
					var at = field.Long("offset_bytes");
					field.Finish();
					var index = Array.IndexOf(FieldNames, name);
					if (index < 0)
					{
						throw field.Error("name", $"'{name}' is not a field of the format.");
					}

					if (seen[index])
					{
						throw field.Error("name", $"'{name}' appears twice.");
					}

					if (Array.IndexOf(FieldTypes[index], type) < 0)
					{
						throw field.Error("type", $"of {name} must be {string.Join(" or ", FieldTypes[index])}, found '{type}'.");
					}

					var bytesWide = type == "float64" ? 8 : 4;
					if (at < 0 || at + bytesWide > size)
					{
						throw field.Error("offset_bytes", $"of {name} puts it at {at} to {at + bytesWide}, outside the {size}-byte record.");
					}

					for (var b = at; b < at + bytesWide; b++)
					{
						if (used[b])
						{
							throw field.Error("offset_bytes", $"of {name} overlaps another field at byte {b}.");
						}

						used[b] = true;
					}

					seen[index] = true;
					offset[index] = (int)at;
					width[index] = bytesWide;
				}

				for (var i = 0; i < FieldNames.Length; i++)
				{
					if (!seen[i])
					{
						throw node.Error("fields", $"lacks {FieldNames[i]}.");
					}
				}

				var missing = node.Text("missing_float");
				if (missing != "IEEE 754 quiet NaN")
				{
					throw node.Error("missing_float", $"must be 'IEEE 754 quiet NaN', found '{missing}'.");
				}

				var angles = node.Text("angles");
				if (angles != "radians clockwise from true north")
				{
					throw node.Error("angles", $"must be 'radians clockwise from true north', found '{angles}'.");
				}

				node.Text("positions");
				node.Finish(new[] { "byte_order", "record_size_bytes", "fields", "missing_float", "angles", "positions" });
				return new Layout((int)size, offset, width);
			}

			public TrafficReports Decode(string path, byte[] bytes)
			{
				if (bytes.Length % _size != 0)
				{
					throw new TrafficPackException(path, $"has {bytes.Length} bytes, not a whole number of {_size}-byte records: it is truncated.");
				}

				var count = bytes.Length / _size;
				var vessel = new int[count];
				var segment = new int[count];
				var time = new double[count];
				var north = new double[count];
				var east = new double[count];
				var sog = new float[count];
				var cog = new float[count];
				var heading = new float[count];
				for (var i = 0; i < count; i++)
				{
					var record = i * _size;
					vessel[i] = Int32(bytes, record + _offset[0]);
					segment[i] = Int32(bytes, record + _offset[1]);
					time[i] = Real(bytes, record + _offset[2], _width[2]);
					north[i] = Real(bytes, record + _offset[3], _width[3]);
					east[i] = Real(bytes, record + _offset[4], _width[4]);
					sog[i] = (float)Real(bytes, record + _offset[5], _width[5]);
					cog[i] = (float)Real(bytes, record + _offset[6], _width[6]);
					heading[i] = (float)Real(bytes, record + _offset[7], _width[7]);
				}

				return new TrafficReports(vessel, segment, time, north, east, sog, cog, heading);
			}

			private static int Int32(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24;

			private static double Real(byte[] bytes, int at, int width)
			{
				if (width == 4)
				{
					return BitConverter.Int32BitsToSingle(Int32(bytes, at));
				}

				var low = (uint)Int32(bytes, at);
				var high = (uint)Int32(bytes, at + 4);
				return BitConverter.Int64BitsToDouble((long)((ulong)high << 32 | low));
			}
		}

		/// <summary>A JSON object of a manifest whose keys are read and checked, so unknown ones are refused.</summary>
		private sealed class Node
		{
			private readonly string _file;
			private readonly JObject _object;
			private readonly string _path;
			private readonly HashSet<string> _read = new HashSet<string>(StringComparer.Ordinal);

			public Node(string file, JObject value, string path)
			{
				_file = file;
				_object = value;
				_path = path;
			}

			public TrafficPackException Error(string key, string message) =>
				new TrafficPackException(_file, (key.Length > 0 ? $"{_path}.{key} " : $"{_path} ") + message);

			public string Text(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.String)
				{
					throw Error(key, "must be a string.");
				}

				return (string)token;
			}

			public string Hex(string key)
			{
				var text = Text(key);
				if (!EnvironmentPackDigests.IsHexDigest(text))
				{
					throw Error(key, $"must be 64 lower-case hex characters, found '{text}'.");
				}

				return text;
			}

			public void Present(string key) => Required(key);

			public double Number(string key) => Number(key, Required(key));

			public double? NullableNumber(string key)
			{
				var token = Required(key);
				return token.Type == JTokenType.Null ? (double?)null : Number(key, token);
			}

			public long Long(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.Integer)
				{
					throw Error(key, "must be a whole number.");
				}

				var value = (long)token;
				if (value < 0)
				{
					throw Error(key, $"must not be negative, found {value}.");
				}

				return value;
			}

			public Node Object(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.Object)
				{
					throw Error(key, "must be a JSON object.");
				}

				return new Node(_file, (JObject)token, _path + "." + key);
			}

			public List<Node> Objects(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.Array)
				{
					throw Error(key, "must be a JSON array.");
				}

				var nodes = new List<Node>();
				var array = (JArray)token;
				for (var i = 0; i < array.Count; i++)
				{
					if (array[i].Type != JTokenType.Object)
					{
						throw Error($"{key}[{i}]", "must be a JSON object.");
					}

					nodes.Add(new Node(_file, (JObject)array[i], $"{_path}.{key}[{i}]"));
				}

				return nodes;
			}

			/// <summary>Every key of this object as a whole-number count.</summary>
			public Dictionary<string, long> Counts()
			{
				var counts = new Dictionary<string, long>(StringComparer.Ordinal);
				foreach (var property in _object.Properties())
				{
					counts[property.Name] = Long(property.Name);
				}

				return counts;
			}

			/// <summary>Refuses keys that were never read.</summary>
			public void Finish()
			{
				foreach (var property in _object.Properties())
				{
					if (!_read.Contains(property.Name))
					{
						throw Error(property.Name, "is not part of the format.");
					}
				}
			}

			/// <summary>Refuses keys that are not in <paramref name="keys"/>.</summary>
			public void Finish(IReadOnlyList<string> keys)
			{
				foreach (var property in _object.Properties())
				{
					var known = false;
					foreach (var key in keys)
					{
						known |= key == property.Name;
					}

					if (!known)
					{
						throw Error(property.Name, "is not part of the format.");
					}
				}
			}

			private double Number(string key, JToken token)
			{
				if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
				{
					throw Error(key, "must be a number.");
				}

				var value = (double)token;
				if (double.IsNaN(value) || double.IsInfinity(value))
				{
					throw Error(key, "must be finite.");
				}

				return value;
			}

			private JToken Required(string key)
			{
				_read.Add(key);
				if (!_object.TryGetValue(key, StringComparison.Ordinal, out var token))
				{
					throw Error(key, "is missing.");
				}

				return token;
			}
		}
	}
}
