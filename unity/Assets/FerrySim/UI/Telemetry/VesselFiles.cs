using System;
using System.IO;
using System.Text.RegularExpressions;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;

namespace FerrySim.UI
{
	/// <summary>
	/// Loads <c>vessels/&lt;id&gt;.json</c> for development tools that only have a CSV (the fake source and
	/// the debrief). In the running app, parameters come from <c>IVesselTelemetrySource.Parameters</c>.
	/// </summary>
	public static class VesselFiles
	{
		// Vessel ids are file names such as "t-class"; anything else could walk out of the vessels folder.
		private static readonly Regex SafeId = new Regex("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant);

		/// <summary>The parameters, or null with <paramref name="problem"/> set when the id is unsafe or the file is missing or invalid.</summary>
		public static VesselParameters TryLoad(string vesselId, out string problem)
		{
			if (string.IsNullOrEmpty(vesselId) || !SafeId.IsMatch(vesselId))
			{
				problem = $"'{vesselId}' is not a vessel id (expected something like t-class).";
				return null;
			}

			var path = Path.Combine(UIRepoPaths.Root, "vessels", vesselId + ".json");
			if (!File.Exists(path))
			{
				problem = $"No parameter file for vessel '{vesselId}' at {path}.";
				return null;
			}

			try
			{
				problem = null;
				return VesselParametersJson.Load(path);
			}
			catch (Exception error) when (error is VesselParametersException || error is IOException || error is UnauthorizedAccessException)
			{
				problem = $"Vessel '{vesselId}' did not load: {error.Message}";
				return null;
			}
		}
	}
}
