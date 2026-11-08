using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Opt-in per-landing fender experiments, mapped explicitly to stable contact segment IDs by the caller.
	/// Does not change geometry, order, eligibility or any unmatched surface. No real-landing values are supplied:
	/// callers must retain their measurement source and uncertainty with the scenario, never infer stiffness from a video.
	/// Create a new simulation when changing overrides (ContactModel caches prepared segment laws).
	/// </summary>
	public sealed class ContactSurfaceOverrides : IWorldGeometry
	{
		private readonly IWorldGeometry _world;
		private readonly Dictionary<int, ContactSurface> _surfaces = new Dictionary<int, ContactSurface>();

		public ContactSurfaceOverrides(IWorldGeometry world, IReadOnlyDictionary<int, ContactSurface> surfaces, bool enabled = false)
		{
			_world = world ?? throw new ArgumentNullException(nameof(world));
			if (surfaces == null) throw new ArgumentNullException(nameof(surfaces));
			Enabled = enabled;
			foreach (var entry in surfaces)
			{
				var s = entry.Value;
				// Revalidate value types, including default(ContactSurface).
				_surfaces.Add(entry.Key, new ContactSurface(s.Stiffness, s.Damping, s.Friction, s.MaxCompression));
			}
		}

		public bool Enabled { get; }

		public int QuerySegments(double north, double east, double radius, ContactSegment[] buffer)
		{
			var count = _world.QuerySegments(north, east, radius, buffer);
			if (!Enabled) return count;
			for (var i = 0; i < count; i++)
			{
				var segment = buffer[i];
				if (_surfaces.TryGetValue(segment.Id, out var surface))
					buffer[i] = new ContactSegment(segment.Start, segment.End, surface, segment.Id);
			}
			return count;
		}
	}
}
