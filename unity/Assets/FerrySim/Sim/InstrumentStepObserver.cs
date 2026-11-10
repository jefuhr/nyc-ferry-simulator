using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;

namespace FerrySim.Sim
{
	/// <summary>Observes completed physics steps without changing the simulation or allocating per step.</summary>
	public sealed class InstrumentStepObserver : IStepObserver
	{
		private readonly IInstrumentTaps _taps;
		private ControlInputs _controls;

		public InstrumentStepObserver(InstrumentModel model, IInstrumentTaps taps)
		{
			Model = model ?? throw new ArgumentNullException(nameof(model));
			_taps = taps ?? throw new ArgumentNullException(nameof(taps));
		}

		public InstrumentModel Model { get; }

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) =>
			_controls = controls;

		public void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry) =>
			Model.Step(state, telemetry, _controls, _taps);
	}

	/// <summary>A non-null, unavailable readout before initialization or when instrument data is absent.</summary>
	internal sealed class UnavailableInstrumentReadout : IInstrumentReadout
	{
		public static readonly UnavailableInstrumentReadout Instance = new UnavailableInstrumentReadout();
		private UnavailableInstrumentReadout() { }
		public double Value(Channel channel) => double.NaN;
		public int AlarmCount => 0;
		public string AlarmId(int alarm) => throw new ArgumentOutOfRangeException(nameof(alarm));
		public AlarmGroup Group(int alarm) => throw new ArgumentOutOfRangeException(nameof(alarm));
		public AlarmPhase Phase(int alarm) => throw new ArgumentOutOfRangeException(nameof(alarm));
		public bool SounderOn(AlarmGroup group) => false;
		public bool LampTest => false;
		public long FirstEventSequence => 0;
		public long NextEventSequence => 0;
		public AlarmEvent EventAt(long sequence) => throw new ArgumentOutOfRangeException(nameof(sequence));
	}
}
