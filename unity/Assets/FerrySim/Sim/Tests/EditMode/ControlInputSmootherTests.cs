using System;
using FerrySim.Controls;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// Device-noise smoothing. Failure modes guarded: a filter that biases or never quite reaches a steady input
	/// (the lever then sits a hair off a detent), a transient at the start of a run, smoothing that cannot be turned
	/// off, and a filter that does not actually calm noise.
	/// </summary>
	public class ControlInputSmootherTests
	{
		private static readonly double[] TimeConstants = { 0.0, 0.01, 0.05, 0.2, 1.0, 10.0 };

		private static readonly ControlInputs[] SteadyInputs =
		{
			ControlInputs.Neutral,
			new ControlInputs(0.2, 0.2, 0.0),
			new ControlInputs(-0.6, 1.0, -1.0),
			new ControlInputs(0.123456789012345, -0.987654321098765, 0.3333333333333333),
			new ControlInputs(1.0, -1.0, 1.0),
		};

		[Test]
		public void SteadyInput_IsUnchangedBitForBitFromTheFirstStep([ValueSource(nameof(TimeConstants))] double timeConstant)
		{
			foreach (var input in SteadyInputs)
			{
				var smoother = new ControlInputSmoother(timeConstant);
				for (var step = 0; step < 3000; step++)
				{
					var output = smoother.Step(input);
					AssertBitEqual(output, input, $"tau {timeConstant} s, step {step}");
				}
			}
		}

		[Test]
		public void AfterAChange_SettlesExactlyOnTheNewInputAndStaysThere([ValueSource(nameof(TimeConstants))] double timeConstant)
		{
			var smoother = new ControlInputSmoother(timeConstant);
			var before = new ControlInputs(0.0, 0.4, -0.2);
			var after = new ControlInputs(0.6, -0.8, 0.7);
			for (var step = 0; step < 100; step++)
			{
				smoother.Step(before);
			}

			var settledAt = -1;
			for (var step = 0; step < 20000; step++)
			{
				var output = smoother.Step(after);
				if (output.Equals(after))
				{
					settledAt = step;
					break;
				}

				Assert.That(output.PortLever, Is.InRange(0.0, 0.6), "moves monotonically toward the new value");
			}

			Assert.That(settledAt, Is.GreaterThanOrEqualTo(0), "never settled exactly on the new input");
			var bound = timeConstant <= 0.0 ? 0 : (int)Math.Ceiling(timeConstant * SimulationClock.StepsPerSecond * Math.Log(2.0 / ControlInputSmoother.SettleTolerance)) + 1;
			Assert.That(settledAt, Is.LessThanOrEqualTo(bound), "settles within the time the time constant implies");
			for (var step = 0; step < 2000; step++)
			{
				AssertBitEqual(smoother.Step(after), after, "after settling");
			}
		}

		[Test]
		public void ZeroTimeConstant_PassesEveryInputStraightThrough()
		{
			var smoother = new ControlInputSmoother(0.0);
			var random = new Random(2);
			for (var step = 0; step < 5000; step++)
			{
				var input = new ControlInputs(random.NextDouble() * 2.0 - 1.0, random.NextDouble() * 2.0 - 1.0, random.NextDouble() * 2.0 - 1.0);
				AssertBitEqual(smoother.Step(input), input, $"step {step}");
			}
		}

		[Test]
		public void Noise_IsCalmed()
		{
			var smoother = new ControlInputSmoother(ControlInputSmoother.DefaultTimeConstant);
			var random = new Random(9);
			double inputSpread = 0.0, outputSpread = 0.0;
			const int Steps = 20000;
			var noisy = 0.5;
			for (var step = 0; step < Steps; step++)
			{
				// A potentiometer that reads ±0.01 around 0.5, sampled every other physics step (about 50 fps).
				if (step % 2 == 0)
				{
					noisy = 0.5 + (random.NextDouble() * 2.0 - 1.0) * 0.01;
				}

				var output = smoother.Step(new ControlInputs(noisy, 0.0, 0.0));
				if (step > 100)
				{
					inputSpread += (noisy - 0.5) * (noisy - 0.5);
					outputSpread += (output.PortLever - 0.5) * (output.PortLever - 0.5);
				}
			}

			Assert.That(Math.Sqrt(outputSpread / inputSpread), Is.LessThan(0.6), "noise amplitude reduced");
		}

		[Test]
		public void Reset_ForgetsTheHistory()
		{
			var smoother = new ControlInputSmoother(0.5);
			smoother.Step(new ControlInputs(1.0, 1.0, 1.0));

			smoother.Reset();
			var output = smoother.Step(new ControlInputs(-0.3, 0.0, 0.0));

			AssertBitEqual(output, new ControlInputs(-0.3, 0.0, 0.0), "first step after a reset");
		}

		[TestCase(-0.1)]
		[TestCase(double.NaN)]
		[TestCase(double.PositiveInfinity)]
		public void InvalidTimeConstants_AreRejected(double timeConstant)
		{
			Assert.That(() => new ControlInputSmoother(timeConstant), Throws.InstanceOf<ArgumentOutOfRangeException>());
		}

		private static void AssertBitEqual(in ControlInputs actual, in ControlInputs expected, string message)
		{
			Assert.That(BitConverter.DoubleToInt64Bits(actual.PortLever), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected.PortLever)), message + " (port)");
			Assert.That(BitConverter.DoubleToInt64Bits(actual.StarboardLever), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected.StarboardLever)), message + " (starboard)");
			Assert.That(BitConverter.DoubleToInt64Bits(actual.Helm), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected.Helm)), message + " (helm)");
		}
	}
}
