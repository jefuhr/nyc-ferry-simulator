using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// What instruments, cameras and debrief tools read from a running vessel. The simulation bridge
	/// implements it; UI development scenes can use a fake. All values are SI and follow
	/// docs/conventions.md; convert to knots, rpm and degrees only for display.
	/// </summary>
	public interface IVesselTelemetrySource
	{
		/// <summary>Parameters of the vessel being simulated (display name, particulars).</summary>
		VesselParameters Parameters { get; }

		/// <summary>Physics state at the end of the most recent fixed step.</summary>
		VesselState State { get; }

		/// <summary>Telemetry from the most recent fixed step.</summary>
		VesselTelemetry Telemetry { get; }

		/// <summary>Operator inputs applied in the most recent fixed step, after device mapping.</summary>
		ControlInputs Controls { get; }

		/// <summary>Scenario start in UTC. Clock time is this plus <see cref="VesselState.Time"/> seconds.</summary>
		DateTimeOffset ScenarioStartUtc { get; }

		/// <summary>
		/// True only while <see cref="State"/>, <see cref="Telemetry"/> and <see cref="Controls"/> come from at
		/// least one completed physics step of the current run. False before the first step, after the run
		/// ends, and whenever the source has no current data. Instruments show "no data" instead of values
		/// when this is false.
		/// </summary>
		bool HasTelemetry { get; }

		/// <summary>
		/// Identifies the current run. It changes on every new run, reset or replay start and is never reused
		/// within a process. Consumers clear per-run state (alarm acknowledgements, latches, plots) when it
		/// changes.
		/// </summary>
		long RunId { get; }

		/// <summary>
		/// Contact seen since the current run started, latched every physics step so that a contact lasting a
		/// single 10 ms step is never lost between rendered frames.
		/// </summary>
		ContactLatch Contact { get; }
	}

	/// <summary>
	/// Cumulative contact record for the current run. Consumers detect new contact by comparing
	/// <see cref="StepsWithContact"/> with the value they saw on their previous update.
	/// </summary>
	public readonly struct ContactLatch
	{
		public ContactLatch(long stepsWithContact, long lastContactStep, double lastContactForce, double peakContactForce, long peakContactStep)
		{
			StepsWithContact = stepsWithContact;
			LastContactStep = lastContactStep;
			LastContactForce = lastContactForce;
			PeakContactForce = peakContactForce;
			PeakContactStep = peakContactStep;
		}

		/// <summary>No contact yet in this run.</summary>
		public static ContactLatch None => new ContactLatch(0, -1, 0.0, 0.0, -1);

		/// <summary>Number of physics steps in this run with a non-zero contact force.</summary>
		public long StepsWithContact { get; }

		/// <summary>Step index since the run started of the most recent step with contact, or -1 if none.</summary>
		public long LastContactStep { get; }

		/// <summary>Contact force magnitude in that most recent contact step, newtons.</summary>
		public double LastContactForce { get; }

		/// <summary>Largest contact force magnitude in this run, newtons.</summary>
		public double PeakContactForce { get; }

		/// <summary>Step index since the run started of <see cref="PeakContactForce"/>, or -1 if none.</summary>
		public long PeakContactStep { get; }
	}
}
