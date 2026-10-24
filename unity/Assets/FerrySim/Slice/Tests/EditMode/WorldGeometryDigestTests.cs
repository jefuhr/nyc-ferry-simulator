using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using NUnit.Framework;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The geometry identity recordings carry (<see cref="WorldGeometryDigest"/>, the coordinator-approved Sim
	/// change). Failure modes guarded: two different geometries with the same digest (a replay against the wrong
	/// faces would pass the check), a digest that misses a field (a changed fender or a renumbered face would go
	/// unnoticed), order not counted (the core's queries depend on it), and open water confused with an empty or
	/// unknown geometry.
	/// </summary>
	public class WorldGeometryDigestTests
	{
		private static SegmentListGeometry Geometry(params ContactSegment[] segments) => new SegmentListGeometry(segments);

		private static ContactSegment A() => new ContactSegment(new HorizontalVector(963.0241368959863, 3771.9112337790434), new HorizontalVector(983.9753242661793, 3786.6043392838224), new ContactSurface(1.0e6, 5.0e4, 0.3, 0.25), 1101942229);

		private static ContactSegment B() => new ContactSegment(new HorizontalVector(395.03136729958675, 1623.4396393185966), new HorizontalVector(420.47475958988525, 1633.7105801271898), new ContactSurface(1.0e6, 5.0e4, 0.3, 0.25), 1444272253);

		[Test]
		public void TheSameSegments_GiveTheSameDigest()
		{
			var first = WorldGeometryDigest.Of(Geometry(A(), B()));
			var second = WorldGeometryDigest.Of(Geometry(A(), B()));

			Assert.That(first, Does.Match("^fnv1a64:[0-9a-f]{16}$"));
			Assert.That(second, Is.EqualTo(first));
		}

		private static double NextBit(double value) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) ^ 1L);

		private static IEnumerable<TestCaseData> SingleBitChanges()
		{
			var a = A();
			var s = a.Surface;
			yield return new TestCaseData(new ContactSegment(a.Start, a.End, s, a.Id ^ 1)).SetName("ADifferentId_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(new HorizontalVector(NextBit(a.Start.North), a.Start.East), a.End, s, a.Id)).SetName("OneBitOfStartNorth_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(new HorizontalVector(a.Start.North, NextBit(a.Start.East)), a.End, s, a.Id)).SetName("OneBitOfStartEast_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(a.Start, new HorizontalVector(NextBit(a.End.North), a.End.East), s, a.Id)).SetName("OneBitOfEndNorth_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(a.Start, new HorizontalVector(a.End.North, NextBit(a.End.East)), s, a.Id)).SetName("OneBitOfEndEast_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(a.Start, a.End, new ContactSurface(NextBit(s.Stiffness), s.Damping, s.Friction, s.MaxCompression), a.Id)).SetName("OneBitOfStiffness_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(a.Start, a.End, new ContactSurface(s.Stiffness, NextBit(s.Damping), s.Friction, s.MaxCompression), a.Id)).SetName("OneBitOfDamping_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(a.Start, a.End, new ContactSurface(s.Stiffness, s.Damping, NextBit(s.Friction), s.MaxCompression), a.Id)).SetName("OneBitOfFriction_ChangesTheDigest");
			yield return new TestCaseData(new ContactSegment(a.Start, a.End, new ContactSurface(s.Stiffness, s.Damping, s.Friction, NextBit(s.MaxCompression)), a.Id)).SetName("OneBitOfMaxCompression_ChangesTheDigest");
		}

		[TestCaseSource(nameof(SingleBitChanges))]
		public void AnySingleBit_ChangesTheDigest(ContactSegment changed)
		{
			var original = WorldGeometryDigest.Of(Geometry(A(), B()));
			var modified = WorldGeometryDigest.Of(Geometry(changed, B()));

			Assert.That(modified, Does.StartWith("fnv1a64:"));
			Assert.That(modified, Is.Not.EqualTo(original));
		}

		[Test]
		public void SegmentOrder_ChangesTheDigest()
		{
			Assert.That(WorldGeometryDigest.Of(Geometry(B(), A())), Is.Not.EqualTo(WorldGeometryDigest.Of(Geometry(A(), B()))));
		}

		[Test]
		public void OpenWater_IsNone()
		{
			Assert.That(WorldGeometryDigest.Of(EmptyWorldGeometry.Instance), Is.EqualTo(WorldGeometryDigest.OpenWater));
			Assert.That(WorldGeometryDigest.Of(null), Is.EqualTo("none"));
		}

		[Test]
		public void AnEmptySegmentList_IsNotOpenWater_AndAnUnknownGeometryHasNoDigest()
		{
			Assert.That(WorldGeometryDigest.Of(Geometry()), Does.StartWith("fnv1a64:"));
			Assert.That(WorldGeometryDigest.Of(new OpaqueGeometry()), Is.Null, "a geometry that cannot list its segments cannot be identified");
		}

		private sealed class OpaqueGeometry : IWorldGeometry
		{
			public int QuerySegments(double north, double east, double radius, ContactSegment[] buffer) => 0;
		}
	}
}
