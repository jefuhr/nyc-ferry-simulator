namespace FerrySim.Sim
{
	/// <summary>
	/// Marks a control source whose <see cref="FerrySim.Core.Contracts.ControlInputs.Jog"/> can be
	/// non-zero: the live helm, or the replay of a recording made from it. The recorder writes the jog
	/// column only for such a source. Every other source (scripted runs, tests, journeys, benchmarks)
	/// keeps the original trace format byte for byte, and it is an error for one of them to produce a
	/// non-zero jog demand. The formats are described in docs/contracts/README.md.
	/// </summary>
	public interface INfuControlSource : IControlInputSource
	{
	}
}
