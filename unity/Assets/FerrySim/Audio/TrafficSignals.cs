using System;
using FerrySim.Traffic;
using FerrySim.World;
using UnityEngine;

namespace FerrySim.Audio
{
	public static class TrafficSignalTiming
	{
		public const double AudibleRangeMetres = 2000;
		/// <summary>A stable integer mix, never string.GetHashCode or UnityEngine.Random.</summary>
		public static double Phase(int mmsi)
		{
			var hash = unchecked((uint)mmsi);
			hash ^= hash >> 16; hash *= 0x7feb352d; hash ^= hash >> 15; hash *= 0x846ca68b; hash ^= hash >> 16;
			return hash / ((double)uint.MaxValue + 1) * FogSignalSchedule.IntervalSeconds;
		}
		public static double SecondsInCycle(double time, int mmsi)
		{
			var shifted = time + Phase(mmsi);
			return shifted - Math.Floor(shifted / FogSignalSchedule.IntervalSeconds) * FogSignalSchedule.IntervalSeconds;
		}
		public static bool IsBlasting(double time, int mmsi, FogSignalKind kind) => FogSignalSchedule.IsBlasting(kind, SecondsInCycle(time, mmsi));
		public static double Envelope(double second)
		{
			if (second >= 7) second -= 7;
			return EngineSoundMapping.Clamp(Math.Min(second / HornEnvelope.AttackSeconds, (5 - second) / HornEnvelope.ReleaseSeconds));
		}
	}

	/// <summary>Bounded nearest selection with stable MMSI ties; caller retains the roster and all positions.</summary>
	public sealed class NearestAudibleTargets
	{
		private readonly int[] _indices = new int[AudioVoiceBudget.Traffic];
		private readonly int[] _mmsis = new int[AudioVoiceBudget.Traffic];
		private readonly double[] _distances = new double[AudioVoiceBudget.Traffic];
		public int Count { get; private set; }
		public int IndexAt(int index) => _indices[index];
		public void Begin() => Count = 0;
		public void Consider(int index, int mmsi, double squaredDistance)
		{
			if (!(squaredDistance >= 0 && squaredDistance <= TrafficSignalTiming.AudibleRangeMetres * TrafficSignalTiming.AudibleRangeMetres)) return;
			var insert = Count;
			while (insert > 0 && (squaredDistance < _distances[insert - 1] || (squaredDistance == _distances[insert - 1] && mmsi < _mmsis[insert - 1]))) insert--;
			if (insert >= _indices.Length) return;
			for (var j = Math.Min(Count, _indices.Length - 1); j > insert; j--)
			{ _indices[j] = _indices[j - 1]; _mmsis[j] = _mmsis[j - 1]; _distances[j] = _distances[j - 1]; }
			_indices[insert] = index; _mmsis[insert] = mmsi; _distances[insert] = squaredDistance;
			Count = Math.Min(Count + 1, _indices.Length);
		}
	}

	/// <summary>Eight nearest moving AIS vessels within 2 km. Reads existing public hull snapshots; no traffic update/model changes.</summary>
	internal sealed class TrafficSignals
	{
		private readonly AudioVoice[] _voices = new AudioVoice[AudioVoiceBudget.Traffic];
		private readonly NearestAudibleTargets _nearest = new NearestAudibleTargets();
		private readonly int[] _assigned = new int[AudioVoiceBudget.Traffic];
		private readonly bool[] _used = new bool[AudioVoiceBudget.Traffic];
		private readonly int[] _mmsis = new int[AudioVoiceBudget.Traffic];
		private readonly long[] _cycles = new long[AudioVoiceBudget.Traffic];
		private readonly FogSignalKind[] _kinds = new FogSignalKind[AudioVoiceBudget.Traffic];
		public int SoundingCount { get; private set; }
		public TrafficSignals(Transform parent)
		{
			for (var i = 0; i < _voices.Length; i++)
			{
				_voices[i] = new AudioVoice(parent, "traffic fog " + i, "horn", range: (float)TrafficSignalTiming.AudibleRangeMetres);
				_voices[i].Source.priority = 20;
			}
		}
		public void Reset() { Stop(); Array.Clear(_mmsis, 0, _mmsis.Length); }
		public void Stop() { foreach (var voice in _voices) voice.Stop(); SoundingCount = 0; }

		public void Refresh(TrafficView traffic, Vector3 listener, double time, bool restricted, bool paused, float gain)
		{
			SoundingCount = 0;
			if (!restricted || traffic == null) { Stop(); return; }
			_nearest.Begin();
			var hulls = traffic.Hulls;
			for (var i = 0; i < hulls.Count; i++)
			{
				var hull = hulls[i];
				if (!hull.Shown || !hull.Moving) continue;
				_nearest.Consider(i, hull.Mmsi, (hull.transform.position - listener).sqrMagnitude);
			}
			// Preserve a selected vessel's voice while its distance rank changes; no restart or mid-cycle pattern change.
			Array.Clear(_used, 0, _used.Length);
			for (var i = 0; i < _assigned.Length; i++)
			{
				_assigned[i] = -1;
				for (var j = 0; j < _nearest.Count; j++)
					if (_mmsis[i] == hulls[_nearest.IndexAt(j)].Mmsi) { _assigned[i] = j; _used[j] = true; break; }
			}
			for (var i = 0; i < _assigned.Length; i++)
				if (_assigned[i] < 0) for (var j = 0; j < _nearest.Count; j++)
					if (!_used[j]) { _assigned[i] = j; _used[j] = true; break; }
			for (var i = 0; i < _voices.Length; i++)
			{
				var voice = _voices[i];
				if (_assigned[i] < 0) { voice.Stop(); _mmsis[i] = 0; continue; }
				var hull = hulls[_nearest.IndexAt(_assigned[i])];
				var cycle = (long)Math.Floor((time + TrafficSignalTiming.Phase(hull.Mmsi)) / FogSignalSchedule.IntervalSeconds);
				if (_mmsis[i] != hull.Mmsi || _cycles[i] != cycle)
				{
					voice.Stop(); _mmsis[i] = hull.Mmsi; _cycles[i] = cycle;
					// AIS provides ground speed, not speed through water. This render estimate cannot infer navigational status.
					_kinds[i] = FogSignalSchedule.KindForRecordedKnots(FerrySim.Core.Contracts.Units.MetresPerSecondToKnots(hull.SpeedOverGround));
				}
				var since = TrafficSignalTiming.SecondsInCycle(time, hull.Mmsi);
				var sounding = FogSignalSchedule.IsBlasting(_kinds[i], since);
				voice.Source.transform.position = hull.transform.TransformPoint(new Vector3(0, (float)hull.Height, 0));
				voice.Level(gain * TrafficSignalTiming.Envelope(since));
				voice.Loop(sounding, paused);
				if (sounding) SoundingCount++;
			}
		}
	}
}
