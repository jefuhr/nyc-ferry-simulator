using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// A player reads the scenario's vessel file from its StreamingAssets, where every player build copies it
	/// (FerrySim.Build.PlayerPayload). Failure modes guarded: a player that cannot find the file disabling the runner
	/// with only the runner's file-not-found exception in the log, a message that still calls players unsupported when
	/// the fault is a build that left the file out, and the check getting in the way in the editor, where the
	/// repository holds the file.
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
		}
	}
}
