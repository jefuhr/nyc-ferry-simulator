using System;
using FerrySim.Core.Traffic;

namespace FerrySim.Core.Navigation
{
	/// <summary>
	/// When a chartplotter target is an alarm: its closest point of approach is under <see cref="Cpa"/> and the time to
	/// it is ahead and within <see cref="Tcpa"/>. A target already past its closest point (negative TCPA), or one that
	/// never comes closer than the threshold, is not an alarm however near it is. The encounter itself comes from the
	/// core's <see cref="Encounter"/> (the same formula the run's monitor uses). The defaults are the task's estimates
	/// for a harbor ferry (0.1 nautical mile, 6 minutes), not published or crew figures; both are settable.
	/// </summary>
	public readonly struct CpaAlarmLimits
	{
		/// <summary>Default CPA threshold, m: 0.1 nautical mile (estimated).</summary>
		public const double DefaultCpa = 185.2;

		/// <summary>Default TCPA limit, s: 6 minutes (estimated).</summary>
		public const double DefaultTcpa = 360.0;

		public CpaAlarmLimits(double cpa, double tcpa)
		{
			if (!(cpa > 0.0) || !(tcpa > 0.0) || double.IsInfinity(cpa) || double.IsInfinity(tcpa))
			{
				throw new ArgumentException("CPA and TCPA alarm limits must be positive and finite.");
			}

			Cpa = cpa;
			Tcpa = tcpa;
		}

		public static CpaAlarmLimits Default => new CpaAlarmLimits(DefaultCpa, DefaultTcpa);

		/// <summary>CPA at or over which a target is not an alarm, m.</summary>
		public double Cpa { get; }

		/// <summary>TCPA beyond which a target is not yet an alarm, s.</summary>
		public double Tcpa { get; }

		/// <summary>True when the CPA is under the threshold and the TCPA is in (0, limit].</summary>
		public bool IsAlarm(in Encounter encounter) =>
			encounter.Cpa < Cpa && encounter.Tcpa > 0.0 && encounter.Tcpa <= Tcpa;
	}

	/// <summary>
	/// The closest few targets for a CPA list, kept sorted while targets are added one by one: the alarms first (CPA under
	/// the threshold with the closest point ahead within the TCPA limit), by CPA with the nearest first; then the other
	/// active targets (a TCPA from 0 to the alarm limit, so the closest point is still ahead or there is no relative
	/// motion), by CPA; then the rest (past their closest point, or too far ahead) by CPA; ties by target index, so the
	/// order is deterministic. An alarm therefore always leads, however many nearer non-alarm targets there are
	/// (ranking policy decided by the coordinator, wave 3c review). Preallocated for a fixed list length and
	/// allocation-free per update.
	/// </summary>
	public sealed class TargetRanking
	{
		private readonly int[] _targets;
		private readonly Encounter[] _encounters;
		private readonly bool[] _alarms;
		private readonly bool[] _active;

		public TargetRanking(int listLength)
		{
			if (listLength <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(listLength));
			}

			_targets = new int[listLength];
			_encounters = new Encounter[listLength];
			_alarms = new bool[listLength];
			_active = new bool[listLength];
		}

		/// <summary>How many rows the list keeps.</summary>
		public int Capacity => _targets.Length;

		/// <summary>Rows filled since <see cref="Begin"/>.</summary>
		public int Count { get; private set; }

		/// <summary>Targets added since <see cref="Begin"/>, listed or not.</summary>
		public int Considered { get; private set; }

		/// <summary>Targets that were alarms since <see cref="Begin"/>, listed or not.</summary>
		public int AlarmCount { get; private set; }

		/// <summary>True when any considered target is an alarm.</summary>
		public bool Alarm => AlarmCount > 0;

		public int Target(int row) => _targets[Check(row)];

		public Encounter Encounter(int row) => _encounters[Check(row)];

		public bool IsAlarm(int row) => _alarms[Check(row)];

		/// <summary>True when the row's closest point is still ahead and within the TCPA limit, alarm or not.</summary>
		public bool IsActive(int row) => _active[Check(row)];

		/// <summary>True when the row's closest point is behind it (a negative TCPA): shown dimmed.</summary>
		public bool IsPast(int row) => _encounters[Check(row)].Tcpa < 0.0;

		/// <summary>Starts a new ranking.</summary>
		public void Begin()
		{
			Count = 0;
			Considered = 0;
			AlarmCount = 0;
		}

		/// <summary>Adds a target's encounter, keeping the list sorted and no longer than its capacity.</summary>
		public void Add(int target, in Encounter encounter, in CpaAlarmLimits limits)
		{
			Considered++;
			var alarm = limits.IsAlarm(encounter);
			var active = encounter.Tcpa >= 0.0 && encounter.Tcpa <= limits.Tcpa;
			if (alarm)
			{
				AlarmCount++;
			}

			// Find where it goes: before the first row it beats.
			var position = Count;
			for (var i = 0; i < Count; i++)
			{
				if (Precedes(alarm, active, encounter, target, _alarms[i], _active[i], _encounters[i], _targets[i]))
				{
					position = i;
					break;
				}
			}

			if (position >= _targets.Length)
			{
				return;
			}

			var last = Math.Min(Count, _targets.Length - 1);
			for (var i = last; i > position; i--)
			{
				_targets[i] = _targets[i - 1];
				_encounters[i] = _encounters[i - 1];
				_alarms[i] = _alarms[i - 1];
				_active[i] = _active[i - 1];
			}

			_targets[position] = target;
			_encounters[position] = encounter;
			_alarms[position] = alarm;
			_active[position] = active;
			if (Count < _targets.Length)
			{
				Count++;
			}
		}

		private static bool Precedes(bool alarmA, bool activeA, in Encounter a, int targetA, bool alarmB, bool activeB, in Encounter b, int targetB)
		{
			if (alarmA != alarmB)
			{
				return alarmA;
			}

			if (activeA != activeB)
			{
				return activeA;
			}

			if (a.Cpa != b.Cpa)
			{
				return a.Cpa < b.Cpa;
			}

			return targetA < targetB;
		}

		private int Check(int row)
		{
			if (row < 0 || row >= Count)
			{
				throw new ArgumentOutOfRangeException(nameof(row));
			}

			return row;
		}
	}
}
