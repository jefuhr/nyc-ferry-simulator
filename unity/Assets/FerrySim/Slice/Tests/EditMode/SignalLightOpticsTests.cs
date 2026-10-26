using System;
using FerrySim.World;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// How far a light is seen (Allard's law with Annex I's threshold and transmissivity) and how thick the fog is drawn
	/// (the meteorological optical range). Failure modes guarded: light ranges ignoring the visibility; a light not seen
	/// at its Rule 22 range in clear air; a fog density off the 5% definition of the meteorological optical range; a
	/// restricted-visibility test that fires in clear weather or on an unlimited setting; daylight ranges as long as night
	/// ones; and non-finite results for edge inputs.
	/// </summary>
	public class SignalLightOpticsTests
	{
		[Test]
		public void InAnnexIsClearAir_EveryPrescribedLightIsSeenAtItsNominalRange()
		{
			foreach (var nauticalMiles in new[] { 1.0, 2.0, 3.0, 5.0, 6.0 })
			{
				var nominal = nauticalMiles * NavigationLightRules.MetresPerNauticalMile;
				var intensity = SignalLightOptics.IntensityForRange(nominal);
				var range = SignalLightOptics.LuminousRange(intensity, SignalLightOptics.ClearAirExtinction, SignalLightOptics.NightThresholdLux);

				// 84.14's 3.43e6 is 1852 squared rounded, so the range comes out 0.001% long.
				Assert.That(range, Is.EqualTo(nominal).Within(1e-4 * nominal), $"{nauticalMiles} nm light");
				Assert.That(SignalLightOptics.Illuminance(intensity, range, SignalLightOptics.ClearAirExtinction), Is.EqualTo(SignalLightOptics.NightThresholdLux).Within(1e-9 * 2e-7));
			}
		}

		[Test]
		public void AnnexIsTransmissivity_IsAMeteorologicalVisibilityOfAbout13NauticalMiles()
		{
			var visibility = MeteorologicalVisibility.FromExtinction(SignalLightOptics.ClearAirExtinction);

			Assert.That(visibility / NavigationLightRules.MetresPerNauticalMile, Is.EqualTo(13.4).Within(0.05), "84.14(a): K = 0.8, about 13 nautical miles");
		}

		[TestCase(5000.0)]
		[TestCase(500.0)]
		[TestCase(200.0)]
		public void TheFogDensity_LeavesFivePercentAtTheVisibility(double visibility)
		{
			var density = MeteorologicalVisibility.ExtinctionCoefficient(visibility);

			Assert.That(Math.Exp(-density * visibility), Is.EqualTo(0.05).Within(1e-12), "the meteorological optical range: 5% of a beam left");
			Assert.That(density, Is.EqualTo(Math.Log(20.0) / visibility).Within(1e-15));
		}

		[Test]
		public void LightRanges_ShrinkWithTheVisibility()
		{
			var sidelight = SignalLightOptics.IntensityForRange(2.0 * NavigationLightRules.MetresPerNauticalMile);
			var clear = SignalLightOptics.LuminousRange(sidelight, SignalLightOptics.LightExtinction(MeteorologicalVisibility.Unlimited), SignalLightOptics.NightThresholdLux);
			var fog500 = SignalLightOptics.LuminousRange(sidelight, SignalLightOptics.LightExtinction(500.0), SignalLightOptics.NightThresholdLux);
			var fog200 = SignalLightOptics.LuminousRange(sidelight, SignalLightOptics.LightExtinction(200.0), SignalLightOptics.NightThresholdLux);

			Assert.That(clear, Is.EqualTo(2.0 * 1852.0).Within(1.0));
			Assert.That(fog500, Is.LessThan(clear / 4.0));
			Assert.That(fog200, Is.LessThan(fog500 * 0.7));
			// At night a light is seen beyond the meteorological visibility (that is where Allard's law and Koschmieder's
			// differ: a light needs a threshold of illuminance, an object 5% contrast).
			Assert.That(fog500, Is.InRange(500.0, 800.0));
			Assert.That(fog200, Is.InRange(200.0, 400.0));
			Assert.That(SignalLightOptics.Illuminance(sidelight, fog500, SignalLightOptics.LightExtinction(500.0)), Is.EqualTo(SignalLightOptics.NightThresholdLux).Within(1e-12));
		}

		[Test]
		public void DaylightThreshold_IsHigherThanNights_SoLightsAreSeenNearerByDay()
		{
			var night = SignalLightOptics.ThresholdLux(-12.0);
			var dusk = SignalLightOptics.ThresholdLux(-3.0);
			var day = SignalLightOptics.ThresholdLux(20.0);
			var masthead = SignalLightOptics.IntensityForRange(5.0 * 1852.0);

			Assert.That(night, Is.EqualTo(SignalLightOptics.NightThresholdLux), "Annex I's threshold, 2e-7 lux");
			Assert.That(dusk, Is.GreaterThan(night).And.LessThan(day));
			Assert.That(day, Is.EqualTo(SignalLightOptics.DayThresholdLux));
			Assert.That(SignalLightOptics.LuminousRange(masthead, SignalLightOptics.ClearAirExtinction, day),
				Is.LessThan(0.1 * SignalLightOptics.LuminousRange(masthead, SignalLightOptics.ClearAirExtinction, night)));
		}

		[TestCase(0.0, false, false)]
		[TestCase(-1.0, false, false)]
		[TestCase(200.0, true, true)]
		[TestCase(1851.0, true, true)]
		[TestCase(1852.0, true, false)]
		[TestCase(10000.0, true, false)]
		public void RestrictedVisibility_IsALimitedVisibilityUnderOneNauticalMile(double visibility, bool limited, bool restricted)
		{
			Assert.That(MeteorologicalVisibility.IsLimited(visibility), Is.EqualTo(limited));
			Assert.That(MeteorologicalVisibility.IsRestricted(visibility), Is.EqualTo(restricted));
		}

		[Test]
		public void EdgeInputs_GiveFiniteAnswers()
		{
			Assert.That(SignalLightOptics.LuminousRange(0.0, SignalLightOptics.ClearAirExtinction, SignalLightOptics.NightThresholdLux), Is.EqualTo(0.0));
			Assert.That(SignalLightOptics.LuminousRange(52.0, 0.0, SignalLightOptics.NightThresholdLux), Is.EqualTo(Math.Sqrt(52.0 / 2e-7)).Within(1e-6), "no extinction: the inverse square alone");
			Assert.That(SignalLightOptics.LuminousRange(52.0, MeteorologicalVisibility.ExtinctionCoefficient(10.0), SignalLightOptics.NightThresholdLux), Is.GreaterThan(0.0).And.LessThan(200.0));
			Assert.That(SignalLightOptics.Illuminance(52.0, 0.0, SignalLightOptics.ClearAirExtinction), Is.EqualTo(double.PositiveInfinity));
			Assert.Throws<ArgumentOutOfRangeException>(() => MeteorologicalVisibility.ExtinctionCoefficient(0.0));
		}
	}
}
