using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The runner on an environment pack's day, and its environment readout (<see cref="IEnvironmentReadoutSource"/>).
	/// Failure modes guarded: the simulation not getting the pack (depth under the keel still from 50 m of calm water); a
	/// readout that reports another time or place than the vessel's last step, a rate with the wrong sign or size, the
	/// wrong local offset, or the pack's coverage counts polluted by its own sampling; time holds and depth misses not
	/// counted, or carried into the next run; data shown before the first step or after the run ends; calm and uniform runs
	/// reporting a tide; a scenario that cannot start ending the run that is going; the drawn tide's hook firing after the
	/// placement; and garbage per step.
	/// </summary>
	public class PackRunTests
	{
		private static readonly VesselParameters Vessel = TestSupport.LoadTClass();

		private SyntheticPacks _packs;
		private GameObject _host;

		[SetUp]
		public void SetUp() => _packs = new SyntheticPacks();

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
			_host = new GameObject("pack run runner (test)");
			var runner = _host.AddComponent<VesselSimulationRunner>();
			runner.RecordRuns = false;
			runner.EnvironmentPacks = _packs.Packs;
			return runner;
		}

		/// <summary>At rest at the world position (north, east), heading north.</summary>
		private static ScenarioSettings At(double north, double east, string startUtc, string pack = null)
		{
			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new ScenarioSettings
			{
				name = "pack run",
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				scenarioStartUtc = startUtc,
				environment = new ScenarioEnvironment { pack = pack ?? SyntheticPacks.Name(SyntheticPacks.SummerDate) },
			};
		}

		[Test]
		public void APackRun_SimulatesTheDaysDepthCurrentAndWind_AndReadsThemOutAtTheVessel()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var runner = Runner();
			runner.Initialize(At(0.0, 0.0, "2025-07-14T16:00:00Z"), new ConstantControls(ControlInputs.Neutral), Vessel);

			Assert.That(runner.HasEnvironment, Is.False, "no step yet");
			for (var frame = 0; frame < 50; frame++)
			{
				runner.AdvanceFrame(0.02);
			}

			var time = runner.Driver.SimulationTime;
			var state = runner.State;
			var packTime = 43200.0 + time;
			var readout = runner.Environment;
			Assert.That(runner.HasEnvironment, Is.True);
			Assert.That(readout.Kind, Is.EqualTo(EnvironmentKind.Pack));
			Assert.That(readout.PackName, Is.EqualTo("test-harbor/2025-07-14"));
			Assert.That(readout.PackDigest, Is.EqualTo(File.ReadAllText(Path.Combine(_packs.Root, "test-harbor", "env", "days", "2025-07-14", "DIGEST")).Trim()));
			Assert.That(readout.LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-4.0)));
			Assert.That(readout.WaterLevelNavd88, Is.EqualTo(SyntheticPacks.Level(packTime)).Within(1e-12), "the level at the last step's time");
			Assert.That(readout.WaterLevelRate, Is.EqualTo(SyntheticPacks.LevelRate).Within(1e-12), "rising, in m/s");
			Assert.That(readout.Current, Is.EqualTo(SyntheticPacks.Current));
			Assert.That(readout.Wind, Is.EqualTo(SyntheticPacks.Wind));
			Assert.That(readout.WindMissing, Is.False);
			Assert.That(readout.TimeHolds, Is.Zero);
			Assert.That(readout.DepthMisses, Is.Zero);
			var depth = SyntheticPacks.Level(packTime) - SyntheticPacks.Bed;
			Assert.That(runner.Telemetry.DepthUnderKeel, Is.EqualTo(depth - Vessel.Particulars.MaxDraft.Value).Within(1e-6), "the depth alarm runs on the pack's depth");
			Assert.That(state.GroundVelocity.North, Is.EqualTo(SyntheticPacks.Current.North).Within(0.05), "at rest in the water, the hull goes with the current (and a little wind)");

			var coverage = runner.RunEnvironment.PackRun.Coverage;
			Assert.That(coverage.Samples, Is.GreaterThan(0));
			var before = coverage.Samples;
			_ = runner.Environment;
			runner.ApplyRenderPose();
			Assert.That(runner.RunEnvironment.PackRun.Coverage.Samples, Is.EqualTo(before), "reading out never samples the run's environment");
		}

		[Test]
		public void TheRate_FollowsTheTide_AndAWinterDayIsFiveHoursBehindUtc()
		{
			_packs.Write(SyntheticPacks.WinterDate, withWind: false);
			var runner = Runner();
			runner.Initialize(At(0.0, 0.0, "2025-01-14T17:00:00Z", SyntheticPacks.Name(SyntheticPacks.WinterDate)), new ConstantControls(ControlInputs.Neutral), Vessel);
			runner.AdvanceFrame(0.1);

			var readout = runner.Environment;
			Assert.That(readout.LocalUtcOffset, Is.EqualTo(TimeSpan.FromHours(-5.0)));
			Assert.That(readout.WindMissing, Is.True);
			Assert.That(readout.Wind, Is.EqualTo(HorizontalVector.Zero), "still air");
			Assert.That(readout.WaterLevelNavd88, Is.EqualTo(SyntheticPacks.Level(12.0 * 3600.0 + runner.Driver.SimulationTime)).Within(1e-12));
		}

		[Test]
		public void TimeHoldsAndDepthMisses_AreCountedForTheRun_AndStartAgainAtZero()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var runner = Runner();
			// 1 s before the coverage ends, outside the 2 km grid.
			runner.Initialize(At(1500.0, 0.0, "2025-07-15T04:59:59Z"), new ConstantControls(ControlInputs.Neutral), Vessel);
			for (var frame = 0; frame < 25; frame++)
			{
				runner.AdvanceFrame(0.1);
			}

			var readout = runner.Environment;
			Assert.That(readout.TimeHolds, Is.GreaterThan(0), "samples after the coverage end are held");
			Assert.That(readout.DepthMisses, Is.EqualTo(runner.Driver.StepCount + 1), "one depth sample at the start and one per step, every one off the grid");
			Assert.That(runner.Telemetry.DepthUnderKeel, Is.EqualTo(20.0 - Vessel.Particulars.MaxDraft.Value).Within(1e-9), "the pack's outside depth");
			Assert.That(readout.WaterLevelNavd88, Is.EqualTo(SyntheticPacks.Level(SyntheticPacks.CoverageEnd)).Within(1e-12), "held at the coverage end");

			runner.ResetRun();
			runner.AdvanceFrame(0.005);
			Assert.That(runner.Driver.StepCount, Is.Zero);
			Assert.That(runner.HasEnvironment, Is.False, "a new run has no step yet");
			Assert.That(runner.Environment.TimeHolds, Is.Zero, "a new run counts only its own samples");
			Assert.That(runner.Environment.DepthMisses, Is.EqualTo(1), "only the new run's start sample");
		}

		[Test]
		public void AfterTheRunEnds_ThereIsNoEnvironmentToShow()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var runner = Runner();
			runner.Initialize(At(0.0, 0.0, "2025-07-14T16:00:00Z"), new ConstantControls(ControlInputs.Neutral), Vessel);
			runner.AdvanceFrame(0.1);
			Assert.That(runner.HasEnvironment, Is.True);

			runner.EndRun();

			Assert.That(runner.HasEnvironment, Is.False);
		}

		[Test]
		public void CalmAndUniformRuns_ReportTheirKindAndNoTide()
		{
			var runner = Runner();
			var uniform = new ScenarioSettings
			{
				environment = new ScenarioEnvironment { calm = false, currentSpeedKnots = 2.0, currentTowardDegrees = 90.0, windSpeedKnots = 10.0, windFromDegrees = 0.0, depthMetres = 9.0 },
			};

			runner.Initialize(new ScenarioSettings(), new ConstantControls(ControlInputs.Neutral), Vessel);
			runner.AdvanceFrame(0.1);
			var calm = runner.Environment;
			runner.Initialize(uniform, new ConstantControls(ControlInputs.Neutral), Vessel);
			runner.AdvanceFrame(0.1);
			var readout = runner.Environment;

			Assert.That(calm.Kind, Is.EqualTo(EnvironmentKind.Calm));
			Assert.That(double.IsNaN(calm.WaterLevelNavd88) && double.IsNaN(calm.WaterLevelRate), Is.True);
			Assert.That(calm.Current, Is.EqualTo(HorizontalVector.Zero));
			Assert.That(readout.Kind, Is.EqualTo(EnvironmentKind.Uniform));
			Assert.That(double.IsNaN(readout.WaterLevelNavd88), Is.True);
			Assert.That(readout.Current, Is.EqualTo(uniform.environment.Current));
			Assert.That(readout.Wind, Is.EqualTo(uniform.environment.Wind));
			Assert.That(readout.PackName, Is.Empty);
			Assert.That(readout.PackDigest, Is.Empty);
			Assert.That(readout.LocalUtcOffset, Is.EqualTo(TimeSpan.Zero));
			Assert.That(readout.TimeHolds + readout.DepthMisses, Is.Zero);
			Assert.That(runner.Telemetry.DepthUnderKeel, Is.EqualTo(9.0 - Vessel.Particulars.MaxDraft.Value).Within(1e-9));
		}

		[Test]
		public void AScenarioThatCannotStart_LeavesTheCurrentRunGoing()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var runner = Runner();
			runner.Initialize(At(0.0, 0.0, "2025-07-14T16:00:00Z"), new ConstantControls(ControlInputs.Neutral), Vessel);
			runner.AdvanceFrame(0.1);
			var run = runner.RunId;
			var ended = 0;
			runner.RunEnded += () => ended++;

			Assert.Throws<ArgumentException>(() => runner.Initialize(At(0.0, 0.0, "2025-07-14T16:00:00Z", SyntheticPacks.Name("2025-07-16")), null, Vessel));
			Assert.Throws<FileNotFoundException>(() => runner.Initialize(new ScenarioSettings { vesselFile = "vessels/missing.json" }));

			Assert.That(runner.RunId, Is.EqualTo(run));
			Assert.That(runner.IsRunActive, Is.True);
			Assert.That(ended, Is.Zero);
			Assert.That(runner.AdvanceFrame(0.1).Steps, Is.EqualTo(10), "the run goes on");
		}

		[Test]
		public void BeforePlacement_IsRaisedJustBeforeThePlacementReadsTheWater_AtStartAndEveryFrame()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var runner = Runner();
			var raised = 0;
			long stepsAtLastRaise = -1;
			runner.BeforePlacement += () =>
			{
				raised++;
				stepsAtLastRaise = runner.Driver != null ? runner.Driver.StepCount : -1;
			};

			runner.Initialize(At(0.0, 0.0, "2025-07-14T16:00:00Z"), new ConstantControls(ControlInputs.Neutral), Vessel);
			Assert.That(raised, Is.EqualTo(1), "the start's placement");
			runner.AdvanceFrame(0.05);
			runner.ApplyRenderPose();
			Assert.That(raised, Is.EqualTo(2));
			Assert.That(stepsAtLastRaise, Is.EqualTo(5), "after the frame's steps");
			runner.Pause();
			runner.ApplyRenderPose();
			Assert.That(raised, Is.EqualTo(3), "also while paused");
		}

		[Test]
		public void SteppingAndReadingOutAPackRun_AllocatesNothing()
		{
			_packs.Write(SyntheticPacks.SummerDate);
			var runner = Runner();
			runner.Initialize(At(0.0, 0.0, "2025-07-14T16:00:00Z"), new ConstantControls(new ControlInputs(0.3, 0.3, 0.2)), Vessel);
			runner.BeforePlacement += () => _ = runner.Environment.WaterLevelNavd88;
			for (var frame = 0; frame < 20; frame++)
			{
				runner.AdvanceFrame(1.0 / 60.0);
				runner.ApplyRenderPose();
			}

			Assert.That(() =>
			{
				runner.AdvanceFrame(1.0 / 60.0);
				runner.ApplyRenderPose();
				_ = runner.Environment;
			}, Is.Not.AllocatingGCMemory());
		}
	}
}
