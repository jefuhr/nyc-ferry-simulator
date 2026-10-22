using System;
using System.IO;
using NUnit.Framework;

namespace FerrySim.Core.Tests
{
	/// <summary>Locates files in the repository from the test output directory.</summary>
	public static class RepoPaths
	{
		public static string Root
		{
			get
			{
				var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
				while (directory != null)
				{
					if (Directory.Exists(Path.Combine(directory.FullName, "vessels")) &&
						Directory.Exists(Path.Combine(directory.FullName, "core")))
					{
						return directory.FullName;
					}

					directory = directory.Parent;
				}

				throw new InvalidOperationException("Could not find the repository root above " + TestContext.CurrentContext.TestDirectory);
			}
		}

		public static string Vessel(string fileName) => Path.Combine(Root, "vessels", fileName);
	}
}
