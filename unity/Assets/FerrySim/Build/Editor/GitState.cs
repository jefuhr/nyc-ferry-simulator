using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace FerrySim.Build
{
	/// <summary>
	/// The commit a build comes from, and whether the working tree differed from it: tracked or untracked changes, as
	/// <c>git status --porcelain</c> lists them. Unknown, and counted as dirty, when git cannot be run.
	/// </summary>
	internal readonly struct GitState
	{
		public GitState(string commit, bool dirty)
		{
			Commit = commit;
			Dirty = dirty;
		}

		public string Commit { get; }

		public bool Dirty { get; }

		public static GitState Read(string repoRoot)
		{
			var commit = Run(repoRoot, "rev-parse HEAD");
			var status = Run(repoRoot, "status --porcelain");
			return commit == null || status == null ? new GitState("unknown", true) : new GitState(commit.Trim(), status.Trim().Length > 0);
		}

		private static string Run(string repoRoot, string arguments)
		{
			try
			{
				var start = new ProcessStartInfo("git", arguments)
				{
					WorkingDirectory = repoRoot,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					CreateNoWindow = true,
				};
				using (var process = Process.Start(start))
				{
					var output = process.StandardOutput.ReadToEnd();
					return process.WaitForExit(10000) && process.ExitCode == 0 ? output : null;
				}
			}
			catch (Exception error) when (error is Win32Exception || error is InvalidOperationException || error is IOException)
			{
				return null;
			}
		}
	}
}
