using System;
using System.Collections.Generic;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// What a <see cref="DataEnvironment"/> has had to make up so far, for the UI to show: the pack's time window, the
	/// depth samples that fell back to the outside depth, the samples held at the window's edges, and the gaps in the
	/// series. A snapshot; the counts keep growing as the environment is sampled.
	/// </summary>
	public readonly struct EnvironmentCoverage
	{
		public EnvironmentCoverage(DateTimeOffset startUtc, DateTimeOffset endUtc, double startTime, double endTime, double outsideDepth,
			long depthSamples, long depthMisses, long samples, long timeHolds, bool windMissing, IReadOnlyList<string> notes, IReadOnlyList<SeriesGap> gaps)
		{
			StartUtc = startUtc;
			EndUtc = endUtc;
			StartTime = startTime;
			EndTime = endTime;
			OutsideDepth = outsideDepth;
			DepthSamples = depthSamples;
			DepthMisses = depthMisses;
			Samples = samples;
			TimeHolds = timeHolds;
			WindMissing = windMissing;
			Notes = notes;
			Gaps = gaps;
		}

		/// <summary>Start of the pack's time coverage, UTC.</summary>
		public DateTimeOffset StartUtc { get; }

		/// <summary>End of the pack's time coverage, UTC.</summary>
		public DateTimeOffset EndUtc { get; }

		/// <summary>The coverage start in simulation time for this scenario start, s (negative when it is before the start).</summary>
		public double StartTime { get; }

		/// <summary>The coverage end in simulation time for this scenario start, s.</summary>
		public double EndTime { get; }

		/// <summary>The depth returned for a miss, m.</summary>
		public double OutsideDepth { get; }

		/// <summary>Depth samples taken.</summary>
		public long DepthSamples { get; }

		/// <summary>Depth samples outside every grid or on unknown bed, which returned <see cref="OutsideDepth"/>.</summary>
		public long DepthMisses { get; }

		/// <summary>Samples of any kind taken (a simulation step takes the current at the hull's reference point and along it, the wind and the depth).</summary>
		public long Samples { get; }

		/// <summary>Samples whose time fell outside the coverage window and were held at its nearer edge.</summary>
		public long TimeHolds { get; }

		/// <summary>True when the day has no wind station, so the wind is still air throughout.</summary>
		public bool WindMissing { get; }

		/// <summary>The pack manifest's notes, such as the gaps the pipeline filled or left.</summary>
		public IReadOnlyList<string> Notes { get; }

		/// <summary>Where a station's series holds a sample inside the window instead of interpolating.</summary>
		public IReadOnlyList<SeriesGap> Gaps { get; }

		/// <summary>True when anything was made up: a depth miss, a time outside the window, or still air for missing wind.</summary>
		public bool HasMisses => DepthMisses > 0 || TimeHolds > 0 || WindMissing;
	}
}
