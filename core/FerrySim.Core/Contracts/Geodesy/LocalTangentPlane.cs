using System;

namespace FerrySim.Core.Contracts.Geodesy
{
	/// <summary>A WGS84 latitude and longitude in decimal degrees (the one place the core uses degrees).</summary>
	public readonly struct GeodeticPosition
	{
		public GeodeticPosition(double latitudeDegrees, double longitudeDegrees)
		{
			LatitudeDegrees = latitudeDegrees;
			LongitudeDegrees = longitudeDegrees;
		}

		public double LatitudeDegrees { get; }

		public double LongitudeDegrees { get; }

		public override string ToString() => $"({LatitudeDegrees:F7}, {LongitudeDegrees:F7})";
	}

	/// <summary>The shared world origin near The Battery. Every subsystem uses this exact point.</summary>
	public static class WorldOrigin
	{
		public const double LatitudeDegrees = 40.7000;

		public const double LongitudeDegrees = -74.0150;

		public static GeodeticPosition Position => new GeodeticPosition(LatitudeDegrees, LongitudeDegrees);
	}

	/// <summary>
	/// Converts between WGS84 geodetic coordinates and a local north-east-down tangent plane through
	/// ECEF. The simulation world is the horizontal (north, east) part of this plane for points on
	/// the ellipsoid surface. Iteration counts are fixed so results are deterministic.
	/// </summary>
	public sealed class LocalTangentPlane
	{
		private const double SemiMajorAxis = 6378137.0;
		private const double Flattening = 1.0 / 298.257223563;
		private const double EccentricitySquared = Flattening * (2.0 - Flattening);
		private const int GeodeticIterations = 6;
		private const int SurfaceIterations = 4;

		private readonly double _originX;
		private readonly double _originY;
		private readonly double _originZ;
		private readonly double _sinLat;
		private readonly double _cosLat;
		private readonly double _sinLon;
		private readonly double _cosLon;

		public LocalTangentPlane(GeodeticPosition origin)
		{
			Origin = origin;
			ToEcef(origin.LatitudeDegrees, origin.LongitudeDegrees, 0.0, out _originX, out _originY, out _originZ);
			var lat = Angles.ToRadians(origin.LatitudeDegrees);
			var lon = Angles.ToRadians(origin.LongitudeDegrees);
			_sinLat = Math.Sin(lat);
			_cosLat = Math.Cos(lat);
			_sinLon = Math.Sin(lon);
			_cosLon = Math.Cos(lon);
		}

		/// <summary>The plane through <see cref="WorldOrigin"/> used by the whole simulator.</summary>
		public static LocalTangentPlane World { get; } = new LocalTangentPlane(WorldOrigin.Position);

		public GeodeticPosition Origin { get; }

		/// <summary>North-east-down offset of a geodetic point at an ellipsoidal height, metres.</summary>
		public void ToNed(GeodeticPosition position, double ellipsoidalHeight, out double north, out double east, out double down)
		{
			ToEcef(position.LatitudeDegrees, position.LongitudeDegrees, ellipsoidalHeight, out var x, out var y, out var z);
			var dx = x - _originX;
			var dy = y - _originY;
			var dz = z - _originZ;
			north = -_sinLat * _cosLon * dx - _sinLat * _sinLon * dy + _cosLat * dz;
			east = -_sinLon * dx + _cosLon * dy;
			down = -_cosLat * _cosLon * dx - _cosLat * _sinLon * dy - _sinLat * dz;
		}

		/// <summary>Simulation world position (north, east) of a point on the ellipsoid surface.</summary>
		public HorizontalVector ToLocal(GeodeticPosition position)
		{
			ToNed(position, 0.0, out var north, out var east, out _);
			return new HorizontalVector(north, east);
		}

		/// <summary>The ellipsoid-surface point whose world position is (north, east).</summary>
		public GeodeticPosition ToGeodetic(double north, double east) => ToGeodetic(north, east, out _);

		/// <summary>
		/// The ellipsoid-surface point whose world position is (north, east), and how far below the
		/// tangent plane that point lies (Earth curvature, metres).
		/// </summary>
		public GeodeticPosition ToGeodetic(double north, double east, out double down)
		{
			// Slide along the plane's down axis until the point reaches the ellipsoid surface.
			down = 0.0;
			var latitude = Origin.LatitudeDegrees;
			var longitude = Origin.LongitudeDegrees;
			for (var i = 0; i < SurfaceIterations; i++)
			{
				var x = _originX - _sinLat * _cosLon * north - _sinLon * east - _cosLat * _cosLon * down;
				var y = _originY - _sinLat * _sinLon * north + _cosLon * east - _cosLat * _sinLon * down;
				var z = _originZ + _cosLat * north - _sinLat * down;
				ToGeodetic(x, y, z, out latitude, out longitude, out var height);
				down += height;
			}

			return new GeodeticPosition(latitude, longitude);
		}

		private static void ToEcef(double latitudeDegrees, double longitudeDegrees, double height, out double x, out double y, out double z)
		{
			var lat = Angles.ToRadians(latitudeDegrees);
			var lon = Angles.ToRadians(longitudeDegrees);
			var sinLat = Math.Sin(lat);
			var cosLat = Math.Cos(lat);
			var primeVertical = SemiMajorAxis / Math.Sqrt(1.0 - EccentricitySquared * sinLat * sinLat);
			x = (primeVertical + height) * cosLat * Math.Cos(lon);
			y = (primeVertical + height) * cosLat * Math.Sin(lon);
			z = (primeVertical * (1.0 - EccentricitySquared) + height) * sinLat;
		}

		private static void ToGeodetic(double x, double y, double z, out double latitudeDegrees, out double longitudeDegrees, out double height)
		{
			var p = Math.Sqrt(x * x + y * y);
			var lon = Math.Atan2(y, x);
			var lat = Math.Atan2(z, p * (1.0 - EccentricitySquared));
			height = 0.0;
			for (var i = 0; i < GeodeticIterations; i++)
			{
				var sinLat = Math.Sin(lat);
				var primeVertical = SemiMajorAxis / Math.Sqrt(1.0 - EccentricitySquared * sinLat * sinLat);
				height = p / Math.Cos(lat) - primeVertical;
				lat = Math.Atan2(z, p * (1.0 - EccentricitySquared * primeVertical / (primeVertical + height)));
			}

			latitudeDegrees = Angles.ToDegrees(lat);
			longitudeDegrees = Angles.ToDegrees(lon);
		}
	}
}
