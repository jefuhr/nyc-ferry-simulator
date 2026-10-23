using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// One of the twin rudders, each behind its own propeller (MMG rudder model, Yasukawa and
	/// Yoshimura 2015, extended to all inflow directions). The normal force acts on the rudder plane:
	/// F_N = ½ρA_R U_R² C_N(α) toward the side the flow strikes, with
	/// C_N = f_α sin α up to stall, f_α = 6.13Λ/(Λ + 2.25) (Fujii), then easing toward a flat plate.
	/// Inflow combines the free stream with the propeller slipstream; flow straightening applies when
	/// the rudder sees normal (leading-edge-first) flow.
	/// </summary>
	public sealed class RudderModel
	{
		private const double FlatPlateNormalCoefficient = 1.2;
		private const double ReversedStallRatio = 0.6;

		private readonly double _rho = PhysicalConstants.WaterDensity;
		private readonly double _area;
		private readonly double _liftSlope;
		private readonly double _stallAngle;
		private readonly double _reversedLiftFactor;
		private readonly double _straightening;
		private readonly double _effectiveLever;
		private readonly double _spanInSlipstream;
		private readonly double _wakeRatio;
		private readonly double _hullLateral;
		private readonly double _hullPosition;
		private readonly double _dragDeduction;

		public RudderModel(VesselParameters parameters)
		{
			var particulars = Required.Section(parameters.Particulars, "particulars");
			var hull = Required.Section(parameters.Hull, "hull");
			var section = Required.Section(parameters.Rudders, "rudders");
			var length = Required.Positive(particulars.LengthWaterline, "particulars.lengthWaterline");
			_area = Required.Positive(section.Area, "rudders.area");
			var aspect = Required.Positive(section.AspectRatio, "rudders.aspectRatio");
			_liftSlope = 6.13 * aspect / (aspect + 2.25);
			_stallAngle = Required.InRange(section.StallAngle, "rudders.stallAngle", 0.1, 1.2);
			_reversedLiftFactor = Required.InRange(section.ReversedFlowLiftFactor, "rudders.reversedFlowLiftFactor", 0.0, 1.0);
			_straightening = Required.InRange(section.FlowStraightening, "rudders.flowStraightening", 0.0, 1.5);
			_effectiveLever = length * Required.Value(section.EffectiveLeverFactor, "rudders.effectiveLeverFactor");
			_spanInSlipstream = Required.InRange(section.SlipstreamSpanFraction, "rudders.slipstreamSpanFraction", 0.0, 1.0);
			_wakeRatio = Required.Positive(section.WakeRatio, "rudders.wakeRatio");
			_hullLateral = Required.Value(section.HullLateralInteraction, "rudders.hullLateralInteraction");
			_hullPosition = length * Required.Value(section.HullInteractionPosition, "rudders.hullInteractionPosition");
			_dragDeduction = Required.InRange(section.DragDeduction, "rudders.dragDeduction", 0.0, 0.9);
			LongitudinalPosition = Required.Value(section.LongitudinalPosition, "rudders.longitudinalPosition");
			LateralOffset = 0.5 * Required.Positive(hull.DemihullSeparation, "hull.demihullSeparation");
			ForceDepth = Required.Value(section.ForceDepth, "rudders.forceDepth");
		}

		/// <summary>Rudder stock position from midships, m (negative aft).</summary>
		public double LongitudinalPosition { get; }

		/// <summary>Lateral distance of each rudder from the centreline, m.</summary>
		public double LateralOffset { get; }

		/// <summary>Depth of the rudders' centre of effort below the waterline, m.</summary>
		public double ForceDepth { get; }

		/// <summary>
		/// Forces on the hull from the port (−1) or starboard (+1) rudder at angle <paramref name="angle"/>
		/// (positive is starboard rudder), given the hull's motion through the water, that side's propeller
		/// advance speed and its fully developed slipstream speed.
		/// </summary>
		public void Forces(
			int side,
			double angle,
			double sway,
			double yawRate,
			double advanceSpeed,
			double slipstreamSpeed,
			double slipstreamFactor,
			double upstreamFactor,
			out double x,
			out double y,
			out double n)
		{
			// Downstream of the propeller the jet speeds the flow up; upstream (astern thrust) only weakly.
			var factor = slipstreamSpeed >= 0.0 ? slipstreamFactor : upstreamFactor;
			var washed = advanceSpeed + factor * (slipstreamSpeed - advanceSpeed);
			var blend = _spanInSlipstream * washed * Math.Abs(washed) + (1.0 - _spanInSlipstream) * advanceSpeed * Math.Abs(advanceSpeed);
			var inflowX = _wakeRatio * (blend >= 0.0 ? Math.Sqrt(blend) : -Math.Sqrt(-blend));
			var normalFlow = inflowX >= 0.0;
			var inflowY = normalFlow
				? _straightening * (sway + _effectiveLever * yawRate)
				: sway + LongitudinalPosition * yawRate;

			var sin = Math.Sin(angle);
			var cos = Math.Cos(angle);
			var speedSquared = inflowX * inflowX + inflowY * inflowY;
			var normalForce = 0.0;
			if (speedSquared > 1e-12)
			{
				// The water moves past the rudder at −(inflowX, inflowY); the plate normal is (sin δ, cos δ).
				var across = -inflowX * sin - inflowY * cos;
				var along = inflowX * cos - inflowY * sin;
				var incidence = Math.Asin(Math.Min(1.0, Math.Abs(across) / Math.Sqrt(speedSquared)));
				var reversed = along < 0.0;
				var coefficient = NormalForceCoefficient(incidence, reversed);
				normalForce = Math.Sign(across) * 0.5 * _rho * _area * speedSquared * coefficient;
			}

			var hullLateral = normalFlow ? _hullLateral : 0.0;
			var dragDeduction = normalFlow ? _dragDeduction : 0.0;
			x = (1.0 - dragDeduction) * normalForce * sin;
			y = (1.0 + hullLateral) * normalForce * cos;
			n = (LongitudinalPosition + hullLateral * _hullPosition) * normalForce * cos - side * LateralOffset * x;
		}

		/// <summary>Normal-force coefficient at an incidence between 0 and π/2.</summary>
		public double NormalForceCoefficient(double incidence, bool reversedFlow)
		{
			var slope = reversedFlow ? _liftSlope * _reversedLiftFactor : _liftSlope;
			var stall = reversedFlow ? _stallAngle * ReversedStallRatio : _stallAngle;
			if (incidence <= stall)
			{
				return slope * Math.Sin(incidence);
			}

			var atStall = slope * Math.Sin(stall);
			var t = (incidence - stall) / (0.5 * Math.PI - stall);
			return atStall + (FlatPlateNormalCoefficient - atStall) * t;
		}
	}
}
