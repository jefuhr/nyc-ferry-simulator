using System;
using FerrySim.Core.Instruments;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// An <see cref="IInstrumentReadout"/> whose values and alarm phases are set by hand, for tests, captures and dev
	/// scenes that need a display without a running vessel. Every channel starts not available (NaN); the event log
	/// is empty. It is not a model: nothing moves unless the caller moves it.
	/// </summary>
	public sealed class FakeInstrumentReadout : IInstrumentReadout
	{
		private readonly double[] _values = new double[ChannelNames.Count];
		private readonly string[] _ids;
		private readonly AlarmGroup[] _groups;
		private readonly AlarmPhase[] _phases;

		public FakeInstrumentReadout(params (string id, AlarmGroup group)[] alarms)
		{
			alarms = alarms ?? new (string, AlarmGroup)[0];
			_ids = new string[alarms.Length];
			_groups = new AlarmGroup[alarms.Length];
			_phases = new AlarmPhase[alarms.Length];
			for (var i = 0; i < alarms.Length; i++)
			{
				_ids[i] = alarms[i].id;
				_groups[i] = alarms[i].group;
			}

			for (var i = 0; i < _values.Length; i++)
			{
				_values[i] = double.NaN;
			}
		}

		/// <summary>The four alarms of vessels/instruments/t-class.json, in its order, all normal; no readings.</summary>
		public static FakeInstrumentReadout TClass() => new FakeInstrumentReadout(
			("navigation.shallow-water", AlarmGroup.Navigation),
			("hull.grounding", AlarmGroup.Hull),
			("hull.contact", AlarmGroup.Hull),
			("steering.failure", AlarmGroup.Steering));

		/// <summary>
		/// A T-class under way at half ahead on both engines with plausible warm readings, set in display units so the
		/// captures show round numbers. These are illustrative, not the instrument model's curves.
		/// </summary>
		public static FakeInstrumentReadout Underway()
		{
			var readout = TClass();
			readout.Set(Channel.LeverPort, 0.55);
			readout.Set(Channel.LeverStarboard, 0.5);
			readout.SetEngine(Channel.EnginePortSpeed, rpm: 1480, shaftRpm: 705, load: 0.58, oilPsi: 58, coolantF: 182, exhaustF: 742, boostPsi: 17, gallonsPerHour: 21.4, gearOilPsi: 232);
			readout.SetEngine(Channel.EngineStarboardSpeed, rpm: 1440, shaftRpm: 686, load: 0.55, oilPsi: 57, coolantF: 180, exhaustF: 728, boostPsi: 16, gallonsPerHour: 20.1, gearOilPsi: 230);
			readout.Set(Channel.EnginePortShiftPhase, 4);
			readout.Set(Channel.EngineStarboardShiftPhase, 4);
			readout.Set(Channel.BusVoltage, 27.6);
			readout.Set(Channel.SpeedOverGround, SkinUnits.ToSi(DisplayUnit.Knots, 14.2));
			readout.Set(Channel.SpeedThroughWater, SkinUnits.ToSi(DisplayUnit.Knots, 13.6));
			readout.Set(Channel.DepthUnderKeel, SkinUnits.ToSi(DisplayUnit.Feet, 31.0));
			return readout;
		}

		public bool LampTest { get; set; }

		public int AlarmCount => _ids.Length;

		public long FirstEventSequence => 0;

		public long NextEventSequence => 0;

		public void Set(Channel channel, double value) => _values[(int)channel] = value;

		public void SetPhase(int alarm, AlarmPhase phase) => _phases[alarm] = phase;

		public void SetPhase(string id, AlarmPhase phase)
		{
			var index = Array.IndexOf(_ids, id);
			if (index < 0)
			{
				throw new ArgumentException("this readout defines no alarm '" + id + "'", nameof(id));
			}

			_phases[index] = phase;
		}

		public double Value(Channel channel) => _values[(int)channel];

		public string AlarmId(int alarm) => _ids[alarm];

		public AlarmGroup Group(int alarm) => _groups[alarm];

		public AlarmPhase Phase(int alarm) => _phases[alarm];

		/// <summary>On while any alarm of the group is unacknowledged (this fake has no silence).</summary>
		public bool SounderOn(AlarmGroup group)
		{
			for (var i = 0; i < _ids.Length; i++)
			{
				if (_groups[i] == group && _phases[i] == AlarmPhase.Unacknowledged)
				{
					return true;
				}
			}

			return false;
		}

		public AlarmEvent EventAt(long sequence) =>
			throw new ArgumentOutOfRangeException(nameof(sequence), "a fake readout keeps no events");

		/// <summary>One engine's channels; <paramref name="speed"/> is its EngineXSpeed channel and the rest follow in C1 order.</summary>
		private void SetEngine(Channel speed, double rpm, double shaftRpm, double load, double oilPsi, double coolantF, double exhaustF, double boostPsi, double gallonsPerHour, double gearOilPsi)
		{
			var offset = (int)speed - (int)Channel.EnginePortSpeed;
			Set(Channel.EnginePortSpeed + offset, SkinUnits.ToSi(DisplayUnit.Rpm, rpm));
			Set(Channel.EnginePortShaftSpeed + offset, SkinUnits.ToSi(DisplayUnit.Rpm, shaftRpm));
			Set(Channel.EnginePortLoad + offset, load);
			Set(Channel.EnginePortOilPressure + offset, SkinUnits.ToSi(DisplayUnit.Psi, oilPsi));
			Set(Channel.EnginePortCoolantTemperature + offset, SkinUnits.ToSi(DisplayUnit.Fahrenheit, coolantF));
			Set(Channel.EnginePortExhaustTemperature + offset, SkinUnits.ToSi(DisplayUnit.Fahrenheit, exhaustF));
			Set(Channel.EnginePortBoostPressure + offset, SkinUnits.ToSi(DisplayUnit.Psi, boostPsi));
			Set(Channel.EnginePortFuelRate + offset, SkinUnits.ToSi(DisplayUnit.GallonsPerHour, gallonsPerHour));
			Set(Channel.EnginePortGearOilPressure + offset, SkinUnits.ToSi(DisplayUnit.Psi, gearOilPsi));
		}
	}
}
