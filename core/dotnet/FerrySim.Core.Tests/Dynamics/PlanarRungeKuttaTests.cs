using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// The planar Runge-Kutta step on systems with exact or converging answers. Failure modes guarded:
	/// stage poses evaluated at the wrong heading (for example a stage's yaw acceleration used where its yaw
	/// rate belongs), which leaves the heading's own update exact but drops the order of accuracy of anything
	/// that depends on the heading: the track in a turn, and heading-dependent moments.
	/// </summary>
	public class PlanarRungeKuttaTests
	{
		[Test]
		public void SteadyTurn_WithoutForces_FollowsTheExactCircle()
		{
			// Constant body velocity and yaw rate: the reference point runs round a circle.
			var turn = new SteadyTurn(10.0, 1.0);
			var north = 0.0;
			var east = 0.0;
			var heading = 0.3;
			var surge = 10.0;
			var sway = 1.0;
			var yawRate = 0.1;
			const double h = 0.01;
			for (var step = 0; step < 3000; step++)
			{
				PlanarRungeKutta.Step(turn, h, ref north, ref east, ref heading, ref surge, ref sway, ref yawRate);
			}

			turn.Exact(0.3, 0.1, 30.0, out var exactNorth, out var exactEast);
			Assert.That(Math.Sqrt((north - exactNorth) * (north - exactNorth) + (east - exactEast) * (east - exactEast)), Is.LessThan(1e-6),
				$"track error after 30 s of turning: north {north - exactNorth}, east {east - exactEast}");
			Assert.That(Angles.WrapPi(heading - (0.3 + 3.0)), Is.EqualTo(0.0).Within(1e-9));
		}

		[Test]
		public void SteadyTurn_TrackError_FallsWithTheFourthPowerOfTheStep()
		{
			var errors = new double[3];
			for (var i = 0; i < errors.Length; i++)
			{
				var h = 0.1 / (1 << i);
				var steps = (int)Math.Round(10.0 / h);
				var turn = new SteadyTurn(10.0, 1.0);
				var north = 0.0;
				var east = 0.0;
				var heading = 0.3;
				var surge = 10.0;
				var sway = 1.0;
				var yawRate = 0.3;
				for (var step = 0; step < steps; step++)
				{
					PlanarRungeKutta.Step(turn, h, ref north, ref east, ref heading, ref surge, ref sway, ref yawRate);
				}

				turn.Exact(0.3, 0.3, 10.0, out var exactNorth, out var exactEast);
				errors[i] = Math.Sqrt((north - exactNorth) * (north - exactNorth) + (east - exactEast) * (east - exactEast));
			}

			TestContext.Out.WriteLine($"track errors {errors[0]:E3}, {errors[1]:E3}, {errors[2]:E3}");
			Assert.That(errors[0] / errors[1], Is.InRange(12.0, 20.0), "halving the step divides the error by about 16");
			Assert.That(errors[1] / errors[2], Is.InRange(12.0, 20.0));
		}

		[Test]
		public void HeadingDependentYawMoment_ErrorFallsWithTheFourthPowerOfTheStep()
		{
			// ψ'' = −ω² sin ψ, a weathervaning moment: the stage headings set the moment at every stage.
			var reference = Swing(0.1 / 64.0);
			var errors = new double[3];
			for (var i = 0; i < errors.Length; i++)
			{
				errors[i] = Math.Abs(Angles.WrapPi(Swing(0.1 / (1 << i)) - reference));
			}

			TestContext.Out.WriteLine($"heading errors {errors[0]:E3}, {errors[1]:E3}, {errors[2]:E3}");
			Assert.That(errors[0] / errors[1], Is.InRange(12.0, 20.0), "halving the step divides the error by about 16");
			Assert.That(errors[1] / errors[2], Is.InRange(12.0, 20.0));
		}

		[Test]
		public void ConstantYawMoment_FromRest_TurnsByHalfAlphaTSquared()
		{
			// The heading's own update is exact for constant yaw acceleration (it was even with the old staging).
			var spin = new ConstantYawAcceleration(0.02);
			var north = 0.0;
			var east = 0.0;
			var heading = 0.0;
			var surge = 0.0;
			var sway = 0.0;
			var yawRate = 0.0;
			for (var step = 0; step < 1000; step++)
			{
				PlanarRungeKutta.Step(spin, 0.01, ref north, ref east, ref heading, ref surge, ref sway, ref yawRate);
			}

			Assert.That(heading, Is.EqualTo(0.5 * 0.02 * 10.0 * 10.0).Within(1e-12));
			Assert.That(yawRate, Is.EqualTo(0.2).Within(1e-12));
			Assert.That(north, Is.EqualTo(0.0));
			Assert.That(east, Is.EqualTo(0.0));
		}

		private static double Swing(double h)
		{
			var swing = new HeadingRestoring(1.0);
			var north = 0.0;
			var east = 0.0;
			var heading = 0.5;
			var surge = 0.0;
			var sway = 0.0;
			var yawRate = 0.0;
			var steps = (int)Math.Round(10.0 / h);
			for (var step = 0; step < steps; step++)
			{
				PlanarRungeKutta.Step(swing, h, ref north, ref east, ref heading, ref surge, ref sway, ref yawRate);
			}

			return heading;
		}

		/// <summary>No forces: constant body velocity; the position rates rotate with the heading.</summary>
		private readonly struct SteadyTurn : IPlanarRates
		{
			private readonly double _surge;
			private readonly double _sway;

			public SteadyTurn(double surge, double sway)
			{
				_surge = surge;
				_sway = sway;
			}

			public void Rates(double north, double east, double heading, double surge, double sway, double yawRate, bool firstStage,
				out double northRate, out double eastRate, out double surgeRate, out double swayRate, out double yawAcceleration)
			{
				northRate = surge * Math.Cos(heading) - sway * Math.Sin(heading);
				eastRate = surge * Math.Sin(heading) + sway * Math.Cos(heading);
				surgeRate = 0.0;
				swayRate = 0.0;
				yawAcceleration = 0.0;
			}

			/// <summary>Position after <paramref name="time"/> from the origin at heading ψ₀ and yaw rate r.</summary>
			public void Exact(double initialHeading, double yawRate, double time, out double north, out double east)
			{
				var heading = initialHeading + yawRate * time;
				north = (_surge * (Math.Sin(heading) - Math.Sin(initialHeading)) + _sway * (Math.Cos(heading) - Math.Cos(initialHeading))) / yawRate;
				east = (-_surge * (Math.Cos(heading) - Math.Cos(initialHeading)) + _sway * (Math.Sin(heading) - Math.Sin(initialHeading))) / yawRate;
			}
		}

		private readonly struct HeadingRestoring : IPlanarRates
		{
			private readonly double _frequencySquared;

			public HeadingRestoring(double frequency)
			{
				_frequencySquared = frequency * frequency;
			}

			public void Rates(double north, double east, double heading, double surge, double sway, double yawRate, bool firstStage,
				out double northRate, out double eastRate, out double surgeRate, out double swayRate, out double yawAcceleration)
			{
				northRate = 0.0;
				eastRate = 0.0;
				surgeRate = 0.0;
				swayRate = 0.0;
				yawAcceleration = -_frequencySquared * Math.Sin(heading);
			}
		}

		private readonly struct ConstantYawAcceleration : IPlanarRates
		{
			private readonly double _acceleration;

			public ConstantYawAcceleration(double acceleration)
			{
				_acceleration = acceleration;
			}

			public void Rates(double north, double east, double heading, double surge, double sway, double yawRate, bool firstStage,
				out double northRate, out double eastRate, out double surgeRate, out double swayRate, out double yawAcceleration)
			{
				northRate = 0.0;
				eastRate = 0.0;
				surgeRate = 0.0;
				swayRate = 0.0;
				yawAcceleration = _acceleration;
			}
		}
	}
}
