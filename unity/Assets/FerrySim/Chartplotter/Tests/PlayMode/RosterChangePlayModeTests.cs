using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	/// <summary>
	/// Review item 5: a new roster of the same size (another day, a replay) must never keep the previous roster's names
	/// in the selection label or an unnamed row's MMSI text, even when the same index is selected or ranked again.
	/// </summary>
	public class RosterChangePlayModeTests
	{
		private PlotterRig _rig;

		[TearDown]
		public void TearDown()
		{
			_rig?.Dispose();
			_rig = null;
		}

		/// <summary>One of two rosters at a time; swapping bumps the generation the way a run change does.</summary>
		private sealed class SwitchingTargets : ITargetSource
		{
			private readonly SyntheticTargets[] _rosters;
			private int _current;

			public SwitchingTargets(SyntheticTargets a, SyntheticTargets b) { _rosters = new[] { a, b }; }

			public void Switch() => _current = 1 - _current;

			public long Generation => _rosters[_current].Generation + 1000 * (_current + 1);

			public int Count => _rosters[_current].Count;

			public bool TrySample(int target, double time, out TargetSample sample) => _rosters[_current].TrySample(target, time, out sample);

			public string Name(int target) => _rosters[_current].Name(target);

			public int Mmsi(int target) => _rosters[_current].Mmsi(target);
		}

		[UnityTest]
		public IEnumerator ANewRosterOfTheSameSize_ReplacesTheSelectionNameAndTheRowMmsi()
		{
			_rig = new PlotterRig(1280, 720, new Rect(0, 0, 1280, 720));
			_rig.Own.Place(0, -1500, 0, 0, 6);
			var a = new SyntheticTargets();
			a.Add("ALPHA", 100, -600, 60, -Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			a.Add("", 200, -1500, 400, Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			var b = new SyntheticTargets();
			b.Add("BRAVO", 300, -600, 60, -Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			b.Add("", 400, -1500, 400, Core.Contracts.Units.KnotsToMetresPerSecond(6), 0);
			var switching = new SwitchingTargets(a, b);
			_rig.Display.Connect(_rig.Own, _rig.Display.Chart, switching);
			_rig.View.SpanNauticalMiles = 2.0;
			for (var i = 0; i < 3; i++) yield return null;
			var view = _rig.View;
			Assert.That(view.List.RowName(0), Is.EqualTo("ALPHA"));
			Assert.That(view.List.RowName(1), Is.EqualTo("200"), "an unnamed vessel reads its MMSI");
			view.SelectNextTarget();
			Assert.That(view.SelectedTarget, Is.EqualTo(0));
			yield return null;
			Assert.That(view.Targets.SelectionTextShown, Is.EqualTo("ALPHA"));

			// The roster changes under the same indices: the selection clears, and the texts must follow the new roster.
			switching.Switch();
			yield return null;
			Assert.That(view.SelectedTarget, Is.EqualTo(-1), "a roster change clears the selection");
			Assert.That(view.List.RowName(0), Is.EqualTo("BRAVO"));
			Assert.That(view.List.RowName(1), Is.EqualTo("400"), "the row's MMSI text follows the new roster");
			view.SelectNextTarget();
			Assert.That(view.SelectedTarget, Is.EqualTo(0));
			yield return null;
			Assert.That(view.Targets.SelectionTextShown, Is.EqualTo("BRAVO"), "the selection label names the new roster's vessel");

			// Clearing the selection and selecting the same index again rebuilds the text too.
			view.ClearSelection();
			yield return null;
			switching.Switch();
			yield return null;
			view.SelectNextTarget();
			yield return null;
			Assert.That(view.Targets.SelectionTextShown, Is.EqualTo("ALPHA"));
		}
	}
}
