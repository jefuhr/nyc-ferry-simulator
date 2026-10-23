using System;
using System.Collections.Generic;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// Material of a fixed surface the hull can touch: a fender, a pier face, a bulkhead. Values are per
	/// metre of contact length so long and short segments behave the same way.
	/// </summary>
	public readonly struct ContactSurface
	{
		public ContactSurface(double stiffness, double damping, double friction, double maxCompression)
		{
			if (!IsFinite(stiffness) || stiffness <= 0.0 ||
				!IsFinite(damping) || damping < 0.0 ||
				!IsFinite(friction) || friction < 0.0 ||
				!IsFinite(maxCompression) || maxCompression <= 0.0)
			{
				throw new ArgumentException("Contact surface values must be finite, with positive stiffness and compression limit and non-negative damping and friction.");
			}

			Stiffness = stiffness;
			Damping = damping;
			Friction = friction;
			MaxCompression = maxCompression;
		}

		/// <summary>Normal stiffness per metre of contact length, N/m per m.</summary>
		public double Stiffness { get; }

		/// <summary>Normal damping per metre of contact length, N·s/m per m.</summary>
		public double Damping { get; }

		/// <summary>Coulomb friction coefficient along the surface.</summary>
		public double Friction { get; }

		/// <summary>Compression at which the fender bottoms out and contact becomes rigid, metres.</summary>
		public double MaxCompression { get; }

		private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
	}

	/// <summary>
	/// A straight piece of fixed structure in world coordinates (north, east). The structure is solid on
	/// the right-hand side when walking from <see cref="Start"/> to <see cref="End"/> seen from above,
	/// so the water is on the left. A segment running north has its solid side to the east.
	/// </summary>
	public readonly struct ContactSegment
	{
		public ContactSegment(HorizontalVector start, HorizontalVector end, ContactSurface surface, int id)
		{
			var length = (end - start).Magnitude;
			if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0.0)
			{
				throw new ArgumentException("A contact segment needs two distinct, finite end points.");
			}

			Start = start;
			End = end;
			Surface = surface;
			Id = id;
		}

		public HorizontalVector Start { get; }

		public HorizontalVector End { get; }

		public ContactSurface Surface { get; }

		/// <summary>Stable identifier for telemetry and debriefs, for example a fender on a landing face.</summary>
		public int Id { get; }

		public double Length => (End - Start).Magnitude;

		/// <summary>Unit vector from <see cref="Start"/> toward <see cref="End"/>.</summary>
		public HorizontalVector Direction
		{
			get
			{
				var delta = End - Start;
				return delta * (1.0 / delta.Magnitude);
			}
		}

		/// <summary>Unit normal pointing out of the structure into the water (the left-hand side).</summary>
		public HorizontalVector WaterNormal
		{
			get
			{
				var direction = Direction;
				return new HorizontalVector(direction.East, -direction.North);
			}
		}

		/// <summary>Shortest distance from a point to this segment, metres.</summary>
		public double DistanceTo(double north, double east)
		{
			var delta = End - Start;
			var lengthSquared = delta.North * delta.North + delta.East * delta.East;
			var t = ((north - Start.North) * delta.North + (east - Start.East) * delta.East) / lengthSquared;
			t = Math.Clamp(t, 0.0, 1.0);
			var closestNorth = Start.North + t * delta.North;
			var closestEast = Start.East + t * delta.East;
			var dn = north - closestNorth;
			var de = east - closestEast;
			return Math.Sqrt(dn * dn + de * de);
		}
	}

	/// <summary>
	/// Fixed structures near the vessel: landing faces, piers, bulkheads. Implementations must be
	/// deterministic (same segments in the same order for the same query) and allocation-free per query.
	/// </summary>
	public interface IWorldGeometry
	{
		/// <summary>
		/// Writes the segments that come within <paramref name="radius"/> metres of (north, east) into
		/// <paramref name="buffer"/> and returns how many it wrote, never more than the buffer length.
		/// </summary>
		int QuerySegments(double north, double east, double radius, ContactSegment[] buffer);
	}

	/// <summary>Open water: no structures anywhere.</summary>
	public sealed class EmptyWorldGeometry : IWorldGeometry
	{
		private EmptyWorldGeometry()
		{
		}

		public static EmptyWorldGeometry Instance { get; } = new EmptyWorldGeometry();

		public int QuerySegments(double north, double east, double radius, ContactSegment[] buffer) => 0;
	}

	/// <summary>
	/// A fixed list of segments searched linearly. Fine for a landing or a test; large worlds will want a
	/// spatial index behind the same interface.
	/// </summary>
	public sealed class SegmentListGeometry : IWorldGeometry
	{
		private readonly ContactSegment[] _segments;

		public SegmentListGeometry(IEnumerable<ContactSegment> segments)
		{
			if (segments == null)
			{
				throw new ArgumentNullException(nameof(segments));
			}

			_segments = new List<ContactSegment>(segments).ToArray();
		}

		public IReadOnlyList<ContactSegment> Segments => _segments;

		public int QuerySegments(double north, double east, double radius, ContactSegment[] buffer)
		{
			if (buffer == null)
			{
				throw new ArgumentNullException(nameof(buffer));
			}

			var count = 0;
			for (var i = 0; i < _segments.Length && count < buffer.Length; i++)
			{
				if (_segments[i].DistanceTo(north, east) <= radius)
				{
					buffer[count] = _segments[i];
					count++;
				}
			}

			return count;
		}
	}
}
