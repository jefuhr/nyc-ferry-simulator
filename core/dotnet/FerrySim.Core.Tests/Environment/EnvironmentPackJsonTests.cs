using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Loading and verifying environment packs. Failure modes guarded:
	/// <list type="bullet">
	/// <item>A pack written by Python to the spec that the C# reader refuses, or whose digests it computes differently.</item>
	/// <item>A flipped byte anywhere (manifests, series files, grid files, DIGEST files) accepted.</item>
	/// <item>A DIGEST that does not match, or a day whose depth_digest is not its depth set's, accepted.</item>
	/// <item>Refusals that do not name the file.</item>
	/// <item>Manifests with the wrong format, region, epoch (not New York midnight), unknown or missing keys, station
	/// positions that disagree with their latitude and longitude, or grid files that do not fit their shape.</item>
	/// <item>Series files with CRLF line ends, the wrong header, times that do not increase, unknown or ungrouped
	/// stations, non-finite numbers, or culture-dependent parsing.</item>
	/// <item>Writing and loading changing any value.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class EnvironmentPackJsonTests
	{
		private string _root;

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "ferrysim-core-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TearDown]
		public void TearDown()
		{
			try
			{
				Directory.Delete(_root, true);
			}
			catch (IOException)
			{
			}
		}

		[Test]
		public void APythonWrittenPack_Loads_WithPythonsDigests()
		{
			var day = PythonPackFixture.Write(_root);
			var pack = EnvironmentPackJson.Load(day);

			Assert.That(pack.Digest, Is.EqualTo(PythonPackFixture.DayDigest));
			Assert.That(pack.Depth.Digest, Is.EqualTo(PythonPackFixture.DepthDigest));
			Assert.That(pack.Name, Is.EqualTo("lower-east-river/2025-07-14"));
			Assert.That(pack.EpochUtc, Is.EqualTo(new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero)));
			Assert.That((pack.CoverageStart, pack.CoverageEnd), Is.EqualTo((-3600.0, 90000.0)));
			Assert.That(pack.Notes, Is.EqualTo(new[] { "water level 8518750: observations missing 00:30-01:00 local, filled with predictions" }));

			var current = pack.CurrentStations[0];
			Assert.That(current.Id, Is.EqualTo("NYH1920"));
			Assert.That(current.Kind, Is.EqualTo("harmonic-prediction"));
			Assert.That(current.North, Is.EqualTo(699.75758330211));
			Assert.That(current.East, Is.EqualTo(1512.685028260301));
			Assert.That(current.BinDepth, Is.EqualTo(1.5));
			Assert.That(current.Velocity.Sample(360.0), Is.EqualTo(new HorizontalVector(1e-05, 0.3)));
			Assert.That(current.Velocity.Sample(180.0).North, Is.EqualTo(0.375005).Within(1e-12));

			var gauge = pack.WaterLevelStations[0];
			Assert.That(gauge.Level.Count, Is.EqualTo(3));
			Assert.That(gauge.IsPredicted(1), Is.True);
			Assert.That(gauge.IsPredicted(2), Is.False);

			var wind = pack.WindStations[0];
			Assert.That(wind.SensorHeight, Is.Null);
			Assert.That(double.IsNaN(wind.Gust(0)), Is.True, "an empty gust_mps is no gust");
			Assert.That(wind.Gust(1), Is.EqualTo(7.5));

			var grid = pack.Depth.Grids[0];
			Assert.That((grid.Rows, grid.Columns, grid.CellSize, grid.OriginNorth, grid.OriginEast), Is.EqualTo((3, 2, 10.0, -20.0, -10.0)));
			Assert.That(grid.BedElevation(0, 1), Is.EqualTo(-9.5f));
			Assert.That(float.IsNaN(grid.BedElevation(1, 0)), Is.True);
			Assert.That(grid.BedElevation(2, 1), Is.EqualTo(2.0f));
			Assert.That(pack.Depth.OutsideDepth, Is.EqualTo(20.0));
		}

		[Test]
		public void APackWrittenAndLoaded_KeepsEveryValue()
		{
			var original = SyntheticPack();
			var day = EnvironmentPackWriter.Write(original, EnvFolder());
			var loaded = EnvironmentPackJson.Load(day);

			Assert.That(loaded.Digest, Is.EqualTo(Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(day, "DIGEST"))).Trim()));
			Assert.That(loaded.CurrentStations.Count, Is.EqualTo(original.CurrentStations.Count));
			for (var i = 0; i < original.CurrentStations.Count; i++)
			{
				var a = original.CurrentStations[i];
				var b = loaded.CurrentStations[i];
				Assert.That((b.Id, b.North, b.East, b.Latitude, b.Longitude), Is.EqualTo((a.Id, a.North, a.East, a.Latitude, a.Longitude)));
				for (var j = 0; j < a.Velocity.Count; j++)
				{
					Assert.That(b.Velocity.Time(j), Is.EqualTo(a.Velocity.Time(j)));
					Assert.That(b.Velocity.Value(j), Is.EqualTo(a.Velocity.Value(j)), $"{a.Id} sample {j}");
				}
			}

			for (var j = 0; j < original.WaterLevelStations[0].Level.Count; j++)
			{
				Assert.That(loaded.WaterLevelStations[0].Level.Value(j), Is.EqualTo(original.WaterLevelStations[0].Level.Value(j)));
				Assert.That(loaded.WaterLevelStations[0].IsPredicted(j), Is.EqualTo(original.WaterLevelStations[0].IsPredicted(j)));
			}

			var originalGrid = original.Depth.Grids[0];
			var loadedGrid = loaded.Depth.Grids[0];
			for (var r = 0; r < originalGrid.Rows; r++)
			{
				for (var c = 0; c < originalGrid.Columns; c++)
				{
					Assert.That(BitConverter.SingleToInt32Bits(loadedGrid.BedElevation(r, c)), Is.EqualTo(BitConverter.SingleToInt32Bits(originalGrid.BedElevation(r, c))));
				}
			}

			// The same day gives the same environment in every bit.
			var start = PackFixtures.Epoch.AddHours(8.0);
			var before = new DataEnvironment(original, start);
			var after = new DataEnvironment(loaded, start);
			for (var i = 0; i < 500; i++)
			{
				var north = -900.0 + 3.7 * i;
				var east = 400.0 - 2.9 * i;
				var time = 11.0 * i;
				Assert.That(after.SampleDepth(north, east, time), Is.EqualTo(before.SampleDepth(north, east, time)));
				Assert.That(after.SampleCurrent(north, east, time), Is.EqualTo(before.SampleCurrent(north, east, time)));
				Assert.That(after.SampleWind(north, east, time), Is.EqualTo(before.SampleWind(north, east, time)));
			}
		}

		[Test]
		public void AFlippedByte_InAnyFile_IsRefused_NamingTheFile()
		{
			var day = PythonPackFixture.Write(_root);
			var env = Path.Combine(_root, "lower-east-river", "env");
			var files = new List<string>(Directory.GetFiles(env, "*", SearchOption.AllDirectories));
			files.Sort(StringComparer.Ordinal);
			Assert.That(files.Count, Is.EqualTo(8), "two manifests, three series files, one grid file and two DIGEST files");
			Assert.That(EnvironmentPackJson.Load(day).Digest, Is.EqualTo(PythonPackFixture.DayDigest));

			var flips = 0;
			foreach (var file in files)
			{
				var original = File.ReadAllBytes(file);
				for (var offset = 0; offset < original.Length; offset++)
				{
					foreach (var mask in new byte[] { 0x01, 0x20 })
					{
						var bytes = (byte[])original.Clone();
						bytes[offset] ^= mask;
						File.WriteAllBytes(file, bytes);
						var what = $"{file.Substring(env.Length + 1)} byte {offset} ^ 0x{mask:x2}";
						var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day), what);
						Assert.That(Path.GetDirectoryName(error.File), Is.EqualTo(Path.GetDirectoryName(file)), $"{what}: {error.Message}");
						Assert.That(error.Message, Does.Contain(Path.GetFileName(file)), what);
						flips++;
					}
				}

				File.WriteAllBytes(file, original);
			}

			Assert.That(EnvironmentPackJson.Load(day).Digest, Is.EqualTo(PythonPackFixture.DayDigest), "the restored pack loads again");
			TestContext.Out.WriteLine($"{flips} single-byte changes across {files.Count} files, every one refused");
		}

		[Test]
		public void AWrongDigestFile_IsRefused()
		{
			var day = PythonPackFixture.Write(_root);
			File.WriteAllText(Path.Combine(day, "DIGEST"), new string('0', 64) + "\n");
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "DIGEST")));
			Assert.That(error.Message, Does.Contain(PythonPackFixture.DayDigest), "says what the digest should be");

			File.WriteAllText(Path.Combine(day, "DIGEST"), PythonPackFixture.DayDigest.ToUpperInvariant() + "\n");
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day)).Message, Does.Contain("lower-case hex"));
			File.WriteAllText(Path.Combine(day, "DIGEST"), PythonPackFixture.DayDigest);
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day)).Message, Does.Contain("and a newline"));
		}

		[Test]
		public void ASeriesFileEditedWithItsHash_ButNotTheDigest_IsRefused()
		{
			var day = PythonPackFixture.Write(_root);
			var path = Path.Combine(day, "currents.csv");
			File.WriteAllText(path, File.ReadAllText(path).Replace("0.75", "0.85"));
			EditJson(Path.Combine(day, "manifest.json"), manifest => manifest["files"]["currents.csv"] = EnvironmentPackDigests.Sha256Hex(File.ReadAllBytes(path)));
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "DIGEST")));
		}

		[Test]
		public void ADayWhoseDepthDigestIsNotItsDepthSets_IsRefused()
		{
			var day = PythonPackFixture.Write(_root);
			var depth = EnvironmentPackJson.DepthFolderOf(day);

			// Another depth set in the same folder, sealed correctly, and the day resealed over it without updating depth_digest.
			EditJson(Path.Combine(depth, "manifest.json"), manifest => manifest["outside_depth_m"] = 25.0);
			ResealDepth(depth);
			Reseal(day);
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "manifest.json")));
			Assert.That(error.Message, Does.Contain("depth_digest"));
		}

		[TestCase("format", "\"ferrysim-env/2\"", "format")]
		[TestCase("region", "\"upper-bay\"", "region")]
		[TestCase("epoch_utc", "\"2025-07-14T00:00:00Z\"", "local midnight in New York")]
		[TestCase("epoch_utc", "\"2025-07-14T04:00:00+00:00\"", "epoch_utc")]
		[TestCase("date", "\"2025-07-15\"", "local midnight in New York")]
		[TestCase("time_coverage_s", "[90000, -3600]", "time_coverage_s")]
		[TestCase("notes", "\"one note\"", "notes")]
		[TestCase("digest", "\"" + PythonPackFixture.DayDigest + "\"", "not part of the format")]
		public void AMalformedDayManifest_IsRefused(string key, string json, string expected)
		{
			var day = PythonPackFixture.Write(_root);
			EditManifest(day, manifest => manifest[key] = JToken.Parse(json));
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "manifest.json")));
			Assert.That(error.Message, Does.Contain(expected));
		}

		[Test]
		public void AMissingManifestKey_IsRefused()
		{
			var day = PythonPackFixture.Write(_root);
			EditManifest(day, manifest => manifest.Remove("time_coverage_s"));
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day)).Message, Does.Contain("time_coverage_s is missing"));
		}

		[TestCase("north_m", 1.0, "m from the world position")]
		[TestCase("east_m", -0.05, "m from the world position")]
		[TestCase("lat", 0.0001, "m from the world position")]
		public void AStationWhosePositionDisagreesWithItsLatitudeAndLongitude_IsRefused(string key, double change, string expected)
		{
			var day = PythonPackFixture.Write(_root);
			EditManifest(day, manifest => manifest["water_level_stations"][0][key] = (double)manifest["water_level_stations"][0][key] + change);
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.Message, Does.Contain(expected));
			Assert.That(error.Message, Does.Contain("water_level_stations[0]"));
		}

		[Test]
		public void AStationListedTwice_OrWithAnUnknownKey_IsRefused()
		{
			var day = PythonPackFixture.Write(_root);
			EditManifest(day, manifest => ((JArray)manifest["current_stations"]).Add(manifest["current_stations"][0].DeepClone()));
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day)).Message, Does.Contain("appears twice"));

			var other = PythonPackFixture.Write(Path.Combine(_root, "other"));
			EditManifest(other, manifest => manifest["wind_stations"][0]["height_m"] = 10.0);
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(other)).Message, Does.Contain("height_m is not part of the format"));
		}

		[TestCase("file", "\"other.f32\"", "slice.f32")]
		[TestCase("rows", "4", "bytes")]
		[TestCase("nodata", "\"-9999\"", "nodata")]
		[TestCase("cell_m", "0.0", "cell size")]
		public void AMalformedGrid_IsRefused(string key, string json, string expected)
		{
			var day = PythonPackFixture.Write(_root);
			var depth = EnvironmentPackJson.DepthFolderOf(day);
			EditJson(Path.Combine(depth, "manifest.json"), manifest => manifest["grids"][0][key] = JToken.Parse(json));
			ResealDepth(depth);
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Does.StartWith(depth));
			Assert.That(error.Message, Does.Contain(expected));
		}

		[TestCase("outside_depth_m", "-1.0", "positive")]
		[TestCase("datum", "\"MLLW\"", "NAVD88")]
		[TestCase("format", "\"ferrysim-env-depth/2\"", "format")]
		public void AMalformedDepthManifest_IsRefused(string key, string json, string expected)
		{
			var day = PythonPackFixture.Write(_root);
			var depth = EnvironmentPackJson.DepthFolderOf(day);
			EditJson(Path.Combine(depth, "manifest.json"), manifest => manifest[key] = JToken.Parse(json));
			ResealDepth(depth);
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(depth, "manifest.json")));
			Assert.That(error.Message, Does.Contain(expected));
		}

		[TestCase("currents.csv", "\n", "\r\n", "carriage return")]
		[TestCase("currents.csv", "station_id,t_s,north_mps,east_mps", "station,t_s,north_mps,east_mps", "header")]
		[TestCase("currents.csv", "NYH1920,360,", "NYH1920,-10,", "does not increase")]
		[TestCase("currents.csv", "NYH1920,360,", "NYH1920,0,", "does not increase")]
		[TestCase("currents.csv", "1e-05", "nan", "not a finite number")]
		[TestCase("currents.csv", "1e-05", "inf", "not a finite number")]
		[TestCase("currents.csv", "-0.125", "-0,125", "fields")]
		[TestCase("currents.csv", "NYH1920,0,", "NYH1921,0,", "not in the manifest")]
		[TestCase("water_levels.csv", "predicted", "forecast", "observed or predicted")]
		[TestCase("wind.csv", ",7.5", ",-7.5", "negative")]
		[TestCase("wind.csv", "8518750,-3600", "8518751,-3600", "not in the manifest")]
		public void AMalformedSeriesFile_IsRefused(string file, string from, string to, string expected)
		{
			var day = PythonPackFixture.Write(_root);
			var path = Path.Combine(day, file);
			var text = File.ReadAllText(path);
			Assert.That(text, Does.Contain(from), "the edit applies");
			File.WriteAllText(path, text.Replace(from, to));
			Reseal(day);
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(path));
			Assert.That(error.Message, Does.Contain(expected));
		}

		[Test]
		public void SeriesRows_MustBeGroupedByStation_AndEveryStationNeedsRows()
		{
			var day = PythonPackFixture.Write(_root);
			EditManifest(day, manifest =>
			{
				var second = manifest["current_stations"][0].DeepClone();
				second["id"] = "NYH1921";
				((JArray)manifest["current_stations"]).Add(second);
			});
			var path = Path.Combine(day, "currents.csv");
			var noRows = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(noRows.Message, Does.Contain("'NYH1921' is in the manifest but has no rows"));

			var lines = File.ReadAllText(path).TrimEnd('\n').Split('\n');
			File.WriteAllText(path, string.Join("\n", lines[0], lines[1], "NYH1921,0,0.1,0.1", lines[2], lines[3], lines[4]) + "\n");
			Reseal(day);
			var ungrouped = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(ungrouped.Message, Does.Contain("appears again after other stations"));
		}

		[Test]
		public void ADayWithNoWindStation_Loads_WithStillAir()
		{
			// No station had wind data: wind_stations is [] and wind.csv has only its header.
			var day = PythonPackFixture.Write(_root);
			File.WriteAllText(Path.Combine(day, "wind.csv"), "station_id,t_s,north_mps,east_mps,gust_mps\n");
			EditManifest(day, manifest => manifest["wind_stations"] = new JArray());
			var pack = EnvironmentPackJson.Load(day);
			Assert.That(pack.WindStations.Count, Is.EqualTo(0));
			var environment = new DataEnvironment(pack, pack.EpochUtc);
			Assert.That(environment.SampleWind(0.0, 0.0, 0.0), Is.EqualTo(HorizontalVector.Zero));
			Assert.That(environment.Coverage.WindMissing, Is.True);

			// Written and loaded again, the day still has no wind.
			var again = EnvironmentPackJson.Load(EnvironmentPackWriter.Write(pack, EnvFolder()));
			Assert.That(again.WindStations.Count, Is.EqualTo(0));
		}

		[Test]
		public void ADayWithNoCurrentStation_IsRefused()
		{
			var day = PythonPackFixture.Write(_root);
			File.WriteAllText(Path.Combine(day, "currents.csv"), "station_id,t_s,north_mps,east_mps\n");
			EditManifest(day, manifest => manifest["current_stations"] = new JArray());
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "manifest.json")));
			Assert.That(error.Message, Does.Contain("currents.csv"));
		}

		[Test]
		public void Numbers_AreParsedWithTheInvariantCulture()
		{
			var day = PythonPackFixture.Write(_root);
			var previous = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
				var pack = EnvironmentPackJson.Load(day);
				Assert.That(pack.CurrentStations[0].Velocity.Value(1), Is.EqualTo(new HorizontalVector(0.75, -0.125)));
				Assert.That(pack.WindStations[0].Gust(1), Is.EqualTo(7.5));
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = previous;
			}
		}

		[Test]
		public void AMissingFile_IsRefused_NamingIt()
		{
			var day = PythonPackFixture.Write(_root);
			File.Delete(Path.Combine(day, "wind.csv"));
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "wind.csv")));
		}

		[TestCase(2024, 10, 7, 4)]
		[TestCase(2025, 1, 13, 5)]
		[TestCase(2025, 4, 14, 4)]
		[TestCase(2025, 7, 14, 4)]
		[TestCase(2025, 3, 9, 5)]
		[TestCase(2025, 3, 10, 4)]
		[TestCase(2025, 11, 2, 4)]
		[TestCase(2025, 11, 3, 5)]
		public void NewYorkMidnight_FollowsDaylightSavingTime(int year, int month, int day, int utcHour)
		{
			// Midnight on a changeover Sunday is still on the old offset: the clocks change at 02:00.
			Assert.That(EnvironmentPackJson.NewYorkMidnight(new DateTime(year, month, day)), Is.EqualTo(new DateTimeOffset(year, month, day, utcHour, 0, 0, TimeSpan.Zero)));
		}

		private string EnvFolder() => Path.Combine(_root, "written", "lower-east-river", "env");

		private static EnvironmentPack SyntheticPack()
		{
			var grid = PackFixtures.Grid("slice", -1000.0, -800.0, 5.0, 300, 320, (r, c) => (r * 7 + c * 3) % 101 == 0 ? float.NaN : (float)(-12.0 + 0.013 * r - 0.007 * c));
			var currents = new[]
			{
				PackFixtures.Current("NYH1920", 700.0, 1500.0, t => HorizontalVector.FromSpeedToward(1.1 * Math.Sin(t / 7116.0), 0.7)),
				PackFixtures.Current("NYH1921", 1900.0, 2400.0, t => HorizontalVector.FromSpeedToward(0.6 * Math.Sin(t / 7116.0 + 0.4), 0.5)),
			};
			var gauges = new[] { PackFixtures.Gauge("8518750", 66.6, 67.6, t => 0.68 * Math.Sin(t / 7116.0)) };
			var winds = new[] { PackFixtures.Wind("8518750", 66.6, 67.6, t => HorizontalVector.FromSpeedFrom(5.0 + Math.Sin(t / 3000.0), 3.9)) };
			return PackFixtures.Pack(PackFixtures.Depth(20.0, grid), currents, gauges, winds, new[] { "synthetic" });
		}

		// Rewrites the day manifest, then reseals the day.
		private static void EditManifest(string day, Action<JObject> edit)
		{
			EditJson(Path.Combine(day, "manifest.json"), edit);
			Reseal(day);
		}

		private static void EditJson(string path, Action<JObject> edit)
		{
			var manifest = JObject.Parse(File.ReadAllText(path));
			edit(manifest);
			File.WriteAllText(path, manifest.ToString().Replace("\r\n", "\n") + "\n");
		}

		// Recomputes a day's files entries and its DIGEST from what is on disk now.
		private static void Reseal(string day)
		{
			var manifestPath = Path.Combine(day, "manifest.json");
			var manifest = JObject.Parse(File.ReadAllText(manifestPath));
			var series = new List<byte[]>();
			foreach (var name in EnvironmentPackDigests.SeriesFiles)
			{
				var bytes = File.ReadAllBytes(Path.Combine(day, name));
				series.Add(bytes);
				if (manifest["files"] is JObject files && files[name] != null)
				{
					files[name] = EnvironmentPackDigests.Sha256Hex(bytes);
				}
			}

			File.WriteAllText(manifestPath, manifest.ToString().Replace("\r\n", "\n") + "\n");
			var depthDigest = File.ReadAllText(Path.Combine(EnvironmentPackJson.DepthFolderOf(day), "DIGEST")).Trim();
			var digest = EnvironmentPackDigests.DayDigest(File.ReadAllBytes(manifestPath), series, depthDigest);
			File.WriteAllBytes(Path.Combine(day, "DIGEST"), EnvironmentPackDigests.DigestFileBytes(digest));
		}

		// Recomputes a depth set's DIGEST from what is on disk now.
		private static void ResealDepth(string depth)
		{
			var manifestPath = Path.Combine(depth, "manifest.json");
			var manifest = JObject.Parse(File.ReadAllText(manifestPath));
			var names = new List<string>();
			var files = new List<byte[]>();
			foreach (var grid in (JArray)manifest["grids"])
			{
				var name = (string)grid["file"];
				var path = Path.Combine(depth, name);
				names.Add(name);
				files.Add(File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());
			}

			var digest = EnvironmentPackDigests.DepthDigest(File.ReadAllBytes(manifestPath), names, files);
			File.WriteAllBytes(Path.Combine(depth, "DIGEST"), EnvironmentPackDigests.DigestFileBytes(digest));
		}
	}
}
