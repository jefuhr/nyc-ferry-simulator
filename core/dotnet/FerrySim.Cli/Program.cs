using System;

namespace FerrySim.Cli
{
	/// <summary>Headless runner for maneuvers and scenario replays. Implemented by the core-cli-harness task.</summary>
	public static class Program
	{
		public static int Main(string[] args)
		{
			Console.Error.WriteLine("ferrysim: no commands implemented yet (see the core-cli-harness task).");
			return 2;
		}
	}
}
