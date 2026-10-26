using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Traffic.Tests
{
	/// <summary>
	/// Traffic through the runner. Failure modes guarded: the monitor on another clock than the run (pack epoch against run
	/// time), a step it misses, or a reset that keeps the last run's alarms; traffic that pushes the learner's vessel; the
	/// automatic leave-out taking the wrong vessel; garbage per step with traffic; the readout describing a step other than the
	/// latest.
	/// </summary>
	public class TrafficRunTests
	{
		private TrafficTestPacks _packs;
		private GameObject _host;

		[SetUp]
		public void SetUp() => _packs = new TrafficTestPacks();

		[TearDown]
		public void TearDown()
		{
			if (_host != null)
			{
				Object.DestroyImmediate(_host);
			}

			_packs.Dispose();
		}

		private VesselSimulationRunner Runner()
		{
			_host = new GameObject("traffic runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = false;
			runner.EnvironmentPacks = _packs.EnvironmentPacks;
			runner.TrafficPacks = _packs.TrafficPacks;
			runner.MaxStepsPerFrame = 100;
			return runner;
		}

		/// <summary>Ahead on both engines: the T-class gathers way to the north from rest.</summary>
		private static IControlInputSource Ahead() => new ConstantControls(new ControlInputs(0.6, 0.6, 0.0));

		/// <summary>A tug 900 m north of the start heading south at 4 m/s from pack time 43100: head-on with the learner.</summary>
		private static TrafficTestPacks.Track HeadOn() => TrafficTestPacks.Track.Straight(366000001, 43100.0, 1300.0, 0.0, 4.0, Math.PI, 12);

		[Test]
		public void TheMonitor_FollowsTheRunsClock_AndItsAlarmComesBeforeTheCollision()
		{
			_packs.WriteTraffic(TrafficTestPacks.Date, HeadOn());
			var runner = Runner();
			runner.Initialize(TrafficTestPacks.Scenario(), Ahead());
			var monitor = runner.TrafficMonitor;
			Assert.That(monitor, Is.Not.Null);
			Assert.That(runner.Traffic.EpochOffset, Is.EqualTo(TrafficTestPacks.StartPackTime), "16:00 UTC is 43200 s after the EDT epoch");
			Assert.That(runner.TrafficEnabled, Is.True);
			Assert.That(runner.HasTraffic, Is.False, "nothing to read before the first step");
			var log = new List<TrafficEvent>();
			var sink = new Sink(log);
			var steps = new StepCheck(runner);
			runner.Driver.AddObserver(steps);
			// The runner hands its monitor's events to its recorder or replay only; a second monitor over the same steps sees the same.
			var copy = new TrafficStepObserver(runner.Traffic.StartRun(HullOutlineFor(runner), runner.TrafficLimits)) { Verifier = sink };
			runner.Driver.AddObserver(copy);
			for (var frame = 0; frame < 150 && monitor.CollisionsStarted == 0; frame++)
			{
				runner.AdvanceFrame(1.0);
				Assert.That(monitor.LastStep, Is.EqualTo(runner.Driver.StepCount), "the monitor saw the latest step");
				Assert.That(runner.HasTraffic, Is.True);
			}

			Assert.That(steps.Mismatches, Is.Zero, "pack time is run time plus 43200 s at every step");
			Assert.That(monitor.AlarmsRaised, Is.EqualTo(1));
			Assert.That(monitor.CollisionsStarted, Is.EqualTo(1));
			Assert.That(log.ConvertAll(e => e.Kind).GetRange(0, 2), Is.EqualTo(new[] { TrafficEventKind.AlarmRaised, TrafficEventKind.CollisionStarted }));
			Assert.That(log[1].Time - log[0].Time, Is.GreaterThan(30.0), "the alarm well before the collision");
			Assert.That(runner.TrafficAlarm.State, Is.EqualTo(TrafficAlarmState.Collision));
			Assert.That(runner.TrafficAlarm.Target.Mmsi, Is.EqualTo(366000001));
			Assert.That(copy.Monitor.Digest, Is.EqualTo(monitor.Digest), "the copy saw what the runner's monitor saw");
		}

		[Test]
		public void Traffic_NeverPushesTheVessel()
		{
			_packs.WriteTraffic(TrafficTestPacks.Date, HeadOn());
			var with = Runner();
			with.Initialize(TrafficTestPacks.Scenario(), Ahead());
			var withStates = new States();
			with.Driver.AddObserver(withStates);
			for (var frame = 0; frame < 120; frame++)
			{
				with.AdvanceFrame(1.0);
			}

			Assert.That(with.TrafficMonitor.CollisionsStarted, Is.EqualTo(1), "it did collide");
			Object.DestroyImmediate(_host);
			var without = Runner();
			without.Initialize(TrafficTestPacks.Scenario(trafficDay: string.Empty), Ahead());
			var withoutStates = new States();
			without.Driver.AddObserver(withoutStates);
			for (var frame = 0; frame < 120; frame++)
			{
				without.AdvanceFrame(1.0);
			}

			Assert.That(without.Traffic, Is.Null);
			Assert.That(withStates.Digest.Text, Is.EqualTo(withoutStates.Digest.Text), "every state bit for bit the same with and without the collision");
			Assert.That(withStates.Digest.Count, Is.EqualTo(12000));
		}

		[Test]
		public void TheAutomaticLeaveOut_TakesTheFerryAtTheStart()
		{
			var near = TrafficTestPacks.Track.Straight(367000001, 43140.0, 30.0, 20.0, 0.0, 0.0, 5);
			near.Ferry = true;
			near.Class = TrafficClass.FerryPassenger;
			var nearer = TrafficTestPacks.Track.Straight(367000002, 43140.0, 200.0, 0.0, 0.0, 0.0, 5);
			nearer.Class = TrafficClass.FerryPassenger;
			var far = TrafficTestPacks.Track.Straight(367000003, 43140.0, 400.0, 0.0, 0.0, 0.0, 5);
			far.Ferry = true;
			_packs.WriteTraffic(TrafficTestPacks.Date, near, nearer, far);
			var runner = Runner();
			runner.Initialize(TrafficTestPacks.Scenario(leaveOut: ScenarioTraffic.Automatic), new ConstantControls(ControlInputs.Neutral));
			Assert.That(runner.Traffic.Removed.Mmsi, Is.EqualTo(367000001), "the candidate 36 m away; the nearer non-candidate and the far candidate stay");
			Assert.That(runner.Traffic.OwnFerry.Distance, Is.EqualTo(Math.Sqrt(30 * 30 + 20 * 20)).Within(1e-6));
			runner.AdvanceFrame(1.0);
			Assert.That(runner.Traffic.IsShown(runner.Traffic.Removed.Index, runner.Driver.SimulationTime), Is.False);
			Assert.That(runner.Traffic.IsShown(1, runner.Driver.SimulationTime), Is.True);
		}

		[Test]
		public void AReset_StartsTheTrafficOver()
		{
			_packs.WriteTraffic(TrafficTestPacks.Date, HeadOn());
			var runner = Runner();
			runner.Initialize(TrafficTestPacks.Scenario(), Ahead());
			for (var frame = 0; frame < 120; frame++)
			{
				runner.AdvanceFrame(1.0);
			}

			var first = runner.TrafficMonitor;
			Assert.That(first.AlarmsRaised, Is.EqualTo(1));
			runner.ResetRun();
			Assert.That(runner.TrafficMonitor, Is.Not.SameAs(first), "a fresh monitor");
			Assert.That(runner.TrafficMonitor.AlarmsRaised, Is.Zero);
			Assert.That(runner.TrafficMonitor.LastStep, Is.EqualTo(-1));
			for (var frame = 0; frame < 120 && runner.TrafficMonitor.AlarmsRaised == 0; frame++)
			{
				runner.AdvanceFrame(1.0);
			}

			Assert.That(runner.TrafficMonitor.AlarmsRaised, Is.EqualTo(1), "and the new run raises its own alarm");
			Assert.That(first.LastStep, Is.EqualTo(12000), "the old one stopped at its run's end");
		}

		[Test]
		public void ARunWithTraffic_MakesNoGarbagePerStep()
		{
			var tracks = new List<TrafficTestPacks.Track> { HeadOn() };
			for (var i = 0; i < 40; i++)
			{
				tracks.Add(TrafficTestPacks.Track.Straight(368000000 + i, 42000.0 + 13 * i, -1500.0 + 70 * i, 800.0 - 37 * i, 2.0 + 0.1 * i, 0.15 * i, 80));
			}

			_packs.WriteTraffic(TrafficTestPacks.Date, tracks.ToArray());
			var runner = Runner();
			runner.Initialize(TrafficTestPacks.Scenario(), Ahead());
			for (var frame = 0; frame < 20; frame++)
			{
				runner.AdvanceFrame(1.0 / 60.0);
				_ = runner.TrafficAlarm;
			}

			void Frame()
			{
				runner.AdvanceFrame(1.0 / 60.0);
				_ = runner.TrafficAlarm;
				_ = runner.HasTraffic;
			}

			Assert.That(Frame, Is.Not.AllocatingGCMemory(), "stepping with 41 vessels about, readout included");
		}

		private static HullOutline HullOutlineFor(VesselSimulationRunner runner) => HullOutline.FromContact(runner.Parameters);

		private sealed class Sink : ITrafficEventSink
		{
			private readonly List<TrafficEvent> _log;

			public Sink(List<TrafficEvent> log) => _log = log;

			public void OnTrafficEvent(in TrafficEvent trafficEvent) => _log.Add(trafficEvent);
		}

		/// <summary>Checks, after every step, that the runner's traffic reads the pack at run time plus the epoch offset.</summary>
		private sealed class StepCheck : IStepObserver
		{
			private readonly VesselSimulationRunner _runner;

			public StepCheck(VesselSimulationRunner runner) => _runner = runner;

			public int Mismatches { get; private set; }

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry)
			{
				if (_runner.Traffic.PackTime(state.Time) != state.Time + TrafficTestPacks.StartPackTime)
				{
					Mismatches++;
				}
			}
		}

		private sealed class States : IStepObserver
		{
			public readonly StateDigest Digest = new StateDigest();

			public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { }

			public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry) => Digest.Add(state);
		}
	}
}
