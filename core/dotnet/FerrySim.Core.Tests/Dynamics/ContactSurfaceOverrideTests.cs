using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>Failure modes: silent default change, wrong segment, changed geometry/order, mutable overrides and invalid default surface.</summary>
	public class ContactSurfaceOverrideTests
	{
		[Test]
		public void OverridesRequireExplicitOptInAndKeepOtherSegmentsAndGeometry()
		{
			var original = ContactScenarios.EstimatedBargeFender;
			var revised = ContactScenarios.ContractTestFender;
			var first = new ContactSegment(new HorizontalVector(0, 0), new HorizontalVector(10, 0), original, 1);
			var second = new ContactSegment(new HorizontalVector(10, 0), new HorizontalVector(10, 10), original, 2);
			var world = new SegmentListGeometry(new[] { first, second });
			var overrides = new Dictionary<int, ContactSurface> { [1] = revised };
			var off = new ContactSurfaceOverrides(world, overrides);
			var on = new ContactSurfaceOverrides(world, overrides, enabled: true);
			overrides[1] = original;
			var buffer = new ContactSegment[2];
			Assert.That(off.QuerySegments(0, 0, 30, buffer), Is.EqualTo(2));
			Assert.That(buffer, Is.EqualTo(new[] { first, second }));
			Assert.That(on.QuerySegments(0, 0, 30, buffer), Is.EqualTo(2));
			Assert.That(buffer[0].Surface, Is.EqualTo(revised));
			Assert.That(buffer[0].Start, Is.EqualTo(first.Start));
			Assert.That(buffer[0].End, Is.EqualTo(first.End));
			Assert.That(buffer[0].Id, Is.EqualTo(first.Id));
			Assert.That(buffer[1], Is.EqualTo(second));
		}

		[Test]
		public void InvalidSurfaceIsRejectedBeforeSimulation()
		{
			Assert.Throws<ArgumentException>(() => new ContactSurfaceOverrides(EmptyWorldGeometry.Instance,
				new Dictionary<int, ContactSurface> { [1] = default }, enabled: true));
		}
	}
}
