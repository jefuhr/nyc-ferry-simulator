using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.Audio.Clips
{
	public static class AudioSlots
	{
		public static readonly string[] Ids = { "engine-idle", "engine-mid", "engine-high", "engine-low", "propeller", "clutch-engage", "clutch-disengage", "horn", "water-wash", "wind", "gull", "fender", "barge-creak" };
	}

	[Serializable]
	public sealed class ClipSlot
	{
		public string id;
		public string file;
		public bool loop;
		public int loopStartSample;
		public int loopEndSample;
		public float baseGain;
		public string source;
		public string date;
		public string edits;
	}

	[Serializable]
	public sealed class ClipManifest
	{
		public int version;
		public ClipSlot[] slots;

		public static ClipManifest Parse(string json)
		{
			ClipManifest manifest;
			try { manifest = JsonUtility.FromJson<ClipManifest>(json); }
			catch (Exception error) { throw new ArgumentException("Audio clips.json is not valid JSON.", error); }
			if (manifest == null || manifest.version != 1 || manifest.slots == null || manifest.slots.Length != AudioSlots.Ids.Length)
				throw new ArgumentException("Audio clips.json needs version 1 and exactly one of every documented slot.");
			var ids = new HashSet<string>(StringComparer.Ordinal);
			foreach (var slot in manifest.slots)
			{
				if (slot == null || Array.IndexOf(AudioSlots.Ids, slot.id) < 0 || !ids.Add(slot.id)) throw new ArgumentException("Unknown or duplicate audio slot.");
				if (!(slot.baseGain >= 0 && slot.baseGain <= 1) || string.IsNullOrWhiteSpace(slot.source) || string.IsNullOrWhiteSpace(slot.edits) ||
					!DateTime.TryParseExact(slot.date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
					throw new ArgumentException("Audio slot " + slot.id + " needs a base gain in [0,1] and source/date/edits provenance.");
				if (!string.IsNullOrEmpty(slot.file) && (slot.file.StartsWith("/", StringComparison.Ordinal) || slot.file.Contains("..") ||
					slot.file.Contains("\\") || slot.file.Contains(":") || !slot.file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
					throw new ArgumentException("Audio slot " + slot.id + " file must be a relative WAV path under Audio/Clips.");
				if (slot.loopStartSample < 0 || slot.loopEndSample < 0 || (slot.loopEndSample > 0 && slot.loopEndSample <= slot.loopStartSample) ||
					(slot.loopStartSample > 0 && slot.loopEndSample == 0) || (!slot.loop && (slot.loopStartSample != 0 || slot.loopEndSample != 0)))
					throw new ArgumentException("Audio slot " + slot.id + " has invalid loop sample frames (end exclusive; 0/0 means the whole file).");
			}
			return manifest;
		}

		public ClipSlot Find(string id)
		{
			for (var i = 0; i < slots.Length; i++) if (slots[i].id == id) return slots[i];
			throw new ArgumentException("No audio slot " + id + ".");
		}
	}
}
