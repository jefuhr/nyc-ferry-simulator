using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Instruments
{
	/// <summary>Read-only display model; warm-running initial values, then lags driven by simulation time.</summary>
	public sealed class InstrumentModel : IInstrumentReadout
	{
		private readonly double[] _values = new double[ChannelNames.Count];
		private readonly Curve[] _curves;
		private readonly Trigger[] _triggers;
		private readonly double[] _discrepancySince;
		private readonly double[] _lastOrders;
		private readonly AlarmAnnunciator _alarms;
		private readonly double _idleSpeed;
		private readonly double _speedRange;
		private readonly double _busVoltage;
		private readonly double _loadTimeConstant;
		private bool _initialized;
		private double _lastTime;

		private readonly struct Curve
		{
			private readonly double _idle, _speedRise, _loadRise, _tau;
			public Curve(EngineInstrumentCurve p) { _idle = p.Idle.Value; _speedRise = p.SpeedRise.Value; _loadRise = p.LoadRise.Value; _tau = p.TimeConstant.Value; }
			public double Target(double speed, double load) => _idle + _speedRise * speed + _loadRise * load;
			public double Alpha(double dt) => 1 - Math.Exp(-dt / _tau);
		}

		private readonly struct Trigger
		{
			public readonly string Condition;
			public readonly double Threshold, Delay;
			public Trigger(AlarmDefinition alarm) { Condition = alarm.Condition; Threshold = alarm.Threshold.Value; Delay = alarm.DelaySeconds?.Value ?? 0; }
		}

		public InstrumentModel(InstrumentParameters parameters)
		{
			if (parameters == null) throw new ArgumentNullException(nameof(parameters));
			parameters.Validate();
			_idleSpeed = parameters.IdleSpeed.Value; _speedRange = parameters.RatedSpeed.Value - _idleSpeed;
			_busVoltage = parameters.BusVoltage.Value; _loadTimeConstant = parameters.LoadTimeConstant.Value;
			_curves = new[] { new Curve(parameters.OilPressure), new Curve(parameters.CoolantTemperature), new Curve(parameters.ExhaustTemperature), new Curve(parameters.BoostPressure), new Curve(parameters.FuelRate), new Curve(parameters.GearOilPressure) };
			_alarms = new AlarmAnnunciator(parameters.Alarms);
			_triggers = new Trigger[parameters.Alarms.Count]; _discrepancySince = new double[_triggers.Length]; _lastOrders = new double[_triggers.Length];
			for (var i = 0; i < _triggers.Length; i++) _triggers[i] = new Trigger(parameters.Alarms[i]);
			Reset();
		}

		/// <summary>Reset with each run. Viewer actions and engine lag state never enter a recording or digest.</summary>
		public void Reset()
		{
			for (var i = 0; i < _values.Length; i++) _values[i] = double.NaN;
			for (var i = 0; i < _discrepancySince.Length; i++) _discrepancySince[i] = double.NaN;
			_alarms.Reset(); _initialized = false; _lastTime = 0;
		}

		public void Step(in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls, IInstrumentTaps taps)
		{
			if (taps == null) throw new ArgumentNullException(nameof(taps));
			if (double.IsNaN(state.Time) || double.IsInfinity(state.Time) || state.Time < 0) throw new ArgumentOutOfRangeException(nameof(state));
			if (_initialized && state.Time < _lastTime) Reset();
			var dt = _initialized ? state.Time - _lastTime : 0;
			Set(Channel.LeverPort, controls.PortLever); Set(Channel.LeverStarboard, controls.StarboardLever);
			Set(Channel.TillerOrder, controls.Helm); Set(Channel.SteeringJog, controls.Jog);
			Set(Channel.NfuActive, taps.NfuActive ? 1 : 0); Set(Channel.RudderOrder, taps.RudderOrder);
			Set(Channel.RudderPort, telemetry.PortRudderAngle); Set(Channel.RudderStarboard, telemetry.StarboardRudderAngle);
			Set(Channel.Heading, Angles.WrapTwoPi(state.Heading)); Set(Channel.RateOfTurn, state.YawRate);
			Set(Channel.CourseOverGround, Angles.WrapTwoPi(telemetry.CourseOverGround)); Set(Channel.SpeedOverGround, telemetry.SpeedOverGround);
			Set(Channel.SpeedThroughWater, telemetry.SpeedThroughWater); Set(Channel.DepthUnderKeel, telemetry.DepthUnderKeel);
			var loadAlpha = 1 - Math.Exp(-dt / _loadTimeConstant);
			Engine((int)Channel.EnginePortSpeed, telemetry.Port, (double)taps.PortShiftPhase, taps.PortEngineLoad, loadAlpha);
			Engine((int)Channel.EngineStarboardSpeed, telemetry.Starboard, (double)taps.StarboardShiftPhase, taps.StarboardEngineLoad, loadAlpha);
			for (var curve = 0; curve < _curves.Length; curve++)
			{
				var alpha = _curves[curve].Alpha(dt);
				Reading((int)Channel.EnginePortOilPressure + curve, _curves[curve], telemetry.Port.EngineSpeed, taps.PortEngineLoad, alpha);
				Reading((int)Channel.EngineStarboardOilPressure + curve, _curves[curve], telemetry.Starboard.EngineSpeed, taps.StarboardEngineLoad, alpha);
			}
			Set(Channel.BusVoltage, _busVoltage);
			var step = (long)Math.Round(state.Time * SimulationClock.StepsPerSecond);
			for (var i = 0; i < _triggers.Length; i++)
			{
				var trigger = _triggers[i];
				bool condition;
				switch (trigger.Condition)
				{
					case "shallow-water": condition = telemetry.DepthUnderKeel < trigger.Threshold; break;
					case "grounding": condition = telemetry.DepthUnderKeel < 0; break;
					case "contact": condition = telemetry.ContactForce > trigger.Threshold; break;
					default:
						var discrepancy = Math.Max(Math.Abs(taps.RudderOrder - telemetry.PortRudderAngle), Math.Abs(taps.RudderOrder - telemetry.StarboardRudderAngle));
						if (discrepancy > trigger.Threshold)
						{
							if (double.IsNaN(_discrepancySince[i]) || taps.RudderOrder != _lastOrders[i]) _discrepancySince[i] = state.Time;
						}
						else _discrepancySince[i] = double.NaN;
						// The healthy gear can take a complete hard-over swing before a timeout is possible.
						var transit = 2 * taps.HardOverAngle / taps.SteeringRate;
						_lastOrders[i] = taps.RudderOrder;
						condition = state.Time - _discrepancySince[i] > transit + trigger.Delay;
						break;
				}
				_alarms.Step(i, condition, state.Time, step);
			}
			_initialized = true; _lastTime = state.Time;
		}

		private void Engine(int first, EngineTelemetry engine, double phase, double load, double alpha)
		{
			_values[first] = engine.EngineSpeed; _values[first + 1] = engine.ShaftSpeed; _values[first + 2] = phase;
			Lag(first + 3, Math.Clamp(load, 0, 1), alpha);
		}

		private void Reading(int channel, Curve curve, double engineSpeed, double load, double alpha) =>
			Lag(channel, curve.Target(Math.Clamp((engineSpeed - _idleSpeed) / _speedRange, 0, 1), Math.Clamp(load, 0, 1)), alpha);

		private void Lag(int channel, double target, double alpha) => _values[channel] = _initialized ? _values[channel] + alpha * (target - _values[channel]) : target;
		private void Set(Channel channel, double value) => _values[(int)channel] = value;
		public double Value(Channel channel) => _values[(int)channel];
		public int AlarmCount => _alarms.AlarmCount;
		public string AlarmId(int alarm) => _alarms.AlarmId(alarm);
		public AlarmGroup Group(int alarm) => _alarms.Group(alarm);
		public AlarmPhase Phase(int alarm) => _alarms.Phase(alarm);
		public bool SounderOn(AlarmGroup group) => _alarms.SounderOn(group);
		public bool LampTest => _alarms.LampTest;
		public bool LampOn(int alarm, double time) => _alarms.LampOn(alarm, time);
		public long FirstEventSequence => _alarms.FirstEventSequence;
		public long NextEventSequence => _alarms.NextEventSequence;
		public AlarmEvent EventAt(long sequence) => _alarms.EventAt(sequence);
		public void Acknowledge(AlarmGroup group) => _alarms.Acknowledge(group);
		public void Silence(AlarmGroup group) => _alarms.Silence(group);
		public void SetLampTest(bool enabled, AlarmGroup group = AlarmGroup.Steering) => _alarms.SetLampTest(enabled, group);
		public void Acknowledge() { for (var i = 0; i < AlarmCount; i++) Acknowledge(Group(i)); }
		public void Silence() { for (var i = 0; i < AlarmCount; i++) Silence(Group(i)); }
	}
}
