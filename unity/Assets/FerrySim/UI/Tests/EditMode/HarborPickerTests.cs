using System.Linq;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The picker on the harbor's catalog shape (<see cref="FakeScenarioCatalog.Harbor"/>): 24 landings with notes, a
	/// landing with three berths, days with station-blend currents, and landings the catalog refuses. Failure modes
	/// guarded: the search re-sorting or dropping landings, changing a stable id, leaving the selection on a hidden
	/// landing when a match is shown, or losing the choice when it is cleared; a search with no match clearing the choice
	/// silently; notes missing from the rows or the line under the fields, or a berth's note not following the berth; a
	/// refusal that hides the catalog's reason, or stays after another landing is chosen and started; the current method
	/// missing from the summary; a rebind not clearing the search or not restoring the choice by id; and the slice's
	/// picker changing (no notes, no method).
	/// </summary>
	public class HarborPickerTests
	{
		private const int EastNinety = 15;
		private const int StGeorge = 21;
		private const int Pier11 = 10;

		[SetUp]
		public void ForgetTheSession() => ScenarioPicker.SessionChoice = null;

		[TearDown]
		public void ForgetTheSessionAgain() => ScenarioPicker.SessionChoice = null;

		private static ScenarioPicker Bound(FakeScenarioCatalog catalog)
		{
			var picker = new ScenarioPicker();
			picker.Bind(catalog, catalog);
			return picker;
		}

		[Test]
		public void TheHarborLandings_AreListedInStopIdOrder_WithTheirNotes()
		{
			var catalog = FakeScenarioCatalog.Harbor();
			var picker = Bound(catalog);
			Assert.That(picker.Landings.Count, Is.EqualTo(24));
			Assert.That(picker.Landings.OptionText(0), Is.EqualTo("Hunters Point South"));
			Assert.That(picker.Landings.OptionText(Pier11), Is.EqualTo("Wall St/Pier 11"));
			Assert.That(picker.Landings.OptionText(EastNinety), Is.EqualTo("East 90th St"));
			Assert.That(picker.Landings.OptionText(23), Is.EqualTo("Ferry Point Park"));
			Assert.That(picker.Landings.OptionNote(EastNinety), Is.EqualTo("Waiting for a crew answer on the landing side"), "the crew question shows in the row");
			Assert.That(picker.Landings.OptionNote(0), Is.EqualTo(catalog.Landings[0].Note));
			Assert.That(picker.Landings.Option(EastNinety).ClassListContains("fs-option-noted"), Is.True);
			Assert.That(picker.NoteLabel.text, Is.EqualTo(catalog.Landings[0].Note), "the selected landing's note under the fields");
			Assert.That(picker.NoteLabel.style.display.value, Is.EqualTo(DisplayStyle.Flex));
			picker.Landings.SelectedIndex = EastNinety;
			Assert.That(picker.NoteLabel.text, Is.EqualTo("Waiting for a crew answer on the landing side"));
			Assert.That(picker.Choice.StopId, Is.EqualTo("113"), "the stable id, not the row number");
		}

		[Test]
		public void TheSearch_NarrowsTheList_MovesTheSelectionToTheFirstMatch_AndClearingKeepsIt()
		{
			var picker = Bound(FakeScenarioCatalog.Harbor());
			picker.SetSearch("wall");
			Assert.That(picker.Landings.VisibleCount, Is.EqualTo(1));
			Assert.That(picker.Landings.IsVisible(Pier11), Is.True);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(Pier11), "the selection moved to the match");
			Assert.That(picker.Choice.StopId, Is.EqualTo("87"));
			Assert.That(ScenarioPicker.SessionChoice?.StopId, Is.EqualTo("87"), "remembered by id");
			Assert.That(picker.Berths.Count, Is.EqualTo(3), "the berths follow the landing");

			picker.SetSearch(string.Empty);
			Assert.That(picker.Landings.VisibleCount, Is.EqualTo(24), "every landing is back");
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(Pier11), "and the choice stays");
			Assert.That(picker.Landings.Count, Is.EqualTo(24), "nothing was dropped or re-sorted");
			Assert.That(picker.Landings.OptionText(0), Is.EqualTo("Hunters Point South"));

			picker.SetSearch("  PIER ");
			Assert.That(picker.Landings.VisibleCount, Is.EqualTo(4), "case-insensitive, trimmed: Pier 6, Pier 11, Yankee Pier, Pier 79");
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(Pier11), "a selection still shown stays");
		}

		[Test]
		public void ASearchWithNoMatch_KeepsTheChoice_AndSaysSo()
		{
			var picker = Bound(FakeScenarioCatalog.Harbor());
			picker.Landings.SelectedIndex = StGeorge;
			picker.SetSearch("zzz");
			Assert.That(picker.Landings.VisibleCount, Is.Zero);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(StGeorge));
			Assert.That(picker.Choice.StopId, Is.EqualTo("137"));
			Assert.That(picker.NoteLabel.text, Is.EqualTo("No landing name contains 'zzz'."));
			picker.SetSearch("george");
			Assert.That(picker.NoteLabel.text, Is.EqualTo("Waiting for a crew answer on the landing side"));
		}

		[Test]
		public void StepHomeAndEnd_SkipHiddenOptions()
		{
			var picker = Bound(FakeScenarioCatalog.Harbor());
			picker.SetSearch("pier");
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(2), "Atlantic Ave/BBP Pier 6 is the first match");
			picker.Landings.Step(1);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(Pier11));
			picker.Landings.Step(1);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(13), "Gov. Island/Yankee Pier");
			picker.Landings.Step(5);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(22), "Midtown West 39th St-Pier 79, the last match, clamped");
			picker.Landings.Step(-1);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(13));
			picker.Landings.Step(-10);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(2));
		}

		[Test]
		public void ARefusedLanding_ShowsTheCatalogsReason_AndAnotherLanding_Starts()
		{
			var catalog = FakeScenarioCatalog.Harbor();
			var picker = Bound(catalog);
			picker.Landings.SelectedIndex = EastNinety;
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo("East 90th St: waiting for a crew answer on the landing side."));
			Assert.That(catalog.Calls.Count, Is.EqualTo(1), "the launcher is not asked");

			picker.Landings.SelectedIndex = 0;
			Assert.That(picker.Problem, Is.Empty, "the refusal was about East 90th St");
			Assert.That(picker.TryStartRun(), Is.True, picker.Problem);
			Assert.That(catalog.Calls[1], Does.StartWith("create 4 "));
			Assert.That(catalog.LastStartedChoice?.StopId, Is.EqualTo("4"));
		}

		[Test]
		public void BerthNotes_ShowInTheRows_AndTheSelectedBerthsNote_UnderTheFields()
		{
			var catalog = FakeScenarioCatalog.Harbor();
			var picker = Bound(catalog);
			picker.Landings.SelectedIndex = Pier11;
			var berths = catalog.Landings[Pier11].Berths;
			Assert.That(picker.Berths.Count, Is.EqualTo(3));
			Assert.That(picker.Berths.OptionText(1), Is.EqualTo("North pontoon"));
			Assert.That(picker.Berths.OptionNote(1), Is.EqualTo(berths[1].Note));
			Assert.That(picker.NoteLabel.text, Is.EqualTo("Outer pontoon (east): " + berths[0].Note));
			picker.Berths.SelectedIndex = 2;
			Assert.That(picker.NoteLabel.text, Is.EqualTo("South pontoon: " + berths[2].Note));
			Assert.That(picker.Choice.BerthId, Is.EqualTo("enc:0226015B0512B198"));
			catalog.RefusedBerths["enc:0226015B0512B198"] = "the audit refuses it";
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo("Wall St/Pier 11, South pontoon: the audit refuses it."));
			picker.Berths.SelectedIndex = 0;
			Assert.That(picker.Problem, Is.Empty);
			Assert.That(picker.TryStartRun(), Is.True, picker.Problem);
		}

		[Test]
		public void TheSummary_NamesTheDaysCurrentMethod()
		{
			var picker = Bound(FakeScenarioCatalog.Harbor());
			Assert.That(picker.Summary.text, Does.EndWith(" · STATION BLEND CURRENTS"));
			Assert.That(picker.Summary.text, Does.Contain("Hunters Point South · T-class"));
			var slice = Bound(FakeScenarioCatalog.Slice());
			Assert.That(slice.Summary.text, Does.Not.Contain("CURRENTS"), "the slice's days name no method");
		}

		[Test]
		public void RebindingWhileFiltered_ClearsTheSearch_AndRestoresTheChoiceById()
		{
			var catalog = FakeScenarioCatalog.Harbor();
			var picker = Bound(catalog);
			picker.SetSearch("st. george");
			Assert.That(picker.Choice.StopId, Is.EqualTo("137"));
			picker.Bind(catalog, catalog);
			Assert.That(picker.Search.value, Is.Empty);
			Assert.That(picker.Landings.VisibleCount, Is.EqualTo(24));
			Assert.That(picker.Choice.StopId, Is.EqualTo("137"), "restored from the session by id");
			var other = Bound(catalog);
			Assert.That(other.Choice.StopId, Is.EqualTo("137"), "a new picker in the session too");
			Assert.That(other.Landings.SelectedIndex, Is.EqualTo(StGeorge));
		}

		[Test]
		public void TheSlicePicker_IsUnchanged_WithoutNotesOrAMethod()
		{
			var picker = Bound(FakeScenarioCatalog.Slice());
			Assert.That(Enumerable.Range(0, picker.Landings.Count).Select(picker.Landings.OptionNote), Is.All.Empty);
			Assert.That(Enumerable.Range(0, picker.Landings.Count).Any(i => picker.Landings.Option(i).ClassListContains("fs-option-noted")), Is.False, "one-line rows");
			Assert.That(picker.Landings.Option(0).text, Is.EqualTo("South Williamsburg"), "the button's own text, as before");
			Assert.That(picker.NoteLabel.style.display.value, Is.EqualTo(DisplayStyle.None));
			Assert.That(picker.Search.value, Is.Empty);
			Assert.That(picker.Landings.VisibleCount, Is.EqualTo(5));
		}
	}
}
