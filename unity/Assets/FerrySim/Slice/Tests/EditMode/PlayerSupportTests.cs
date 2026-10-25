using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// A player reads the scenario's vessel file and environment pack from its StreamingAssets, where every player build
	/// copies them (FerrySim.Build.PlayerPayload). Failure modes guarded: a player that cannot find the file or the pack
	/// disabling the runner with only the runner's own exception in the log, a message that still calls players unsupported
	/// when the fault is a build that left the data out, and the check getting in the way in the editor, where the
	/// repository holds the data.
	/// </summary>
	public class PlayerSupportTests
	{
		[Test]
		public void APlayerWithoutTheVesselFile_FailsWithAClearMessage()
		{
			var problem = SliceBootstrap.PlayerProblem(false, "vessels/not-packaged.json");

			Assert.That(problem, Is.Not.Null);
			Assert.That(problem, Does.Not.Contain("editor only"), "players are supported; a missing file is a packaging fault");
			Assert.That(problem, Does.Contain("vessels/not-packaged.json"));
			Assert.That(problem, Does.Contain("StreamingAssets"));
			Assert.That(problem, Does.Contain("docs/releasing.md"));
		}

		[Test]
		public void ThePlayerCheck_PassesWhenTheVesselFileResolves()
		{
			// In the editor the repository's vessels/ folder is where SimPaths resolves it, as StreamingAssets would be in a player.
			Assert.That(SliceBootstrap.PlayerProblem(false, "vessels/t-class.json"), Is.Null);
		}

		[Test]
		public void TheEditor_IsNotChecked()
		{
			Assert.That(SliceBootstrap.PlayerProblem(true, "vessels/not-packaged.json"), Is.Null, "the runner reports a missing vessel file in the editor itself");
			Assert.That(SliceBootstrap.PlayerProblem(true, "vessels/t-class.json", "lower-east-river/2025-07-21"), Is.Null);
		}

		[Test]
		public void APlayerWithoutTheScenariosPack_FailsWithAClearMessage()
		{
			var problem = SliceBootstrap.PlayerProblem(false, "vessels/t-class.json", "lower-east-river/2025-07-21");

			Assert.That(problem, Is.Not.Null);
			Assert.That(problem, Does.Contain("lower-east-river/2025-07-21"));
			Assert.That(problem, Does.Contain("StreamingAssets"));
			Assert.That(problem, Does.Contain("docs/releasing.md"));
			Assert.That(SliceBootstrap.PlayerProblem(false, "vessels/t-class.json", "not a pack name"), Does.Contain("is not region/date"));
		}

		[Test]
		public void ThePlayerCheck_PassesWhenThePackResolves_OrTheScenarioHasNone()
		{
			Assert.That(SliceBootstrap.PlayerProblem(false, "vessels/t-class.json", string.Empty), Is.Null, "calm or uniform water needs no pack");
			if (!System.IO.Directory.Exists(System.IO.Path.Combine(SliceTestData.RepoRoot, "data/processed/lower-east-river/env/days/2025-07-20")))
			{
				Assert.Ignore("The environment packs are not built.");
			}

			// In the editor the repository's packs are where the pack library finds them, as StreamingAssets' would be in a player.
			Assert.That(SliceBootstrap.PlayerProblem(false, "vessels/t-class.json", "lower-east-river/2025-07-20"), Is.Null);
		}
	}
}
