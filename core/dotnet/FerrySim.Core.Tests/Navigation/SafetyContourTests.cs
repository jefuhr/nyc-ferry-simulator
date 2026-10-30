using FerrySim.Core.Navigation;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: a safety contour drawn shallower than requested, an area at exactly the safety depth shaded unsafe,
	// drying areas shaded as water, and a request deeper than any charted contour giving nothing.
	public class SafetyContourTests
	{
		private static readonly double[] Charted = { 2.0, 5.0, 10.0, 20.0 };

		[Test]
		public void Select_TakesTheNextDeeperChartedContour()
		{
			Assert.That(SafetyContour.Select(Charted, 3.0), Is.EqualTo(5.0));
			Assert.That(SafetyContour.Select(Charted, 5.0), Is.EqualTo(5.0), "an exact charted depth is itself");
			Assert.That(SafetyContour.Select(Charted, 25.0), Is.EqualTo(20.0), "deeper than any: the deepest charted");
			Assert.That(SafetyContour.Select(new double[0], 3.0), Is.NaN);
			Assert.That(SafetyContour.Select(Charted, double.NaN), Is.NaN);
		}

		[Test]
		public void DeepContour_IsTwiceTheSafetyOrThreeMetresDeeper_FromTheChart()
		{
			Assert.That(SafetyContour.DeepContour(Charted, 5.0), Is.EqualTo(10.0));
			Assert.That(SafetyContour.DeepContour(Charted, 2.0), Is.EqualTo(5.0), "2 + 3 = 5");
			Assert.That(SafetyContour.DeepContour(Charted, 20.0), Is.EqualTo(20.0), "nothing deeper: the safety contour itself");
		}

		[Test]
		public void Classify_BandsAgainstTheContours()
		{
			Assert.That(SafetyContour.Classify(-0.5, 5.0, 10.0), Is.EqualTo(DepthBand.Drying));
			Assert.That(SafetyContour.Classify(double.NaN, 5.0, 10.0), Is.EqualTo(DepthBand.Drying));
			Assert.That(SafetyContour.Classify(0.0, 5.0, 10.0), Is.EqualTo(DepthBand.Unsafe));
			Assert.That(SafetyContour.Classify(4.99, 5.0, 10.0), Is.EqualTo(DepthBand.Unsafe));
			Assert.That(SafetyContour.Classify(5.0, 5.0, 10.0), Is.EqualTo(DepthBand.Caution), "at the safety contour is on the safe side");
			Assert.That(SafetyContour.Classify(10.0, 5.0, 10.0), Is.EqualTo(DepthBand.Deep));
			Assert.That(SafetyContour.Classify(7.0, double.NaN, double.NaN), Is.EqualTo(DepthBand.Caution), "no contours: neither emphasised nor deep");
		}
	}
}
