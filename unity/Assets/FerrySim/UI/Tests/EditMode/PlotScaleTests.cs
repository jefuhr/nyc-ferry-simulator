using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Debrief plot scaling. Guards against: a flat series (zero span) dividing by zero, NaN in a series
	/// poisoning the range, axis ranges that cut off data, tick values like 0.30000000000000004, inverted
	/// pixel mapping, a track plot that is stretched (metres per pixel different on each axis), a heading
	/// trace drawing a line across the whole plot when it wraps through north, and decimation dropping
	/// the peaks a learner needs to see.
	/// </summary>
	public class PlotScaleTests
	{
		[TestCase(10.0, 5, 2.0)]
		[TestCase(1.0, 5, 0.2)]
		[TestCase(7.3, 5, 2.0)]
		[TestCase(360.0, 4, 100.0)]
		[TestCase(0.03, 3, 0.01)]
		[TestCase(25000.0, 5, 5000.0)]
		[TestCase(3.0, 6, 0.5)]
		public void NiceStep_FollowsOneTwoFive(double span, int ticks, double expected)
		{
			Assert.That(PlotScale.NiceStep(span, ticks), Is.EqualTo(expected).Within(expected * 1e-9));
		}

		[Test]
		public void NiceStep_SurvivesNonsense()
		{
			Assert.That(PlotScale.NiceStep(0.0, 5), Is.EqualTo(1.0));
			Assert.That(PlotScale.NiceStep(double.NaN, 5), Is.EqualTo(1.0));
			Assert.That(PlotScale.NiceStep(-3.0, 5), Is.EqualTo(1.0));
			Assert.That(PlotScale.NiceStep(10.0, 0), Is.EqualTo(1.0));
		}

		[Test]
		public void NiceRange_CoversTheDataOnWholeSteps()
		{
			var range = PlotScale.NiceRange(-7.4, 24.99, 5, out var step);

			Assert.That(step, Is.EqualTo(10.0));
			Assert.That(range.Min, Is.EqualTo(-10.0));
			Assert.That(range.Max, Is.EqualTo(30.0));
		}

		[Test]
		public void NiceRange_KeepsValuesThatAreAlreadyOnAStep()
		{
			var range = PlotScale.NiceRange(0.0, 20.0, 4, out var step);

			Assert.That(step, Is.EqualTo(5.0));
			Assert.That(range.Min, Is.EqualTo(0.0));
			Assert.That(range.Max, Is.EqualTo(20.0));
		}

		[Test]
		public void NiceRange_WidensAFlatSeries()
		{
			var depth = PlotScale.NiceRange(48.1, 48.1, 4, out _);
			var zero = PlotScale.NiceRange(0.0, 0.0, 4, out _);

			Assert.That(depth.Min, Is.LessThan(48.1));
			Assert.That(depth.Max, Is.GreaterThan(48.1));
			Assert.That(zero.Min, Is.LessThan(0.0));
			Assert.That(zero.Max, Is.GreaterThan(0.0));
		}

		[Test]
		public void NiceRange_FallsBackWhenThereIsNoData()
		{
			var range = PlotScale.NiceRange(double.NaN, double.NaN, 4, out var step);

			Assert.That(range.Min, Is.EqualTo(0.0));
			Assert.That(range.Max, Is.EqualTo(1.0));
			Assert.That(step, Is.GreaterThan(0.0));
		}

		[Test]
		public void DataRange_IgnoresNaN()
		{
			var range = PlotScale.DataRange(new[] { new[] { 3.0, double.NaN, -2.0 }, new[] { 7.5, double.PositiveInfinity } });

			Assert.That(range.Min, Is.EqualTo(-2.0));
			Assert.That(range.Max, Is.EqualTo(7.5));
		}

		[Test]
		public void Ticks_AreCleanMultiplesInsideTheRange()
		{
			var ticks = PlotScale.Ticks(new AxisRange(0.0, 1.0), 0.1);

			Assert.That(ticks.Count, Is.EqualTo(11));
			Assert.That(ticks[3], Is.EqualTo(0.3), "no 0.30000000000000004");
			Assert.That(ticks.First(), Is.EqualTo(0.0));
			Assert.That(ticks.Last(), Is.EqualTo(1.0));
			Assert.That(PlotScale.Ticks(new AxisRange(-7.0, 23.0), 10.0), Is.EqualTo(new[] { 0.0, 10.0, 20.0 }));
		}

		[TestCase(5.0, 0)]
		[TestCase(0.5, 1)]
		[TestCase(0.2, 1)]
		[TestCase(0.05, 2)]
		[TestCase(1000.0, 0)]
		public void TickDecimals_MatchTheStep(double step, int expected)
		{
			Assert.That(PlotScale.TickDecimals(step), Is.EqualTo(expected));
		}

		[Test]
		public void PixelMapping_IsLinearInvertibleAndCanFlipY()
		{
			var range = new AxisRange(-10.0, 30.0);

			Assert.That(PlotScale.ToPixel(-10.0, range, 0f, 400f), Is.EqualTo(0f));
			Assert.That(PlotScale.ToPixel(30.0, range, 0f, 400f), Is.EqualTo(400f));
			Assert.That(PlotScale.ToPixel(10.0, range, 0f, 400f), Is.EqualTo(200f));
			// Screen y grows downward, so a y axis maps its minimum to the bottom pixel.
			Assert.That(PlotScale.ToPixel(-10.0, range, 300f, 0f), Is.EqualTo(300f));
			Assert.That(PlotScale.FromPixel(PlotScale.ToPixel(12.34, range, 300f, 0f), range, 300f, 0f), Is.EqualTo(12.34).Within(1e-4));
			Assert.That(PlotScale.ToPixel(5.0, new AxisRange(5.0, 5.0), 0f, 100f), Is.EqualTo(50f), "zero span maps to the middle");
		}

		[Test]
		public void EqualAspect_KeepsOneMetrePerPixelBothWaysAndContainsTheTrack()
		{
			var east = new AxisRange(0.0, 100.0);
			var north = new AxisRange(0.0, 500.0);

			PlotScale.EqualAspect(east, north, 400f, 300f, out var x, out var y);

			Assert.That(x.Span / 400.0, Is.EqualTo(y.Span / 300.0).Within(1e-9));
			Assert.That(x.Min, Is.LessThanOrEqualTo(0.0));
			Assert.That(x.Max, Is.GreaterThanOrEqualTo(100.0));
			Assert.That(y.Min, Is.LessThanOrEqualTo(0.0));
			Assert.That(y.Max, Is.GreaterThanOrEqualTo(500.0));
			Assert.That((x.Min + x.Max) / 2.0, Is.EqualTo(50.0).Within(1e-9), "the track stays centred");
		}

		[Test]
		public void EqualAspect_HandlesAPointTrack()
		{
			PlotScale.EqualAspect(new AxisRange(3.0, 3.0), new AxisRange(4.0, 4.0), 200f, 100f, out var x, out var y);

			Assert.That(x.Span, Is.GreaterThan(0.0));
			Assert.That(x.Span / 200.0, Is.EqualTo(y.Span / 100.0).Within(1e-9));
		}

		[Test]
		public void CompassRuns_BreakWhereHeadingWrapsThroughNorth()
		{
			var headings = new[] { 350.0, 355.0, 359.0, 2.0, 6.0, 4.0, 358.0 };

			var runs = PlotDecimation.CompassRuns(headings, 0, headings.Length);

			Assert.That(runs, Is.EqualTo(new[] { (0, 3), (3, 6), (6, 7) }));
		}

		[Test]
		public void Decimate_KeepsPeaksAndEndsAndCapsThePointCount()
		{
			var count = 10001;
			var x = new double[count];
			var y = new double[count];
			for (var i = 0; i < count; i++)
			{
				x[i] = i * 0.01;
				y[i] = Math.Sin(i * 0.01);
			}

			y[4321] = 9.0;
			y[7777] = -9.0;
			var points = new List<PlotPoint>();

			PlotDecimation.Decimate(x, y, 0, count, x[0], x[count - 1], 100, points);

			Assert.That(points.Count, Is.LessThanOrEqualTo(4 * 100));
			Assert.That(points.First().X, Is.EqualTo(x[0]));
			Assert.That(points.Last().X, Is.EqualTo(x[count - 1]));
			Assert.That(points.Max(p => p.Y), Is.EqualTo(9.0), "spike survives");
			Assert.That(points.Min(p => p.Y), Is.EqualTo(-9.0), "dip survives");
			for (var i = 1; i < points.Count; i++)
			{
				Assert.That(points[i].X, Is.GreaterThanOrEqualTo(points[i - 1].X), "points stay in time order");
			}
		}

		[Test]
		public void Decimate_PassesShortSeriesThroughUnchanged()
		{
			var x = new[] { 0.0, 1.0, 2.0 };
			var y = new[] { 5.0, 6.0, 4.0 };
			var points = new List<PlotPoint>();

			PlotDecimation.Decimate(x, y, 0, 3, 0.0, 2.0, 100, points);

			Assert.That(points.Select(p => p.Y), Is.EqualTo(y));
		}
	}
}
