using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Build.Tests
{
	/// <summary>
	/// The command line the release script builds a player with (<see cref="PlayerBuild"/>). Failure modes guarded: a
	/// build for an unknown platform, a player written relative to Unity's working directory instead of where the
	/// script looks, a version on the command line that disagrees with the project's, a build with no scenes, a player
	/// laid out other than the script packages it (the .app at the top of the Mac folder, the .exe in a folder of its
	/// own on Windows), and a build that throws leaving Unity's exit code to chance.
	/// </summary>
	public class PlayerBuildTests
	{
		private const string Product = "NYC Ferry Simulator";
		private const string Version = "0.1.0";
		private static readonly string Output = Path.Combine(Path.GetTempPath(), "ferrysim-release", "player");
		private static readonly string[] Scenes = { "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity" };

		private static readonly object[] Unusable =
		{
			new object[] { new[] { "Unity", "-ferrysimOutput", Output }, "-ferrysimTarget" },
			new object[] { new[] { "Unity", "-ferrysimTarget", "linux", "-ferrysimOutput", Output }, "mac or windows" },
			new object[] { new[] { "Unity", "-ferrysimTarget", "mac" }, "-ferrysimOutput" },
			new object[] { new[] { "Unity", "-ferrysimTarget", "mac", "-ferrysimOutput", "relative/player" }, "absolute" },
			new object[] { new[] { "Unity", "-ferrysimTarget", "mac", "-ferrysimOutput", Output, "-ferrysimVersion", "0.2.0" }, "0.1.0" },
		};

		[Test]
		public void AMacBuild_IsAnAppAtTheTopOfTheFolder()
		{
			var request = PlayerBuild.Parse(new[] { "Unity", "-batchmode", "-ferrysimTarget", "mac", "-ferrysimOutput", Output }, Version, Product, Scenes);

			Assert.That(request.Target, Is.EqualTo(BuildTarget.StandaloneOSX));
			Assert.That(request.OutputFolder, Is.EqualTo(Output));
			Assert.That(request.PlayerPath, Is.EqualTo(Path.Combine(Output, "NYC Ferry Simulator.app")));
			Assert.That(request.Scenes, Is.EqualTo(Scenes));
		}

		[Test]
		public void AWindowsBuild_IsAnExeInAFolderOfItsOwn()
		{
			var request = PlayerBuild.Parse(new[] { "Unity", "-ferrysimTarget", "windows", "-ferrysimOutput", Output, "-ferrysimVersion", Version }, Version, Product, Scenes);

			Assert.That(request.Target, Is.EqualTo(BuildTarget.StandaloneWindows64));
			Assert.That(request.PlayerPath, Is.EqualTo(Path.Combine(Output, "NYC Ferry Simulator", "NYC Ferry Simulator.exe")));
		}

		[TestCaseSource(nameof(Unusable))]
		public void UnusableCommandLines_AreRejected_WithTheReason(string[] args, string reason)
		{
			var error = Assert.Throws<ArgumentException>(() => PlayerBuild.Parse(args, Version, Product, Scenes));

			Assert.That(error.Message, Does.Contain(reason));
		}

		[Test]
		public void ABuildWithoutScenes_IsRejected()
		{
			var error = Assert.Throws<ArgumentException>(() => PlayerBuild.Parse(new[] { "Unity", "-ferrysimTarget", "mac", "-ferrysimOutput", Output }, Version, Product, new string[0]));

			Assert.That(error.Message, Does.Contain("scene"));
		}

		[Test]
		public void TheExitCode_Is0ForASuccess_1ForAFailureOrAThrow_And2ForAnUnusableCommandLine()
		{
			var args = new[] { "Unity", "-ferrysimTarget", "mac", "-ferrysimOutput", Output };

			Assert.That(PlayerBuild.Run(args, Version, Product, Scenes, request => BuildResult.Succeeded), Is.EqualTo(0));
			LogAssert.Expect(LogType.Error, new Regex("build ended Failed"));
			Assert.That(PlayerBuild.Run(args, Version, Product, Scenes, request => BuildResult.Failed), Is.EqualTo(1));
			LogAssert.Expect(LogType.Error, new Regex("threw InvalidOperationException: no disk"));
			Assert.That(PlayerBuild.Run(args, Version, Product, Scenes, request => throw new InvalidOperationException("no disk")), Is.EqualTo(1));
			LogAssert.Expect(LogType.Error, new Regex("No player built"));
			Assert.That(PlayerBuild.Run(new[] { "Unity" }, Version, Product, Scenes, request => BuildResult.Succeeded), Is.EqualTo(2));
		}
	}
}
