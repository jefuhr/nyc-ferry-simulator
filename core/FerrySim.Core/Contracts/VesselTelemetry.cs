namespace FerrySim.Core.Contracts
{
	/// <summary>Marine gear (clutch) state for one engine.</summary>
	public enum GearState
	{
		Neutral = 0,
		Ahead = 1,
		Astern = 2,

		/// <summary>Clutch is disengaging or engaging; no reliable drive to the shaft.</summary>
		Shifting = 3,
	}

	/// <summary>One engine and its shaft, as the gauges would show them. SI units.</summary>
	public readonly struct EngineTelemetry
	{
		public EngineTelemetry(double engineSpeed, double shaftSpeed, GearState gear, double throttle)
		{
			EngineSpeed = engineSpeed;
			ShaftSpeed = shaftSpeed;
			Gear = gear;
			Throttle = throttle;
		}

		/// <summary>Engine crankshaft speed, rad/s. The UI shows rpm.</summary>
		public double EngineSpeed { get; }

		/// <summary>Propeller shaft speed, rad/s, negative when turning astern.</summary>
		public double ShaftSpeed { get; }

		public GearState Gear { get; }

		/// <summary>Governor demand from 0 (idle) to 1 (rated speed).</summary>
		public double Throttle { get; }
	}

	/// <summary>Derived quantities for instruments, logging and debriefs. SI units, radians.</summary>
	public readonly struct VesselTelemetry
	{
		public VesselTelemetry(
			EngineTelemetry port,
			EngineTelemetry starboard,
			double portRudderAngle,
			double starboardRudderAngle,
			double speedThroughWater,
			double speedOverGround,
			double courseOverGround,
			double depthUnderKeel,
			double contactForce)
		{
			Port = port;
			Starboard = starboard;
			PortRudderAngle = portRudderAngle;
			StarboardRudderAngle = starboardRudderAngle;
			SpeedThroughWater = speedThroughWater;
			SpeedOverGround = speedOverGround;
			CourseOverGround = courseOverGround;
			DepthUnderKeel = depthUnderKeel;
			ContactForce = contactForce;
		}

		public EngineTelemetry Port { get; }

		public EngineTelemetry Starboard { get; }

		/// <summary>Port rudder angle, radians, positive is starboard rudder.</summary>
		public double PortRudderAngle { get; }

		/// <summary>Starboard rudder angle, radians, positive is starboard rudder.</summary>
		public double StarboardRudderAngle { get; }

		/// <summary>Longitudinal speed relative to the water, m/s, negative going astern (speed log).</summary>
		public double SpeedThroughWater { get; }

		/// <summary>Magnitude of velocity over the ground, m/s (GPS SOG).</summary>
		public double SpeedOverGround { get; }

		/// <summary>Direction of velocity over the ground, radians clockwise from true north (GPS COG).</summary>
		public double CourseOverGround { get; }

		/// <summary>Water depth below the keel, metres (depth sounder relative to the keel).</summary>
		public double DepthUnderKeel { get; }

		/// <summary>Magnitude of all contact forces (fenders, collisions) this step, newtons.</summary>
		public double ContactForce { get; }
	}
}
