using System;
using System.Collections.Generic;

namespace FerrySim.Core.Navigation
{
	/// <summary>How a depth area is shaded against the safety contour.</summary>
	public enum DepthBand
	{
		/// <summary>Dries or has no charted depth range: shown as foreshore.</summary>
		Drying = 0,

		/// <summary>Shallower than the safety contour: the emphasised side.</summary>
		Unsafe = 1,

		/// <summary>At or deeper than the safety contour but shallower than the deep contour.</summary>
		Caution = 2,

		/// <summary>At or deeper than the deep contour.</summary>
		Deep = 3,
	}

	/// <summary>
	/// The safety contour as an ECDIS chooses it: the chart only carries contours at certain depths, so the contour
	/// drawn for a requested depth is the shallowest charted contour at or deeper than it (the next deeper one), or the
	/// deepest charted contour when none is that deep. Depth areas are banded against it. Depths are chart datum (MLLW here).
	/// </summary>
	public static class SafetyContour
	{
		/// <summary>The task's default safety contour, m (an estimate pending crew input).</summary>
		public const double DefaultMetres = 3.0;

		/// <summary>The contour depth drawn for a requested safety depth, from the sorted distinct charted contour depths; NaN without any.</summary>
		public static double Select(IReadOnlyList<double> chartedContours, double requested)
		{
			if (chartedContours == null || chartedContours.Count == 0 || double.IsNaN(requested))
			{
				return double.NaN;
			}

			var deepest = double.NegativeInfinity;
			var chosen = double.NaN;
			for (var i = 0; i < chartedContours.Count; i++)
			{
				var depth = chartedContours[i];
				if (double.IsNaN(depth))
				{
					continue;
				}

				deepest = Math.Max(deepest, depth);
				if (depth >= requested && (double.IsNaN(chosen) || depth < chosen))
				{
					chosen = depth;
				}
			}

			return double.IsNaN(chosen) ? deepest : chosen;
		}

		/// <summary>The deep contour paired with a safety contour: twice it, at least 3 m deeper, from the charted contours.</summary>
		public static double DeepContour(IReadOnlyList<double> chartedContours, double safety)
		{
			if (double.IsNaN(safety))
			{
				return double.NaN;
			}

			var deep = Select(chartedContours, Math.Max(2.0 * safety, safety + 3.0));
			return double.IsNaN(deep) || deep <= safety ? safety : deep;
		}

		/// <summary>The band of a depth area whose shallowest depth is <paramref name="minimumDepth"/> (DRVAL1; NaN when uncharted).</summary>
		public static DepthBand Classify(double minimumDepth, double safety, double deep)
		{
			if (double.IsNaN(minimumDepth) || minimumDepth < 0.0)
			{
				return DepthBand.Drying;
			}

			if (double.IsNaN(safety))
			{
				return DepthBand.Caution;
			}

			if (minimumDepth < safety)
			{
				return DepthBand.Unsafe;
			}

			return double.IsNaN(deep) || minimumDepth < deep ? DepthBand.Caution : DepthBand.Deep;
		}
	}
}
