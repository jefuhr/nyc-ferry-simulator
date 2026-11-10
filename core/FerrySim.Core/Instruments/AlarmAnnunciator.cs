using System;
using System.Collections.Generic;

namespace FerrySim.Core.Instruments
{
	/// <summary>Allocation-free alarm state machine with independent silence and a bounded pollable log.</summary>
	public sealed class AlarmAnnunciator
	{
		public const int EventCapacity = 256;
		private readonly string[] _ids;
		private readonly AlarmGroup[] _groups;
		private readonly double[] _repeat;
		private readonly AlarmPhase[] _phases;
		private readonly bool[] _silenced;
		private readonly double[] _acknowledgedAt;
		private readonly AlarmEvent[] _events = new AlarmEvent[EventCapacity];
		private double _time;
		private long _step;

		public AlarmAnnunciator(IReadOnlyList<AlarmDefinition> definitions)
		{
			if (definitions == null) throw new ArgumentNullException(nameof(definitions));
			var count = definitions.Count;
			_ids = new string[count]; _groups = new AlarmGroup[count]; _repeat = new double[count];
			_phases = new AlarmPhase[count]; _silenced = new bool[count]; _acknowledgedAt = new double[count];
			for (var i = 0; i < count; i++)
			{
				var d = definitions[i] ?? throw new ArgumentException("Null alarm definition.");
				if (string.IsNullOrWhiteSpace(d.Id)) throw new ArgumentException("Alarm id is required.");
				for (var j = 0; j < i; j++) if (_ids[j] == d.Id) throw new ArgumentException("Duplicate alarm id: " + d.Id);
				_ids[i] = d.Id; _groups[i] = d.Group; _repeat[i] = d.ReRaiseSeconds?.Value ?? 0;
				if (!Enum.IsDefined(typeof(AlarmGroup), d.Group) || double.IsNaN(_repeat[i]) || double.IsInfinity(_repeat[i]) || _repeat[i] < 0)
					throw new ArgumentException("Invalid alarm group or repeat interval.");
			}
		}

		public int AlarmCount => _ids.Length;
		public string AlarmId(int alarm) => _ids[alarm];
		public AlarmGroup Group(int alarm) => _groups[alarm];
		public AlarmPhase Phase(int alarm) => _phases[alarm];
		public bool LampTest { get; private set; }
		public long FirstEventSequence => Math.Max(0, NextEventSequence - EventCapacity);
		public long NextEventSequence { get; private set; }

		public AlarmEvent EventAt(long sequence)
		{
			if (sequence < FirstEventSequence || sequence >= NextEventSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
			return _events[sequence % EventCapacity];
		}

		/// <summary>Call for each alarm at the current simulation time and physics step.</summary>
		public void Step(int alarm, bool condition, double time, long step)
		{
			_time = time; _step = step;
			var phase = _phases[alarm];
			if (condition)
			{
				if (phase == AlarmPhase.Normal || phase == AlarmPhase.RingBack)
				{
					_phases[alarm] = AlarmPhase.Unacknowledged; _silenced[alarm] = false;
					Log(alarm, AlarmEventKind.Raised);
				}
				else if (phase == AlarmPhase.Acknowledged && _repeat[alarm] > 0 && time - _acknowledgedAt[alarm] >= _repeat[alarm])
				{
					_phases[alarm] = AlarmPhase.Unacknowledged; _silenced[alarm] = false;
					Log(alarm, AlarmEventKind.ReRaised);
				}
			}
			else if (phase == AlarmPhase.Unacknowledged || phase == AlarmPhase.Acknowledged)
			{
				_phases[alarm] = phase == AlarmPhase.Unacknowledged ? AlarmPhase.RingBack : AlarmPhase.Normal;
				_silenced[alarm] = false;
				Log(alarm, AlarmEventKind.Cleared);
			}
		}

		public bool SounderOn(AlarmGroup group)
		{
			for (var i = 0; i < AlarmCount; i++)
				if (_groups[i] == group && _phases[i] == AlarmPhase.Unacknowledged && !_silenced[i]) return true;
			return false;
		}

		/// <summary>Whether this alarm's lamp is lit at <paramref name="time"/>; see <see cref="LampLit"/>.</summary>
		public bool LampOn(int alarm, double time) => LampLit(_phases[alarm], LampTest, time);

		/// <summary>
		/// The one viewer flash rhythm: 1 Hz while unacknowledged, 0.5 Hz on ring-back, both at 50 percent
		/// duty; steady while acknowledged; every lamp lit during a lamp test. Pure, so any viewer holding
		/// only an <see cref="IInstrumentReadout"/> gets the same answer from its <c>Phase</c>, its
		/// <c>LampTest</c> and the simulation time of the state it is showing. Using simulation time means
		/// every lamp flashes in step and a paused run freezes the flash.
		/// </summary>
		public static bool LampLit(AlarmPhase phase, bool lampTest, double time)
		{
			if (lampTest) return true;
			switch (phase)
			{
				case AlarmPhase.Acknowledged: return true;
				case AlarmPhase.Unacknowledged: return Math.Floor(time * 2) % 2 == 0;
				case AlarmPhase.RingBack: return Math.Floor(time) % 2 == 0;
				default: return false;
			}
		}

		public void Acknowledge(AlarmGroup group)
		{
			for (var i = 0; i < AlarmCount; i++)
			{
				if (_groups[i] != group) continue;
				if (_phases[i] == AlarmPhase.Unacknowledged)
				{
					_phases[i] = AlarmPhase.Acknowledged; _acknowledgedAt[i] = _time;
					Log(i, AlarmEventKind.Acknowledged);
				}
				else if (_phases[i] == AlarmPhase.RingBack)
				{
					_phases[i] = AlarmPhase.Normal;
					Log(i, AlarmEventKind.Acknowledged);
				}
			}
		}

		public void Silence(AlarmGroup group)
		{
			for (var i = 0; i < AlarmCount; i++)
			{
				if (_groups[i] != group || _phases[i] != AlarmPhase.Unacknowledged || _silenced[i]) continue;
				_silenced[i] = true; Log(i, AlarmEventKind.Silenced);
			}
		}

		public void SetLampTest(bool enabled, AlarmGroup group = AlarmGroup.Steering)
		{
			if (enabled == LampTest) return;
			LampTest = enabled;
			Log(-1, enabled ? AlarmEventKind.LampTestOn : AlarmEventKind.LampTestOff, group);
		}

		public void Reset()
		{
			Array.Clear(_phases, 0, _phases.Length); Array.Clear(_silenced, 0, _silenced.Length);
			Array.Clear(_acknowledgedAt, 0, _acknowledgedAt.Length);
			LampTest = false; _time = 0; _step = 0; NextEventSequence = 0;
		}

		private void Log(int alarm, AlarmEventKind kind, AlarmGroup group = AlarmGroup.Steering)
		{
			var sequence = NextEventSequence++;
			_events[sequence % EventCapacity] = new AlarmEvent(sequence, _step, alarm, alarm < 0 ? group : _groups[alarm], kind);
		}
	}
}
