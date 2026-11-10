namespace FerrySim.Core.Instruments
{
	/// <summary>
	/// Everything a wheelhouse instrument can show, as one flat list. Values are SI (docs/conventions.md)
	/// and <c>double.NaN</c> means "not available". The list is append-only: a member never changes
	/// meaning or position, because display skins and the control map refer to channels by the names in
	/// <see cref="ChannelNames"/>. Convert to knots, rpm, degrees, psi and so on only for display.
	/// </summary>
	public enum Channel
	{
		/// <summary>Port engine lever as applied, [-1, 1]: -1 full astern, +1 full ahead.</summary>
		LeverPort,

		/// <summary>Starboard engine lever as applied, [-1, 1].</summary>
		LeverStarboard,

		/// <summary>Follow-up tiller position as applied, [-1, 1], positive to starboard.</summary>
		TillerOrder,

		/// <summary>Jog (non-follow-up) lever demand as applied, [-1, 1], positive to starboard.</summary>
		SteeringJog,

		/// <summary>1 while a jog lever is overriding the tiller, otherwise 0.</summary>
		NfuActive,

		/// <summary>Rudder angle the steering gear is being asked for, rad, positive to starboard.</summary>
		RudderOrder,

		/// <summary>Actual port rudder angle, rad, positive to starboard.</summary>
		RudderPort,

		/// <summary>Actual starboard rudder angle, rad, positive to starboard.</summary>
		RudderStarboard,

		/// <summary>True heading, rad, [0, 2π).</summary>
		Heading,

		/// <summary>Rate of turn, rad/s, positive when the bow swings to starboard.</summary>
		RateOfTurn,

		/// <summary>Course over ground, rad, [0, 2π).</summary>
		CourseOverGround,

		/// <summary>Speed over ground, m/s.</summary>
		SpeedOverGround,

		/// <summary>Speed through the water, m/s.</summary>
		SpeedThroughWater,

		/// <summary>Depth of water under the keel, m.</summary>
		DepthUnderKeel,

		/// <summary>Port engine crankshaft speed, rad/s.</summary>
		EnginePortSpeed,

		/// <summary>Port propeller shaft speed, rad/s, negative astern.</summary>
		EnginePortShaftSpeed,

		/// <summary>Port gearbox shift phase, as the numeric value of the control model's shift phase.</summary>
		EnginePortShiftPhase,

		/// <summary>Port engine load, ratio in [0, 1] of the torque available at the current speed.</summary>
		EnginePortLoad,

		/// <summary>Port engine lubricating oil pressure, N/m2 (gauge).</summary>
		EnginePortOilPressure,

		/// <summary>Port engine coolant temperature, K.</summary>
		EnginePortCoolantTemperature,

		/// <summary>Port engine exhaust gas temperature, K.</summary>
		EnginePortExhaustTemperature,

		/// <summary>Port engine boost (charge air) pressure, N/m2 (gauge).</summary>
		EnginePortBoostPressure,

		/// <summary>Port engine fuel consumption, kg/s.</summary>
		EnginePortFuelRate,

		/// <summary>Port gearbox oil pressure, N/m2 (gauge).</summary>
		EnginePortGearOilPressure,

		/// <summary>Starboard engine crankshaft speed, rad/s.</summary>
		EngineStarboardSpeed,

		/// <summary>Starboard propeller shaft speed, rad/s, negative astern.</summary>
		EngineStarboardShaftSpeed,

		/// <summary>Starboard gearbox shift phase, as the numeric value of the control model's shift phase.</summary>
		EngineStarboardShiftPhase,

		/// <summary>Starboard engine load, ratio in [0, 1].</summary>
		EngineStarboardLoad,

		/// <summary>Starboard engine lubricating oil pressure, N/m2 (gauge).</summary>
		EngineStarboardOilPressure,

		/// <summary>Starboard engine coolant temperature, K.</summary>
		EngineStarboardCoolantTemperature,

		/// <summary>Starboard engine exhaust gas temperature, K.</summary>
		EngineStarboardExhaustTemperature,

		/// <summary>Starboard engine boost (charge air) pressure, N/m2 (gauge).</summary>
		EngineStarboardBoostPressure,

		/// <summary>Starboard engine fuel consumption, kg/s.</summary>
		EngineStarboardFuelRate,

		/// <summary>Starboard gearbox oil pressure, N/m2 (gauge).</summary>
		EngineStarboardGearOilPressure,

		/// <summary>Main electrical bus voltage, V.</summary>
		BusVoltage,
	}
}
