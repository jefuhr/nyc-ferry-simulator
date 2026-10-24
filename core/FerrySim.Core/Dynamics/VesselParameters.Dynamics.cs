using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// The dynamics model's sections of the vessel parameter file. Published particulars and
	/// machinery stay in the contract part; everything here is a model coefficient with its own
	/// provenance. See core/FerrySim.Core/Dynamics/README.md for the meaning of each value.
	/// </summary>
	public sealed partial class VesselParameters
	{
		public MassProperties MassProperties { get; set; }

		public HullParameters Hull { get; set; }

		public PropellerParameters Propellers { get; set; }

		public EngineParameters Engines { get; set; }

		public TransmissionParameters Transmission { get; set; }

		public ControlParameters Controls { get; set; }

		public RudderParameters Rudders { get; set; }

		public WindageParameters Windage { get; set; }

		public ShallowWaterParameters ShallowWater { get; set; }

		public MotionResponseParameters MotionResponse { get; set; }

		public ContactParameters Contact { get; set; }
	}
}

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Rigid-body mass distribution. The mass itself is <c>particulars.displacement</c>; positions are
	/// measured from the reference point (centreline, midships of the waterline, design waterline).
	/// </summary>
	public sealed class MassProperties
	{
		/// <summary>Yaw radius of gyration about the centre of gravity, m.</summary>
		public ParameterValue YawRadiusOfGyration { get; set; }

		/// <summary>Longitudinal position of the centre of gravity, m, positive forward of midships.</summary>
		public ParameterValue LongitudinalCentreOfGravity { get; set; }

		/// <summary>Height of the centre of gravity above the design waterline, m.</summary>
		public ParameterValue CentreOfGravityHeight { get; set; }
	}

	/// <summary>
	/// Catamaran hull: calm-water resistance, added masses and the MMG-style manoeuvring derivatives.
	/// Non-dimensional derivatives use the MMG basis: forces on ½ρLTU², moments on ½ρL²TU², with L the
	/// waterline length and T the hull draft of one demihull, and v' = v/U, r' = rL/U.
	/// </summary>
	public sealed class HullParameters
	{
		/// <summary>Distance between the two demihull centrelines, m.</summary>
		public ParameterValue DemihullSeparation { get; set; }

		/// <summary>Waterline beam of one demihull, m.</summary>
		public ParameterValue DemihullBeam { get; set; }

		/// <summary>Immersed midship section area of both demihulls together, m².</summary>
		public ParameterValue MidshipSectionArea { get; set; }

		/// <summary>Wetted surface of both demihulls at rest, m².</summary>
		public ParameterValue WettedSurface { get; set; }

		/// <summary>Viscous form factor (1 + βk) including demihull interference.</summary>
		public ParameterValue FormFactor { get; set; }

		/// <summary>Model-ship correlation (roughness) allowance on the resistance coefficient.</summary>
		public ParameterValue CorrelationAllowance { get; set; }

		/// <summary>Residuary resistance coefficient against Froude number (shape of the hump).</summary>
		public List<ResistancePoint> ResiduaryResistance { get; set; }

		/// <summary>Calibration multiplier on the residuary resistance curve.</summary>
		public ParameterValue ResiduaryResistanceScale { get; set; }

		/// <summary>Resistance going astern divided by resistance ahead at the same speed.</summary>
		public ParameterValue AsternResistanceFactor { get; set; }

		/// <summary>Surge added mass m_x, kg.</summary>
		public ParameterValue SurgeAddedMass { get; set; }

		/// <summary>Sway added mass m_y about midships, kg.</summary>
		public ParameterValue SwayAddedMass { get; set; }

		/// <summary>Yaw added moment of inertia J_z about midships, kg·m².</summary>
		public ParameterValue YawAddedInertia { get; set; }

		/// <summary>Linear sway force due to sway, Y'_v (negative).</summary>
		public ParameterValue DerivativeYv { get; set; }

		/// <summary>Linear sway force due to yaw rate, Y'_r.</summary>
		public ParameterValue DerivativeYr { get; set; }

		/// <summary>Linear yaw moment due to sway, N'_v (negative: Munk moment, destabilising).</summary>
		public ParameterValue DerivativeNv { get; set; }

		/// <summary>Linear yaw moment due to yaw rate, N'_r (negative: yaw damping).</summary>
		public ParameterValue DerivativeNr { get; set; }

		/// <summary>Surge force due to sway squared, X'_vv (negative: induced drag in drift).</summary>
		public ParameterValue DerivativeXvv { get; set; }

		/// <summary>Surge force due to sway times yaw rate, X'_vr (hydrodynamic part only).</summary>
		public ParameterValue DerivativeXvr { get; set; }

		/// <summary>Surge force due to yaw rate squared, X'_rr.</summary>
		public ParameterValue DerivativeXrr { get; set; }

		/// <summary>Cross-flow drag coefficient on the lateral area of both demihulls (2LT), deep water.</summary>
		public ParameterValue CrossFlowDragCoefficient { get; set; }

		/// <summary>Depth below the waterline at which the hull's lateral force acts, m (roll arm).</summary>
		public ParameterValue LateralForceDepth { get; set; }
	}

	/// <summary>One point of the residuary resistance curve.</summary>
	public sealed class ResistancePoint
	{
		/// <summary>Length Froude number U/√(gL) on the waterline length.</summary>
		public ParameterValue FroudeNumber { get; set; }

		/// <summary>Residuary resistance coefficient R_R / (½ρSU²).</summary>
		public ParameterValue Coefficient { get; set; }
	}

	/// <summary>
	/// Twin fixed-pitch propellers. Diameter, blade count and gear ratio come from <c>machinery</c>.
	/// Open-water characteristics are the Wageningen B-series at the given pitch and area ratios,
	/// extended to four quadrants; see the README.
	/// </summary>
	public sealed class PropellerParameters
	{
		/// <summary>Pitch-diameter ratio P/D at 0.7R.</summary>
		public ParameterValue PitchRatio { get; set; }

		/// <summary>Expanded blade area ratio A_E/A_0.</summary>
		public ParameterValue ExpandedAreaRatio { get; set; }

		/// <summary>Longitudinal position of the propellers, m from midships (negative aft).</summary>
		public ParameterValue LongitudinalPosition { get; set; }

		/// <summary>Depth of the propeller shafts below the waterline, m (pitch and roll arms).</summary>
		public ParameterValue ShaftDepth { get; set; }

		/// <summary>Effective wake fraction w: advance speed = (1 − w) × local speed through water.</summary>
		public ParameterValue WakeFraction { get; set; }

		/// <summary>Thrust deduction fraction t for ahead thrust.</summary>
		public ParameterValue ThrustDeduction { get; set; }

		/// <summary>Thrust deduction fraction for astern thrust (the jet washes forward along the hull).</summary>
		public ParameterValue AsternThrustDeduction { get; set; }

		/// <summary>Blade lift in reversed (trailing-edge-first) flow relative to normal flow.</summary>
		public ParameterValue ReversedFlowLiftFactor { get; set; }

		/// <summary>Blade section normal-force coefficient at 90° incidence (locked-propeller drag).</summary>
		public ParameterValue BladeNormalDragCoefficient { get; set; }

		/// <summary><c>outward-turning</c> or <c>inward-turning</c> (tops of the blades, going ahead).</summary>
		public ParameterText Rotation { get; set; }

		/// <summary>Side force from one propeller going ahead, as a fraction of its thrust (prop walk).</summary>
		public ParameterValue PropWalkAhead { get; set; }

		/// <summary>Side force from one propeller going astern, as a fraction of its thrust.</summary>
		public ParameterValue PropWalkAstern { get; set; }

		/// <summary>Rotating inertia on the propeller side of the clutch, including entrained water, kg·m².</summary>
		public ParameterValue ShaftInertia { get; set; }

		/// <summary>Bearing, seal and gearbox-output drag torque on a turning shaft, N·m.</summary>
		public ParameterValue ShaftFrictionTorque { get; set; }

		/// <summary>Fraction of the fully developed slipstream speed-up reached at the rudder (MMG κ).</summary>
		public ParameterValue SlipstreamFactor { get; set; }

		/// <summary>Same as <see cref="SlipstreamFactor"/> when the rudder is upstream of the jet (astern thrust).</summary>
		public ParameterValue UpstreamInflowFactor { get; set; }
	}

	/// <summary>One engine. Rated power and speed come from <c>machinery</c>.</summary>
	public sealed class EngineParameters
	{
		/// <summary>Governed idle speed, rad/s.</summary>
		public ParameterValue IdleSpeed { get; set; }

		/// <summary>Full-load brake torque against engine speed, in increasing speed order.</summary>
		public List<TorquePoint> TorqueCurve { get; set; }

		/// <summary>Engine-side rotating inertia (crank, flywheel, gearbox input), kg·m².</summary>
		public ParameterValue Inertia { get; set; }

		/// <summary>Friction and pumping torque at idle speed, N·m.</summary>
		public ParameterValue FrictionTorqueAtIdle { get; set; }

		/// <summary>Friction and pumping torque at rated speed, N·m.</summary>
		public ParameterValue FrictionTorqueAtRatedSpeed { get; set; }

		/// <summary>Closed-loop speed time constant of the governor with the gear engaged, s.</summary>
		public ParameterValue GovernorTimeConstant { get; set; }

		/// <summary>Integral time of the governor's PI speed loop, s.</summary>
		public ParameterValue GovernorIntegralTime { get; set; }

		/// <summary>Fraction of full-load torque available before the turbocharger builds boost.</summary>
		public ParameterValue UnboostedTorqueFraction { get; set; }

		/// <summary>Time constant of the turbocharger boost build-up, s.</summary>
		public ParameterValue TurbochargerTimeConstant { get; set; }
	}

	/// <summary>One point of the full-load torque curve.</summary>
	public sealed class TorquePoint
	{
		/// <summary>Engine speed, rad/s.</summary>
		public ParameterValue Speed { get; set; }

		/// <summary>Maximum brake torque at that speed, N·m.</summary>
		public ParameterValue Torque { get; set; }
	}

	/// <summary>Marine reverse-reduction gear with hydraulic clutches.</summary>
	public sealed class TransmissionParameters
	{
		/// <summary>Mechanical efficiency of the gear.</summary>
		public ParameterValue GearEfficiency { get; set; }

		/// <summary>Torque the fully pressurised clutch transmits before slipping, at the propeller shaft, N·m.</summary>
		public ParameterValue ClutchTorqueCapacity { get; set; }

		/// <summary>Time from a shift command to the clutch starting to carry torque, s.</summary>
		public ParameterValue ClutchFillTime { get; set; }

		/// <summary>Time for the clutch pressure to ramp from first torque to full capacity, s.</summary>
		public ParameterValue ClutchEngagementTime { get; set; }

		/// <summary>Time for a clutch to release, s.</summary>
		public ParameterValue ClutchReleaseTime { get; set; }
	}

	/// <summary>
	/// The control model: how lever and helm positions become clutch, governor and rudder orders.
	/// Every timing here is an estimate until crew confirm the real control system's settings.
	/// </summary>
	public sealed class ControlParameters
	{
		/// <summary>Lever position (fraction of travel) of the ahead and astern idle detents, where the clutch engages.</summary>
		public ParameterValue ClutchInLever { get; set; }

		/// <summary>Lever position above which the throttle opens from idle toward rated speed.</summary>
		public ParameterValue ThrottleStartLever { get; set; }

		/// <summary>How far back past the clutch-in position the lever must come to disengage.</summary>
		public ParameterValue DetentHysteresis { get; set; }

		/// <summary>Time held in neutral when reversing, before the opposite clutch is filled, s.</summary>
		public ParameterValue NeutralDwell { get; set; }

		/// <summary>Time the throttle is held at idle after a clutch locks, s.</summary>
		public ParameterValue ThrottlePause { get; set; }

		/// <summary>A clutch is only filled when the engine is at or below this speed, rad/s.</summary>
		public ParameterValue ShiftInterlockSpeed { get; set; }

		/// <summary><c>follow-up</c> (helm position orders rudder angle) or <c>non-follow-up</c> (helm orders rudder movement).</summary>
		public ParameterText SteeringMode { get; set; }

		/// <summary>Maximum rudder angle each way, rad.</summary>
		public ParameterValue HardOverAngle { get; set; }

		/// <summary>Steering gear slew rate, rad/s.</summary>
		public ParameterValue SteeringRate { get; set; }
	}

	/// <summary>Twin spade rudders, one behind each propeller.</summary>
	public sealed class RudderParameters
	{
		/// <summary>Movable area of one rudder, m².</summary>
		public ParameterValue Area { get; set; }

		/// <summary>Geometric aspect ratio of one rudder (span²/area).</summary>
		public ParameterValue AspectRatio { get; set; }

		/// <summary>Longitudinal position of the rudder stocks, m from midships (negative aft).</summary>
		public ParameterValue LongitudinalPosition { get; set; }

		/// <summary>Angle of attack at which the rudder stalls in normal flow, rad.</summary>
		public ParameterValue StallAngle { get; set; }

		/// <summary>Lift slope in reversed flow (going astern) relative to normal flow.</summary>
		public ParameterValue ReversedFlowLiftFactor { get; set; }

		/// <summary>Flow-straightening factor γ_R on the lateral inflow to the rudder.</summary>
		public ParameterValue FlowStraightening { get; set; }

		/// <summary>Effective longitudinal lever of yaw rate on rudder inflow, l'_R, as a fraction of L.</summary>
		public ParameterValue EffectiveLeverFactor { get; set; }

		/// <summary>Fraction of the rudder span immersed in the propeller slipstream (MMG η).</summary>
		public ParameterValue SlipstreamSpanFraction { get; set; }

		/// <summary>Ratio of the rudder's wake to the propeller's wake (MMG ε).</summary>
		public ParameterValue WakeRatio { get; set; }

		/// <summary>Extra hull side force induced by the rudder, as a fraction of its own (MMG a_H).</summary>
		public ParameterValue HullLateralInteraction { get; set; }

		/// <summary>Position of that extra hull force, as a fraction of L from midships (MMG x'_H).</summary>
		public ParameterValue HullInteractionPosition { get; set; }

		/// <summary>Fraction of the rudder's longitudinal force lost to the hull (MMG t_R).</summary>
		public ParameterValue DragDeduction { get; set; }

		/// <summary>Depth of the rudders' centre of effort below the waterline, m (roll arm).</summary>
		public ParameterValue ForceDepth { get; set; }
	}

	/// <summary>Wind loads on the superstructure (Fossen's three-coefficient model).</summary>
	public sealed class WindageParameters
	{
		/// <summary>Frontal projected area above the waterline, m².</summary>
		public ParameterValue FrontalArea { get; set; }

		/// <summary>Lateral projected area above the waterline, m².</summary>
		public ParameterValue LateralArea { get; set; }

		/// <summary>Head-on drag coefficient on the frontal area, c_x.</summary>
		public ParameterValue LongitudinalCoefficient { get; set; }

		/// <summary>Beam-on drag coefficient on the lateral area, c_y.</summary>
		public ParameterValue LateralCoefficient { get; set; }

		/// <summary>Yaw moment coefficient c_n on lateral area times length overall.</summary>
		public ParameterValue YawCoefficient { get; set; }

		/// <summary>Longitudinal centre of the lateral area, m from midships (positive forward).</summary>
		public ParameterValue LateralAreaCentre { get; set; }

		/// <summary>Height of the centre of the lateral area above the waterline, m.</summary>
		public ParameterValue LateralAreaHeight { get; set; }
	}

	/// <summary>Finite-depth corrections driven by <c>IEnvironment.SampleDepth</c>.</summary>
	public sealed class ShallowWaterParameters
	{
		/// <summary>Return-flow speed increase per unit of (A_X/h² − 0.05) (Lackenby).</summary>
		public ParameterValue BlockageCoefficient { get; set; }

		/// <summary>Peak fractional increase of residuary resistance near depth Froude number 1.</summary>
		public ParameterValue CriticalSpeedAmplitude { get; set; }

		/// <summary>Width (in depth Froude number) of that peak.</summary>
		public ParameterValue CriticalSpeedWidth { get; set; }

		/// <summary>Growth of the linear manoeuvring derivatives with T/h: factor 1 + c(T/h)²/(1 − T/h).</summary>
		public ParameterValue LinearDerivativeFactor { get; set; }

		/// <summary>Growth of the cross-flow drag coefficient with T/h, same form.</summary>
		public ParameterValue CrossFlowFactor { get; set; }

		/// <summary>Growth of sway added mass and yaw added inertia with T/h, same form.</summary>
		public ParameterValue AddedMassFactor { get; set; }

		/// <summary>Smallest depth-to-draft ratio the corrections use; shallower water is treated as this.</summary>
		public ParameterValue MinimumDepthRatio { get; set; }
	}

	/// <summary>The light heave, roll and pitch layer used for feel and visuals.</summary>
	public sealed class MotionResponseParameters
	{
		/// <summary>Transverse metacentric height GM_T, m.</summary>
		public ParameterValue TransverseMetacentricHeight { get; set; }

		/// <summary>Longitudinal metacentric height GM_L, m.</summary>
		public ParameterValue LongitudinalMetacentricHeight { get; set; }

		/// <summary>Natural roll period, s.</summary>
		public ParameterValue RollPeriod { get; set; }

		/// <summary>Natural pitch period, s.</summary>
		public ParameterValue PitchPeriod { get; set; }

		/// <summary>Natural heave period, s.</summary>
		public ParameterValue HeavePeriod { get; set; }

		/// <summary>Fraction of critical damping in roll.</summary>
		public ParameterValue RollDampingRatio { get; set; }

		/// <summary>Fraction of critical damping in pitch.</summary>
		public ParameterValue PitchDampingRatio { get; set; }

		/// <summary>Fraction of critical damping in heave.</summary>
		public ParameterValue HeaveDampingRatio { get; set; }

		/// <summary>Running trim and sinkage in calm deep water against Froude number.</summary>
		public List<RunningAttitudePoint> RunningAttitude { get; set; }
	}

	/// <summary>One point of the running attitude curve.</summary>
	public sealed class RunningAttitudePoint
	{
		/// <summary>Length Froude number on the waterline length.</summary>
		public ParameterValue FroudeNumber { get; set; }

		/// <summary>Dynamic trim, rad, positive bow up.</summary>
		public ParameterValue Trim { get; set; }

		/// <summary>Dynamic sinkage at midships, m, positive down.</summary>
		public ParameterValue Sinkage { get; set; }
	}

	/// <summary>
	/// The hull's plan-view outline at the fender line and its own fendering, for contact with fixed
	/// structures. Points are body axes from the reference point (x forward, y starboard, metres). The
	/// outline is four open polylines that join end to end: each demihull's outboard sheer line from aft to
	/// forward, the bow fender line across the bow and the stern fender line across the transoms.
	/// </summary>
	public sealed class ContactParameters
	{
		/// <summary>Points along the starboard demihull's outboard sheer line, from aft to forward.</summary>
		public List<OutlinePoint> StarboardDemihull { get; set; }

		/// <summary>Points along the port demihull's outboard sheer line, from aft to forward.</summary>
		public List<OutlinePoint> PortDemihull { get; set; }

		/// <summary>The bow fender line from port to starboard, joining the forward ends of the sheer lines.</summary>
		public List<OutlinePoint> BowFenderLine { get; set; }

		/// <summary>The stern fender line from port to starboard, joining the aft ends of the sheer lines.</summary>
		public List<OutlinePoint> SternFenderLine { get; set; }

		/// <summary>Normal stiffness of the hull's fendering per metre of contact length, N/m per m.</summary>
		public ParameterValue HullFenderStiffness { get; set; }

		/// <summary>Normal damping of the hull's fendering per metre of contact length, N·s/m per m.</summary>
		public ParameterValue HullFenderDamping { get; set; }

		/// <summary>Compression at which the hull's fendering bottoms out, m.</summary>
		public ParameterValue HullFenderMaxCompression { get; set; }

		/// <summary>
		/// Stiffness of a fender past its compression limit, when the structure behind it takes the load,
		/// as a multiple of the fender's own stiffness.
		/// </summary>
		public ParameterValue BottomedStiffnessFactor { get; set; }

		/// <summary>
		/// Rate-independent hysteresis of the fender rubber, hull and landing sides together: while a fender
		/// compresses its elastic force is (1 + h) times the elastic law, while it recovers (1 − h) times.
		/// </summary>
		public ParameterValue FenderHysteresis { get; set; }
	}

	/// <summary>One point of the hull outline, body axes.</summary>
	public sealed class OutlinePoint
	{
		/// <summary>Distance forward of midships, m.</summary>
		public ParameterValue X { get; set; }

		/// <summary>Distance to starboard of the centreline, m.</summary>
		public ParameterValue Y { get; set; }
	}
}
