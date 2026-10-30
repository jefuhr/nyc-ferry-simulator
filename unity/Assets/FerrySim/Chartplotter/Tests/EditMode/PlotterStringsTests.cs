using NUnit.Framework;

namespace FerrySim.Chartplotter.Tests.EditMode
{
	// Failure modes: a CPA rounded to the wrong hundredth, a negative TCPA shown as a time, non-finite values printed, spans mislabelled.
	public class PlotterStringsTests
	{
		[Test]
		public void Miles()
		{
			Assert.That(PlotterStrings.MilesHundredths(185.2), Is.EqualTo("0.10"));
			Assert.That(PlotterStrings.MilesHundredths(0), Is.EqualTo("0.00"));
			Assert.That(PlotterStrings.MilesHundredths(1852 * 12.345), Is.EqualTo("12.35").Or.EqualTo("12.34"), "beyond the table, formatted");
			Assert.That(PlotterStrings.MilesHundredths(double.NaN), Is.EqualTo("---"));
			Assert.That(PlotterStrings.MilesTenths(926), Is.EqualTo("0.5"));
			Assert.That(ReferenceEquals(PlotterStrings.MilesHundredths(100), PlotterStrings.MilesHundredths(100)), Is.True, "table strings are shared");
		}

		[Test]
		public void Tcpa()
		{
			Assert.That(PlotterStrings.MinutesAndSeconds(0), Is.EqualTo("0:00"));
			Assert.That(PlotterStrings.MinutesAndSeconds(359.6), Is.EqualTo("6:00"));
			Assert.That(PlotterStrings.MinutesAndSeconds(-1), Is.EqualTo("PAST"));
			Assert.That(PlotterStrings.MinutesAndSeconds(3600), Is.EqualTo("> 60:00"));
			Assert.That(PlotterStrings.MinutesAndSeconds(3599.4), Is.EqualTo("59:59"));
			Assert.That(PlotterStrings.MinutesAndSeconds(3599.5), Is.EqualTo("> 60:00"), "rounds up past the last table entry");
			Assert.That(PlotterStrings.MinutesAndSeconds(double.PositiveInfinity), Is.EqualTo("---"));
		}

		// A nearly parallel target gives a huge finite TCPA; casting it to int overflowed to a negative index (harbor-e2e, Astoria).
		[TestCase(2147483648.0)]
		[TestCase(1e12)]
		[TestCase(double.MaxValue)]
		public void HugeFiniteTcpaIsOverAnHour(double seconds)
		{
			Assert.That(PlotterStrings.MinutesAndSeconds(seconds), Is.EqualTo("> 60:00"));
		}

		[Test]
		public void Span()
		{
			Assert.That(PlotterStrings.Span(0.25), Is.EqualTo("0.25 NM"));
			Assert.That(PlotterStrings.Span(0.5), Is.EqualTo("0.5 NM"));
			Assert.That(PlotterStrings.Span(2), Is.EqualTo("2 NM"));
		}
	}
}
