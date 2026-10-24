using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FerrySim.Slice
{
	/// <summary>
	/// The command line of a smoke run (<see cref="PlayerSmokeRun"/>). <c>-ferrysim-smoke &lt;folder&gt;</c> starts one and names
	/// the folder for its report and screenshots; <c>-ferrysim-smoke-seconds &lt;s&gt;</c> sets the scripted drive in simulated
	/// seconds (<see cref="DefaultDriveSeconds"/> unless given, <see cref="MinimumDriveSeconds"/> to <see cref="MaximumDriveSeconds"/>).
	/// Switches are case-sensitive, as Unity's own are; a relative folder is taken from the working directory.
	/// </summary>
	public sealed class PlayerSmokeOptions
	{
		public const string FolderSwitch = "-ferrysim-smoke";
		public const string SecondsSwitch = "-ferrysim-smoke-seconds";
		public const double DefaultDriveSeconds = 10.0;
		public const double MinimumDriveSeconds = 1.0;
		public const double MaximumDriveSeconds = 120.0;

		public PlayerSmokeOptions(string outputFolder, double driveSeconds)
		{
			if (string.IsNullOrWhiteSpace(outputFolder))
			{
				throw new ArgumentException("A smoke run needs a folder for its report.", nameof(outputFolder));
			}

			if (!(driveSeconds >= MinimumDriveSeconds && driveSeconds <= MaximumDriveSeconds))
			{
				throw new ArgumentException(
					$"A smoke drive must be between {MinimumDriveSeconds:0} and {MaximumDriveSeconds:0} simulated seconds (got {driveSeconds.ToString(CultureInfo.InvariantCulture)}).",
					nameof(driveSeconds));
			}

			OutputFolder = Path.GetFullPath(outputFolder);
			DriveSeconds = driveSeconds;
		}

		/// <summary>Absolute: where smoke.json and the screenshots go.</summary>
		public string OutputFolder { get; }

		/// <summary>How long the scripted drive lasts, in simulated seconds.</summary>
		public double DriveSeconds { get; }

		/// <summary>
		/// The options in <paramref name="args"/>, the program first as <see cref="Environment.GetCommandLineArgs"/> gives
		/// them, or null when <see cref="FolderSwitch"/> is absent. Throws <see cref="ArgumentException"/>, naming the switch
		/// and the value, when a switch is there but unusable.
		/// </summary>
		public static PlayerSmokeOptions Parse(IReadOnlyList<string> args)
		{
			var folderAt = IndexOf(args, FolderSwitch);
			if (folderAt < 0)
			{
				return null;
			}

			var folder = ValueAfter(args, folderAt);
			if (string.IsNullOrWhiteSpace(folder) || folder.StartsWith("-", StringComparison.Ordinal))
			{
				throw new ArgumentException($"{FolderSwitch} needs a folder for the report (got {Quote(folder)}).");
			}

			var seconds = DefaultDriveSeconds;
			var secondsAt = IndexOf(args, SecondsSwitch);
			if (secondsAt >= 0)
			{
				var text = ValueAfter(args, secondsAt);
				if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds))
				{
					throw new ArgumentException($"{SecondsSwitch} needs a number of seconds (got {Quote(text)}).");
				}

				if (seconds < MinimumDriveSeconds || seconds > MaximumDriveSeconds)
				{
					throw new ArgumentException($"{SecondsSwitch} must be between {MinimumDriveSeconds:0} and {MaximumDriveSeconds:0} (got {Quote(text)}).");
				}
			}

			return new PlayerSmokeOptions(folder, seconds);
		}

		// Index 0 is the program.
		private static int IndexOf(IReadOnlyList<string> args, string name)
		{
			for (var i = 1; i < args.Count; i++)
			{
				if (string.Equals(args[i], name, StringComparison.Ordinal))
				{
					return i;
				}
			}

			return -1;
		}

		private static string ValueAfter(IReadOnlyList<string> args, int index) => index + 1 < args.Count ? args[index + 1] : null;

		private static string Quote(string value) => value == null ? "nothing" : $"'{value}'";
	}
}
