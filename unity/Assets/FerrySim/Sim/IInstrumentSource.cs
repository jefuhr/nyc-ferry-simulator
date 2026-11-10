using FerrySim.Core.Instruments;

namespace FerrySim.Sim
{
	/// <summary>
	/// The instrument model of a running vessel, and the three things a viewer can do to its alarms.
	/// The simulation bridge implements it next to <see cref="IVesselTelemetrySource"/>; consumers find it
	/// by casting their telemetry source and must cope with it being absent (fakes, replays of old
	/// builds). Acknowledge, silence and lamp test are viewer actions: they are never recorded, never
	/// reach the physics, and reset when <see cref="IVesselTelemetrySource.RunId"/> changes.
	/// </summary>
	public interface IInstrumentSource
	{
		/// <summary>
		/// True only while <see cref="Instruments"/> reflects at least one completed physics step of the
		/// current run. Instruments show "no data" when it is false.
		/// </summary>
		bool HasInstruments { get; }

		/// <summary>The readout as of the most recent fixed step. Never null; meaningful only while <see cref="HasInstruments"/> is true.</summary>
		IInstrumentReadout Instruments { get; }

		/// <summary>Acknowledges every unacknowledged alarm: lamps go steady and sounders stop.</summary>
		void AcknowledgeAlarms();

		/// <summary>Stops the sounders without acknowledging; lamps keep flashing.</summary>
		void SilenceAlarms();

		/// <summary>Holds (true) or releases (false) the lamp test.</summary>
		void SetLampTest(bool on);
	}
}
