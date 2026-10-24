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
			double windX, double windY, double windN,
			double contactX, double contactY, double contactN)
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
			ContactX = contactX;
			ContactY = contactY;
			ContactN = contactN;
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

		public double ContactX { get; }

		public double ContactY { get; }

		public double ContactN { get; }
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
	/// <para>
	/// With world geometry, each step queries the structures near the hull (up to
	/// <see cref="MaximumSegments"/>). When some part of the outline is in contact or could reach a face within
	/// the step, the rigid body advances in ten 1 ms Runge-Kutta substeps with the contact forces of
	/// <see cref="ContactModel"/> added; otherwise the step is exactly the open-water one.
	/// </para>
	/// </summary>
	public sealed class VesselDynamicsSimulation : ISimulation
	{
		/// <summary>
		/// Most structure segments one step takes into account: the first this many the geometry returns within
		/// the query radius (the hull's bounding radius, plus its reach in the step, plus 5 m). Past it the rest
		/// are left out and <see cref="SegmentQuerySaturated"/> says so. The buffer and the contact model are
		/// sized for it when the simulation is built.
		/// </summary>
		public const int MaximumSegments = 4096;

		private const int DrivelineSubsteps = 4;
		private const int PortSide = -1;
		private const int StarboardSide = 1;

		// Contact: rigid-body substeps in a step that may touch, the share of the explicit stability limit the
		// friction and hysteresis regularisation may use, the slack added to the distance the hull can travel
		// in a step, and the query margin around the hull.
		private const int ContactSubsteps = 10;
		private const double FrictionStability = 0.5;
		private const double ReachMargin = 0.02;
		private const double QueryMargin = 5.0;

		private readonly IEnvironment _environment;
		private readonly IWorldGeometry _geometry;
		private readonly ContactModel _contact;
		private readonly double _contactMass;

		// One more than the most segments used, so a query that fills it is known to have left some out.
		private readonly ContactSegment[] _segments;
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
		private ContactLoad _contactLoad;
		private bool _segmentQuerySaturated;
		private long _saturatedSegmentQueries;

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
		private bool _contactActive;
		private double _slipSpeed;

		/// <summary>A vessel in open water, with no fixed structures to touch.</summary>
		public VesselDynamicsSimulation(VesselParameters parameters, IEnvironment environment, in VesselState initialState)
			: this(parameters, environment, EmptyWorldGeometry.Instance, initialState)
		{
		}

		/// <summary>
		/// A vessel whose hull can touch the structures in <paramref name="geometry"/>. Any geometry other than
		/// <see cref="EmptyWorldGeometry"/> needs the parameter file's <c>contact</c> section.
		/// </summary>
		public VesselDynamicsSimulation(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState)
		{
			Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
			_environment = environment ?? throw new ArgumentNullException(nameof(environment));
			_geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));

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

			_segments = Array.Empty<ContactSegment>();
			if (!(geometry is EmptyWorldGeometry))
			{
				_contact = new ContactModel(parameters, MaximumSegments);
				_segments = new ContactSegment[MaximumSegments + 1];

				// A conservative effective mass for a tangential push anywhere on the outline: the lighter
				// translational mass with the yaw inertia acting at the farthest outline point.
				var translational = Math.Min(_mass + _hull.SurgeAddedMass, _mass + _hull.SwayAddedMass);
				var lever = _contact.BoundingRadius + Math.Abs(_centreOfGravityX);
				_contactMass = 1.0 / (1.0 / translational + lever * lever / (_yawInertia + _hull.YawAddedInertia));
			}

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

		/// <summary>The hull outline and contact force model; null in open water.</summary>
		public ContactModel Contact => _contact;

		/// <summary>Resultant contact load at the end of the last step; zero when nothing touches.</summary>
		public ContactLoad ContactLoad => _contactLoad;

		/// <summary>Number of segments touching the hull at the end of the last step.</summary>
		public int ContactCount => _contact == null ? 0 : _contact.ContactCount;

		/// <summary>
		/// True when the last query of nearby structures (at the start of the last step, or at reset) found more
		/// than <see cref="MaximumSegments"/> segments, so some were left out of contact.
		/// </summary>
		public bool SegmentQuerySaturated => _segmentQuerySaturated;

		/// <summary>Number of queries since the last reset (including its own) that found more than <see cref="MaximumSegments"/> segments.</summary>
		public long SaturatedSegmentQueries => _saturatedSegmentQueries;

		/// <summary>One touching segment at the end of the last step, <c>0 ≤ index &lt; ContactCount</c>.</summary>
		public SegmentContact GetContact(int index)
		{
			if (_contact == null)
			{
				throw new ArgumentOutOfRangeException(nameof(index));
			}

			return _contact.Contact(index);
		}

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
			_segmentQuerySaturated = false;
			_saturatedSegmentQueries = 0;
			ClearContact();
			if (_contact != null)
			{
				// Nothing the hull overlaps at the start is in contact: contact begins by crossing a face.
				_contact.Reset(_north, _east, _heading);
			}

			if (PrepareContact())
			{
				_slipSpeed = SlipSpeed(SimulationClock.FixedTimeStep / ContactSubsteps);
				RecordContact();
			}

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
			if (PrepareContact())
			{
				IntegrateWithContact(dt);
			}
			else
			{
				IntegrateRigidBody(dt);
				ClearContact();
			}

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
			_surgeAcceleration = PlanarRungeKutta.Step(new RigidBodyRates(this, true), h,
				ref _north, ref _east, ref _heading, ref _surge, ref _sway, ref _yawRate);
		}

		// A step that may touch a structure: ContactSubsteps Runge-Kutta substeps with the contact forces at
		// each stage's pose and velocity. The slip speed regularising friction and hysteresis is set per substep,
		// and the parts of the hull in contact are updated after each one.
		private void IntegrateWithContact(double dt)
		{
			var h = dt / ContactSubsteps;
			_contactActive = true;
			_contact.Begin(_north, _east, _heading);
			for (var substep = 0; substep < ContactSubsteps; substep++)
			{
				_slipSpeed = SlipSpeed(h);
				var first = substep == 0;
				var surgeAcceleration = PlanarRungeKutta.Step(new RigidBodyRates(this, first), h,
					ref _north, ref _east, ref _heading, ref _surge, ref _sway, ref _yawRate);
				_contact.Commit(_north, _east, _heading);
				if (first)
				{
					// The pitch layer models acceleration from thrust below the centre of gravity; contact
					// loads act near the fender line, so they are left out of it.
					_surgeAcceleration = surgeAcceleration - _forces.ContactX / _surgeMass;
				}
			}

			_contactActive = false;
			RecordContact();
		}

		// Queries the structures the hull could reach this step. True when some part of the outline is in
		// contact or may touch one, so the step must integrate contact forces.
		private bool PrepareContact()
		{
			if (_contact == null)
			{
				return false;
			}

			GroundVelocity(out var northRate, out var eastRate);
			var speed = Math.Sqrt(northRate * northRate + eastRate * eastRate) + Math.Abs(_yawRate) * _contact.BoundingRadius;
			var reach = 2.0 * speed * SimulationClock.FixedTimeStep + ReachMargin;
			var radius = _contact.BoundingRadius + reach + QueryMargin;
			var count = _geometry.QuerySegments(_north, _east, radius, _segments);
			_segmentQuerySaturated = count > MaximumSegments;
			if (_segmentQuerySaturated)
			{
				// The buffer filled up: there are more than MaximumSegments nearby, and the rest are left out.
				_saturatedSegmentQueries++;
				count = MaximumSegments;
			}

			_contact.Prepare(_segments, count);
			return _contact.MayTouch(_north, _east, _heading, reach);
		}

		// Slip speed regularising friction and hysteresis over one substep: the floor, raised when the friction
		// and hysteresis the contact carries would make them too stiff for an explicit substep of length h.
		private double SlipSpeed(double h)
		{
			GroundVelocity(out var northRate, out var eastRate);
			var load = _contact.FrictionLoad(_north, _east, _heading, northRate, eastRate, _yawRate);
			return Math.Max(ContactModel.MinimumSlipSpeed, load * h / (FrictionStability * _contactMass));
		}

		// Contact forces and per-segment details at the end of the step, for telemetry.
		private void RecordContact()
		{
			GroundVelocity(out var northRate, out var eastRate);
			_contact.Forces(_north, _east, _heading, northRate, eastRate, _yawRate, _slipSpeed, true, out _, out _, out _);
			_contactLoad = _contact.LastLoad;
		}

		private void ClearContact()
		{
			_contactLoad = default;
			if (_contact != null)
			{
				_contact.ClearRecord();
			}
		}

		private void GroundVelocity(out double northRate, out double eastRate)
		{
			var cos = Math.Cos(_heading);
			var sin = Math.Sin(_heading);
			northRate = _surge * cos - _sway * sin + _current.North;
			eastRate = _surge * sin + _sway * cos + _current.East;
		}

		// Rates of the planar state for given pose and velocities through the water, with the machinery,
		// rudder angle and environment held for the step. The position matters only for contact.
		private void Derivatives(double north, double east, double heading, double u, double v, double r, bool record,
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
			var contactX = 0.0;
			var contactY = 0.0;
			var contactN = 0.0;
			if (_contactActive)
			{
				_contact.Forces(north, east, heading, northRate, eastRate, r, _slipSpeed, false, out contactX, out contactY, out contactN);
				x += contactX;
				y += contactY;
				n += contactN;
			}

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
					windX, windY, windN,
					contactX, contactY, contactN);
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
				_contactLoad.Magnitude);
		}

		private static EngineTelemetry Gauges(Driveline driveline, EngineControl control) =>
			new EngineTelemetry(driveline.EngineSpeed, driveline.ShaftSpeed, control.Gear, control.Throttle);

		// The equations of motion for the integrator; forces are recorded at the first stage when asked.
		private readonly struct RigidBodyRates : IPlanarRates
		{
			private readonly VesselDynamicsSimulation _simulation;
			private readonly bool _record;

			public RigidBodyRates(VesselDynamicsSimulation simulation, bool record)
			{
				_simulation = simulation;
				_record = record;
			}

			public void Rates(double north, double east, double heading, double surge, double sway, double yawRate, bool firstStage,
				out double northRate, out double eastRate, out double surgeRate, out double swayRate, out double yawAcceleration) =>
				_simulation.Derivatives(north, east, heading, surge, sway, yawRate, _record && firstStage,
					out northRate, out eastRate, out surgeRate, out swayRate, out yawAcceleration);
		}
	}
}
