using System;
using System.IO;
using System.Reflection;
using FerrySim.Sim;
using NUnit.Framework;

namespace FerrySim.Build.Tests
{
	/// <summary>The real installed harbor failed because the resolver recognized packaged files but not folders.</summary>
	public class InstalledPathTests
	{
		private string _temporary;
		private string _streaming;
		private string _repository;
		[SetUp] public void SetUp()
		{
			_temporary = Path.Combine(Path.GetTempPath(), "ferrysim-installed-path-" + Guid.NewGuid().ToString("N"));
			_streaming = Path.Combine(_temporary, "installed");
			_repository = Path.Combine(_temporary, "unavailable-repository");
			Directory.CreateDirectory(_streaming);
		}
		[TearDown] public void TearDown() => Directory.Delete(_temporary, true);

		[Test] public void FolderPresentOnlyInStreamingAssetsResolvesThere()
		{
			var runtime = Path.Combine(_streaming, "data/processed/harbor/runtime");
			Directory.CreateDirectory(runtime);
			File.WriteAllText(Path.Combine(runtime, "manifest.json"), "{}");
			Assert.That(Resolve("data/processed/harbor/runtime"), Is.EqualTo(runtime));
		}
		[Test] public void FilePresentOnlyInStreamingAssetsStillResolvesThere()
		{
			var file = Path.Combine(_streaming, "vessel.json");
			File.WriteAllText(file, "{}");
			Assert.That(Resolve("vessel.json"), Is.EqualTo(file));
		}
		[Test] public void MissingPathKeepsRepositoryLocationForItsErrorMessage() =>
			Assert.That(Resolve("missing/runtime"), Is.EqualTo(Path.Combine(_repository, "missing/runtime")));
		[TestCase(null), TestCase("")] public void EmptyPathIsUnchanged(string path) => Assert.That(Resolve(path), Is.EqualTo(path));
		[Test] public void AbsolutePathIsUnchanged() => Assert.That(Resolve(_repository), Is.EqualTo(_repository));

		private string Resolve(string path)
		{
			var method = typeof(SimPaths).GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string), typeof(string), typeof(string) }, null);
			return (string)method.Invoke(null, new object[] { path, _streaming, _repository });
		}
	}
}
