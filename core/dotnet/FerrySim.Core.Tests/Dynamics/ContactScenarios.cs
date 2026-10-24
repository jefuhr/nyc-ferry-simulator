using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>Peak values from one landing on a fendered face, SI units.</summary>
	public sealed class LandingResult
	{
		public double ApproachSpeed;
		public double PeakForce;
		public double PeakCompression;
		public double ReboundSpeed;
		public double ContactDuration;
		public bool BottomedOut;
		public bool Separated;
	}

	/// <summary>
	/// Landing faces, barges and runs shared by the contact tests and the contact report.
	/// <para>
	/// The fender values below are ESTIMATES for tests and the report only. They are not surveyed, published
	/// or crew-reported properties of any NYC Ferry landing: the landing fenders, and how the boats actually
	/// land, are still open crew questions (docs/research/crew-questions.md). Landing faces in the simulator
	/// come from the data pipeline, never from here.
	/// </para>
	/// </summary>
	public static class ContactScenarios
	{
		/// <summary>
		/// Estimated elastomeric barge face fender: about 250 kN per metre at 0.25 m compression (the order of
		/// arch or D fenders sized for small passenger vessels), damping time c/k of 0.05 s, rubber on the
		/// hull fender at a friction of 0.3. An estimate, not a landing survey.
		/// </summary>
		public static readonly ContactSurface EstimatedBargeFender = new ContactSurface(1.0e6, 5.0e4, 0.3, 0.25);

		/// <summary>The softer fender of the contract tests (WorldGeometryTests), also an estimate.</summary>
		public static readonly ContactSurface ContractTestFender = new ContactSurface(2.0e5, 2.0e4, 0.3, 0.25);

		/// <summary>ASCE (2021) describes the NYC Ferry landing barges as 90 ft long and 35 ft wide.</summary>
		public const double BargeLength = 90.0 * 0.3048;

		public const double BargeWidth = 35.0 * 0.3048;

		/// <summary>Middle of the ahead idle detent band (in gear at idle).</summary>
		public static double IdleLever(VesselParameters parameters) =>
			0.5 * (parameters.Controls.ClutchInLever.Value + parameters.Controls.ThrottleStartLever.Value);

		public static IEnumerable<List<OutlinePoint>> OutlineParts(VesselParameters parameters)
		{
			yield return parameters.Contact.StarboardDemihull;
			yield return parameters.Contact.PortDemihull;
			yield return parameters.Contact.BowFenderLine;
			yield return parameters.Contact.SternFenderLine;
		}

		/// <summary>Every outline vertex in body axes (x forward, y starboard).</summary>
		public static List<(double X, double Y)> OutlineVertices(VesselParameters parameters)
		{
			var vertices = new List<(double X, double Y)>();
			foreach (var part in OutlineParts(parameters))
			{
				foreach (var point in part)
				{
					vertices.Add((point.X.Value, point.Y.Value));
				}
			}

			return vertices;
		}

		/// <summary>Forward end of the outline (the bow fender line), m from midships.</summary>
		public static double BowX(VesselParameters parameters)
		{
			var bow = double.MinValue;
			foreach (var vertex in OutlineVertices(parameters))
			{
				bow = Math.Max(bow, vertex.X);
			}

			return bow;
		}

		public static double SternX(VesselParameters parameters)
		{
			var stern = double.MaxValue;
			foreach (var vertex in OutlineVertices(parameters))
			{
				stern = Math.Min(stern, vertex.X);
			}

			return stern;
		}

		public static double HalfBeam(VesselParameters parameters)
		{
			var half = 0.0;
			foreach (var vertex in OutlineVertices(parameters))
			{
				half = Math.Max(half, Math.Abs(vertex.Y));
			}

			return half;
		}

		public static double Length(List<OutlinePoint> line)
		{
			var length = 0.0;
			for (var i = 1; i < line.Count; i++)
			{
				var dx = line[i].X.Value - line[i - 1].X.Value;
				var dy = line[i].Y.Value - line[i - 1].Y.Value;
				length += Math.Sqrt(dx * dx + dy * dy);
			}

			return length;
		}

		/// <summary>Northernmost outline point of a vessel at the origin with this heading, m.</summary>
		public static double MaxNorth(VesselParameters parameters, double heading)
		{
			var north = double.MinValue;
			foreach (var vertex in OutlineVertices(parameters))
			{
				north = Math.Max(north, vertex.X * Math.Cos(heading) - vertex.Y * Math.Sin(heading));
			}

			return north;
		}

		/// <summary>
		/// Deepest any outline vertex of the vessel sits behind <paramref name="segment"/>'s face, within its
		/// length (m, positive on the solid side; negative means every vertex is on the water side).
		/// </summary>
		public static double DeepestVertex(VesselParameters parameters, in VesselState state, in ContactSegment segment)
		{
			var direction = segment.Direction;
			var normal = segment.WaterNormal;
			var cos = Math.Cos(state.Heading);
			var sin = Math.Sin(state.Heading);
			var deepest = double.MinValue;
			foreach (var vertex in OutlineVertices(parameters))
			{
				var north = state.North + vertex.X * cos - vertex.Y * sin - segment.Start.North;
				var east = state.East + vertex.X * sin + vertex.Y * cos - segment.Start.East;
				var along = north * direction.North + east * direction.East;
				if (along < 0.0 || along > segment.Length)
				{
					continue;
				}

				deepest = Math.Max(deepest, -(north * normal.North + east * normal.East));
			}

			return deepest;
		}

		/// <summary>
		/// A straight face running east-west at <paramref name="north"/>, solid to the north and water to the
		/// south, from <paramref name="westEnd"/> to <paramref name="eastEnd"/> metres east.
		/// </summary>
		public static ContactSegment FaceToTheNorth(double north, double westEnd, double eastEnd, in ContactSurface surface, int id) =>
			new ContactSegment(new HorizontalVector(north, eastEnd), new HorizontalVector(north, westEnd), surface, id);

		/// <summary>A face running north-south at <paramref name="east"/>, solid to the east and water to the west.</summary>
		public static ContactSegment FaceToTheEast(double east, double southEnd, double northEnd, in ContactSurface surface, int id) =>
			new ContactSegment(new HorizontalVector(southEnd, east), new HorizontalVector(northEnd, east), surface, id);

		/// <summary>
		/// A landing barge north of the vessel: its long south face at <paramref name="southFace"/> spans
		/// <see cref="BargeLength"/> centred on <paramref name="centreEast"/>. The four faces walk clockwise
		/// seen from above, so the barge is solid inside. The south face has <paramref name="firstId"/>.
		/// </summary>
		public static ContactSegment[] Barge(double southFace, double centreEast, in ContactSurface surface, int firstId)
		{
			var west = centreEast - 0.5 * BargeLength;
			var east = centreEast + 0.5 * BargeLength;
			var north = southFace + BargeWidth;
			var southWest = new HorizontalVector(southFace, west);
			var southEast = new HorizontalVector(southFace, east);
			var northWest = new HorizontalVector(north, west);
			var northEast = new HorizontalVector(north, east);
			return new[]
			{
				new ContactSegment(southEast, southWest, surface, firstId),
				new ContactSegment(southWest, northWest, surface, firstId + 1),
				new ContactSegment(northWest, northEast, surface, firstId + 2),
				new ContactSegment(northEast, southEast, surface, firstId + 3),
			};
		}

		/// <summary>
		/// A closed rectangular structure north of the vessel: its south face at <paramref name="southFace"/>,
		/// <paramref name="width"/> metres deep to the north, from <paramref name="westEnd"/> to
		/// <paramref name="eastEnd"/> metres east. The faces walk clockwise from the south face, which has
		/// <paramref name="firstId"/>; the north face has <paramref name="firstId"/> + 2.
		/// </summary>
		public static ContactSegment[] Block(double southFace, double width, double westEnd, double eastEnd, in ContactSurface surface, int firstId)
		{
			var southWest = new HorizontalVector(southFace, westEnd);
			var southEast = new HorizontalVector(southFace, eastEnd);
			var northWest = new HorizontalVector(southFace + width, westEnd);
			var northEast = new HorizontalVector(southFace + width, eastEnd);
			return new[]
			{
				new ContactSegment(southEast, southWest, surface, firstId),
				new ContactSegment(southWest, northWest, surface, firstId + 1),
				new ContactSegment(northWest, northEast, surface, firstId + 2),
				new ContactSegment(northEast, southEast, surface, firstId + 3),
			};
		}

		/// <summary>
		/// <paramref name="segment"/> cut into consecutive segments at the given distances from its start, with the
		/// same surface and id. Each piece starts exactly where the one before it ends.
		/// </summary>
		public static ContactSegment[] Split(in ContactSegment segment, params double[] cuts)
		{
			var pieces = new ContactSegment[cuts.Length + 1];
			var direction = segment.Direction;
			var start = segment.Start;
			for (var i = 0; i < cuts.Length; i++)
			{
				var end = new HorizontalVector(segment.Start.North + cuts[i] * direction.North, segment.Start.East + cuts[i] * direction.East);
				pieces[i] = new ContactSegment(start, end, segment.Surface, segment.Id);
				start = end;
			}

			pieces[cuts.Length] = new ContactSegment(start, segment.End, segment.Surface, segment.Id);
			return pieces;
		}

		/// <summary>
		/// Gives <paramref name="model"/> the segments and moves the hull straight in to the pose from
		/// <paramref name="distance"/> metres out along the first segment's water normal, as a real approach
		/// would: contact starts only where the outline crosses a face from its water side.
		/// </summary>
		public static void Approach(ContactModel model, ContactSegment[] segments, double north, double east, double heading, double distance = 2.0)
		{
			model.Prepare(segments, segments.Length);
			var normal = segments.Length > 0 ? segments[0].WaterNormal : new HorizontalVector(0.0, 0.0);
			model.Reset(north + distance * normal.North, east + distance * normal.East, heading);
			model.Commit(north, east, heading);
		}

		public static VesselDynamicsSimulation Create(VesselParameters parameters, IWorldGeometry geometry, in VesselState initial, IEnvironment environment = null) =>
			new VesselDynamicsSimulation(parameters, environment ?? UniformEnvironment.Calm, geometry, initial);

		public static double MaxCompression(VesselDynamicsSimulation simulation)
		{
			var deepest = 0.0;
			for (var i = 0; i < simulation.ContactCount; i++)
			{
				deepest = Math.Max(deepest, simulation.GetContact(i).Compression);
			}

			return deepest;
		}

		public static bool AnyBottomedOut(VesselDynamicsSimulation simulation)
		{
			for (var i = 0; i < simulation.ContactCount; i++)
			{
				if (simulation.GetContact(i).BottomedOut)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Coasts the vessel, engines idling in neutral, bow-on into the long face of a barge from 0.2 m off
		/// at <paramref name="speed"/>, and records the impact and the rebound.
		/// </summary>
		public static LandingResult BowOnLanding(VesselParameters parameters, in ContactSurface fender, double speed, double seconds = 20.0) =>
			BowOnLanding(parameters, Barge(BowX(parameters) + 0.2, 0.0, fender, 1), speed, seconds);

		/// <summary>As above, into <paramref name="segments"/>, whose nearest face lies 0.2 m off the bow.</summary>
		public static LandingResult BowOnLanding(VesselParameters parameters, ContactSegment[] segments, double speed, double seconds = 20.0)
		{
			var geometry = new SegmentListGeometry(segments);
			var simulation = Create(parameters, geometry, VesselState.Planar(0.0, 0.0, 0.0, 0.0, surge: speed));
			var result = new LandingResult();
			var touched = false;
			var firstContact = 0.0;
			var steps = (int)Math.Round(seconds * SimulationClock.StepsPerSecond);
			for (var step = 0; step < steps; step++)
			{
				var before = simulation.State.GroundVelocity.North;
				simulation.Step(ControlInputs.Neutral);
				var inContact = simulation.ContactCount > 0;
				if (inContact && !touched)
				{
					touched = true;
					firstContact = simulation.State.Time;
					result.ApproachSpeed = before;
				}

				if (inContact)
				{
					result.PeakForce = Math.Max(result.PeakForce, simulation.Telemetry.ContactForce);
					result.PeakCompression = Math.Max(result.PeakCompression, MaxCompression(simulation));
					result.BottomedOut |= AnyBottomedOut(simulation);
				}
				else if (touched)
				{
					result.Separated = true;
					result.ContactDuration = simulation.State.Time - firstContact;
					result.ReboundSpeed = -simulation.State.GroundVelocity.North;
					break;
				}
			}

			return result;
		}
	}
}
