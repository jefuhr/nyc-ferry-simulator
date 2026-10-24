using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace FerrySim.Build.Tests
{
	/// <summary>
	/// The project settings a release depends on. Failure modes guarded: a version the release script cannot tag
	/// (not major.minor.patch), the bundle identifier Unity would derive from the product name, a build that opens on
	/// another scene than the slice or carries a development scene, and a product name changed by accident, which
	/// would rename the installed app and move its saved input profiles.
	/// </summary>
	public class ReleaseSettingsTests
	{
		[Test]
		public void TheVersion_IsMajorMinorPatch()
		{
			Assert.That(PlayerSettings.bundleVersion, Does.Match(@"^\d+\.\d+\.\d+$"));
		}

		[Test]
		public void TheBundleIdentifier_IsTheProjects()
		{
			Assert.That(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone), Is.EqualTo("com.nycferrysim.simulator"));
		}

		[Test]
		public void TheBuild_OpensOnTheSlice_AndCarriesNothingElse()
		{
			var enabled = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();

			Assert.That(enabled, Is.EqualTo(new[] { "Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity" }));
		}

		[Test]
		public void TheProductName_IsUnchanged()
		{
			Assert.That(PlayerSettings.productName, Is.EqualTo("NYC Ferry Simulator"));
		}
	}
}
