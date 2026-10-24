using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
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
	/// Failure modes: missing/wrong trace; refused or overlapping start; departure/other-face contact;
	/// bridge detour or overlong crossing; missed required span; hard arrival; drift or loss of contact during the hold; state/recording/geometry
	/// divergence; UI detached from the real runner. Every physics step is observed, including between frames.
	/// </summary>
	public sealed class TripAcceptanceTests
	{
		private VesselSimulationRunner _runner;
		private RenderTexture _uiTarget;
		private PanelSettings _uiSettings;

		[UnityTearDown]
		public IEnumerator Cleanup()
		{
			_runner?.EndRun();
			var scene = SceneManager.GetSceneByPath(TripData.ScenePath);
			if (scene.IsValid() && scene.isLoaded)
			{
				SceneManager.SetActiveScene(SceneManager.CreateScene("M2 teardown " + Time.frameCount));
				yield return SceneManager.UnloadSceneAsync(scene);
			}
			if (_uiSettings != null) Object.Destroy(_uiSettings);
			if (_uiTarget != null)
			{
				_uiTarget.Release();
				Object.Destroy(_uiTarget);
			}
		}

		[UnityTest]
		public IEnumerator Pier11_ToDumbo_Direct_NoBridge_SoftLanding_Holds_AndReplaysExactly() => Run(TripData.Pier11Dumbo);

		[UnityTest]
		public IEnumerator Dumbo_ToSouthWilliamsburg_UnderBothBridges_SoftLanding_Holds_AndReplaysExactly() => Run(TripData.DumboSouthWilliamsburg);

		// Regression failure modes: subdivision must not require brushing every segment;
		// no contact and contact with any non-arrival segment must still be rejected.
		[Test]
		public void SegmentedArrival_MiddleLanding_PassesOnlyArrivalFaceAcceptance()
		{
			var leg = TripData.Pier11Dumbo;
			var vessel = TripData.Vessel();
			var landings = TripData.Landings();
			var arrival = landings.PrimaryFace(leg.ArrivalStop).Segments.Single().Segment;
			var parts = new List<ContactSegment>();
			var arrivalIds = new HashSet<int>();
			foreach (var segment in landings.Geometry.Segments)
			{
				if (segment.Id != arrival.Id) { parts.Add(segment); continue; }
				for (var i = 0; i < 5; i++)
				{
					var id = 100000 + i;
					arrivalIds.Add(id);
					parts.Add(new ContactSegment(segment.Start + (segment.End - segment.Start) * (i / 5.0),
						segment.Start + (segment.End - segment.Start) * ((i + 1) / 5.0), segment.Surface, id));
				}
			}
			var scenario = TripData.Start(leg, landings, vessel);
			var environment = scenario.environment.Create();
			var simulation = (VesselDynamicsSimulation)new VesselDynamicsFactory().Create(vessel, environment,
				new SegmentListGeometry(parts), scenario.InitialState(environment));
			var trace = ControlTrace.Load(leg.TracePath);
			var touched = new HashSet<int>();
			for (long step = 0; step < trace.EndStep; step++)
			{
				simulation.Step(trace.At(step));
				for (var i = 0; i < simulation.ContactCount; i++) touched.Add(simulation.GetContact(i).SegmentId);
				if (step < trace.EndStep - 1000) continue;
				Assert.That(simulation.ContactCount, Is.GreaterThan(0), "contact throughout the final ten seconds");
				Assert.That(simulation.Telemetry.ContactForce, Is.GreaterThan(0));
				Assert.That(Units.MetresPerSecondToKnots(simulation.Telemetry.SpeedOverGround), Is.LessThan(0.1));
				for (var i = 0; i < simulation.ContactCount; i++)
					Assert.That(arrivalIds.Contains(simulation.GetContact(i).SegmentId), Is.True);
			}
			var state = simulation.State;
			Assert.That(FaceClearance.Check(vessel, state.North, state.East, state.Heading,
				parts.Where(s => arrivalIds.Contains(s.Id)).ToArray()).Overlaps, Is.True);
			var normal = arrival.WaterNormal;
			Assert.That(Math.Abs(Angles.ToDegrees(Angles.WrapPi(state.Heading - Math.Atan2(-normal.East, -normal.North)))),
				Is.LessThan(5.0));
			Assert.That(touched, Is.EquivalentTo(new[] { 100002 }), "valid landing touches only the middle subsegment");
			TestContext.WriteLine("segmented arrival ids: " + string.Join(",", arrivalIds.OrderBy(id => id)) +
				"; touched: " + string.Join(",", touched));
			TripObserver.AssertOnlyArrivalFaceTouched(touched, arrivalIds);
		}

		[Test]
		public void OnlyArrivalFaceAcceptance_RejectsEveryNonArrivalSegment()
		{
			var landings = TripData.Landings();
			var arrivalIds = new HashSet<int>(landings.PrimaryFace(TripData.Pier11Dumbo.ArrivalStop).Segments.Select(s => s.Id));
			foreach (var segment in landings.Geometry.Segments.Where(s => !arrivalIds.Contains(s.Id)))
			{
				var touched = new HashSet<int>(arrivalIds) { segment.Id };
				Assert.Throws<AssertionException>(() => TripObserver.AssertOnlyArrivalFaceTouched(touched, arrivalIds),
					"must reject non-arrival segment " + segment.Id);
			}
		}

		[Test]
		public void OnlyArrivalFaceAcceptance_RejectsNoContact()
		{
			Assert.Throws<AssertionException>(() => TripObserver.AssertOnlyArrivalFaceTouched(new HashSet<int>(),
				new HashSet<int> { 100000, 100001, 100002, 100003, 100004 }));
		}

		private IEnumerator Run(TripLeg leg)
		{
			Assert.That(File.Exists(leg.TracePath), Is.True, "M2 requires the committed " + leg.Key + " input trace");
			var trace = ControlTrace.Load(leg.TracePath);
			Assert.That(trace.EndStep, Is.GreaterThan(1000), "a trip and at least ten seconds holding");
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(TripData.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
			Assert.Fail("The integrated slice currently supports the editor only.");
#endif
			yield return null;
			yield return null;
			var bootstrap = Object.FindFirstObjectByType<SliceBootstrap>();
			Assert.That(bootstrap, Is.Not.Null);
			Assert.That(bootstrap.LoadError, Is.Null);
			_runner = bootstrap.Runner;
			var landings = bootstrap.Landings;
			var vessel = TripData.Vessel();
			var scenario = TripData.Start(leg, landings, vessel);
			var environment = scenario.environment.Create();
			var initial = scenario.InitialState(environment);
			var startClearance = FaceClearance.Check(vessel, initial.North, initial.East, initial.Heading, landings.Geometry.Segments);
			Assert.That(startClearance.IsClear(), Is.True, "start clear of every face");
			var departure = FaceClearance.Check(vessel, initial.North, initial.East, initial.Heading, landings.PrimaryFace(leg.DepartureStop).Segments.Select(s => s.Segment).ToArray());
			Assert.That(departure.Distance, Is.InRange(3.0, 5.0), "bow-on 3-5 m off departure primary face");
			Directory.CreateDirectory(leg.Artifacts);
			_runner.RecordRuns = true;
			_runner.TelemetrySampleHz = 100;
			_runner.RecordingDirectory = leg.Artifacts;
			_runner.MaxStepsPerFrame = 100;
			_runner.Initialize(scenario, trace, vessel);
			_runner.Pause();
			_runner.Driver.StepLimit = trace.EndStep;
			Assert.That(bootstrap.StartRefusal, Is.Null);
			Assert.That(_runner.WorldGeometry, Is.SameAs(landings.Geometry), "real slice landing geometry");

			var ui = bootstrap.WheelhouseUI;
			Assert.That(ui.Source, Is.SameAs(_runner));
			_uiTarget = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			_uiTarget.Create();
			_uiSettings = WheelhousePanelSettings.Create(_uiTarget, Color.clear);
			ui.enabled = false;
			ui.Configure(_runner, _uiSettings);
			ui.enabled = true;
			var rig = bootstrap.CameraRig;
			var camera = rig.GetComponent<Camera>();
			rig.CurrentMode = CameraRig.Mode.Chase;
			rig.Chase.Distance = 65f;
			rig.Chase.Height = 24f;
			var bridges = Object.FindObjectsByType<BridgeGreybox>(FindObjectsSortMode.None);
			var headlessGeometry = TripData.Landings().Geometry;
			Assert.That(WorldGeometryDigest.Of(headlessGeometry), Is.EqualTo(landings.Digest), "independent headless geometry has identical bits");
			var shadow = new VesselDynamicsFactory().Create(vessel, environment, headlessGeometry, initial);
			using (var evidence = new TripObserver(_runner, shadow, trace, leg, landings.PrimaryFace(leg.ArrivalStop), bridges))
			{
				_runner.Driver.AddObserver(evidence);
				try
				{
					while (_runner.Driver.StepCount < trace.EndStep)
					{
						var beforeStep = _runner.Driver.StepCount;
						_runner.Resume();
						_runner.AdvanceFrame(1.0);
						_runner.Pause();
						Assert.That(_runner.Driver.StepCount, Is.GreaterThan(beforeStep), "runner must make progress on every driven frame");
						Assert.That(_runner.Driver.Faulted, Is.False);
						yield return null;
						if (!evidence.DepartureCaptured && _runner.Driver.StepCount >= 1000)
						{
							rig.Chase.Snap();
							yield return Capture(camera, ui, Path.Combine(leg.Key, "departure.png"));
							evidence.DepartureCaptured = true;
						}
						foreach (var passage in evidence.Passages.Where(p => leg.RequiredBridges.Contains(p.Span.Name) && p.MainSpanStep >= 0 && !p.Captured))
						{
							Assert.That(TripData.Inside(new HorizontalVector(_runner.State.North, _runner.State.East), passage.Span.Polygon), Is.True, "screenshot still under " + passage.Span.Name);
							rig.CurrentMode = CameraRig.Mode.Chase;
							rig.Chase.Distance = 95f;
							rig.Chase.Height = 55f;
							rig.Chase.Snap();
							yield return Capture(camera, ui, Path.Combine(leg.Key, "under-" + passage.Span.Name.ToLowerInvariant().Replace(' ', '-') + ".png"));
							passage.Captured = true;
						}
					}
					rig.CurrentMode = CameraRig.Mode.Chase;
					rig.Chase.Distance = 45f;
					rig.Chase.Height = 16f;
					rig.Chase.Snap();
					yield return Capture(camera, ui, Path.Combine(leg.Key, "held.png"));
					Assert.That(ui.View.Panel.ShowingNoData, Is.False, "real wheelhouse UI has live telemetry");
					Assert.That(_runner.HasTelemetry, Is.True);
					_runner.EndRun();
					Assert.That(_runner.RecordingError, Is.Null);
					var run = RecordedRun.Load(_runner.LastRecordingPath);
					Assert.That(run.Header[WorldGeometryDigest.HeaderKey], Is.EqualTo(landings.Digest));
					Assert.That(run.StepCount, Is.EqualTo(trace.EndStep));
					Assert.That(run.Rows.Count, Is.EqualTo(trace.EndStep + 1), "every 100 Hz recorded row, including final state");
					Assert.That(evidence.LiveDigest.Text, Is.EqualTo(evidence.ShadowDigest.Text));
					Assert.That(run.StateDigest, Is.EqualTo(evidence.LiveDigest.Text));
					var replay = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory(), geometry: TripData.Landings().Geometry);
					Assert.That(replay.Matches, Is.True, replay.ToString());
					Assert.That(replay.RowsCompared, Is.EqualTo(run.Rows.Count));
					Assert.That(replay.ReplayedDigest, Is.EqualTo(run.StateDigest));
					var wrongGeometry = ReplayPlayer.Verify(run, vessel, new VesselDynamicsFactory());
					Assert.That(wrongGeometry.Matches, Is.False, "geometry identity must be checked, even for a valid trace");
					Assert.That(wrongGeometry.ToString(), Does.Contain(landings.Digest));
					evidence.AssertAcceptance(vessel, landings, replay, _runner.LastRecordingPath);
				}
				finally { _runner.Driver.RemoveObserver(evidence); }
			}
		}

		private IEnumerator Capture(Camera camera, WheelhouseUI ui, string filename)
		{
			for (var i = 0; i < 4; i++) yield return null;
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
				Assert.That(top.Count(p => p.a > 0), Is.GreaterThan(1000), "real wheelhouse UI rendered");
				for (var i = 0; i < pixels.Length; i++)
				{
					var a = top[i].a / 255f;
					pixels[i] = new Color32((byte)Mathf.Min(255f, top[i].r + pixels[i].r * (1f - a)),
						(byte)Mathf.Min(255f, top[i].g + pixels[i].g * (1f - a)), (byte)Mathf.Min(255f, top[i].b + pixels[i].b * (1f - a)), 255);
				}
				world.SetPixels32(pixels);
				world.Apply();
				File.WriteAllBytes(Path.Combine(TripData.Artifacts, filename), world.EncodeToPNG());
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
	}

	internal sealed class TripObserver : IStepObserver, IDisposable
	{
		private readonly VesselSimulationRunner _runner;
		private readonly ISimulation _shadow;
		private readonly ControlTrace _trace;
		private readonly TripLeg _leg;
		private readonly HashSet<int> _arrivalIds;
		private readonly VesselState _initial;
		private readonly StreamWriter _states;
		private readonly HashSet<int> _touched = new HashSet<int>();
		private ControlInputs _controls;
		private double _beforeSpeed;
		private long _firstContact = -1;
		private double _firstSpeed, _peak, _compression, _finalForce;
		private double _holdMaxSpeed;
		private int _holdSteps;
		private bool _holdContact = true;
		private bool _bottomedOut, _departureTurning;
		private double _asternBacked, _maxSpeed;
		public readonly StateDigest LiveDigest = new StateDigest();
		public readonly StateDigest ShadowDigest = new StateDigest();
		public readonly BridgePassage[] Passages;
		public bool DepartureCaptured;

		public TripObserver(VesselSimulationRunner runner, ISimulation shadow, ControlTrace trace, TripLeg leg, LandingFace arrival, BridgeGreybox[] bridges)
		{
			_runner = runner;
			_shadow = shadow;
			_trace = trace;
			_leg = leg;
			_initial = runner.State;
			_arrivalIds = new HashSet<int>(arrival.Segments.Select(s => s.Id));
			Passages = TripData.BridgeSpans().Select(span => new BridgePassage(span, bridges.Single(b => b.BridgeName == span.Name))).ToArray();
			_states = new StreamWriter(Path.Combine(leg.Artifacts, "states.ieee754.csv"));
			_states.WriteLine("step,time,north,east,down,roll,pitch,heading,surge,sway,heave,roll_rate,pitch_rate,yaw_rate");
			var initial = TripData.Values(runner.State);
			var headlessInitial = TripData.Values(shadow.State);
			for (var i = 0; i < initial.Length; i++)
				Assert.That(BitConverter.DoubleToInt64Bits(initial[i]), Is.EqualTo(BitConverter.DoubleToInt64Bits(headlessInitial[i])), $"initial state field {i}");
			WriteState(0, initial);
			ObserveBridges(0, runner.State);
		}

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls)
		{
			_controls = controls;
			_beforeSpeed = telemetry.SpeedOverGround;
			Assert.That(controls, Is.EqualTo(_trace.At(step)));
		}

		public void AfterStep(long step, in VesselState state, in VesselTelemetry telemetry)
		{
			_shadow.Step(_controls);
			var live = TripData.Values(state);
			var headless = TripData.Values(_shadow.State);
			for (var i = 0; i < live.Length; i++)
				Assert.That(BitConverter.DoubleToInt64Bits(live[i]), Is.EqualTo(BitConverter.DoubleToInt64Bits(headless[i])), $"state field {i} differs at step {step}");
			LiveDigest.Add(state);
			ShadowDigest.Add(_shadow.State);
			WriteState(step, live);
			var simulation = (VesselDynamicsSimulation)_runner.Driver.Simulation;
			if (simulation.ContactCount > 0 && _firstContact < 0)
			{
				_firstContact = step;
				_firstSpeed = _beforeSpeed;
			}
			_peak = Math.Max(_peak, telemetry.ContactForce);
			_finalForce = telemetry.ContactForce;
			_maxSpeed = Math.Max(_maxSpeed, telemetry.SpeedOverGround);
			_departureTurning |= Math.Abs(Angles.WrapPi(state.Heading - _initial.Heading)) > Angles.ToRadians(10.0);
			if (!_departureTurning && state.Surge < 0)
				_asternBacked = Math.Max(_asternBacked, -(state.North - _initial.North) * Math.Cos(_initial.Heading) - (state.East - _initial.East) * Math.Sin(_initial.Heading));
			for (var i = 0; i < simulation.ContactCount; i++)
			{
				var contact = simulation.GetContact(i);
				_touched.Add(contact.SegmentId);
				_compression = Math.Max(_compression, contact.Compression);
				_bottomedOut |= contact.BottomedOut;
			}
			if (step > _trace.EndStep - 1000)
			{
				_holdSteps++;
				_holdMaxSpeed = Math.Max(_holdMaxSpeed, telemetry.SpeedOverGround);
				_holdContact &= simulation.ContactCount > 0 && telemetry.ContactForce > 0;
				for (var i = 0; i < simulation.ContactCount; i++) _holdContact &= _arrivalIds.Contains(simulation.GetContact(i).SegmentId);
			}
			ObserveBridges(step, state);
		}

		private void ObserveBridges(long step, in VesselState state)
		{
			foreach (var passage in Passages)
			{
				if (!TripData.Inside(new HorizontalVector(state.North, state.East), passage.Span.Polygon)) continue;
				if (passage.SpanStep < 0) passage.SpanStep = step;
				if (passage.MainSpanStep >= 0) continue;
				// The World's polygon puts this point under the published main span; the actual greybox
				// tower inner faces bound the usable gap along its local z axis.
				var bridge = passage.Bridge;
				var relative = Quaternion.Inverse(bridge.transform.localRotation) *
					(new Vector3((float)state.East, 0, (float)state.North) - bridge.transform.localPosition);
				var a = bridge.transform.Find("Tower A");
				var b = bridge.transform.Find("Tower B");
				var margin = _runner.Parameters.Particulars.LengthOverall.Value * 0.5;
				if (relative.z > a.localPosition.z + a.localScale.z * 0.5 + margin &&
					relative.z < b.localPosition.z - b.localScale.z * 0.5 - margin) passage.MainSpanStep = step;
			}
		}

		public void AssertAcceptance(VesselParameters vessel, LandingGeometry landings, ReplayResult replay, string recording)
		{
			var threshold = JObject.Parse(File.ReadAllText(Path.Combine(TripData.Folder, "landing-thresholds.json")));
			Assert.That((double)threshold["firstContactSpeed"]["value"], Is.EqualTo(1.5));
			Assert.That((string)threshold["firstContactSpeed"]["unit"], Is.EqualTo("kn"));
			Assert.That((string)threshold["firstContactSpeed"]["confidence"], Is.EqualTo("crew-reported"));
			Assert.That((string)threshold["firstContactSpeed"]["source"], Is.EqualTo("crew-reported: project crew reviewer, 2026-10-24"));
			Assert.That((string)threshold["peakForceReference"]["confidence"], Is.EqualTo("estimated"));
			Assert.That((double)threshold["peakForceReference"]["speed"], Is.EqualTo(2.0));
			Assert.That((string)threshold["peakForceReference"]["unit"], Is.EqualTo("kn"));
			var source = File.ReadAllBytes(Path.Combine(TripData.Root, "core/dotnet/FerrySim.Core.Tests/Dynamics/ContactScenarios.cs"));
			Assert.That(File.ReadAllBytes(Path.Combine(TripData.Folder, "ContactScenarios.cs")), Is.EqualTo(source), "reference scenario is the same code as the core report");
			var reference = ContactScenarios.BowOnLanding(vessel, ContactScenarios.EstimatedBargeFender,
				Units.KnotsToMetresPerSecond((double)threshold["peakForceReference"]["speed"]));
			var state = _runner.State;
			var summary = new JObject
			{
				["leg"] = _leg.Key, ["duration_s"] = state.Time, ["steps"] = _trace.EndStep,
				["first_contact_step"] = _firstContact, ["first_contact_speed_kn"] = Units.MetresPerSecondToKnots(_firstSpeed),
				["peak_contact_force_n"] = _peak, ["peak_compression_m"] = _compression, ["final_hold_force_n"] = _finalForce,
				["last_10s_max_speed_kn"] = Units.MetresPerSecondToKnots(_holdMaxSpeed), ["last_10s_contact_every_step"] = _holdContact,
				["bridges"] = new JArray(Passages.Select(p => new JObject { ["name"] = p.Span.Name, ["span_step"] = p.SpanStep, ["main_span_step"] = p.MainSpanStep, ["screenshot_captured"] = p.Captured })),
				["contact_faces"] = new JArray(_touched.OrderBy(x => x)),
				["elapsed_since_first_contact_s"] = _firstContact < 0 ? 0 : SimulationClock.TimeAtStep(_trace.EndStep - _firstContact),
				["maximum_speed_kn"] = Units.MetresPerSecondToKnots(_maxSpeed), ["astern_backed_before_turn_m"] = _asternBacked, ["bottomed_out"] = _bottomedOut,
				["force_limit_n"] = reference.PeakForce, ["force_limit_confidence"] = "estimated", ["speed_limit_kn"] = (double)threshold["firstContactSpeed"]["value"], ["speed_limit_confidence"] = "crew-reported",
				["world_geometry_digest"] = landings.Digest, ["state_digest"] = LiveDigest.Text,
				["states_compared_bit_for_bit"] = LiveDigest.Count, ["replay_rows_compared"] = replay.RowsCompared,
				["replay"] = replay.ToString(), ["recording"] = Path.GetFileName(recording), ["inputs"] = Path.GetFileNameWithoutExtension(recording) + ".inputs.csv"
			};
			File.WriteAllText(Path.Combine(_leg.Artifacts, "summary.json"), summary.ToString() + "\n");
			TestContext.WriteLine(summary.ToString());
			AssertOnlyArrivalFaceTouched(_touched, _arrivalIds);
			Assert.That(_firstContact, Is.GreaterThan(0));
			Assert.That(_trace.EndStep - _firstContact, Is.GreaterThanOrEqualTo(6000), "at least 60 s elapsed from the first touching step to the final step, including rebounds");
			Assert.That(_asternBacked, Is.GreaterThan(15.0), "back clear astern before turning more than ten degrees from the departure heading");
			Assert.That(Units.MetresPerSecondToKnots(_firstSpeed), Is.LessThanOrEqualTo((double)threshold["firstContactSpeed"]["value"]));
			Assert.That(_peak, Is.GreaterThan(0).And.LessThanOrEqualTo(reference.PeakForce));
			Assert.That(_bottomedOut, Is.False, "no fender bottoms out");
			Assert.That(_holdSteps, Is.EqualTo(1000), "last ten seconds sampled at 100 Hz");
			Assert.That(Units.MetresPerSecondToKnots(_holdMaxSpeed), Is.LessThan(0.1), "at rest throughout last ten seconds");
			Assert.That(_holdContact, Is.True, "held on only the arrival face at every step throughout last ten seconds");
			Assert.That(_finalForce, Is.GreaterThan(0));
			var normal = TripData.Normal(landings.PrimaryFace(_leg.ArrivalStop));
			Assert.That(Math.Abs(Angles.ToDegrees(Angles.WrapPi(state.Heading - Math.Atan2(-normal.East, -normal.North)))),
				Is.LessThan(5.0), "held bow-in, within five degrees of the face normal");
			Assert.That(FaceClearance.Check(vessel, state.North, state.East, state.Heading,
				landings.PrimaryFace(_leg.ArrivalStop).Segments.Select(s => s.Segment).ToArray()).Overlaps, Is.True);
			if (_leg.RequiredBridges.Length == 0)
			{
				Assert.That(Passages.All(p => p.SpanStep < 0), Is.True, "direct Pier 11 to DUMBO track must never enter any World's bridge span");
				Assert.That(state.Time, Is.LessThan(360.0), "direct crossing, landing and at least 60 s elapsed since first contact must finish under six minutes");
			}
			else
			{
				long previous = 0;
				foreach (var name in _leg.RequiredBridges)
				{
					var passage = Passages.Single(p => p.Span.Name == name);
					Assert.That(passage.MainSpanStep, Is.GreaterThan(previous), "track under " + name + " main-span polygon between the actual greybox towers, in route order");
					Assert.That(passage.Captured, Is.True, name + " screenshot");
					previous = passage.MainSpanStep;
				}
			}
		}

		internal static void AssertOnlyArrivalFaceTouched(HashSet<int> touched, HashSet<int> arrivalIds)
		{
			Assert.That(touched, Is.Not.Empty, "arrival primary face must be touched");
			Assert.That(touched.IsSubsetOf(arrivalIds), Is.True, "only the arrival primary face touched anywhere in the trip, including departure");
		}

		private void WriteState(long step, double[] values) => _states.WriteLine(step + "," + string.Join(",",
			values.Select(v => unchecked((ulong)BitConverter.DoubleToInt64Bits(v)).ToString("x16", CultureInfo.InvariantCulture))));

		public void Dispose() => _states.Dispose();
	}

	internal sealed class BridgePassage
	{
		public BridgePassage(BridgeSpan span, BridgeGreybox bridge) { Span = span; Bridge = bridge; }
		public BridgeSpan Span { get; }
		public BridgeGreybox Bridge { get; }
		public long SpanStep = -1, MainSpanStep = -1;
		public bool Captured;
	}
}
