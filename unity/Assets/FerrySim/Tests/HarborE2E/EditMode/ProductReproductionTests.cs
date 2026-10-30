using System;
using NUnit.Framework;

namespace FerrySim.HarborE2E.Tests
{
	/// <summary>Explicit desired-behavior repros for product defects found by the installed journeys.</summary>
	public class ProductReproductionTests
	{
		[TestCase(2147483648.0)]
		[TestCase(1e12)]
		[TestCase(double.MaxValue)]
		[Explicit("Observed player exception in PlotterStrings.MinutesAndSeconds; product fix belongs to the coordinator")]
		public void LargeFiniteTcpaShowsOverAnHour(double seconds)
		{
			// Reflection keeps this evidence assembly independent of the product's formatting implementation.
			var type=Type.GetType("FerrySim.Chartplotter.PlotterStrings, FerrySim.Chartplotter",true);
			var method=type.GetMethod("MinutesAndSeconds");
			var value=method.Invoke(null,new object[] {seconds});
			Assert.That(value,Is.EqualTo("> 60:00"));
		}
	}
}
