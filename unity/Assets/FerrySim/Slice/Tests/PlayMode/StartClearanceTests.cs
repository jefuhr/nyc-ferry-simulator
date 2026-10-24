using System.Collections;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The runtime start guard on the scene's real runner (Astra review finding 3): a run that starts overlapping a
	/// landing face, or less than 1 m from one, is ended before its first physics step, on reset too, and the reason
	/// stays readable. Failure modes guarded: the guard logging an error while the run steps anyway (the core never
	/// pushes a hull placed across a face, so it would sail through the landing), a reset re-running the bad start,
	/// the reason lost once the run is stopped, and a refused start leaving the runner unable to start a good run.
	/// </summary>
	public class StartClearanceTests
	{
		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator AStartOverlappingTheFace_IsEndedBeforeItsFirstStep_AlsoOnReset()
		{
			// Bow 1 m past the face line: the outline straddles the face.
			var face = _scene.ContactTestFace();
			var scenario = Shifted(_scene.Approach(10.0, "overlapping start"), face.Segment.WaterNormal * -11.0);
			yield return AssertRefused(scenario, face, overlapping: true);
		}

		[UnityTest]
		public IEnumerator AStartHalfAMetreFromTheFace_IsEndedBeforeItsFirstStep_AlsoOnReset()
		{
			var face = _scene.ContactTestFace();
			yield return AssertRefused(_scene.Approach(0.5, "start 0.5 m off"), face, overlapping: false);
		}

		private IEnumerator AssertRefused(ScenarioSettings scenario, LandingSegment face, bool overlapping)
		{
			var logged = new Regex($@"starts {(overlapping ? "overlapping" : "0\\.50 m from")} landing face {face.Id} ");
			_scene.StepsPerFrame(5);
			var controls = new ConstantControls(new ControlInputs(0.15, 0.15, 0.0));

			LogAssert.Expect(LogType.Error, logged);
			_scene.Restart(scenario, controls);
			yield return AssertNoStep("the first run", face, overlapping);

			LogAssert.Expect(LogType.Error, logged);
			var refusedRun = _scene.Runner.RunId;
			_scene.Runner.ResetRun();
			Assert.That(_scene.Runner.RunId, Is.Not.EqualTo(refusedRun), "a new run was started");
			yield return AssertNoStep("after a reset", face, overlapping);

			// Control: a clear start still runs on the same runner.
			_scene.Restart(_scene.Approach(15.0, "clear start"), controls);
			yield return SliceScene.Frames(5);
			Assert.That(_scene.Bootstrap.StartRefusal, Is.Null, "a clear start is not refused");
			Assert.That(_scene.Runner.Driver.StepCount, Is.GreaterThan(0));
			Assert.That(_scene.Runner.HasTelemetry, Is.True);
		}

		private IEnumerator AssertNoStep(string when, LandingSegment face, bool overlapping)
		{
			var clearance = _scene.Bootstrap.StartClearance;
			Assert.That(clearance.NearestSegmentId, Is.EqualTo(face.Id), $"{when}: the guard names the face");
			Assert.That(clearance.Overlaps, Is.EqualTo(overlapping), when);
			if (!overlapping)
			{
				Assert.That(clearance.Distance, Is.EqualTo(0.5).Within(1e-6), when);
			}

			Assert.That(_scene.Runner.IsRunActive, Is.False, $"{when}: the run is ended at its start");
			yield return SliceScene.Frames(10);
			Assert.That(_scene.Runner.Driver.StepCount, Is.EqualTo(0), $"{when}: no physics step");
			Assert.That(_scene.Runner.HasTelemetry, Is.False, $"{when}: no live telemetry");
			Assert.That(_scene.Panel.ShowingNoData, Is.True, $"{when}: the panel shows no data");
			Assert.That(_scene.Runner.Contact.StepsWithContact, Is.EqualTo(0), when);
			Assert.That(_scene.Bootstrap.StartRefusal, Does.Contain(face.Id.ToString()).And.Contain("1.0 m"), $"{when}: the reason is kept");
		}

		/// <summary>The scenario with its start moved by <paramref name="by"/> metres (north, east).</summary>
		private static ScenarioSettings Shifted(ScenarioSettings scenario, HorizontalVector by)
		{
			var start = scenario.StartPosition + by;
			var position = LocalTangentPlane.World.ToGeodetic(start.North, start.East);
			scenario.startLatitude = position.LatitudeDegrees;
			scenario.startLongitude = position.LongitudeDegrees;
			return scenario;
		}
	}
}
