using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// The one entry point callers use to get a vessel simulation. It currently returns
	/// <see cref="KinematicPlaceholderSimulation"/> so the CLI and Unity can be wired up before the
	/// dynamics model exists; the dynamics work replaces the body of <see cref="Create"/>.
	/// </summary>
	public sealed class VesselDynamicsFactory : ISimulationFactory
	{
		public ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState) =>
			new KinematicPlaceholderSimulation(parameters, environment, initialState);
	}
}
