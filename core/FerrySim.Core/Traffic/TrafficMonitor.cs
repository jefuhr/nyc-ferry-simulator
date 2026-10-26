using System;
using System.Globalization;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Traffic
{
	/// <summary>
	/// When traffic raises the CPA alarm, and how collisions are counted. Every value is an estimate for a ferry in New York
	/// harbor, pending crew review (README.md, Alarm thresholds, has the reasoning).
	/// </summary>
	public readonly struct TrafficAlarmLimits
	{
		/// <summary>CPA at or under which a closing vessel is a threat, m: about four T-class lengths.</summary>
		public const double DefaultCpa = 100.0;

		/// <summary>The look-ahead, s: a threat's TCPA is at most this.</summary>
		public const double DefaultTcpa = 120.0;

		/// <summary>Slowest relative speed that counts as closing, m/s (about 0.5 kn), so two vessels at rest side by side never alarm.</summary>
		public const double DefaultClosingSpeed = 0.25;

		/// <summary>How long the alarm stays up after the last threatening step, s, so it does not chatter at its edges.</summary>
		public const double DefaultClearHold = 2.0;

		/// <summary>Gaps between overlaps shorter than this are one collision, s.</summary>
		public const double DefaultCollisionHold = 1.0;

		/// <summary>
		/// How far off a vessel may lie at the run's first step and still be alongside, m: its rectangle grown by this on every side
		/// touches the learner's hull. Larger than an AIS position's wander at a berth (a metre or two).
		/// </summary>
		public const double DefaultAlongsideMargin = 5.0;

		public TrafficAlarmLimits(double cpa, double tcpa, double closingSpeed, double clearHold, double collisionHold, double alongsideMargin)
		{
			if (!(cpa > 0.0) || !(tcpa > 0.0) || !(closingSpeed >= 0.0) || !(clearHold >= 0.0) || !(collisionHold >= 0.0) || !(alongsideMargin >= 0.0) ||
				double.IsInfinity(cpa) || double.IsInfinity(tcpa) || double.IsInfinity(closingSpeed) || double.IsInfinity(clearHold) || double.IsInfinity(collisionHold) ||
				double.IsInfinity(alongsideMargin))
			{
				throw new ArgumentException("Traffic alarm limits must be finite, the CPA and TCPA positive and the rest zero or more.");
			}

			Cpa = cpa;
			Tcpa = tcpa;
			ClosingSpeed = closingSpeed;
			ClearHold = clearHold;
			CollisionHold = collisionHold;
			AlongsideMargin = alongsideMargin;
		}

		/// <summary>The estimates above.</summary>
		public static TrafficAlarmLimits Default => new TrafficAlarmLimits(DefaultCpa, DefaultTcpa, DefaultClosingSpeed, DefaultClearHold, DefaultCollisionHold, DefaultAlongsideMargin);

		public double Cpa { get; }

		public double Tcpa { get; }

		public double ClosingSpeed { get; }

		public double ClearHold { get; }

		public double CollisionHold { get; }

		public double AlongsideMargin { get; }

		/// <summary>True when an encounter is a threat: closing at <see cref="ClosingSpeed"/> or more, with a CPA of at most <see cref="Cpa"/> within <see cref="Tcpa"/>.</summary>
		public bool IsThreat(in Encounter encounter) =>
			encounter.Tcpa > 0.0 && encounter.Tcpa <= Tcpa && encounter.Cpa <= Cpa && encounter.RelativeSpeed >= ClosingSpeed;
	}

	public enum TrafficEventKind
	{
		/// <summary>A vessel became a threat: the CPA alarm is up for it.</summary>
		AlarmRaised = 0,

		/// <summary>The alarm for a vessel cleared, <see cref="TrafficAlarmLimits.ClearHold"/> after its last threatening step.</summary>
		AlarmCleared = 1,

		/// <summary>The learner's hull started to overlap a vessel's hull.</summary>
		CollisionStarted = 2,

		/// <summary>A collision ended, <see cref="TrafficAlarmLimits.CollisionHold"/> after its last overlapping step.</summary>
		CollisionEnded = 3,

		/// <summary>
		/// At the run's first step the vessel's rectangle, grown by <see cref="TrafficAlarmLimits.AlongsideMargin"/>, touched the
		/// learner's hull: the run starts alongside it, and it raises no alarm and no collision until they part.
		/// </summary>
		AlongsideStarted = 4,

		/// <summary>The grown rectangle stopped touching for the first time: they parted, for good. It alarms and collides as any other from this step.</summary>
		AlongsideEnded = 5,

		/// <summary>
		/// Not an event: an alarm still up when the recording ended, in the end-of-recording snapshot
		/// (<see cref="TrafficMonitor.ReportOpen"/>), with its least range and CPA so far.
		/// </summary>
		AlarmOpen = 6,

		/// <summary>Not an event: a collision still going on when the recording ended, in the snapshot.</summary>
		CollisionOpen = 7,

		/// <summary>Not an event: still alongside when the recording ended, in the snapshot, with the least range so far.</summary>
		AlongsideOpen = 8,
	}

	/// <summary>
	/// An alarm, a collision or a start alongside starting or ending, at the state after <see cref="Step"/> physics steps. The
	/// values are the encounter at that step (at the last step the vessel was present, for an end after it vanished); an end
	/// carries its episode's first step, its last step with the condition and the least range and CPA over it.
	/// </summary>
	public readonly struct TrafficEvent
	{
		public TrafficEvent(TrafficEventKind kind, long step, long startStep, long lastStep, int vessel, int mmsi, in Encounter encounter, double minimumRange, double minimumCpa)
		{
			Kind = kind;
			Step = step;
			StartStep = startStep;
			LastStep = lastStep;
			Vessel = vessel;
			Mmsi = mmsi;
			Range = encounter.Range;
			Bearing = encounter.Bearing;
			Cpa = encounter.Cpa;
			Tcpa = encounter.Tcpa;
			RelativeSpeed = encounter.RelativeSpeed;
			MinimumRange = minimumRange;
			MinimumCpa = minimumCpa;
		}

		public TrafficEventKind Kind { get; }

		/// <summary>Physics steps since the run started: the event is at the state after them.</summary>
		public long Step { get; }

		/// <summary>The episode's first step (this step for a start).</summary>
		public long StartStep { get; }

		/// <summary>The episode's last step with the condition (this step for a start).</summary>
		public long LastStep { get; }

		/// <summary>The vessel's index in the pack.</summary>
		public int Vessel { get; }

		public int Mmsi { get; }

		public double Time => SimulationClock.TimeAtStep(Step);

		public double Range { get; }

		public double Bearing { get; }

		public double Cpa { get; }

		public double Tcpa { get; }

		public double RelativeSpeed { get; }

		/// <summary>Least range over the episode so far, m.</summary>
		public double MinimumRange { get; }

		/// <summary>Least CPA over the episode so far, m.</summary>
		public double MinimumCpa { get; }
	}

	/// <summary>Receives a run's traffic events as they happen, in order.</summary>
	public interface ITrafficEventSink
	{
		void OnTrafficEvent(in TrafficEvent trafficEvent);
	}

	public enum TrafficAlarmState
	{
		/// <summary>No traffic vessel within <see cref="TrafficMonitor.ReadoutRange"/>.</summary>
		None = 0,

		/// <summary>Traffic about, none of it a threat.</summary>
		Clear = 1,

		/// <summary>At least one vessel is a threat.</summary>
		Alarm = 2,

		/// <summary>The hull overlaps a vessel's hull.</summary>
		Collision = 3,
	}

	/// <summary>
	/// What the wheelhouse shows about traffic at the latest step: the alarm state and its target, the nearest vessel in collision
	/// or under alarm, else the nearest vessel within <see cref="TrafficMonitor.ReadoutRange"/>. Copying it allocates nothing.
	/// </summary>
	public readonly struct TrafficAlarmReadout
	{
		public TrafficAlarmReadout(TrafficAlarmState state, int alarms, int collisions, TrafficVessel target, in Encounter encounter, TrafficAlarmLimits limits)
		{
			State = state;
			Alarms = alarms;
			Collisions = collisions;
			Target = target;
			Range = target != null ? encounter.Range : double.NaN;
			Bearing = target != null ? encounter.Bearing : double.NaN;
			Cpa = target != null ? encounter.Cpa : double.NaN;
			Tcpa = target != null ? encounter.Tcpa : double.NaN;
			Limits = limits;
		}

		public TrafficAlarmState State { get; }

		/// <summary>Vessels under alarm.</summary>
		public int Alarms { get; }

		/// <summary>Vessels in collision.</summary>
		public int Collisions { get; }

		/// <summary>The vessel the readout is about, or null.</summary>
		public TrafficVessel Target { get; }

		public double Range { get; }

		/// <summary>True bearing of the target from the learner, rad.</summary>
		public double Bearing { get; }

		public double Cpa { get; }

		public double Tcpa { get; }

		public TrafficAlarmLimits Limits { get; }
	}

	/// <summary>
	/// What the wheelhouse panel reads about traffic. The simulation bridge implements it; tests use a fake.
	/// </summary>
	public interface ITrafficReadoutSource
	{
		/// <summary>True when the current run has traffic at all, even before its first step: the panel then shows the readout.</summary>
		bool TrafficEnabled { get; }

		/// <summary>True only while the current run has traffic and <see cref="TrafficAlarm"/> describes its most recent physics step.</summary>
		bool HasTraffic { get; }

		TrafficAlarmReadout TrafficAlarm { get; }
	}

	/// <summary>
	/// Watches a run's traffic at every physics step: for each vessel present (not the one left out) its encounter with the
	/// learner, the CPA alarm (<see cref="TrafficAlarmLimits"/>) and collision, the learner's contact outline against the vessel's
	/// rectangle (<see cref="HullOutline"/>). A vessel whose rectangle, grown by <see cref="TrafficAlarmLimits.AlongsideMargin"/>,
	/// touches the hull at the first step is alongside: no alarm and no collision for it until the grown rectangle first stops
	/// touching (seen at a step it is present), then it is like any other. Starts and ends are <see cref="TrafficEvent"/>s, handed
	/// to a sink in order. Traffic never pushes the learner's vessel: a collision is an event only. A digest over every step's
	/// alarm counts and readout target (<see cref="Digest"/>) lets a replay prove it saw the same traffic. Deterministic and
	/// allocation-free per step.
	/// </summary>
	public sealed class TrafficMonitor
	{
		/// <summary>Farthest vessel the readout names when nothing is a threat, m (one nautical mile).</summary>
		public const double ReadoutRange = 1852.0;

		public const string DigestPrefix = "fnv1a64:";

		private const ulong OffsetBasis = 14695981039346656037UL;
		private const ulong Prime = 1099511628211UL;

		private readonly TrafficReplay _replay;
		private readonly HullOutline _outline;
		private readonly double _offset;
		private readonly int _removed;
		private readonly long _clearSteps;
		private readonly long _collisionSteps;
		private readonly bool[] _alarm;
		private readonly long[] _alarmStart;
		private readonly long[] _lastThreat;
		private readonly bool[] _collision;
		private readonly long[] _collisionStart;
		private readonly long[] _lastOverlap;
		private readonly double[] _alarmMinimumRange;
		private readonly double[] _alarmMinimumCpa;
		private readonly double[] _collisionMinimumRange;
		private readonly double[] _collisionMinimumCpa;
		private readonly bool[] _alongside;
		private readonly long[] _alongsideStart;
		private readonly long[] _lastAlongside;
		private readonly double[] _alongsideMinimumRange;
		private readonly double[] _alongsideMinimumCpa;
		private readonly Encounter[] _last;
		private ulong _hash;

		/// <param name="replay">The day's traffic.</param>
		/// <param name="epochOffset">Seconds from the pack's epoch to the run's start: pack time = run time + this.</param>
		/// <param name="removedVessel">The vessel left out (its index in the pack), or −1.</param>
		/// <param name="outline">The learner's hull.</param>
		/// <param name="limits">The alarm's limits.</param>
		public TrafficMonitor(TrafficReplay replay, double epochOffset, int removedVessel, HullOutline outline, TrafficAlarmLimits limits)
		{
			_replay = replay ?? throw new ArgumentNullException(nameof(replay));
			_outline = outline ?? throw new ArgumentNullException(nameof(outline));
			if (double.IsNaN(epochOffset) || double.IsInfinity(epochOffset))
			{
				throw new ArgumentException("The epoch offset must be finite.", nameof(epochOffset));
			}

			_offset = epochOffset;
			_removed = removedVessel;
			Limits = limits;
			_clearSteps = (long)Math.Round(limits.ClearHold * SimulationClock.StepsPerSecond);
			_collisionSteps = (long)Math.Round(limits.CollisionHold * SimulationClock.StepsPerSecond);
			var count = replay.Count;
			_alarm = new bool[count];
			_alarmStart = new long[count];
			_lastThreat = new long[count];
			_collision = new bool[count];
			_collisionStart = new long[count];
			_lastOverlap = new long[count];
			_alarmMinimumRange = new double[count];
			_alarmMinimumCpa = new double[count];
			_collisionMinimumRange = new double[count];
			_collisionMinimumCpa = new double[count];
			_alongside = new bool[count];
			_alongsideStart = new long[count];
			_lastAlongside = new long[count];
			_alongsideMinimumRange = new double[count];
			_alongsideMinimumCpa = new double[count];
			_last = new Encounter[count];
			Reset();
		}

		public TrafficAlarmLimits Limits { get; }

		/// <summary>The latest step's readout.</summary>
		public TrafficAlarmReadout Readout { get; private set; }

		/// <summary>The state after this many steps was the last seen; −1 before the first.</summary>
		public long LastStep { get; private set; }

		/// <summary>Events handed to sinks so far.</summary>
		public long EventCount { get; private set; }

		/// <summary>Alarms raised and collisions started so far.</summary>
		public long AlarmsRaised { get; private set; }

		public long CollisionsStarted { get; private set; }

		/// <summary>Vessels the run started alongside.</summary>
		public long AlongsideAtStart { get; private set; }

		/// <summary>The digest of every step seen so far, for example <c>fnv1a64:9ae16a3b2f90404f</c>.</summary>
		public string Digest => DigestPrefix + _hash.ToString("x16", CultureInfo.InvariantCulture);

		/// <summary>Forgets everything: no alarms, no collisions, no steps seen.</summary>
		public void Reset()
		{
			Array.Clear(_alarm, 0, _alarm.Length);
			Array.Clear(_collision, 0, _collision.Length);
			Array.Clear(_alongside, 0, _alongside.Length);
			for (var i = 0; i < _last.Length; i++)
			{
				_lastThreat[i] = long.MinValue / 2;
				_lastOverlap[i] = long.MinValue / 2;
			}

			_hash = OffsetBasis;
			LastStep = -1;
			EventCount = 0;
			AlarmsRaised = 0;
			CollisionsStarted = 0;
			AlongsideAtStart = 0;
			Readout = new TrafficAlarmReadout(TrafficAlarmState.None, 0, 0, null, default, Limits);
		}

		/// <summary>
		/// Looks at the state after <paramref name="stepCount"/> physics steps (simulation time
		/// <see cref="SimulationClock.TimeAtStep"/>): every vessel's encounter, alarm and collision; at the first step seen since
		/// the monitor started or was reset, which vessels the run starts alongside. Events go to <paramref name="sink"/> (which may
		/// be null) in vessel order.
		/// </summary>
		public void Update(long stepCount, in VesselState state, ITrafficEventSink sink)
		{
			var first = LastStep < 0;
			var margin = 2.0 * Limits.AlongsideMargin;
			var packTime = SimulationClock.TimeAtStep(stepCount) + _offset;
			var own = state.GroundVelocity;
			var ownNorth = state.North;
			var ownEast = state.East;
			var heading = state.Heading;
			var pack = _replay.Pack;
			var alarms = 0;
			var collisions = 0;
			var alarmTarget = -1;
			var collisionTarget = -1;
			var nearest = -1;
			var nearestRange = double.PositiveInfinity;
			var alarmRange = double.PositiveInfinity;
			var collisionRange = double.PositiveInfinity;
			for (var v = 0; v < _alarm.Length; v++)
			{
				if (v == _removed)
				{
					continue;
				}

				var present = _replay.TrySample(v, packTime, out var sample);
				if (present)
				{
					var encounter = Encounter.Between(ownNorth, ownEast, own.North, own.East, sample.North, sample.East, sample.VelocityNorth, sample.VelocityEast);
					_last[v] = encounter;
					if (encounter.Range < nearestRange)
					{
						nearestRange = encounter.Range;
						nearest = v;
					}

					var hull = pack.Vessels[v].Hull;
					if (first || _alongside[v])
					{
						var touching = _outline.Overlaps(ownNorth, ownEast, heading, sample.North, sample.East, sample.Heading, hull.Length + margin, hull.Beam + margin);
						if (first && touching)
						{
							_alongside[v] = true;
							_alongsideStart[v] = stepCount;
							_lastAlongside[v] = stepCount;
							_alongsideMinimumRange[v] = encounter.Range;
							_alongsideMinimumCpa[v] = encounter.Cpa;
							AlongsideAtStart++;
							Emit(sink, new TrafficEvent(TrafficEventKind.AlongsideStarted, stepCount, stepCount, stepCount, v, pack.Vessels[v].Mmsi, encounter, encounter.Range, encounter.Cpa));
						}
						else if (touching)
						{
							_lastAlongside[v] = stepCount;
							_alongsideMinimumRange[v] = Math.Min(_alongsideMinimumRange[v], encounter.Range);
							_alongsideMinimumCpa[v] = Math.Min(_alongsideMinimumCpa[v], encounter.Cpa);
						}
						else if (_alongside[v])
						{
							// Parted, for good: from this step on, alarms and collisions as for any other vessel.
							_alongside[v] = false;
							Emit(sink, new TrafficEvent(TrafficEventKind.AlongsideEnded, stepCount, _alongsideStart[v], _lastAlongside[v], v, pack.Vessels[v].Mmsi, encounter, _alongsideMinimumRange[v], _alongsideMinimumCpa[v]));
						}
					}

					if (!_alongside[v] && Limits.IsThreat(encounter))
					{
						_lastThreat[v] = stepCount;
						if (!_alarm[v])
						{
							_alarm[v] = true;
							_alarmStart[v] = stepCount;
							_alarmMinimumRange[v] = encounter.Range;
							_alarmMinimumCpa[v] = encounter.Cpa;
							AlarmsRaised++;
							Emit(sink, new TrafficEvent(TrafficEventKind.AlarmRaised, stepCount, stepCount, stepCount, v, pack.Vessels[v].Mmsi, encounter, encounter.Range, encounter.Cpa));
						}
					}

					if (_alarm[v])
					{
						_alarmMinimumRange[v] = Math.Min(_alarmMinimumRange[v], encounter.Range);
						_alarmMinimumCpa[v] = Math.Min(_alarmMinimumCpa[v], encounter.Cpa);
					}

					if (!_alongside[v] && _outline.Overlaps(ownNorth, ownEast, heading, sample.North, sample.East, sample.Heading, hull.Length, hull.Beam))
					{
						_lastOverlap[v] = stepCount;
						if (!_collision[v])
						{
							_collision[v] = true;
							_collisionStart[v] = stepCount;
							_collisionMinimumRange[v] = encounter.Range;
							_collisionMinimumCpa[v] = encounter.Cpa;
							CollisionsStarted++;
							Emit(sink, new TrafficEvent(TrafficEventKind.CollisionStarted, stepCount, stepCount, stepCount, v, pack.Vessels[v].Mmsi, encounter, encounter.Range, encounter.Cpa));
						}
					}

					if (_collision[v])
					{
						_collisionMinimumRange[v] = Math.Min(_collisionMinimumRange[v], encounter.Range);
						_collisionMinimumCpa[v] = Math.Min(_collisionMinimumCpa[v], encounter.Cpa);
					}
				}

				if (_alarm[v] && _lastThreat[v] != stepCount && stepCount - _lastThreat[v] >= _clearSteps)
				{
					_alarm[v] = false;
					Emit(sink, new TrafficEvent(TrafficEventKind.AlarmCleared, stepCount, _alarmStart[v], _lastThreat[v], v, pack.Vessels[v].Mmsi, _last[v], _alarmMinimumRange[v], _alarmMinimumCpa[v]));
				}

				if (_collision[v] && _lastOverlap[v] != stepCount && stepCount - _lastOverlap[v] >= _collisionSteps)
				{
					_collision[v] = false;
					Emit(sink, new TrafficEvent(TrafficEventKind.CollisionEnded, stepCount, _collisionStart[v], _lastOverlap[v], v, pack.Vessels[v].Mmsi, _last[v], _collisionMinimumRange[v], _collisionMinimumCpa[v]));
				}

				// The readout's targets are the vessels present now.
				if (present && _collision[v])
				{
					collisions++;
					if (_last[v].Range < collisionRange)
					{
						collisionRange = _last[v].Range;
						collisionTarget = v;
					}
				}

				if (present && _alarm[v])
				{
					alarms++;
					if (_last[v].Range < alarmRange)
					{
						alarmRange = _last[v].Range;
						alarmTarget = v;
					}
				}
			}

			int target;
			TrafficAlarmState readoutState;
			if (collisionTarget >= 0)
			{
				target = collisionTarget;
				readoutState = TrafficAlarmState.Collision;
			}
			else if (alarmTarget >= 0)
			{
				target = alarmTarget;
				readoutState = TrafficAlarmState.Alarm;
			}
			else if (nearest >= 0 && nearestRange <= ReadoutRange)
			{
				target = nearest;
				readoutState = TrafficAlarmState.Clear;
			}
			else
			{
				target = -1;
				readoutState = TrafficAlarmState.None;
			}

			Readout = target >= 0
				? new TrafficAlarmReadout(readoutState, alarms, collisions, pack.Vessels[target], _last[target], Limits)
				: new TrafficAlarmReadout(readoutState, alarms, collisions, null, default, Limits);
			LastStep = stepCount;
			Mix(stepCount);
			Mix(alarms);
			Mix(collisions);
			Mix(target >= 0 ? pack.Vessels[target].Mmsi : 0);
			Mix(BitConverter.DoubleToInt64Bits(Readout.Range));
			Mix(BitConverter.DoubleToInt64Bits(Readout.Cpa));
			Mix(BitConverter.DoubleToInt64Bits(Readout.Tcpa));
		}

		/// <summary>True while the vessel's alarm is up.</summary>
		public bool IsAlarmed(int vessel) => vessel >= 0 && vessel < _alarm.Length && _alarm[vessel];

		/// <summary>True while the vessel is in a collision episode.</summary>
		public bool IsColliding(int vessel) => vessel >= 0 && vessel < _collision.Length && _collision[vessel];

		/// <summary>
		/// Hands <paramref name="sink"/> (which may be null) a snapshot of every episode still open after the last step seen, in
		/// vessel order, alarm before collision before alongside (<see cref="TrafficEventKind.AlarmOpen"/>,
		/// <see cref="TrafficEventKind.CollisionOpen"/>, <see cref="TrafficEventKind.AlongsideOpen"/>), each at
		/// <see cref="LastStep"/> with its first step, its last step with the condition, the encounter at the last step the vessel
		/// was present and its least range and CPA so far. A snapshot, not an end: nothing changes, and it is neither counted as an
		/// event nor mixed into the digest. Returns how many it handed over.
		/// </summary>
		public int ReportOpen(ITrafficEventSink sink)
		{
			var pack = _replay.Pack;
			var count = 0;
			for (var v = 0; v < _alarm.Length; v++)
			{
				if (_alarm[v])
				{
					count++;
					sink?.OnTrafficEvent(new TrafficEvent(TrafficEventKind.AlarmOpen, LastStep, _alarmStart[v], _lastThreat[v], v, pack.Vessels[v].Mmsi, _last[v], _alarmMinimumRange[v], _alarmMinimumCpa[v]));
				}

				if (_collision[v])
				{
					count++;
					sink?.OnTrafficEvent(new TrafficEvent(TrafficEventKind.CollisionOpen, LastStep, _collisionStart[v], _lastOverlap[v], v, pack.Vessels[v].Mmsi, _last[v], _collisionMinimumRange[v], _collisionMinimumCpa[v]));
				}

				if (_alongside[v])
				{
					count++;
					sink?.OnTrafficEvent(new TrafficEvent(TrafficEventKind.AlongsideOpen, LastStep, _alongsideStart[v], _lastAlongside[v], v, pack.Vessels[v].Mmsi, _last[v], _alongsideMinimumRange[v], _alongsideMinimumCpa[v]));
				}
			}

			return count;
		}

		/// <summary>True while the run is still alongside the vessel it started beside.</summary>
		public bool IsAlongside(int vessel) => vessel >= 0 && vessel < _alongside.Length && _alongside[vessel];

		private void Emit(ITrafficEventSink sink, in TrafficEvent trafficEvent)
		{
			EventCount++;
			Mix((long)trafficEvent.Kind);
			Mix(trafficEvent.Mmsi);
			sink?.OnTrafficEvent(trafficEvent);
		}

		private void Mix(long value)
		{
			var bits = unchecked((ulong)value);
			for (var shift = 0; shift < 64; shift += 8)
			{
				_hash ^= (bits >> shift) & 0xFFUL;
				_hash = unchecked(_hash * Prime);
			}
		}
	}
}
