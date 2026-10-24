using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.Slice.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The shipped scenario against the landing data it is built from (Astra review finding 2). Failure modes guarded:
	/// a scenario asset left behind when contacts.json changed (the approach aims where a face used to be), and the
	/// builder aiming at the wrong face of the stop (its first usable segment instead of the pipeline's primary
	/// face). Clearance alone catches neither: a stale pose can still start well clear of every face.
	/// </summary>
	public class SliceScenarioTests
	{
		private const string Rebuild = "the shipped scenario is not the builder's scenario for the imported landing data; rebuild the slice scene with " +
			"-executeMethod FerrySim.Slice.Authoring.SliceSceneBuilder.BuildFromCommandLine";

		[Test]
		public void TheShippedScenario_IsTheBuildersScenario_FromTheImportedLandingData()
		{
			var shipped = AssetDatabase.LoadAssetAtPath<ScenarioAsset>(SliceSceneBuilder.ScenarioPath);
			Assert.That(shipped, Is.Not.Null, SliceSceneBuilder.ScenarioPath + " is missing");
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);

			var expected = SliceSceneBuilder.DefaultScenario(landings, SliceTestData.LoadTClass());

			var actual = shipped.Settings;
			Assert.That(actual.name, Is.EqualTo(expected.name), Rebuild);
			Assert.That(actual.vesselFile, Is.EqualTo(expected.vesselFile), Rebuild);
			Assert.That(actual.startLatitude, Is.EqualTo(expected.startLatitude), Rebuild);
			Assert.That(actual.startLongitude, Is.EqualTo(expected.startLongitude), Rebuild);
			Assert.That(actual.startHeadingDegrees, Is.EqualTo(expected.startHeadingDegrees), Rebuild);
			Assert.That(actual.startSpeedKnots, Is.EqualTo(expected.startSpeedKnots), Rebuild);
			Assert.That(actual.scenarioStartUtc, Is.EqualTo(expected.scenarioStartUtc), Rebuild);
			Assert.That(JsonUtility.ToJson(actual.environment), Is.EqualTo(JsonUtility.ToJson(expected.environment)), Rebuild);
		}

		[Test]
		public void TheBuildersScenario_StartsBowOnToThePrimaryFace_AtTheApproachClearance()
		{
			var landings = LandingGeometry.Load(SliceTestData.ContactsPath, SliceTestData.SurfacesPath);
			var vessel = SliceTestData.LoadTClass();
			var face = landings.PrimaryFace(SliceSceneBuilder.ApproachStopId);

			var settings = SliceSceneBuilder.DefaultScenario(landings, vessel);

			var start = settings.InitialState(settings.environment.Create());
			var ids = face.Segments.Select(s => s.Id).ToArray();
			var clearance = FaceClearance.Check(vessel, start.North, start.East, start.Heading, landings.Geometry.Segments);
			var toFace = FaceClearance.Check(vessel, start.North, start.East, start.Heading, face.Segments.Select(s => s.Segment).ToArray());
			var chord = face.End - face.Start;
			// Against the water normal (east, -north) of the face's direction: toward (-east, north).
			var inward = Angles.WrapTwoPi(System.Math.Atan2(chord.North, -chord.East));
			Assert.That(ids, Does.Contain(clearance.NearestSegmentId), "the nearest face at the start is the primary face");
			Assert.That(toFace.Distance, Is.EqualTo(SliceSceneBuilder.ApproachBowClearance).Within(1e-6), "bow 40 m off the primary face");
			Assert.That(Angles.ToDegrees(System.Math.Abs(Angles.WrapPi(start.Heading - inward))), Is.LessThan(1e-9), "heading straight in, against the face's water normal");
			Assert.That(settings.name, Does.Contain(face.StopName));
		}
	}
}
