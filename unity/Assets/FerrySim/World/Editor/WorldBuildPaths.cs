using System;

namespace FerrySim.World.EditorTools
{
	/// <summary>Maps the shipped asset paths into an isolated builder output folder for reproducibility checks.</summary>
	internal static class WorldBuildPaths
	{
		public static string At(string worldFolder, string path)
		{
			if (string.IsNullOrEmpty(worldFolder) || !worldFolder.StartsWith("Assets/", StringComparison.Ordinal) || worldFolder.Contains(".."))
				throw new ArgumentException("World output must be a folder under Assets.", nameof(worldFolder));
			return worldFolder.TrimEnd('/') + path.Substring(GreyboxBuilder.WorldFolder.Length);
		}
	}
}
