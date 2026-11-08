using System;
using System.IO;
using FerrySim.Audio.Clips;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FerrySim.Audio.EditorTools
{
	[InitializeOnLoad]
	public sealed class AudioCatalogBuilder : IPreprocessBuildWithReport
	{
		public const string ManifestPath = "Assets/FerrySim/Audio/clips.json";
		public const string ClipsPath = "Assets/FerrySim/Audio/Clips";
		public const string CatalogPath = "Assets/FerrySim/Audio/Resources/FerrySimAudio/Library.asset";
		public const string GeneratedLoopsPath = "Assets/FerrySim/Audio/Resources/FerrySimAudio/Loops";
		private static bool _scheduled;
		public int callbackOrder => -100;
		static AudioCatalogBuilder() => Schedule();
		public static string LoopPath(string id) => GeneratedLoopsPath + "/" + id + ".wav";
		public static void Schedule()
		{
			if (_scheduled) return;
			_scheduled = true;
			EditorApplication.delayCall += () => { _scheduled = false; Rebuild(); };
		}
		public void OnPreprocessBuild(BuildReport report) => Rebuild();

		[MenuItem("FerrySim/Audio/Rebuild Clip Catalog")]
		public static void Rebuild()
		{
			var manifest = ClipManifest.Parse(File.ReadAllText(ManifestPath));
			Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
			var catalog = AssetDatabase.LoadAssetAtPath<AudioClipCatalog>(CatalogPath);
			if (catalog == null)
			{
				catalog = ScriptableObject.CreateInstance<AudioClipCatalog>();
				AssetDatabase.CreateAsset(catalog, CatalogPath);
			}
			var entries = new AudioClipCatalog.Entry[manifest.slots.Length];
			for (var i = 0; i < entries.Length; i++)
			{
				var slot = manifest.slots[i];
				var path = string.IsNullOrEmpty(slot.file) ? null : ClipsPath + "/" + slot.file;
				AudioClip clip = null;
				if (path != null && File.Exists(path))
				{
					if (slot.loopEndSample > 0)
					{
						Directory.CreateDirectory(GeneratedLoopsPath);
						var cropped = LoopPath(slot.id);
						WaveLoop.Trim(path, cropped, slot.loopStartSample, slot.loopEndSample);
						AssetDatabase.ImportAsset(cropped, ImportAssetOptions.ForceSynchronousImport);
						path = cropped;
					}
					clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
					if (clip == null) throw new InvalidOperationException("Audio WAV did not import: " + path);
				}
				entries[i] = new AudioClipCatalog.Entry { id = slot.id, clip = clip };
			}
			var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
			var changed = catalog.manifest != asset || catalog.clips.Length != entries.Length;
			if (!changed) for (var i = 0; i < entries.Length; i++) if (catalog.clips[i].id != entries[i].id || catalog.clips[i].clip != entries[i].clip) { changed = true; break; }
			if (!changed) return;
			catalog.manifest = asset; catalog.clips = entries;
			EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssets();
		}
	}
}
