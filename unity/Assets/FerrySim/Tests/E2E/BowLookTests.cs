using FerrySim.Core.Contracts;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.E2E
{
	/// <summary>Guards camera chatter at the approach boundary, automatic takeover after hand look, and render-only scope.</summary>
	public class BowLookTests
	{
		[Test]
		public void B_LeavesAutomaticBowOnOnePress_UntilTheNextApproach()
		{
			var look = new BowLookState();
			look.UpdateApproach(19, .5, true);
			Assert.That(look.ShowBow, Is.True);
			Assert.That(look.Manual, Is.False);
			look.ToggleManual();
			Assert.That(look.ShowBow, Is.False, "one B press leaves the visible automatic preset");
			Assert.That(look.Manual, Is.False, "leaving automatic bow must not latch manual on");
			look.UpdateApproach(4, .1, true);
			Assert.That(look.ShowBow, Is.False, "the same approach stays suppressed");
			look.UpdateApproach(30, 1.5, true);
			Assert.That(look.ShowBow, Is.False);
			look.UpdateApproach(31, .5, true);
			look.UpdateApproach(19, .5, true);
			Assert.That(look.ShowBow, Is.True, "the next approach can re-enable automatic bow");
		}

		[Test]
		public void B_WithAutomaticOff_TogglesVisiblePreset_AndResetClearsIt()
		{
			var look = new BowLookState();
			look.UpdateApproach(19, .5, false);
			look.ToggleManual();
			Assert.That(look.ShowBow, Is.True);
			look.ToggleManual();
			Assert.That(look.ShowBow, Is.False);
			look.ToggleManual();
			look.HandLook();
			Assert.That(look.ShowBow, Is.False);
			look.ToggleManual();
			look.Reset();
			look.UpdateApproach(19, .5, false);
			Assert.That(look.ShowBow, Is.False, "reset clears manual state without enabling automatic");
		}

		[Test]
		public void Automatic_EntersSlowAndNear_ExitsWithHysteresis()
		{
			var look = new BowLookState();
			look.UpdateApproach(19, 2, true);
			Assert.That(look.ShowBow, Is.False);
			look.UpdateApproach(19, .9, true);
			Assert.That(look.ShowBow, Is.True);
			look.UpdateApproach(25, 1.2, true);
			Assert.That(look.ShowBow, Is.True);
			look.UpdateApproach(31, 1.2, true);
			Assert.That(look.ShowBow, Is.False);
			look.UpdateApproach(19, .9, true);
			look.UpdateApproach(19, 1.6, true);
			Assert.That(look.ShowBow, Is.False);
		}

		[Test]
		public void HandLook_OverridesUntilTheNextApproach_AndKeyTogglesIndependently()
		{
			var look = new BowLookState();
			look.UpdateApproach(4, 0, true);
			look.HandLook();
			Assert.That(look.ShowBow, Is.False);
			look.UpdateApproach(5, .1, true);
			Assert.That(look.ShowBow, Is.False);
			look.UpdateApproach(35, .1, true);
			look.UpdateApproach(19, .1, true);
			Assert.That(look.ShowBow, Is.True);
			look.UpdateApproach(19, .1, false);
			Assert.That(look.ShowBow, Is.False);
			look.ToggleManual();
			Assert.That(look.ShowBow, Is.True);
			look.ToggleManual();
			Assert.That(look.ShowBow, Is.False);
		}

		[Test]
		public void ApproachQueriesAndHysteresis_AllocateNothingAfterConstruction()
		{
			var face = new ContactSegment(new HorizontalVector(25, 10), new HorizontalVector(25, -10), new ContactSurface(1000, 100, .3, .1), 1);
			var geometry = new SegmentListGeometry(new[] { face });
			var buffer = new ContactSegment[16];
			var look = new BowLookState();
			void Frame()
			{
				var distance = BowLookState.FaceDistance(geometry, buffer, 0, 0, 0, 10);
				look.UpdateApproach(distance, .5, true);
			}
			Frame();
			Assert.That(Frame, Is.Not.AllocatingGCMemory());
		}

		[Test]
		public void HandLookAwayFromLanding_DoesNotSuppressTheNextApproach()
		{
			var look = new BowLookState();
			look.UpdateApproach(100, 0, true);
			look.HandLook();
			look.UpdateApproach(19, .5, true);
			Assert.That(look.ShowBow, Is.True);
		}

		[Test]
		public void ApproachDistance_UsesBowAndOnlyWaterSideFacesAhead()
		{
			var surface = new ContactSurface(1000, 100, .3, .1);
			var geometry = new SegmentListGeometry(new[] {
				new ContactSegment(new HorizontalVector(25, 10), new HorizontalVector(25, -10), surface, 1) });
			var buffer = new ContactSegment[16];
			Assert.That(BowLookState.FaceDistance(geometry, buffer, 0, 0, 0, 10), Is.EqualTo(15));
			Assert.That(BowLookState.FaceDistance(geometry, buffer, 0, 0, System.Math.PI, 10), Is.EqualTo(double.PositiveInfinity));
		}
	}
}
