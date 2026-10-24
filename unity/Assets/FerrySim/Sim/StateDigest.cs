using System;
using System.Globalization;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// A running FNV-1a 64-bit hash of every bit of every state in a run. The telemetry CSV rounds positions to
	/// millimetres, which would hide a divergence in the last bits; recording this digest lets a replay prove
	/// it reproduced the states exactly. Allocation-free per step.
	/// </summary>
	public sealed class StateDigest
	{
		public const string Prefix = "fnv1a64:";

		private const ulong OffsetBasis = 14695981039346656037UL;
		private const ulong Prime = 1099511628211UL;

		private ulong _hash = OffsetBasis;

		/// <summary>States added so far.</summary>
		public long Count { get; private set; }

		public ulong Value => _hash;

		/// <summary>The digest as recorded, for example <c>fnv1a64:9ae16a3b2f90404f</c>.</summary>
		public string Text => Prefix + _hash.ToString("x16", CultureInfo.InvariantCulture);

		public void Add(in VesselState state)
		{
			Mix(state.Time);
			Mix(state.North);
			Mix(state.East);
			Mix(state.Down);
			Mix(state.Roll);
			Mix(state.Pitch);
			Mix(state.Heading);
			Mix(state.Surge);
			Mix(state.Sway);
			Mix(state.Heave);
			Mix(state.RollRate);
			Mix(state.PitchRate);
			Mix(state.YawRate);
			Count++;
		}

		public void Reset()
		{
			_hash = OffsetBasis;
			Count = 0;
		}

		/// <summary>A state's values at full precision, for the human-readable end-of-run line.</summary>
		public static string Describe(in VesselState state) =>
			"t=" + ControlTrace.FormatValue(state.Time) +
			" north=" + ControlTrace.FormatValue(state.North) +
			" east=" + ControlTrace.FormatValue(state.East) +
			" heading=" + ControlTrace.FormatValue(state.Heading) +
			" surge=" + ControlTrace.FormatValue(state.Surge) +
			" sway=" + ControlTrace.FormatValue(state.Sway) +
			" yaw_rate=" + ControlTrace.FormatValue(state.YawRate);

		private void Mix(double value)
		{
			var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
			for (var shift = 0; shift < 64; shift += 8)
			{
				_hash ^= (bits >> shift) & 0xFFUL;
				_hash = unchecked(_hash * Prime);
			}
		}
	}
}
