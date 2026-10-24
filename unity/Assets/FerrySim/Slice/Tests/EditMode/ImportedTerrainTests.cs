using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// Putting the importer's flat terrain on the world's curved water. Failure modes guarded: a correction applied
	/// to a prefab already on the water's datum (a later importer that maps heights the world's way would be moved
	/// twice), a missing or malformed manifest moving the terrain by a guess, a correction of the wrong sign or
	/// size (shoals still showing through the water, or the shore sunk), and a second correction of the same instance
	/// adding the offset again and copying its meshes again (Astra review finding 4).
	/// </summary>
	public class ImportedTerrainTests
	{
		private const string DatumPath = "Assets/FerrySim/World/Data/LowerEastRiverDatum.asset";
		private const string ManifestPath = "Assets/FerrySim/World/Generated/LowerEastRiver/import-manifest.json";

		private readonly List<Object> _created = new List<Object>();

		[TearDown]
		public void TearDown()
		{
			foreach (var created in _created)
			{
				if (created != null)
				{
					Object.DestroyImmediate(created);
				}
			}

			_created.Clear();
		}

		[Test]
		public void TheImportersManifest_DescribesTheFlatMapping_AtTheSliceCentreGeoid()
		{
			var manifest = File.ReadAllText(Path.Combine(SliceTestData.ProjectRoot, ManifestPath));
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(DatumPath);

			var zero = ImportedTerrain.FlatNavd88ZeroHeight(manifest, out var problem);

			Assert.That(problem, Is.Null);
			Assert.That(zero, Is.EqualTo(datum.GeoidCentre), "the importer's NAVD88 zero is the pipeline's slice-centre geoid sample");
		}

		[TestCase("{\"vertical_mapping\": \"Unity y = ellipsoidal height\", \"navd88_zero_y_m\": -31.8}", "vertical_mapping", TestName = "AnotherMapping_IsNotCorrected")]
		[TestCase("{\"vertical_mapping\": \"" + ImportedTerrain.FlatMapping + "\"}", "navd88_zero_y_m", TestName = "NoZeroHeight_IsNotCorrected")]
		[TestCase("{\"vertical_mapping\": ", "unreadable", TestName = "AnUnreadableManifest_IsNotCorrected")]
		[TestCase(null, "unreadable", TestName = "NoManifest_IsNotCorrected")]
		public void AManifestThatIsNotTheFlatMapping_GivesNoCorrection(string manifest, string reason)
		{
			var zero = ImportedTerrain.FlatNavd88ZeroHeight(manifest, out var problem);

			Assert.That(double.IsNaN(zero), Is.True);
			Assert.That(problem, Does.Contain(reason));
		}

		[Test]
		public void TheCorrection_IsCurvatureAndGeoid_SmallAtTheOrigin_AndOverAMetreFourKilometresOut()
		{
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(DatumPath);
			var zero = datum.GeoidCentre;

			var atOrigin = ImportedTerrain.Correction(datum, 0.0, 0.0, zero);
			var atSouthWilliamsburg = ImportedTerrain.Correction(datum, 973.5, 3779.3, zero);
			var atNorthEastCorner = ImportedTerrain.Correction(datum, 5553.0, 4648.0, zero);

			Assert.That(Math.Abs(atOrigin), Is.LessThan(0.15), "only the geoid's difference from the centre sample at the origin");
			Assert.That(atSouthWilliamsburg, Is.InRange(-1.3, -1.1), "about 1.2 m of curvature 3.9 km out");
			Assert.That(atNorthEastCorner, Is.InRange(-4.2, -3.9), "about 4.1 m at the far corner, as World/README.md gives for the water");
			var expected = WaterSurface.SurfaceUpAt(datum, 973.5, 3779.3, 0.0) - zero;
			Assert.That(atSouthWilliamsburg, Is.EqualTo(expected));
		}

		/// <summary>
		/// The review's case: a second Apply on the same instance would lower a vertex at South Williamsburg another
		/// 1.2 m and clone every mesh again. It must change nothing and copy nothing.
		/// </summary>
		[Test]
		public void ApplyingTwice_LeavesTheVerticesAndTransforms_AndCopiesNoMoreMeshes()
		{
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(DatumPath);
			var (root, tile, aid) = ImportLikeInstance();
			var owned = new List<Object>();

			var first = ImportedTerrain.Apply(root, datum, datum.GeoidCentre, owned);
			_created.AddRange(owned);
			var corrected = tile.GetComponent<MeshFilter>().sharedMesh;
			var vertices = corrected.vertices;
			var position = aid.transform.localPosition;
			var copies = owned.Count;

			var second = ImportedTerrain.Apply(root, datum, datum.GeoidCentre, owned);
			_created.AddRange(owned);

			Assert.That(first.MeshesCorrected, Is.EqualTo(1));
			Assert.That(first.ObjectsMoved, Is.EqualTo(1));
			Assert.That(vertices[0].y, Is.EqualTo(1.0f - 1.2f).Within(0.1f), "control: the first Apply lowered South Williamsburg by about 1.2 m");
			Assert.That(owned.Count, Is.EqualTo(copies), "no more mesh copies");
			Assert.That(tile.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(corrected), "the same corrected mesh");
			Assert.That(tile.GetComponent<MeshFilter>().sharedMesh.vertices, Is.EqualTo(vertices), "the vertices are where the first Apply put them");
			Assert.That(aid.transform.localPosition, Is.EqualTo(position), "the placed object is where the first Apply put it");
			Assert.That(second, Is.SameAs(first), "the second Apply reports the correction already made");
		}

		[Test]
		public void ApplyingAnotherCorrection_ToACorrectedInstance_Fails()
		{
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(DatumPath);
			var (root, tile, _) = ImportLikeInstance();
			var owned = new List<Object>();
			ImportedTerrain.Apply(root, datum, datum.GeoidCentre, owned);
			_created.AddRange(owned);
			var vertices = tile.GetComponent<MeshFilter>().sharedMesh.vertices;

			var error = Assert.Throws<InvalidOperationException>(() => ImportedTerrain.Apply(root, datum, datum.GeoidCentre + 1.0, owned));

			_created.AddRange(owned);
			Assert.That(error.Message, Does.Contain("already"));
			Assert.That(tile.GetComponent<MeshFilter>().sharedMesh.vertices, Is.EqualTo(vertices), "nothing moved");
		}

		/// <summary>An instance shaped like the import prefab: a mesh drawn in frame coordinates and an object placed by position.</summary>
		private (GameObject Root, GameObject Tile, GameObject Aid) ImportLikeInstance()
		{
			var root = Track(new GameObject("import (test)"));
			var tile = new GameObject("tile");
			tile.transform.SetParent(root.transform, false);
			var mesh = Track(new Mesh { name = "tile mesh" });
			// x east, z north: South Williamsburg, the north-east corner and the origin.
			mesh.vertices = new[] { new Vector3(3779.3f, 1.0f, 973.5f), new Vector3(4648.0f, -2.0f, 5553.0f), new Vector3(0.0f, 0.5f, 0.0f) };
			mesh.triangles = new[] { 0, 1, 2 };
			tile.AddComponent<MeshFilter>().sharedMesh = mesh;
			var aid = new GameObject("aid");
			aid.transform.SetParent(root.transform, false);
			aid.transform.localPosition = new Vector3(3779.3f, 2.0f, 973.5f);
			return (root, tile, aid);
		}

		private T Track<T>(T created) where T : Object
		{
			_created.Add(created);
			return created;
		}
	}
}
