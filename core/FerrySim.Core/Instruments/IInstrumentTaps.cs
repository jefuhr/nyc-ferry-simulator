using FerrySim.Core.Dynamics;

namespace FerrySim.Core.Instruments
{
	/// <summary>Read-only control and machinery observations; never used to advance the simulation.</summary>
	public interface IInstrumentTaps
	{
		double RudderOrder { get; }
		bool NfuActive { get; }
		double HardOverAngle { get; }
		double SteeringRate { get; }
		ShiftPhase PortShiftPhase { get; }
		ShiftPhase StarboardShiftPhase { get; }
		double PortEngineLoad { get; }
		double StarboardEngineLoad { get; }
	}
}
