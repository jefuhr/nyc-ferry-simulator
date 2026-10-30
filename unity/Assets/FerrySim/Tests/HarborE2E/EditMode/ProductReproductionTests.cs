using System;
using NUnit.Framework;

namespace FerrySim.HarborE2E.Tests
{
	/// <summary>Regression repros for product defects found by the installed journeys; they run in the ordinary gate once fixed.</summary>
	public class ProductReproductionTests
	{
		// Found as a player exception in PlotterStrings.MinutesAndSeconds on the Astoria journey; fixed by comparing before the int cast.
		[TestCase(2147483648.0)]
		[TestCase(1e12)]
		[TestCase(double.MaxValue)]
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
