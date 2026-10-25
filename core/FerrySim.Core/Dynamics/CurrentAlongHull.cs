using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// The current along the hull's centreline, sampled once per step at the centre of each of the hull's cross-flow strips
	/// and kept as differences from the current at the reference point (world north and east, m/s). In a uniform current
	/// every difference is exactly zero and <see cref="Varies"/> is false, so the simulation keeps the reference-point model.
	/// <para>
	/// At each Runge-Kutta stage the differences are resolved in body axes at that stage's heading and fitted with a
	/// least-squares straight line along the hull. The line's value at the reference point is a current the whole hull
	/// feels on top of the reference point's. Its slope across the hull, ∂v_c/∂x, acts on the hull like a yaw rate: water at
	/// the bow moving to starboard relative to water at the stern is, to the hull, the hull turning to port through still
	/// water. Each cross-flow strip also feels the part of its own current the line leaves out.
	/// </para>
	/// </summary>
	public sealed class CurrentAlongHull
	{
		private readonly double[] _positions;
		private readonly double[] _north;
		private readonly double[] _east;
		private readonly double[] _residual;
		private readonly double _meanPosition;
		private readonly double _spread;
		private double _interceptNorth;
		private double _interceptEast;
		private double _slopeNorth;
		private double _slopeEast;

		public CurrentAlongHull(HullModel hull)
		{
			var count = hull.StripCount;
			_positions = new double[count];
			_north = new double[count];
			_east = new double[count];
			_residual = new double[count];
			var sum = 0.0;
			for (var i = 0; i < count; i++)
			{
				_positions[i] = hull.StripPosition(i);
				sum += _positions[i];
			}

			_meanPosition = sum / count;
			for (var i = 0; i < count; i++)
			{
				var offset = _positions[i] - _meanPosition;
				_spread += offset * offset;
			}
		}

		/// <summary>Number of sample stations along the hull: one per cross-flow strip.</summary>
		public int Count => _positions.Length;

		/// <summary>True when the last samples differ anywhere from the current at the reference point.</summary>
		public bool Varies { get; private set; }

		/// <summary>Longitudinal position of station <paramref name="index"/> from midships, m.</summary>
		public double Position(int index) => _positions[index];

		/// <summary>The last sampled difference at station <paramref name="index"/>, world north and east, m/s.</summary>
		public HorizontalVector Difference(int index) => new HorizontalVector(_north[index], _east[index]);

		/// <summary>
		/// Samples the current at every station for the hull at (<paramref name="north"/>, <paramref name="east"/>) heading
		/// <paramref name="heading"/>, relative to <paramref name="reference"/>, the current at the reference point.
		/// Returns <see cref="Varies"/>.
		/// </summary>
		public bool Sample(IEnvironment environment, double north, double east, double heading, double time, HorizontalVector reference)
		{
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var varies = false;
			for (var i = 0; i < _positions.Length; i++)
			{
				var x = _positions[i];
				var current = environment.SampleCurrent(north + x * cos, east + x * sin, time);
				var dn = current.North - reference.North;
				var de = current.East - reference.East;
				_north[i] = dn;
				_east[i] = de;
				varies |= dn != 0.0 || de != 0.0;
			}

			Varies = varies;
			if (!varies)
			{
				_interceptNorth = 0.0;
				_interceptEast = 0.0;
				_slopeNorth = 0.0;
				_slopeEast = 0.0;
				return false;
			}

			// The least-squares line through the differences, d(x) = intercept + slope·x, in world components.
			var meanNorth = 0.0;
			var meanEast = 0.0;
			var slopeNorth = 0.0;
			var slopeEast = 0.0;
			for (var i = 0; i < _positions.Length; i++)
			{
				var offset = _positions[i] - _meanPosition;
				meanNorth += _north[i];
				meanEast += _east[i];
				slopeNorth += offset * _north[i];
				slopeEast += offset * _east[i];
			}

			_slopeNorth = slopeNorth / _spread;
			_slopeEast = slopeEast / _spread;
			_interceptNorth = meanNorth / _positions.Length - _slopeNorth * _meanPosition;
			_interceptEast = meanEast / _positions.Length - _slopeEast * _meanPosition;
			return true;
		}

		/// <summary>
		/// The fitted line in body axes at a heading (its cosine and sine): the current difference at the reference point
		/// along and across the hull (m/s), and how fast each changes per metre forward (1/s).
		/// </summary>
		public void Resolve(double cos, double sin, out double surge, out double sway, out double surgeSlope, out double swaySlope)
		{
			surge = _interceptNorth * cos + _interceptEast * sin;
			sway = -_interceptNorth * sin + _interceptEast * cos;
			surgeSlope = _slopeNorth * cos + _slopeEast * sin;
			swaySlope = -_slopeNorth * sin + _slopeEast * cos;
		}

		/// <summary>
		/// Each station's current across the hull that the fitted line (<paramref name="sway"/> + <paramref name="swaySlope"/>·x)
		/// leaves out, at a heading, m/s toward starboard. The returned buffer is reused by the next call.
		/// </summary>
		public double[] Residuals(double cos, double sin, double sway, double swaySlope)
		{
			for (var i = 0; i < _positions.Length; i++)
			{
				_residual[i] = -_north[i] * sin + _east[i] * cos - (sway + swaySlope * _positions[i]);
			}

			return _residual;
		}
	}
}
