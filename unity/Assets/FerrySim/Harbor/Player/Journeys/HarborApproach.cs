using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Slice;

namespace FerrySim.Harbor.Player.Journeys
{
	/// <summary>Sampled approach preflight, before any simulation step. This is not navigation clearance.</summary>
	public static class HarborApproach
	{
		public static string Problem(HarborJourney route, VesselParameters vessel, DataEnvironment water, IReadOnlyList<ContactSegment> segments, out double minimumUnderKeel)
		{
			minimumUnderKeel = double.NaN;
			if (!route.ApproachOnly) return null;
			var depth = SliceScenarioCatalog.MinimumStartDepth(water, route.ApproachCorridor, 0);
			if (double.IsNaN(depth) || double.IsInfinity(depth)) return "Approach corridor has unknown depth.";
			minimumUnderKeel = depth - (vessel.Particulars.MaxDraft ?? vessel.Particulars.Draft).Value;
			if (minimumUnderKeel < 0) return "Approach corridor is shallower than the vessel's maximum draft.";
			var previous = route.StartPosition;
			for (var leg = 0; leg < route.Points.Length; leg++)
			{
				var end = route.Points[leg]; var delta = end - previous;
				var heading = delta.Magnitude > 0 ? Math.Atan2(delta.East, delta.North) : route.StartHeading;
				var count = Math.Max(1, (int)Math.Ceiling(delta.Magnitude / .25));
				for (var sample = 0; sample <= count; sample++)
				{
					var position = previous + delta * ((double)sample / count);
					var poseHeading = leg == 0 && sample == 0 ? route.StartHeading : heading;
					var hull = FaceClearance.WorldOutline(vessel, position.North, position.East, poseHeading);
					foreach (var point in hull)
						if (!route.InApproachCorridor(point)) return "Approach contact hull leaves the declared corridor.";
					var clearance = FaceClearance.Check(vessel, position.North, position.East, poseHeading, segments);
					if (!clearance.IsClear()) return "Approach corridor overlaps or is too close to segment " + clearance.NearestSegmentId + ".";
				}
				previous = end;
			}
			return null;
		}
	}
}
