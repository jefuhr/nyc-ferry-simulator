using System;
using FerrySim.Core.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// Depth sets with several grids (ferrysim-env-depth/1 as revised 2026-10-25): grids may meet only on one lattice and
	/// where every shared sample is identical, and a point inside several grids resolves to the first. Failure modes guarded:
	/// <list type="bullet">
	/// <item>A seam step: grids sharing an edge sampled either side of it differing by more than the bed's own slope.</item>
	/// <item>An ambiguous set accepted: a shared sample that differs (−10 against −20 m), is unknown in one grid only, or
	/// grids that meet off one lattice or with different cell sizes, in either order.</item>
	/// <item>Grids in the wrong order winning: a point inside several grids resolved by a later one, which shows where the
	/// first grid's cell has an unknown corner of zero weight.</item>
	/// <item>Valid sets refused: grids that do not meet, a shared edge or corner, an aligned overlap, a sample unknown in
	/// both grids.</item>
	/// </list>
	/// </summary>
	[TestFixture]
	public class DepthSetTests
	{
		// A bed that is not bilinear, the same function of world position in every grid, so shared samples agree.
		private static float Bed(double north, double east) => (float)(-10.0 - 0.01 * north - 0.02 * east - 0.0001 * north * north);

		private static DepthGrid Grid(string id, double originNorth, double originEast, double cell, int rows, int columns, Func<double, double, float> bed = null)
		{
			bed = bed ?? Bed;
			return PackFixtures.Grid(id, originNorth, originEast, cell, rows, columns, (r, c) => bed(originNorth + r * cell, originEast + c * cell));
		}

		[Test]
		public void GridsSharingAnEdge_WithIdenticalSamples_AreAccepted_AndContinuousAcrossTheSeam()
		{
			var south = Grid("south", -100.0, -50.0, 5.0, 21, 21);
			var north = Grid("north", 0.0, -50.0, 5.0, 21, 21);
			var depth = PackFixtures.Depth(20.0, south, north);

			// Along a line across the seam in 1 mm steps no step exceeds what the bed's own slope gives over 1 mm.
			var previous = double.NaN;
			var largest = 0.0;
			for (var k = -2000; k <= 2000; k++)
			{
				var n = k * 0.001;
				Assert.That(depth.Sample(n, 3.3, out var bed), Is.EqualTo(BedSample.Known), $"north {n}");
				if (!double.IsNaN(previous))
				{
					largest = Math.Max(largest, Math.Abs(bed - previous));
				}

				previous = bed;
			}

			// The bed's slope here is at most 0.03 m per metre: 3e-5 m per millimetre.
			Assert.That(largest, Is.LessThan(3.1e-5));
			Assert.That(depth.Sample(-1e-9, 3.3, out var below), Is.EqualTo(BedSample.Known));
			Assert.That(depth.Sample(1e-9, 3.3, out var above), Is.EqualTo(BedSample.Known));
			Assert.That(Math.Abs(above - below), Is.LessThan(1e-9));
			Assert.That(depth.GridAt(-1e-9, 3.3), Is.EqualTo(0));
			Assert.That(depth.GridAt(1e-9, 3.3), Is.EqualTo(1));
		}

		[TestCase(false)]
		[TestCase(true)]
		public void GridsSharingAnEdge_WithADifferentSample_AreRefused_InEitherOrder(bool reversed)
		{
			// The wave 1 review's case: on the shared row one grid says −10 m and the other −20 m.
			var south = PackFixtures.FlatGrid("south", -10.0, 0.0, 10.0, 2, 2, -10.0f);
			var north = PackFixtures.FlatGrid("north", 0.0, 0.0, 10.0, 2, 2, -20.0f);
			var grids = reversed ? new[] { north, south } : new[] { south, north };

			var error = Assert.Throws<ArgumentException>(() => PackFixtures.Depth(20.0, grids));
			Assert.That(error.Message, Does.Contain("south").And.Contain("north").And.Contain("north 0, east 0").And.Contain("-10").And.Contain("-20"));
		}

		[Test]
		public void ASharedSampleUnknownInOneGridOnly_IsRefused()
		{
			var south = Grid("south", -10.0, 0.0, 10.0, 2, 3);
			var north = Grid("north", 0.0, 0.0, 10.0, 2, 3, (n, e) => n == 0.0 && e == 10.0 ? float.NaN : Bed(n, e));

			var error = Assert.Throws<ArgumentException>(() => PackFixtures.Depth(20.0, south, north));
			Assert.That(error.Message, Does.Contain("north 0, east 10").And.Contain("NaN"));
		}

		[Test]
		public void ASharedSampleUnknownInBothGrids_IsAccepted_AndUnknownOnEitherSide()
		{
			Func<double, double, float> bed = (n, e) => n == 0.0 && e == 10.0 ? float.NaN : Bed(n, e);
			var depth = PackFixtures.Depth(20.0, Grid("south", -10.0, 0.0, 10.0, 2, 3, bed), Grid("north", 0.0, 0.0, 10.0, 2, 3, bed));

			Assert.That(depth.Sample(-1.0, 12.0, out _), Is.EqualTo(BedSample.Unknown));
			Assert.That(depth.Sample(1.0, 12.0, out _), Is.EqualTo(BedSample.Unknown));
			Assert.That(depth.Sample(1.0, 2.0, out _), Is.EqualTo(BedSample.Unknown));
		}

		[TestCase(15.0, 0.0, TestName = "GridsThatMeetOffOneLattice_AreRefused(overlapping by half a cell)")]
		[TestCase(20.0, 2.5, TestName = "GridsThatMeetOffOneLattice_AreRefused(on an edge, a quarter cell east)")]
		[TestCase(-5.0, -5.0, TestName = "GridsThatMeetOffOneLattice_AreRefused(overlapping, half a cell off both ways)")]
		public void GridsThatMeetOffOneLattice_AreRefused(double originNorth, double originEast)
		{
			var first = Grid("first", 0.0, 0.0, 10.0, 3, 3);
			var second = Grid("second", originNorth, originEast, 10.0, 3, 3);

			var error = Assert.Throws<ArgumentException>(() => PackFixtures.Depth(20.0, first, second));
			Assert.That(error.Message, Does.Contain("first").And.Contain("second").And.Contain("one lattice"));
		}

		[Test]
		public void GridsThatMeetWithDifferentCellSizes_AreRefused()
		{
			var coarse = Grid("coarse", 0.0, 0.0, 10.0, 3, 3);
			var fine = Grid("fine", 20.0, 0.0, 5.0, 5, 5);

			var error = Assert.Throws<ArgumentException>(() => PackFixtures.Depth(20.0, coarse, fine));
			Assert.That(error.Message, Does.Contain("coarse").And.Contain("fine").And.Contain("cell size"));
		}

		[Test]
		public void GridsThatDoNotMeet_MayUseAnyLattice()
		{
			var depth = PackFixtures.Depth(20.0, Grid("a", 0.0, 0.0, 10.0, 3, 3), Grid("b", 20.5, 0.0, 5.0, 3, 3), Grid("c", 0.0, 20.0001, 7.0, 2, 2));
			Assert.That(depth.Grids.Count, Is.EqualTo(3));
			Assert.That(depth.Sample(20.25, 5.0, out _), Is.EqualTo(BedSample.Outside), "between the lattices");
		}

		[Test]
		public void GridsMeetingAtACornerOnly_MustAgreeThere()
		{
			var southWest = Grid("south-west", -20.0, -20.0, 10.0, 3, 3);
			var northEast = Grid("north-east", 0.0, 0.0, 10.0, 3, 3);
			Assert.That(PackFixtures.Depth(20.0, southWest, northEast).GridAt(0.0, 0.0), Is.EqualTo(0));

			var other = Grid("north-east", 0.0, 0.0, 10.0, 3, 3, (n, e) => n == 0.0 && e == 0.0 ? -99.0f : Bed(n, e));
			var error = Assert.Throws<ArgumentException>(() => PackFixtures.Depth(20.0, southWest, other));
			Assert.That(error.Message, Does.Contain("north 0, east 0"));
		}

		[Test]
		public void OverlappingGridsOnOneLattice_WithIdenticalSamples_AreAccepted_AndAPointResolvesToTheFirst()
		{
			var big = Grid("big", 0.0, 0.0, 10.0, 6, 6);
			var small = Grid("small", 20.0, 20.0, 10.0, 6, 6);

			var depth = PackFixtures.Depth(20.0, big, small);
			Assert.That(depth.GridAt(35.0, 35.0), Is.EqualTo(0), "inside both: the first");
			Assert.That(depth.GridAt(65.0, 65.0), Is.EqualTo(1), "inside the second only");
			Assert.That(depth.GridAt(80.0, 80.0), Is.EqualTo(-1), "inside neither");
			Assert.That(PackFixtures.Depth(20.0, small, big).GridAt(35.0, 35.0), Is.EqualTo(0), "the first is the other one now");
			Assert.That(depth.Sample(35.0, 35.0, out var first), Is.EqualTo(BedSample.Known));
			Assert.That(PackFixtures.Depth(20.0, small, big).Sample(35.0, 35.0, out var second), Is.EqualTo(BedSample.Known));
			Assert.That(second, Is.EqualTo(first).Within(1e-12), "identical shared samples give the same bed either way");
		}

		[Test]
		public void APointOnASeam_ResolvesToTheFirstGrid_WhichDecidesWhetherTheBedIsKnown()
		{
			// The south grid's cell under the seam has an unknown corner a row below it, of zero weight on the seam; the
			// north grid's cell above the seam is all known. The first grid decides.
			Func<double, double, float> bed = (n, e) => n == -10.0 && e == 10.0 ? float.NaN : Bed(n, e);
			var south = Grid("south", -20.0, 0.0, 10.0, 3, 3, bed);
			var north = Grid("north", 0.0, 0.0, 10.0, 3, 3, bed);

			Assert.That(PackFixtures.Depth(20.0, south, north).Sample(0.0, 15.0, out _), Is.EqualTo(BedSample.Unknown));
			Assert.That(PackFixtures.Depth(20.0, north, south).Sample(0.0, 15.0, out var known), Is.EqualTo(BedSample.Known));
			double west = Bed(0.0, 10.0), east = Bed(0.0, 20.0);
			Assert.That(known, Is.EqualTo(west + 0.5 * (east - west)).Within(1e-12));
		}

		[Test]
		public void ASingleGrid_SamplesAsItsOwnGrid()
		{
			var grid = Grid("only", -653.25, -425.75, 5.0, 40, 30);
			var depth = PackFixtures.Depth(20.0, grid);
			foreach (var (north, east) in new[] { (-653.25, -425.75), (-600.0, -400.0), (-512.4, -301.9), (-458.25, -280.75), (-458.26, -280.75), (-458.24, -280.75) })
			{
				Assert.That(depth.Sample(north, east, out var bed), Is.EqualTo(grid.Sample(north, east, out var expected)));
				Assert.That(bed, Is.EqualTo(expected));
				Assert.That(depth.GridAt(north, east), Is.EqualTo(grid.Contains(north, east) ? 0 : -1));
			}
		}
	}
}
