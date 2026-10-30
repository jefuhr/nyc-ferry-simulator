using FerrySim.Core.Navigation;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Navigation
{
	// Failure modes: two kept labels overlapping, a gap under the spacing, a label kept outside the area, a large label
	// missed by the bucket grid, an order that differs between identical passes, and capacity silently exceeded.
	public class LabelDeclutterTests
	{
		[Test]
		public void OverlappingLabel_IsRefused_SeparatedLabel_IsKept()
		{
			var declutter = new LabelDeclutter(16);
			declutter.Begin(0, 0, 400, 300, 2.0, 40, 14);
			Assert.That(declutter.TryPlace(100, 100, 30, 12), Is.True);
			Assert.That(declutter.TryPlace(110, 104, 30, 12), Is.False, "overlaps the first");
			Assert.That(declutter.TryPlace(100, 113, 30, 12), Is.False, "1 px gap is under the 2 px spacing");
			Assert.That(declutter.TryPlace(100, 116.5, 30, 12), Is.True, "4.5 px gap clears the spacing");
			Assert.That(declutter.Count, Is.EqualTo(2));
			Assert.That(declutter.Refused, Is.EqualTo(2));
		}

		[Test]
		public void OutsideTheArea_OrTooLarge_IsRefused()
		{
			var declutter = new LabelDeclutter(16);
			declutter.Begin(0, 0, 200, 200, 0, 40, 14);
			Assert.That(declutter.TryPlace(5, 100, 30, 12), Is.False, "left edge outside");
			Assert.That(declutter.TryPlace(100, 196, 30, 12), Is.False, "bottom edge outside");
			Assert.That(declutter.TryPlace(100, 100, 41, 12), Is.False, "wider than the declared largest label");
			Assert.That(declutter.TryPlace(100, 100, 40, 14), Is.True);
		}

		[Test]
		public void LargeLabel_BlocksEveryCellItCovers()
		{
			var declutter = new LabelDeclutter(16);
			declutter.Begin(0, 0, 960, 540, 4.0, 60, 20);
			Assert.That(declutter.TryPlace(480, 270, 60, 20), Is.True);
			// Far corners of the kept label, in different grid cells from its centre.
			Assert.That(declutter.TryPlace(480 + 30 + 2 + 15, 270 + 10 + 2 + 5, 30, 10), Is.False, "touches the bottom-right corner within the spacing");
			Assert.That(declutter.TryPlace(480 - 30 - 2 - 15, 270 - 10 - 2 - 5, 30, 10), Is.False, "touches the top-left corner within the spacing");
			Assert.That(declutter.TryPlace(480 + 30 + 4 + 16, 270, 30, 10), Is.True, "clear to the right");
		}

		[Test]
		public void SameOffers_SameResult_AndCapacityHolds()
		{
			var first = Run(new LabelDeclutter(5));
			var second = Run(new LabelDeclutter(5));
			Assert.That(first, Is.EqualTo(second));
			Assert.That(first, Is.EqualTo("111110000000"), "five kept, then the capacity refuses the rest");
		}

		private static string Run(LabelDeclutter declutter)
		{
			declutter.Begin(0, 0, 1000, 100, 1.0, 20, 10);
			var text = string.Empty;
			for (var i = 0; i < 12; i++)
			{
				text += declutter.TryPlace(20 + i * 40, 50, 20, 10) ? "1" : "0";
			}

			return text;
		}

		[Test]
		public void Begin_RejectsBadAreas()
		{
			var declutter = new LabelDeclutter(4);
			Assert.That(() => declutter.Begin(0, 0, 0, 10, 0, 1, 1), Throws.ArgumentException);
			Assert.That(() => declutter.Begin(0, 0, 10, 10, -1, 1, 1), Throws.ArgumentException);
			Assert.That(() => new LabelDeclutter(0), Throws.InstanceOf<System.ArgumentOutOfRangeException>());
		}
	}
}
