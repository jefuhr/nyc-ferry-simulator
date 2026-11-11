using System;
using System.Collections.Generic;

namespace FerrySim.Controls
{
	/// <summary>The shared collision rule for defaults, saved overrides and the options menu.</summary>
	public static class BindingConflicts
	{
		public static readonly IReadOnlyList<string> ReservedKeys = Array.AsReadOnly(new[]
		{
			"<Keyboard>/escape", "<Keyboard>/enter", "<Keyboard>/numpadEnter", "<Keyboard>/space",
			"<Keyboard>/upArrow", "<Keyboard>/downArrow", "<Keyboard>/pageUp", "<Keyboard>/pageDown", "<Keyboard>/home", "<Keyboard>/end",
		});

		public static bool CanBeLiveTogether(string firstActionId, string secondActionId)
		{
			var first = Map(firstActionId);
			var second = Map(secondActionId);
			if (first == "Audio" || first == "Interface" || second == "Audio" || second == "Interface") return true;
			if (first == "Camera" || second == "Camera") return true;
			return (first == "FreeFly") == (second == "FreeFly");
		}

		public static bool Conflicts(string firstActionId, string firstPath, string secondActionId, string secondPath) =>
			CanBeLiveTogether(firstActionId, secondActionId) && PathsOverlap(firstPath, secondPath);

		public static bool IsReserved(string path)
		{
			foreach (var reserved in ReservedKeys) if (PathsOverlap(path, reserved)) return true;
			return false;
		}

		public static bool PathsOverlap(string first, string second) => new BindingControlPaths().Overlap(first, second);
		private static string Map(string actionId)
		{
			if (string.IsNullOrEmpty(actionId) || actionId.IndexOf('/') <= 0) throw new ArgumentException("Expected Map/Action.", nameof(actionId));
			return actionId.Substring(0, actionId.IndexOf('/'));
		}
	}
}
