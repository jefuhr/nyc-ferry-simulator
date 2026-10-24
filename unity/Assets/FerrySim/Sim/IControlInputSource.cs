using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// Supplies the operator inputs for each physics step. Live devices (the Input assembly), recorded
	/// traces and test scripts all implement it, so the bridge never depends on where controls come from.
	/// </summary>
	public interface IControlInputSource
	{
		/// <summary>
		/// The controls to apply during the physics step that starts at <paramref name="step"/>, counted from
		/// zero at the start of the run. Called exactly once per step, in step order.
		/// </summary>
		ControlInputs ControlsForStep(long step);
	}

	/// <summary>The same controls for every step. Handy for tests and for a vessel left drifting.</summary>
	public sealed class ConstantControls : IControlInputSource
	{
		public ConstantControls(ControlInputs controls)
		{
			Controls = controls;
		}

		public ControlInputs Controls { get; set; }

		public ControlInputs ControlsForStep(long step) => Controls;
	}
}
