using System;
using System.IO;
using FerrySim.Audio.Clips;
using FerrySim.Audio.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FerrySim.Audio.Tests
{
	public class ClipLibraryTests
	{
		private const string TinyPath = "Assets/FerrySim/Audio/Clips/audio-import-test.wav";

		private static ClipManifest Fixture()
		{
			var slots = new ClipSlot[AudioSlots.Ids.Length];
			for (var i = 0; i < slots.Length; i++)
			{
				var id = AudioSlots.Ids[i];
				slots[i] = new ClipSlot { id = id, file = id + ".wav", loop = i < 5 || id == "horn" || id == "water-wash" || id == "wind" || id.StartsWith("alarm-"),
					baseGain = .5f, source = "procedural test fixture", date = "2026-11-02", edits = "generated fixture" };
			}
			return new ClipManifest { version = 1, slots = slots };
		}

		[Test]
		public void ManifestContainsEverySlotAndMissingFilesResolveToSharedPlaceholders()
		{
			var manifest = ClipManifest.Parse(File.ReadAllText("Assets/FerrySim/Audio/clips.json"));
			Assert.That(manifest.slots.Length, Is.EqualTo(AudioSlots.Ids.Length));
			var catalog = ScriptableObject.CreateInstance<AudioClipCatalog>();
			catalog.manifest = new TextAsset(JsonUtility.ToJson(Fixture()));
			try
			{
				using (var provider = new AudioClipProvider(catalog))
				{
					foreach (var id in AudioSlots.Ids)
					{
						var slot = manifest.Find(id);
						Assert.That(slot.source, Is.Not.Empty);
						Assert.That(slot.date, Is.Not.Empty); Assert.That(slot.edits, Is.Not.Empty);
						var a = provider.Get(id);
						Assert.That(a.Clip, Is.Not.Null); Assert.That(a.IsPlaceholder, Is.True);
						Assert.That(provider.Get(id).Clip, Is.SameAs(a.Clip));
					}
					Assert.That(provider.Get("horn").Clip.length, Is.EqualTo(5).Within(.001));
				}
			}
			finally { UnityEngine.Object.DestroyImmediate(catalog.manifest); UnityEngine.Object.DestroyImmediate(catalog); }
		}

		[Test]
		public void PreparedCrewRecordingsResolveWithMonoVorbisAndStreamingPolicy()
		{
			AudioCatalogBuilder.Rebuild();
			var catalog = AssetDatabase.LoadAssetAtPath<AudioClipCatalog>(AudioCatalogBuilder.CatalogPath);
			var manifest = ClipManifest.Parse(catalog.manifest.text);
			var real = new[] { "engine-idle", "engine-high", "engine-low", "propeller", "clutch-engage", "clutch-disengage", "water-wash", "alarm-steering" };
			var sizes = "slot,source_wav_bytes,loop_wav_bytes,editor_audio_util_sound_size_bytes\n";
			// This load-dependent editor diagnostic is not a compressed/player-size measurement. Streaming clips can return zero.
			var audioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
			var soundSize = audioUtil?.GetMethod("GetSoundSize", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
				null, new[] { typeof(AudioClip) }, null);
			using (var provider = new AudioClipProvider(catalog))
			{
				foreach (var id in real)
				{
					var slot = manifest.Find(id);
					Assert.That(slot.source, Is.EqualTo("crew recording (private), 2026-11"), id);
					var resolved = provider.Get(id);
					Assert.That(resolved.IsPlaceholder, Is.False, id);
					Assert.That(resolved.Clip.channels, Is.EqualTo(1), id);
					Assert.That(resolved.Clip.frequency, Is.EqualTo(22050), id);
					var path = AssetDatabase.GetAssetPath(resolved.Clip);
					var importer = (AudioImporter)AssetImporter.GetAtPath(path);
					Assert.That(importer.forceToMono, Is.True, id);
					Assert.That(importer.defaultSampleSettings.compressionFormat, Is.EqualTo(AudioCompressionFormat.Vorbis), id);
					var streaming = slot.loop && WaveLoop.Duration(path) >= AudioImportPolicy.StreamingSeconds;
					Assert.That(importer.defaultSampleSettings.loadType, Is.EqualTo(streaming ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad), id);
					Assert.That(importer.defaultSampleSettings.preloadAudioData, Is.EqualTo(!streaming), id);
					if (slot.loop)
					{
						Assert.That(slot.loopStartSample, Is.Zero, id);
						Assert.That(slot.loopEndSample, Is.EqualTo(resolved.Clip.samples), id);
					}
					var sourceBytes = new FileInfo(AudioCatalogBuilder.ClipsPath + "/" + slot.file).Length;
					var loopBytes = slot.loop ? new FileInfo(AudioCatalogBuilder.LoopPath(id)).Length : 0;
					var editorSoundSize = soundSize == null ? "unavailable" : Convert.ToString(soundSize.Invoke(null, new object[] { resolved.Clip }), System.Globalization.CultureInfo.InvariantCulture);
					sizes += id + "," + sourceBytes + "," + loopBytes + "," + editorSoundSize + "\n";
				}
				foreach (var id in new[] { "engine-mid", "horn", "wind", "gull", "fender", "barge-creak", "alarm-general" })
					Assert.That(provider.Get(id).IsPlaceholder, Is.True, id + " has no verified real source");
			}
			var evidence = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/sound-gaps"));
			Directory.CreateDirectory(evidence);
			File.WriteAllText(Path.Combine(evidence, "unity-audio-sizes.csv"), sizes);
		}

		[Test]
		public void InvalidManifestCannotEscapeClipFolderOrHideBadLoopPoints()
		{
			var json = JsonUtility.ToJson(Fixture(), true);
			Assert.Throws<ArgumentException>(() => ClipManifest.Parse("{}"));
			Assert.Throws<ArgumentException>(() => ClipManifest.Parse(json.Replace("engine-idle.wav", "../outside.wav")));
			Assert.Throws<ArgumentException>(() => ClipManifest.Parse(json.Replace("\"loopEndSample\": 0", "\"loopEndSample\": -1")));
			Assert.Throws<ArgumentException>(() => ClipManifest.Parse(json.Replace("\"id\": \"engine-mid\"", "\"id\": \"engine-idle\"")));
		}

		[Test]
		public void ProceduralLoopsAreFiniteAndSeamlessAndOneShotsHaveQuietEdges()
		{
			foreach (var id in AudioSlots.Ids)
			{
				var slot = Fixture().Find(id); var clip = ProceduralClips.Create(id, slot.loop);
				var samples = new float[clip.samples]; Assert.That(clip.GetData(samples, 0), Is.True);
				var peak = 0f; var maximumStep = 0f; var finite = true;
				for (var i = 0; i < samples.Length; i++)
				{
					var sample = samples[i]; finite &= !float.IsNaN(sample) && !float.IsInfinity(sample);
					peak = Math.Max(peak, Math.Abs(sample));
					if (i > 0) maximumStep = Math.Max(maximumStep, Math.Abs(samples[i] - samples[i - 1]));
				}
				Assert.That(finite, Is.True, id);
				Assert.That(peak, Is.InRange(.001f, 1f));
				Assert.That(Math.Abs(samples[0] - samples[samples.Length - 1]), Is.LessThanOrEqualTo(slot.loop ? maximumStep * 1.01f + 1e-6f : .01f), id + " seam/edges");
				UnityEngine.Object.DestroyImmediate(clip);
			}
		}

		[Test]
		public void TinyGeneratedWavImportsAsVorbisDecompressedMono()
		{
			try
			{
				WriteWave(TinyPath, 800, 8000);
				AssetDatabase.ImportAsset(TinyPath, ImportAssetOptions.ForceSynchronousImport);
				var importer = (AudioImporter)AssetImporter.GetAtPath(TinyPath);
				Assert.That(importer.defaultSampleSettings.compressionFormat, Is.EqualTo(AudioCompressionFormat.Vorbis));
				Assert.That(importer.defaultSampleSettings.loadType, Is.EqualTo(AudioClipLoadType.DecompressOnLoad));
				Assert.That(importer.forceToMono, Is.True);
				Assert.That(importer.defaultSampleSettings.preloadAudioData, Is.True);
				var longLoop = AudioImportPolicy.Settings(true, 12);
				Assert.That(longLoop.loadType, Is.EqualTo(AudioClipLoadType.Streaming));
				Assert.That(AudioImportPolicy.Settings(false, 12).loadType, Is.EqualTo(AudioClipLoadType.DecompressOnLoad));
			}
			finally { AssetDatabase.DeleteAsset(TinyPath); }
		}

		[Test]
		public void CatalogGenerationIsReproducible()
		{
			AudioCatalogBuilder.Rebuild();
			var first = File.ReadAllBytes(AudioCatalogBuilder.CatalogPath);
			AudioCatalogBuilder.Rebuild();
			Assert.That(File.ReadAllBytes(AudioCatalogBuilder.CatalogPath), Is.EqualTo(first));
			var catalog = AssetDatabase.LoadAssetAtPath<AudioClipCatalog>(AudioCatalogBuilder.CatalogPath);
			Assert.That(catalog.manifest, Is.Not.Null); Assert.That(catalog.clips.Length, Is.EqualTo(AudioSlots.Ids.Length));
		}

		[Test]
		public void LoopCropKeepsExactSampleFramesAndRejectsOutOfBoundsPoints()
		{
			var input = Path.GetTempFileName(); var output = input + ".wav";
			try
			{
				WriteWave(input, 100, 8000);
				WaveLoop.Trim(input, output, 10, 80);
				var bytes = File.ReadAllBytes(output);
				Assert.That(BitConverter.ToInt32(bytes, 40), Is.EqualTo(70 * 2));
				Assert.That(BitConverter.ToInt16(bytes, 44), Is.EqualTo(10));
				Assert.That(BitConverter.ToInt16(bytes, bytes.Length - 2), Is.EqualTo(79));
				Assert.Throws<ArgumentException>(() => WaveLoop.Trim(input, output, 80, 101));
			}
			finally { File.Delete(input); File.Delete(output); }
		}

		private static void WriteWave(string path, int frames, int sampleRate)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
			using (var writer = new BinaryWriter(File.Create(path)))
			{
				writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2);
				writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
				writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
				writer.Write((short)2); writer.Write((short)16);
				writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
				for (var i = 0; i < frames; i++) writer.Write((short)i);
			}
		}
	}
}
