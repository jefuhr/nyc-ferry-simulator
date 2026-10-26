using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Traffic
{
	/// <summary>
	/// Loading and verifying traffic packs. Failure modes guarded:
	/// <list type="bullet">
	/// <item>A pack written by Python to the format that the C# reader refuses, or whose digest it computes differently.</item>
	/// <item>A changed byte in any file accepted: each file against its SHA-256 and size, the digest over the files and the epoch.</item>
	/// <item>Refusals that do not name the file.</item>
	/// <item>A manifest with the wrong format, date, epoch (not New York midnight) or area, an unknown or a missing key, or counts
	/// that do not add up.</item>
	/// <item>A layout the reader does not follow (fields moved), or one it should refuse (a field missing, overlapping, of the
	/// wrong type), and a truncated reports.bin.</item>
	/// <item>A roster with the wrong columns, an unknown class, a bad flag or a quoted name read wrong; reports out of order,
	/// in the wrong segment or not matching the roster.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class TrafficPackJsonTests
	{
		private string _root;
		private string _day;

		[SetUp]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), "ferrysim-core-tests", Guid.NewGuid().ToString("N"));
			_day = PythonTrafficFixture.Write(_root);
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

		private string FilePath(string name) => Path.Combine(_day, name);

		/// <summary>The manifest as JSON, its times kept as the text they are (JObject.Parse would read the epoch as a date).</summary>
		private JObject ReadManifest()
		{
			using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(FilePath("manifest.json")))) { DateParseHandling = DateParseHandling.None })
			{
				return JObject.Load(reader);
			}
		}

		/// <summary>Rewrites the manifest's sizes, hashes and digest for the files as they are now, so the deeper checks are reached.</summary>
		private void Reseal(Action<JObject> edit = null)
		{
			var manifest = ReadManifest();
			var files = new List<byte[]>();
			foreach (JObject entry in (JArray)manifest["files"])
			{
				var bytes = File.ReadAllBytes(FilePath((string)entry["name"]));
				entry["sha256"] = TrafficPackJson.Sha256Hex(bytes);
				entry["size_bytes"] = bytes.LongLength;
				files.Add(bytes);
			}

			manifest["digest"] = TrafficPackJson.Digest(files, (string)manifest["epoch_utc"]);
			edit?.Invoke(manifest);
			File.WriteAllText(FilePath("manifest.json"), manifest.ToString() + "\n");
		}

		private void Refused(string file, string expected)
		{
			var error = Assert.Throws<TrafficPackException>(() => TrafficPackJson.Load(_day));
			Assert.That(error.File, Is.EqualTo(FilePath(file)), error.Message);
			Assert.That(error.Message, Does.StartWith(FilePath(file)).And.Contain(expected));
		}

		[Test]
		public void APythonWrittenPack_Loads_WithPythonsDigest()
		{
			var pack = TrafficPackJson.Load(_day);
			Assert.That(pack.Digest, Is.EqualTo(PythonTrafficFixture.Digest));
			Assert.That(pack.Date, Is.EqualTo("2025-07-14"));
			Assert.That(pack.EpochUtc, Is.EqualTo(new DateTimeOffset(2025, 7, 14, 4, 0, 0, TimeSpan.Zero)));
			Assert.That((pack.CoverageFrom, pack.CoverageTo), Is.EqualTo((-3600.0, 90000.0)));
			Assert.That(pack.Vessels.Count, Is.EqualTo(18));
			var steady = pack.Vessels[0];
			Assert.That((steady.Index, steady.Mmsi, steady.Name, steady.AisType, steady.Class), Is.EqualTo((0, 100000001, "STEADY ONE", 70.0, TrafficClass.Cargo)));
			Assert.That((steady.Length, steady.Beam, steady.Draft), Is.EqualTo((120.0, 18.0, 6.5)));
			Assert.That((steady.FirstTime, steady.LastTime, steady.Reports, steady.Segments), Is.EqualTo((3600.0, 3840.0, 5, 1)));
			Assert.That((steady.Moving, steady.FerryCandidate), Is.EqualTo((true, false)));
			var quoted = pack.Vessel(100000004);
			Assert.That(quoted.Name, Is.EqualTo("NO \"SOG\", TEST"), "a quoted name with a comma and quotes");
			Assert.That(double.IsNaN(quoted.Beam) && double.IsNaN(quoted.Draft), Is.True, "blank dimensions are NaN");
			Assert.That(pack.Vessel(100000008).Name, Is.EqualTo(string.Empty));
			Assert.That(pack.Vessel(100000010).FerryCandidate, Is.True);
			Assert.That(pack.Vessel(100000007).Moving, Is.False);
			Assert.That(pack.Vessel(999999999), Is.Null);
			var reports = pack.Reports;
			Assert.That(reports.Time(0), Is.EqualTo(3600.0));
			Assert.That(reports.SpeedOverGround(0), Is.EqualTo((double)(float)5.0));
			Assert.That(double.IsNaN(reports.Heading(pack.Vessel(100000004).FirstReport)), Is.True, "a NaN heading stays NaN");
		}

		[Test]
		public void APackWrittenAndLoaded_KeepsEveryValue()
		{
			var original = TrafficPackJson.Load(_day);
			var copy = TrafficPackWriter.Write(original, Path.Combine(_root, "copy", "harbor", "traffic", "2025-07-14"));
			var loaded = TrafficPackJson.Load(copy);
			Assert.That(loaded.Vessels.Count, Is.EqualTo(original.Vessels.Count));
			for (var i = 0; i < original.Vessels.Count; i++)
			{
				var a = original.Vessels[i];
				var b = loaded.Vessels[i];
				Assert.That((b.Mmsi, b.Name, b.Class, b.Moving, b.FerryCandidate, b.Reports, b.Segments), Is.EqualTo((a.Mmsi, a.Name, a.Class, a.Moving, a.FerryCandidate, a.Reports, a.Segments)));
				Assert.That(BitConverter.DoubleToInt64Bits(b.Length), Is.EqualTo(BitConverter.DoubleToInt64Bits(a.Length)));
				Assert.That(BitConverter.DoubleToInt64Bits(b.AisType), Is.EqualTo(BitConverter.DoubleToInt64Bits(a.AisType)));
			}

			for (var i = 0; i < original.Reports.Count; i++)
			{
				Assert.That(BitConverter.DoubleToInt64Bits(loaded.Reports.North(i)), Is.EqualTo(BitConverter.DoubleToInt64Bits(original.Reports.North(i))));
				Assert.That(BitConverter.DoubleToInt64Bits(loaded.Reports.CourseOverGround(i)), Is.EqualTo(BitConverter.DoubleToInt64Bits(original.Reports.CourseOverGround(i))));
			}

			Assert.That(File.ReadAllText(Path.Combine(copy, "reports.json")), Is.EqualTo(File.ReadAllText(FilePath("reports.json"))), "the pipeline's layout file, byte for byte");
			Assert.That(File.ReadAllBytes(Path.Combine(copy, "reports.bin")), Is.EqualTo(File.ReadAllBytes(FilePath("reports.bin"))), "the same records");
			Assert.That(loaded.Digest, Is.EqualTo(original.Digest), "the same vessels.csv bytes too, so the same digest");
		}

		[TestCase("vessels.csv")]
		[TestCase("reports.bin")]
		[TestCase("reports.json")]
		public void AChangedByte_IsRefused_NamingTheFile(string name)
		{
			var bytes = File.ReadAllBytes(FilePath(name));
			bytes[bytes.Length / 2] ^= 0x01;
			File.WriteAllBytes(FilePath(name), bytes);
			Refused(name, "SHA-256");
		}

		[Test]
		public void AMissingFile_IsRefused_NamingIt()
		{
			File.Delete(FilePath("reports.bin"));
			Refused("reports.bin", "cannot be read");
		}

		[Test]
		public void AFileOfAnotherSize_IsRefused()
		{
			File.AppendAllText(FilePath("vessels.csv"), "\n");
			Refused("vessels.csv", "bytes, but manifest.json lists");
		}

		[Test]
		public void TheDigest_CoversTheFilesAndTheEpoch()
		{
			Reseal(m => m["digest"] = new string('a', 64));
			Refused("manifest.json", "manifest.digest is aaaa");
			var files = new[] { File.ReadAllBytes(FilePath("vessels.csv")), File.ReadAllBytes(FilePath("reports.bin")), File.ReadAllBytes(FilePath("reports.json")) };
			Assert.That(TrafficPackJson.Digest(files, "2025-07-14T04:00:00Z"), Is.EqualTo(PythonTrafficFixture.Digest));
			Assert.That(TrafficPackJson.Digest(files, "2025-07-15T04:00:00Z"), Is.Not.EqualTo(PythonTrafficFixture.Digest), "another epoch, another digest");
		}

		[Test]
		public void TheManifest_IsChecked()
		{
			var pristine = File.ReadAllText(FilePath("manifest.json"));
			void Edit(Action<JObject> edit)
			{
				File.WriteAllText(FilePath("manifest.json"), pristine);
				Reseal(edit);
			}

			Edit(m => m["format"] = "ferrysim-traffic/2");
			Refused("manifest.json", "manifest.format must be ferrysim-traffic/1");
			Edit(m => m["epoch_utc"] = "2025-07-14T05:00:00Z");
			Refused("manifest.json", "local midnight in New York on 2025-07-14 is 2025-07-14T04:00:00Z");
			Edit(m => m["date"] = "2025-7-14");
			Refused("manifest.json", "manifest.date must be YYYY-MM-DD");
			Edit(m => m["bbox"]["north"] = 41.0);
			Refused("manifest.json", "the format's area");
			Edit(m => m["surprise"] = 1);
			Refused("manifest.json", "manifest.surprise is not part of the format");
			Edit(m => m.Remove("cleaning"));
			Refused("manifest.json", "manifest.cleaning is missing");
			Edit(m => m["counts"]["source_reports"] = 1);
			Refused("manifest.json", "manifest.counts.source_reports is 1");
			Edit(m => m["counts"]["vessels_by_class"]["cargo"] = 2);
			Refused("manifest.json", "vessels_by_class does not match");
			Edit(m => { m["counts"]["reports"] = 5; m["counts"]["source_reports"] = 8; });
			Refused("reports.bin", "manifest.json counts 5");
			Edit(m => m["files"][0]["name"] = "roster.csv");
			Refused("manifest.json", "must be vessels.csv");
			Edit(null);
			Assert.That(TrafficPackJson.Load(_day).Digest, Is.EqualTo(PythonTrafficFixture.Digest), "and the untouched manifest still loads");
		}

		[Test]
		public void TheLayout_IsFollowed_AndAWrongOneRefused()
		{
			// Swap where sog_mps and cog_rad live, in reports.json and in every record: the reader follows the layout.
			var original = TrafficPackJson.Load(_day);
			var layout = JObject.Parse(File.ReadAllText(FilePath("reports.json")));
			layout["fields"][5]["offset_bytes"] = 36;
			layout["fields"][6]["offset_bytes"] = 32;
			File.WriteAllText(FilePath("reports.json"), layout.ToString() + "\n");
			var bytes = File.ReadAllBytes(FilePath("reports.bin"));
			for (var i = 0; i < bytes.Length; i += 44)
			{
				for (var b = 0; b < 4; b++)
				{
					var swap = bytes[i + 32 + b];
					bytes[i + 32 + b] = bytes[i + 36 + b];
					bytes[i + 36 + b] = swap;
				}
			}

			File.WriteAllBytes(FilePath("reports.bin"), bytes);
			Reseal();
			var moved = TrafficPackJson.Load(_day);
			for (var i = 0; i < original.Reports.Count; i++)
			{
				Assert.That(BitConverter.DoubleToInt64Bits(moved.Reports.SpeedOverGround(i)), Is.EqualTo(BitConverter.DoubleToInt64Bits(original.Reports.SpeedOverGround(i))));
				Assert.That(BitConverter.DoubleToInt64Bits(moved.Reports.CourseOverGround(i)), Is.EqualTo(BitConverter.DoubleToInt64Bits(original.Reports.CourseOverGround(i))));
			}

			layout["fields"][6]["offset_bytes"] = 34;
			File.WriteAllText(FilePath("reports.json"), layout.ToString() + "\n");
			Reseal();
			Refused("reports.json", "overlaps another field");
			layout["fields"][6]["offset_bytes"] = 32;
			layout["fields"][2]["type"] = "float32";
			File.WriteAllText(FilePath("reports.json"), layout.ToString() + "\n");
			Reseal();
			Refused("reports.json", "type of t_s must be float64");
			layout["fields"][2]["type"] = "float64";
			((JArray)layout["fields"]).RemoveAt(7);
			File.WriteAllText(FilePath("reports.json"), layout.ToString() + "\n");
			Reseal();
			Refused("reports.json", "lacks heading_rad");
		}

		[Test]
		public void ATruncatedReportsFile_IsRefused()
		{
			var bytes = File.ReadAllBytes(FilePath("reports.bin"));
			Array.Resize(ref bytes, bytes.Length - 10);
			File.WriteAllBytes(FilePath("reports.bin"), bytes);
			Reseal();
			Refused("reports.bin", "truncated");
		}

		[Test]
		public void TheRoster_IsChecked()
		{
			var text = File.ReadAllText(FilePath("vessels.csv"));
			File.WriteAllText(FilePath("vessels.csv"), text.Replace("index,mmsi,", "idx,mmsi,"));
			Reseal();
			Refused("vessels.csv", "line 1 must be");
			File.WriteAllText(FilePath("vessels.csv"), text.Replace(",cargo,", ",freighter,"));
			Reseal();
			Refused("vessels.csv", "class 'freighter'");
			File.WriteAllText(FilePath("vessels.csv"), text.Replace(",True,False\n", ",yes,False\n"));
			Reseal();
			Refused("vessels.csv", "moving 'yes' must be True or False");
			File.WriteAllText(FilePath("vessels.csv"), text.Replace("\n", "\r\n"));
			Reseal();
			Refused("vessels.csv", "CR characters");
		}

		[Test]
		public void ReportsThatBreakTheFormatsOrder_AreRefused()
		{
			var bytes = File.ReadAllBytes(FilePath("reports.bin"));
			// The second report of the first vessel moved before the first: time order broken.
			BitConverter.GetBytes(3500.0).CopyTo(bytes, 44 + 8);
			File.WriteAllBytes(FilePath("reports.bin"), bytes);
			Reseal();
			Refused("reports.bin", "strictly increasing time");

			bytes = File.ReadAllBytes(FilePath("reports.bin"));
			BitConverter.GetBytes(3660.0).CopyTo(bytes, 44 + 8);
			BitConverter.GetBytes(1).CopyTo(bytes, 44 + 4);
			File.WriteAllBytes(FilePath("reports.bin"), bytes);
			Reseal();
			Refused("reports.bin", "the 300 s gap rule puts it in segment 0");
		}

		[Test]
		public void PacksAreFoundByDay_InTheirRoots_InOrder()
		{
			var packs = new TrafficPacks(Path.Combine(_root, "missing"), _root);
			Assert.That(packs.DayFolder("2025-07-14"), Is.EqualTo(_day));
			Assert.That(packs.DayFolder("2025-07-15"), Is.Null);
			Assert.That(packs.Days(), Is.EqualTo(new[] { "2025-07-14" }));
			Assert.That(packs.Load("2025-07-14").Digest, Is.EqualTo(PythonTrafficFixture.Digest));
			var missing = Assert.Throws<FileNotFoundException>(() => packs.Load("2025-07-15"));
			Assert.That(missing.Message, Does.Contain(Path.Combine(_root, "missing", "harbor", "traffic", "2025-07-15", "manifest.json")).And.Contain(Path.Combine(_root, "harbor", "traffic", "2025-07-15", "manifest.json")));
			Assert.Throws<ArgumentException>(() => packs.DayFolder("../../etc"));
			Assert.That(TrafficPacks.IsDay("2025-02-30"), Is.False);
			Directory.Move(_day, Path.Combine(_root, "harbor", "traffic", "2025-07-15"));
			var wrongFolder = Assert.Throws<TrafficPackException>(() => packs.Load("2025-07-15"));
			Assert.That(wrongFolder.Message, Does.Contain("is the pack of 2025-07-14, but it is in the folder of 2025-07-15"));
		}
	}
}
