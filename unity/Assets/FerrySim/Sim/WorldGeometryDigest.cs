using System;
using System.Globalization;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>
	/// Names the exact fixed structures a run was simulated against: a 64-bit FNV-1a hash of every bit of every
	/// segment of a <see cref="SegmentListGeometry"/>, in order (id, start, end and surface). Recordings carry it
	/// in the <see cref="HeaderKey"/> header, so a replay against different structures is reported instead of
	/// diverging at the first contact.
	/// </summary>
	public static class WorldGeometryDigest
	{
		public const string HeaderKey = "world_geometry_digest";

		/// <summary>The digest of open water (<see cref="EmptyWorldGeometry"/>).</summary>
		public const string OpenWater = "none";

		private const ulong OffsetBasis = 14695981039346656037UL;
		private const ulong Prime = 1099511628211UL;

		/// <summary>
		/// The digest, for example <c>fnv1a64:9ae16a3b2f90404f</c>; <see cref="OpenWater"/> for open water; null
		/// for a geometry that cannot list its segments.
		/// </summary>
		public static string Of(IWorldGeometry geometry)
		{
			if (geometry == null || geometry is EmptyWorldGeometry)
			{
				return OpenWater;
			}

			if (!(geometry is SegmentListGeometry list))
			{
				return null;
			}

			var hash = OffsetBasis;
			var segments = list.Segments;
			Mix(ref hash, segments.Count);
			for (var i = 0; i < segments.Count; i++)
			{
				var segment = segments[i];
				var surface = segment.Surface;
				Mix(ref hash, segment.Id);
				Mix(ref hash, segment.Start.North);
				Mix(ref hash, segment.Start.East);
				Mix(ref hash, segment.End.North);
				Mix(ref hash, segment.End.East);
				Mix(ref hash, surface.Stiffness);
				Mix(ref hash, surface.Damping);
				Mix(ref hash, surface.Friction);
				Mix(ref hash, surface.MaxCompression);
			}

			return StateDigest.Prefix + hash.ToString("x16", CultureInfo.InvariantCulture);
		}

		private static void Mix(ref ulong hash, double value) => Mix(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

		private static void Mix(ref ulong hash, int value) => Mix(ref hash, unchecked((ulong)(uint)value));

		private static void Mix(ref ulong hash, ulong bits)
		{
			for (var shift = 0; shift < 64; shift += 8)
			{
				hash ^= (bits >> shift) & 0xFFUL;
				hash = unchecked(hash * Prime);
			}
		}
	}
}
