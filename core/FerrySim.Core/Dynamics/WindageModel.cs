using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Wind load on the superstructure (Fossen 2011, §10.1 three-coefficient model). For an air
	/// velocity relative to the vessel w in body axes, with γ the angle the wind comes from relative to
	/// the bow, q = ½ρ_a|w|²:
	/// X = −q A_F c_x cos γ, Y = −q A_L c_y sin γ, N = −q A_L L_OA c_n sin 2γ + x_LA·Y.
	/// <para>
	/// Following the ISO 15016 / ITTC 7.5-04-01-01.1 wind correction, the hull's calm-water resistance
	/// already contains the vessel's own still-air drag, so the wind module applies the load from the
	/// apparent wind minus the load of the vessel's own motion over the ground in still air, and adds
	/// back its own motion through the water in still air. Without current this is exactly the
	/// apparent-wind load; with no true wind and no motion through the water it is exactly zero.
	/// </para>
	/// </summary>
	public sealed class WindageModel
	{
		private readonly double _frontal;
		private readonly double _lateral;
		private readonly double _cx;
		private readonly double _cy;
		private readonly double _cn;
		private readonly double _lengthOverall;

		public WindageModel(VesselParameters parameters)
		{
			var particulars = Required.Section(parameters.Particulars, "particulars");
			var section = Required.Section(parameters.Windage, "windage");
			_lengthOverall = Required.Positive(particulars.LengthOverall, "particulars.lengthOverall");
			_frontal = Required.Positive(section.FrontalArea, "windage.frontalArea");
			_lateral = Required.Positive(section.LateralArea, "windage.lateralArea");
			_cx = Required.NonNegative(section.LongitudinalCoefficient, "windage.longitudinalCoefficient");
			_cy = Required.NonNegative(section.LateralCoefficient, "windage.lateralCoefficient");
			_cn = Required.Value(section.YawCoefficient, "windage.yawCoefficient");
			LateralAreaCentre = Required.Value(section.LateralAreaCentre, "windage.lateralAreaCentre");
			LateralAreaHeight = Required.Value(section.LateralAreaHeight, "windage.lateralAreaHeight");
		}

		/// <summary>Longitudinal centre of the lateral area, m from midships.</summary>
		public double LateralAreaCentre { get; }

		/// <summary>Height of the lateral area's centre above the waterline, m.</summary>
		public double LateralAreaHeight { get; }

		/// <summary>Aerodynamic load for an air velocity relative to the vessel, body axes (m/s).</summary>
		public void Load(double airForward, double airStarboard, out double x, out double y, out double n)
		{
			var speedSquared = airForward * airForward + airStarboard * airStarboard;
			if (speedSquared < 1e-12)
			{
				x = 0.0;
				y = 0.0;
				n = 0.0;
				return;
			}

			// The wind comes from γ: cos γ = −w_x/|w|, sin γ = −w_y/|w|.
			var speed = Math.Sqrt(speedSquared);
			var pressure = 0.5 * PhysicalConstants.AirDensity * speedSquared;
			var cosFrom = -airForward / speed;
			var sinFrom = -airStarboard / speed;
			x = -pressure * _frontal * _cx * cosFrom;
			y = -pressure * _lateral * _cy * sinFrom;
			n = -pressure * _lateral * _lengthOverall * _cn * 2.0 * sinFrom * cosFrom + LateralAreaCentre * y;
		}

		/// <summary>
		/// Wind load in body axes from the true wind and the vessel's motion. Velocities over the ground
		/// and the true wind are world north/east; motion through the water is body surge and sway.
		/// </summary>
		public void Forces(
			HorizontalVector trueWind,
			HorizontalVector groundVelocity,
			double heading,
			double surgeThroughWater,
			double swayThroughWater,
			out double x,
			out double y,
			out double n)
		{
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var apparentNorth = trueWind.North - groundVelocity.North;
			var apparentEast = trueWind.East - groundVelocity.East;
			Load(apparentNorth * cos + apparentEast * sin, -apparentNorth * sin + apparentEast * cos, out x, out y, out n);
			Load(-(groundVelocity.North * cos + groundVelocity.East * sin), -(-groundVelocity.North * sin + groundVelocity.East * cos),
				out var groundX, out var groundY, out var groundN);
			Load(-surgeThroughWater, -swayThroughWater, out var waterX, out var waterY, out var waterN);
			x += waterX - groundX;
			y += waterY - groundY;
			n += waterN - groundN;
		}
	}
}
