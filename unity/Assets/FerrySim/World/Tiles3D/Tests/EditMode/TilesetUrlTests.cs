using System;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.World.Tiles3D.Tests
{
	/// <summary>
	/// The file:// URL the probe hands Cesium (<see cref="TilesetUrl"/>). Failure modes guarded: a Windows drive letter
	/// or backslashes reaching the URL as written; spaces, '#' or non-ASCII names left unescaped; a relative path turned
	/// into a URL; and disagreement with System.Uri, which the spike's macOS run used.
	/// </summary>
	public sealed class TilesetUrlTests
	{
		[Test]
		public void AMacPathWithSpacesMatchesSystemUri()
		{
			const string path = "/Users/julie/orca/workspaces/NYC Ferry Simulator/Tiles3DProbe.app/Contents/Resources/Data/StreamingAssets/harbor3d/tileset.json";

			Assert.That(TilesetUrl.FromAbsolutePath(path), Is.EqualTo(
				"file:///Users/julie/orca/workspaces/NYC%20Ferry%20Simulator/Tiles3DProbe.app/Contents/Resources/Data/StreamingAssets/harbor3d/tileset.json"));
			Assert.That(TilesetUrl.FromAbsolutePath(path), Is.EqualTo(new Uri(path).AbsoluteUri));
		}

		[Test]
		public void AWindowsPathKeepsItsDriveAndUsesForwardSlashes()
		{
			Assert.That(TilesetUrl.FromAbsolutePath(@"C:\Users\Ferry Crew\Downloads\FerrySim Tiles3D Probe\Tiles3DProbe_Data\StreamingAssets\harbor3d\tileset.json"),
				Is.EqualTo("file:///C:/Users/Ferry%20Crew/Downloads/FerrySim%20Tiles3D%20Probe/Tiles3DProbe_Data/StreamingAssets/harbor3d/tileset.json"));
			// Unity reports Windows paths with forward slashes; both spellings give one URL.
			Assert.That(TilesetUrl.FromAbsolutePath("D:/Games/Probe_Data/StreamingAssets/harbor3d/tileset.json"),
				Is.EqualTo("file:///D:/Games/Probe_Data/StreamingAssets/harbor3d/tileset.json"));
		}

		[Test]
		public void ReservedAndNonAsciiCharactersAreEscaped()
		{
			Assert.That(TilesetUrl.FromAbsolutePath("/tmp/a#b/100% sure/Zoë/tileset.json"),
				Is.EqualTo("file:///tmp/a%23b/100%25%20sure/Zo%C3%AB/tileset.json"));
		}

		[Test]
		public void ARelativePathIsRefused()
		{
			Assert.Throws<ArgumentException>(() => TilesetUrl.FromAbsolutePath("harbor3d/tileset.json"));
			Assert.Throws<ArgumentException>(() => TilesetUrl.FromAbsolutePath(string.Empty));
		}

		[Test]
		public void TheTilesetLivesInHarbor3dUnderStreamingAssets()
		{
			var path = TilesetUrl.PathIn(Application.streamingAssetsPath).Replace('\\', '/');

			Assert.That(path, Does.EndWith("/StreamingAssets/harbor3d/tileset.json"));
		}
	}
}
