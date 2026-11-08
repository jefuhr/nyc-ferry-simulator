using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.Audio.Clips
{
	public readonly struct ResolvedClip
	{
		public ResolvedClip(ClipSlot slot, AudioClip clip, bool placeholder) { Slot = slot; Clip = clip; IsPlaceholder = placeholder; }
		public ClipSlot Slot { get; }
		public AudioClip Clip { get; }
		public bool IsPlaceholder { get; }
	}

	/// <summary>One cache per process, shared by the manual horn, existing fog signal and traffic. Resolves only at composition.</summary>
	public sealed class AudioClipProvider : IDisposable
	{
		private static AudioClipProvider _shared;
		private readonly Dictionary<string, ResolvedClip> _resolved = new Dictionary<string, ResolvedClip>(StringComparer.Ordinal);
		public static AudioClipProvider Shared => _shared ??= new AudioClipProvider(Resources.Load<AudioClipCatalog>("FerrySimAudio/Library"));
		public ClipManifest Manifest { get; }

		public AudioClipProvider(AudioClipCatalog catalog)
		{
			if (catalog == null || catalog.manifest == null) throw new InvalidOperationException("Missing Audio/Resources/FerrySimAudio/Library.asset. Run FerrySim > Audio > Rebuild Clip Catalog.");
			Manifest = ClipManifest.Parse(catalog.manifest.text);
			foreach (var slot in Manifest.slots)
			{
				AudioClip clip = null;
				foreach (var entry in catalog.clips) if (entry.id == slot.id) { clip = entry.clip; break; }
				var placeholder = clip == null;
				if (placeholder) clip = ProceduralClips.Create(slot.id, slot.loop);
				_resolved.Add(slot.id, new ResolvedClip(slot, clip, placeholder));
			}
		}

		public ResolvedClip Get(string id) => _resolved.TryGetValue(id, out var clip) ? clip : throw new ArgumentException("Unknown audio slot " + id + ".");
		public void Dispose()
		{
			foreach (var resolved in _resolved.Values)
			{
				if (!resolved.IsPlaceholder || resolved.Clip == null) continue;
				if (Application.isPlaying) UnityEngine.Object.Destroy(resolved.Clip); else UnityEngine.Object.DestroyImmediate(resolved.Clip);
			}
			_resolved.Clear();
		}
	}
}
