using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Packs whose depth set has several grids, against golden vectors from an independent Python implementation
	/// (<see cref="PythonDepthPackFixture"/>). Failure modes guarded:
	/// <list type="bullet">
	/// <item>The readers disagreeing: a Python-written multi-grid pack refused, its digests computed differently, or its
	/// samples not reproduced within 1e-12 m on and next to seams and corners, in an overlap and next to unknown
	/// centres.</item>
	/// <item>Grids in the wrong order winning: the grid a point resolves to not the first that contains it, so swapping two
	/// grids in the manifest does not change it.</item>
	/// <item>An ambiguous pack loaded: a shared sample that differs or is unknown in one grid only, grids that meet off one
	/// lattice or with different cell sizes; or the refusal not naming the depth manifest.</item>
	/// </list>
	/// The pipeline checks the same samples and refusals (data-pipeline/tests/test_environment_depth_set.py).
	/// </summary>
	[TestFixture]
	public class DepthSetPackTests
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
		public void APythonWrittenMultiGridPack_Loads_WithPythonsDigests()
		{
			var pack = EnvironmentPackJson.Load(PythonDepthPackFixture.Write(_root));

			Assert.That(pack.Digest, Is.EqualTo(PythonDepthPackFixture.DayDigest));
			Assert.That(pack.Depth.Digest, Is.EqualTo(PythonDepthPackFixture.DepthDigest));
			Assert.That(pack.Name, Is.EqualTo("harbor/2025-07-14"));
			var ids = new List<string>();
			foreach (var grid in pack.Depth.Grids)
			{
				ids.Add(grid.Id);
			}

			Assert.That(ids, Is.EqualTo(new[] { "sw", "se", "nw", "ne", "cap", "far" }));
			Assert.That(pack.CurrentField, Is.Null);
		}

		[TestCaseSource(typeof(PythonDepthPackFixture), nameof(PythonDepthPackFixture.Samples))]
		public void TheDepthSet_ReproducesPythonsSample(PythonDepthPackFixture.Sample sample)
		{
			var depth = EnvironmentPackJson.LoadDepthSet(EnvironmentPackJson.DepthFolderOf(PythonDepthPackFixture.Write(_root)));
			var kind = depth.Sample(sample.North, sample.East, out var bed);
			var index = depth.GridAt(sample.North, sample.East);

			Assert.That(kind, Is.EqualTo(sample.Kind));
			Assert.That(index < 0 ? null : depth.Grids[index].Id, Is.EqualTo(sample.Grid));
			Assert.That(bed, Is.EqualTo(sample.BedElevation).Within(PythonDepthPackFixture.Tolerance));
		}

		[Test]
		public void SwappingTwoGrids_ChangesWhichOneAPointOnTheirSeamResolvesTo()
		{
			var day = PythonDepthPackFixture.Write(_root);
			var folder = EnvironmentPackJson.DepthFolderOf(day);
			Assert.That(EnvironmentPackJson.LoadDepthSet(folder).Sample(0.0, 15.0, out _), Is.EqualTo(BedSample.Unknown), "se first: its cell has an unknown corner");

			EditDepth(day, manifest =>
			{
				var grids = (JArray)manifest["grids"];
				var se = grids[1];
				var ne = grids[3];
				grids[1] = ne.DeepClone();
				grids[3] = se.DeepClone();
			});

			var depth = EnvironmentPackJson.Load(day).Depth;
			Assert.That(depth.Sample(0.0, 15.0, out var bed), Is.EqualTo(BedSample.Known), "ne first: its cell is all known");
			Assert.That(depth.Grids[depth.GridAt(0.0, 15.0)].Id, Is.EqualTo("ne"));
			Assert.That(bed, Is.EqualTo(Sample("just north of it, in ne").BedElevation).Within(1e-8));
		}

		// Each edit breaks the rule for two grids that meet; the grid's file and the manifest are resealed, so only the rule refuses it.
		[TestCase("nw", 1, -20.0f, null, "sw and nw", TestName = "AMultiGridPackThatBreaksTheRule_IsRefused(a shared sample differs)")]
		[TestCase("nw", 0, -9.0f, null, "sw and nw", TestName = "AMultiGridPackThatBreaksTheRule_IsRefused(a shared sample unknown in one grid only)")]
		[TestCase("nw", -1, 0.0f, "origin_east_m=-25.0", "one lattice", TestName = "AMultiGridPackThatBreaksTheRule_IsRefused(grids meeting half a cell off)")]
		[TestCase("cap", -1, 0.0f, "cell_m=5.0", "cell size", TestName = "AMultiGridPackThatBreaksTheRule_IsRefused(grids meeting with different cell sizes)")]
		public void AMultiGridPackThatBreaksTheRule_IsRefused(string grid, int sample, float value, string manifestEdit, string expected)
		{
			var day = PythonDepthPackFixture.Write(_root);
			var folder = EnvironmentPackJson.DepthFolderOf(day);
			EditDepth(day, manifest =>
			{
				foreach (var node in (JArray)manifest["grids"])
				{
					if ((string)node["id"] != grid)
					{
						continue;
					}

					if (sample >= 0)
					{
						var path = Path.Combine(folder, grid + ".f32");
						var bytes = File.ReadAllBytes(path);
						BitConverter.GetBytes(value).CopyTo(bytes, 4 * sample);
						File.WriteAllBytes(path, bytes);
						node["sha256"] = EnvironmentPackDigests.Sha256Hex(bytes);
					}

					if (manifestEdit != null)
					{
						var parts = manifestEdit.Split('=');
						node[parts[0]] = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
					}
				}
			});

			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day));
			Assert.That(error.File, Is.EqualTo(Path.Combine(folder, "manifest.json")));
			Assert.That(error.Message, Does.Contain(expected));
		}

		private static PythonDepthPackFixture.Sample Sample(string label) => Array.Find(PythonDepthPackFixture.Samples, s => s.Label == label);

		// Edits the depth manifest, then reseals the depth set and the day over it.
		private static void EditDepth(string day, Action<JObject> edit)
		{
			var folder = EnvironmentPackJson.DepthFolderOf(day);
			var manifestPath = Path.Combine(folder, "manifest.json");
			var manifest = JObject.Parse(File.ReadAllText(manifestPath));
			edit(manifest);
			File.WriteAllText(manifestPath, manifest.ToString().Replace("\r\n", "\n") + "\n");
			var names = new List<string>();
			var files = new List<byte[]>();
			foreach (var grid in (JArray)manifest["grids"])
			{
				names.Add((string)grid["file"]);
				files.Add(File.ReadAllBytes(Path.Combine(folder, (string)grid["file"])));
			}

			var depthDigest = EnvironmentPackDigests.DepthDigest(File.ReadAllBytes(manifestPath), names, files);
			File.WriteAllBytes(Path.Combine(folder, "DIGEST"), EnvironmentPackDigests.DigestFileBytes(depthDigest));

			var dayManifestPath = Path.Combine(day, "manifest.json");
			var dayManifest = JObject.Parse(File.ReadAllText(dayManifestPath));
			dayManifest["depth_digest"] = depthDigest;
			File.WriteAllText(dayManifestPath, dayManifest.ToString().Replace("\r\n", "\n") + "\n");
			var series = new List<byte[]>();
			foreach (var name in EnvironmentPackDigests.SeriesFiles)
			{
				series.Add(File.ReadAllBytes(Path.Combine(day, name)));
			}

			File.WriteAllBytes(Path.Combine(day, "DIGEST"),
				EnvironmentPackDigests.DigestFileBytes(EnvironmentPackDigests.DayDigest(File.ReadAllBytes(dayManifestPath), series, depthDigest)));
		}
	}
}
