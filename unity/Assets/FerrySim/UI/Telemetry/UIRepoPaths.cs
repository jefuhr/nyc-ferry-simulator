using System.IO;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>Repository-relative paths for development tools (the fake source, the debrief and the dev scene).</summary>
	public static class UIRepoPaths
	{
		/// <summary>The committed sample: a CLI crash-stop run of the T-class.</summary>
		public const string SampleCrashStopCsv = "unity/Assets/FerrySim/UI/Samples/t-crash-stop.csv";

		/// <summary>Where scripts/check.sh writes CLI maneuver CSVs.</summary>
		public const string ManeuverArtifacts = "artifacts/maneuvers";

		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		public static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		/// <summary>An absolute path, or a path relative to the repository root.</summary>
		public static string Resolve(string path) =>
			Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Root, path));
	}
}
