using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Traffic
{
	/// <summary>The vessel classes of the traffic pack format (ferrysim-traffic/1), from the AIS ship type.</summary>
	public enum TrafficClass
	{
		Other = 0,
		FerryPassenger = 1,
		TugTow = 2,
		Cargo = 3,
		Tanker = 4,
		Pleasure = 5,
		Sailing = 6,
		Fishing = 7,
		LawEnforcement = 8,
		Pilot = 9,
		SearchRescue = 10,
		Military = 11,
	}

	/// <summary>The pack's class names, as <c>vessels.csv</c> writes them.</summary>
	public static class TrafficClasses
	{
		private static readonly string[] Names =
		{
			"other", "ferry-passenger", "tug-tow", "cargo", "tanker", "pleasure", "sailing", "fishing", "law-enforcement", "pilot", "search-rescue", "military",
		};

		/// <summary>Every class, in enum order.</summary>
		public static int Count => Names.Length;

		/// <summary>The pack's name for a class, for example <c>tug-tow</c>.</summary>
		public static string Name(TrafficClass value)
		{
			var index = (int)value;
			return index >= 0 && index < Names.Length ? Names[index] : Names[0];
		}

		/// <summary>The class a pack name stands for; false for a name the format does not have.</summary>
		public static bool TryParse(string text, out TrafficClass value)
		{
			for (var i = 0; i < Names.Length; i++)
			{
				if (string.Equals(Names[i], text, StringComparison.Ordinal))
				{
					value = (TrafficClass)i;
					return true;
				}
			}

			value = TrafficClass.Other;
			return false;
		}
	}

	/// <summary>
	/// One vessel of a traffic pack: a row of <c>vessels.csv</c>. Dimensions are the AIS modes in metres, NaN where AIS gave
	/// none; times are seconds since the pack's epoch.
	/// </summary>
	public sealed class TrafficVessel
	{
		public TrafficVessel(int index, int mmsi, string name, double aisType, TrafficClass vesselClass, double length, double beam, double draft,
			double firstTime, double lastTime, int reports, int segments, bool moving, bool ferryCandidate)
		{
			Index = index;
			Mmsi = mmsi;
			Name = name ?? string.Empty;
			AisType = aisType;
			Class = vesselClass;
			Length = length;
			Beam = beam;
			Draft = draft;
			FirstTime = firstTime;
			LastTime = lastTime;
			Reports = reports;
			Segments = segments;
			Moving = moving;
			FerryCandidate = ferryCandidate;
			Hull = TrafficHullSize.For(vesselClass, length, beam, draft);
		}

		/// <summary>The vessel's index in the pack, 0 to vessels − 1, in MMSI order.</summary>
		public int Index { get; }

		public int Mmsi { get; }

		/// <summary>The AIS name, empty when AIS gave none.</summary>
		public string Name { get; }

		/// <summary>The AIS ship type code, NaN when AIS gave none.</summary>
		public double AisType { get; }

		public TrafficClass Class { get; }

		/// <summary>AIS length overall, m; NaN when AIS gave none.</summary>
		public double Length { get; }

		/// <summary>AIS beam, m; NaN when AIS gave none.</summary>
		public double Beam { get; }

		/// <summary>AIS draught, m; NaN when AIS gave none.</summary>
		public double Draft { get; }

		/// <summary>The first and last report times, s since the pack's epoch.</summary>
		public double FirstTime { get; }

		public double LastTime { get; }

		/// <summary>Kept reports and segments.</summary>
		public int Reports { get; }

		public int Segments { get; }

		/// <summary>False for a vessel whose every report is within 50 m of its median position: it is drawn and checked as static.</summary>
		public bool Moving { get; }

		/// <summary>True for an MMSI of the calibration identity audit's T and K subsets: a ferry of the fleet the simulator trains on.</summary>
		public bool FerryCandidate { get; }

		/// <summary>The hull the simulator gives it: the AIS length and beam, or the class's documented defaults.</summary>
		public TrafficHullSize Hull { get; }

		/// <summary>Index of the vessel's first report in <see cref="TrafficPack.Reports"/>.</summary>
		public int FirstReport { get; internal set; }

		/// <summary>A static vessel's position: the componentwise median of its reports, world (north, east) m.</summary>
		public double StaticNorth { get; internal set; }

		public double StaticEast { get; internal set; }

		/// <summary>A static vessel's heading: the circular mean of its valid AIS headings, rad; NaN when it reported none.</summary>
		public double StaticHeading { get; internal set; } = double.NaN;

		public override string ToString() => $"{Mmsi} {Name} ({TrafficClasses.Name(Class)})";
	}

	/// <summary>
	/// Every kept report of a traffic pack, in <c>reports.bin</c> order (vessel index, then time), as parallel arrays.
	/// Positions are world (north, east) metres; SOG, COG and heading are the pack's float32 values, NaN where AIS gave none.
	/// </summary>
	public sealed class TrafficReports
	{
		internal readonly int[] VesselIndex;
		internal readonly int[] SegmentIndex;
		internal readonly double[] T;
		internal readonly double[] N;
		internal readonly double[] E;
		internal readonly float[] Sog;
		internal readonly float[] Cog;
		internal readonly float[] Hdg;

		/// <summary>Takes the arrays as they are (no copy). Every array has one entry per report.</summary>
		public TrafficReports(int[] vessel, int[] segment, double[] time, double[] north, double[] east, float[] sog, float[] cog, float[] heading)
		{
			VesselIndex = vessel ?? throw new ArgumentNullException(nameof(vessel));
			SegmentIndex = segment ?? throw new ArgumentNullException(nameof(segment));
			T = time ?? throw new ArgumentNullException(nameof(time));
			N = north ?? throw new ArgumentNullException(nameof(north));
			E = east ?? throw new ArgumentNullException(nameof(east));
			Sog = sog ?? throw new ArgumentNullException(nameof(sog));
			Cog = cog ?? throw new ArgumentNullException(nameof(cog));
			Hdg = heading ?? throw new ArgumentNullException(nameof(heading));
			var count = vessel.Length;
			if (segment.Length != count || time.Length != count || north.Length != count || east.Length != count || sog.Length != count || cog.Length != count || heading.Length != count)
			{
				throw new ArgumentException("Every report field needs one value per report.");
			}
		}

		public int Count => T.Length;

		public int Vessel(int report) => VesselIndex[report];

		public int Segment(int report) => SegmentIndex[report];

		/// <summary>Seconds since the pack's epoch.</summary>
		public double Time(int report) => T[report];

		public double North(int report) => N[report];

		public double East(int report) => E[report];

		/// <summary>Speed over ground, m/s; NaN when AIS gave none.</summary>
		public double SpeedOverGround(int report) => Sog[report];

		/// <summary>Course over ground, rad clockwise from true north; NaN when AIS gave none.</summary>
		public double CourseOverGround(int report) => Cog[report];

		/// <summary>True heading, rad clockwise from true north; NaN when AIS gave none.</summary>
		public double Heading(int report) => Hdg[report];
	}

	/// <summary>
	/// One scenario day of harbor traffic (ferrysim-traffic/1, <c>data-pipeline/ferrydata/traffic/README.md</c>): the vessels and
	/// their cleaned, one-a-minute AIS reports. The constructor checks what the format promises and the replay relies on: vessels
	/// indexed in MMSI order, reports sorted by vessel then strictly increasing time, segments numbered from 0 and split exactly at
	/// gaps over <see cref="SegmentGap"/>, each vessel's counts and time bounds, finite times and positions, and SOG, COG and heading
	/// NaN or in range. Anything else throws <see cref="ArgumentException"/>.
	/// </summary>
	public sealed class TrafficPack
	{
		public const string Format = "ferrysim-traffic/1";

		/// <summary>A gap between reports longer than this starts a new segment, s (the pipeline's rule).</summary>
		public const double SegmentGap = 300.0;

		/// <summary>Largest SOG the format keeps, m/s: 102.2 kn, plus the pipeline's float32 rounding allowance.</summary>
		public const double MaximumSpeed = 102.2 * 1852.0 / 3600.0 + 1e-5;

		private readonly TrafficVessel[] _vessels;

		public TrafficPack(string date, DateTimeOffset epochUtc, double coverageFrom, double coverageTo, IReadOnlyList<TrafficVessel> vessels, TrafficReports reports, string digest)
		{
			Date = date ?? throw new ArgumentNullException(nameof(date));
			EpochUtc = epochUtc.ToUniversalTime();
			CoverageFrom = coverageFrom;
			CoverageTo = coverageTo;
			Reports = reports ?? throw new ArgumentNullException(nameof(reports));
			Digest = digest ?? string.Empty;
			if (vessels == null)
			{
				throw new ArgumentNullException(nameof(vessels));
			}

			_vessels = new TrafficVessel[vessels.Count];
			for (var i = 0; i < _vessels.Length; i++)
			{
				var vessel = vessels[i] ?? throw new ArgumentException($"vessel {i} is missing.");
				if (vessel.Index != i)
				{
					throw new ArgumentException($"vessel {vessel.Mmsi} has index {vessel.Index} but is row {i}; indices run 0 to vessels − 1.");
				}

				if (vessel.Mmsi < 1 || vessel.Mmsi > 999999999)
				{
					throw new ArgumentException($"vessel {i} has MMSI {vessel.Mmsi}, outside 1 to 999999999.");
				}

				if (i > 0 && vessel.Mmsi <= _vessels[i - 1].Mmsi)
				{
					throw new ArgumentException($"vessel {i} (MMSI {vessel.Mmsi}) does not follow MMSI {_vessels[i - 1].Mmsi}; vessels are in increasing MMSI order.");
				}

				_vessels[i] = vessel;
			}

			CheckReports();
		}

		/// <summary>The local (America/New_York) date, yyyy-MM-dd.</summary>
		public string Date { get; }

		/// <summary>Local midnight on <see cref="Date"/>, as UTC: report times count from it.</summary>
		public DateTimeOffset EpochUtc { get; }

		/// <summary>The span the sources covered, s since the epoch (<c>time_coverage.covered_*</c>); NaN when none was covered.</summary>
		public double CoverageFrom { get; }

		public double CoverageTo { get; }

		/// <summary>The pack's SHA-256 digest (<c>manifest.json</c>'s <c>digest</c>), 64 lower-case hex characters.</summary>
		public string Digest { get; }

		public IReadOnlyList<TrafficVessel> Vessels => _vessels;

		public TrafficReports Reports { get; }

		/// <summary>The vessel with this MMSI, or null.</summary>
		public TrafficVessel Vessel(int mmsi)
		{
			var low = 0;
			var high = _vessels.Length - 1;
			while (low <= high)
			{
				var middle = (low + high) >> 1;
				var value = _vessels[middle].Mmsi;
				if (value == mmsi)
				{
					return _vessels[middle];
				}

				if (value < mmsi)
				{
					low = middle + 1;
				}
				else
				{
					high = middle - 1;
				}
			}

			return null;
		}

		private void CheckReports()
		{
			var r = Reports;
			var count = r.Count;
			var report = 0;
			for (var v = 0; v < _vessels.Length; v++)
			{
				var vessel = _vessels[v];
				var first = report;
				if (report >= count || r.VesselIndex[report] != v)
				{
					throw new ArgumentException($"vessel {v} (MMSI {vessel.Mmsi}) has no reports where reports.bin's vessel order puts them (report {report}).");
				}

				var segment = 0;
				for (; report < count && r.VesselIndex[report] == v; report++)
				{
					var t = r.T[report];
					if (double.IsNaN(t) || double.IsInfinity(t) || double.IsNaN(r.N[report]) || double.IsInfinity(r.N[report]) || double.IsNaN(r.E[report]) || double.IsInfinity(r.E[report]))
					{
						throw new ArgumentException($"report {report} (vessel {v}) has a non-finite time or position.");
					}

					if (report > first)
					{
						var gap = t - r.T[report - 1];
						if (!(gap > 0.0))
						{
							throw new ArgumentException($"report {report} (vessel {v}) at {t} s does not follow {r.T[report - 1]} s; a vessel's reports are in strictly increasing time.");
						}

						if (gap > SegmentGap)
						{
							segment++;
						}
					}

					if (r.SegmentIndex[report] != segment)
					{
						throw new ArgumentException($"report {report} (vessel {v}) is in segment {r.SegmentIndex[report]}, but the {SegmentGap} s gap rule puts it in segment {segment}.");
					}

					CheckRange(report, "sog_mps", r.Sog[report], MaximumSpeed);
					CheckRange(report, "cog_rad", r.Cog[report], 2.0 * Math.PI);
					CheckRange(report, "heading_rad", r.Hdg[report], 2.0 * Math.PI);
				}

				var reports = report - first;
				if (reports != vessel.Reports || segment + 1 != vessel.Segments)
				{
					throw new ArgumentException($"vessel {v} (MMSI {vessel.Mmsi}) lists {vessel.Reports} reports in {vessel.Segments} segments, but reports.bin has {reports} in {segment + 1}.");
				}

				if (r.T[first] != vessel.FirstTime || r.T[report - 1] != vessel.LastTime)
				{
					throw new ArgumentException($"vessel {v} (MMSI {vessel.Mmsi}) lists reports from {vessel.FirstTime} s to {vessel.LastTime} s, but reports.bin has {r.T[first]} s to {r.T[report - 1]} s.");
				}

				vessel.FirstReport = first;
				if (!vessel.Moving)
				{
					Settle(vessel, first, report);
				}
			}

			if (report != count)
			{
				throw new ArgumentException($"report {report} names vessel {r.VesselIndex[report]}, which is not in vessels.csv's order (vessels 0 to {_vessels.Length - 1}).");
			}
		}

		private static void CheckRange(int report, string field, float value, double limit)
		{
			if (float.IsNaN(value))
			{
				return;
			}

			if (float.IsInfinity(value) || value < 0.0f || value > limit)
			{
				throw new ArgumentException($"report {report} has {field} {value}, outside 0 to {limit}.");
			}
		}

		/// <summary>
		/// A static vessel's single position and heading: the componentwise median of its reports (the mean of the two middle
		/// values for an even count, as pandas takes it) and the circular mean of its valid headings in report order.
		/// </summary>
		private void Settle(TrafficVessel vessel, int first, int end)
		{
			var count = end - first;
			var north = new double[count];
			var east = new double[count];
			Array.Copy(Reports.N, first, north, 0, count);
			Array.Copy(Reports.E, first, east, 0, count);
			Array.Sort(north);
			Array.Sort(east);
			vessel.StaticNorth = Median(north);
			vessel.StaticEast = Median(east);
			double sin = 0.0, cos = 0.0;
			var valid = 0;
			for (var i = first; i < end; i++)
			{
				var heading = Reports.Hdg[i];
				if (!float.IsNaN(heading))
				{
					sin += Math.Sin(heading);
					cos += Math.Cos(heading);
					valid++;
				}
			}

			vessel.StaticHeading = valid > 0 && (sin != 0.0 || cos != 0.0) ? Angles.WrapTwoPi(Math.Atan2(sin, cos)) : double.NaN;
		}

		private static double Median(double[] sorted)
		{
			var middle = sorted.Length / 2;
			return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
		}
	}
}
