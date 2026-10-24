using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;

namespace FerrySim.Slice
{
	/// <summary>Scenario starts for coming alongside a landing face bow-on, clear of every face.</summary>
	public static class LandingApproach
	{
		/// <summary>
		/// A scenario with the bow <paramref name="bowClearance"/> metres off the middle of <paramref name="face"/> on
		/// its water side, heading straight in (against the face's water normal). The start goes through
		/// <see cref="LocalTangentPlane.World"/>, like every scenario position.
		/// </summary>
		public static ScenarioSettings BowOn(LandingSegment face, VesselParameters vessel, double bowClearance, string name, string vesselFile, string scenarioStartUtc,
			double startSpeedKnots = 0.0, ScenarioEnvironment environment = null)
		{
			if (face == null)
			{
				throw new ArgumentNullException(nameof(face));
			}

			if (!(bowClearance > 0.0))
			{
				throw new ArgumentOutOfRangeException(nameof(bowClearance), "The bow must start clear of the face.");
			}

			var bow = double.NegativeInfinity;
			foreach (var point in FaceClearance.BodyOutline(vessel))
			{
				bow = Math.Max(bow, point.North);
			}

			var segment = face.Segment;
			var normal = segment.WaterNormal;
			var offset = bow + bowClearance;
			var north = 0.5 * (segment.Start.North + segment.End.North) + normal.North * offset;
			var east = 0.5 * (segment.Start.East + segment.End.East) + normal.East * offset;
			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return new ScenarioSettings
			{
				name = name,
				vesselFile = vesselFile,
				startLatitude = position.LatitudeDegrees,
				startLongitude = position.LongitudeDegrees,
				startHeadingDegrees = Angles.ToDegrees(Angles.WrapTwoPi(Math.Atan2(-normal.East, -normal.North))),
				startSpeedKnots = startSpeedKnots,
				scenarioStartUtc = scenarioStartUtc,
				environment = environment ?? new ScenarioEnvironment { calm = true },
			};
		}
	}
}
