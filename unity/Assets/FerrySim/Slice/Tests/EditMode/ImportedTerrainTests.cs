using System;
using System.IO;
using FerrySim.World;
using NUnit.Framework;
using UnityEditor;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// Putting the importer's flat terrain on the world's curved water. Failure modes guarded: a correction applied
	/// to a prefab already on the water's datum (a later importer that maps heights the world's way would be moved
	/// twice), a missing or malformed manifest moving the terrain by a guess, and a correction of the wrong sign or
	/// size (shoals still showing through the water, or the shore sunk).
	/// </summary>
	public class ImportedTerrainTests
	{
		private const string DatumPath = "Assets/FerrySim/World/Data/LowerEastRiverDatum.asset";
		private const string ManifestPath = "Assets/FerrySim/World/Generated/LowerEastRiver/import-manifest.json";

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
	}
}
