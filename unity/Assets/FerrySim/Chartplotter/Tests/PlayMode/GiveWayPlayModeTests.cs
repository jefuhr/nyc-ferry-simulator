using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: a landing name drawn over own ship or a target; a name that never comes back once clear; a name
	// hidden while nothing covers it.
	public class GiveWayPlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		[UnityTest]
		public IEnumerator LandingNames_GiveWayToOwnShipAndTargets_AndReturn()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.View.SpanNauticalMiles = 0.5;
			// Own ship well clear of Pier A: its name sits at the preferred anchor.
			_rig.Own.Place(0, -400, 0, 0, 3);
			for (var i = 0; i < 4; i++) yield return null;
			var pool = _rig.View.Canvas.StopLabelPool;
			var pierA = pool.Find(SyntheticHarbor.PierA);
			Assert.That(pierA, Is.GreaterThanOrEqualTo(0));
			Assert.That(pool.IsAlternate(pierA), Is.False);
			Assert.That(pool.IsHidden(pierA), Is.False);
			// Own ship on the preferred anchor's side of the mark: the name moves to its alternate anchor.
			_rig.Own.Place(1, SyntheticHarbor.PierAPosition.North + 10, SyntheticHarbor.PierAPosition.East + 40, 0, 0);
			for (var i = 0; i < 3; i++) yield return null;
			Assert.That(pool.IsAlternate(pierA), Is.True, "moved to the lower left");
			Assert.That(pool.IsHidden(pierA), Is.False);
			Assert.That(pool[pierA].resolvedStyle.visibility, Is.EqualTo(Visibility.Visible));
			// A target on the alternate anchor as well: the name hides.
			var target = _rig.Targets.Add("TUG", 123, SyntheticHarbor.PierAPosition.North - 12, SyntheticHarbor.PierAPosition.East - 45, 0, 0);
			for (var i = 0; i < 3; i++) yield return null;
			Assert.That(pool.IsHidden(pierA), Is.True, "both anchors covered");
			// Everything clears: the name returns to its preferred anchor.
			_rig.Targets.Clear();
			_rig.Own.Place(2, -400, 0, 0, 3);
			for (var i = 0; i < 3; i++) yield return null;
			Assert.That(pool.IsHidden(pierA), Is.False);
			Assert.That(pool.IsAlternate(pierA), Is.False, "back to the preferred anchor");
		}
	}
}
