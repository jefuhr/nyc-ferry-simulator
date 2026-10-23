using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// The one entry point callers use to get a vessel simulation. It returns the twin-screw catamaran
	/// manoeuvring model, <see cref="VesselDynamicsSimulation"/>; the parameter file must carry the
	/// dynamics sections described in core/FerrySim.Core/Dynamics/README.md.
	/// </summary>
	public sealed class VesselDynamicsFactory : ISimulationFactory
	{
		public ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState) =>
			new VesselDynamicsSimulation(parameters, environment, initialState);
	}
}
