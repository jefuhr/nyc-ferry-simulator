using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// Scenario setup, the water surface and the greybox sizes. Failure modes guarded: a start position from a flat
	/// lat/lon scaling instead of the tangent plane, degrees or knots leaking into the core, the wind's "from" read
	/// as "toward" (or the current's "toward" as "from"), a local-time start parsed as UTC, an environment the CLI
	/// would rebuild differently, a hard-coded geoid offset, and greybox sizes not taken from the vessel file.
	/// </summary>
	public class ScenarioTests
	{
		[Test]
		public void StartPosition_GoesThroughTheSharedTangentPlane()
		{
			var settings = new ScenarioSettings { startLatitude = 40.7225, startLongitude = -73.99, startHeadingDegrees = 400.0 };

			var expected = LocalTangentPlane.World.ToLocal(new GeodeticPosition(40.7225, -73.99));
			Assert.That(settings.StartPosition, Is.EqualTo(expected));
			// The pipeline's own figure for this point (geoid_offset.json, slice centre): 2498.888 N, 2112.177 E.
			Assert.That(settings.StartPosition.North, Is.EqualTo(2498.888).Within(0.001));
			Assert.That(settings.StartPosition.East, Is.EqualTo(2112.177).Within(0.001));
			Assert.That(settings.StartHeading, Is.EqualTo(Angles.ToRadians(40.0)).Within(1e-12), "headings wrap into [0, 2π)");
		}

		[Test]
		public void InitialState_AtRest_HasNoVelocity()
		{
			var settings = new ScenarioSettings { startHeadingDegrees = 90.0 };

			var state = settings.InitialState(settings.environment.Create());

			Assert.That(state.Surge, Is.EqualTo(0.0));
			Assert.That(state.Sway, Is.EqualTo(0.0));
			Assert.That(state.Heading, Is.EqualTo(Math.PI / 2.0));
			Assert.That(state.Time, Is.EqualTo(0.0));
		}

		[Test]
		public void InitialState_UnderwayInACurrent_MovesThroughTheWaterAtTheSetSpeed()
		{
			var settings = new ScenarioSettings
			{
				startHeadingDegrees = 0.0,
				startSpeedKnots = 10.0,
				environment = new ScenarioEnvironment { calm = false, currentSpeedKnots = 2.0, currentTowardDegrees = 90.0, depthMetres = 20.0 },
			};

			var state = settings.InitialState(settings.environment.Create());

			Assert.That(state.Surge, Is.EqualTo(Units.KnotsToMetresPerSecond(10.0)).Within(1e-12), "heading north: surge is the water speed");
			Assert.That(state.Sway, Is.EqualTo(Units.KnotsToMetresPerSecond(2.0)).Within(1e-12), "an easterly set pushes to starboard");
		}

		[Test]
		public void Environment_FollowsTheFlowConventions()
		{
			var environment = new ScenarioEnvironment
			{
				calm = false,
				currentSpeedKnots = 1.0,
				currentTowardDegrees = 90.0,
				windSpeedKnots = 10.0,
				windFromDegrees = 0.0,
				depthMetres = 8.0,
			}.Create();

			Assert.That(environment.Current.East, Is.EqualTo(Units.KnotsToMetresPerSecond(1.0)).Within(1e-12), "current flows toward its direction");
			Assert.That(environment.Current.North, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(environment.Wind.North, Is.EqualTo(-Units.KnotsToMetresPerSecond(10.0)).Within(1e-12), "a north wind blows toward the south");
			Assert.That(environment.Depth, Is.EqualTo(8.0));
		}

		[Test]
		public void Calm_IsTheCoreCalm()
		{
			var environment = new ScenarioEnvironment { calm = true, currentSpeedKnots = 5.0 }.Create();

			Assert.That(environment.Current, Is.EqualTo(UniformEnvironment.Calm.Current));
			Assert.That(environment.Wind, Is.EqualTo(UniformEnvironment.Calm.Wind));
			Assert.That(environment.Depth, Is.EqualTo(UniformEnvironment.Calm.Depth));
		}

		[Test]
		public void SpecText_IsTheCliEnvSyntax()
		{
			Assert.That(new ScenarioEnvironment { calm = true }.SpecText(), Is.EqualTo("calm"));
			var spec = new ScenarioEnvironment { calm = false, currentSpeedKnots = 1.5, currentTowardDegrees = 200.0, windSpeedKnots = 15.0, windFromDegrees = 300.0, depthMetres = 8.0 }.SpecText();
			Assert.That(spec, Is.EqualTo("current=1.5kn@200,wind=15kn@from300,depth=8m"));
		}

		[Test]
		public void StartUtc_IsReadAsUtc()
		{
			Assert.That(new ScenarioSettings { scenarioStartUtc = "2026-06-01T13:00:00Z" }.StartUtc.Offset, Is.EqualTo(TimeSpan.Zero));
			Assert.That(new ScenarioSettings { scenarioStartUtc = "2026-06-01T09:00:00-04:00" }.StartUtc.UtcDateTime, Is.EqualTo(new DateTime(2026, 6, 1, 13, 0, 0, DateTimeKind.Utc)));
			Assert.That(new ScenarioSettings { scenarioStartUtc = "2026-06-01 13:00" }.StartUtc.UtcDateTime.Hour, Is.EqualTo(13), "no offset means UTC, not machine local time");
			Assert.That(() => new ScenarioSettings { scenarioStartUtc = "half past nine" }.StartUtc, Throws.TypeOf<FormatException>());
		}

		[Test]
		public void Json_FillsGivenFieldsAndKeepsDefaults()
		{
			var settings = ScenarioAsset.FromJson("{\"vesselFile\": \"vessels/k-class.json\", \"startHeadingDegrees\": 45.5, \"environment\": {\"calm\": false, \"windSpeedKnots\": 12, \"windFromDegrees\": 270, \"depthMetres\": 15}}");

			Assert.That(settings.vesselFile, Is.EqualTo("vessels/k-class.json"));
			Assert.That(settings.startHeadingDegrees, Is.EqualTo(45.5));
			Assert.That(settings.startLatitude, Is.EqualTo(WorldOrigin.LatitudeDegrees), "missing fields keep their defaults");
			Assert.That(settings.environment.calm, Is.False);
			Assert.That(settings.environment.Wind.East, Is.EqualTo(Units.KnotsToMetresPerSecond(12.0)).Within(1e-12), "a west wind blows east");
			Assert.That(() => ScenarioAsset.FromJson("{\"startLatitude\": 123}"), Throws.ArgumentException);
			Assert.That(() => ScenarioAsset.FromJson("{\"environment\": {\"calm\": false, \"depthMetres\": 0}}"), Throws.ArgumentException);
		}

		[Test]
		public void ExampleScenarioJson_LoadsAndValidates()
		{
			var settings = ScenarioAsset.LoadJson("unity/Assets/FerrySim/Sim/Scenarios/east-river-ebb-crosswind.json");

			Assert.That(settings.name, Is.EqualTo("east-river-ebb-crosswind"));
			Assert.That(settings.environment.calm, Is.False);
			Assert.That(settings.environment.SpecText(), Is.EqualTo("current=2kn@200,wind=15kn@from300,depth=12m"));
			Assert.That(File.Exists(SimPaths.Resolve(settings.vesselFile)), Is.True);
		}

		[Test]
		public void VesselFile_ResolvesAgainstTheRepositoryRoot()
		{
			var path = SimPaths.Resolve("vessels/t-class.json");

			Assert.That(File.Exists(path), Is.True, path);
			Assert.That(SimPaths.Resolve("/absolute/path.json"), Is.EqualTo("/absolute/path.json"));
		}

		[Test]
		public void GeoidOffsetFile_GivesTheSliceCentreValue()
		{
			var folder = TestSupport.FreshArtifactFolder("geoid-offset");
			var path = Path.Combine(folder, "geoid_offset.json");
			File.WriteAllText(path, "{\"slice\": \"lower-east-river\", \"points\": {\"centre\": {\"lon\": -73.99, \"lat\": 40.7225, \"navd88_zero_ellipsoidal_height_m\": -31.8005}, \"sw\": {\"navd88_zero_ellipsoidal_height_m\": -31.9117}}, \"mean_m\": -31.8086}");

			Assert.That(ConstantWaterSurface.TryReadGeoidOffset(path, out var offset, out var problem), Is.True, problem);
			Assert.That(offset, Is.EqualTo(-31.8005));
			var surface = new ConstantWaterSurface(offset, 0.75);
			Assert.That(surface.EllipsoidalHeight(100.0, 200.0, 50.0), Is.EqualTo(-31.8005 + 0.75).Within(1e-12));

			Assert.That(ConstantWaterSurface.TryReadGeoidOffset(Path.Combine(folder, "missing.json"), out _, out var missing), Is.False);
			Assert.That(missing, Does.Contain("not found"));
			File.WriteAllText(path, "{\"points\": {}}");
			Assert.That(ConstantWaterSurface.TryReadGeoidOffset(path, out _, out _), Is.False, "no centre point, no guess");
		}

		[Test]
		public void GreyboxDimensions_ComeFromTheVesselFile()
		{
			var dimensions = GreyboxDimensions.From(TestSupport.LoadTClass());

			Assert.That(dimensions.LengthOverall, Is.EqualTo(26.0096).Within(1e-9));
			Assert.That(dimensions.Beam, Is.EqualTo(8.001).Within(1e-9));
			Assert.That(dimensions.DemihullBeam, Is.EqualTo(2.4).Within(1e-9));
			Assert.That(dimensions.DemihullSeparation, Is.EqualTo(5.6).Within(1e-9));
			Assert.That(dimensions.Depth, Is.EqualTo(2.8).Within(1e-9));
			Assert.That(dimensions.Draft, Is.EqualTo(0.9906).Within(1e-9));
			// Frontal area 42 m² over an 8 m beam is 5.25 m above water; less the 1.81 m freeboard leaves a 3.44 m cabin.
			Assert.That(dimensions.CabinHeight, Is.EqualTo(42.0 / 8.001 - (2.8 - 0.9906)).Within(1e-9));
			Assert.That(dimensions.CabinLength, Is.InRange(0.3 * 26.0096, 0.85 * 26.0096));
		}
	}
}
