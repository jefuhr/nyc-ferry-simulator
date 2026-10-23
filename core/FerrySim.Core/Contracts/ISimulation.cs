namespace FerrySim.Core.Contracts
{
	/// <summary>The fixed simulation clock shared by the core, the CLI and Unity.</summary>
	public static class SimulationClock
	{
		/// <summary>Physics steps per simulated second.</summary>
		public const int StepsPerSecond = 100;

		/// <summary>Length of one physics step, seconds.</summary>
		public const double FixedTimeStep = 1.0 / StepsPerSecond;

		/// <summary>
		/// Simulation time after <paramref name="stepCount"/> steps. Always derive time this way
		/// instead of adding <see cref="FixedTimeStep"/> repeatedly, which drifts.
		/// </summary>
		public static double TimeAtStep(long stepCount) => stepCount / (double)StepsPerSecond;
	}

	/// <summary>
	/// One vessel advancing on the fixed clock. Implementations must be deterministic: the same
	/// parameters, environment, initial state and input sequence give bit-identical states.
	/// </summary>
	public interface ISimulation
	{
		VesselParameters Parameters { get; }

		VesselState State { get; }

		VesselTelemetry Telemetry { get; }

		/// <summary>Advances exactly one <see cref="SimulationClock.FixedTimeStep"/>.</summary>
		void Step(in ControlInputs controls);

		/// <summary>
		/// Restarts the clock at zero from <paramref name="initialState"/>'s position, attitude and
		/// velocities (its <see cref="VesselState.Time"/> is ignored), with machinery at rest in neutral.
		/// </summary>
		void Reset(in VesselState initialState);
	}

	/// <summary>Builds simulations. Callers (CLI, Unity) depend on this, never on a concrete model.</summary>
	public interface ISimulationFactory
	{
		/// <summary>A simulation in open water, with no fixed structures to touch.</summary>
		ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState);

		/// <summary>A simulation whose hull can touch the structures in <paramref name="geometry"/>.</summary>
		ISimulation Create(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState);
	}
}
