namespace FerrySim.Core.Instruments
{
	/// <summary>
	/// What the instrument model shows at the end of the most recent physics step. The console screens,
	/// the head-up display, the moving wheelhouse controls and the audio all read this one object, so
	/// they cannot disagree. Reading it never allocates and never changes the simulation: the instrument
	/// model sits outside the state digest.
	/// </summary>
	public interface IInstrumentReadout
	{
		/// <summary>The current value of <paramref name="channel"/> in SI units, or <c>double.NaN</c> when it is not available.</summary>
		double Value(Channel channel);

		/// <summary>Number of alarms this vessel defines. Alarm indices run from 0 to <c>AlarmCount - 1</c> and are stable for a run.</summary>
		int AlarmCount { get; }

		/// <summary>Stable text identifier of an alarm, for example <c>steering.failure</c>. Skins and the control map use it.</summary>
		string AlarmId(int alarm);

		/// <summary>The group an alarm belongs to.</summary>
		AlarmGroup Group(int alarm);

		/// <summary>Where an alarm is in its sequence.</summary>
		AlarmPhase Phase(int alarm);

		/// <summary>
		/// True while the sounder of <paramref name="group"/> should be heard: from an alarm being raised or
		/// re-raised until it is acknowledged, silenced or cleared. The beep rhythm belongs to the sound
		/// clip, not to this flag.
		/// </summary>
		bool SounderOn(AlarmGroup group);

		/// <summary>True while a lamp test is held; every lamp shows lit.</summary>
		bool LampTest { get; }

		/// <summary>
		/// Sequence number of the oldest event still held. The log keeps a bounded number of events; a
		/// consumer whose cursor is older than this has missed some and should resynchronise from
		/// <see cref="Phase"/> and <see cref="SounderOn"/>.
		/// </summary>
		long FirstEventSequence { get; }

		/// <summary>Sequence number the next event will get. A consumer has read everything when its cursor equals this.</summary>
		long NextEventSequence { get; }

		/// <summary>The event with the given sequence number, which must lie in [<see cref="FirstEventSequence"/>, <see cref="NextEventSequence"/>).</summary>
		AlarmEvent EventAt(long sequence);
	}
}
