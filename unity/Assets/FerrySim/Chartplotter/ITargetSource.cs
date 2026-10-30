using System;
using FerrySim.Core.Traffic;
using FerrySim.Sim;

namespace FerrySim.Chartplotter
{
	/// <summary>A displayed target at one time: its AIS-replayed position, motion and particulars, SI and world axes.</summary>
	public readonly struct TargetSample
	{
		public TargetSample(double north, double east, double velocityNorth, double velocityEast, double heading, bool headingKnown, bool moving, double length, double beam)
		{
			North = north;
			East = east;
			VelocityNorth = velocityNorth;
			VelocityEast = velocityEast;
			Heading = heading;
			HeadingKnown = headingKnown;
			Moving = moving;
			Length = length;
			Beam = beam;
		}

		public double North { get; }

		public double East { get; }

		/// <summary>Velocity over the ground, m/s.</summary>
		public double VelocityNorth { get; }

		public double VelocityEast { get; }

		/// <summary>True heading, rad clockwise from north; meaningful only when <see cref="HeadingKnown"/>.</summary>
		public double Heading { get; }

		public bool HeadingKnown { get; }

		/// <summary>False for a vessel the pack holds static all day (moored or at anchor), whatever its speed now.</summary>
		public bool Moving { get; }

		/// <summary>Hull length, m.</summary>
		public double Length { get; }

		/// <summary>Hull beam, m.</summary>
		public double Beam { get; }

		public double SpeedOverGround => Math.Sqrt(VelocityNorth * VelocityNorth + VelocityEast * VelocityEast);
	}

	/// <summary>
	/// Where the chartplotter's AIS targets come from. The run's traffic implements it (<see cref="TrafficRunTargets"/>);
	/// tests and the dev scene use synthetic sources. Queries are pure functions of the index and the time and allocate nothing.
	/// </summary>
	public interface ITargetSource
	{
		/// <summary>Changes whenever the roster changes (a new run, another day), so the display can forget its selection.</summary>
		long Generation { get; }

		/// <summary>Targets in the roster, present or not.</summary>
		int Count { get; }

		/// <summary>A target's position and motion at a run time (s since the scenario start), or false when it is absent then.</summary>
		bool TrySample(int target, double time, out TargetSample sample);

		/// <summary>AIS name, or an empty string.</summary>
		string Name(int target);

		int Mmsi(int target);
	}

	/// <summary>The run's traffic as targets, read from the runner the way the traffic view reads it: never written to.</summary>
	public sealed class TrafficRunTargets : ITargetSource
	{
		private readonly VesselSimulationRunner _runner;
		private TrafficRun _seen;
		private long _generation;

		public TrafficRunTargets(VesselSimulationRunner runner)
		{
			_runner = runner != null ? runner : throw new ArgumentNullException(nameof(runner));
		}

		private TrafficRun Traffic
		{
			get
			{
				var traffic = _runner.Traffic;
				if (!ReferenceEquals(traffic, _seen))
				{
					_seen = traffic;
					_generation++;
				}

				return traffic;
			}
		}

		public long Generation
		{
			get
			{
				_ = Traffic;
				return _generation;
			}
		}

		public int Count => Traffic?.Pack.Vessels.Count ?? 0;

		public bool TrySample(int target, double time, out TargetSample sample)
		{
			var traffic = Traffic;
			if (traffic == null || target < 0 || target >= traffic.Pack.Vessels.Count || target == traffic.RemovedVessel ||
				!traffic.Replay.TrySample(target, traffic.PackTime(time), out var replayed))
			{
				sample = default;
				return false;
			}

			var vessel = traffic.Pack.Vessels[target];
			sample = new TargetSample(replayed.North, replayed.East, replayed.VelocityNorth, replayed.VelocityEast, replayed.Heading,
				replayed.HeadingSource != HeadingSource.Unknown, vessel.Moving, vessel.Hull.Length, vessel.Hull.Beam);
			return true;
		}

		public string Name(int target)
		{
			var traffic = Traffic;
			return traffic != null && target >= 0 && target < traffic.Pack.Vessels.Count ? traffic.Pack.Vessels[target].Name ?? string.Empty : string.Empty;
		}

		public int Mmsi(int target)
		{
			var traffic = Traffic;
			return traffic != null && target >= 0 && target < traffic.Pack.Vessels.Count ? traffic.Pack.Vessels[target].Mmsi : 0;
		}
	}
}
