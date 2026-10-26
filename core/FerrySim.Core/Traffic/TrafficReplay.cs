using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Traffic
{
	/// <summary>How a <see cref="TrafficSample"/>'s position was made.</summary>
	public enum TrafficMotion
	{
		/// <summary>A static vessel (the pack's <c>moving</c> false): its median position, at rest.</summary>
		Static = 0,

		/// <summary>The only report of a one-report segment, at exactly its time.</summary>
		Report = 1,

		/// <summary>The cubic Hermite curve between two reports, with end velocities from SOG and COG.</summary>
		Hermite = 2,

		/// <summary>The straight line between two reports, at constant speed.</summary>
		Linear = 3,
	}

	/// <summary>Where a <see cref="TrafficSample"/>'s heading came from.</summary>
	public enum HeadingSource
	{
		/// <summary>The AIS heading: interpolated between two valid headings, or a static vessel's mean heading.</summary>
		Ais = 0,

		/// <summary>The direction of the curve's velocity (its tangent).</summary>
		Tangent = 1,

		/// <summary>The one valid AIS heading of the two reports: slow, or too slow for a tangent.</summary>
		AisHeld = 2,

		/// <summary>Too slow for a tangent, no AIS heading, but not slow: the direction from one report to the next.</summary>
		Chord = 3,

		/// <summary>Nothing to go by: the heading reads 0 (north).</summary>
		Unknown = 4,

		/// <summary>Slow with no AIS heading: the last valid AIS heading before the stretch, held through it.</summary>
		LastAis = 5,

		/// <summary>Slow with no AIS heading and none before: the median of the segment's COGs reported at speed, held through the stretch.</summary>
		MedianCog = 6,
	}

	/// <summary>A traffic vessel at one pack time: position, velocity over the ground and heading, SI and world axes.</summary>
	public readonly struct TrafficSample
	{
		public TrafficSample(int vessel, int segment, int report, TrafficMotion motion, double north, double east, double velocityNorth, double velocityEast, double heading, HeadingSource headingSource)
		{
			Vessel = vessel;
			Segment = segment;
			Report = report;
			Motion = motion;
			North = north;
			East = east;
			VelocityNorth = velocityNorth;
			VelocityEast = velocityEast;
			Heading = heading;
			HeadingSource = headingSource;
		}

		/// <summary>The vessel's index in the pack.</summary>
		public int Vessel { get; }

		/// <summary>The segment the vessel is in.</summary>
		public int Segment { get; }

		/// <summary>The report the sample starts from: the interval's first report, or the one report.</summary>
		public int Report { get; }

		public TrafficMotion Motion { get; }

		public double North { get; }

		public double East { get; }

		/// <summary>Velocity over the ground, m/s, north and east.</summary>
		public double VelocityNorth { get; }

		public double VelocityEast { get; }

		/// <summary>True heading, rad clockwise from north, in [0, 2π); 0 when <see cref="HeadingSource"/> is Unknown.</summary>
		public double Heading { get; }

		public HeadingSource HeadingSource { get; }

		public HorizontalVector Position => new HorizontalVector(North, East);

		public HorizontalVector Velocity => new HorizontalVector(VelocityNorth, VelocityEast);

		/// <summary>Speed over the ground, m/s.</summary>
		public double SpeedOverGround => Math.Sqrt(VelocityNorth * VelocityNorth + VelocityEast * VelocityEast);
	}

	/// <summary>
	/// Where a pack's vessels are at any pack time (s since the pack's epoch), by the format's rule
	/// (README.md, The replay):
	/// <list type="bullet">
	/// <item>A vessel exists only inside its segments: from a segment's first report to its last, both included. Between
	/// segments, before its first report and after its last it is absent. A one-report segment exists only at its report's time.</item>
	/// <item>Between two reports of a segment its position is the cubic Hermite curve through both, with end velocities from SOG
	/// and COG, when both ends have a valid SOG and COG and each is consistent with the step: dead reckoning over the step with
	/// that end's velocity misses the other report by no more than the step's length plus <see cref="ConsistencySlack"/>.
	/// Otherwise it is the straight line, at constant speed. The velocity is the curve's derivative.</item>
	/// <item>At a report's own time the interval starting there is used (the one ending there for a segment's last report).</item>
	/// <item>Heading: the AIS heading, interpolated the short way, when both reports have one. An interval is slow when both reports'
	/// SOGs (where given) and its own speed from report to report are under <see cref="SlowSpeed"/>; there motion is GPS jitter
	/// and gives no heading: one valid AIS heading is held, and with none the interval takes the heading its stretch holds (a run
	/// of slow intervals without AIS headings): the last valid AIS heading before the stretch in the segment, else the median of
	/// the segment's COGs reported at or above the slow limit, else unknown (0, north). Otherwise the curve's tangent; below
	/// <see cref="TangentMinimumSpeed"/> the one valid AIS heading, else the direction from report to report when they are at
	/// least <see cref="ChordMinimumLength"/> apart, else unknown. A one-report segment without an AIS heading takes its velocity's
	/// direction only at or above the slow limit.</item>
	/// <item>A static vessel (<c>moving</c> false) sits at its median position at rest, heading its mean AIS heading (unknown
	/// without one), and exists inside its segments like any other.</item>
	/// </list>
	/// Queries are pure functions of the vessel and the time, deterministic and allocation-free.
	/// </summary>
	public sealed class TrafficReplay
	{
		/// <summary>The dead-reckoning slack of the consistency rule, m: the pipeline's position-jump slack.</summary>
		public const double ConsistencySlack = 30.0;

		/// <summary>Slowest speed with a usable tangent heading, m/s (about 0.2 kn).</summary>
		public const double TangentMinimumSpeed = 0.1;

		/// <summary>Shortest report-to-report distance with a usable chord heading, m.</summary>
		public const double ChordMinimumLength = 1.0;

		/// <summary>How much <see cref="MayEnter"/> widens the area it is given, m: far more than a sample's rounding, far less than a hull.</summary>
		private const double BoundSlack = 1e-6;

		/// <summary>
		/// Under this speed a vessel without an AIS heading takes no heading from its motion, m/s (about 1 kn): a moored or drifting
		/// vessel's positions wander a metre or two, so their direction is noise. An estimate, pending crew review.
		/// </summary>
		public const double SlowSpeed = 0.5;

		private readonly TrafficPack _pack;
		private readonly TrafficReports _reports;
		private readonly int[] _first;
		private readonly int[] _end;

		// Worked out once, so a query does no trigonometry for them: each report's AIS velocity (NaN when its SOG or COG is), and
		// whether the interval from each report to the next of its segment is the Hermite curve, is slow, and, in a slow stretch
		// without AIS headings, the heading it holds and where that came from.
		private readonly double[] _vn;
		private readonly double[] _ve;
		private readonly bool[] _hermite;
		private readonly bool[] _slow;
		private readonly double[] _held;
		private readonly byte[] _heldSource;

		public TrafficReplay(TrafficPack pack)
		{
			_pack = pack ?? throw new ArgumentNullException(nameof(pack));
			_reports = pack.Reports;
			var vessels = pack.Vessels;
			_first = new int[vessels.Count];
			_end = new int[vessels.Count];
			for (var v = 0; v < vessels.Count; v++)
			{
				_first[v] = vessels[v].FirstReport;
				_end[v] = vessels[v].FirstReport + vessels[v].Reports;
			}

			var r = _reports;
			var count = r.Count;
			_vn = new double[count];
			_ve = new double[count];
			_hermite = new bool[count];
			for (var i = 0; i < count; i++)
			{
				double sog = r.Sog[i];
				double cog = r.Cog[i];
				var valid = !double.IsNaN(sog) && !double.IsNaN(cog);
				_vn[i] = valid ? sog * Math.Cos(cog) : double.NaN;
				_ve[i] = valid ? sog * Math.Sin(cog) : double.NaN;
			}

			_slow = new bool[count];
			for (var i = 0; i + 1 < count; i++)
			{
				if (r.VesselIndex[i + 1] == r.VesselIndex[i] && r.SegmentIndex[i + 1] == r.SegmentIndex[i])
				{
					var dt = r.T[i + 1] - r.T[i];
					var dn = r.N[i + 1] - r.N[i];
					var de = r.E[i + 1] - r.E[i];
					_hermite[i] = Consistent(i, dt, dn, de) & Consistent(i + 1, dt, dn, de);
					_slow[i] = Slow(i, dt, dn, de);
				}
			}

			_held = new double[count];
			_heldSource = new byte[count];
			var longest = 0;
			for (var v = 0; v < vessels.Count; v++)
			{
				longest = Math.Max(longest, vessels[v].Reports);
			}

			var scratch = new double[longest];
			for (var start = 0; start < count;)
			{
				var end = start + 1;
				while (end < count && r.VesselIndex[end] == r.VesselIndex[start] && r.SegmentIndex[end] == r.SegmentIndex[start])
				{
					end++;
				}

				HoldStretches(start, end, scratch);
				start = end;
			}
		}

		public TrafficPack Pack => _pack;

		/// <summary>Vessels in the pack.</summary>
		public int Count => _first.Length;

		/// <summary>
		/// False only when the vessel provably never comes inside the area (north and east bounds in metres, edges included) while
		/// it exists, for the motion the replay actually uses: a static vessel's one position; for a moving vessel every report,
		/// the straight line of every interval the replay moves on straight, and for every Hermite interval the box around its
		/// Bézier control points (p₀, p₀ + v₀ dt/3, p₁ − v₁ dt/3, p₁), which the curve never leaves. True does not promise that
		/// it enters. Allocation-free.
		/// </summary>
		public bool MayEnter(int vessel, double minNorth, double maxNorth, double minEast, double maxEast)
		{
			if (vessel < 0 || vessel >= _first.Length)
			{
				return false;
			}

			// A hair of slack, so that rounding in a sample's arithmetic cannot put it just inside an edge the bound kept it out of.
			minNorth -= BoundSlack;
			maxNorth += BoundSlack;
			minEast -= BoundSlack;
			maxEast += BoundSlack;
			var info = _pack.Vessels[vessel];
			if (!info.Moving)
			{
				return Inside(info.StaticNorth, info.StaticEast, minNorth, maxNorth, minEast, maxEast);
			}

			var r = _reports;
			var end = _end[vessel];
			for (var i = _first[vessel]; i < end; i++)
			{
				var n0 = r.N[i];
				var e0 = r.E[i];
				if (Inside(n0, e0, minNorth, maxNorth, minEast, maxEast))
				{
					return true;
				}

				if (i + 1 >= end || r.SegmentIndex[i + 1] != r.SegmentIndex[i])
				{
					continue;
				}

				var n1 = r.N[i + 1];
				var e1 = r.E[i + 1];
				if (_hermite[i])
				{
					var third = (r.T[i + 1] - r.T[i]) / 3.0;
					var c1n = n0 + third * _vn[i];
					var c1e = e0 + third * _ve[i];
					var c2n = n1 - third * _vn[i + 1];
					var c2e = e1 - third * _ve[i + 1];
					if (Math.Min(Math.Min(n0, n1), Math.Min(c1n, c2n)) <= maxNorth && Math.Max(Math.Max(n0, n1), Math.Max(c1n, c2n)) >= minNorth &&
						Math.Min(Math.Min(e0, e1), Math.Min(c1e, c2e)) <= maxEast && Math.Max(Math.Max(e0, e1), Math.Max(c1e, c2e)) >= minEast)
					{
						return true;
					}
				}
				else if (Crosses(n0, e0, n1, e1, minNorth, maxNorth, minEast, maxEast))
				{
					return true;
				}
			}

			return false;
		}

		private static bool Inside(double north, double east, double minNorth, double maxNorth, double minEast, double maxEast) =>
			north >= minNorth && north <= maxNorth && east >= minEast && east <= maxEast;

		/// <summary>Whether the straight line between two points meets the area (Liang–Barsky).</summary>
		private static bool Crosses(double n0, double e0, double n1, double e1, double minNorth, double maxNorth, double minEast, double maxEast)
		{
			double low = 0.0, high = 1.0;
			return Clip(-(n1 - n0), n0 - minNorth, ref low, ref high) && Clip(n1 - n0, maxNorth - n0, ref low, ref high) &&
				Clip(-(e1 - e0), e0 - minEast, ref low, ref high) && Clip(e1 - e0, maxEast - e0, ref low, ref high);
		}

		private static bool Clip(double p, double q, ref double low, ref double high)
		{
			if (p == 0.0)
			{
				return q >= 0.0;
			}

			var t = q / p;
			if (p < 0.0)
			{
				if (t > high)
				{
					return false;
				}

				low = Math.Max(low, t);
			}
			else
			{
				if (t < low)
				{
					return false;
				}

				high = Math.Min(high, t);
			}

			return true;
		}

		/// <summary>True when the vessel exists at <paramref name="packTime"/> (inside one of its segments).</summary>
		public bool IsPresent(int vessel, double packTime) => Locate(vessel, packTime, out _, out _);

		/// <summary>The vessel at <paramref name="packTime"/>; false, with a default sample, when it is absent then.</summary>
		public bool TrySample(int vessel, double packTime, out TrafficSample sample)
		{
			if (!Locate(vessel, packTime, out var report, out var next))
			{
				sample = default;
				return false;
			}

			var r = _reports;
			var info = _pack.Vessels[vessel];
			if (!info.Moving)
			{
				var heading = info.StaticHeading;
				sample = double.IsNaN(heading)
					? new TrafficSample(vessel, r.SegmentIndex[report], report, TrafficMotion.Static, info.StaticNorth, info.StaticEast, 0.0, 0.0, 0.0, HeadingSource.Unknown)
					: new TrafficSample(vessel, r.SegmentIndex[report], report, TrafficMotion.Static, info.StaticNorth, info.StaticEast, 0.0, 0.0, heading, HeadingSource.Ais);
				return true;
			}

			sample = next < 0 ? AtReport(vessel, report) : Between(vessel, report, next, packTime);
			return true;
		}

		/// <summary>
		/// The report at or before <paramref name="packTime"/> and the next report of its segment that the interval runs to (−1
		/// for a one-report segment at its time). False when the vessel is absent.
		/// </summary>
		private bool Locate(int vessel, double packTime, out int report, out int next)
		{
			report = -1;
			next = -1;
			if (vessel < 0 || vessel >= _first.Length || double.IsNaN(packTime))
			{
				return false;
			}

			var t = _reports.T;
			var low = _first[vessel];
			var high = _end[vessel] - 1;
			if (packTime < t[low] || packTime > t[high])
			{
				return false;
			}

			// The last report at or before the time.
			while (low < high)
			{
				var middle = (low + high + 1) >> 1;
				if (t[middle] <= packTime)
				{
					low = middle;
				}
				else
				{
					high = middle - 1;
				}
			}

			var segment = _reports.SegmentIndex;
			var end = _end[vessel];
			if (low + 1 < end && segment[low + 1] == segment[low])
			{
				report = low;
				next = low + 1;
				return true;
			}

			if (t[low] != packTime)
			{
				// After a segment's last report, before the next segment's first: absent.
				return false;
			}

			if (low > _first[vessel] && segment[low - 1] == segment[low])
			{
				// A segment's last report, at its own time: the interval ending there.
				report = low - 1;
				next = low;
				return true;
			}

			report = low;
			return true;
		}

		private TrafficSample AtReport(int vessel, int report)
		{
			var r = _reports;
			var vn = double.IsNaN(_vn[report]) ? 0.0 : _vn[report];
			var ve = double.IsNaN(_ve[report]) ? 0.0 : _ve[report];

			double heading = r.Hdg[report];
			if (!double.IsNaN(heading))
			{
				return new TrafficSample(vessel, r.SegmentIndex[report], report, TrafficMotion.Report, r.N[report], r.E[report], vn, ve, heading, HeadingSource.Ais);
			}

			double sog = r.Sog[report];
			return sog >= SlowSpeed && Math.Sqrt(vn * vn + ve * ve) >= TangentMinimumSpeed
				? new TrafficSample(vessel, r.SegmentIndex[report], report, TrafficMotion.Report, r.N[report], r.E[report], vn, ve, Angles.WrapTwoPi(Math.Atan2(ve, vn)), HeadingSource.Tangent)
				: new TrafficSample(vessel, r.SegmentIndex[report], report, TrafficMotion.Report, r.N[report], r.E[report], vn, ve, 0.0, HeadingSource.Unknown);
		}

		private TrafficSample Between(int vessel, int i, int j, double packTime)
		{
			var r = _reports;
			var t0 = r.T[i];
			var dt = r.T[j] - t0;
			var s = (packTime - t0) / dt;
			var n0 = r.N[i];
			var e0 = r.E[i];
			var n1 = r.N[j];
			var e1 = r.E[j];
			var dn = n1 - n0;
			var de = e1 - e0;

			var hermite = _hermite[i];
			double north, east, vn, ve;
			if (hermite)
			{
				var m0n = _vn[i];
				var m0e = _ve[i];
				var m1n = _vn[j];
				var m1e = _ve[j];
				var s2 = s * s;
				var s3 = s2 * s;
				var h00 = 2.0 * s3 - 3.0 * s2 + 1.0;
				var h10 = s3 - 2.0 * s2 + s;
				var h01 = -2.0 * s3 + 3.0 * s2;
				var h11 = s3 - s2;
				north = h00 * n0 + h10 * dt * m0n + h01 * n1 + h11 * dt * m1n;
				east = h00 * e0 + h10 * dt * m0e + h01 * e1 + h11 * dt * m1e;
				var d00 = 6.0 * s2 - 6.0 * s;
				var d10 = 3.0 * s2 - 4.0 * s + 1.0;
				var d01 = -6.0 * s2 + 6.0 * s;
				var d11 = 3.0 * s2 - 2.0 * s;
				vn = (d00 * n0 + d01 * n1) / dt + d10 * m0n + d11 * m1n;
				ve = (d00 * e0 + d01 * e1) / dt + d10 * m0e + d11 * m1e;
			}
			else
			{
				// Written so that a report's own time gives its position exactly, at either end.
				north = (1.0 - s) * n0 + s * n1;
				east = (1.0 - s) * e0 + s * e1;
				vn = dn / dt;
				ve = de / dt;
			}

			var motion = hermite ? TrafficMotion.Hermite : TrafficMotion.Linear;
			var segment = r.SegmentIndex[i];
			double h0 = r.Hdg[i];
			double h1 = r.Hdg[j];
			if (!double.IsNaN(h0) && !double.IsNaN(h1))
			{
				var heading = Angles.WrapTwoPi(h0 + s * Angles.WrapPi(h1 - h0));
				return new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, heading, HeadingSource.Ais);
			}

			if (_slow[i])
			{
				// Too slow for motion to mean anything: one valid AIS heading is held, else the stretch's.
				return !double.IsNaN(h0) || !double.IsNaN(h1)
					? new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, double.IsNaN(h0) ? h1 : h0, HeadingSource.AisHeld)
					: new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, _held[i], (HeadingSource)_heldSource[i]);
			}

			if (Math.Sqrt(vn * vn + ve * ve) >= TangentMinimumSpeed)
			{
				return new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, Angles.WrapTwoPi(Math.Atan2(ve, vn)), HeadingSource.Tangent);
			}

			if (!double.IsNaN(h0) || !double.IsNaN(h1))
			{
				return new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, double.IsNaN(h0) ? h1 : h0, HeadingSource.AisHeld);
			}

			if (Math.Sqrt(dn * dn + de * de) >= ChordMinimumLength)
			{
				return new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, Angles.WrapTwoPi(Math.Atan2(de, dn)), HeadingSource.Chord);
			}

			return new TrafficSample(vessel, segment, i, motion, north, east, vn, ve, 0.0, HeadingSource.Unknown);
		}

		/// <summary>
		/// Whether the interval from report <paramref name="report"/> over (<paramref name="dn"/>, <paramref name="de"/>) metres in
		/// <paramref name="dt"/> seconds is slow: its own speed and both reports' SOGs (where given) under <see cref="SlowSpeed"/>.
		/// </summary>
		private bool Slow(int report, double dt, double dn, double de)
		{
			if (Math.Sqrt(dn * dn + de * de) / dt >= SlowSpeed)
			{
				return false;
			}

			double sog0 = _reports.Sog[report];
			double sog1 = _reports.Sog[report + 1];
			return (double.IsNaN(sog0) || sog0 < SlowSpeed) && (double.IsNaN(sog1) || sog1 < SlowSpeed);
		}

		/// <summary>
		/// The headings the slow stretches without AIS headings of one segment (reports <paramref name="start"/> to
		/// <paramref name="end"/> − 1) hold: the last valid AIS heading before each, else the segment's median COG at speed, else
		/// north.
		/// </summary>
		private void HoldStretches(int start, int end, double[] scratch)
		{
			var r = _reports;
			var lastHeading = double.NaN;
			var median = double.NaN;
			var medianDone = false;
			for (var i = start; i + 1 < end;)
			{
				double here = r.Hdg[i];
				if (!double.IsNaN(here))
				{
					lastHeading = here;
				}

				if (!InStretch(i))
				{
					i++;
					continue;
				}

				double heading;
				HeadingSource source;
				if (!double.IsNaN(lastHeading))
				{
					heading = lastHeading;
					source = HeadingSource.LastAis;
				}
				else
				{
					if (!medianDone)
					{
						median = MedianCogAtSpeed(start, end, scratch);
						medianDone = true;
					}

					heading = double.IsNaN(median) ? 0.0 : median;
					source = double.IsNaN(median) ? HeadingSource.Unknown : HeadingSource.MedianCog;
				}

				for (; i + 1 < end && InStretch(i); i++)
				{
					_held[i] = heading;
					_heldSource[i] = (byte)source;
				}
			}
		}

		/// <summary>Whether the interval from report <paramref name="i"/> is slow with no AIS heading at either end.</summary>
		private bool InStretch(int i) => _slow[i] && float.IsNaN(_reports.Hdg[i]) && float.IsNaN(_reports.Hdg[i + 1]);

		/// <summary>
		/// The median of the COGs reported at or above <see cref="SlowSpeed"/> among reports <paramref name="start"/> to
		/// <paramref name="end"/> − 1, on the circle (cut at the widest gap between neighbours, the first of equal widest counting
		/// the gap through north last; the middle one, or the mean of the two middle ones); NaN when there are none.
		/// </summary>
		private double MedianCogAtSpeed(int start, int end, double[] scratch)
		{
			var r = _reports;
			var n = 0;
			for (var k = start; k < end; k++)
			{
				double sog = r.Sog[k];
				double cog = r.Cog[k];
				if (sog >= SlowSpeed && !double.IsNaN(cog))
				{
					scratch[n++] = Angles.WrapTwoPi(cog);
				}
			}

			if (n == 0)
			{
				return double.NaN;
			}

			Array.Sort(scratch, 0, n);
			var cut = 0;
			var widest = -1.0;
			for (var k = 0; k < n; k++)
			{
				var gap = k + 1 < n ? scratch[k + 1] - scratch[k] : scratch[0] + Angles.TwoPi - scratch[n - 1];
				if (gap > widest)
				{
					widest = gap;
					cut = k;
				}
			}

			// Laid out from the cut: scratch[cut + 1 .. n − 1], then scratch[0 .. cut] a turn on.
			var before = n - 1 - cut;
			double At(int index) => index < before ? scratch[cut + 1 + index] : scratch[index - before] + Angles.TwoPi;
			var middle = n % 2 == 1 ? At(n / 2) : 0.5 * (At(n / 2 - 1) + At(n / 2));
			return Angles.WrapTwoPi(middle);
		}

		/// <summary>
		/// Whether the report's AIS velocity is valid (finite SOG and COG) and consistent with a step of (<paramref name="dn"/>,
		/// <paramref name="de"/>) metres over <paramref name="dt"/> seconds: dead reckoning with it over the step misses the other
		/// end by no more than the step's length plus <see cref="ConsistencySlack"/>.
		/// </summary>
		private bool Consistent(int report, double dt, double dn, double de)
		{
			var vn = _vn[report];
			var ve = _ve[report];
			if (double.IsNaN(vn))
			{
				return false;
			}

			var missN = vn * dt - dn;
			var missE = ve * dt - de;
			return Math.Sqrt(missN * missN + missE * missE) <= Math.Sqrt(dn * dn + de * de) + ConsistencySlack;
		}
	}
}
