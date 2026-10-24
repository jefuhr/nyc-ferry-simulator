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
		/// its water side, heading straight in (against its water normal). A face the pipeline split into subsegments
		/// is approached as one, from its first segment's start to its last segment's end; it must be straight, every
		/// segment within <see cref="LandingGeometry.NormalToleranceDegrees"/> of that line. The start goes through
		/// <see cref="LocalTangentPlane.World"/>, like every scenario position.
		/// </summary>
		public static ScenarioSettings BowOn(LandingFace face, VesselParameters vessel, double bowClearance, string name, string vesselFile, string scenarioStartUtc,
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

			// The water normal exactly as ContactSegment.WaterNormal computes it, so a one-segment face gives the same start to the bit.
			var delta = face.End - face.Start;
			var along = delta * (1.0 / delta.Magnitude);
			var normal = new HorizontalVector(along.East, -along.North);
			var straight = Math.Cos(LandingGeometry.NormalToleranceDegrees * Math.PI / 180.0);
			foreach (var part in face.Segments)
			{
				var direction = part.Segment.Direction;
				if (direction.North * along.North + direction.East * along.East < straight)
				{
					throw new ArgumentException($"Stop {face.StopId}'s primary face bends at segment {part.Id}, so a bow-on approach has no single heading.", nameof(face));
				}
			}

			var bow = double.NegativeInfinity;
			foreach (var point in FaceClearance.BodyOutline(vessel))
			{
				bow = Math.Max(bow, point.North);
			}

			var offset = bow + bowClearance;
			var north = 0.5 * (face.Start.North + face.End.North) + normal.North * offset;
			var east = 0.5 * (face.Start.East + face.End.East) + normal.East * offset;
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
