using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Packs with a current field (the ferrysim-env/1 extension approved 2026-10-25). Failure modes guarded:
	/// <list type="bullet">
	/// <item>A Python-written field pack refused, its digests computed differently, or its samples not reproduced (the
	/// sampling rule drifting between the pipeline and the core).</item>
	/// <item>A flipped byte anywhere in a field pack accepted, or refused without naming the file.</item>
	/// <item>A day naming another field, lacking a field station, or placing one beyond the tolerance, accepted.</item>
	/// <item>A field manifest with another format or method, an unknown key, no grids or slots outside 1 to 8, accepted.</item>
	/// <item>A cell whose stations sit behind or between empty slots taken as dry (golden samples on such cells).</item>
	/// <item>Writing and loading losing a field value or a derivation; a pack without a field not falling back to the station
	/// blend.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class CurrentFieldPackTests
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
		public void APythonWrittenFieldPack_Loads_WithPythonsDigests()
		{
			var pack = EnvironmentPackJson.Load(PythonFieldPackFixture.Write(_root));
			Assert.That(pack.Digest, Is.EqualTo(PythonFieldPackFixture.DayDigest));
			Assert.That(pack.Depth.Digest, Is.EqualTo(PythonFieldPackFixture.DepthDigest));
			Assert.That(pack.CurrentField, Is.Not.Null);
			Assert.That(pack.CurrentField.Digest, Is.EqualTo(PythonFieldPackFixture.FieldDigest));
			Assert.That(new[] { pack.CurrentField.Stations[0].Id, pack.CurrentField.Stations[1].Id, pack.CurrentField.Stations[2].Id }, Is.EqualTo(new[] { "A", "B", "C" }));
			var grid = pack.CurrentField.Grids[0];
			Assert.That((grid.Rows, grid.Columns, grid.Slots, grid.CellSize), Is.EqualTo((4, 5, 3, 20.0)));
			Assert.That(grid.IsWet(3, 4), Is.False);
			// Water wherever any slot names a station: behind two empty slots, and around an empty one.
			Assert.That((grid.Slot(2, 3, 0), grid.Slot(2, 3, 1), grid.Slot(2, 3, 2), grid.IsWet(2, 3)), Is.EqualTo((CurrentFieldGrid.EmptySlot, CurrentFieldGrid.EmptySlot, (byte)1, true)));
			Assert.That((grid.Slot(2, 0, 0), grid.Slot(2, 0, 1), grid.Slot(2, 0, 2), grid.IsWet(2, 0)), Is.EqualTo(((byte)1, CurrentFieldGrid.EmptySlot, (byte)2, true)));
			Assert.That(pack.CurrentField.Parameters.ShelterThreshold, Is.EqualTo(0.85));
			Assert.That(pack.CurrentField.Parameters.AcrossReach, Is.EqualTo(500.0));
			Assert.That(pack.CurrentField.Sources[0].Sha256, Is.EqualTo(PythonFieldPackFixture.DepthDigest));

			var b = pack.CurrentStations[1];
			Assert.That(b.Kind, Is.EqualTo("subordinate-prediction"));
			Assert.That((b.Derivation.ReferenceStation, b.Derivation.ReferenceBin, b.Derivation.FloodRatio, b.Derivation.EbbRatio),
				Is.EqualTo(("NYH1924", 6, 0.6, 1.12)));
			Assert.That(b.Derivation.Sources[0].Name, Is.EqualTo("offsets_B.json"));
			Assert.That(pack.CurrentStations[0].Derivation, Is.Null);
			Assert.That(new DataEnvironment(pack, pack.EpochUtc).CurrentField, Is.TypeOf<ChannelCurrentField>());
		}

		[TestCaseSource(typeof(PythonFieldPackFixture), nameof(PythonFieldPackFixture.Samples))]
		public void TheChannelField_ReproducesPythonsSample(PythonFieldPackFixture.Sample sample)
		{
			var pack = EnvironmentPackJson.Load(PythonFieldPackFixture.Write(_root));
			var field = new ChannelCurrentField(pack.CurrentField, pack.CurrentStations);
			var value = field.Sample(sample.North, sample.East, sample.PackTime);
			Assert.That(value.North, Is.EqualTo(sample.VelocityNorth).Within(PythonFieldPackFixture.Tolerance), "north");
			Assert.That(value.East, Is.EqualTo(sample.VelocityEast).Within(PythonFieldPackFixture.Tolerance), "east");

			// The environment reads the same field at the same pack time (scenario start at the epoch).
			var environment = new DataEnvironment(pack, pack.EpochUtc);
			Assert.That(environment.SampleCurrent(sample.North, sample.East, sample.PackTime), Is.EqualTo(value));
		}

		[Test]
		public void AFlippedByte_InAnyFieldPackFile_IsRefused_NamingTheFile()
		{
			var day = PythonFieldPackFixture.Write(_root);
			var env = Path.Combine(_root, "lower-east-river", "env");
			var files = new List<string>(Directory.GetFiles(env, "*", SearchOption.AllDirectories));
			files.Sort(StringComparer.Ordinal);
			Assert.That(files.Count, Is.EqualTo(12), "three manifests, three series files, a depth grid, the field's values and slots, three DIGEST files");

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

			Assert.That(EnvironmentPackJson.Load(day).Digest, Is.EqualTo(PythonFieldPackFixture.DayDigest), "the restored pack loads again");
			TestContext.Out.WriteLine($"{flips} single-byte changes across {files.Count} files, every one refused");
		}

		[Test]
		public void ADayNamingAnotherField_IsRefused()
		{
			var day = PythonFieldPackFixture.Write(_root);
			EditDay(day, manifest => manifest["field_digest"] = new string('5', 64));
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(day, "manifest.json")));
			Assert.That(error.Message, Does.Contain("field_digest"));
		}

		[TestCase(0.009, null)]
		[TestCase(0.011, "from where the current field solved it")]
		public void AFieldStation_MustBeWhereTheDayPlacesIt(double shift, string expected)
		{
			var day = PythonFieldPackFixture.Write(_root);
			var field = EnvironmentPackJson.FieldFolderOf(day);
			EditJson(Path.Combine(field, "manifest.json"), manifest => manifest["stations"][0]["north_m"] = (double)manifest["stations"][0]["north_m"] + shift);
			ResealField(field);
			EditDay(day, manifest => manifest["field_digest"] = File.ReadAllText(Path.Combine(field, "DIGEST")).Trim());
			if (expected == null)
			{
				Assert.That(EnvironmentPackJson.Load(day).CurrentField, Is.Not.Null);
				return;
			}

			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.Message, Does.Contain(expected).And.Contain("A"));
		}

		[Test]
		public void ADayWithoutAFieldStation_IsRefused()
		{
			var day = PythonFieldPackFixture.Write(_root);
			var path = Path.Combine(day, "currents.csv");
			var kept = new List<string>();
			foreach (var line in File.ReadAllLines(path))
			{
				if (!line.StartsWith("C,", StringComparison.Ordinal))
				{
					kept.Add(line);
				}
			}

			File.WriteAllText(path, string.Join("\n", kept) + "\n");
			EditDay(day, manifest =>
			{
				((JArray)manifest["current_stations"]).RemoveAt(2);
				manifest["files"]["currents.csv"] = EnvironmentPackDigests.Sha256Hex(File.ReadAllBytes(path));
			});
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.Message, Does.Contain("lacks C"));
		}

		[TestCase("method", "\"channel-blend/2\"", "method")]
		[TestCase("format", "\"ferrysim-env-field/2\"", "format")]
		[TestCase("extra", "1", "not part of the format")]
		[TestCase("grids", "[]", "at least one grid")]
		public void AMalformedFieldManifest_IsRefused(string key, string json, string expected)
		{
			var day = PythonFieldPackFixture.Write(_root);
			var field = EnvironmentPackJson.FieldFolderOf(day);
			EditJson(Path.Combine(field, "manifest.json"), manifest => manifest[key] = JToken.Parse(json));
			ResealField(field);
			EditDay(day, manifest => manifest["field_digest"] = File.ReadAllText(Path.Combine(field, "DIGEST")).Trim());
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(field, "manifest.json")));
			Assert.That(error.Message, Does.Contain(expected));
		}

		[Test]
		public void SlotsBeyondEight_AreRefused()
		{
			var day = PythonFieldPackFixture.Write(_root);
			var field = EnvironmentPackJson.FieldFolderOf(day);
			EditJson(Path.Combine(field, "manifest.json"), manifest => manifest["grids"][0]["slots"] = 9);
			ResealField(field);
			EditDay(day, manifest => manifest["field_digest"] = File.ReadAllText(Path.Combine(field, "DIGEST")).Trim());
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day)).Message, Does.Contain("must be 1 to 8"));
		}

		[Test]
		public void AFieldPackWrittenAndLoaded_KeepsEveryValue()
		{
			var original = EnvironmentPackJson.Load(PythonFieldPackFixture.Write(_root));
			var loaded = EnvironmentPackJson.Load(EnvironmentPackWriter.Write(original, Path.Combine(_root, "written", "lower-east-river", "env")));
			var a = original.CurrentField.Grids[0];
			var b = loaded.CurrentField.Grids[0];
			for (var r = 0; r < a.Rows; r++)
			{
				for (var c = 0; c < a.Columns; c++)
				{
					for (var j = 0; j < a.ValuesPerCell; j++)
					{
						Assert.That(BitConverter.SingleToInt32Bits(b.Value(r, c, j)), Is.EqualTo(BitConverter.SingleToInt32Bits(a.Value(r, c, j))));
					}

					for (var j = 0; j < a.Slots; j++)
					{
						Assert.That(b.Slot(r, c, j), Is.EqualTo(a.Slot(r, c, j)));
					}
				}
			}

			for (var k = 0; k < original.CurrentField.Stations.Count; k++)
			{
				var s = original.CurrentField.Stations[k];
				var t = loaded.CurrentField.Stations[k];
				Assert.That((t.Id, t.North, t.East, t.FrameNorth, t.FrameEast), Is.EqualTo((s.Id, s.North, s.East, s.FrameNorth, s.FrameEast)));
			}

			Assert.That(loaded.CurrentStations[1].Derivation.MaximumEbbMinutes, Is.EqualTo(8.0));
			Assert.That(loaded.CurrentField.Parameters.DepthExponent, Is.EqualTo(original.CurrentField.Parameters.DepthExponent));
			foreach (var sample in PythonFieldPackFixture.Samples)
			{
				Assert.That(new ChannelCurrentField(loaded.CurrentField, loaded.CurrentStations).Sample(sample.North, sample.East, sample.PackTime),
					Is.EqualTo(new ChannelCurrentField(original.CurrentField, original.CurrentStations).Sample(sample.North, sample.East, sample.PackTime)), sample.Label);
			}
		}

		[Test]
		public void APackWithoutAField_LoadsWithTheStationBlend()
		{
			var pack = EnvironmentPackJson.Load(PythonPackFixture.Write(_root));
			Assert.That(pack.Digest, Is.EqualTo(PythonPackFixture.DayDigest), "the digest rule without a field is unchanged");
			Assert.That(pack.CurrentField, Is.Null);
			Assert.That(new DataEnvironment(pack, pack.EpochUtc).CurrentField, Is.TypeOf<StationBlendCurrentField>());
		}

		private static void EditJson(string path, Action<JObject> edit)
		{
			var manifest = JObject.Parse(File.ReadAllText(path));
			edit(manifest);
			File.WriteAllText(path, manifest.ToString().Replace("\r\n", "\n") + "\n");
		}

		// Edits the day manifest, then reseals the day over the depth and field DIGEST files on disk.
		private static void EditDay(string day, Action<JObject> edit)
		{
			var manifestPath = Path.Combine(day, "manifest.json");
			EditJson(manifestPath, edit);
			var series = new List<byte[]>();
			foreach (var name in EnvironmentPackDigests.SeriesFiles)
			{
				series.Add(File.ReadAllBytes(Path.Combine(day, name)));
			}

			var depth = File.ReadAllText(Path.Combine(EnvironmentPackJson.DepthFolderOf(day), "DIGEST")).Trim();
			var field = File.ReadAllText(Path.Combine(EnvironmentPackJson.FieldFolderOf(day), "DIGEST")).Trim();
			var digest = EnvironmentPackDigests.DayDigest(File.ReadAllBytes(manifestPath), series, depth, field);
			File.WriteAllBytes(Path.Combine(day, "DIGEST"), EnvironmentPackDigests.DigestFileBytes(digest));
		}

		private static void ResealField(string field)
		{
			var manifestPath = Path.Combine(field, "manifest.json");
			var manifest = JObject.Parse(File.ReadAllText(manifestPath));
			var names = new List<string>();
			var files = new List<byte[]>();
			foreach (var grid in (JArray)manifest["grids"])
			{
				foreach (var key in new[] { "values_file", "stations_file" })
				{
					var name = (string)grid[key];
					names.Add(name);
					files.Add(File.ReadAllBytes(Path.Combine(field, name)));
				}
			}

			var digest = EnvironmentPackDigests.FieldDigest(File.ReadAllBytes(manifestPath), names, files);
			File.WriteAllBytes(Path.Combine(field, "DIGEST"), EnvironmentPackDigests.DigestFileBytes(digest));
		}
	}
}
