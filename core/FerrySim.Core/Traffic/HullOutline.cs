using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;

namespace FerrySim.Core.Traffic
{
	/// <summary>
	/// The learner's hull in plan, for collision with traffic: the vessel file's contact outline (the fender line) as one closed
	/// ring, body axes (x forward, y starboard, metres from the reference point). The ring runs the starboard sheer aft to
	/// forward, the bow fender line starboard to port, the port sheer forward to aft and the stern fender line port to starboard,
	/// as the slice's start clearance check walks it. <see cref="Overlaps"/> tests it against a traffic hull's rectangle.
	/// Allocation-free after construction; not thread-safe (one scratch buffer).
	/// </summary>
	public sealed class HullOutline
	{
		private readonly double[] _x;
		private readonly double[] _y;
		private readonly double[] _u;
		private readonly double[] _v;

		/// <summary>A ring of at least three body points, not repeating the first at the end.</summary>
		public HullOutline(IReadOnlyList<double> x, IReadOnlyList<double> y)
		{
			if (x == null || y == null || x.Count != y.Count || x.Count < 3)
			{
				throw new ArgumentException("A hull outline needs at least three points, each with an x and a y.");
			}

			_x = new double[x.Count];
			_y = new double[x.Count];
			for (var i = 0; i < _x.Length; i++)
			{
				if (double.IsNaN(x[i]) || double.IsInfinity(x[i]) || double.IsNaN(y[i]) || double.IsInfinity(y[i]))
				{
					throw new ArgumentException($"Hull outline point {i} is not finite.");
				}

				_x[i] = x[i];
				_y[i] = y[i];
				Radius = Math.Max(Radius, Math.Sqrt(x[i] * x[i] + y[i] * y[i]));
			}

			_u = new double[_x.Length];
			_v = new double[_x.Length];
		}

		/// <summary>Points in the ring.</summary>
		public int Count => _x.Length;

		/// <summary>Largest distance of a point from the reference point, m.</summary>
		public double Radius { get; }

		public double X(int index) => _x[index];

		public double Y(int index) => _y[index];

		/// <summary>The ring from a vessel file's <c>contact</c> section. Throws <see cref="ArgumentException"/> without one.</summary>
		public static HullOutline FromContact(VesselParameters vessel)
		{
			var contact = vessel?.Contact ?? throw new ArgumentException("The vessel file has no contact section, so it has no hull outline to collide with traffic.", nameof(vessel));
			var x = new List<double>();
			var y = new List<double>();
			Append(x, y, contact.StarboardDemihull, false, "contact.starboardDemihull");
			Append(x, y, contact.BowFenderLine, true, "contact.bowFenderLine");
			Append(x, y, contact.PortDemihull, true, "contact.portDemihull");
			Append(x, y, contact.SternFenderLine, false, "contact.sternFenderLine");
			if (x.Count > 1 && Same(x[0], y[0], x[x.Count - 1], y[y.Count - 1]))
			{
				x.RemoveAt(x.Count - 1);
				y.RemoveAt(y.Count - 1);
			}

			return new HullOutline(x, y);
		}

		/// <summary>
		/// True when the hull at (<paramref name="north"/>, <paramref name="east"/>, <paramref name="heading"/>) and a traffic
		/// hull's rectangle (<paramref name="length"/> by <paramref name="beam"/>, centred on its position, along its heading)
		/// overlap or touch.
		/// </summary>
		public bool Overlaps(double north, double east, double heading, double otherNorth, double otherEast, double otherHeading, double length, double beam)
		{
			var a = 0.5 * length;
			var b = 0.5 * beam;
			var dn = north - otherNorth;
			var de = east - otherEast;
			var reach = Radius + Math.Sqrt(a * a + b * b);
			if (dn * dn + de * de > reach * reach)
			{
				return false;
			}

			// The ring in the rectangle's own axes: u along its heading, v to its starboard.
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var otherCos = Math.Cos(otherHeading);
			var otherSin = Math.Sin(otherHeading);
			for (var i = 0; i < _x.Length; i++)
			{
				var n = dn + _x[i] * cos - _y[i] * sin;
				var e = de + _x[i] * sin + _y[i] * cos;
				_u[i] = n * otherCos + e * otherSin;
				_v[i] = -n * otherSin + e * otherCos;
			}

			for (var i = 0; i < _u.Length; i++)
			{
				var j = i + 1 == _u.Length ? 0 : i + 1;
				if (SegmentMeetsBox(_u[i], _v[i], _u[j], _v[j], a, b))
				{
					return true;
				}
			}

			// No edge meets the rectangle: it overlaps only if it lies wholly inside the ring.
			return Inside(0.0, 0.0);
		}

		/// <summary>Whether a segment meets the box [−a, a] × [−b, b], boundary included (Liang–Barsky clipping).</summary>
		private static bool SegmentMeetsBox(double u0, double v0, double u1, double v1, double a, double b)
		{
			var du = u1 - u0;
			var dv = v1 - v0;
			var low = 0.0;
			var high = 1.0;
			return Clip(-du, u0 + a, ref low, ref high) && Clip(du, a - u0, ref low, ref high) &&
				Clip(-dv, v0 + b, ref low, ref high) && Clip(dv, b - v0, ref low, ref high);
		}

		private static bool Clip(double p, double q, ref double low, ref double high)
		{
			if (p == 0.0)
			{
				return q >= 0.0;
			}

			var t = q / p;
			if (p < 0.0)
			{
				if (t > high)
				{
					return false;
				}

				if (t > low)
				{
					low = t;
				}
			}
			else
			{
				if (t < low)
				{
					return false;
				}

				if (t < high)
				{
					high = t;
				}
			}

			return true;
		}

		/// <summary>Crossing-number test of a point against the ring in the rectangle's axes.</summary>
		private bool Inside(double u, double v)
		{
			var inside = false;
			for (int i = 0, j = _u.Length - 1; i < _u.Length; j = i++)
			{
				if ((_v[i] > v) != (_v[j] > v) && u < (_u[j] - _u[i]) * (v - _v[i]) / (_v[j] - _v[i]) + _u[i])
				{
					inside = !inside;
				}
			}

			return inside;
		}

		private static void Append(List<double> x, List<double> y, List<OutlinePoint> part, bool reversed, string name)
		{
			if (part == null || part.Count < 2)
			{
				throw new ArgumentException($"{name} needs at least two points.");
			}

			for (var k = 0; k < part.Count; k++)
			{
				var point = part[reversed ? part.Count - 1 - k : k];
				if (point?.X == null || point.Y == null)
				{
					throw new ArgumentException($"{name} has a point without x or y.");
				}

				var px = point.X.Value;
				var py = point.Y.Value;
				if (x.Count > 0 && Same(x[x.Count - 1], y[y.Count - 1], px, py))
				{
					continue;
				}

				x.Add(px);
				y.Add(py);
			}
		}

		private static bool Same(double ax, double ay, double bx, double by) => Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
	}
}
