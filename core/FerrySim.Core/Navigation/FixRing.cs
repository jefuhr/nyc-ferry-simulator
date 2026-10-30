using System;

namespace FerrySim.Core.Navigation
{
	/// <summary>
	/// Own ship's track: a ring of position fixes with a fixed capacity, taking a new fix only when the vessel has moved
	/// at least <see cref="MinimumDistance"/> or <see cref="MinimumInterval"/> has passed since the last one. When full,
	/// the oldest fix is overwritten. Preallocated, so adding fixes never allocates. <see cref="Clear"/> starts a new track.
	/// </summary>
	public sealed class FixRing
	{
		private readonly double[] _north;
		private readonly double[] _east;
		private readonly double[] _time;
		private int _start;

		public FixRing(int capacity, double minimumDistance, double minimumInterval)
		{
			if (capacity <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(capacity));
			}

			if (!(minimumDistance >= 0.0) || !(minimumInterval >= 0.0) || double.IsInfinity(minimumDistance) || double.IsInfinity(minimumInterval))
			{
				throw new ArgumentException("Fix spacing must be finite and zero or more.");
			}

			_north = new double[capacity];
			_east = new double[capacity];
			_time = new double[capacity];
			MinimumDistance = minimumDistance;
			MinimumInterval = minimumInterval;
		}

		/// <summary>Fixes the ring can hold.</summary>
		public int Capacity => _north.Length;

		/// <summary>Fixes held, oldest first.</summary>
		public int Count { get; private set; }

		/// <summary>A new fix is taken once the vessel is this far from the last fix, m.</summary>
		public double MinimumDistance { get; }

		/// <summary>A new fix is taken once this long has passed since the last fix, s.</summary>
		public double MinimumInterval { get; }

		/// <summary>Fixes ever taken since the last <see cref="Clear"/>, including overwritten ones.</summary>
		public long Taken { get; private set; }

		public double North(int index) => _north[Slot(index)];

		public double East(int index) => _east[Slot(index)];

		public double Time(int index) => _time[Slot(index)];

		/// <summary>Forgets every fix.</summary>
		public void Clear()
		{
			Count = 0;
			_start = 0;
			Taken = 0;
		}

		/// <summary>
		/// Takes a fix when it is due: always for the first, then after the distance or the interval. A non-finite
		/// position is ignored. Time going backwards (a new run) does not clear the track by itself; callers clear it on a run change.
		/// </summary>
		public bool TryAdd(double north, double east, double time)
		{
			if (double.IsNaN(north) || double.IsNaN(east) || double.IsNaN(time) || double.IsInfinity(north) || double.IsInfinity(east) || double.IsInfinity(time))
			{
				return false;
			}

			if (Count > 0)
			{
				var last = Slot(Count - 1);
				var dn = north - _north[last];
				var de = east - _east[last];
				var far = dn * dn + de * de >= MinimumDistance * MinimumDistance;
				var late = time - _time[last] >= MinimumInterval;
				if (!(far || late) || (MinimumDistance == 0.0 && MinimumInterval == 0.0 && dn == 0.0 && de == 0.0))
				{
					return false;
				}
			}

			int slot;
			if (Count < Capacity)
			{
				slot = (_start + Count) % Capacity;
				Count++;
			}
			else
			{
				slot = _start;
				_start = (_start + 1) % Capacity;
			}

			_north[slot] = north;
			_east[slot] = east;
			_time[slot] = time;
			Taken++;
			return true;
		}

		private int Slot(int index)
		{
			if (index < 0 || index >= Count)
			{
				throw new ArgumentOutOfRangeException(nameof(index));
			}

			return (_start + index) % Capacity;
		}
	}
}
