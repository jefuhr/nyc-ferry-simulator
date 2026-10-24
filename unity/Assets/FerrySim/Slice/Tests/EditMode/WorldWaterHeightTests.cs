using System;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.World;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The bridge's water height from the world's water. Failure modes guarded: a constant geoid offset instead of
	/// the slice's grid (centimetres off across the slice), the tide level ignored, and a missing water surface
	/// silently putting the hull on the ellipsoid (32 m under the water).
	/// </summary>
	public class WorldWaterHeightTests
	{
		private const string DatumPath = "Assets/FerrySim/World/Data/LowerEastRiverDatum.asset";

		private GameObject _host;

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}
		}

		[Test]
		public void TheHeight_IsTheWorldsWaterSurface_AcrossTheSlice_AtEveryLevel()
		{
			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(DatumPath);
			Assert.That(datum != null && datum.IsPopulated, Is.True, "the world's datum asset is built");
			_host = new GameObject("water (test)");
			var water = _host.AddComponent<WaterSurface>();
			water.Configure(datum);
			var adapter = _host.AddComponent<WorldWaterHeight>();
			adapter.Water = water;

			// Pier 11, South Williamsburg and the slice's north-east corner: the geoid differs by centimetres.
			var points = new[] { (391.6, 750.4), (973.5, 3779.3), (5552.0, 4648.0) };
			foreach (var level in new[] { 0.0, 0.62, -0.85 })
			{
				water.WaterLevelNavd88 = level;
				foreach (var (north, east) in points)
				{
					var geodetic = LocalTangentPlane.World.ToGeodetic(north, east);
					var expected = datum.GeoidHeight(geodetic.LatitudeDegrees, geodetic.LongitudeDegrees) + level;
					Assert.That(adapter.EllipsoidalHeight(north, east, 123.0), Is.EqualTo(expected).Within(1e-9), $"({north}, {east}) at level {level}");
					Assert.That(adapter.EllipsoidalHeight(north, east, 0.0), Is.EqualTo(water.EllipsoidalHeight(north, east)));
				}
			}

			var corner = adapter.EllipsoidalHeight(5552.0, 4648.0, 0.0);
			var pier = adapter.EllipsoidalHeight(391.6, 750.4, 0.0);
			Assert.That(Math.Abs(corner - pier), Is.GreaterThan(0.01), "the grid varies across the slice; a constant would not");
		}

		[Test]
		public void WithoutAWaterSurface_ItFailsLoudly()
		{
			_host = new GameObject("adapter without water (test)");
			var adapter = _host.AddComponent<WorldWaterHeight>();

			var error = Assert.Throws<InvalidOperationException>(() => adapter.EllipsoidalHeight(0.0, 0.0, 0.0));
			Assert.That(error.Message, Does.Contain("WaterSurface"));
		}
	}
}
