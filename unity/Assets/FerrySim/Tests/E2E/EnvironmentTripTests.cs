using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Tests.Dynamics;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.UI;
using FerrySim.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.E2E
{
	/// <summary>
	/// The slice on real days (environment packs), end to end in the integrated scene. Every scenario is made by the
	/// scene's <see cref="IScenarioCatalog.TryCreate"/> and started by its <see cref="IScenarioLauncher.TryStart"/>; every run
	/// is recorded at 100 Hz and its trace, summary and screenshots kept under artifacts/e2e/. Failure modes guarded: a hull
	/// in neutral not carried by the pack's current; drawn water, barges or depth not at the pack's tide; a landing on a real
	/// flood or ebb that lands hard, touches anything but the primary face, grounds, or does not hold; a pack run that does
	/// not replay bit for bit, or that replays against another day's pack instead of being refused with both digests named.
	/// </summary>
	public sealed class EnvironmentTripTests
	{
		private const string Region = "lower-east-river";

		/// <summary>The drift's tolerance on the mean velocity, m/s (about 0.1 kn); see <see cref="Drift_InNeutralByACurrentStation_IsTheStationsPredictedCurrent"/>.</summary>
		private const double DriftMeanTolerance = 0.05;

		/// <summary>The drift's tolerance on each step's velocity, m/s.</summary>
		private const double DriftSampleTolerance = 0.08;

		private VesselSimulationRunner _runner;
		private RenderTexture _uiTarget;
		private PanelSettings _uiSettings;

		private static string Artifacts(string name)
		{
			var path = Path.Combine(TripData.Artifacts, "environment", name);
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}

			Directory.CreateDirectory(path);
			return path;
		}

		private static string DayFolder(string date) => Path.Combine(TripData.Root, EnvironmentPacks.RegionFolder(Region), "days", date);

		/// <summary>The pack read independently of the runner, from the repository.</summary>
		private static EnvironmentPack Pack(string date) => EnvironmentPackJson.Load(DayFolder(date));

		[UnityTearDown]
		public IEnumerator Cleanup()
		{
			_runner?.EndRun();
			var scene = SceneManager.GetSceneByPath(TripData.ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				SceneManager.SetActiveScene(SceneManager.CreateScene("M3 teardown " + Time.frameCount));
				yield return SceneManager.UnloadSceneAsync(scene);
			}

			if (_uiSettings != null) Object.Destroy(_uiSettings);
			if (_uiTarget != null)
			{
				_uiTarget.Release();
				Object.Destroy(_uiTarget);
			}
		}

		private IEnumerator LoadScene()
		{
			Assert.That(Directory.Exists(DayFolder("2025-07-14")), Is.True, "the environment packs are built (Tests/E2E/README.md, Run and Evidence)");
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(TripData.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
			Assert.Fail("The integrated slice tests run in the editor.");
#endif
			yield return null;
			yield return null;
			var bootstrap = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(bootstrap, Is.Not.Null);
			Assert.That(bootstrap.LoadError, Is.Null);
			_runner = bootstrap.Runner;
		}

		private SliceBootstrap Bootstrap => Object.FindFirstObjectByType<SliceBootstrap>();

		/// <summary>The catalog's scenario for the choice, started by the launcher, recorded at 100 Hz, paused before its first step.</summary>
		private ScenarioSettings Start(ScenarioChoice choice, IControlInputSource controls, string folder, Action<ScenarioSettings> adjust = null)
		{
			var bootstrap = Bootstrap;
			Assert.That(bootstrap.ScenarioCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			adjust?.Invoke(settings);
			_runner.RecordRuns = true;
			_runner.TelemetrySampleHz = 100;
			_runner.RecordingDirectory = folder;
			_runner.MaxStepsPerFrame = 100;
			Assert.That(bootstrap.ScenarioLauncher.TryStart(settings, out problem), Is.True, problem);
			_runner.Pause();
			_runner.Driver.Controls = controls;
			Assert.That(_runner.Driver.StepCount, Is.Zero);
			Assert.That(bootstrap.StartRefusal, Is.Null);
			Assert.That(_runner.WorldGeometry, Is.SameAs(bootstrap.Landings.Geometry), "real slice landing geometry");
			Assert.That(_runner.RunEnvironment.Pack.Name, Is.EqualTo(Region + "/" + choice.Date));
			return settings;
		}

		/// <summary>Steps the paused run frame by frame (100 steps a frame) until <paramref name="done"/>, rendering as it goes.</summary>
		private IEnumerator Drive(Func<bool> done, long maximumSteps, Action eachFrame = null)
		{
			while (!done() && _runner.Driver.StepCount < maximumSteps)
			{
				var before = _runner.Driver.StepCount;
				_runner.Resume();
				_runner.AdvanceFrame(1.0);
				_runner.Pause();
				Assert.That(_runner.Driver.StepCount, Is.GreaterThan(before), "the run makes progress on every driven frame");
				Assert.That(_runner.Driver.Faulted, Is.False);
				eachFrame?.Invoke();
				yield return null;
			}
		}

		private RecordedRun EndAndLoad()
		{
			_runner.EndRun();
			Assert.That(_runner.RecordingError, Is.Null);
			return RecordedRun.Load(_runner.LastRecordingPath);
		}

		[UnityTest]
		public IEnumerator Drift_InNeutralByACurrentStation_IsTheStationsPredictedCurrent()
		{
			// Corlears Hook's station NYH1921 on 2025-07-20 at 14:48 EDT: a strong steady flood (1.7 m/s) in almost no wind
			// (0.5 m/s), the day's best case for seeing the current alone.
			yield return LoadScene();
			var folder = Artifacts("drift");
			var pack = Pack("2025-07-20");
			var station = pack.CurrentStations.Single(s => s.Id == "NYH1921");
			var settings = Start(new ScenarioChoice("115", "2025-07-20", 14 * 60 + 48, "t-class"), new ConstantControls(ControlInputs.Neutral), folder, scenario =>
			{
				// The catalog's day, time, vessel and pack; the start moved off the landing to the station, in open water.
				scenario.startLatitude = station.Latitude;
				scenario.startLongitude = station.Longitude;
			});
			var start = _runner.State;
			Assert.That(Math.Sqrt(Math.Pow(start.North - station.North, 2) + Math.Pow(start.East - station.East, 2)), Is.LessThan(0.01), "at the station");
			var independent = new DataEnvironment(pack, settings.StartUtc);
			var rows = new List<string> { "time_s,north_m,east_m,ground_north_mps,ground_east_mps,station_north_mps,station_east_mps,field_at_hull_north_mps,field_at_hull_east_mps" };
			double sumNorth = 0, sumEast = 0, sumStationNorth = 0, sumStationEast = 0, worst = 0, worstField = 0;
			var samples = 0;
			var observer = new StepWatch((step, state, telemetry) =>
			{
				Assert.That(telemetry.ContactForce, Is.Zero, "nothing touched while drifting");
				if (step < 1000)
				{
					return;
				}

				var ground = state.GroundVelocity;
				var predicted = station.Velocity.Sample(independent.PackTime(state.Time));
				var field = independent.SampleCurrent(state.North, state.East, state.Time);
				sumNorth += ground.North;
				sumEast += ground.East;
				sumStationNorth += predicted.North;
				sumStationEast += predicted.East;
				worst = Math.Max(worst, (ground - predicted).Magnitude);
				worstField = Math.Max(worstField, (field - predicted).Magnitude);
				samples++;
				if (step % 100 == 0)
				{
					rows.Add(string.Join(",", new[] { state.Time, state.North, state.East, ground.North, ground.East, predicted.North, predicted.East, field.North, field.East }
						.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
				}
			});
			_runner.Driver.AddObserver(observer);
			_runner.Driver.StepLimit = 13000;
			try
			{
				yield return Drive(() => _runner.Driver.AtStepLimit, 13000);
			}
			finally
			{
				_runner.Driver.RemoveObserver(observer);
			}

			yield return Capture(Path.Combine(folder, "drift.png"), 60f, 30f);
			var run = EndAndLoad();
			var mean = new HorizontalVector(sumNorth / samples, sumEast / samples);
			var predictedMean = new HorizontalVector(sumStationNorth / samples, sumStationEast / samples);
			var turn = Angles.ToDegrees(Angles.WrapPi(mean.Direction - predictedMean.Direction));
			File.WriteAllText(Path.Combine(folder, "drift.csv"), string.Join("\n", rows) + "\n");
			var summary = new JObject
			{
				["pack"] = pack.Name, ["pack_digest"] = pack.Digest, ["station"] = station.Id, ["station_name"] = station.Name, ["start_utc"] = settings.scenarioStartUtc,
				["seconds_compared"] = samples / 100.0, ["drift_speed_mps"] = mean.Magnitude, ["drift_toward_deg"] = Angles.ToDegrees(mean.Direction),
				["predicted_speed_mps"] = predictedMean.Magnitude, ["predicted_toward_deg"] = Angles.ToDegrees(predictedMean.Direction),
				["mean_difference_mps"] = (mean - predictedMean).Magnitude, ["direction_difference_deg"] = turn, ["worst_step_difference_mps"] = worst,
				["worst_field_at_hull_vs_station_mps"] = worstField, ["wind_mps"] = _runner.RunEnvironment.Observer.SampleWind(start.North, start.East, 0.0).Magnitude,
				["moved_m"] = Math.Sqrt(Math.Pow(_runner.State.North - start.North, 2) + Math.Pow(_runner.State.East - start.East, 2)),
				["tolerance_mean_mps"] = DriftMeanTolerance, ["tolerance_step_mps"] = DriftSampleTolerance,
				["tolerance_reason"] = "At rest in the water the core's relative-velocity model carries the hull with the water at the hull exactly. What separates the drift from the station's prediction is the field between stations as the hull leaves the station (the hull moves about 220 m; the station blend changes by up to about 0.03 m/s) and the day's 0.5 m/s wind (leeway up to about 0.05 m/s). 0.05 m/s on the mean is about 3% of the current and 0.1 kn.",
				["recording"] = Path.GetFileName(_runner.LastRecordingPath),
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());

			Assert.That(samples, Is.EqualTo(12000), "120 s compared at 100 Hz");
			Assert.That((mean - predictedMean).Magnitude, Is.LessThanOrEqualTo(DriftMeanTolerance), "the hull drifts at the station's predicted current");
			Assert.That(Math.Abs(turn), Is.LessThan(2.0), "toward the predicted direction");
			Assert.That(worst, Is.LessThanOrEqualTo(DriftSampleTolerance), "at every step");
			Assert.That(predictedMean.Magnitude, Is.GreaterThan(1.5), "a strong current, so the check means something");
			Assert.That(run.Header["environment_digest"], Is.EqualTo(pack.Digest));
		}

		[UnityTest]
		public IEnumerator Tide_TheWaterABargeAndTheDepthStandAtThePacksLevel_NearHighAndNearLowWater()
		{
			yield return LoadScene();
			var folder = Artifacts("tide");
			const string date = "2025-07-14";
			var pack = Pack(date);
			var bootstrap = Bootstrap;
			Assert.That(bootstrap.ScenarioCatalog.TryCreate(new ScenarioChoice("87", date, 0, "t-class"), out var probe, out var problem), Is.True, problem);
			var at = probe.StartPosition;
			var day = new DataEnvironment(pack, pack.EpochUtc);
			// High and low water at Pier 11's start in daylight (07:00 to 20:00), from the pack itself.
			var minutes = Enumerable.Range(7 * 60, 13 * 60 + 1).ToList();
			var high = minutes.OrderByDescending(m => day.SampleWaterLevel(at.North, at.East, m * 60.0)).First();
			var low = minutes.OrderBy(m => day.SampleWaterLevel(at.North, at.East, m * 60.0)).First();
			var water = Object.FindFirstObjectByType<WaterSurface>();
			var heights = bootstrap.GetComponent<WorldWaterHeight>();
			var floating = Object.FindObjectsByType<FloatOnWater>(FindObjectsSortMode.None);
			Assert.That(floating, Is.Not.Empty, "the world's landings float");
			var vessel = _runner.Parameters;
			var results = new JArray();
			var levels = new Dictionary<string, double>();
			foreach (var (name, minute) in new[] { ("high", high), ("low", low) })
			{
				var settings = Start(new ScenarioChoice("87", date, minute, "t-class"), new ConstantControls(ControlInputs.Neutral), Path.Combine(folder, name));
				yield return Drive(() => _runner.Driver.StepCount >= 100, 100);
				// Placement and the tide again after the last step, and a frame for the barge (FloatOnWater follows in LateUpdate).
				_runner.ApplyRenderPose();
				yield return null;

				var state = _runner.State;
				var time = _runner.Driver.SimulationTime;
				var independent = new DataEnvironment(pack, settings.StartUtc);
				var level = independent.SampleWaterLevel(state.North, state.East, time);
				Assert.That(pack.Depth.Sample(state.North, state.East, out var bed), Is.EqualTo(BedSample.Known));
				Assert.That(water.WaterLevelNavd88, Is.EqualTo(level).Within(1e-9), $"{name}: the drawn water at the pack's level at the vessel");
				Assert.That(_runner.Environment.WaterLevelNavd88, Is.EqualTo(level).Within(1e-9), $"{name}: the readout");
				foreach (var barges in floating)
				{
					Assert.That(barges.transform.localPosition.y, Is.EqualTo(level).Within(1e-4), $"{name}: {barges.name} floats at it");
				}

				Assert.That(water.transform.localPosition.y, Is.EqualTo(level).Within(1e-4), $"{name}: the mesh is raised to it");
				Assert.That(heights.EllipsoidalHeight(state.North, state.East, time), Is.EqualTo(water.GeoidHeight(state.North, state.East) + level).Within(1e-9),
					$"{name}: the placement reads the same surface");
				Assert.That(_runner.Telemetry.DepthUnderKeel, Is.EqualTo(level - bed - vessel.Particulars.MaxDraft.Value).Within(1e-6), $"{name}: depth under the keel from the core");
				levels[name] = level;
				Bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
				yield return Capture(Path.Combine(folder, $"tide-{name}.png"), 55f, 14f);
				results.Add(new JObject
				{
					["water"] = name, ["local_time"] = $"{minute / 60:D2}:{minute % 60:D2}", ["start_utc"] = settings.scenarioStartUtc, ["level_navd88_m"] = level,
					["drawn_level_navd88_m"] = water.WaterLevelNavd88, ["barge_level_navd88_m"] = floating[0].transform.localPosition.y, ["bed_navd88_m"] = bed,
					["depth_under_keel_m"] = _runner.Telemetry.DepthUnderKeel, ["recording"] = Path.GetFileName(EndAndLoad().TelemetryPath),
				});
			}

			var summary = new JObject { ["pack"] = pack.Name, ["pack_digest"] = pack.Digest, ["landing"] = "87 Wall St/Pier 11", ["runs"] = results, ["range_m"] = levels["high"] - levels["low"] };
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());
			Assert.That(levels["high"], Is.GreaterThan(0.7), "near high water");
			Assert.That(levels["low"], Is.LessThan(-0.4), "near low water");
			Assert.That(levels["high"] - levels["low"], Is.GreaterThan(1.0), "the day's range shows");
		}

		[UnityTest]
		public IEnumerator Pier11_OnTheFlood_LandsSoftOnThePrimaryFace_AndHolds() => Landing("pier11-flood", "87", "2025-07-20", 14 * 60 + 48, towardTheFace: true);

		[UnityTest]
		public IEnumerator Pier11_OnTheEbb_LandsSoftOnThePrimaryFace_AndHolds() => Landing("pier11-ebb", "87", "2025-04-14", 15 * 60 + 12, towardTheFace: false);

		/// <summary>
		/// The scene's own day and time (Slice/README.md, The scene): at slack water, under two hours after high water, a
		/// T-class can land at South Williamsburg, where the depth grid is shallowest, without grounding.
		/// </summary>
		[UnityTest]
		public IEnumerator SouthWilliamsburg_AtTheScenesDayAndTime_LandsSoftWithoutGrounding() => Landing("south-williamsburg-slack", "8", "2025-07-20", 18 * 60 + 48, towardTheFace: null);

		/// <param name="towardTheFace">True for a current setting onto the face at more than 0.8 m/s, false for one setting off it, null for no expectation.</param>
		private IEnumerator Landing(string leg, string stop, string date, int minutes, bool? towardTheFace)
		{
			yield return LoadScene();
			var folder = Artifacts(leg);
			var bootstrap = Bootstrap;
			var face = bootstrap.Landings.PrimaryFace(stop);
			var arrival = new HashSet<int>(face.Segments.Select(s => s.Id));
			var controls = new PackLandingControls(_runner, face);
			var settings = Start(new ScenarioChoice(stop, date, minutes, "t-class"), controls, folder);
			var vessel = _runner.Parameters;
			var start = _runner.State;
			var normal = face.Segments[0].Segment.WaterNormal;
			var inward = new HorizontalVector(-normal.North, -normal.East);
			var current = _runner.RunEnvironment.Observer.SampleCurrent(start.North, start.East, 0.0);
			var along = current.North * inward.North + current.East * inward.East;
			if (towardTheFace.HasValue)
			{
				Assert.That(towardTheFace.Value ? along : -along, Is.GreaterThan(0.8), $"{leg}: the current runs along the landing, {(towardTheFace.Value ? "onto" : "off")} the face");
			}

			var watch = new LandingWatch(_runner, arrival);
			_runner.Driver.AddObserver(watch);
			var departure = false;
			try
			{
				// 70 s from the first touching step: at least 60 s since first contact, the last 10 s the hold.
				yield return Drive(() => _runner.Driver.AtStepLimit, 60000, () =>
				{
					if (watch.FirstContact >= 0 && !_runner.Driver.StepLimit.HasValue)
					{
						_runner.Driver.StepLimit = watch.FirstContact + LandingWatch.Hold;
					}
				});
			}
			finally
			{
				_runner.Driver.RemoveObserver(watch);
			}

			yield return Capture(Path.Combine(folder, "held.png"), 45f, 16f);
			departure = controls.DepartureContact;

			Assert.That(_runner.HasEnvironment, Is.True);
			var state = _runner.State;
			var readout = _runner.Environment;
			var run = EndAndLoad();
			var thresholds = JObject.Parse(File.ReadAllText(Path.Combine(TripData.Folder, "landing-thresholds.json")));
			var speedLimit = (double)thresholds["firstContactSpeed"]["value"];
			var reference = ContactScenarios.BowOnLanding(vessel, ContactScenarios.EstimatedBargeFender, Units.KnotsToMetresPerSecond((double)thresholds["peakForceReference"]["speed"]));
			var headingError = Angles.ToDegrees(Angles.WrapPi(state.Heading - Math.Atan2(inward.East, inward.North)));
			var replay = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), geometry: TripData.Landings().Geometry);
			var summary = new JObject
			{
				["leg"] = leg, ["landing"] = stop + " " + face.StopName, ["pack"] = readout.PackName, ["pack_digest"] = readout.PackDigest, ["start_utc"] = settings.scenarioStartUtc,
				["local_start"] = $"{minutes / 60:D2}:{minutes % 60:D2} " + (readout.LocalUtcOffset.TotalHours == -4.0 ? "EDT" : "EST"),
				["current_along_landing_at_start_mps"] = along, ["water_level_navd88_m_at_end"] = readout.WaterLevelNavd88, ["wind_mps_at_end"] = readout.Wind.Magnitude,
				["first_contact_step"] = controls.HoldFromStep, ["first_contact_speed_kn"] = Units.MetresPerSecondToKnots(watch.FirstContactSpeed),
				["peak_contact_force_n"] = watch.PeakForce, ["peak_compression_m"] = watch.Compression, ["bottomed_out"] = watch.BottomedOut,
				["final_hold_force_n"] = watch.FinalForce, ["last_10s_max_speed_kn"] = Units.MetresPerSecondToKnots(watch.HoldMaxSpeed),
				["last_10s_contact_every_step"] = watch.HoldContact, ["contact_faces"] = new JArray(watch.Touched.OrderBy(id => id)),
				["departure_contact"] = controls.DepartureContact, ["minimum_depth_under_keel_m"] = watch.MinimumDepthUnderKeel,
				["heading_error_deg"] = headingError, ["duration_s"] = state.Time,
				["force_limit_n"] = reference.PeakForce, ["force_limit_confidence"] = "estimated", ["speed_limit_kn"] = speedLimit, ["speed_limit_confidence"] = "crew-reported",
				["replay"] = replay.ToString(), ["recording"] = Path.GetFileName(run.TelemetryPath), ["inputs"] = Path.GetFileNameWithoutExtension(run.TelemetryPath) + ".inputs.csv",
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());

			Assert.That(departure, Is.False, "backed clear without touching the face");
			Assert.That(controls.HoldFromStep, Is.GreaterThan(0), "landed");
			Assert.That(watch.Touched, Is.Not.Empty);
			Assert.That(watch.Touched.IsSubsetOf(arrival), Is.True, $"only {face.StopName}'s primary face touched");
			Assert.That(Units.MetresPerSecondToKnots(watch.FirstContactSpeed), Is.LessThanOrEqualTo(speedLimit), "soft: first contact at most the crew's 1.5 kn");
			Assert.That(watch.PeakForce, Is.GreaterThan(0.0).And.LessThanOrEqualTo(reference.PeakForce), "soft: below the 2 kn reference landing's peak force");
			Assert.That(watch.BottomedOut, Is.False, "no fender bottoms out");
			Assert.That(watch.MinimumDepthUnderKeel, Is.GreaterThan(0.0), "never aground");
			Assert.That(watch.HoldSteps, Is.EqualTo(1000), "the final ten seconds sampled at 100 Hz");
			Assert.That(watch.HoldContact, Is.True, "held on the primary face at every step of the final ten seconds");
			Assert.That(Units.MetresPerSecondToKnots(watch.HoldMaxSpeed), Is.LessThan(0.1), "at rest on the face");
			Assert.That(Math.Abs(headingError), Is.LessThan(5.0), "bow-in, square to the face");
			Assert.That(FaceClearance.Check(vessel, state.North, state.East, state.Heading, face.Segments.Select(s => s.Segment).ToArray()).Overlaps, Is.True, "the hull on the face");
			Assert.That(replay.Matches, Is.True, replay.ToString());
			Assert.That(run.Header["environment_digest"], Is.EqualTo(Pack(date).Digest));
		}

		[UnityTest]
		public IEnumerator Replay_APackRunReplaysBitForBit_AndAnotherDaysPackIsRefused()
		{
			yield return LoadScene();
			var folder = Artifacts("replay");
			var bootstrap = Bootstrap;
			var face = bootstrap.Landings.PrimaryFace("87");
			var controls = new PackLandingControls(_runner, face);
			Start(new ScenarioChoice("87", "2025-04-14", 15 * 60 + 12, "t-class"), controls, folder);
			var live = new StateDigest();
			var digest = new StepWatch((step, state, telemetry) => live.Add(state));
			_runner.Driver.AddObserver(digest);
			yield return Drive(() => _runner.Driver.StepCount >= 6000, 6000);
			_runner.Driver.RemoveObserver(digest);
			var run = EndAndLoad();
			var vessel = _runner.Parameters;
			var geometry = TripData.Landings().Geometry;
			var recorded = Pack("2025-04-14");
			var other = Pack("2025-04-15");

			var same = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), geometry: geometry);
			var refused = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), geometry: geometry, pack: other);
			var error = Assert.Throws<InvalidOperationException>(() => _runner.InitializeReplay(run, vessel, other));
			_runner.InitializeReplay(run, vessel);
			_runner.MaxStepsPerFrame = 100;
			yield return Drive(() => _runner.ReplayCompleted, 6000);
			var inScene = _runner.ReplayResult;
			var summary = new JObject
			{
				["recording"] = Path.GetFileName(run.TelemetryPath), ["environment_pack"] = run.EnvironmentPack, ["environment_digest"] = run.EnvironmentDigest,
				["environment_epoch_utc"] = run.Header["environment_epoch_utc"], ["world_geometry_digest"] = run.Header[WorldGeometryDigest.HeaderKey],
				["state_digest"] = run.StateDigest, ["live_state_digest"] = live.Text, ["replay"] = same.ToString(), ["in_scene_replay"] = inScene?.ToString(),
				["other_pack"] = other.Name, ["other_digest"] = other.Digest, ["refused"] = refused.ToString(), ["runner_refusal"] = error.Message,
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());

			Assert.That(run.EnvironmentKind, Is.EqualTo(EnvironmentKind.Pack));
			Assert.That(run.EnvironmentDigest, Is.EqualTo(recorded.Digest), "the day DIGEST");
			Assert.That(run.Header["environment_epoch_utc"], Is.EqualTo("2025-04-14T04:00:00Z"));
			Assert.That(run.Header[WorldGeometryDigest.HeaderKey], Is.EqualTo(WorldGeometryDigest.Of(geometry)));
			Assert.That(run.StateDigest, Is.EqualTo(live.Text), "the recording's state digest is the live run's");
			Assert.That(same.Matches, Is.True, same.ToString());
			Assert.That(same.RowsCompared, Is.EqualTo(run.Rows.Count));
			Assert.That(same.ReplayedDigest, Is.EqualTo(run.StateDigest));
			Assert.That(refused.Matches, Is.False);
			Assert.That(refused.RowsCompared, Is.Zero, "nothing replayed against another day");
			Assert.That(refused.ToString(), Does.Contain(recorded.Digest).And.Contain(other.Digest));
			Assert.That(error.Message, Does.Contain(recorded.Digest).And.Contain(other.Digest));
			Assert.That(inScene, Is.Not.Null);
			Assert.That(inScene.Matches, Is.True, inScene?.ToString());
		}

		private IEnumerator Capture(string path, float distance, float height)
		{
			var bootstrap = Bootstrap;
			var ui = bootstrap.WheelhouseUI;
			if (_uiTarget == null)
			{
				_uiTarget = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
				_uiTarget.Create();
				_uiSettings = WheelhousePanelSettings.Create(_uiTarget, Color.clear);
				ui.enabled = false;
				ui.Configure(_runner, _uiSettings);
				ui.enabled = true;
			}

			var rig = bootstrap.CameraRig;
			rig.CurrentMode = CameraRig.Mode.Chase;
			rig.Chase.Distance = distance;
			rig.Chase.Height = height;
			rig.Chase.Snap();
			for (var i = 0; i < 4; i++) yield return null;
			var camera = rig.GetComponent<Camera>();
			var previous = camera.targetTexture;
			var active = RenderTexture.active;
			var aspect = camera.aspect;
			var target = RenderTexture.GetTemporary(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			Texture2D world = null, overlay = null;
#if UNITY_EDITOR
			var asyncShaders = UnityEditor.ShaderUtil.allowAsyncCompilation;
			UnityEditor.ShaderUtil.allowAsyncCompilation = false;
#endif
			try
			{
				camera.aspect = 1600f / 900f;
				camera.targetTexture = target;
				camera.Render();
				world = Read(target);
				overlay = Read(_uiTarget);
				var pixels = world.GetPixels32();
				var top = overlay.GetPixels32();
				Assert.That(pixels.Select(p => (p.r << 16) | (p.g << 8) | p.b).Distinct().Count(), Is.GreaterThan(50), "world screenshot rendered");
				for (var i = 0; i < pixels.Length; i++)
				{
					var a = top[i].a / 255f;
					pixels[i] = new Color32((byte)Mathf.Min(255f, top[i].r + pixels[i].r * (1f - a)),
						(byte)Mathf.Min(255f, top[i].g + pixels[i].g * (1f - a)), (byte)Mathf.Min(255f, top[i].b + pixels[i].b * (1f - a)), 255);
				}

				world.SetPixels32(pixels);
				world.Apply();
				File.WriteAllBytes(path, world.EncodeToPNG());
			}
			finally
			{
#if UNITY_EDITOR
				UnityEditor.ShaderUtil.allowAsyncCompilation = asyncShaders;
#endif
				camera.targetTexture = previous;
				camera.aspect = aspect;
				RenderTexture.active = active;
				if (world != null) Object.Destroy(world);
				if (overlay != null) Object.Destroy(overlay);
				RenderTexture.ReleaseTemporary(target);
			}
		}

		private static Texture2D Read(RenderTexture target)
		{
			RenderTexture.active = target;
			var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
			texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
			texture.Apply();
			return texture;
		}

		/// <summary>Calls back after every physics step with the step index, the state and the telemetry after it.</summary>
		private sealed class StepWatch : IStepObserver
		{
			private readonly Action<long, VesselState, VesselTelemetry> _after;

			public StepWatch(Action<long, VesselState, VesselTelemetry> after) => _after = after;

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry) => _after(stepCount - 1, state, telemetry);
		}

		/// <summary>Contact, speed and depth at every physics step of a landing, as the trips measure them.</summary>
		private sealed class LandingWatch : IStepObserver
		{
			/// <summary>Steps from the first touching step to the end: 70 s, the last 10 s of them the hold.</summary>
			public const long Hold = 7000;

			private readonly VesselSimulationRunner _runner;
			private readonly HashSet<int> _arrival;
			private double _speedBefore;
			private long _end = -1;

			public LandingWatch(VesselSimulationRunner runner, HashSet<int> arrival)
			{
				_runner = runner;
				_arrival = arrival;
			}

			public HashSet<int> Touched { get; } = new HashSet<int>();
			public long FirstContact { get; private set; } = -1;
			public double FirstContactSpeed { get; private set; }
			public double PeakForce { get; private set; }
			public double Compression { get; private set; }
			public bool BottomedOut { get; private set; }
			public double FinalForce { get; private set; }
			public double HoldMaxSpeed { get; private set; }
			public int HoldSteps { get; private set; }
			public bool HoldContact { get; private set; } = true;
			public double MinimumDepthUnderKeel { get; private set; } = double.PositiveInfinity;

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) => _speedBefore = telemetry.SpeedOverGround;

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
			{
				var step = stepCount - 1;
				var simulation = (VesselDynamicsSimulation)_runner.Driver.Simulation;
				if (simulation.ContactCount > 0 && FirstContact < 0)
				{
					FirstContact = step;
					FirstContactSpeed = _speedBefore;
					_end = FirstContact + Hold;
				}

				for (var i = 0; i < simulation.ContactCount; i++)
				{
					var contact = simulation.GetContact(i);
					Touched.Add(contact.SegmentId);
					Compression = Math.Max(Compression, contact.Compression);
					BottomedOut |= contact.BottomedOut;
				}

				PeakForce = Math.Max(PeakForce, telemetry.ContactForce);
				FinalForce = telemetry.ContactForce;
				MinimumDepthUnderKeel = Math.Min(MinimumDepthUnderKeel, telemetry.DepthUnderKeel);
				if (_end > 0 && step >= _end - 1000 && step < _end)
				{
					HoldSteps++;
					HoldMaxSpeed = Math.Max(HoldMaxSpeed, telemetry.SpeedOverGround);
					HoldContact &= simulation.ContactCount > 0 && telemetry.ContactForce > 0.0;
					for (var i = 0; i < simulation.ContactCount; i++)
					{
						HoldContact &= _arrival.Contains(simulation.GetContact(i).SegmentId);
					}
				}
			}
		}
	}
}
