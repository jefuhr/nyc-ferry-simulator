using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The fixed-step accumulator. Failure modes guarded: steps of the wrong size or count (Unity's 0.02 s
	/// FixedUpdate, float drift); physics that depends on the frame rate; a spiral of death on slow frames, or
	/// dropped time that is silent or caught up later in a burst; an interpolation factor outside [0, 1]; zero,
	/// negative or non-finite frame times stepping; controls requested out of order or twice per step; stale
	/// clock or history after a restart; −0.0 reaching the physics where a replay would read +0.0.
	/// </summary>
	public class FixedStepDriverTests
	{
		private static readonly VesselParameters Vessel = TestSupport.LoadTClass();

		private static ISimulation NewSimulation() =>
			new VesselDynamicsFactory().Create(Vessel, UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.3));

		/// <summary>Both ahead, then split levers with helm, so every degree of freedom is exercised.</summary>
		private static ControlTrace ManoeuvringTrace()
		{
			var trace = new ControlTrace();
			trace.Append(0, new ControlInputs(1.0, 1.0, 0.0));
			trace.Append(800, new ControlInputs(0.6, -0.4, 0.35));
			trace.Append(1400, new ControlInputs(-0.2, 0.8, -1.0));
			return trace;
		}

		[Test]
		public void Advance_TakesWholeStepsAndCarriesTheRemainder()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(ControlInputs.Neutral));

			var first = driver.Advance(0.025);
			Assert.That(first.Steps, Is.EqualTo(2));
			Assert.That(driver.Accumulator, Is.EqualTo(0.005).Within(1e-12));
			Assert.That(driver.InterpolationFactor, Is.EqualTo(0.5).Within(1e-9));

			var second = driver.Advance(0.006);
			Assert.That(second.Steps, Is.EqualTo(1));
			Assert.That(driver.StepCount, Is.EqualTo(3));
			Assert.That(driver.Accumulator, Is.EqualTo(0.001).Within(1e-12));

			var third = driver.Advance(0.004);
			Assert.That(third.Steps, Is.EqualTo(0), "half a step owed: nothing runs yet");
			Assert.That(driver.InterpolationFactor, Is.EqualTo(0.5).Within(1e-9));
		}

		[Test]
		public void SimulationTime_IsTheStepCountOverOneHundred()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(new ControlInputs(1.0, 1.0, 0.0)));
			for (var frame = 0; frame < 500; frame++)
			{
				driver.Advance(1.0 / 60.0);
			}

			Assert.That(driver.StepCount, Is.InRange(832L, 834L), "500 frames at 60 fps are 8.33 s");
			Assert.That(driver.SimulationTime, Is.EqualTo(SimulationClock.TimeAtStep(driver.StepCount)));
			Assert.That(driver.CurrentState.Time, Is.EqualTo(SimulationClock.TimeAtStep(driver.StepCount)), "the core's clock and the driver's agree exactly");
		}

		[Test]
		public void FrameRates30And144_GiveIdenticalStatesAtEveryStep()
		{
			var at30 = RunAt(30.0, 2000);
			var at144 = RunAt(144.0, 2000);

			Assert.That(at30.States.Count, Is.EqualTo(2000));
			Assert.That(at144.States.Count, Is.EqualTo(2000));
			for (var i = 0; i < 2000; i++)
			{
				Assert.That(at144.States[i], Is.EqualTo(at30.States[i]), $"state after step {i + 1} differs between 30 and 144 fps");
			}

			Assert.That(at30.States[1999].Time, Is.EqualTo(20.0));
		}

		private static StepLog RunAt(double framesPerSecond, long steps)
		{
			var driver = new FixedStepDriver(NewSimulation(), ManoeuvringTrace()) { StepLimit = steps };
			var log = new StepLog();
			driver.AddObserver(log);
			var frameTime = (double)(float)(1.0 / framesPerSecond); // what Time.deltaTime would hand over
			for (var frame = 0; frame < 100000 && !driver.AtStepLimit; frame++)
			{
				driver.Advance(frameTime);
			}

			return log;
		}

		[Test]
		public void SlowFrame_IsCappedAtTheGuardAndTheExcessIsDropped()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(ControlInputs.Neutral), maxStepsPerFrame: 25);

			var slow = driver.Advance(1.0);

			Assert.That(slow.Steps, Is.EqualTo(25));
			Assert.That(slow.Dropped, Is.True);
			Assert.That(slow.DroppedTime, Is.EqualTo(0.75).Within(1e-9));
			Assert.That(driver.TotalDroppedTime, Is.EqualTo(0.75).Within(1e-9));
			Assert.That(driver.DroppedFrameCount, Is.EqualTo(1));
			Assert.That(driver.Accumulator, Is.LessThan(SimulationClock.FixedTimeStep));

			var next = driver.Advance(0.01);
			Assert.That(next.Steps, Is.EqualTo(1), "dropped time is never caught up in a later burst");
			Assert.That(next.Dropped, Is.False);
		}

		[TestCase(0.0)]
		[TestCase(-0.5)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		[TestCase(double.NegativeInfinity)]
		public void BadFrameTimes_RunNothing(double deltaTime)
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(ControlInputs.Neutral));

			var advance = driver.Advance(deltaTime);

			Assert.That(advance.Steps, Is.EqualTo(0));
			Assert.That(driver.StepCount, Is.EqualTo(0));
			Assert.That(driver.Accumulator, Is.EqualTo(0.0));
		}

		[Test]
		public void Controls_AreRequestedOncePerStepInStepOrder()
		{
			var source = new ScriptedControls(step => new ControlInputs(step % 2 == 0 ? 0.5 : -0.5, 0.0, 0.0));
			var driver = new FixedStepDriver(NewSimulation(), source);
			var log = new StepLog();
			driver.AddObserver(log);

			foreach (var frame in new[] { 0.013, 0.051, 0.0, 0.0333, 0.2 })
			{
				driver.Advance(frame);
			}

			Assert.That(source.Requested.Count, Is.EqualTo(driver.StepCount));
			for (var i = 0; i < source.Requested.Count; i++)
			{
				Assert.That(source.Requested[i], Is.EqualTo((long)i));
				Assert.That(log.StepsSeen[i], Is.EqualTo((long)i));
				Assert.That(log.Controls[i].PortLever, Is.EqualTo(i % 2 == 0 ? 0.5 : -0.5));
			}

			Assert.That(driver.LastControls, Is.EqualTo(log.Controls[log.Controls.Count - 1]));
		}

		[Test]
		public void InterpolationFactor_StaysWithinZeroAndOne()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(new ControlInputs(1.0, 1.0, 0.0)));
			var random = new Random(3);
			for (var frame = 0; frame < 2000; frame++)
			{
				driver.Advance(random.NextDouble() * 0.05);
				Assert.That(driver.InterpolationFactor, Is.InRange(0.0, 1.0));
			}

			Assert.That(driver.PreviousState.Time, Is.EqualTo(SimulationClock.TimeAtStep(driver.StepCount - 1)), "previous state is the one before the last step");
		}

		[Test]
		public void Restart_StartsTheClockAndCountersAgain()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(new ControlInputs(1.0, 1.0, 0.0)), maxStepsPerFrame: 5);
			driver.Advance(0.5);
			Assume.That(driver.DroppedFrameCount, Is.EqualTo(1));

			var fresh = NewSimulation();
			driver.Restart(fresh);

			Assert.That(driver.StepCount, Is.EqualTo(0));
			Assert.That(driver.Accumulator, Is.EqualTo(0.0));
			Assert.That(driver.TotalDroppedTime, Is.EqualTo(0.0));
			Assert.That(driver.DroppedFrameCount, Is.EqualTo(0));
			Assert.That(driver.PreviousState, Is.EqualTo(fresh.State));
			Assert.That(driver.LastControls, Is.EqualTo(ControlInputs.Neutral));
		}

		[Test]
		public void StepLimit_StopsExactlyAtTheLimit()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(new ControlInputs(1.0, 1.0, 0.0))) { StepLimit = 37 };
			while (!driver.AtStepLimit)
			{
				driver.Advance(1.0 / 7.0);
			}

			Assert.That(driver.StepCount, Is.EqualTo(37));
			Assert.That(driver.Advance(1.0).Steps, Is.EqualTo(0));
			Assert.That(driver.TotalDroppedTime, Is.EqualTo(0.0), "time past the limit is not a dropped frame");
		}

		[Test]
		public void NegativeZeroControls_ReachThePhysicsAsPositiveZero()
		{
			var driver = new FixedStepDriver(NewSimulation(), new ConstantControls(new ControlInputs(-0.0, -0.0, -0.0)));

			driver.StepOnce();

			Assert.That(BitConverter.DoubleToInt64Bits(driver.LastControls.PortLever), Is.EqualTo(0L));
			Assert.That(BitConverter.DoubleToInt64Bits(driver.LastControls.StarboardLever), Is.EqualTo(0L));
			Assert.That(BitConverter.DoubleToInt64Bits(driver.LastControls.Helm), Is.EqualTo(0L));
		}
	}
}
