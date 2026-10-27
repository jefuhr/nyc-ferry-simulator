using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace FerrySim.Core.Tests.World
{
	// Failure modes: missing/extra/swapped bytes, rehashed invalid schema, nonfinite coordinates or nodes,
	// a masked cell being extrapolated, mutable resident data, wrong ring normals and lost optional evidence.

	/// <summary>Checks the harbor runtime contract.</summary>
	public class HarborRuntimePackTests
	{
		private string _folder;
		private static string Source => Path.Combine(RepoPaths.Root, "core/dotnet/FerrySim.Core.Tests/World");

		internal static readonly string[] Payloads =
		{
			"aids.json",
			"bridges.json",
			"contacts.json",
			"landings.json",
			"geoid.json",
			"geoid.f64",
			"geoid_cells.u8",
			"footprints.json"
		};
		[SetUp]
		public void Setup()
		{
			_folder = Path.Combine(Path.GetTempPath(), "harbor-runtime-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
			foreach (var path in Directory.GetFiles(Path.Combine(Source, "Fixtures")))
			{
				File.Copy(path, Path.Combine(_folder, Path.GetFileName(path)));
			}
		}

		[TearDown]
		public void Cleanup() => Directory.Delete(_folder, true);
		[Test]
		public void SmallFixture_PreservesTypesDoubleCoordinatesNullsAndEvidence()
		{
			var pack = HarborRuntimePackJson.Load(_folder);
			Assert.That(pack.Region, Is.EqualTo("harbor"));
			Assert.That(pack.Aids[0].Position.East, Is.EqualTo(25.25));
			Assert.That(pack.Aids[0].Properties["lights"].Items[0]["period_s"].Number, Is.EqualTo(4.0));
			Assert.That(pack.Bridges[0].Spans[0].Clearances[0]["underside_navd88_m"].IsNull, Is.True);
			Assert.That(pack.Bridges[0].Piers[0].Geometry.Lines[0].Count, Is.EqualTo(2));
			Assert.That(pack.Stops[0].Berths[0].StructureId, Is.EqualTo(pack.Barges[0].Id));
			Assert.That(pack.Footprints[0].CollisionAuthority, Is.False);
			Assert.That(pack.Footprints[0].VerticalBounds.Minimum, Is.Null);
			Assert.That(pack.Barges, Is.Not.InstanceOf<HarborBarge[]>());
		}

		[TestCase("manifest.json")]
		[TestCase("DIGEST")]
		[TestCase("aids.json")]
		[TestCase("bridges.json")]
		[TestCase("contacts.json")]
		[TestCase("landings.json")]
		[TestCase("geoid.json")]
		[TestCase("geoid.f64")]
		[TestCase("geoid_cells.u8")]
		[TestCase("footprints.json")]
		public void ChangedBytes_AreRefused(string name)
		{
			File.AppendAllText(Path.Combine(_folder, name), " ");
			Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
		}

		[TestCase(true)]
		[TestCase(false)]
		public void Inventory_MustBeExact(bool extra)
		{
			if (extra)
			{
				File.WriteAllText(Path.Combine(_folder, "extra.json"), "{}");
			}
			else
			{
				File.Delete(Path.Combine(_folder, "contacts.json"));
			}

			Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
		}

		[TestCase("mask_shape", "[3,2]")]
		[TestCase("mask_shape", "[2.0,2]")]
		[TestCase("rows", "3.0")]
		[TestCase("rows", "true")]
		[TestCase("rows", "1")]
		[TestCase("values_type", "'float32'")]
		[TestCase("layout", "'column-major'")]
		[TestCase("format", "'ferrysim-harbor-geoid/2'")]
		[TestCase("values_file", "'../geoid.f64'")]
		[TestCase("cell_m", "0")]
		[TestCase("origin_north_m", "NaN")]
		[TestCase("origin_east_m", "Infinity")]
		public void RehashedMalformedGeoid_IsStillRefused(string key, string json)
		{
			Change("geoid.json", node => node[key] = JToken.Parse(json));
			var error = Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
			Assert.That(error.File, Is.EqualTo(Path.Combine(_folder, "geoid.json")));
		}

		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		[TestCase(double.NegativeInfinity)]
		public void RehashedNonfiniteNode_IsRefused(double value)
		{
			var bytes = File.ReadAllBytes(Path.Combine(_folder, "geoid.f64"));
			Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, 0, 8);
			File.WriteAllBytes(Path.Combine(_folder, "geoid.f64"), bytes);
			Rehash(_folder);
			Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
		}

		[Test]
		public void RehashedInvalidMask_IsRefused()
		{
			File.WriteAllBytes(Path.Combine(_folder, "geoid_cells.u8"), new byte[] { 1, 2, 1, 1 });
			Rehash(_folder);
			Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
		}

		[TestCase("aids.json")]
		[TestCase("bridges.json")]
		[TestCase("landings.json")]
		[TestCase("footprints.json")]
		public void UnknownFormats_AreRefusedEvenWhenRehashed(string name)
		{
			Change(name, node => node["format"] = "future/99");
			Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
		}

		[Test]
		public void OptionalBerthMetadata_CanBeAbsent()
		{
			Change("landings.json", node => ((JObject)node["stops"][0]["berths"][0]).Remove("primary_face"));
			var berth = HarborRuntimePackJson.Load(_folder).Stops[0].Berths[0];
			Assert.That(berth.PrimaryFace, Is.False);
			Assert.That(berth.Record["primary_face"].IsNull, Is.True);
		}

		[Test]
		public void Footprints_CannotBecomeCollisionAuthority()
		{
			Change("footprints.json", node => node["footprints"][0]["collision_authority"] = true);
			Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
		}

		[Test]
		public void IndividualHashes_AreCheckedBesidesTheAggregate()
		{
			var manifest = JObject.Parse(File.ReadAllText(Path.Combine(_folder, "manifest.json")));
			manifest["files"]["aids.json"] = new string ('f', 64);
			File.WriteAllText(Path.Combine(_folder, "manifest.json"), manifest.ToString());
			WriteDigest(_folder);
			var error = Assert.Throws<HarborRuntimePackException>(() => HarborRuntimePackJson.Load(_folder));
			Assert.That(error.File, Is.EqualTo(Path.Combine(_folder, "aids.json")));
		}

		[Test]
		public void Lattice_UsesAdjacentActiveCellOnExactEdge_NeverCrossesHoleOrOuterEdge()
		{
			var grid = HarborRuntimePackJson.Load(_folder).Geoid;
			Assert.That(grid.TrySample(125, 375, out _), Is.False);
			Assert.That(grid.TrySample(125, 250, out var edge), Is.True);
			Assert.That(edge, Is.EqualTo(-29));
			Assert.That(grid.TrySample(250, 500, out _), Is.True);
			Assert.That(grid.TrySample(500, 500, out var corner), Is.True);
			Assert.That(corner, Is.EqualTo(-20));
			Assert.That(grid.TrySample(-1e-9, 0, out _), Is.False);
			Assert.That(grid.TrySample(500 + 1e-9, 500, out _), Is.False);
			Assert.That(grid.TrySample(double.NaN, 0, out _), Is.False);
		}

		[Test]
		public void PythonScalarParity_200SeededPoints() => AssertVectors(HarborRuntimePackJson.Load(_folder), "fixture");
		[Test]
		public void SamplingAndQueries_AllocateNothing()
		{
			var pack = HarborRuntimePackJson.Load(_folder);
			var buffer = new HarborFootprint[4];
			for (var i = 0; i < 100; i++)
			{
				pack.Geoid.TrySample(25, 25, out _);
				pack.Queries.Query(0, 0, 100, buffer, out _);
			}

			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 10000; i++)
			{
				pack.Geoid.TrySample(25, 25, out _);
				pack.Queries.Query(0, 0, 100, buffer, out _);
			}

			Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
			Assert.That(buffer[0].Id, Is.EqualTo(pack.Footprints[0].Id));
		}

		[Test]
		public void BargeRings_WaterNormalPointsOutOfSolid() => AssertRingNormals(HarborRuntimePackJson.Load(_folder));
		[Explicit("requires the copied wave 3a runtime export, independent of the small fixture")]
		[Test]
		public void RealExport_CountsParityAndNormals()
		{
			var pack = HarborRuntimePackJson.Load(Path.Combine(RepoPaths.Root, "data/processed/harbor/runtime"));
			Assert.That(new[] { pack.Stops.Count, pack.Stops.Sum(s => s.Berths.Count), pack.Barges.Count, pack.Aids.Count, pack.Bridges.Count, pack.Bridges.Sum(b => b.Spans.Count), pack.Footprints.Count }, Is.EqualTo(new[] { 24, 26, 28, 287, 148, 193, 5572 }));
			AssertVectors(pack, "harbor");
			AssertRingNormals(pack);
			TestContext.Out.WriteLine("Runtime DIGEST " + pack.Digest);
		}

		private static void AssertVectors(HarborRuntimePack pack, string name)
		{
			var rows = File.ReadAllLines(Path.Combine(Source, name + "-geoid-vectors.csv"));
			Assert.That(rows.Length, Is.EqualTo(201));
			foreach (var row in rows.Skip(1))
			{
				var values = row.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
				Assert.That(pack.Geoid.TrySample(values[0], values[1], out var actual), Is.EqualTo(values[2] == 1), row);
				if (values[2] == 1)
				{
					Assert.That(actual, Is.EqualTo(values[3]).Within(1e-9), row);
				}
			}
		}

		private static void AssertRingNormals(HarborRuntimePack pack)
		{
			foreach (var barge in pack.Barges)
			{
				foreach (var polygon in barge.Geometry.Polygons)
				{
					foreach (var ring in polygon.Rings)
					{
						for (var i = 1; i < ring.Count; i++)
						{
							var segment = new ContactSegment(ring[i - 1], ring[i], new ContactSurface(1, 0, 0, 1), i);
							var middle = (segment.Start + segment.End) * .5;
							var water = middle + segment.WaterNormal * .00001;
							var solid = middle - segment.WaterNormal * .00001;
							Assert.That(polygon.Contains(water.North, water.East), Is.False, barge.Id);
							Assert.That(polygon.Contains(solid.North, solid.East), Is.True, barge.Id);
						}
					}
				}
			}
		}

		private void Change(string name, Action<JObject> change)
		{
			var path = Path.Combine(_folder, name);
			var node = JObject.Parse(File.ReadAllText(path));
			change(node);
			File.WriteAllText(path, node.ToString());
			Rehash(_folder);
		}

		private static string Hash(byte[] bytes)
		{
			using var sha = SHA256.Create();
			return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
		}

		private static void Rehash(string folder)
		{
			var path = Path.Combine(folder, "manifest.json");
			var node = JObject.Parse(File.ReadAllText(path));
			foreach (var name in Payloads)
			{
				node["files"][name] = Hash(File.ReadAllBytes(Path.Combine(folder, name)));
			}

			File.WriteAllText(path, node.ToString());
			WriteDigest(folder);
		}

		private static void WriteDigest(string folder)
		{
			using var stream = new MemoryStream();
			foreach (var name in new[]
			{
				"manifest.json"
			}.Concat(Payloads))
			{
				var prefix = Encoding.UTF8.GetBytes(name + "\0");
				stream.Write(prefix, 0, prefix.Length);
				var data = File.ReadAllBytes(Path.Combine(folder, name));
				stream.Write(data, 0, data.Length);
				stream.WriteByte(0);
			}

			File.WriteAllText(Path.Combine(folder, "DIGEST"), Hash(stream.ToArray()) + "\n");
		}
	}
}
