using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The scene's scenario hook point, launcher and pack runs. Failure modes guarded: the bootstrap not exposing the
	/// catalog, launcher and readout a picker binds to; a launch that keeps the old run (same run id, recording left open)
	/// or that ends it and then fails; a refused start reported as started; the scene's own scenario not on its pack day,
	/// or the drawn water and barges not at its tide; a K-class run drawn and viewed from the T-class's greybox and eye; and
	/// garbage per frame on a pack run, in open water and in contact (the runner, its placement, the tide and the readout).
	/// </summary>
	public class ScenarioLauncherTests
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

		private ScenarioSettings Create(string stop, string date, int minutes, string vessel = "t-class")
		{
			Assert.That(_scene.Bootstrap.ScenarioCatalog.TryCreate(new ScenarioChoice(stop, date, minutes, vessel), out var settings, out var problem), Is.True, problem);
			return settings;
		}

		[UnityTest]
		public IEnumerator TheSceneOpensOnItsPackDay_WithTheWaterAndBargesAtTheTide_AndExposesTheHookPoints()
		{
			var bootstrap = _scene.Bootstrap;
			var runner = _scene.Runner;
			yield return _scene.FramesUntil(() => runner.HasEnvironment, 300, "the scene's run to step");

			Assert.That(bootstrap.ScenarioCatalog, Is.SameAs(bootstrap), "a UI can reference the bootstrap as the catalog");
			Assert.That(bootstrap.ScenarioLauncher, Is.SameAs(bootstrap));
			Assert.That(bootstrap.Scenarios.Catalog.Excluded, Is.Empty, string.Join("\n", bootstrap.Scenarios.Catalog.Excluded));
			Assert.That(bootstrap.EnvironmentSource, Is.SameAs(runner));
			Assert.That(bootstrap.ScenarioCatalog.Landings, Is.Not.Empty);
			Assert.That(bootstrap.ScenarioCatalog.Days, Is.Not.Empty);
			Assert.That(bootstrap.ScenarioCatalog.Vessels.Select(v => v.Id), Is.EqualTo(new[] { "t-class", "k-class" }));

			var readout = runner.Environment;
			Assert.That(runner.Scenario.environment.pack, Is.EqualTo("lower-east-river/2025-07-20"));
			Assert.That(readout.Kind, Is.EqualTo(EnvironmentKind.Pack));
			Assert.That(readout.PackName, Is.EqualTo("lower-east-river/2025-07-20"));
			Assert.That(readout.LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-4.0)));
			yield return null;
			Assert.That(_scene.Water.WaterLevelNavd88, Is.EqualTo(runner.Environment.WaterLevelNavd88).Within(1e-9), "the drawn water at the tide");
			Assert.That(_scene.Water.WaterLevelNavd88, Is.GreaterThan(0.5), "18:48 is under two hours after high water");
			var landings = Object.FindObjectsByType<FloatOnWater>(FindObjectsSortMode.None);
			Assert.That(landings, Is.Not.Empty);
			foreach (var floating in landings)
			{
				// FloatOnWater follows in LateUpdate; the tide moves well under 0.1 mm a frame.
				Assert.That(floating.transform.localPosition.y, Is.EqualTo(_scene.Water.WaterLevelNavd88).Within(1e-4), floating.name + " rides the tide");
			}
		}

		[UnityTest]
		public IEnumerator Launching_EndsTheRunAndStartsANewOne_AndARefusedStartLeavesTheRunGoing()
		{
			var runner = _scene.Runner;
			var folder = SliceScene.FreshFolder("launcher");
			runner.RecordingDirectory = folder;
			runner.RecordRuns = true;
			runner.ResetRun();
			yield return _scene.FramesUntil(() => runner.HasTelemetry, 300, "a recorded run");
			var first = runner.RunId;
			var ended = 0;
			runner.RunEnded += () => ended++;

			Assert.That(_scene.Bootstrap.ScenarioLauncher.TryStart(Create("20", "2025-07-14", 600), out var problem), Is.True, problem);

			Assert.That(ended, Is.EqualTo(1), "the old run ended");
			Assert.That(runner.RunId, Is.GreaterThan(first));
			Assert.That(File.Exists(runner.LastRecordingPath), Is.True, "its recording closed");
			Assert.That(runner.Scenario.environment.pack, Is.EqualTo("lower-east-river/2025-07-14"));
			Assert.That(_scene.Bootstrap.StartRefusal, Is.Null);
			yield return _scene.FramesUntil(() => runner.HasEnvironment, 300, "the launched run to step");
			Assert.That(runner.Environment.PackName, Is.EqualTo("lower-east-river/2025-07-14"));

			var going = runner.RunId;
			var unknown = Create("20", "2025-07-14", 600);
			unknown.environment.pack = "lower-east-river/2025-07-22";
			Assert.That(_scene.Bootstrap.ScenarioLauncher.TryStart(unknown, out problem), Is.False);
			Assert.That(problem, Does.Contain("lower-east-river/2025-07-22"));
			Assert.That(runner.RunId, Is.EqualTo(going), "a scenario that cannot start leaves the run going");
			Assert.That(runner.IsRunActive, Is.True);

			var tooClose = Create("8", "2025-07-14", 600);
			var position = tooClose.StartPosition;
			var face = _scene.Landings.PrimaryFace("8").Segments[0].Segment;
			var moved = FerrySim.Core.Contracts.Geodesy.LocalTangentPlane.World.ToGeodetic(position.North - 3.5 * face.WaterNormal.North, position.East - 3.5 * face.WaterNormal.East);
			tooClose.startLatitude = moved.LatitudeDegrees;
			tooClose.startLongitude = moved.LongitudeDegrees;
			LogAssert.Expect(LogType.Error, new Regex(@"stopped before its first step.*landing face 149111046"));
			Assert.That(_scene.Bootstrap.ScenarioLauncher.TryStart(tooClose, out problem), Is.False, "0.5 m off the face");
			Assert.That(problem, Is.EqualTo(_scene.Bootstrap.StartRefusal));
			Assert.That(problem, Does.Contain("149111046"));
			Assert.That(runner.IsRunActive, Is.False, "the bootstrap ended the refused run before its first step");
			Assert.That(runner.Driver.StepCount, Is.Zero);
		}

		[UnityTest]
		public IEnumerator AKClassRun_IsDrawnAndViewedAsTheKClass()
		{
			var tSize = GreyboxDimensions.From(_scene.Runner.Parameters);
			Assert.That(_scene.Runner.Parameters.Id, Is.EqualTo("t-class"));

			Assert.That(_scene.Bootstrap.ScenarioLauncher.TryStart(Create("87", "2025-07-14", 600, "k-class"), out var problem), Is.True, problem);
			yield return SliceScene.Frames(3);

			var kSize = GreyboxDimensions.From(_scene.Runner.Parameters);
			var greybox = _scene.Runner.GetComponent<VesselGreybox>();
			var hull = _scene.Runner.transform.Find(VesselGreybox.RootName + "/PortHull");
			var eye = _scene.Rig.WheelhouseMount.transform.localPosition;
			Assert.That(_scene.Runner.Parameters.Id, Is.EqualTo("k-class"));
			Assert.That(greybox.Dimensions.LengthOverall, Is.EqualTo(29.6).Within(1e-9));
			Assert.That(hull.localScale.z, Is.EqualTo((float)kSize.LengthOverall));
			Assert.That(hull.localScale.x, Is.EqualTo((float)kSize.DemihullBeam));
			Assert.That(eye.y, Is.EqualTo((float)kSize.BridgeEyeHeight), "the wheelhouse eye at the K-class's helm");
			Assert.That(eye.z, Is.EqualTo((float)kSize.BridgeEyeForward));
			Assert.That(kSize.BridgeEyeHeight, Is.Not.EqualTo(tSize.BridgeEyeHeight));
			Assert.That(_scene.Bootstrap.StartRefusal, Is.Null);

			Assert.That(_scene.Bootstrap.ScenarioLauncher.TryStart(Create("87", "2025-07-14", 600, "t-class"), out problem), Is.True, problem);
			yield return SliceScene.Frames(3);
			Assert.That(_scene.Rig.WheelhouseMount.transform.localPosition.y, Is.EqualTo((float)tSize.BridgeEyeHeight), "back to the T-class's eye");
			Assert.That(_scene.Runner.transform.Find(VesselGreybox.RootName + "/PortHull").localScale.z, Is.EqualTo((float)tSize.LengthOverall));
		}

		[UnityTest]
		public IEnumerator APackRun_MakesNoGarbagePerFrame_InOpenWaterOrInContact()
		{
			var runner = _scene.Runner;
			runner.RecordRuns = false;
			// Pier 11 at 13:00 EDT on 2025-07-14, near slack water: 4 m off the face, a few seconds clear of it.
			var atPier11 = Create("87", "2025-07-14", 780);
			Assert.That(_scene.Bootstrap.ScenarioLauncher.TryStart(atPier11, out var problem), Is.True, problem);
			runner.Pause();
			yield return null;

			// The update path the scene runs every frame: steps, the tide, the placement, and the readout the UI reads.
			void Frame()
			{
				runner.AdvanceFrame(1.0 / 60.0);
				runner.ApplyRenderPose();
				_ = runner.Environment;
			}

			runner.Resume();
			for (var i = 0; i < 30; i++)
			{
				Frame();
			}

			Assert.That(Frame, Is.Not.AllocatingGCMemory(), "pack run, open water");
			Assert.That(runner.Telemetry.ContactForce, Is.Zero, "not yet at the face");
			var openWater = runner.Environment;

			var held = new LandingApproachControls(runner, 1.0);
			_scene.Restart(atPier11, held);
			var guard = 0;
			while ((held.HoldFromStep < 0 || runner.Driver.StepCount < held.HoldFromStep + 3000) && guard++ < 40000)
			{
				runner.AdvanceFrame(0.25);
			}

			runner.ApplyRenderPose();
			Assert.That(runner.Telemetry.ContactForce, Is.GreaterThan(0.0), "held on Pier 11's face for the contact measurement");
			Assert.That(Frame, Is.Not.AllocatingGCMemory(), "pack run, in contact");
			var report = string.Format(CultureInfo.InvariantCulture,
				"{{\n\t\"pack_open_water\": \"{0}\",\n\t\"pack_in_contact\": \"{1}\",\n\t\"gc_allocations_per_frame_open_water\": 0,\n\t\"gc_allocations_per_frame_in_contact\": 0,\n" +
				"\t\"note\": \"Asserted with Unity's AllocatingGCMemory constraint on one frame of the update path (runner advance at 60 fps, tide and placement, environment readout). Pier 11 on 2025-07-14 at 13:00 EDT: 4 m off the face before it is reached, then held on it.\",\n" +
				"\t\"contact_force_n\": {2:F1},\n\t\"water_level_navd88_m\": {3:F4}\n}}\n",
				openWater.PackName + " " + openWater.PackDigest, runner.Environment.PackName + " " + runner.Environment.PackDigest, runner.Telemetry.ContactForce, runner.Environment.WaterLevelNavd88);
			File.WriteAllText(Path.Combine(SliceScene.FreshFolder("pack-garbage"), "pack-run-garbage.json"), report);
		}
	}
}
