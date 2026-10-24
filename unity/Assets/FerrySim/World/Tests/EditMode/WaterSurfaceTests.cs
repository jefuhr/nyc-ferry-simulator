using System;
using System.IO;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.World.EditorTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// Water height and datum. Failure modes guarded: the water at y = 0 or at +31.8 m (sign flip),
	/// a hard-coded geoid instead of the pipeline's, Earth curvature ignored (metres too high at the
	/// slice edge), the tide offset applied twice or not at all, and bilinear interpolation that misses
	/// the corners or swaps latitude and longitude.
	/// </summary>
	public class WaterSurfaceTests
	{
		// Mean Earth radius for an independent curvature estimate; the WGS84 radii of curvature here are
		// 6.363e6 (meridian) to 6.387e6 m (prime vertical), so this agrees with the ellipsoid to ~0.02 m at 7 km.
		private const double MeanRadius = 6.375e6;

		private SliceDatum _synthetic;
		private GameObject _waterObject;

		[SetUp]
		public void SetUp()
		{
			_synthetic = ScriptableObject.CreateInstance<SliceDatum>();
			_synthetic.Assign(new SliceDatumValues
			{
				SliceName = "synthetic",
				SouthLatitude = 40.0,
				NorthLatitude = 41.0,
				WestLongitude = -75.0,
				EastLongitude = -74.0,
				GeoidModel = "test",
				GeoidSouthWest = -30.0,
				GeoidSouthEast = -32.0,
				GeoidNorthWest = -34.0,
				GeoidNorthEast = -40.0,
				MeanHighWaterNavd88 = 0.6,
				MeanLowerLowWaterNavd88 = -0.8,
			});
		}

		[TearDown]
		public void TearDown()
		{
			if (_waterObject != null)
			{
				UnityEngine.Object.DestroyImmediate(_waterObject);
			}

			UnityEngine.Object.DestroyImmediate(_synthetic);
		}

		[Test]
		public void GeoidHeight_HitsCornersAndInterpolatesBilinearly()
		{
			Assert.That(_synthetic.GeoidHeight(40.0, -75.0), Is.EqualTo(-30.0).Within(1e-12), "south-west");
			Assert.That(_synthetic.GeoidHeight(40.0, -74.0), Is.EqualTo(-32.0).Within(1e-12), "south-east");
			Assert.That(_synthetic.GeoidHeight(41.0, -75.0), Is.EqualTo(-34.0).Within(1e-12), "north-west");
			Assert.That(_synthetic.GeoidHeight(41.0, -74.0), Is.EqualTo(-40.0).Within(1e-12), "north-east");
			Assert.That(_synthetic.GeoidHeight(40.5, -74.5), Is.EqualTo(-34.0).Within(1e-12), "centre is the corner mean");
			Assert.That(_synthetic.GeoidHeight(40.25, -75.0), Is.EqualTo(-31.0).Within(1e-12), "west edge varies with latitude only");
		}

		[Test]
		public void GeoidHeight_HoldsEdgeValuesOutsideTheBox()
		{
			Assert.That(_synthetic.GeoidHeight(39.0, -76.0), Is.EqualTo(-30.0).Within(1e-12));
			Assert.That(_synthetic.GeoidHeight(42.0, -73.0), Is.EqualTo(-40.0).Within(1e-12));
		}

		[Test]
		public void UnpopulatedDatum_Throws()
		{
			var empty = ScriptableObject.CreateInstance<SliceDatum>();
			try
			{
				Assert.Throws<InvalidOperationException>(() => empty.GeoidHeight(40.7, -74.0));
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(empty);
			}
		}

		[Test]
		public void CommittedDatum_IsPopulatedFromThePipeline()
		{
			var datum = LoadCommittedDatum();

			Assert.That(datum.IsPopulated, Is.True);
			Assert.That(datum.GeoidModel, Does.Contain("GEOID18"));
			Assert.That(datum.DatumStation, Does.StartWith("8518750"));

			// NAVD88 zero is about 32 m below the ellipsoid in New York; the pipeline's own check allows -35 to -29.
			Assert.That(datum.GeoidHeight(datum.CentreLatitude, datum.CentreLongitude), Is.InRange(-35.0, -29.0));

			// Interpolating the corners reproduces the pipeline's separate centre sample.
			Assert.That(datum.GeoidHeight(datum.CentreLatitude, datum.CentreLongitude), Is.EqualTo(datum.GeoidCentre).Within(0.02));

			// The Battery, epoch 1983-2001: MHW 2.445 and MLLW 1.002 above station datum, NAVD88 at 1.848.
			Assert.That(datum.MeanHighWaterNavd88, Is.EqualTo(0.597).Within(1e-9));
			Assert.That(datum.MeanLowerLowWaterNavd88, Is.EqualTo(-0.846).Within(1e-9));
		}

		[Test]
		public void CommittedDatum_MatchesGeoidOffsetJson()
		{
			var path = Path.Combine(PipelineData.SliceDirectoryFor(PipelineData.DefaultSlice), "geoid_offset.json");
			if (!File.Exists(path))
			{
				Assert.Ignore($"{path} not built; the committed datum is checked by the other tests.");
			}

			var datum = LoadCommittedDatum();
			var points = (JObject)JObject.Parse(File.ReadAllText(path))["points"];
			foreach (var corner in new[] { "sw", "se", "nw", "ne", "centre" })
			{
				var point = points[corner];
				var expected = (double)point["navd88_zero_ellipsoidal_height_m"];
				var actual = datum.GeoidHeight((double)point["lat"], (double)point["lon"]);
				var tolerance = corner == "centre" ? 0.02 : 1e-9;
				Assert.That(actual, Is.EqualTo(expected).Within(tolerance), corner);
			}
		}

		[Test]
		public void SurfaceUp_AtOrigin_IsTheGeoidOffset()
		{
			var datum = LoadCommittedDatum();

			var up = WaterSurface.SurfaceUpAt(datum, 0.0, 0.0, 0.0);
			var expected = datum.GeoidHeight(WorldOrigin.LatitudeDegrees, WorldOrigin.LongitudeDegrees);

			Assert.That(up, Is.EqualTo(expected).Within(1e-6));
			Assert.That(up, Is.InRange(-32.2, -31.4), "water must sit about 31.8 m below the ellipsoid, not at 0 or above it");
		}

		[Test]
		public void SurfaceUp_AtFarCorner_DropsWithEarthCurvature()
		{
			var datum = LoadCommittedDatum();
			var corner = LocalTangentPlane.World.ToLocal(new GeodeticPosition(datum.NorthLatitude, datum.EastLongitude));

			var up = WaterSurface.SurfaceUpAt(datum, corner.North, corner.East, 0.0);
			var distance = Math.Sqrt(corner.North * corner.North + corner.East * corner.East);
			var curvatureDrop = distance * distance / (2.0 * MeanRadius);

			Assert.That(curvatureDrop, Is.GreaterThan(3.5), "the far corner is 7 km out");
			Assert.That(up, Is.EqualTo(datum.GeoidHeight(datum.NorthLatitude, datum.EastLongitude) - curvatureDrop).Within(0.05));
		}

		[Test]
		public void WaterLevel_RaisesSurfaceAndEllipsoidalHeightByTheOffset()
		{
			var water = CreateWater(LoadCommittedDatum());
			const double north = 351.0;
			const double east = 748.4;
			var before = water.SurfaceUp(north, east);
			var ellipsoidalBefore = water.EllipsoidalHeight(north, east);
			WaterSurface raised = null;
			water.LevelChanged += surface => raised = surface;

			water.WaterLevelNavd88 = 1.25;

			Assert.That(water.SurfaceUp(north, east) - before, Is.EqualTo(1.25).Within(1e-4));
			Assert.That(water.EllipsoidalHeight(north, east) - ellipsoidalBefore, Is.EqualTo(1.25).Within(1e-12));
			Assert.That(water.transform.localPosition.y, Is.EqualTo(1.25f).Within(1e-6f), "the mesh is built at NAVD88 zero and raised by the level");
			Assert.That(raised, Is.SameAs(water));
		}

		[Test]
		public void EllipsoidalHeight_IsGeoidPlusLevel()
		{
			var water = CreateWater(LoadCommittedDatum());
			water.WaterLevelNavd88 = -0.846;

			var geodetic = LocalTangentPlane.World.ToGeodetic(351.0, 748.4);
			var expected = water.Datum.GeoidHeight(geodetic.LatitudeDegrees, geodetic.LongitudeDegrees) - 0.846;

			Assert.That(water.EllipsoidalHeight(351.0, 748.4), Is.EqualTo(expected).Within(1e-9));
		}

		[Test]
		public void WaterLevel_RejectsNaN()
		{
			var water = CreateWater(LoadCommittedDatum());

			Assert.Throws<ArgumentOutOfRangeException>(() => water.WaterLevelNavd88 = double.NaN);
		}

		[Test]
		public void Mesh_VerticesLieOnTheCurvedSurface()
		{
			var water = CreateWater(LoadCommittedDatum());
			var mesh = water.Mesh;
			Assert.That(mesh, Is.Not.Null);
			Assert.That(mesh.vertexCount, Is.GreaterThan(1000));

			WaterSurface.SliceBounds(water.Datum, out var minNorth, out var maxNorth, out var minEast, out var maxEast);
			var checkedInside = 0;
			var vertices = mesh.vertices;
			for (var i = 0; i < vertices.Length; i += 97)
			{
				var vertex = vertices[i];
				if (vertex.z < minNorth || vertex.z > maxNorth || vertex.x < minEast || vertex.x > maxEast)
				{
					continue;
				}

				// Float vertices within a few kilometres are good to about a millimetre.
				var expected = WaterSurface.SurfaceUpAt(water.Datum, vertex.z, vertex.x, 0.0);
				Assert.That(vertex.y, Is.EqualTo(expected).Within(0.005), $"vertex {i} at E {vertex.x} N {vertex.z}");
				checkedInside++;
			}

			Assert.That(checkedInside, Is.GreaterThan(20));
			Assert.That(mesh.bounds.min.x, Is.LessThan(minEast - 10000.0), "water should reach well past the slice");
			Assert.That(mesh.bounds.max.z, Is.GreaterThan(maxNorth + 10000.0));
		}

		[Test]
		public void AxisNodes_AreIncreasingAndCoverTheReach()
		{
			var nodes = WaterSurface.AxisNodes(-500.0, 5000.0, 100.0, 1000.0, 20000.0, 1.4);

			for (var i = 1; i < nodes.Count; i++)
			{
				Assert.That(nodes[i], Is.GreaterThan(nodes[i - 1]));
			}

			Assert.That(nodes[0], Is.EqualTo(-20500.0).Within(1e-9));
			Assert.That(nodes[nodes.Count - 1], Is.EqualTo(25000.0).Within(1e-9));
			Assert.That(nodes.Count, Is.LessThan(150), "keep the mesh small");
		}

		private WaterSurface CreateWater(SliceDatum datum)
		{
			_waterObject = new GameObject("WaterSurfaceTest", typeof(MeshFilter), typeof(MeshRenderer));
			var water = _waterObject.AddComponent<WaterSurface>();
			water.Configure(datum);
			return water;
		}

		private static SliceDatum LoadCommittedDatum()
		{
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(GreyboxBuilder.DatumPath);
			Assert.That(datum, Is.Not.Null, GreyboxBuilder.DatumPath + " is missing; run FerrySim > World > Rebuild All.");
			return datum;
		}
	}
}
