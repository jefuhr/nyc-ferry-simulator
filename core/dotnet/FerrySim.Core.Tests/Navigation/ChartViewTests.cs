using FerrySim.Core.Navigation;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: a flipped axis (north drawn downward or east to the left), a wrong rotation sense in
	// course-up, scale errors, and a projection that does not invert.
	public class ChartViewTests
	{
		[Test]
		public void NorthUp_NorthIsUpAndEastIsRight()
		{
			var view = new ChartView(1000.0, 2000.0, 2.0, 0.0, 400.0, 300.0);
			view.ToScreen(1100.0, 2000.0, out var x, out var y);
			Assert.That(x, Is.EqualTo(400.0).Within(1e-9));
			Assert.That(y, Is.EqualTo(250.0).Within(1e-9), "100 m north is 50 px up at 2 m per px");
			view.ToScreen(1000.0, 2050.0, out x, out y);
			Assert.That(x, Is.EqualTo(425.0).Within(1e-9), "50 m east is 25 px right");
			Assert.That(y, Is.EqualTo(300.0).Within(1e-9));
		}

		[Test]
		public void CourseUp_TheUpBearingPointsUp_AndStarboardIsRight()
		{
			var east = System.Math.PI / 2.0;
			var view = new ChartView(0.0, 0.0, 1.0, east, 100.0, 100.0);
			view.ToScreen(0.0, 80.0, out var x, out var y);
			Assert.That(x, Is.EqualTo(100.0).Within(1e-9), "a point due east is straight ahead");
			Assert.That(y, Is.EqualTo(20.0).Within(1e-9));
			view.ToScreen(-30.0, 0.0, out x, out y);
			Assert.That(x, Is.EqualTo(130.0).Within(1e-9), "heading east, south is to starboard (right)");
			Assert.That(y, Is.EqualTo(100.0).Within(1e-9));
		}

		[Test]
		public void ToWorld_InvertsToScreen_AtAnyRotation()
		{
			foreach (var bearing in new[] { 0.0, 0.7, 2.9, 4.4 })
			{
				var view = new ChartView(-5000.0, 3000.0, 0.37, bearing, 640.0, 360.0);
				view.ToScreen(-4321.0, 2345.0, out var x, out var y);
				view.ToWorld(x, y, out var north, out var east);
				Assert.That(north, Is.EqualTo(-4321.0).Within(1e-6));
				Assert.That(east, Is.EqualTo(2345.0).Within(1e-6));
			}
		}

		[Test]
		public void Scale_OneNauticalMileAcross1852Pixels_IsOneMetrePerPixel()
		{
			Assert.That(ChartView.MetresPerPixelFor(1.0, 1852.0), Is.EqualTo(1.0).Within(1e-12));
			Assert.That(ChartView.MetresPerPixelFor(4.0, 926.0), Is.EqualTo(8.0).Within(1e-12));
			Assert.That(() => ChartView.MetresPerPixelFor(0.0, 100.0), Throws.ArgumentException);
			Assert.That(() => new ChartView(0, 0, 0, 0, 0, 0), Throws.ArgumentException);
		}

		[Test]
		public void Direction_FollowsTheRotation()
		{
			var view = new ChartView(0, 0, 1, 0, 0, 0);
			view.Direction(0.0, out var dx, out var dy);
			Assert.That(dx, Is.EqualTo(0.0).Within(1e-12));
			Assert.That(dy, Is.EqualTo(-1.0).Within(1e-12), "north points up (negative y)");
			var courseUp = new ChartView(0, 0, 1, System.Math.PI, 0, 0);
			courseUp.Direction(System.Math.PI, out dx, out dy);
			Assert.That(dy, Is.EqualTo(-1.0).Within(1e-12), "heading south, course-up: south is up");
		}

		[Test]
		public void WorldBounds_CoverTheRotatedScreen()
		{
			var view = new ChartView(0, 0, 1, System.Math.PI / 4.0, 50, 50);
			view.WorldBounds(0, 0, 100, 100, out var minNorth, out var minEast, out var maxNorth, out var maxEast);
			var half = 50.0 * System.Math.Sqrt(2.0);
			Assert.That(minNorth, Is.EqualTo(-half).Within(1e-9));
			Assert.That(maxNorth, Is.EqualTo(half).Within(1e-9));
			Assert.That(minEast, Is.EqualTo(-half).Within(1e-9));
			Assert.That(maxEast, Is.EqualTo(half).Within(1e-9));
		}
	}
}
