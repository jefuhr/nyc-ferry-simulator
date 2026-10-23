using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// One of the twin fixed-pitch propellers: four-quadrant thrust and torque, slipstream speed for the
	/// rudders, prop walk, and how much of the thrust reaches the hull. Both propellers share the same
	/// geometry; <c>side</c> is −1 for port and +1 for starboard.
	/// </summary>
	public sealed class PropellerModel
	{
		private readonly double _rho = PhysicalConstants.WaterDensity;
		private readonly double _discArea;
		private readonly double _thrustDeduction;
		private readonly double _asternThrustDeduction;
		private readonly double _walkAhead;
		private readonly double _walkAstern;
		private readonly int _outwardTurning;

		public PropellerModel(VesselParameters parameters)
		{
			var machinery = Required.Section(parameters.Machinery, "machinery");
			var hull = Required.Section(parameters.Hull, "hull");
			var section = Required.Section(parameters.Propellers, "propellers");
			Diameter = Required.Positive(machinery.PropellerDiameter, "machinery.propellerDiameter");
			var blades = Required.Positive(machinery.PropellerBlades, "machinery.propellerBlades");
			var pitchRatio = Required.InRange(section.PitchRatio, "propellers.pitchRatio", 0.5, 1.4);
			var areaRatio = Required.InRange(section.ExpandedAreaRatio, "propellers.expandedAreaRatio", 0.3, 1.05);
			Curves = new FourQuadrantPropellerCurves(
				pitchRatio,
				areaRatio,
				blades,
				Required.InRange(section.ReversedFlowLiftFactor, "propellers.reversedFlowLiftFactor", 0.1, 1.0),
				Required.Positive(section.BladeNormalDragCoefficient, "propellers.bladeNormalDragCoefficient"));
			LongitudinalPosition = Required.Value(section.LongitudinalPosition, "propellers.longitudinalPosition");
			LateralOffset = 0.5 * Required.Positive(hull.DemihullSeparation, "hull.demihullSeparation");
			ShaftDepth = Required.Value(section.ShaftDepth, "propellers.shaftDepth");
			WakeFraction = Required.InRange(section.WakeFraction, "propellers.wakeFraction", 0.0, 0.5);
			_thrustDeduction = Required.InRange(section.ThrustDeduction, "propellers.thrustDeduction", 0.0, 0.5);
			_asternThrustDeduction = Required.InRange(section.AsternThrustDeduction, "propellers.asternThrustDeduction", 0.0, 0.5);
			_walkAhead = Required.NonNegative(section.PropWalkAhead, "propellers.propWalkAhead");
			_walkAstern = Required.NonNegative(section.PropWalkAstern, "propellers.propWalkAstern");
			_outwardTurning = Required.Text(section.Rotation, "propellers.rotation", "outward-turning", "inward-turning") == "outward-turning" ? 1 : -1;
			SlipstreamFactor = Required.InRange(section.SlipstreamFactor, "propellers.slipstreamFactor", 0.0, 1.0);
			UpstreamInflowFactor = Required.InRange(section.UpstreamInflowFactor, "propellers.upstreamInflowFactor", 0.0, 1.0);
			ShaftInertia = Required.Positive(section.ShaftInertia, "propellers.shaftInertia");
			ShaftFrictionTorque = Required.NonNegative(section.ShaftFrictionTorque, "propellers.shaftFrictionTorque");
			_discArea = 0.25 * Math.PI * Diameter * Diameter;
		}

		public FourQuadrantPropellerCurves Curves { get; }

		public double Diameter { get; }

		/// <summary>Propeller position from midships, m (negative aft).</summary>
		public double LongitudinalPosition { get; }

		/// <summary>Lateral distance of each propeller from the centreline, m.</summary>
		public double LateralOffset { get; }

		/// <summary>Shaft depth below the waterline, m.</summary>
		public double ShaftDepth { get; }

		public double WakeFraction { get; }

		public double SlipstreamFactor { get; }

		public double UpstreamInflowFactor { get; }

		public double ShaftInertia { get; }

		public double ShaftFrictionTorque { get; }

		/// <summary>Lateral position of the port (−1) or starboard (+1) propeller, m.</summary>
		public double LateralPosition(int side) => side * LateralOffset;

		/// <summary>Advance speed at one propeller from the hull's velocity through the water.</summary>
		public double AdvanceSpeed(int side, double surge, double yawRate) =>
			(1.0 - WakeFraction) * (surge - yawRate * LateralPosition(side));

		/// <summary>
		/// Thrust (N, positive pushing ahead) and shaft torque (N·m, the load the shaft must supply in the
		/// ahead-turning sense) at a shaft speed (rad/s, positive ahead) and advance speed (m/s).
		/// </summary>
		public void Evaluate(double shaftSpeed, double advanceSpeed, out double thrust, out double torque)
		{
			var tangential = 0.35 * shaftSpeed * Diameter;
			var squared = advanceSpeed * advanceSpeed + tangential * tangential;
			if (squared < 1e-12)
			{
				thrust = 0.0;
				torque = 0.0;
				return;
			}

			Curves.Evaluate(Math.Atan2(advanceSpeed, tangential), out var thrustCoefficient, out var torqueCoefficient);
			var load = 0.5 * _rho * squared * _discArea;
			thrust = load * thrustCoefficient;
			torque = load * Diameter * torqueCoefficient;
		}

		/// <summary>
		/// Fully developed slipstream axial speed from momentum theory, V² = V_A|V_A| + 8T/(ρπD²), signed
		/// (negative when the flow through the disc runs forward).
		/// </summary>
		public double SlipstreamSpeed(double thrust, double advanceSpeed)
		{
			var squared = advanceSpeed * Math.Abs(advanceSpeed) + 8.0 * thrust / (_rho * Math.PI * Diameter * Diameter);
			return squared >= 0.0 ? Math.Sqrt(squared) : -Math.Sqrt(-squared);
		}

		/// <summary>Fraction of a propeller's thrust that pushes the hull (1 − t).</summary>
		public double HullThrustFactor(double thrust) => thrust >= 0.0 ? 1.0 - _thrustDeduction : 1.0 - _asternThrustDeduction;

		/// <summary>
		/// Prop walk side force, N, positive to starboard. A right-handed propeller walks the stern to
		/// starboard going ahead and to port going astern; outward-turning means the starboard propeller
		/// is right-handed and the port one left-handed, so walk always adds to the twisting moment.
		/// </summary>
		public double PropWalkForce(int side, double thrust)
		{
			var handedness = side * _outwardTurning;
			var fraction = thrust >= 0.0 ? _walkAhead : _walkAstern;
			return handedness * fraction * thrust;
		}
	}
}
