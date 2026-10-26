using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.Traffic;
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
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.E2E
{
	/// <summary>
	/// The harbor's real traffic in the integrated slice (M3 C2), end to end. Every scenario is made by the scene's
	/// <see cref="IScenarioCatalog.TryCreate"/>, given its traffic settings and started by its <see cref="IScenarioLauncher.TryStart"/>;
	/// every run is recorded at 100 Hz and its trace, traffic event log, summary and screenshots kept under artifacts/e2e/traffic/.
	/// Failure modes guarded: a hull not where the core's replay says, or shown when its vessel is not in the run's traffic, or
	/// not shown when it is; the learner's own ferry still there, or another vessel gone; no CPA alarm before a collision, a
	/// collision not recorded, or either missing from the debrief; a traffic run that does not replay bit for bit, events
	/// included, or that replays against another day's traffic; traffic costing too much or making garbage per frame; a start
	/// beside a berthed vessel recorded as a collision or alarm, or that vessel's hull turned by GPS jitter.
	/// </summary>
	public sealed class TrafficTripTests
	{
		private const string Day = "2025-07-14";
		private const string OtherDay = "2025-07-15";

		/// <summary>The synthetic vessel of the alarm and collision tests: plainly not a real MMSI's vessel.</summary>
		private const int SyntheticMmsi = 990000001;

		/// <summary>The most a drawn hull may be from the replay's position, m: float32 transforms 5 km out.</summary>
		private const double PositionTolerance = 0.01;

		private VesselSimulationRunner _runner;
		private RenderTexture _uiTarget;
		private PanelSettings _uiSettings;
		private string _synthetic;

		private static string Artifacts(string name)
		{
			var path = Path.Combine(TripData.Artifacts, "traffic", name);
			if (Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}

			Directory.CreateDirectory(path);
			return path;
		}

		private static string TrafficFolder(string date) => Path.Combine(TripData.Root, "data", "processed", "harbor", "traffic", date);

		/// <summary>The day's traffic read independently of the runner, from the repository.</summary>
		private static TrafficPack Pack(string date) => TrafficPackJson.Load(TrafficFolder(date));

		private SliceBootstrap Bootstrap => Object.FindFirstObjectByType<SliceBootstrap>();

		[UnityTearDown]
		public IEnumerator Cleanup()
		{
			_runner?.EndRun();
			var scene = SceneManager.GetSceneByPath(TripData.ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				SceneManager.SetActiveScene(SceneManager.CreateScene("Traffic teardown " + Time.frameCount));
				yield return SceneManager.UnloadSceneAsync(scene);
			}

			if (_uiSettings != null) Object.Destroy(_uiSettings);
			if (_uiTarget != null)
			{
				_uiTarget.Release();
				Object.Destroy(_uiTarget);
			}

			if (_synthetic != null && Directory.Exists(_synthetic))
			{
				Directory.Delete(_synthetic, true);
			}

			_synthetic = null;
		}

		private IEnumerator LoadScene()
		{
			Assert.That(File.Exists(Path.Combine(TrafficFolder(Day), "manifest.json")), Is.True,
				"the traffic packs are built: PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic build --all");
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(TripData.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
			Assert.Fail("The integrated slice tests run in the editor.");
#endif
			yield return null;
			yield return null;
			var bootstrap = Bootstrap;
			Assert.That(bootstrap, Is.Not.Null);
			Assert.That(bootstrap.LoadError, Is.Null);
			Assert.That(bootstrap.TrafficView, Is.Not.Null, "the slice draws traffic");
			_runner = bootstrap.Runner;
		}

		/// <summary>The catalog's scenario for the choice with the given traffic, started by the launcher, recorded at 100 Hz, paused before its first step.</summary>
		private ScenarioSettings Start(ScenarioChoice choice, IControlInputSource controls, string folder, ScenarioTraffic traffic, Action<ScenarioSettings> adjust = null)
		{
			var bootstrap = Bootstrap;
			Assert.That(bootstrap.ScenarioCatalog.TryCreate(choice, out var settings, out var problem), Is.True, problem);
			settings.traffic = traffic;
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
			return settings;
		}

		/// <summary>Steps the paused run frame by frame (a frame's steps at a time) until <paramref name="done"/>, rendering (and placing traffic) every frame.</summary>
		private IEnumerator Drive(Func<bool> done, long maximumSteps, double frameSeconds = 1.0, Action eachFrame = null)
		{
			while (!done() && _runner.Driver.StepCount < maximumSteps)
			{
				var before = _runner.Driver.StepCount;
				_runner.Resume();
				_runner.AdvanceFrame(frameSeconds);
				_runner.Pause();
				Assert.That(_runner.Driver.StepCount, Is.GreaterThan(before), "the run makes progress on every driven frame");
				Assert.That(_runner.Driver.Faulted, Is.False);
				yield return null;
				eachFrame?.Invoke();
			}
		}

		private RecordedRun EndAndLoad()
		{
			_runner.EndRun();
			Assert.That(_runner.RecordingError, Is.Null);
			return RecordedRun.Load(_runner.LastRecordingPath);
		}

		/// <summary>The slice's area with the view's margin, world (north, east) m, read from the drawn water's datum.</summary>
		/// <summary>
		/// The view's hull filter before the wave 2b review's fix, for comparison only: a report of the vessel inside the area, or
		/// the straight line between two reports of a segment crossing it. It misses curves that bulge in.
		/// </summary>
		private static bool ReportsOrLinesInside(TrafficPack pack, TrafficVessel vessel, double minNorth, double maxNorth, double minEast, double maxEast)
		{
			bool Inside(double n, double e) => n >= minNorth && n <= maxNorth && e >= minEast && e <= maxEast;
			if (!vessel.Moving)
			{
				return Inside(vessel.StaticNorth, vessel.StaticEast);
			}

			var reports = pack.Reports;
			var end = vessel.FirstReport + vessel.Reports;
			for (var r = vessel.FirstReport; r < end; r++)
			{
				if (Inside(reports.North(r), reports.East(r)))
				{
					return true;
				}

				if (r + 1 < end && reports.Segment(r + 1) == reports.Segment(r))
				{
					// The straight line between the two reports against the area (Liang–Barsky), as the view clipped it.
					var n0 = reports.North(r);
					var e0 = reports.East(r);
					var dn = reports.North(r + 1) - n0;
					var de = reports.East(r + 1) - e0;
					double low = 0.0, high = 1.0;
					if (Clip(-dn, n0 - minNorth, ref low, ref high) && Clip(dn, maxNorth - n0, ref low, ref high) &&
						Clip(-de, e0 - minEast, ref low, ref high) && Clip(de, maxEast - e0, ref low, ref high))
					{
						return true;
					}
				}
			}

			return false;
		}

		private static bool Clip(double p, double q, ref double low, ref double high)
		{
			if (p == 0.0)
			{
				return q >= 0.0;
			}

			var t = q / p;
			if (p < 0.0)
			{
				if (t > high)
				{
					return false;
				}

				low = Math.Max(low, t);
			}
			else
			{
				if (t < low)
				{
					return false;
				}

				high = Math.Min(high, t);
			}

			return true;
		}

		private static void Area(SliceBootstrap bootstrap, out double minNorth, out double maxNorth, out double minEast, out double maxEast)
		{
			var water = Object.FindFirstObjectByType<WaterSurface>();
			WaterSurface.SliceBounds(water.Datum, out minNorth, out maxNorth, out minEast, out maxEast);
			var margin = bootstrap.TrafficView.AreaMargin;
			minNorth -= margin;
			maxNorth += margin;
			minEast -= margin;
			maxEast += margin;
		}

		/// <summary>The view's camera in the world frame, north and east.</summary>
		private static HorizontalVector CameraPosition(SliceBootstrap bootstrap)
		{
			var camera = bootstrap.CameraRig.GetComponent<Camera>();
			var local = bootstrap.WorldFrame.transform.InverseTransformPoint(camera.transform.position);
			return new HorizontalVector(local.z, local.x);
		}

		/// <summary>
		/// Checks every hull against an independent replay at the time the view placed them: a hull is shown exactly when its vessel
		/// is in the run's traffic, inside the area and within the view range, and then exactly where the replay puts it.
		/// </summary>
		private static (int Shown, int Compared, double WorstPosition, double WorstHeading) CheckHulls(SliceBootstrap bootstrap, TrafficReplay independent, int removed, double epochOffset)
		{
			var view = bootstrap.TrafficView;
			Area(bootstrap, out var minNorth, out var maxNorth, out var minEast, out var maxEast);
			var camera = CameraPosition(bootstrap);
			var range = view.ViewRange;
			var packTime = view.PlacedTime + epochOffset;
			int shown = 0, compared = 0;
			double worstPosition = 0, worstHeading = 0;
			foreach (var hull in view.Hulls)
			{
				var present = independent.TrySample(hull.VesselIndex, packTime, out var sample);
				var expected = present && hull.VesselIndex != removed && sample.North >= minNorth && sample.North <= maxNorth && sample.East >= minEast && sample.East <= maxEast &&
					(sample.Position - camera).Magnitude <= range + 1e-3;
				// The camera can move after the view placed the hulls in the same frame: skip hulls within 50 m of the range's edge.
				var near = present && Math.Abs((sample.Position - camera).Magnitude - range) < 50.0;
				if (!near)
				{
					Assert.That(hull.Shown, Is.EqualTo(expected), $"{hull.Mmsi} {hull.VesselName}: shown exactly while in the run's traffic, the area and the range");
					Assert.That(hull.gameObject.activeSelf, Is.EqualTo(hull.Shown));
				}

				if (!hull.Shown)
				{
					continue;
				}

				shown++;
				var at = hull.transform.localPosition;
				var error = Math.Sqrt((at.z - sample.North) * (at.z - sample.North) + (at.x - sample.East) * (at.x - sample.East));
				var headingError = Math.Abs(Mathf.DeltaAngle(hull.transform.localEulerAngles.y, (float)Angles.ToDegrees(sample.Heading)));
				Assert.That(error, Is.LessThanOrEqualTo(PositionTolerance), $"{hull.Mmsi} {hull.VesselName}: where the core's replay puts it");
				Assert.That(headingError, Is.LessThan(0.01), $"{hull.Mmsi}: heading");
				worstPosition = Math.Max(worstPosition, error);
				worstHeading = Math.Max(worstHeading, headingError);
				compared++;
			}

			return (shown, compared, worstPosition, worstHeading);
		}

		[UnityTest]
		public IEnumerator Replay_OnABusyDayAtPier11_EveryHullIsWhereTheCoreReplaySays()
		{
			// Pier 11 on Monday 2025-07-14 at 17:00 EDT, the evening rush: about 60 of the day's vessels in the slice at once.
			yield return LoadScene();
			var folder = Artifacts("replay-positions");
			var pack = Pack(Day);
			var independent = new TrafficReplay(pack);
			var settings = Start(new ScenarioChoice("87", Day, 17 * 60, "t-class", berthId: "enc:0226015B0512B198"), new ConstantControls(ControlInputs.Neutral), folder, new ScenarioTraffic { day = Day });
			var bootstrap = Bootstrap;
			var view = bootstrap.TrafficView;
			var offset = _runner.Traffic.EpochOffset;
			Assert.That(offset, Is.EqualTo(17 * 3600.0), "pack time = run time + 17:00 local");
			Assert.That(_runner.Traffic.Pack.Digest, Is.EqualTo(pack.Digest));
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			var samples = new JArray();
			var shownPerFrame = new List<int>();
			var compared = 0;
			double worst = 0, worstHeading = 0;
			var frames = 0;
			yield return Drive(() => _runner.Driver.StepCount >= 6000, 6000, 0.5, () =>
			{
				var check = CheckHulls(bootstrap, independent, -1, offset);
				compared += check.Compared;
				worst = Math.Max(worst, check.WorstPosition);
				worstHeading = Math.Max(worstHeading, check.WorstHeading);
				shownPerFrame.Add(check.Shown);
				if (frames++ % 20 == 0)
				{
					foreach (var hull in view.Hulls.Where(h => h.Shown).Take(6))
					{
						independent.TrySample(hull.VesselIndex, view.PlacedTime + offset, out var s);
						samples.Add(new JObject
						{
							["run_time_s"] = view.PlacedTime, ["mmsi"] = hull.Mmsi, ["name"] = hull.VesselName, ["class"] = hull.ClassName,
							["hull_north_m"] = hull.transform.localPosition.z, ["hull_east_m"] = hull.transform.localPosition.x,
							["replay_north_m"] = s.North, ["replay_east_m"] = s.East, ["heading_deg"] = Angles.ToDegrees(s.Heading), ["motion"] = s.Motion.ToString(),
						});
					}
				}
			});

			bootstrap.CameraRig.Chase.Distance = 160f;
			bootstrap.CameraRig.Chase.Height = 90f;
			yield return Capture(Path.Combine(folder, "pier11-traffic.png"), 160f, 90f);
			var run = EndAndLoad();
			var summary = new JObject
			{
				["pack"] = pack.Date, ["pack_digest"] = pack.Digest, ["start_utc"] = settings.scenarioStartUtc, ["landing"] = "87 Wall St/Pier 11",
				["vessels_in_pack"] = pack.Vessels.Count, ["hulls_built"] = view.Hulls.Count, ["frames_checked"] = shownPerFrame.Count,
				["hulls_shown_min"] = shownPerFrame.Min(), ["hulls_shown_max"] = shownPerFrame.Max(), ["hull_positions_compared"] = compared,
				["worst_position_error_m"] = worst, ["worst_heading_error_deg"] = worstHeading, ["tolerance_m"] = PositionTolerance,
				["view_range_m"] = view.ViewRange, ["area_margin_m"] = view.AreaMargin, ["samples"] = samples,
				["recording"] = Path.GetFileName(run.TelemetryPath), ["traffic_events"] = run.Header["traffic_events"],
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());
			Assert.That(shownPerFrame.Min(), Is.GreaterThan(20), "a busy harbor: more than 20 hulls drawn at every frame");
			Assert.That(compared, Is.GreaterThan(2000));
			Assert.That(run.Header["traffic_digest"], Is.EqualTo(pack.Digest));
			Assert.That(run.Header["traffic_removed_mmsi"], Is.EqualTo("none"));
		}

		[UnityTest]
		public IEnumerator LeaveOut_TheLearnersOwnFerryIsAbsent_AndEveryOtherVesselIsThere()
		{
			// Pier 11 at 18:00 EDT on 2025-07-14: one of the fleet's ferries lies at the landing, 44 m from the catalog's start.
			yield return LoadScene();
			var folder = Artifacts("leave-out");
			var pack = Pack(Day);
			var independent = new TrafficReplay(pack);
			var settings = Start(new ScenarioChoice("87", Day, 18 * 60, "t-class", berthId: "enc:0226015B0512B198"), new ConstantControls(ControlInputs.Neutral), folder,
				new ScenarioTraffic { day = Day, leaveOut = ScenarioTraffic.Automatic });
			var bootstrap = Bootstrap;
			var offset = _runner.Traffic.EpochOffset;
			var start = settings.StartPosition;
			// The nearest ferry candidate to the start, worked out here from the independent replay: present now, or by its report
			// nearest in time within two minutes; within 150 m.
			TrafficVessel expected = null;
			var best = double.PositiveInfinity;
			foreach (var vessel in pack.Vessels.Where(v => v.FerryCandidate))
			{
				HorizontalVector at;
				if (independent.TrySample(vessel.Index, offset, out var now))
				{
					at = now.Position;
				}
				else
				{
					var reports = Enumerable.Range(vessel.FirstReport, vessel.Reports).Where(r => Math.Abs(pack.Reports.Time(r) - offset) <= 120.0).ToList();
					if (reports.Count == 0)
					{
						continue;
					}

					var nearest = reports.OrderBy(r => Math.Abs(pack.Reports.Time(r) - offset)).First();
					at = vessel.Moving ? new HorizontalVector(pack.Reports.North(nearest), pack.Reports.East(nearest)) : new HorizontalVector(vessel.StaticNorth, vessel.StaticEast);
				}

				var distance = (at - start).Magnitude;
				if (distance <= 150.0 && distance < best)
				{
					best = distance;
					expected = vessel;
				}
			}

			Assert.That(expected, Is.Not.Null, "a ferry candidate lies at Pier 11 at 18:00");
			var removed = _runner.Traffic.Removed;
			Assert.That(removed, Is.Not.Null);
			Assert.That(removed.Mmsi, Is.EqualTo(expected.Mmsi), "the nearest ferry candidate to the start");
			Assert.That(removed.FerryCandidate, Is.True);
			Assert.That(_runner.Traffic.OwnFerry.Distance, Is.EqualTo(best).Within(1e-6));
			var view = bootstrap.TrafficView;
			var removedHull = view.HullFor(removed.Mmsi);
			var otherShown = new List<int>();
			var removedPresent = 0;
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			yield return Drive(() => _runner.Driver.StepCount >= 6000, 6000, 0.5, () =>
			{
				var check = CheckHulls(bootstrap, independent, removed.Index, offset);
				otherShown.Add(check.Shown);
				if (independent.IsPresent(removed.Index, view.PlacedTime + offset))
				{
					removedPresent++;
				}

				if (removedHull != null)
				{
					Assert.That(removedHull.Shown || removedHull.gameObject.activeSelf, Is.False, "the learner's own ferry is never drawn");
				}
			});

			yield return Capture(Path.Combine(folder, "pier11-own-ferry-left-out.png"), 70f, 35f);
			var run = EndAndLoad();
			var log = run.TrafficEvents;
			var summary = new JObject
			{
				["pack"] = pack.Date, ["pack_digest"] = pack.Digest, ["start_utc"] = settings.scenarioStartUtc, ["landing"] = "87 Wall St/Pier 11",
				["removed_mmsi"] = removed.Mmsi, ["removed_name"] = removed.Name, ["removed_distance_m"] = best, ["removed_time_offset_s"] = _runner.Traffic.OwnFerry.TimeOffset,
				["frames_the_removed_vessel_was_in_the_pack"] = removedPresent, ["frames_checked"] = otherShown.Count,
				["other_hulls_shown_min"] = otherShown.Min(), ["other_hulls_shown_max"] = otherShown.Max(),
				["header_leave_out"] = run.Header["traffic_leave_out"], ["header_removed_mmsi"] = run.Header["traffic_removed_mmsi"],
				["events_with_the_removed_vessel"] = log.Records.Count(r => r.Mmsi == removed.Mmsi), ["recording"] = Path.GetFileName(run.TelemetryPath),
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());
			Assert.That(removedPresent, Is.GreaterThan(0), "the ferry is in the day's pack while the run goes, so leaving it out means something");
			Assert.That(otherShown.Min(), Is.GreaterThan(10), "everyone else is there");
			Assert.That(run.Header["traffic_leave_out"], Is.EqualTo("auto"));
			Assert.That(run.Header["traffic_removed_mmsi"], Is.EqualTo(removed.Mmsi.ToString(CultureInfo.InvariantCulture)));
			Assert.That(log.Records.Any(r => r.Mmsi == removed.Mmsi), Is.False, "it raised no alarm and no collision");
		}

		/// <summary>Another operator's 25 m passenger ferry that never sends an AIS heading, berthed at Pier 11 at 18:00 EDT on 2025-07-14.</summary>
		private const int BerthedMmsi = 367434220;

		/// <summary>
		/// Pier 11 at 18:00 EDT on 2025-07-14, the leave-out's scenario: MMSI 367434220 lies berthed 16.5 m from the catalog's start,
		/// its rectangle within the 5 m alongside margin of the learner's hull. The run starts alongside it: no alarm and no
		/// collision for it at the start (before the rule its rectangle, turned by GPS jitter, overlapped the hull at 0.01 s). While
		/// it lies moored its drawn hull holds one heading, its segment's median COG at speed, where the jitter used to swing it;
		/// and the run replays bit for bit, its alongside event included.
		/// </summary>
		[UnityTest]
		public IEnumerator Alongside_AtPier11_TheFerryBerthedBesideTheStart_NeitherAlarmsNorCollides_HoldsItsHeading_AndReplaysBitForBit()
		{
			yield return LoadScene();
			var folder = Artifacts("alongside");
			var pack = Pack(Day);
			var settings = Start(new ScenarioChoice("87", Day, 18 * 60, "t-class", berthId: "enc:0226015B0512B198"), new ConstantControls(ControlInputs.Neutral), folder,
				new ScenarioTraffic { day = Day, leaveOut = ScenarioTraffic.Automatic });
			var bootstrap = Bootstrap;
			Assert.That(pack.Vessel(BerthedMmsi), Is.Not.Null);
			Assert.That(_runner.Traffic.RemovedMmsi, Is.Not.EqualTo(BerthedMmsi), "not the learner's own ferry: another operator's");
			var headings = new List<double>();
			var rotations = new List<Quaternion>();
			var sources = new HashSet<HeadingSource>();
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			yield return Drive(() => _runner.Driver.StepCount >= 6000, 6000, 0.5, () =>
			{
				var hull = bootstrap.TrafficView.HullFor(BerthedMmsi);
				if (hull != null && hull.Shown)
				{
					headings.Add(hull.Heading);
					rotations.Add(hull.transform.rotation);
					sources.Add(hull.HeadingSource);
				}
			});

			yield return Capture(Path.Combine(folder, "pier11-alongside-at-start.png"), 45f, 30f);
			var run = EndAndLoad();
			var log = run.TrafficEvents;
			var berthed = log.Records.Where(r => r.Mmsi == BerthedMmsi).ToList();
			var parted = berthed.FirstOrDefault(r => r.Kind == TrafficEventKind.AlongsideEnded);
			var replay = ReplayPlayer.Verify(run, _runner.Parameters, new VesselDynamicsFactory(), geometry: TripData.Landings().Geometry, trafficPacks: _runner.TrafficPacks);
			var summary = new JObject
			{
				["pack"] = pack.Date, ["pack_digest"] = pack.Digest, ["start_utc"] = settings.scenarioStartUtc, ["landing"] = "87 Wall St/Pier 11",
				["berthed_mmsi"] = BerthedMmsi, ["removed_mmsi"] = _runner.Traffic.RemovedMmsi, ["alongside_margin_m"] = log.Header["alongside_margin_m"],
				["berthed_events"] = new JArray(berthed.Select(r => r.Line)), ["parted_at_s"] = parted?.Time,
				["other_events"] = new JArray(log.Records.Where(r => r.Mmsi != BerthedMmsi).Select(r => r.Line)),
				["frames_with_the_berthed_hull_drawn"] = headings.Count, ["berthed_heading_deg"] = headings.Count > 0 ? Angles.ToDegrees(headings[0]) : double.NaN,
				["berthed_heading_sources"] = new JArray(sources.Select(s => s.ToString())), ["distinct_berthed_headings"] = headings.Distinct().Count(),
				["replay"] = replay.ToString(), ["recording"] = Path.GetFileName(run.TelemetryPath),
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());

			Assert.That(berthed.Count, Is.GreaterThan(0));
			Assert.That((berthed[0].Kind, berthed[0].Step), Is.EqualTo((TrafficEventKind.AlongsideStarted, 1L)), "alongside from the first step");
			var alongsideUntil = parted?.Step ?? long.MaxValue;
			Assert.That(berthed.Any(r => (r.Kind == TrafficEventKind.AlarmRaised || r.Kind == TrafficEventKind.CollisionStarted) && r.Step < alongsideUntil), Is.False,
				"no alarm and no collision for it at the start");
			Assert.That(headings.Count, Is.GreaterThan(100), "drawn through the run");
			Assert.That(sources, Is.EqualTo(new HashSet<HeadingSource> { HeadingSource.MedianCog }), "moored without an AIS heading: its median COG at speed");
			Assert.That(headings.Distinct().Count(), Is.EqualTo(1), "one heading while moored");
			Assert.That(rotations.All(r => r == rotations[0]), Is.True, "the drawn hull does not turn");
			Assert.That(replay.Matches, Is.True, replay.ToString());
			Assert.That(replay.Traffic.Compared, Is.EqualTo(log.Records.Count));
		}

		/// <summary>
		/// A synthetic traffic pack for the scenario's day, in a temporary folder the runner reads instead of the repository's: one
		/// big slow vessel (a 60 by 20 m tug and tow) coming down the river at 1 m/s straight at the learner, starting 700 m up the
		/// line the learner runs. Made up for the test; not AIS data.
		/// </summary>
		private TrafficPacks SyntheticTraffic(HorizontalVector learnerStart, double learnerHeading, double epochOffset)
		{
			_synthetic = Path.Combine(Path.GetTempPath(), "ferrysim-e2e-traffic-" + Guid.NewGuid().ToString("N"));
			var course = Angles.WrapTwoPi(learnerHeading + Math.PI);
			var t0 = epochOffset - 120.0;
			var origin = learnerStart + HorizontalVector.FromSpeedToward(700.0 + 120.0, learnerHeading);
			var count = 20;
			var vessel = new int[count];
			var segment = new int[count];
			var time = new double[count];
			var north = new double[count];
			var east = new double[count];
			var sog = new float[count];
			var cog = new float[count];
			var heading = new float[count];
			for (var k = 0; k < count; k++)
			{
				var at = origin + HorizontalVector.FromSpeedToward(60.0 * k, course);
				time[k] = t0 + 60.0 * k;
				north[k] = at.North;
				east[k] = at.East;
				sog[k] = 1.0f;
				cog[k] = (float)course;
				heading[k] = (float)course;
			}

			var vessels = new[] { new TrafficVessel(0, SyntheticMmsi, "SYNTHETIC TOW", 31, TrafficClass.TugTow, 60.0, 20.0, 3.0, time[0], time[count - 1], count, 1, true, false) };
			var day = DateTime.ParseExact(Day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
			var pack = new TrafficPack(Day, EnvironmentPackJson.NewYorkMidnight(day), -3600.0, 90000.0, vessels, new TrafficReports(vessel, segment, time, north, east, sog, cog, heading), string.Empty);
			TrafficPackWriter.Write(pack, Path.Combine(_synthetic, "harbor", "traffic", Day));
			return new TrafficPacks(_synthetic);
		}

		/// <summary>
		/// In the stream under the Brooklyn Bridge (current station NYH1920), heading 080° up the river's channel, near slack water,
		/// steered by <see cref="PursuitControls"/> at the synthetic vessel. The slice's depth grid has the bed at least 13 m below
		/// NAVD88 within 100 m either side of that line for its first kilometre, where the two meet (035° ran aground on the
		/// Manhattan side after about 400 m).
		/// </summary>
		private ScenarioSettings StartSynthetic(string folder, out TrafficPacks traffic)
		{
			var station = EnvironmentPackJson.Load(Path.Combine(TripData.Root, EnvironmentPacks.RegionFolder("lower-east-river"), "days", Day)).CurrentStations.Single(s => s.Id == "NYH1920");
			var start = new HorizontalVector(station.North, station.East);
			const double headingDegrees = 80.0;
			// 13:00 EDT, near slack water (as the launcher's garbage test chose it).
			traffic = SyntheticTraffic(start, Angles.ToRadians(headingDegrees), 13 * 3600.0);
			_runner.TrafficPacks = traffic;
			return Start(new ScenarioChoice("87", Day, 13 * 60, "t-class", berthId: "enc:0226015B0512B198"), new PursuitControls(_runner), folder,
				new ScenarioTraffic { day = Day }, scenario =>
				{
					scenario.startLatitude = station.Latitude;
					scenario.startLongitude = station.Longitude;
					scenario.startHeadingDegrees = headingDegrees;
				});
		}

		[UnityTest]
		public IEnumerator Alarm_ThenCollision_WithASyntheticVessel_AreRecorded_AndShownInTheDebrief()
		{
			yield return LoadScene();
			var folder = Artifacts("alarm-collision");
			StartSynthetic(folder, out _);
			var bootstrap = Bootstrap;
			var states = new List<string>();
			double alarmShownAt = double.NaN, collisionShownAt = double.NaN;
			var capturedAlarm = false;
			bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Chase;
			var ended = false;
			var guard = 0;
			while (!ended && guard++ < 400)
			{
				_runner.Resume();
				_runner.AdvanceFrame(0.5);
				_runner.Pause();
				yield return null;
				var readout = _runner.TrafficAlarm;
				// The capture rebuilds the UI's view, so the panel is looked up every frame (it refreshes while hidden too).
				var shown = bootstrap.WheelhouseUI.View.Panel.Traffic.StateText;
				if (states.Count == 0 || states[states.Count - 1] != shown)
				{
					states.Add(shown);
				}

				if (readout.State == TrafficAlarmState.Alarm && double.IsNaN(alarmShownAt))
				{
					alarmShownAt = _runner.Driver.SimulationTime;
				}

				if (readout.State == TrafficAlarmState.Collision && double.IsNaN(collisionShownAt))
				{
					collisionShownAt = _runner.Driver.SimulationTime;
				}

				if (!capturedAlarm && readout.State == TrafficAlarmState.Alarm && readout.Tcpa < 25.0)
				{
					capturedAlarm = true;
					bootstrap.CameraRig.CurrentMode = CameraRig.Mode.Wheelhouse;
					yield return Capture(Path.Combine(folder, "cpa-alarm-wheelhouse.png"), 60f, 25f, wheelhouse: true);
				}

				ended = _runner.TrafficMonitor.CollisionsStarted > 0 && !_runner.TrafficMonitor.IsColliding(0);
				if (_runner.TrafficMonitor.IsColliding(0) && !File.Exists(Path.Combine(folder, "collision-chase.png")))
				{
					yield return Capture(Path.Combine(folder, "collision-chase.png"), 70f, 30f);
				}
			}

			var run = EndAndLoad();
			var log = run.TrafficEvents;
			var alarm = log.Records.First(r => r.Kind == TrafficEventKind.AlarmRaised);
			var collision = log.Records.First(r => r.Kind == TrafficEventKind.CollisionStarted);
			var debrief = bootstrap.WheelhouseUI.View.Debrief;
			Assert.That(debrief.TryLoad(run.TelemetryPath), Is.True, debrief.ErrorText);
			bootstrap.WheelhouseUI.View.DebriefVisible = true;
			yield return Capture(Path.Combine(folder, "debrief.png"), 70f, 30f);
			bootstrap.WheelhouseUI.View.DebriefVisible = false;
			var events = debrief.Events.Where(e => e.Kind == DebriefEventKind.TrafficAlarm || e.Kind == DebriefEventKind.TrafficCollision).ToList();
			var summary = new JObject
			{
				["start_utc"] = run.Header["scenario_start_utc"], ["traffic_pack"] = run.Header["traffic_pack"], ["traffic_digest"] = run.Header["traffic_digest"],
				["synthetic_mmsi"] = SyntheticMmsi, ["alarm_time_s"] = alarm.Time, ["alarm_tcpa_s"] = alarm.Tcpa, ["alarm_cpa_m"] = alarm.Cpa, ["alarm_range_m"] = alarm.Range,
				["collision_time_s"] = collision.Time, ["collision_relative_speed_mps"] = collision.RelativeSpeed, ["seconds_of_warning"] = collision.Time - alarm.Time,
				["panel_states_in_order"] = new JArray(states), ["panel_alarm_shown_at_s"] = alarmShownAt, ["panel_collision_shown_at_s"] = collisionShownAt,
				["debrief_events"] = new JArray(events.Select(e => e.Describe(DistanceUnit.Metres))),
				["events"] = new JArray(log.Records.Select(r => r.Line)), ["recording"] = Path.GetFileName(run.TelemetryPath),
				["thresholds"] = new JObject { ["cpa_m"] = log.Header["cpa_alarm_m"], ["tcpa_s"] = log.Header["tcpa_alarm_s"], ["closing_speed_mps"] = log.Header["closing_speed_mps"], ["confidence"] = "estimated" },
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());

			Assert.That(alarm.Mmsi, Is.EqualTo(SyntheticMmsi));
			Assert.That(collision.Mmsi, Is.EqualTo(SyntheticMmsi));
			Assert.That(alarm.Time, Is.LessThan(collision.Time), "the CPA alarm before the collision");
			Assert.That(collision.Time - alarm.Time, Is.GreaterThan(20.0), "with time to act");
			Assert.That(log.Records.Count(r => r.Kind == TrafficEventKind.CollisionStarted), Is.EqualTo(1), "one collision, not doubled");
			Assert.That(alarmShownAt, Is.LessThan(collisionShownAt), "the panel showed the alarm, then the collision");
			Assert.That(states, Does.Contain("ALARM").And.Contain("COLLISION"));
			Assert.That(events.Select(e => e.Kind), Is.EqualTo(new[] { DebriefEventKind.TrafficAlarm, DebriefEventKind.TrafficCollision }), "both in the debrief, in order");
			Assert.That(events.All(e => e.Mmsi == SyntheticMmsi && e.VesselName == "SYNTHETIC TOW"), Is.True);
			Assert.That(events[0].Describe(DistanceUnit.Metres), Does.StartWith("CPA ALARM, SYNTHETIC TOW"));
			Assert.That(events[1].Describe(DistanceUnit.Metres), Does.StartWith("COLLISION with SYNTHETIC TOW"));
			Assert.That(debrief.Timeline.Events.Count(e => e.Kind == DebriefEventKind.TrafficCollision), Is.EqualTo(1), "on the timeline");
		}

		[UnityTest]
		public IEnumerator Replay_ATrafficRunReplaysBitForBit_EventsIncluded_AndAnotherDaysTrafficIsRefused()
		{
			yield return LoadScene();
			var folder = Artifacts("replay");
			StartSynthetic(folder, out var traffic);
			var live = new StateDigest();
			var digest = new StepWatch(state => live.Add(state));
			_runner.Driver.AddObserver(digest);
			yield return Drive(() => _runner.Driver.StepCount >= 18000, 18000);
			_runner.Driver.RemoveObserver(digest);
			var run = EndAndLoad();
			var vessel = _runner.Parameters;
			var geometry = TripData.Landings().Geometry;
			var other = Pack(OtherDay);
			var recorded = traffic.Load(Day);

			var same = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), geometry: geometry, trafficPacks: traffic);
			var refused = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), geometry: geometry, traffic: other);
			var error = Assert.Throws<InvalidOperationException>(() => _runner.InitializeReplay(run, vessel, null, other));
			_runner.InitializeReplay(run, vessel);
			_runner.MaxStepsPerFrame = 100;
			yield return Drive(() => _runner.ReplayCompleted, 18000);
			var inScene = _runner.ReplayResult;
			var summary = new JObject
			{
				["recording"] = Path.GetFileName(run.TelemetryPath), ["traffic_pack"] = run.TrafficPack, ["traffic_digest"] = run.TrafficDigest,
				["traffic_events"] = run.TrafficEvents.Records.Count, ["encounter_digest"] = run.TrafficEvents.Digest,
				["state_digest"] = run.StateDigest, ["live_state_digest"] = live.Text, ["replay"] = same.ToString(), ["in_scene_replay"] = inScene?.ToString(),
				["other_day"] = other.Date, ["other_digest"] = other.Digest, ["refused"] = refused.ToString(), ["runner_refusal"] = error.Message,
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), summary + "\n");
			TestContext.WriteLine(summary.ToString());

			Assert.That(run.HasTraffic, Is.True);
			Assert.That(run.TrafficDigest, Is.EqualTo(recorded.Digest));
			Assert.That(run.TrafficEvents.Records.Count, Is.GreaterThanOrEqualTo(3), "the alarm and the collision's start and end at least");
			Assert.That(run.TrafficEvents.Records.Count(r => r.Kind == TrafficEventKind.CollisionStarted), Is.GreaterThanOrEqualTo(1), "a collision among them");
			Assert.That(run.StateDigest, Is.EqualTo(live.Text));
			Assert.That(same.Matches, Is.True, same.ToString());
			Assert.That(same.Traffic.Compared, Is.EqualTo(run.TrafficEvents.Records.Count), "every event replayed");
			Assert.That(same.Traffic.ReplayedDigest, Is.EqualTo(run.TrafficEvents.Digest));
			Assert.That(refused.Matches, Is.False);
			Assert.That(refused.RowsCompared, Is.Zero, "nothing replayed against another day's traffic");
			Assert.That(refused.ToString(), Does.Contain(recorded.Digest).And.Contain(other.Digest));
			Assert.That(error.Message, Does.Contain(recorded.Digest).And.Contain(other.Digest));
			Assert.That(inScene, Is.Not.Null);
			Assert.That(inScene.Matches, Is.True, inScene?.ToString());
			Assert.That(inScene.Traffic.Compared, Is.EqualTo(run.TrafficEvents.Records.Count));
		}

		[UnityTest]
		public IEnumerator Traffic_AtTheDaysBusiestMinute_CostsUnderAMillisecondPerFrame_AndMakesNoGarbage()
		{
			yield return LoadScene();
			var folder = Artifacts("cost");
			var pack = Pack(Day);
			var replay = new TrafficReplay(pack);
			var bootstrap = Bootstrap;
			Area(bootstrap, out var minNorth, out var maxNorth, out var minEast, out var maxEast);
			// The minute with the most vessels in the slice's area, from the pack: what the view has to draw at worst. And which
			// vessels are inside at some minute of the day, and at that minute.
			var busiest = 0;
			var most = -1;
			var everInside = new bool[pack.Vessels.Count];
			for (var minute = 0; minute < 24 * 60; minute++)
			{
				var count = 0;
				for (var v = 0; v < pack.Vessels.Count; v++)
				{
					if (replay.TrySample(v, minute * 60.0, out var s) && s.North >= minNorth && s.North <= maxNorth && s.East >= minEast && s.East <= maxEast)
					{
						count++;
						everInside[v] = true;
					}
				}

				if (count > most)
				{
					most = count;
					busiest = minute;
				}
			}

			var insideAtBusiest = pack.Vessels.Where(v => replay.TrySample(v.Index, busiest * 60.0, out var s) && s.North >= minNorth && s.North <= maxNorth && s.East >= minEast && s.East <= maxEast).ToList();
			// The view's hull filter: the replay's conservative bound now; the reports and the straight lines between them before the
			// wave 2b review's fix, kept here only to count what it missed.
			var kept = pack.Vessels.Where(v => replay.MayEnter(v.Index, minNorth, maxNorth, minEast, maxEast)).ToList();
			var linesOnly = pack.Vessels.Where(v => ReportsOrLinesInside(pack, v, minNorth, maxNorth, minEast, maxEast)).ToList();

			var choice = new ScenarioChoice("87", Day, busiest, "t-class", berthId: "enc:0226015B0512B198");
			var withTraffic = new Measurement();
			yield return Measure(choice, new ScenarioTraffic { day = Day }, folder, withTraffic);
			yield return Capture(Path.Combine(folder, "busiest-minute.png"), 300f, 220f);
			var without = new Measurement();
			yield return Measure(choice, new ScenarioTraffic(), folder, without);
			var view = bootstrap.TrafficView;
			var stats = new JObject
			{
				["day"] = Day, ["busiest_minute_local"] = $"{busiest / 60:D2}:{busiest % 60:D2}", ["vessels_in_area_then"] = most, ["hulls_built"] = withTraffic.Hulls,
				["hulls_by_the_old_reports_and_lines_filter"] = linesOnly.Count, ["hulls_by_the_curve_bound"] = kept.Count,
				["kept_only_by_the_curve_bound"] = kept.Count(v => !linesOnly.Contains(v)),
				["inside_at_some_minute_but_dropped_by_the_old_filter"] = pack.Vessels.Count(v => everInside[v.Index] && !linesOnly.Contains(v)),
				["inside_at_the_busiest_minute_but_dropped_by_the_old_filter"] = insideAtBusiest.Count(v => !linesOnly.Contains(v)),
				["hulls_drawn_mean"] = withTraffic.Shown, ["view_range_m"] = 12000, ["gc_allocations_per_frame"] = 0,
				["place_ms"] = withTraffic.Place, ["advance_ms_with_traffic"] = withTraffic.Advance, ["advance_ms_without"] = without.Advance,
				["frame_ms_with_traffic"] = withTraffic.Frame, ["frame_ms_without"] = without.Frame,
				["note"] = "Mac mini M1, batchmode editor rendering off screen. place_ms: TrafficView.Place's own CPU time (every hull sampled, culled and placed) over 600 rendered frames. advance_ms: the runner's frame at 60 fps (about 1.7 physics steps; with traffic the monitor checks every vessel of the day at every step), timed over 600 frames without rendering. frame_ms: rendered frames at a 60 fps capture rate, rendering included, with the machine's once-a-minute stall (50 to 140 ms) in the max. Budgets are on place_ms and the advance difference.",
			};
			File.WriteAllText(Path.Combine(folder, "summary.json"), stats + "\n");
			TestContext.WriteLine(stats.ToString());
			Assert.That(most, Is.GreaterThan(40), "a real busy minute");
			Assert.That(withTraffic.Hulls, Is.EqualTo(kept.Count), "a hull for every vessel the bound keeps");
			Assert.That(pack.Vessels.Where(v => everInside[v.Index]).All(v => kept.Contains(v)), Is.True, "every vessel inside the area at any minute of the day has a hull");
			Assert.That(withTraffic.Shown, Is.GreaterThan(40), "every vessel in the area drawn");
			Assert.That((double)withTraffic.Place["p95"], Is.LessThan(1.0), "placing every hull under a millisecond (p95)");
			Assert.That((double)withTraffic.Advance["p95"] - (double)without.Advance["p95"], Is.LessThan(1.0), "the monitor under a millisecond per frame (p95)");
		}

		private sealed class Measurement
		{
			public JObject Place;
			public JObject Advance;
			public JObject Frame;
			public double Shown;
			public int Hulls;
		}

		/// <summary>
		/// The choice with the traffic given: 600 frames of the runner alone at 60 fps (no rendering), timed, and a check that a
		/// frame makes no garbage; then 600 rendered frames at a 60 fps capture rate, the view placing hulls in each.
		/// </summary>
		private IEnumerator Measure(ScenarioChoice choice, ScenarioTraffic traffic, string folder, Measurement into)
		{
			var bootstrap = Bootstrap;
			Start(choice, new ConstantControls(ControlInputs.Neutral), folder, traffic);
			_runner.RecordRuns = false;
			_runner.ResetRun();
			_runner.Pause();
			var view = bootstrap.TrafficView;
			view.ViewRange = 12000f;
			void Frame()
			{
				_runner.Resume();
				_runner.AdvanceFrame(1.0 / 60.0);
				_runner.Pause();
				view.Place();
			}

			for (var i = 0; i < 30; i++)
			{
				Frame();
			}

			Assert.That(Frame, Is.Not.AllocatingGCMemory(), traffic.IsOn ? "a frame with traffic: steps, the monitor, the hulls" : "a frame without traffic");
			var advance = new List<double>();
			var watch = new Stopwatch();
			for (var i = 0; i < 600; i++)
			{
				watch.Restart();
				_runner.Resume();
				_runner.AdvanceFrame(1.0 / 60.0);
				_runner.Pause();
				watch.Stop();
				advance.Add(watch.Elapsed.TotalMilliseconds);
			}

			// Rendered frames: the runner steps itself at the capture rate, and the view places the hulls in LateUpdate.
			var place = new List<double>();
			var frame = new List<double>();
			var drawn = 0.0;
			var capture = Time.captureDeltaTime;
			Time.captureDeltaTime = 1f / 60f;
			_runner.Resume();
			try
			{
				yield return null;
				var last = Time.realtimeSinceStartupAsDouble;
				for (var i = 0; i < 600; i++)
				{
					yield return null;
					var now = Time.realtimeSinceStartupAsDouble;
					frame.Add((now - last) * 1000.0);
					last = now;
					place.Add(view.LastMilliseconds);
					drawn += view.ShownCount;
				}
			}
			finally
			{
				Time.captureDeltaTime = capture;
				_runner.Pause();
			}

			into.Place = Stats(place);
			into.Advance = Stats(advance);
			into.Frame = Stats(frame);
			into.Shown = drawn / 600.0;
			into.Hulls = view.Hulls.Count;
		}

		private static JObject Stats(List<double> values)
		{
			var sorted = values.OrderBy(v => v).ToList();
			double Percentile(double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * (sorted.Count - 1)))];
			return new JObject { ["frames"] = sorted.Count, ["mean"] = sorted.Average(), ["p50"] = Percentile(0.5), ["p95"] = Percentile(0.95), ["max"] = sorted[sorted.Count - 1] };
		}

		private IEnumerator Capture(string path, float distance, float height, bool wheelhouse = false)
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
			if (!wheelhouse)
			{
				rig.CurrentMode = CameraRig.Mode.Chase;
				rig.Chase.Distance = distance;
				rig.Chase.Height = height;
				rig.Chase.Snap();
			}

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

		/// <summary>
		/// A deterministic scripted approach: both levers at half ahead, and the helm turning the bow onto the bearing of the
		/// run's first traffic vessel (the synthetic one) where the replay puts it, so the wind's and the current's set cannot
		/// make the approach miss. It reads the runner's state before each step, so a replay applies the same controls. Not
		/// operating guidance: it drives into the vessel on purpose.
		/// </summary>
		private sealed class PursuitControls : IControlInputSource
		{
			private readonly VesselSimulationRunner _runner;

			public PursuitControls(VesselSimulationRunner runner) => _runner = runner;

			public ControlInputs ControlsForStep(long step)
			{
				var traffic = _runner.Traffic;
				var state = _runner.State;
				if (traffic == null || !traffic.Replay.TrySample(0, traffic.PackTime(state.Time), out var target))
				{
					return new ControlInputs(0.5, 0.5, 0.0);
				}

				var bearing = Math.Atan2(target.East - state.East, target.North - state.North);
				var helm = Math.Max(-1.0, Math.Min(1.0, 3.0 * Angles.WrapPi(bearing - state.Heading)));
				return new ControlInputs(0.5, 0.5, Math.Round(helm, 2));
			}
		}

		/// <summary>Calls back after every physics step with the state after it.</summary>
		private sealed class StepWatch : IStepObserver
		{
			private readonly Action<VesselState> _after;

			public StepWatch(Action<VesselState> after) => _after = after;

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry) => _after(state);
		}
	}
}
