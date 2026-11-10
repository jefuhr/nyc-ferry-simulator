using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Harbor.Player.Journeys;
using FerrySim.Slice;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.Profiling;

namespace FerrySim.Harbor.Tests.Landings
{
	/// <summary>Failure modes: bed friction or a different face impersonates a hold, arrival backs out, and observer logging allocates.</summary>
	public class LandingHoldTests
	{
		private VesselParameters _vessel;
		[OneTimeSetUp]
		public void LoadVessel() => _vessel = VesselParametersJson.Load(Path.Combine(LandingVideoScenarioTests.Root, "vessels/t-class.json"));

		private HarborJourney Route(int intended = 41) => new HarborJourney
		{
			Points = new[] { new HorizontalVector(60, 0) }, SpanIds = Array.Empty<string>(), SpanRings = Array.Empty<HorizontalVector[]>(),
			FaceIds = new[] { intended }, FaceMiddle = new HorizontalVector(100, 0), FaceNormal = new HorizontalVector(-1, 0), ForceLimit = 414000
		};

		private VesselDynamicsSimulation OnFender()
		{
			var bow = FaceClearance.BodyOutline(_vessel).Max(p => p.North);
			var face = new ContactSegment(new HorizontalVector(bow + .001, 100), new HorizontalVector(bow + .001, -100), new ContactSurface(1e6, 5e4, .3, .25), 41);
			var sim = new VesselDynamicsSimulation(_vessel, new FlatWater(30), new SegmentListGeometry(new[] { face }), VesselState.Planar(0, 0, 0, 0, .02));
			for (var step = 0; step < 100 && (sim.ContactCount == 0 || sim.GetContact(0).NormalForce <= 0); step++) sim.Step(ControlInputs.Neutral);
			Assert.That(sim.ContactCount, Is.GreaterThan(0), "fixture must touch an actual segment");
			Assert.That(sim.GetContact(0).NormalForce, Is.GreaterThan(0));
			Assert.That(sim.Telemetry.ContactForce, Is.GreaterThan(0));
			Assert.That(sim.Telemetry.SpeedOverGround, Is.LessThan(Units.KnotsToMetresPerSecond(.1)));
			return sim;
		}

		private static void Window(HarborJourneyObserver observer, VesselDynamicsSimulation sim)
		{
			// Repeat one real contact sample to isolate the judgment from contact-model motion.
			for (var step = 1; step <= 1000; step++)
			{
				observer.BeforeStep(step - 1, sim.State, sim.Telemetry, ControlInputs.Neutral);
				observer.AfterStep(step, sim.State, sim.Telemetry);
			}
		}

		[Test]
		public void NonIntendedSegmentWithNonzeroTelemetryForceIsNotHolding()
		{
			var sim = OnFender(); var observer = new HarborJourneyObserver(Route(99), sim);
			Window(observer, sim);
			Assert.That(observer.FinalHoldOkay, Is.False, "nonzero contact force from segment 41 cannot hold on intended face 99");
		}

		[Test]
		public void IntendedFenderIsHolding()
		{
			var sim = OnFender(); var observer = new HarborJourneyObserver(Route(), sim);
			Window(observer, sim);
			Assert.That(observer.FinalHoldOkay, Is.True);
		}

		[Test]
		public void GroundedBoatWithBedFrictionIsNotHolding()
		{
			var sim = new VesselDynamicsSimulation(_vessel, new FlatWater(.5), EmptyWorldGeometry.Instance, VesselState.Planar(0, 0, 0, 0, .01));
			Assert.That(sim.Grounding.Grounded, Is.True);
			Assert.That(sim.Telemetry.ContactForce, Is.GreaterThan(0), "bed friction is the regression's misleading force");
			Assert.That(sim.ContactCount, Is.Zero);
			var observer = new HarborJourneyObserver(Route(), sim); Window(observer, sim);
			Assert.That(observer.FinalHoldOkay, Is.False, "grounding cannot count as a landing hold");
		}

		private static JObject ApproachRoute() => JObject.Parse(@"{
			'name':'synthetic-arrival', 'departure_stop':'test', 'arrival_stop':'test', 'date':'2025-07-14', 'minute':0,
			'arrival_blocked':false, 'points':[[60,0]], 'required_spans':[],
			'arrival_face':{'middle':[100,0], 'normal':[-1,0], 'segment_ids':[41]},
			'start':{'mode':'approach-only','position_ne_m':[0,0],'heading_rad':0,
				'corridor_ne_m':[[-20,-20],[80,-20],[80,20],[-20,20],[-20,-20]]}
		}");

		[Test]
		public void ApproachOnlyStartsFromGivenPoseWithoutBackOut()
		{
			var route = HarborJourney.Parse(ApproachRoute()); var state = VesselState.Planar(0, 0, 0, 0);
			var controls = new HarborJourneyControls(route, () => state, () => false, _vessel.Particulars.LengthOverall.Value);
			var input = controls.ControlsForStep(0);
			Assert.That(controls.Phase, Is.Zero, "arrival must begin on its first approach leg");
			Assert.That(input.PortLever, Is.GreaterThan(0)); Assert.That(input.StarboardLever, Is.GreaterThan(0));
		}

		[Test]
		public void ApproachOnlyRejectsWaypointsOutsideDeclaredCorridor()
		{
			var data = ApproachRoute(); data["points"][0] = new JArray(60, 50);
			Assert.Throws<FormatException>(() => HarborJourney.Parse(data));
		}

		[Test]
		public void HoldLogRetainsExactlyOneRowPerStepOfFinalWindow()
		{
			var sim = OnFender(); var observer = new HarborJourneyObserver(Route(), sim);
			Window(observer, sim);
			var method = typeof(HarborJourneyObserver).GetMethod("WriteHoldCsv");
			Assert.That(method, Is.Not.Null, "observer must export its preallocated 1000-step hold window");
			using (var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
			{
				method.Invoke(observer, new object[] { output });
				var lines = output.ToString().TrimEnd().Split('\n');
				Assert.That(lines.Length, Is.EqualTo(1001), "header plus 1000 physics steps");
				Assert.That(lines[1].Split(',')[0], Is.EqualTo("1")); Assert.That(lines[1000].Split(',')[0], Is.EqualTo("1000"));
				Assert.That(lines[0], Does.Contain("intended_face_contact").And.Contain("heading_error_rad").And.Contain("side_force_n").And.Contain("port_gear"));
			}
		}


		[Test]
		public void HoldLogRollsOverAndSummaryMatchesTheMeasuredRows()
		{
			var sim = OnFender(); var observer = new HarborJourneyObserver(Route(), sim); Window(observer, sim);
			observer.AfterStep(1001, sim.State, sim.Telemetry);
			using (var output = new StringWriter(System.Globalization.CultureInfo.GetCultureInfo("fr-FR")))
			{
				observer.WriteHoldCsv(output);
				var lines = output.ToString().TrimEnd().Split('\n');
				Assert.That(lines.Length, Is.EqualTo(1001));
				Assert.That(lines[1].Split(',')[0], Is.EqualTo("2")); Assert.That(lines[1000].Split(',')[0], Is.EqualTo("1001"));
				Assert.That(lines.All(line => line.TrimEnd().Split(',').Length == 18), Is.True);
				Assert.That(lines.Skip(1).All(line => line.Split(',')[13] == "1" && line.Split(',')[14] == "0"), Is.True);
			}
			var summary = new JObject(); observer.AddHoldSummary(summary);
			Assert.That((int)summary["hold_window_steps"], Is.EqualTo(1000));
			Assert.That((int)summary["hold_window_intended_face_steps"], Is.EqualTo(1000));
			Assert.That((int)summary["hold_window_grounded_steps"], Is.Zero);
			Assert.That((int)summary["hold_window_bad_steps"], Is.Zero);
			Assert.That((double)summary["hold_window_port_engine_rad_s_mean"], Is.EqualTo(sim.Telemetry.Port.EngineSpeed).Within(1e-9));
		}

		[Test]
		public void HoldNeedsACompleteWindowAndAnOldBadStepMustAgeOut()
		{
			var sim = OnFender(); var observer = new HarborJourneyObserver(Route(), sim);
			var t = sim.Telemetry;
			var tooFast = new VesselTelemetry(t.Port, t.Starboard, t.PortRudderAngle, t.StarboardRudderAngle, t.SpeedThroughWater,
				Units.KnotsToMetresPerSecond(.1), t.CourseOverGround, t.DepthUnderKeel, t.ContactForce);
			observer.AfterStep(1, sim.State, tooFast);
			for (var step = 2; step <= 999; step++) observer.AfterStep(step, sim.State, t);
			Assert.That(observer.FinalHoldOkay, Is.False, "999 steps cannot qualify");
			observer.AfterStep(1000, sim.State, t);
			Assert.That(observer.FinalHoldOkay, Is.False, "the boundary-speed step is still in the window");
			observer.AfterStep(1001, sim.State, t);
			Assert.That(observer.FinalHoldOkay, Is.True);
		}

		[TestCase(true), TestCase(false)]
		public void AfterStepHasCalibratedZeroAllocation(bool intended)
		{
			var sim = OnFender(); var route = Route(intended ? 41 : 99);
			Window(new HarborJourneyObserver(route, sim), sim);
			var observer = new HarborJourneyObserver(route, sim);
			var chars = new char[16]; var recorder = Recorder.Get("GC.Alloc"); recorder.enabled = false; recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled = true; var control = new string(chars); recorder.enabled = false;
				var calibration = recorder.sampleBlockCount; GC.KeepAlive(control); Assert.That(calibration, Is.EqualTo(1));
				recorder.enabled = true;
				for (var step = 1001; step <= 2000; step++) observer.AfterStep(step, sim.State, sim.Telemetry);
				recorder.enabled = false; var samples = recorder.sampleBlockCount;
				TestContext.WriteLine("positive control=" + calibration + "; journey AfterStep GC.Alloc samples=" + samples);
				Assert.That(samples, Is.Zero);
			}
			finally { recorder.enabled = false; recorder.CollectFromAllThreads(); }
		}

		private sealed class FlatWater : IEnvironment
		{
			private readonly double _depth;
			public FlatWater(double depth) { _depth = depth; }
			public double SampleDepth(double n, double e, double t) => _depth;
			public HorizontalVector SampleCurrent(double n, double e, double t) => default;
			public HorizontalVector SampleWind(double n, double e, double t) => default;
		}
	}
}
