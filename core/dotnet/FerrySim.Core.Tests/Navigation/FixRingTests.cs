using System;
using FerrySim.Core.Navigation;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: a fix taken every frame regardless of movement, the oldest fix not the first after a wrap, an
	// allocation on wrap, a non-finite position recorded, and a track that survives Clear.
	public class FixRingTests
	{
		[Test]
		public void TakesTheFirstFix_ThenOnlyAfterTheDistanceOrTheInterval()
		{
			var ring = new FixRing(8, 25.0, 10.0);
			Assert.That(ring.TryAdd(0, 0, 0), Is.True);
			Assert.That(ring.TryAdd(10, 0, 1), Is.False, "10 m after 1 s is neither far nor late");
			Assert.That(ring.TryAdd(24.9, 0, 9.9), Is.False);
			Assert.That(ring.TryAdd(25, 0, 2), Is.True, "25 m is far enough");
			Assert.That(ring.TryAdd(26, 0, 12), Is.True, "10 s is late enough");
			Assert.That(ring.Count, Is.EqualTo(3));
			Assert.That(ring.North(2), Is.EqualTo(26));
		}

		[Test]
		public void WrapsOldestFirst_WithoutAllocating()
		{
			var ring = new FixRing(4, 0.0, 0.0);
			for (var i = 0; i < 4; i++)
			{
				ring.TryAdd(i, -i, i);
			}

			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 4; i < 4000; i++)
			{
				ring.TryAdd(i, -i, i);
			}

			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			Assert.That(allocated, Is.Zero, "wrapping allocates nothing");
			Assert.That(ring.Count, Is.EqualTo(4));
			Assert.That(ring.Taken, Is.EqualTo(4000));
			for (var i = 0; i < 4; i++)
			{
				Assert.That(ring.North(i), Is.EqualTo(3996 + i), "oldest first");
				Assert.That(ring.Time(i), Is.EqualTo(3996 + i));
			}

			Assert.That(() => ring.North(4), Throws.InstanceOf<ArgumentOutOfRangeException>());
		}

		[Test]
		public void Clear_ForgetsEverything_AndBadFixesAreIgnored()
		{
			var ring = new FixRing(3, 1.0, 1.0);
			ring.TryAdd(0, 0, 0);
			ring.TryAdd(5, 5, 5);
			ring.Clear();
			Assert.That(ring.Count, Is.Zero);
			Assert.That(ring.Taken, Is.Zero);
			Assert.That(ring.TryAdd(double.NaN, 0, 1), Is.False);
			Assert.That(ring.TryAdd(0, double.PositiveInfinity, 1), Is.False);
			Assert.That(ring.Count, Is.Zero);
			Assert.That(ring.TryAdd(1, 1, 1), Is.True, "the first fix after a clear is always taken");
		}

		[Test]
		public void StationaryWithZeroSpacing_DoesNotFillWithDuplicates()
		{
			var ring = new FixRing(3, 0.0, 0.0);
			Assert.That(ring.TryAdd(1, 1, 0), Is.True);
			Assert.That(ring.TryAdd(1, 1, 1), Is.False, "the same point again is not a new fix");
			Assert.That(ring.TryAdd(1, 1.5, 1), Is.True);
		}
	}
}
