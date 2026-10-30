using System;
using System.Collections.Generic;

namespace FerrySim.Chartplotter.Tests
{
	/// <summary>Targets on straight tracks at constant speed, present between two times; a fixture for the list, the alarm and the captures.</summary>
	public sealed class SyntheticTargets : ITargetSource
	{
		private sealed class Entry
		{
			public double North0, East0, VelocityNorth, VelocityEast, Heading, From, To, Length, Beam;
			public bool HeadingKnown, Moving;
			public string Name;
			public int Mmsi;
		}

		private readonly List<Entry> _entries = new List<Entry>();

		public long Generation { get; private set; } = 1;

		public int Count => _entries.Count;

		/// <summary>Adds a target at a position at time 0, moving with a velocity (m/s), heading along it unless given.</summary>
		public int Add(string name, int mmsi, double north, double east, double velocityNorth, double velocityEast, double length = 30, double beam = 9,
			bool moving = true, double from = double.NegativeInfinity, double to = double.PositiveInfinity, double? headingDegrees = null)
		{
			var speed = Math.Sqrt(velocityNorth * velocityNorth + velocityEast * velocityEast);
			_entries.Add(new Entry
			{
				Name = name,
				Mmsi = mmsi,
				North0 = north,
				East0 = east,
				VelocityNorth = velocityNorth,
				VelocityEast = velocityEast,
				Heading = headingDegrees.HasValue ? headingDegrees.Value * Math.PI / 180.0 : speed > 0 ? Math.Atan2(velocityEast, velocityNorth) : 0.0,
				HeadingKnown = headingDegrees.HasValue || speed > 0,
				Moving = moving,
				From = from,
				To = to,
				Length = length,
				Beam = beam,
			});
			Generation++;
			return _entries.Count - 1;
		}

		public void Clear()
		{
			_entries.Clear();
			Generation++;
		}

		public bool TrySample(int target, double time, out TargetSample sample)
		{
			if (target < 0 || target >= _entries.Count || time < _entries[target].From || time > _entries[target].To)
			{
				sample = default;
				return false;
			}

			var e = _entries[target];
			sample = new TargetSample(e.North0 + e.VelocityNorth * time, e.East0 + e.VelocityEast * time, e.VelocityNorth, e.VelocityEast, e.Heading, e.HeadingKnown, e.Moving, e.Length, e.Beam);
			return true;
		}

		public string Name(int target) => target >= 0 && target < _entries.Count ? _entries[target].Name ?? string.Empty : string.Empty;

		public int Mmsi(int target) => target >= 0 && target < _entries.Count ? _entries[target].Mmsi : 0;
	}

	/// <summary>The run's targets with synthetic ones appended after them, for an alarm staged on a real day.</summary>
	public sealed class CompositeTargets : ITargetSource
	{
		private readonly ITargetSource _first;
		private readonly ITargetSource _second;

		public CompositeTargets(ITargetSource first, ITargetSource second)
		{
			_first = first;
			_second = second;
		}

		public long Generation => _first.Generation * 1000003 + _second.Generation;

		public int Count => _first.Count + _second.Count;

		public bool TrySample(int target, double time, out TargetSample sample) =>
			target < _first.Count ? _first.TrySample(target, time, out sample) : _second.TrySample(target - _first.Count, time, out sample);

		public string Name(int target) => target < _first.Count ? _first.Name(target) : _second.Name(target - _first.Count);

		public int Mmsi(int target) => target < _first.Count ? _first.Mmsi(target) : _second.Mmsi(target - _first.Count);
	}
}
