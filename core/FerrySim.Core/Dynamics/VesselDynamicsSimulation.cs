using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>Forces on the hull at the reference point from each module, body axes (N, N·m).</summary>
	public readonly struct ForceBreakdown
	{
		public ForceBreakdown(
			double hullX, double hullY, double hullN,
			double propellerX, double propellerY, double propellerN,
			double rudderX, double rudderY, double rudderN,
			double windX, double windY, double windN)
		{
			HullX = hullX;
			HullY = hullY;
			HullN = hullN;
			PropellerX = propellerX;
			PropellerY = propellerY;
			PropellerN = propellerN;
			RudderX = rudderX;
			RudderY = rudderY;
			RudderN = rudderN;
			WindX = windX;
			WindY = windY;
			WindN = windN;
		}

		public double HullX { get; }

		public double HullY { get; }

		public double HullN { get; }

		public double PropellerX { get; }

		public double PropellerY { get; }

		public double PropellerN { get; }

		public double RudderX { get; }

		public double RudderY { get; }

		public double RudderN { get; }

		public double WindX { get; }

		public double WindY { get; }

		public double WindN { get; }
	}

	/// <summary>
	/// 3-DOF (surge, sway, yaw) MMG-style manoeuvring model of a twin-screw, twin-rudder catamaran with
	/// engines, gears and the control model, plus a light heave/roll/pitch layer. See README.md.
	/// <para>
	/// The hydrodynamic state is the velocity relative to the water (u, v, r) at the reference point.
	/// For a uniform current the relative-velocity equations are exactly the calm-water ones (Fossen 2011,
	/// §10.3), so the current enters only through ν = ν_r + Rᵀ(ψ)V_c: position integrates the velocity over
	/// the ground, never the current on top of it. The rigid-body equations are integrated with classical
	/// fourth-order Runge-Kutta at the fixed 100 Hz step; the machinery is advanced first in four
	/// substeps with the hull's inflow held, and the environment is sampled once per step.
	/// </para>
	/// </summary>
	public sealed class VesselDynamicsSimulation : ISimulation
	{
		private const int DrivelineSubsteps = 4;
		private const int PortSide = -1;
		private const int StarboardSide = 1;

		private readonly IEnvironment _environment;
		private readonly HullModel _hull;
		private readonly PropellerModel _propeller;
		private readonly RudderModel _rudder;
		private readonly WindageModel _windage;
		private readonly ShallowWaterModel _shallow;
		private readonly Driveline _portDriveline;
		private readonly Driveline _starboardDriveline;
		private readonly EngineControl _portControl;
		private readonly EngineControl _starboardControl;
		private readonly SteeringGear _steering;
		private readonly MotionResponse _motion;
		private readonly double _mass;
		private readonly double _yawInertia;
		private readonly double _centreOfGravityX;
		private readonly double _centreOfGravityHeight;

		private long _stepCount;
		private double _north;
		private double _east;
		private double _heading;
		private double _surge;
		private double _sway;
		private double _yawRate;
		private HorizontalVector _current;
		private HorizontalVector _wind;
		private double _depth;

		// Held over one step.
		private ShallowWaterFactors _factors;
		private double _rudderAngle;
		private double _portShaftSpeed;
		private double _starboardShaftSpeed;
		private double _a11;
		private double _a12;
		private double _a22;
		private double _surgeMass;
		private double _swayMass;
		private double _determinant;
		private ForceBreakdown _forces;
		private double _surgeAcceleration;

		public VesselDynamicsSimulation(VesselParameters parameters, IEnvironment environment, in VesselState initialState)
		{
			Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
			_environment = environment ?? throw new ArgumentNullException(nameof(environment));

			var particulars = Required.Section(parameters.Particulars, "particulars");
			var machinery = Required.Section(parameters.Machinery, "machinery");
			var massProperties = Required.Section(parameters.MassProperties, "massProperties");
			var transmission = Required.Section(parameters.Transmission, "transmission");
			_mass = Required.Positive(particulars.Displacement, "particulars.displacement");
			var radius = Required.Positive(massProperties.YawRadiusOfGyration, "massProperties.yawRadiusOfGyration");
			_yawInertia = _mass * radius * radius;
			_centreOfGravityX = Required.Value(massProperties.LongitudinalCentreOfGravity, "massProperties.longitudinalCentreOfGravity");
			_centreOfGravityHeight = Required.Value(massProperties.CentreOfGravityHeight, "massProperties.centreOfGravityHeight");

			if (Required.Value(machinery.EngineCount, "machinery.engineCount") != 2.0 ||
				Required.Value(machinery.PropellerCount, "machinery.propellerCount") != 2.0)
			{
				throw new ArgumentException("The dynamics model is for twin-engine, twin-screw vessels.");
			}

			_hull = new HullModel(parameters);
			_propeller = new PropellerModel(parameters);
			_rudder = new RudderModel(parameters);
			_windage = new WindageModel(parameters);
			_shallow = new ShallowWaterModel(parameters);
			_motion = new MotionResponse(parameters);
			_steering = new SteeringGear(parameters);

			var gearRatio = Required.Positive(machinery.GearRatio, "machinery.gearRatio");
			var engine = new EngineModel(parameters, _propeller.ShaftInertia, gearRatio);
			var efficiency = Required.InRange(transmission.GearEfficiency, "transmission.gearEfficiency", 0.5, 1.0);
			var capacity = Required.Positive(transmission.ClutchTorqueCapacity, "transmission.clutchTorqueCapacity");
			_portDriveline = new Driveline(engine, _propeller, gearRatio, efficiency, capacity);
			_starboardDriveline = new Driveline(engine, _propeller, gearRatio, efficiency, capacity);
			var shift = new ShiftSettings(parameters);
			_portControl = new EngineControl(shift);
			_starboardControl = new EngineControl(shift);
			Engine = engine;

			var maxDraft = particulars.MaxDraft;
			KeelDraftIsHullDraft = maxDraft == null;
			KeelDraft = KeelDraftIsHullDraft
				? Required.Positive(particulars.Draft, "particulars.draft")
				: Required.Positive(maxDraft, "particulars.maxDraft");

			Reset(initialState);
		}

		public VesselParameters Parameters { get; }

		public VesselState State { get; private set; }

		public VesselTelemetry Telemetry { get; private set; }

		/// <summary>The engine model shared by both sides.</summary>
		public EngineModel Engine { get; }

		/// <summary>Draft used for depth under keel: <c>maxDraft</c>, or the hull draft when it is missing.</summary>
		public double KeelDraft { get; }

		/// <summary>True when the parameter file has no <c>maxDraft</c>, so depth under keel uses the hull draft.</summary>
		public bool KeelDraftIsHullDraft { get; }

		/// <summary>True when the water under the vessel is shallower than <see cref="KeelDraft"/>.</summary>
		public bool IsAground => Telemetry.DepthUnderKeel < 0.0;

		public ShiftPhase PortShiftPhase => _portControl.Phase;

		public ShiftPhase StarboardShiftPhase => _starboardControl.Phase;

		/// <summary>Rudder angle the steering gear is driving toward, rad.</summary>
		public double RudderOrder => _steering.Order;

		/// <summary>Module forces at the start of the last step.</summary>
		public ForceBreakdown LastForces => _forces;

		public void Reset(in VesselState initialState)
		{
			_stepCount = 0;
			_north = initialState.North;
			_east = initialState.East;
			_heading = initialState.Heading;
			SampleEnvironment(0.0);

			// The initial state gives velocities over the ground; the hydrodynamic state is through the water.
			var cos = Math.Cos(_heading);
			var sin = Math.Sin(_heading);
			_surge = initialState.Surge - (_current.North * cos + _current.East * sin);
			_sway = initialState.Sway - (-_current.North * sin + _current.East * cos);
			_yawRate = initialState.YawRate;

			_portDriveline.Reset();
			_starboardDriveline.Reset();
			_portControl.Reset();
			_starboardControl.Reset();
			_steering.Reset();
			_motion.Reset(initialState.Down, initialState.Heave, initialState.Roll, initialState.RollRate, initialState.Pitch, initialState.PitchRate);
			_forces = default;
			_surgeAcceleration = 0.0;
			Publish(0.0);
		}

		public void Step(in ControlInputs controls)
		{
			const double dt = SimulationClock.FixedTimeStep;

			_factors = _shallow.Evaluate(_depth, _surge);
			_steering.Step(controls.Helm, dt);
			_rudderAngle = _steering.Angle;

			_portControl.Step(controls.PortLever, _portDriveline.EngineSpeed, dt);
			_starboardControl.Step(controls.StarboardLever, _starboardDriveline.EngineSpeed, dt);
			_portDriveline.Step(dt, DrivelineSubsteps, _portControl.Throttle, _portControl.ClutchDirection, _portControl.ClutchPressure,
				_propeller.AdvanceSpeed(PortSide, _surge, _yawRate));
			_starboardDriveline.Step(dt, DrivelineSubsteps, _starboardControl.Throttle, _starboardControl.ClutchDirection, _starboardControl.ClutchPressure,
				_propeller.AdvanceSpeed(StarboardSide, _surge, _yawRate));
			_portShaftSpeed = _portDriveline.ShaftSpeed;
			_starboardShaftSpeed = _starboardDriveline.ShaftSpeed;

			UpdateMassMatrix();
			IntegrateRigidBody(dt);

			var froude = _surge / Math.Sqrt(PhysicalConstants.Gravity * _hull.Length);
			var lateralArm = _hull.LateralForceDepth + _centreOfGravityHeight;
			var appendageArm = _rudder.ForceDepth + _centreOfGravityHeight;
			var heelingMoment = -lateralArm * _forces.HullY - appendageArm * (_forces.RudderY + _forces.PropellerY) +
				(_windage.LateralAreaHeight - _centreOfGravityHeight) * _forces.WindY;
			_motion.Step(dt, _mass, froude, heelingMoment, _surgeAcceleration, _centreOfGravityHeight + _propeller.ShaftDepth);

			_stepCount++;
			var time = SimulationClock.TimeAtStep(_stepCount);
			SampleEnvironment(time);
			Publish(time);
		}

		private void SampleEnvironment(double time)
		{
			_current = _environment.SampleCurrent(_north, _east, time);
			_wind = _environment.SampleWind(_north, _east, time);
			_depth = _environment.SampleDepth(_north, _east, time);
		}

		private void UpdateMassMatrix()
		{
			var addedMass = _factors.AddedMass;
			_surgeMass = _mass + _hull.SurgeAddedMass;
			_swayMass = _mass + _hull.SwayAddedMass * addedMass;
			_a11 = _swayMass;
			_a12 = _centreOfGravityX * _mass;
			_a22 = _yawInertia + _centreOfGravityX * _centreOfGravityX * _mass + _hull.YawAddedInertia * addedMass;
			_determinant = _a11 * _a22 - _a12 * _a12;
		}

		private void IntegrateRigidBody(double h)
		{
			var psi = _heading;
			var u = _surge;
			var v = _sway;
			var r = _yawRate;

			Derivatives(psi, u, v, r, true, out var n1, out var e1, out var u1, out var v1, out var r1);
			Derivatives(psi + 0.5 * h * r, u + 0.5 * h * u1, v + 0.5 * h * v1, r + 0.5 * h * r1, false, out var n2, out var e2, out var u2, out var v2, out var r2);
			Derivatives(psi + 0.5 * h * r2, u + 0.5 * h * u2, v + 0.5 * h * v2, r + 0.5 * h * r2, false, out var n3, out var e3, out var u3, out var v3, out var r3);
			Derivatives(psi + h * r3, u + h * u3, v + h * v3, r + h * r3, false, out var n4, out var e4, out var u4, out var v4, out var r4);

			var sixth = h / 6.0;
			_north += sixth * (n1 + 2.0 * n2 + 2.0 * n3 + n4);
			_east += sixth * (e1 + 2.0 * e2 + 2.0 * e3 + e4);
			_heading = Angles.WrapTwoPi(psi + sixth * (r + 2.0 * (r + 0.5 * h * r1) + 2.0 * (r + 0.5 * h * r2) + (r + h * r3)));
			_surge = u + sixth * (u1 + 2.0 * u2 + 2.0 * u3 + u4);
			_sway = v + sixth * (v1 + 2.0 * v2 + 2.0 * v3 + v4);
			_yawRate = r + sixth * (r1 + 2.0 * r2 + 2.0 * r3 + r4);
			_surgeAcceleration = u1;
		}

		// Rates of the planar state for given heading and velocities through the water, with the
		// machinery, rudder angle and environment held for the step.
		private void Derivatives(double heading, double u, double v, double r, bool record,
			out double northRate, out double eastRate, out double surgeRate, out double swayRate, out double yawAcceleration)
		{
			_hull.Forces(u, v, r, _factors, out var hullX, out var hullY, out var hullN);
			Appendages(PortSide, _portShaftSpeed, u, v, r, out var portPropX, out var portPropY, out var portPropN, out var portRudderX, out var portRudderY, out var portRudderN);
			Appendages(StarboardSide, _starboardShaftSpeed, u, v, r, out var stbdPropX, out var stbdPropY, out var stbdPropN, out var stbdRudderX, out var stbdRudderY, out var stbdRudderN);

			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			northRate = u * cos - v * sin + _current.North;
			eastRate = u * sin + v * cos + _current.East;
			_windage.Forces(_wind, new HorizontalVector(northRate, eastRate), heading, u, v, out var windX, out var windY, out var windN);

			var x = hullX + portPropX + stbdPropX + portRudderX + stbdRudderX + windX;
			var y = hullY + portPropY + stbdPropY + portRudderY + stbdRudderY + windY;
			var n = hullN + portPropN + stbdPropN + portRudderN + stbdRudderN + windN;

			// MMG equations of motion at midships (Yasukawa and Yoshimura 2015, eq. 4).
			var m = _mass;
			var xg = _centreOfGravityX;
			surgeRate = (x + _swayMass * v * r + xg * m * r * r) / _surgeMass;
			var swayLoad = y - _surgeMass * u * r;
			var yawLoad = n - xg * m * u * r;
			swayRate = (_a22 * swayLoad - _a12 * yawLoad) / _determinant;
			yawAcceleration = (_a11 * yawLoad - _a12 * swayLoad) / _determinant;

			if (record)
			{
				_forces = new ForceBreakdown(
					hullX, hullY, hullN,
					portPropX + stbdPropX, portPropY + stbdPropY, portPropN + stbdPropN,
					portRudderX + stbdRudderX, portRudderY + stbdRudderY, portRudderN + stbdRudderN,
					windX, windY, windN);
			}
		}

		private void Appendages(int side, double shaftSpeed, double u, double v, double r,
			out double propX, out double propY, out double propN, out double rudderX, out double rudderY, out double rudderN)
		{
			var advance = _propeller.AdvanceSpeed(side, u, r);
			_propeller.Evaluate(shaftSpeed, advance, out var thrust, out _);
			var hullThrust = thrust * _propeller.HullThrustFactor(thrust);
			var walk = _propeller.PropWalkForce(side, thrust);
			propX = hullThrust;
			propY = walk;
			propN = -_propeller.LateralPosition(side) * hullThrust + _propeller.LongitudinalPosition * walk;
			var jet = _propeller.SlipstreamSpeed(thrust, advance);
			_rudder.Forces(side, _rudderAngle, v, r, advance, jet, _propeller.SlipstreamFactor, _propeller.UpstreamInflowFactor,
				out rudderX, out rudderY, out rudderN);
		}

		private void Publish(double time)
		{
			var cos = Math.Cos(_heading);
			var sin = Math.Sin(_heading);
			var groundSurge = _surge + _current.North * cos + _current.East * sin;
			var groundSway = _sway - _current.North * sin + _current.East * cos;
			State = new VesselState(time, _north, _east, _motion.Heave, _motion.Roll, _motion.Pitch, _heading,
				groundSurge, groundSway, _motion.HeaveRate, _motion.RollRate, _motion.PitchRate, _yawRate);

			var ground = State.GroundVelocity;
			var speedOverGround = ground.Magnitude;
			Telemetry = new VesselTelemetry(
				Gauges(_portDriveline, _portControl),
				Gauges(_starboardDriveline, _starboardControl),
				_steering.Angle,
				_steering.Angle,
				_surge,
				speedOverGround,
				speedOverGround > 1e-9 ? ground.Direction : _heading,
				_depth - KeelDraft,
				0.0);
		}

		private static EngineTelemetry Gauges(Driveline driveline, EngineControl control) =>
			new EngineTelemetry(driveline.EngineSpeed, driveline.ShaftSpeed, control.Gear, control.Throttle);
	}
}
