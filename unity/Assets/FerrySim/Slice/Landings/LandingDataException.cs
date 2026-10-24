using System;
using System.Collections.Generic;

namespace FerrySim.Slice
{
	/// <summary>
	/// A landing data file (the world's <c>contacts.json</c> or the slice's contact surface parameters) is missing
	/// or malformed. The message names the file and every problem found, so a bad file fails loudly and once.
	/// </summary>
	public sealed class LandingDataException : Exception
	{
		public LandingDataException(string file, IReadOnlyList<string> problems, Exception inner = null)
			: base(Format(file, problems), inner)
		{
			File = file;
			Problems = problems ?? Array.Empty<string>();
		}

		/// <summary>The file (path or asset path) that failed.</summary>
		public string File { get; }

		/// <summary>Each problem, in file order.</summary>
		public IReadOnlyList<string> Problems { get; }

		private static string Format(string file, IReadOnlyList<string> problems) =>
			(string.IsNullOrEmpty(file) ? "(unnamed landing data)" : file) + ": " +
			(problems == null || problems.Count == 0 ? "unreadable" : string.Join("; ", problems));
	}
}
