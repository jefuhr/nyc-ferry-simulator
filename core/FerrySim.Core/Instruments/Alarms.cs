using System;

namespace FerrySim.Core.Instruments
{
	/// <summary>Which sounder and panel an alarm belongs to.</summary>
	public enum AlarmGroup
	{
		Steering,
		Engine,
		Navigation,
		Hull,
	}

	/// <summary>Where one alarm is in its raise, acknowledge and clear sequence.</summary>
	public enum AlarmPhase
	{
		/// <summary>The condition is absent and nothing is waiting. Lamp off.</summary>
		Normal,

		/// <summary>The condition has been seen and not yet acknowledged. Lamp flashes and the sounder runs.</summary>
		Unacknowledged,

		/// <summary>Acknowledged while the condition persists. Lamp steady, sounder off.</summary>
		Acknowledged,

		/// <summary>The condition went away before anyone acknowledged it. Lamp flashes slowly until acknowledged.</summary>
		RingBack,
	}

	/// <summary>What happened to an alarm, or to the panel as a whole.</summary>
	public enum AlarmEventKind
	{
		Raised,
		Acknowledged,
		Silenced,
		Cleared,

		/// <summary>An acknowledged alarm sounded again because its repeat interval ran out.</summary>
		ReRaised,
		LampTestOn,
		LampTestOff,
	}

	/// <summary>
	/// One entry in the alarm event log. Consumers (audio, the head-up display, the debrief) each keep
	/// their own cursor into the log and poll it; nothing is pushed, so no consumer can stall another.
	/// </summary>
	public readonly struct AlarmEvent : IEquatable<AlarmEvent>
	{
		public AlarmEvent(long sequence, long step, int alarm, AlarmGroup group, AlarmEventKind kind)
		{
			Sequence = sequence;
			Step = step;
			Alarm = alarm;
			Group = group;
			Kind = kind;
		}

		/// <summary>Position in the log, counting from zero at the start of the run and never reused.</summary>
		public long Sequence { get; }

		/// <summary>Physics step, counted from the start of the run, at which the event happened.</summary>
		public long Step { get; }

		/// <summary>Index of the alarm in <see cref="IInstrumentReadout"/>, or -1 for a panel-wide event such as a lamp test.</summary>
		public int Alarm { get; }

		/// <summary>The alarm's group. For a panel-wide event this is the group whose panel was tested.</summary>
		public AlarmGroup Group { get; }

		public AlarmEventKind Kind { get; }

		public bool Equals(AlarmEvent other) =>
			Sequence == other.Sequence && Step == other.Step && Alarm == other.Alarm && Group == other.Group && Kind == other.Kind;

		public override bool Equals(object obj) => obj is AlarmEvent other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(Sequence, Step, Alarm, Group, Kind);

		public override string ToString() => $"#{Sequence} step={Step} alarm={Alarm} {Group} {Kind}";
	}
}
