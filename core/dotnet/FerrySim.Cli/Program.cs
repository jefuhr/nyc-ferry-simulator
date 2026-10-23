using System;

namespace FerrySim.Cli
{
	/// <summary>Headless runner for maneuvers and control-trace replays against the sim core.</summary>
	public static class Program
	{
		public static int Main(string[] args) => new CliApp(new FerrySim.Core.Dynamics.VesselDynamicsFactory()).Run(args, Console.Out, Console.Error);
	}
}
