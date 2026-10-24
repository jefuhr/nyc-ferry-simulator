using System;
using System.IO;
using System.Linq;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.World.EditorTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor.Compilation;
using UnityEngine;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// Cesium and the sim core must put every point in the same place. Failure modes guarded: the
	/// georeference at the wrong origin (Cesium's Denver default, swapped lat/lon, a non-zero height),
	/// a scale other than 1, east/north swapped or mirrored between the conventions' mapping and
	/// Cesium's frame, the world frame not pinned to the origin, and an asmdef reference to an
	/// assembly that does not exist being dropped silently (CesiumRuntime vs CesiumForUnity).
	/// </summary>
	public class GeoreferenceTests
	{
		private WorldSceneFixture _fixture;

		[SetUp]
		public void SetUp()
		{
			_fixture = new WorldSceneFixture();
			_fixture.Open();
		}

		[TearDown]
		public void TearDown() => _fixture.Close();

		[Test]
		public void Georeference_IsAtTheSharedWorldOrigin()
		{
			var georeference = _fixture.Find<CesiumGeoreference>();

			Assert.That(georeference.originPlacement, Is.EqualTo(CesiumGeoreferenceOriginPlacement.CartographicOrigin));
			Assert.That(georeference.originAuthority, Is.EqualTo(CesiumGeoreferenceOriginAuthority.LongitudeLatitudeHeight));
			Assert.That(georeference.latitude, Is.EqualTo(WorldOrigin.LatitudeDegrees).Within(1e-12));
			Assert.That(georeference.longitude, Is.EqualTo(WorldOrigin.LongitudeDegrees).Within(1e-12));
			Assert.That(georeference.height, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(georeference.scale, Is.EqualTo(1.0));
			Assert.That(georeference.transform.localToWorldMatrix, Is.EqualTo(Matrix4x4.identity));
		}

		[Test]
		public void GeoreferenceCheck_Pier11FromGtfs_AgreesWithinFiveCentimetres()
		{
			var check = _fixture.Find<GeoreferenceCheck>();
			Assert.That(check.ReferenceName, Is.EqualTo("Wall St/Pier 11"));
			Assert.That(check.ReferenceSource, Does.Contain("GTFS stop 87"));

			var result = check.Run();

			Assert.That(result.ErrorMetres, Is.LessThanOrEqualTo(GeoreferenceCheck.ToleranceMetres), result.ToString());
			Assert.That(result.Passed, Is.True, result.ToString());

			// Pier 11 is about 350 m north and 750 m east of the origin; a swapped axis would be ~400 m off.
			Assert.That(result.NorthEastDown.x, Is.EqualTo(351.06).Within(0.5));
			Assert.That(result.NorthEastDown.y, Is.EqualTo(748.43).Within(0.5));
		}

		[Test]
		public void GeoreferenceCheck_UsesTheGtfsCoordinatesFromThePipeline()
		{
			var path = Path.Combine(PipelineData.SliceDirectoryFor(PipelineData.DefaultSlice), "landings.geojson");
			if (!File.Exists(path))
			{
				Assert.Ignore($"{path} not built; the stored reference point is still checked by the other tests.");
			}

			var stop = ((JArray)JObject.Parse(File.ReadAllText(path))["features"])
				.Select(f => f["properties"])
				.Single(p => (string)p["stop_id"] == SceneBuilder.ReferenceStopId);
			var check = _fixture.Find<GeoreferenceCheck>();

			Assert.That(check.ReferenceName, Is.EqualTo((string)stop["stop_name"]));
			Assert.That(check.ReferenceLatitude, Is.EqualTo((double)stop["lat"]));
			Assert.That(check.ReferenceLongitude, Is.EqualTo((double)stop["lon"]));
		}

		[TestCase(40.695, -74.020, TestName = "Slice south-west corner")]
		[TestCase(40.695, -73.960, TestName = "Slice south-east corner")]
		[TestCase(40.750, -74.020, TestName = "Slice north-west corner")]
		[TestCase(40.750, -73.960, TestName = "Slice north-east corner")]
		[TestCase(40.741838, -73.961322, TestName = "Hunters Point South GTFS stop")]
		public void CesiumAndCoreAgree_AcrossTheSlice(double latitude, double longitude)
		{
			var georeference = _fixture.Find<CesiumGeoreference>();
			var frame = _fixture.Find<WorldFrame>();

			var result = GeoreferenceCheck.Measure(georeference, frame, latitude, longitude, -31.8);

			Assert.That(result.ErrorMetres, Is.LessThanOrEqualTo(GeoreferenceCheck.ToleranceMetres), result.ToString());
		}

		[Test]
		public void WorldFrame_MatchesCesiumLocalFrameAtTheOrigin()
		{
			// Cesium's frame is x east, y up, z north at the origin: the same matrix WorldFrame pins.
			var georeference = _fixture.Find<CesiumGeoreference>();
			var cesium = georeference.localToEcefMatrix;
			var ours = WorldFrame.LocalToEcef();

			for (var column = 0; column < 3; column++)
			{
				Assert.That(math.length(cesium[column].xyz - ours[column].xyz), Is.LessThan(1e-9), "axis " + column);
			}

			Assert.That(math.length(cesium.c3.xyz - ours.c3.xyz), Is.LessThan(1e-6), "origin");
		}

		[Test]
		public void WorldFrame_IsPinnedAndUnderTheGeoreference()
		{
			var frame = _fixture.Find<WorldFrame>();
			frame.MeasurePinError(out var position, out var rotation);

			Assert.That(position, Is.LessThan(0.001));
			Assert.That(rotation, Is.LessThan(1e-6));
			Assert.That(frame.GetComponentInParent<CesiumGeoreference>(), Is.Not.Null);
			Assert.That(frame.transform.localPosition.magnitude, Is.LessThan(1e-3f), "georeference sits at the world origin in the saved scene");
		}

		[Test]
		public void WorldFrameMapping_FollowsConventions()
		{
			var local = WorldFrame.ToLocal(north: 100.0, east: 200.0, down: 3.0);

			Assert.That(local, Is.EqualTo(new Vector3(200f, -3f, 100f)));
		}

		[Test]
		public void WorldAssembly_ReferencesCesiumForUnity()
		{
			var references = typeof(WorldFrame).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

			Assert.That(references, Does.Contain("CesiumForUnity"));
			Assert.That(typeof(CesiumGeoreference).Assembly.GetName().Name, Is.EqualTo("CesiumForUnity"));
			Assert.That(Attribute.GetCustomAttributes(typeof(WorldFrame), typeof(RequireComponent)).Cast<RequireComponent>().Select(r => r.m_Type0), Does.Contain(typeof(CesiumGlobeAnchor)));
		}

		[TestCase("World/FerrySim.Unity.World.asmdef")]
		[TestCase("World/Editor/FerrySim.Unity.World.Editor.asmdef")]
		[TestCase("World/Tests/EditMode/FerrySim.Unity.World.Tests.EditMode.asmdef")]
		[TestCase("World/Tests/PlayMode/FerrySim.Unity.World.Tests.PlayMode.asmdef")]
		public void AsmdefReferences_AllResolve(string relativePath)
		{
			// Unity drops an asmdef reference to a missing assembly without an error.
			var path = Path.Combine(Application.dataPath, "FerrySim", relativePath);
			var known = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Select(a => a.name)
				.Concat(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name))
				.ToHashSet();

			var references = ((JArray)JObject.Parse(File.ReadAllText(path))["references"]).Select(r => (string)r).ToArray();

			Assert.That(references, Is.Not.Empty);
			foreach (var reference in references)
			{
				Assert.That(known, Does.Contain(reference), $"{relativePath} references {reference}, which no assembly is called");
			}
		}
	}
}
