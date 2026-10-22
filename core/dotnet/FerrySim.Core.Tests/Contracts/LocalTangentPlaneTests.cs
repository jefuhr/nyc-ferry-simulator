using FerrySim.Core.Contracts.Geodesy;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Contracts
{
	/// <summary>
	/// Reference values come from an independent Python implementation of the same WGS84 ECEF
	/// tangent-plane math (documented in docs/conventions.md) so the C# and pipeline sides agree.
	/// </summary>
	public class LocalTangentPlaneTests
	{
		[Test]
		public void Origin_MapsToZero()
		{
			var local = LocalTangentPlane.World.ToLocal(WorldOrigin.Position);
			Assert.That(local.North, Is.EqualTo(0.0).Within(1e-6));
			Assert.That(local.East, Is.EqualTo(0.0).Within(1e-6));
		}

		[TestCase(40.7100, -74.0150, 1110.4821, 0.0000, 0.0969)]
		[TestCase(40.7000, -74.0050, 0.0481, 845.1551, 0.0559)]
		[TestCase(40.5800, -73.8200, -13307.3042, 16510.0303, 35.2544)]
		[TestCase(40.8100, -73.8600, 12226.9375, 13078.3146, 25.1375)]
		public void ToNed_MatchesPythonReference(double latitude, double longitude, double north, double east, double down)
		{
			LocalTangentPlane.World.ToNed(new GeodeticPosition(latitude, longitude), 0.0, out var n, out var e, out var d);
			Assert.That(n, Is.EqualTo(north).Within(1e-3));
			Assert.That(e, Is.EqualTo(east).Within(1e-3));
			Assert.That(d, Is.EqualTo(down).Within(1e-3));
		}

		[TestCase(40.5800, -73.8200)]
		[TestCase(40.8100, -73.8600)]
		[TestCase(40.6430, -74.0740)]
		public void ToGeodetic_RoundTripsThroughLocal(double latitude, double longitude)
		{
			var local = LocalTangentPlane.World.ToLocal(new GeodeticPosition(latitude, longitude));
			var back = LocalTangentPlane.World.ToGeodetic(local.North, local.East, out var down);
			Assert.That(back.LatitudeDegrees, Is.EqualTo(latitude).Within(1e-9));
			Assert.That(back.LongitudeDegrees, Is.EqualTo(longitude).Within(1e-9));
			Assert.That(down, Is.GreaterThan(0.0), "points away from the origin lie below the tangent plane");
		}
	}
}
