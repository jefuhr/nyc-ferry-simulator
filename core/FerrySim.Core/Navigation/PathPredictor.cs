using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Navigation
{
	/// <summary>Display-only constant-speed, constant-yaw-rate ground track; no force or current integration.</summary>
	public static class PathPredictor
	{
		/// <summary>Fills caller-owned points, including the initial position and the horizon endpoint.</summary>
		public static void Sample(HorizontalVector position, double speedOverGround, double courseOverGround,
			double yawRate, double horizonSeconds, Span<HorizontalVector> points)
		{
			if (points.Length < 2) throw new ArgumentException("At least two predictor points are required.", nameof(points));
			if (!Finite(position.North) || !Finite(position.East)) throw new ArgumentOutOfRangeException(nameof(position));
			if (!Finite(speedOverGround) || speedOverGround < 0) throw new ArgumentOutOfRangeException(nameof(speedOverGround));
			if (!Finite(courseOverGround)) throw new ArgumentOutOfRangeException(nameof(courseOverGround));
			if (!Finite(yawRate)) throw new ArgumentOutOfRangeException(nameof(yawRate));
			if (!Finite(horizonSeconds) || horizonSeconds < 0) throw new ArgumentOutOfRangeException(nameof(horizonSeconds));
			points[0] = position;
			for (var i = 1; i < points.Length; i++)
			{
				var time = horizonSeconds * (i / (double)(points.Length - 1));
				var halfAngle = .5 * yawRate * time;
				// Midpoint bearing times sinc avoids subtracting two nearly equal trig values at small rates.
				var sinc = Math.Abs(halfAngle) < 1e-6 ? 1 - halfAngle * halfAngle / 6 : Math.Sin(halfAngle) / halfAngle;
				var distance = speedOverGround * time * sinc;
				var bearing = courseOverGround + halfAngle;
				points[i] = new HorizontalVector(position.North + distance * Math.Cos(bearing), position.East + distance * Math.Sin(bearing));
			}
		}

		private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}
}
