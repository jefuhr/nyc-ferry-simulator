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
			Create(parameters, environment, EmptyWorldGeometry.Instance, initialState);

		/// <summary>
		/// The hull touches the structures in <paramref name="geometry"/>. Any geometry other than
		/// <see cref="EmptyWorldGeometry"/> needs the parameter file's <c>contact</c> section.
		/// </summary>
		public ISimulation Create(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState) =>
			new VesselDynamicsSimulation(parameters, environment, geometry, initialState);
	}
}
