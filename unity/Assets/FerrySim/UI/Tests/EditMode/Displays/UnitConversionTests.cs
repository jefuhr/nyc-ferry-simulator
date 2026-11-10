using System;
using FerrySim.Core.Instruments;
using FerrySim.UI.Displays;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>
	/// Every display unit a skin may name, against values worked by hand from the defining constants (1 psi is
	/// 0.45359237 kg × 9.80665 m/s² over 0.0254² m², 1 US gallon is 3.785411784 L, 1 knot is 1852 m per hour).
	/// Failure modes: a wrong factor or offset (°F from kelvin is the usual one), a conversion that turns NaN into
	/// a number, and a skin unit allowed on a channel of another quantity.
	/// </summary>
	public class UnitConversionTests
	{
		private const double Tight = 1e-9;

		[TestCase(DisplayUnit.Rpm, 104.71975511965977, 1000.0)]
		[TestCase(DisplayUnit.Rpm, -52.35987755982988, -500.0)]
		[TestCase(DisplayUnit.Knots, 1.0, 1.9438444924406046)]
		[TestCase(DisplayUnit.Knots, 5.144444444444445, 10.0)]
		[TestCase(DisplayUnit.Degrees, Math.PI, 180.0)]
		[TestCase(DisplayUnit.Degrees, -0.5235987755982988, -30.0)]
		[TestCase(DisplayUnit.DegreesPerMinute, 0.01, 34.37746770784939)]
		[TestCase(DisplayUnit.Psi, 344737.86465841805, 50.0)]
		[TestCase(DisplayUnit.Psi, 101325.0, 14.69594877551345)]
		[TestCase(DisplayUnit.Bar, 200000.0, 2.0)]
		[TestCase(DisplayUnit.Kilopascals, 200000.0, 200.0)]
		[TestCase(DisplayUnit.Celsius, 373.15, 100.0)]
		[TestCase(DisplayUnit.Celsius, 273.15, 0.0)]
		[TestCase(DisplayUnit.Fahrenheit, 373.15, 212.0)]
		[TestCase(DisplayUnit.Fahrenheit, 233.15, -40.0)]
		[TestCase(DisplayUnit.Fahrenheit, 310.15, 98.6)]
		[TestCase(DisplayUnit.Volts, 27.6, 27.6)]
		[TestCase(DisplayUnit.LitresPerHour, 0.01, 42.857142857142854)]
		[TestCase(DisplayUnit.GallonsPerHour, 0.01, 11.32165938677779)]
		[TestCase(DisplayUnit.Percent, 0.5, 50.0)]
		[TestCase(DisplayUnit.Percent, -1.0, -100.0)]
		[TestCase(DisplayUnit.Metres, 4.2, 4.2)]
		[TestCase(DisplayUnit.Feet, 1.0, 3.280839895013123)]
		[TestCase(DisplayUnit.Feet, 0.3048, 1.0)]
		[TestCase(DisplayUnit.None, 1234.5, 1234.5)]
		public void FromSi_MatchesTheHandCheckedValue(DisplayUnit unit, double si, double expected)
		{
			Assert.That(SkinUnits.FromSi(unit, si), Is.EqualTo(expected).Within(1e-9 * Math.Max(1.0, Math.Abs(expected))));
		}

		[Test]
		public void ToSi_UndoesFromSi_ForEveryUnit()
		{
			foreach (DisplayUnit unit in Enum.GetValues(typeof(DisplayUnit)))
			{
				foreach (var si in new[] { -40.0, 0.0, 1.0, 293.15, 123456.0 })
				{
					var back = SkinUnits.ToSi(unit, SkinUnits.FromSi(unit, si));
					Assert.That(back, Is.EqualTo(si).Within(Tight * Math.Max(1.0, Math.Abs(si))), unit + " round trip of " + si);
				}
			}

			Assert.That(SkinUnits.ToSi(DisplayUnit.Psi, 50.0), Is.EqualTo(344737.86465841805).Within(1e-6));
			Assert.That(SkinUnits.ToSi(DisplayUnit.Fahrenheit, 32.0), Is.EqualTo(273.15).Within(Tight));
			Assert.That(SkinUnits.ToSi(DisplayUnit.GallonsPerHour, 1.0), Is.EqualTo(3.785411784 * 0.84 / 3600.0).Within(Tight));
		}

		[Test]
		public void NotAvailable_StaysNotAvailable()
		{
			foreach (DisplayUnit unit in Enum.GetValues(typeof(DisplayUnit)))
			{
				Assert.That(SkinUnits.FromSi(unit, double.NaN), Is.NaN, unit.ToString());
			}
		}

		[Test]
		public void FuelDensity_IsANamedConstantInKilogramsPerLitre()
		{
			Assert.That(SkinUnits.FuelDensityKilogramsPerLitre, Is.EqualTo(0.84));
			Assert.That(SkinUnits.LitresPerUsGallon, Is.EqualTo(3.785411784));
			Assert.That(SkinUnits.PascalsPerPsi, Is.EqualTo(6894.757293168361).Within(1e-9));
		}

		[TestCase("rpm", DisplayUnit.Rpm, "rpm")]
		[TestCase("kn", DisplayUnit.Knots, "kn")]
		[TestCase("deg", DisplayUnit.Degrees, "°")]
		[TestCase("deg/min", DisplayUnit.DegreesPerMinute, "°/min")]
		[TestCase("psi", DisplayUnit.Psi, "psi")]
		[TestCase("bar", DisplayUnit.Bar, "bar")]
		[TestCase("kPa", DisplayUnit.Kilopascals, "kPa")]
		[TestCase("degC", DisplayUnit.Celsius, "°C")]
		[TestCase("degF", DisplayUnit.Fahrenheit, "°F")]
		[TestCase("V", DisplayUnit.Volts, "V")]
		[TestCase("L/h", DisplayUnit.LitresPerHour, "L/h")]
		[TestCase("gal/h", DisplayUnit.GallonsPerHour, "gal/h")]
		[TestCase("pct", DisplayUnit.Percent, "%")]
		[TestCase("m", DisplayUnit.Metres, "m")]
		[TestCase("ft", DisplayUnit.Feet, "ft")]
		[TestCase("none", DisplayUnit.None, "")]
		public void EverySchemaUnit_ParsesAndHasASymbol(string name, DisplayUnit unit, string symbol)
		{
			Assert.That(SkinUnits.TryParse(name, out var parsed), Is.True);
			Assert.That(parsed, Is.EqualTo(unit));
			Assert.That(SkinUnits.Name(unit), Is.EqualTo(name));
			Assert.That(SkinUnits.Symbol(unit), Is.EqualTo(symbol));
		}

		[Test]
		public void UnknownUnits_DoNotParse()
		{
			Assert.That(SkinUnits.TryParse("mph", out _), Is.False);
			Assert.That(SkinUnits.TryParse("PSI", out _), Is.False, "names are case-sensitive, like channel names");
			Assert.That(SkinUnits.TryParse(null, out _), Is.False);
		}

		[TestCase(Channel.EnginePortOilPressure, DisplayUnit.Psi, true)]
		[TestCase(Channel.EnginePortOilPressure, DisplayUnit.Kilopascals, true)]
		[TestCase(Channel.EnginePortOilPressure, DisplayUnit.Rpm, false)]
		[TestCase(Channel.EnginePortSpeed, DisplayUnit.Rpm, true)]
		[TestCase(Channel.EngineStarboardShaftSpeed, DisplayUnit.Rpm, true)]
		[TestCase(Channel.EnginePortSpeed, DisplayUnit.Psi, false)]
		[TestCase(Channel.EnginePortCoolantTemperature, DisplayUnit.Fahrenheit, true)]
		[TestCase(Channel.EnginePortCoolantTemperature, DisplayUnit.Percent, false)]
		[TestCase(Channel.EnginePortFuelRate, DisplayUnit.GallonsPerHour, true)]
		[TestCase(Channel.EnginePortLoad, DisplayUnit.Percent, true)]
		[TestCase(Channel.LeverPort, DisplayUnit.Percent, true)]
		[TestCase(Channel.RudderPort, DisplayUnit.Degrees, true)]
		[TestCase(Channel.RateOfTurn, DisplayUnit.DegreesPerMinute, true)]
		[TestCase(Channel.RateOfTurn, DisplayUnit.Degrees, false)]
		[TestCase(Channel.SpeedOverGround, DisplayUnit.Knots, true)]
		[TestCase(Channel.DepthUnderKeel, DisplayUnit.Feet, true)]
		[TestCase(Channel.BusVoltage, DisplayUnit.Volts, true)]
		[TestCase(Channel.EnginePortShiftPhase, DisplayUnit.Percent, false)]
		[TestCase(Channel.EnginePortShiftPhase, DisplayUnit.None, true)]
		public void AUnitFitsOnlyChannelsOfItsQuantity(Channel channel, DisplayUnit unit, bool fits)
		{
			Assert.That(SkinUnits.Fits(unit, channel), Is.EqualTo(fits));
		}

		[Test]
		public void EveryChannelHasAQuantity_AndNoneFitsThemAll()
		{
			foreach (Channel channel in Enum.GetValues(typeof(Channel)))
			{
				Assert.DoesNotThrow(() => SkinUnits.Quantity(channel), channel.ToString());
				Assert.That(SkinUnits.Fits(DisplayUnit.None, channel), Is.True, channel.ToString());
			}
		}
	}
}
