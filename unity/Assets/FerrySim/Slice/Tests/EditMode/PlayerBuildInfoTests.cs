using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The build's record of a player (<see cref="PlayerBuildInfo"/>, StreamingAssets/build-info.json). Failure modes
	/// guarded: a missing, malformed or version-less file read as a valid record, fields lost between the build that
	/// writes the record and the player that reads it, and a description that hides a build from uncommitted changes.
	/// </summary>
	public class PlayerBuildInfoTests
	{
		private string _folder;

		[SetUp]
		public void SetUp()
		{
			_folder = Path.Combine(Path.GetTempPath(), "ferrysim-build-info-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_folder);
		}

		[TearDown]
		public void TearDown() => Directory.Delete(_folder, true);

		[Test]
		public void TheRecord_RoundTrips()
		{
			var written = new PlayerBuildInfo
			{
				product = "NYC Ferry Simulator",
				version = "0.1.0",
				commit = "04db90b99e5ecd3f63654ae432becbe88e80a184",
				dirty = false,
				target = "StandaloneOSX",
				built_utc = "2026-10-24T18:00:00Z",
				unity = "6000.3.27f1",
				payload = new[] { new PlayerBuildInfo.PackagedFile { path = "vessels/t-class.json", sha256 = "ab12", bytes = 52133 } },
			};
			File.WriteAllText(Path.Combine(_folder, PlayerBuildInfo.FileName), JsonUtility.ToJson(written, true));

			Assert.That(PlayerBuildInfo.TryLoad(_folder, out var read, out var problem), Is.True, problem);
			Assert.That(problem, Is.Null);
			Assert.That(JsonUtility.ToJson(read), Is.EqualTo(JsonUtility.ToJson(written)));
		}

		[Test]
		public void AMissingRecord_IsAProblem()
		{
			Assert.That(PlayerBuildInfo.TryLoad(_folder, out var info, out var problem), Is.False);
			Assert.That(info, Is.Null);
			Assert.That(problem, Does.Contain(PlayerBuildInfo.FileName));
		}

		[TestCase("{not json")]
		[TestCase("")]
		public void AMalformedRecord_IsAProblem(string text)
		{
			File.WriteAllText(Path.Combine(_folder, PlayerBuildInfo.FileName), text);

			Assert.That(PlayerBuildInfo.TryLoad(_folder, out var info, out var problem), Is.False);
			Assert.That(info, Is.Null);
			Assert.That(problem, Does.Contain(PlayerBuildInfo.FileName));
		}

		[Test]
		public void ARecordWithoutAVersion_IsAProblem()
		{
			File.WriteAllText(Path.Combine(_folder, PlayerBuildInfo.FileName), "{\"product\": \"NYC Ferry Simulator\"}");

			Assert.That(PlayerBuildInfo.TryLoad(_folder, out var info, out var problem), Is.False);
			Assert.That(info, Is.Null);
			Assert.That(problem, Does.Contain("version"));
		}

		[Test]
		public void TheDescription_ShowsTheShortCommit_AndMarksDirtyBuilds()
		{
			Assert.That(new PlayerBuildInfo { version = "0.1.0", commit = "04db90b99e5ecd3f", dirty = false }.Describe(), Is.EqualTo("0.1.0 (04db90b)"));
			Assert.That(new PlayerBuildInfo { version = "0.1.0", commit = "04db90b99e5ecd3f", dirty = true }.Describe(), Is.EqualTo("0.1.0 (04db90b-dirty)"));
			Assert.That(new PlayerBuildInfo { version = "0.1.0", commit = null, dirty = true }.Describe(), Is.EqualTo("0.1.0 (unknown-dirty)"));
		}
	}
}
