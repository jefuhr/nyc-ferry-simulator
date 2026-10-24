using System.Collections;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (f) Determinism of an integrated run: a run recorded in the slice scene, with a landing in it, replays bit for
	/// bit, contact included, through <see cref="ReplayPlayer.Verify"/> and through the runner's own replay. Failure
	/// modes guarded: anything in the integration (placement, cameras, UI, origin shifts, frame times) feeding back into
	/// the physics, contact that differs on replay, and a replay against other faces passing.
	/// </summary>
	public class IntegratedReplayTests
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
		public IEnumerator ALandingRecordedInTheScene_ReplaysBitForBit_ContactIncluded()
		{
			var folder = SliceScene.FreshFolder("replay");
			var controls = new LandingApproachControls(_scene.Runner, 1.0);
			var live = new ContactLog(() => (VesselDynamicsSimulation)_scene.Runner.Driver.Simulation);
			_scene.StepsPerFrame(37);
			_scene.Restart(_scene.Approach(15.0, "integrated replay"), controls, folder);
			_scene.Runner.Driver.AddObserver(live);
			try
			{
				yield return _scene.FramesUntil(() => controls.HoldFromStep >= 0, 400, "the bow to touch the face");
				var hold = _scene.Runner.Driver.StepCount;
				yield return _scene.FramesUntil(() => _scene.Runner.Driver.StepCount >= hold + 1500, 200, "15 s on the face");
				_scene.Runner.EndRun();
			}
			finally
			{
				_scene.Runner.Driver.RemoveObserver(live);
			}

			Assert.That(_scene.Runner.RecordingError, Is.Null);
			var run = RecordedRun.Load(_scene.Runner.LastRecordingPath);
			Assert.That(run.Header[WorldGeometryDigest.HeaderKey], Is.EqualTo(_scene.Landings.Digest), "the recording names the slice's faces");
			Assert.That(live.ContactSteps.Count, Is.GreaterThan(100), "the run has a landing in it");

			var factory = new RememberingFactory();
			var replayed = new ContactLog(() => factory.Last);
			var result = ReplayPlayer.Verify(run, _scene.Vessel, factory, replayed, _scene.Landings.Geometry);
			Assert.That(result.Matches, Is.True, result.ToString());
			Assert.That(result.ReplayedDigest, Is.EqualTo(result.RecordedDigest), "every state, bit for bit");
			Assert.That(replayed.ContactSteps, Is.EqualTo(live.ContactSteps), "the same steps touch");
			Assert.That(replayed.Digest, Is.EqualTo(live.Digest), "every contact force and segment detail, bit for bit");
			Debug.Log($"Integrated replay: {result}; contact on {live.ContactSteps.Count} steps, contact digest {live.Digest}.");

			var openWater = ReplayPlayer.Verify(run, _scene.Vessel, new VesselDynamicsFactory());
			Assert.That(openWater.Matches, Is.False, "a replay without the faces is refused");
			Assert.That(openWater.ToString(), Does.Contain(_scene.Landings.Digest));

			// The runner's own replay in the scene, checked row by row as it plays.
			_scene.StepsPerFrame(50);
			_scene.Runner.InitializeReplay(run, _scene.Vessel);
			yield return _scene.FramesUntil(() => _scene.Runner.ReplayCompleted, 400, "the in-scene replay to finish");
			Assert.That(_scene.Runner.ReplayResult.Matches, Is.True, _scene.Runner.ReplayResult.ToString());
			Assert.That(_scene.Runner.ReplayResult.RowsCompared, Is.EqualTo(run.Rows.Count));
		}
	}
}
