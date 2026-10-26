using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FerrySim.World.Tiles3D
{
	/// <summary>
	/// The probe player's command line: <c>-probe-out &lt;folder&gt;</c> names the folder for probe.json and its screenshots.
	/// Without it they go beside the executable: the folder holding the .exe on Windows, the folder holding the .app on
	/// macOS. The switch is case-sensitive, as Unity's own are; a relative folder is taken from the working directory.
	/// </summary>
	public sealed class ProbeOptions
	{
		public const string OutSwitch = "-probe-out";

		public ProbeOptions(string outputFolder)
		{
			if (string.IsNullOrWhiteSpace(outputFolder))
			{
				throw new ArgumentException("The probe needs a folder for its report.", nameof(outputFolder));
			}

			OutputFolder = Path.GetFullPath(outputFolder);
		}

		/// <summary>Absolute: where probe.json and the screenshots go.</summary>
		public string OutputFolder { get; }

		/// <summary>
		/// The options in <paramref name="args"/> (the program first, as <see cref="Environment.GetCommandLineArgs"/> gives
		/// them), with <paramref name="defaultFolder"/> when the switch is absent. Throws <see cref="ArgumentException"/>,
		/// naming the switch, when it is there without a folder.
		/// </summary>
		public static ProbeOptions Parse(IReadOnlyList<string> args, string defaultFolder)
		{
			for (var i = 1; i < args.Count; i++)
			{
				if (!string.Equals(args[i], OutSwitch, StringComparison.Ordinal))
				{
					continue;
				}

				var folder = i + 1 < args.Count ? args[i + 1] : null;
				if (string.IsNullOrWhiteSpace(folder) || folder.StartsWith("-", StringComparison.Ordinal))
				{
					throw new ArgumentException($"{OutSwitch} needs a folder for probe.json (got {(folder == null ? "nothing" : $"'{folder}'")}).");
				}

				return new ProbeOptions(folder);
			}

			return new ProbeOptions(defaultFolder);
		}

		/// <summary>
		/// The folder beside the executable, from <see cref="Application.dataPath"/>: on Windows and Linux the data folder
		/// (<c>&lt;name&gt;_Data</c>) sits beside the executable; on macOS it is the .app's Contents folder. In the editor,
		/// <c>artifacts/tiles3d-probe/editor</c> under the repository.
		/// </summary>
		public static string DefaultFolder(string dataPath, RuntimePlatform platform)
		{
			var data = dataPath.TrimEnd('/', '\\');
			switch (platform)
			{
				case RuntimePlatform.OSXPlayer:
					return Path.GetDirectoryName(Path.GetDirectoryName(data));
				case RuntimePlatform.WindowsPlayer:
				case RuntimePlatform.LinuxPlayer:
					return Path.GetDirectoryName(data);
				default:
					return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(data)) ?? data, "artifacts", "tiles3d-probe", "editor");
			}
		}
	}
}
