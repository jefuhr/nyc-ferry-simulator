using System;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	public class UnitsAndAnglesTests
	{
		[Test]
		public void Knot_IsExactlyNauticalMilePerHour()
		{
			Assert.That(Units.KnotsToMetresPerSecond(1.0), Is.EqualTo(1852.0 / 3600.0));
			Assert.That(Units.MetresPerSecondToKnots(Units.KnotsToMetresPerSecond(25.0)), Is.EqualTo(25.0).Within(1e-12));
		}

		[Test]
		public void FeetAndInches_ConvertToMetres()
		{
			// T-class length overall as published: 85'4".
			Assert.That(Units.FeetAndInchesToMetres(85.0, 4.0), Is.EqualTo(26.0096).Within(1e-9));
		}

		[Test]
		public void Rpm_ConvertsToRadiansPerSecond()
		{
			Assert.That(Units.RpmToRadiansPerSecond(60.0), Is.EqualTo(2.0 * Math.PI).Within(1e-12));
			Assert.That(Units.RadiansPerSecondToRpm(Units.RpmToRadiansPerSecond(2100.0)), Is.EqualTo(2100.0).Within(1e-9));
		}

		[Test]
		public void Horsepower_IsMechanicalHorsepower()
		{
			Assert.That(Units.HorsepowerToWatts(815.0), Is.EqualTo(607745.395).Within(0.001));
		}

		[TestCase(0.0, 0.0)]
		[TestCase(-0.5 * Math.PI, 1.5 * Math.PI)]
		[TestCase(2.0 * Math.PI, 0.0)]
		[TestCase(5.0 * Math.PI, Math.PI)]
		[TestCase(-1e-18, 0.0)]
		public void WrapTwoPi_StaysInHalfOpenRange(double input, double expected)
		{
			var wrapped = Angles.WrapTwoPi(input);
			Assert.That(wrapped, Is.EqualTo(expected).Within(1e-12));
			Assert.That(wrapped, Is.GreaterThanOrEqualTo(0.0).And.LessThan(Angles.TwoPi));
		}

		[TestCase(1.5 * Math.PI, -0.5 * Math.PI)]
		[TestCase(0.25 * Math.PI, 0.25 * Math.PI)]
		[TestCase(-2.5 * Math.PI, -0.5 * Math.PI)]
		public void WrapPi_StaysInSignedRange(double input, double expected)
		{
			var wrapped = Angles.WrapPi(input);
			Assert.That(wrapped, Is.EqualTo(expected).Within(1e-12));
			Assert.That(wrapped, Is.GreaterThanOrEqualTo(-Math.PI).And.LessThan(Math.PI));
		}
	}
}
