using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Core.Serialization
{
	/// <summary>A pack file that is missing, malformed or does not match its digests. The message starts with the file's path.</summary>
	public sealed class EnvironmentPackException : Exception
	{
		public EnvironmentPackException(string file, string message, Exception inner = null)
			: base(file + ": " + message, inner)
		{
			File = file;
		}

		/// <summary>The file the problem is in.</summary>
		public string File { get; }
	}

	/// <summary>
	/// Loads an environment pack (ferrysim-env/1) from disk and verifies it: every file's SHA-256 against its manifest,
	/// both <c>DIGEST</c> files against the bytes they cover (<see cref="EnvironmentPackDigests"/>), the day's
	/// <c>depth_digest</c> against the depth set, and the manifests themselves (format, region, date and epoch, station
	/// positions against their latitude and longitude, keys, number ranges). Series files are parsed with the invariant
	/// culture. Anything that does not match is refused with an <see cref="EnvironmentPackException"/> naming the file.
	/// </summary>
	public static class EnvironmentPackJson
	{
		/// <summary>Largest distance, m, between a station's north_m/east_m and its latitude and longitude in the world frame.</summary>
		public const double PositionTolerance = 0.01;

		private const string EpochFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

		/// <summary>
		/// Loads a day folder (<c>…/&lt;region&gt;/env/days/&lt;date&gt;</c>) and the depth set the layout puts beside it
		/// (<c>…/&lt;region&gt;/env/depth</c>).
		/// </summary>
		public static EnvironmentPack Load(string dayFolder) => Load(dayFolder, DepthFolderOf(dayFolder));

		/// <summary>Loads a day folder and a depth set from an explicit folder.</summary>
		public static EnvironmentPack Load(string dayFolder, string depthFolder)
		{
			if (string.IsNullOrEmpty(dayFolder))
			{
				throw new ArgumentException("A day folder is required.", nameof(dayFolder));
			}

			var depth = LoadDepthSet(depthFolder);
			var manifestPath = Path.Combine(dayFolder, EnvironmentPackDigests.ManifestFile);
			var manifestBytes = ReadFile(manifestPath);
			var digestPath = Path.Combine(dayFolder, EnvironmentPackDigests.DigestFile);
			var recorded = ReadDigestFile(digestPath);
			var manifest = new Node(manifestPath, ParseObject(manifestPath, manifestBytes), "manifest");
			var series = new byte[EnvironmentPackDigests.SeriesFiles.Count][];
			var files = manifest.Object("files");
			for (var i = 0; i < series.Length; i++)
			{
				var name = EnvironmentPackDigests.SeriesFiles[i];
				series[i] = ReadFile(Path.Combine(dayFolder, name));
				CheckHash(Path.Combine(dayFolder, name), series[i], files.Hex(name));
			}

			files.Finish();
			var computed = EnvironmentPackDigests.DayDigest(manifestBytes, series, depth.Digest);
			if (computed != recorded)
			{
				throw new EnvironmentPackException(digestPath,
					$"the day digest of manifest.json, the series files and the depth digest is {computed}, but DIGEST says {recorded}: manifest.json or DIGEST has changed.");
			}

			var format = manifest.Text("format");
			if (format != EnvironmentPack.Format)
			{
				throw manifest.Error("format", $"must be {EnvironmentPack.Format}, found '{format}'.");
			}

			var region = manifest.Text("region");
			if (region != depth.Region)
			{
				throw manifest.Error("region", $"is '{region}', but the depth set is for '{depth.Region}'.");
			}

			var date = manifest.Text("date");
			var epochText = manifest.Text("epoch_utc");
			var epoch = Epoch(manifest, date, epochText);
			var coverage = manifest.Numbers("time_coverage_s");
			if (coverage.Count != 2 || !(coverage[1] > coverage[0]))
			{
				throw manifest.Error("time_coverage_s", "must be [start, end] with the end after the start.");
			}

			var depthDigest = manifest.Hex("depth_digest");
			if (depthDigest != depth.Digest)
			{
				throw manifest.Error("depth_digest", $"is {depthDigest}, but the depth set's DIGEST is {depth.Digest}.");
			}

			var currentNodes = manifest.Objects("current_stations");
			var gaugeNodes = manifest.Objects("water_level_stations");
			var windNodes = manifest.Objects("wind_stations");
			var notes = manifest.Texts("notes");
			manifest.Finish();

			var currentRows = Rows(dayFolder, 0, series[0], EnvironmentPackCsv.CurrentsHeader, currentNodes);
			var gaugeRows = Rows(dayFolder, 1, series[1], EnvironmentPackCsv.WaterLevelsHeader, gaugeNodes);
			var windRows = Rows(dayFolder, 2, series[2], EnvironmentPackCsv.WindHeader, windNodes, allowNone: true);

			var currents = new List<CurrentStation>();
			foreach (var node in currentNodes)
			{
				var id = node.Text("id");
				var rows = currentRows[id];
				var name = node.Text("name");
				var kind = node.Text("kind");
				Position(node, out var latitude, out var longitude, out var north, out var east);
				var binDepth = node.NullableNumber("bin_depth_m");
				currents.Add(new CurrentStation(id, name, kind, latitude, longitude, north, east, binDepth, node.Text("source"), node.Hex("source_sha256"),
					new VectorSeries(rows.Times, rows.First, rows.Second)));
				node.Finish();
			}

			var gauges = new List<WaterLevelStation>();
			foreach (var node in gaugeNodes)
			{
				var id = node.Text("id");
				var rows = gaugeRows[id];
				var name = node.Text("name");
				Position(node, out var latitude, out var longitude, out var north, out var east);
				gauges.Add(new WaterLevelStation(id, name, latitude, longitude, north, east, node.Text("source"), node.Hex("source_sha256"),
					new ScalarSeries(rows.Times, rows.First), rows.Flags));
				node.Finish();
			}

			var winds = new List<WindStation>();
			foreach (var node in windNodes)
			{
				var id = node.Text("id");
				var rows = windRows[id];
				var name = node.Text("name");
				Position(node, out var latitude, out var longitude, out var north, out var east);
				var height = node.NullableNumber("sensor_height_m");
				winds.Add(new WindStation(id, name, latitude, longitude, north, east, height, node.Text("source"), node.Hex("source_sha256"),
					new VectorSeries(rows.Times, rows.First, rows.Second), rows.Third));
				node.Finish();
			}

			try
			{
				return new EnvironmentPack(region, date, epoch, coverage[0], coverage[1], depth, currents, gauges, winds, notes, computed);
			}
			catch (ArgumentException exception)
			{
				throw new EnvironmentPackException(manifestPath, exception.Message, exception);
			}
		}

		/// <summary>Loads and verifies a depth set folder: its manifest, grid files and DIGEST.</summary>
		public static DepthSet LoadDepthSet(string depthFolder)
		{
			if (string.IsNullOrEmpty(depthFolder))
			{
				throw new ArgumentException("A depth folder is required.", nameof(depthFolder));
			}

			var manifestPath = Path.Combine(depthFolder, EnvironmentPackDigests.ManifestFile);
			var manifestBytes = ReadFile(manifestPath);
			var digestPath = Path.Combine(depthFolder, EnvironmentPackDigests.DigestFile);
			var recorded = ReadDigestFile(digestPath);
			var manifest = new Node(manifestPath, ParseObject(manifestPath, manifestBytes), "manifest");
			var gridNodes = manifest.Objects("grids");
			if (gridNodes.Count == 0)
			{
				throw manifest.Error("grids", "must list at least one grid.");
			}

			// Integrity first: each grid file against its hash, then DIGEST over the manifest and the grid files.
			var names = new List<string>();
			var files = new List<byte[]>();
			foreach (var node in gridNodes)
			{
				var id = node.Text("id");
				var file = node.Text("file");
				if (file != id + ".f32" || !IsPlainName(id))
				{
					throw node.Error("file", $"must be the grid's id with .f32 ('{id}.f32'), a plain file name, found '{file}'.");
				}

				var gridPath = Path.Combine(depthFolder, file);
				var bytes = ReadFile(gridPath);
				CheckHash(gridPath, bytes, node.Hex("sha256"));
				names.Add(file);
				files.Add(bytes);
			}

			var computed = EnvironmentPackDigests.DepthDigest(manifestBytes, names, files);
			if (computed != recorded)
			{
				throw new EnvironmentPackException(digestPath,
					$"the depth digest of manifest.json and the grid files is {computed}, but DIGEST says {recorded}: manifest.json or DIGEST has changed.");
			}

			var format = manifest.Text("format");
			if (format != EnvironmentPack.DepthFormat)
			{
				throw manifest.Error("format", $"must be {EnvironmentPack.DepthFormat}, found '{format}'.");
			}

			var region = manifest.Text("region");
			var datum = manifest.Text("datum");
			if (datum != "NAVD88")
			{
				throw manifest.Error("datum", $"must be NAVD88, found '{datum}'.");
			}

			manifest.Text("value");
			var outsideDepth = manifest.Number("outside_depth_m");
			if (!(outsideDepth > 0.0))
			{
				throw manifest.Error("outside_depth_m", "must be positive.");
			}

			var sources = new List<DepthSource>();
			foreach (var node in manifest.Objects("sources"))
			{
				sources.Add(new DepthSource(node.Text("name"), node.Hex("sha256"), node.OptionalText("note")));
				node.Finish();
			}

			manifest.Finish();

			var grids = new List<DepthGrid>();
			for (var g = 0; g < gridNodes.Count; g++)
			{
				var node = gridNodes[g];
				var gridPath = Path.Combine(depthFolder, names[g]);
				var originNorth = node.Number("origin_north_m");
				var originEast = node.Number("origin_east_m");
				var cell = node.Number("cell_m");
				var rows = node.Count("rows");
				var columns = node.Count("cols");
				if (node.Text("nodata") != "NaN")
				{
					throw node.Error("nodata", "must be \"NaN\".");
				}

				node.Finish();
				var bytes = files[g];
				if (bytes.LongLength != 4L * rows * columns)
				{
					throw new EnvironmentPackException(gridPath, $"has {bytes.LongLength} bytes; {rows} rows by {columns} columns of float32 need {4L * rows * columns}.");
				}

				var values = new float[rows * columns];
				for (var i = 0; i < values.Length; i++)
				{
					values[i] = BitConverter.Int32BitsToSingle(bytes[4 * i] | bytes[4 * i + 1] << 8 | bytes[4 * i + 2] << 16 | bytes[4 * i + 3] << 24);
				}

				try
				{
					grids.Add(new DepthGrid(node.Text("id"), originNorth, originEast, cell, rows, columns, values));
				}
				catch (ArgumentException exception)
				{
					throw new EnvironmentPackException(manifestPath, exception.Message, exception);
				}
			}

			try
			{
				return new DepthSet(region, datum, outsideDepth, grids, sources, computed);
			}
			catch (ArgumentException exception)
			{
				throw new EnvironmentPackException(manifestPath, exception.Message, exception);
			}
		}

		/// <summary>The depth folder the layout puts beside a day folder: <c>days/&lt;date&gt;/../../depth</c>.</summary>
		public static string DepthFolderOf(string dayFolder) => Path.GetFullPath(Path.Combine(dayFolder, "..", "..", "depth"));

		/// <summary>
		/// Local midnight in America/New_York on a date, as UTC, by the US daylight-saving rule in force since 2007
		/// (second Sunday in March to first Sunday in November, changing at 02:00 local).
		/// </summary>
		public static DateTimeOffset NewYorkMidnight(DateTime date)
		{
			if (date.Year < 2007)
			{
				throw new ArgumentOutOfRangeException(nameof(date), "The reader knows New York's daylight-saving rule from 2007.");
			}

			var day = date.Date;
			var start = NthSunday(day.Year, 3, 2);
			var end = NthSunday(day.Year, 11, 1);
			var daylight = day > start && day <= end;
			return new DateTimeOffset(day.Ticks, TimeSpan.FromHours(daylight ? -4.0 : -5.0)).ToUniversalTime();
		}

		private static DateTime NthSunday(int year, int month, int n)
		{
			var first = new DateTime(year, month, 1);
			var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
			return first.AddDays(offset + 7 * (n - 1));
		}

		private static DateTimeOffset Epoch(Node manifest, string date, string epochText)
		{
			if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
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

			var midnight = NewYorkMidnight(day);
			if (epoch.UtcTicks != midnight.UtcTicks)
			{
				throw manifest.Error("epoch_utc", $"is {epochText}, but local midnight in New York on {date} is {midnight.UtcDateTime.ToString(EpochFormat, CultureInfo.InvariantCulture)}.");
			}

			return midnight;
		}

		private static void Position(Node node, out double latitude, out double longitude, out double north, out double east)
		{
			latitude = node.Number("lat");
			longitude = node.Number("lon");
			north = node.Number("north_m");
			east = node.Number("east_m");
			if (latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0)
			{
				throw node.Error("lat", "latitude and longitude must be WGS84 degrees.");
			}

			var world = LocalTangentPlane.World.ToLocal(new GeodeticPosition(latitude, longitude));
			var dn = world.North - north;
			var de = world.East - east;
			var miss = Math.Sqrt(dn * dn + de * de);
			if (!(miss <= PositionTolerance))
			{
				throw node.Error("north_m",
					$"north_m {north.ToString("R", CultureInfo.InvariantCulture)} and east_m {east.ToString("R", CultureInfo.InvariantCulture)} are {miss:F3} m from the world position of lat {latitude.ToString("R", CultureInfo.InvariantCulture)}, lon {longitude.ToString("R", CultureInfo.InvariantCulture)} ({world.North:F3}, {world.East:F3}).");
			}
		}

		private static Dictionary<string, EnvironmentPackCsv.Rows> Rows(string dayFolder, int file, byte[] bytes, string header, List<Node> stations, bool allowNone = false)
		{
			var ids = new List<string>();
			foreach (var node in stations)
			{
				var id = node.Text("id");
				if (id.Length == 0 || id.IndexOf(',') >= 0 || id.IndexOf('"') >= 0)
				{
					throw node.Error("id", "must be non-empty with no comma or quote.");
				}

				if (ids.Contains(id))
				{
					throw node.Error("id", $"'{id}' appears twice.");
				}

				ids.Add(id);
			}

			if (ids.Count == 0 && !allowNone)
			{
				throw new EnvironmentPackException(Path.Combine(dayFolder, EnvironmentPackDigests.ManifestFile),
					$"lists no stations for {EnvironmentPackDigests.SeriesFiles[file]}; current and water level stations are required.");
			}

			return EnvironmentPackCsv.Read(Path.Combine(dayFolder, EnvironmentPackDigests.SeriesFiles[file]), bytes, header, ids);
		}

		private static void CheckHash(string path, byte[] bytes, string expected)
		{
			var actual = EnvironmentPackDigests.Sha256Hex(bytes);
			if (actual != expected)
			{
				throw new EnvironmentPackException(path, $"SHA-256 is {actual}, but manifest.json lists {expected}: the file or manifest.json has changed.");
			}
		}

		private static bool IsPlainName(string name)
		{
			if (name.Length == 0 || name == "." || name == "..")
			{
				return false;
			}

			foreach (var ch in name)
			{
				if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.'))
				{
					return false;
				}
			}

			return true;
		}

		private static byte[] ReadFile(string path)
		{
			try
			{
				return File.ReadAllBytes(path);
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				throw new EnvironmentPackException(path, "cannot be read: " + exception.Message, exception);
			}
		}

		private static string ReadDigestFile(string path)
		{
			var digest = EnvironmentPackDigests.ParseDigestFile(ReadFile(path));
			if (digest == null)
			{
				throw new EnvironmentPackException(path, "must be 64 lower-case hex characters and a newline.");
			}

			return digest;
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
				throw new EnvironmentPackException(path, "is not valid UTF-8.");
			}

			try
			{
				using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
				{
					var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
					if (reader.Read())
					{
						throw new EnvironmentPackException(path, "has text after the JSON object.");
					}

					return token as JObject ?? throw new EnvironmentPackException(path, "must hold a JSON object.");
				}
			}
			catch (JsonException exception)
			{
				throw new EnvironmentPackException(path, "is not valid JSON: " + exception.Message, exception);
			}
		}

		/// <summary>A JSON object of a manifest whose keys are all read, so unknown ones are refused.</summary>
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

			public EnvironmentPackException Error(string key, string message) => new EnvironmentPackException(_file, $"{_path}.{key} {message}");

			public string Text(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.String)
				{
					throw Error(key, "must be a string.");
				}

				return (string)token;
			}

			public string OptionalText(string key)
			{
				_read.Add(key);
				if (!_object.TryGetValue(key, StringComparison.Ordinal, out var token) || token.Type == JTokenType.Null)
				{
					return null;
				}

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

			public double Number(string key) => Number(key, Required(key));

			public double? NullableNumber(string key)
			{
				var token = Required(key);
				return token.Type == JTokenType.Null ? (double?)null : Number(key, token);
			}

			public int Count(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.Integer)
				{
					throw Error(key, "must be a whole number.");
				}

				var value = (long)token;
				if (value < 1 || value > 1 << 20)
				{
					throw Error(key, $"must be between 1 and {1 << 20}, found {value}.");
				}

				return (int)value;
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
				var nodes = new List<Node>();
				var array = Array(key);
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

			public List<string> Texts(string key)
			{
				var texts = new List<string>();
				var array = Array(key);
				for (var i = 0; i < array.Count; i++)
				{
					if (array[i].Type != JTokenType.String)
					{
						throw Error($"{key}[{i}]", "must be a string.");
					}

					texts.Add((string)array[i]);
				}

				return texts;
			}

			public List<double> Numbers(string key)
			{
				var numbers = new List<double>();
				var array = Array(key);
				for (var i = 0; i < array.Count; i++)
				{
					numbers.Add(Number($"{key}[{i}]", array[i]));
				}

				return numbers;
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

			private JArray Array(string key)
			{
				var token = Required(key);
				if (token.Type != JTokenType.Array)
				{
					throw Error(key, "must be a JSON array.");
				}

				return (JArray)token;
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
