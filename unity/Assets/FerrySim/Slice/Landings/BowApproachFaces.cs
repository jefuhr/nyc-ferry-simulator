using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Slice
{
	/// <summary>Render-only bow-look faces, selected from the same retained contacts used by the run.</summary>
	public static class BowApproachFaces
	{
		public static SegmentListGeometry From(LandingGeometry landings)
		{
			var faces = new List<ContactSegment>();
			if (landings != null)
				foreach (var segment in landings.Segments)
					if ((segment.Kind == "berth-face" || segment.IsPrimary) && segment.StartFaceEligible)
						faces.Add(segment.Segment);
			return new SegmentListGeometry(faces);
		}
	}
}
