using System;
using System.Globalization;
using System.Threading;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Unit conversion and readout text. Guards against: wrong factors (statute vs nautical, rad vs deg,
	/// rad/s vs rpm), heading wrap mistakes (negative angles, 359.6° printing "360"), rudder sides swapped,
	/// a speed log losing its astern sign, NaN leaking into a readout, culture-dependent decimal commas,
	/// "-0.0" readouts and the metres/feet setting not reaching depths.
	/// </summary>
	public class DisplayFormatTests
	{
		private static double Deg(double degrees) => Angles.ToRadians(degrees);

		[Test]
		public void Conversions_UseExactFactors()
		{
			Assert.That(DisplayUnits.Knots(1852.0 / 3600.0), Is.EqualTo(1.0).Within(1e-12));
			Assert.That(DisplayUnits.Knots(10.0), Is.EqualTo(19.438444924406047).Within(1e-9));
			Assert.That(DisplayUnits.Rpm(2.0 * Math.PI), Is.EqualTo(60.0).Within(1e-9));
			Assert.That(DisplayUnits.Distance(0.3048, DistanceUnit.Feet), Is.EqualTo(1.0).Within(1e-12));
			Assert.That(DisplayUnits.Distance(12.5, DistanceUnit.Metres), Is.EqualTo(12.5));
			Assert.That(DisplayUnits.DistanceToMetres(10.0, DistanceUnit.Feet), Is.EqualTo(3.048).Within(1e-12));
			Assert.That(DisplayUnits.DistanceToMetres(DisplayUnits.Distance(7.3, DistanceUnit.Feet), DistanceUnit.Feet), Is.EqualTo(7.3).Within(1e-12));
			Assert.That(DisplayUnits.Kilonewtons(12500.0), Is.EqualTo(12.5));
			Assert.That(DisplayUnits.Symbol(DistanceUnit.Feet), Is.EqualTo("ft"));
			Assert.That(DisplayUnits.Symbol(DistanceUnit.Metres), Is.EqualTo("m"));
		}

		[TestCase(0.0, "000")]
		[TestCase(5.0, "005")]
		[TestCase(90.0, "090")]
		[TestCase(359.4, "359")]
		[TestCase(359.6, "000")]
		[TestCase(360.0, "000")]
		[TestCase(-0.4, "000")]
		[TestCase(-10.0, "350")]
		[TestCase(725.0, "005")]
		public void Compass_WrapsAndPadsToThreeDigits(double degrees, string expected)
		{
			Assert.That(InstrumentFormat.Compass(Deg(degrees)), Is.EqualTo(expected));
		}

		[Test]
		public void Course_IsBlankWhenNearlyStopped()
		{
			var slow = Units.KnotsToMetresPerSecond(0.1);
			var moving = Units.KnotsToMetresPerSecond(3.0);

			Assert.That(InstrumentFormat.Course(Deg(123.0), slow), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(InstrumentFormat.Course(Deg(123.0), moving), Is.EqualTo("123"));
			Assert.That(InstrumentFormat.Course(Deg(123.0), double.NaN), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[Test]
		public void Speed_IsKnotsWithOneDecimalAndKeepsAsternSign()
		{
			Assert.That(InstrumentFormat.Speed(Units.KnotsToMetresPerSecond(24.9973)), Is.EqualTo("25.0"));
			Assert.That(InstrumentFormat.Speed(Units.KnotsToMetresPerSecond(12.34)), Is.EqualTo("12.3"));
			Assert.That(InstrumentFormat.Speed(Units.KnotsToMetresPerSecond(-1.26)), Is.EqualTo("-1.3"));
			Assert.That(InstrumentFormat.Speed(Units.KnotsToMetresPerSecond(-0.02)), Is.EqualTo("0.0"), "no negative zero");
			Assert.That(InstrumentFormat.Speed(double.NaN), Is.EqualTo(InstrumentFormat.NoValue));
			Assert.That(InstrumentFormat.Speed(double.PositiveInfinity), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[Test]
		public void Rpm_IsWholeAndUnsigned()
		{
			Assert.That(InstrumentFormat.Rpm(Units.RpmToRadiansPerSecond(2025.66)), Is.EqualTo("2026"));
			Assert.That(InstrumentFormat.Rpm(Units.RpmToRadiansPerSecond(-539.55)), Is.EqualTo("540"));
			Assert.That(InstrumentFormat.Rpm(Units.RpmToRadiansPerSecond(0.3)), Is.EqualTo("0"));
			Assert.That(InstrumentFormat.Rpm(double.NaN), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[TestCase(12.0, "S 12°")]
		[TestCase(-3.4, "P 3°")]
		[TestCase(0.4, "0°")]
		[TestCase(-0.4, "0°")]
		[TestCase(35.0, "S 35°")]
		[TestCase(-34.6, "P 35°")]
		public void Rudder_PositiveIsStarboard(double degrees, string expected)
		{
			Assert.That(InstrumentFormat.Rudder(Deg(degrees)), Is.EqualTo(expected));
		}

		[Test]
		public void Lever_ShowsPercentAndDirection()
		{
			Assert.That(InstrumentFormat.LeverPercent(0.959), Is.EqualTo("96%"));
			Assert.That(InstrumentFormat.LeverDirection(0.959), Is.EqualTo("AHEAD"));
			Assert.That(InstrumentFormat.LeverPercent(-1.0), Is.EqualTo("100%"));
			Assert.That(InstrumentFormat.LeverDirection(-1.0), Is.EqualTo("ASTERN"));
			Assert.That(InstrumentFormat.LeverPercent(0.004), Is.EqualTo("0%"));
			Assert.That(InstrumentFormat.LeverDirection(0.004), Is.EqualTo("NEUTRAL"));
			Assert.That(InstrumentFormat.LeverDirection(-0.004), Is.EqualTo("NEUTRAL"));
			Assert.That(InstrumentFormat.LeverDirection(0.05), Is.EqualTo("AHEAD"), "a lever inside the clutch-in band is still a command");
			Assert.That(InstrumentFormat.LeverPercent(double.NaN), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[Test]
		public void Distance_FollowsUnitSettingAndPrecisionRule()
		{
			Assert.That(InstrumentFormat.Distance(48.1, DistanceUnit.Metres), Is.EqualTo("48.1"));
			Assert.That(InstrumentFormat.Distance(48.1, DistanceUnit.Feet), Is.EqualTo("158"));
			Assert.That(InstrumentFormat.Distance(1.524, DistanceUnit.Feet), Is.EqualTo("5.0"));
			Assert.That(InstrumentFormat.Distance(1.524, DistanceUnit.Metres), Is.EqualTo("1.5"));
			Assert.That(InstrumentFormat.Distance(30.47, DistanceUnit.Feet), Is.EqualTo("100"), "no '100.0' at the boundary");
			Assert.That(InstrumentFormat.Distance(-0.3048, DistanceUnit.Feet), Is.EqualTo("-1.0"), "aground reads negative, not hidden");
			Assert.That(InstrumentFormat.Distance(double.NaN, DistanceUnit.Feet), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[Test]
		public void Force_IsKilonewtons()
		{
			Assert.That(InstrumentFormat.Force(420.0), Is.EqualTo("0.4"));
			Assert.That(InstrumentFormat.Force(9940.0), Is.EqualTo("9.9"));
			Assert.That(InstrumentFormat.Force(45210.0), Is.EqualTo("45"));
			Assert.That(InstrumentFormat.Force(double.NaN), Is.EqualTo(InstrumentFormat.NoValue));
		}

		[Test]
		public void Elapsed_FormatsMinutesAndHours()
		{
			Assert.That(InstrumentFormat.Elapsed(45.9), Is.EqualTo("T+0:45"));
			Assert.That(InstrumentFormat.Elapsed(725.0), Is.EqualTo("T+12:05"));
			Assert.That(InstrumentFormat.Elapsed(3723.0), Is.EqualTo("T+1:02:03"));
			Assert.That(InstrumentFormat.Elapsed(-5.0), Is.EqualTo("T-0:05"));
		}

		[Test]
		public void Readouts_IgnoreTheMachineCulture()
		{
			var saved = Thread.CurrentThread.CurrentCulture;
			try
			{
				Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
				Assert.That(InstrumentFormat.Speed(Units.KnotsToMetresPerSecond(12.34)), Is.EqualTo("12.3"));
				Assert.That(InstrumentFormat.Distance(48.1, DistanceUnit.Metres), Is.EqualTo("48.1"));
				Assert.That(InstrumentFormat.Clock(new DateTime(2026, 6, 15, 9, 5, 7)), Is.EqualTo("09:05:07"));
			}
			finally
			{
				Thread.CurrentThread.CurrentCulture = saved;
			}
		}
	}
}
