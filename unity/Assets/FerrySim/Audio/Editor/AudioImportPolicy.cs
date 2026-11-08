using System;
using System.IO;
using FerrySim.Audio.Clips;
using UnityEditor;
using UnityEngine;

namespace FerrySim.Audio.EditorTools
{
	public static class AudioImportPolicy
	{
		public const double StreamingSeconds = 10;
		public static AudioImporterSampleSettings Settings(bool loop, double duration) => new AudioImporterSampleSettings
		{
			compressionFormat = AudioCompressionFormat.Vorbis,
			quality = .7f,
			loadType = loop && duration >= StreamingSeconds ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad,
			preloadAudioData = !(loop && duration >= StreamingSeconds),
			sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate,
		};

		public static bool IsLoop(string path)
		{
			var manifest = ClipManifest.Parse(File.ReadAllText(AudioCatalogBuilder.ManifestPath));
			foreach (var slot in manifest.slots)
				if (path == AudioCatalogBuilder.ClipsPath + "/" + slot.file || path == AudioCatalogBuilder.LoopPath(slot.id)) return slot.loop;
			return false;
		}
	}

	public sealed class AudioClipPostprocessor : AssetPostprocessor
	{
		private void OnPreprocessAudio()
		{
			if (!assetPath.StartsWith(AudioCatalogBuilder.ClipsPath + "/", StringComparison.Ordinal) &&
				!assetPath.StartsWith(AudioCatalogBuilder.GeneratedLoopsPath + "/", StringComparison.Ordinal)) return;
			var importer = (AudioImporter)assetImporter;
			importer.forceToMono = true;
			importer.loadInBackground = false;
			importer.defaultSampleSettings = AudioImportPolicy.Settings(AudioImportPolicy.IsLoop(assetPath), WaveLoop.Duration(assetPath));
		}

		private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
		{
			if (TouchesClips(imported) || TouchesClips(deleted) || TouchesClips(moved) || TouchesClips(movedFrom)) AudioCatalogBuilder.Schedule();
		}
		private static bool TouchesClips(string[] paths)
		{
			foreach (var path in paths)
				if (path == AudioCatalogBuilder.ManifestPath || path.StartsWith(AudioCatalogBuilder.ClipsPath + "/", StringComparison.Ordinal)) return true;
			return false;
		}
	}
}
