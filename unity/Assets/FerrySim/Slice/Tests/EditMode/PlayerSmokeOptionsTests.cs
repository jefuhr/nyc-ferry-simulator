using System;
using System.IO;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The switches the release script passes to a built player (<see cref="PlayerSmokeOptions"/>). Failure modes
	/// guarded: a smoke run started without its switch, or by one that only looks like it; a missing or flag-like value
	/// taken as the folder; drive seconds that are not a finite number, are out of range or use a decimal comma; and a
	/// relative folder resolved anywhere but the working directory.
	/// </summary>
	public class PlayerSmokeOptionsTests
	{
		private static readonly string Folder = Path.Combine(Path.GetTempPath(), "ferrysim-smoke");

		private static readonly object[] Unusable =
		{
			new object[] { new[] { "player", "-ferrysim-smoke" }, "folder" },
			new object[] { new[] { "player", "-ferrysim-smoke", "-batchmode" }, "folder" },
			new object[] { new[] { "player", "-ferrysim-smoke", " " }, "folder" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds" }, "number of seconds" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds", "ten" }, "number of seconds" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds", "2,5" }, "number of seconds" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds", "NaN" }, "number of seconds" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds", "Infinity" }, "number of seconds" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds", "0.5" }, "between 1 and 120" },
			new object[] { new[] { "player", "-ferrysim-smoke", Folder, "-ferrysim-smoke-seconds", "121" }, "between 1 and 120" },
		};

		[Test]
		public void WithoutTheSwitch_NoSmokeRunStarts()
		{
			Assert.That(PlayerSmokeOptions.Parse(new[] { "player" }), Is.Null);
			Assert.That(PlayerSmokeOptions.Parse(new[] { "player", "-ferrysim-smoke-seconds", "5" }), Is.Null, "seconds alone start nothing");
			Assert.That(PlayerSmokeOptions.Parse(new[] { "player", "-FERRYSIM-SMOKE", Folder }), Is.Null, "switches are case-sensitive");
			Assert.That(PlayerSmokeOptions.Parse(new[] { "-ferrysim-smoke", Folder }), Is.Null, "the program name is never a switch");
		}

		[Test]
		public void TheFolderFollowsTheSwitch_AndTheDriveDefaultsToTenSeconds()
		{
			var options = PlayerSmokeOptions.Parse(new[] { "player", "-screen-fullscreen", "0", "-ferrysim-smoke", Folder });

			Assert.That(options, Is.Not.Null);
			Assert.That(options.OutputFolder, Is.EqualTo(Path.GetFullPath(Folder)));
			Assert.That(options.DriveSeconds, Is.EqualTo(10.0));
		}

		[Test]
		public void ARelativeFolder_IsResolvedFromTheWorkingDirectory()
		{
			var options = PlayerSmokeOptions.Parse(new[] { "player", "-ferrysim-smoke", "smoke-out" });

			Assert.That(options.OutputFolder, Is.EqualTo(Path.GetFullPath("smoke-out")));
		}

		[Test]
		public void TheDrive_IsReadInTheInvariantCulture_WhereverTheSwitchIs()
		{
			var options = PlayerSmokeOptions.Parse(new[] { "player", "-ferrysim-smoke-seconds", "2.5", "-ferrysim-smoke", Folder });

			Assert.That(options.DriveSeconds, Is.EqualTo(2.5));
		}

		[TestCaseSource(nameof(Unusable))]
		public void UnusableSwitches_AreRejected_WithTheReason(string[] args, string reason)
		{
			var error = Assert.Throws<ArgumentException>(() => PlayerSmokeOptions.Parse(args));

			Assert.That(error.Message, Does.Contain(reason));
		}
	}
}
