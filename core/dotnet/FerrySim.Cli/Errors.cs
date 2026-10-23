using System;

namespace FerrySim.Cli
{
	/// <summary>Bad command line: reported with the usage text and exit code 2.</summary>
	public class UsageException : Exception
	{
		public UsageException(string message)
			: base(message)
		{
		}
	}

	/// <summary>A valid command that could not be carried out (missing file, unreachable speed): exit code 1.</summary>
	public sealed class CliException : Exception
	{
		public CliException(string message)
			: base(message)
		{
		}
	}
}
