using System;

namespace FerrySim.Core.Navigation
{
	/// <summary>
	/// The mapping between world north/east metres and a display's pixels: a world centre point at a screen
	/// centre point, a scale, and the world bearing that points up the screen (0 for north-up, the own course
	/// for course-up). Screen x grows to the right and y grows downward, as UI Toolkit lays things out.
	/// Pure arithmetic in doubles: the same view and the same world point always give the same pixel.
	/// </summary>
	public readonly struct ChartView
	{
		/// <summary>Metres in one nautical mile.</summary>
		public const double MetresPerNauticalMile = 1852.0;

		/// <summary>Metres in one cable (a tenth of a nautical mile).</summary>
		public const double MetresPerCable = 185.2;

		public ChartView(double centreNorth, double centreEast, double metresPerPixel, double upBearing, double screenCentreX, double screenCentreY)
		{
			if (!(metresPerPixel > 0.0) || double.IsInfinity(metresPerPixel))
			{
				throw new ArgumentException("A chart view needs a positive, finite scale.");
			}

			CentreNorth = centreNorth;
			CentreEast = centreEast;
			MetresPerPixel = metresPerPixel;
			UpBearing = upBearing;
			ScreenCentreX = screenCentreX;
			ScreenCentreY = screenCentreY;
			_cos = Math.Cos(upBearing);
			_sin = Math.Sin(upBearing);
		}

		private readonly double _cos;
		private readonly double _sin;

		/// <summary>The world point drawn at the screen centre, m north.</summary>
		public double CentreNorth { get; }

		/// <summary>The world point drawn at the screen centre, m east.</summary>
		public double CentreEast { get; }

		/// <summary>Scale: world metres per screen pixel.</summary>
		public double MetresPerPixel { get; }

		/// <summary>Pixels per world metre.</summary>
		public double PixelsPerMetre => 1.0 / MetresPerPixel;

		/// <summary>The world bearing that points up the screen, rad clockwise from north. 0 is north-up.</summary>
		public double UpBearing { get; }

		public double ScreenCentreX { get; }

		public double ScreenCentreY { get; }

		/// <summary>The scale for a view that shows <paramref name="spanNauticalMiles"/> across <paramref name="widthPixels"/>.</summary>
		public static double MetresPerPixelFor(double spanNauticalMiles, double widthPixels)
		{
			if (!(spanNauticalMiles > 0.0) || !(widthPixels > 0.0))
			{
				throw new ArgumentException("A span and a width must be positive.");
			}

			return spanNauticalMiles * MetresPerNauticalMile / widthPixels;
		}

		/// <summary>Where a world point lands on the screen.</summary>
		public void ToScreen(double north, double east, out double x, out double y)
		{
			var dn = north - CentreNorth;
			var de = east - CentreEast;
			// Rotate the world offset so the up bearing points up: forward along the bearing, right across it.
			var forward = dn * _cos + de * _sin;
			var right = de * _cos - dn * _sin;
			x = ScreenCentreX + right / MetresPerPixel;
			y = ScreenCentreY - forward / MetresPerPixel;
		}

		/// <summary>The world point under a screen pixel.</summary>
		public void ToWorld(double x, double y, out double north, out double east)
		{
			var right = (x - ScreenCentreX) * MetresPerPixel;
			var forward = (ScreenCentreY - y) * MetresPerPixel;
			north = CentreNorth + forward * _cos - right * _sin;
			east = CentreEast + forward * _sin + right * _cos;
		}

		/// <summary>The screen direction (unit vector, y down) of a world bearing in this view.</summary>
		public void Direction(double bearing, out double dx, out double dy)
		{
			var relative = bearing - UpBearing;
			dx = Math.Sin(relative);
			dy = -Math.Cos(relative);
		}

		/// <summary>The same view with another centre.</summary>
		public ChartView WithCentre(double north, double east) => new ChartView(north, east, MetresPerPixel, UpBearing, ScreenCentreX, ScreenCentreY);

		/// <summary>The world box covering a screen rectangle, whatever the rotation.</summary>
		public void WorldBounds(double left, double top, double right, double bottom, out double minNorth, out double minEast, out double maxNorth, out double maxEast)
		{
			minNorth = double.PositiveInfinity;
			minEast = double.PositiveInfinity;
			maxNorth = double.NegativeInfinity;
			maxEast = double.NegativeInfinity;
			Corner(left, top, ref minNorth, ref minEast, ref maxNorth, ref maxEast);
			Corner(right, top, ref minNorth, ref minEast, ref maxNorth, ref maxEast);
			Corner(left, bottom, ref minNorth, ref minEast, ref maxNorth, ref maxEast);
			Corner(right, bottom, ref minNorth, ref minEast, ref maxNorth, ref maxEast);
		}

		private void Corner(double x, double y, ref double minNorth, ref double minEast, ref double maxNorth, ref double maxEast)
		{
			ToWorld(x, y, out var north, out var east);
			minNorth = Math.Min(minNorth, north);
			minEast = Math.Min(minEast, east);
			maxNorth = Math.Max(maxNorth, north);
			maxEast = Math.Max(maxEast, east);
		}
	}
}
