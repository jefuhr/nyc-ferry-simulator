using FerrySim.Sim;

namespace FerrySim.World
{
	/// <summary>The visibility of a runner's current run: its scenario's, or for a replay its recording's (<c>visibility_m</c>).</summary>
	public static class RunVisibility
	{
		/// <summary>Metres; 0 (unlimited) without a run, or for a run or recording that set none.</summary>
		public static double Of(VesselSimulationRunner runner)
		{
			if (runner == null || !runner.IsInitialized)
			{
				return MeteorologicalVisibility.Unlimited;
			}

			if (runner.IsReplaying)
			{
				return runner.Replay != null ? runner.Replay.VisibilityMetres : MeteorologicalVisibility.Unlimited;
			}

			return runner.Scenario != null ? runner.Scenario.visibilityMetres : MeteorologicalVisibility.Unlimited;
		}
	}
}
