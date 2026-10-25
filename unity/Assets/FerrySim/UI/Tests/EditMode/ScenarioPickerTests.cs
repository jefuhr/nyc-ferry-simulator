using System;
using System.Collections.Generic;
using FerrySim.Sim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// The scenario picker's logic, through its public members (PlayMode drives the same picker with the mouse and
	/// the keyboard). Guards against: Start not asking the catalog and then the launcher, with the settings the
	/// catalog made; a refusal hidden, empty or left showing after a success; an exception from either escaping;
	/// wrong choice values (stop id, date, minutes in 5-minute steps, vessel id); options re-sorted instead of the
	/// catalog's order; days without wind data not marked; Start enabled with nothing to start; and the choice
	/// forgotten between openings or views in one session.
	/// </summary>
	public class ScenarioPickerTests
	{
		[SetUp]
		public void ForgetTheSession() => ScenarioPicker.SessionChoice = null;

		[TearDown]
		public void ForgetTheSessionAgain() => ScenarioPicker.SessionChoice = null;

		private static FakeScenarioCatalog Catalog() => new FakeScenarioCatalog(
			new[] { new ScenarioLanding("8", "South Williamsburg"), new ScenarioLanding("20", "Dumbo/Fulton Ferry"), new ScenarioLanding("87", "Wall St/Pier 11") },
			new[]
			{
				new ScenarioDay("2025-01-13", "lower-east-river/2025-01-13", TimeSpan.FromHours(-5), false),
				new ScenarioDay("2025-01-14", "lower-east-river/2025-01-14", TimeSpan.FromHours(-5), true),
				new ScenarioDay("2025-07-14", "lower-east-river/2025-07-14", TimeSpan.FromHours(-4), false),
			},
			new[] { new ScenarioVessel("t-class", "T-class", "vessels/t-class.json"), new ScenarioVessel("k-class", "K-class", "vessels/k-class.json") });

		private static ScenarioPicker Bound(FakeScenarioCatalog catalog)
		{
			var picker = new ScenarioPicker();
			picker.Bind(catalog, catalog);
			return picker;
		}

		[Test]
		public void Options_FollowTheCatalogOrder_WithDefaults()
		{
			var picker = Bound(Catalog());

			Assert.That(picker.Landings.Count, Is.EqualTo(3));
			Assert.That(picker.Landings.OptionText(0), Is.EqualTo("South Williamsburg"), "the catalog's order, not alphabetical");
			Assert.That(picker.Landings.OptionText(2), Is.EqualTo("Wall St/Pier 11"));
			Assert.That(picker.Vessels.OptionText(0), Is.EqualTo("T-class"));
			Assert.That(picker.Days.Count, Is.EqualTo(3));
			var choice = picker.Choice;
			Assert.That(choice.StopId, Is.EqualTo("8"));
			Assert.That(choice.Date, Is.EqualTo("2025-01-13"));
			Assert.That(choice.LocalMinutes, Is.EqualTo(ScenarioPicker.DefaultLocalMinutes));
			Assert.That(ScenarioPicker.DefaultLocalMinutes, Is.EqualTo(9 * 60));
			Assert.That(choice.VesselId, Is.EqualTo("t-class"));
			Assert.That(picker.CanStart, Is.True);
			Assert.That(picker.Problem, Is.Empty);
		}

		[Test]
		public void Choice_FollowsEverySelection()
		{
			var picker = Bound(Catalog());
			picker.Landings.SelectedIndex = 2;
			picker.Days.SelectedIndex = 2;
			picker.Time.Minutes = 17 * 60 + 35;
			picker.Vessels.SelectedIndex = 1;

			var choice = picker.Choice;
			Assert.That(choice.StopId, Is.EqualTo("87"));
			Assert.That(choice.Date, Is.EqualTo("2025-07-14"));
			Assert.That(choice.LocalMinutes, Is.EqualTo(1055));
			Assert.That(choice.VesselId, Is.EqualTo("k-class"));
			Assert.That(picker.Time.Text, Is.EqualTo("17:35"));
			Assert.That(picker.Time.Zone, Is.EqualTo("EDT"), "the selected day's offset");
			StringAssert.Contains("MON 14 JUL 2025", picker.Summary.text);
			StringAssert.Contains("17:35 EDT", picker.Summary.text);
			StringAssert.Contains("Wall St/Pier 11", picker.Summary.text);
			StringAssert.Contains("K-class", picker.Summary.text);
		}

		[Test]
		public void Time_IsLocal24HourIn5MinuteSteps_ClampedToTheDay()
		{
			var picker = Bound(Catalog());
			picker.Time.Minutes = 0;
			Assert.That(picker.Time.Text, Is.EqualTo("00:00"));
			picker.Time.Step(-5);
			Assert.That(picker.Time.Minutes, Is.EqualTo(0), "no wrap into the previous day");
			picker.Time.Minutes = 23 * 60 + 55;
			picker.Time.Step(5);
			Assert.That(picker.Time.Minutes, Is.EqualTo(1435), "the last start is 23:55");
			picker.Time.Step(-60);
			Assert.That(picker.Time.Minutes, Is.EqualTo(1375));
			picker.Time.Minutes = 9 * 60 + 7;
			Assert.That(picker.Time.Minutes, Is.EqualTo(545), "snapped to the 5-minute step");
			picker.Time.Minutes = 5000;
			Assert.That(picker.Time.Minutes, Is.EqualTo(1435));
			picker.Time.Minutes = -40;
			Assert.That(picker.Time.Minutes, Is.EqualTo(0));
			Assert.That(picker.Days.SelectedIndex, Is.EqualTo(0));
			Assert.That(picker.Time.Zone, Is.EqualTo("EST"), "January");
		}

		[Test]
		public void DaysWithoutWindData_AreMarked_NotByColorAlone()
		{
			var picker = Bound(Catalog());
			Assert.That(picker.Days.CellText(0), Is.EqualTo("13"));
			Assert.That(picker.Days.CellText(1), Is.EqualTo("14" + DayCalendar.NoWindMark));
			Assert.That(picker.Days.Legend.text, Does.Contain(DayCalendar.NoWindMark).And.Contain("NO WIND DATA"));
			picker.Days.SelectedIndex = 1;
			StringAssert.Contains("NO WIND DATA", picker.Summary.text);
			picker.Days.SelectedIndex = 0;
			StringAssert.DoesNotContain("NO WIND", picker.Summary.text);
		}

		[Test]
		public void Calendar_LaysOutWeeksAsColumnsAndWeekdaysAsRows()
		{
			var picker = Bound(Catalog());
			var days = picker.Days;
			Assert.That(days.ColumnCount, Is.EqualTo(2), "two Monday-to-Sunday weeks");
			Assert.That(days.ColumnTitle(0), Is.EqualTo("JAN 2025"));
			Assert.That(days.ColumnTitle(1), Is.EqualTo("JUL 2025"));
			Assert.That(days.CellPosition(0), Is.EqualTo(new Vector2Int(0, 0)), "Monday 13 January");
			Assert.That(days.CellPosition(1), Is.EqualTo(new Vector2Int(0, 1)), "Tuesday 14 January, under it");
			Assert.That(days.CellPosition(2), Is.EqualTo(new Vector2Int(1, 0)), "Monday 14 July");
		}

		[Test]
		public void Calendar_PageKeysMoveAWeek_ArrowKeysADay()
		{
			var picker = Bound(Catalog());
			var days = picker.Days;
			days.SelectedIndex = 1;
			days.StepDays(1);
			Assert.That(days.SelectedIndex, Is.EqualTo(2), "the next day in the list");
			days.StepWeeks(-1);
			Assert.That(days.SelectedIndex, Is.EqualTo(0), "Monday of the week before");
			days.StepDays(-1);
			Assert.That(days.SelectedIndex, Is.EqualTo(0), "clamped at the first day");
			days.StepWeeks(1);
			Assert.That(days.SelectedIndex, Is.EqualTo(2));
			days.StepWeeks(1);
			Assert.That(days.SelectedIndex, Is.EqualTo(2), "clamped at the last week");
		}

		[Test]
		public void Start_AsksTheCatalogThenTheLauncher_WithTheSettingsTheCatalogMade()
		{
			var catalog = Catalog();
			var picker = Bound(catalog);
			picker.Landings.SelectedIndex = 1;
			picker.Days.SelectedIndex = 2;
			picker.Time.Minutes = 7 * 60 + 5;
			picker.Vessels.SelectedIndex = 1;
			ScenarioChoice? started = null;
			picker.Started += choice => started = choice;

			Assert.That(picker.TryStartRun(), Is.True);

			Assert.That(catalog.Calls, Is.EqualTo(new[] { "create 20 2025-07-14 425 k-class", "start " + catalog.LastCreatedSettings.name }));
			Assert.That(catalog.LastStartedSettings, Is.SameAs(catalog.LastCreatedSettings));
			Assert.That(started.HasValue, Is.True);
			Assert.That(started.Value.StopId, Is.EqualTo("20"));
			Assert.That(picker.Problem, Is.Empty);
			Assert.That(ScenarioPicker.SessionChoice.HasValue, Is.True);
			Assert.That(ScenarioPicker.SessionChoice.Value.LocalMinutes, Is.EqualTo(425));
		}

		[Test]
		public void ACatalogRefusal_ShowsInline_AndNothingStarts()
		{
			var catalog = Catalog();
			catalog.CreateProblem = "The time is outside the day's coverage.";
			var picker = Bound(catalog);
			var startedCount = 0;
			picker.Started += _ => startedCount++;

			Assert.That(picker.TryStartRun(), Is.False);

			Assert.That(picker.Problem, Is.EqualTo("The time is outside the day's coverage."));
			Assert.That(picker.ProblemLabel.text, Is.EqualTo(picker.Problem));
			Assert.That(picker.ProblemLabel.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex));
			Assert.That(catalog.Calls.Count, Is.EqualTo(1), "the launcher is not asked after a refusal");
			Assert.That(startedCount, Is.Zero);
		}

		[Test]
		public void ALauncherRefusal_ShowsInline()
		{
			var catalog = Catalog();
			catalog.StartProblem = "The vessel file is missing.";
			var picker = Bound(catalog);

			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo("The vessel file is missing."));
			Assert.That(catalog.Calls.Count, Is.EqualTo(2));
		}

		[Test]
		public void ARefusalWithoutAReason_StillSaysSomething_AndASuccessClearsIt()
		{
			var catalog = Catalog();
			catalog.StartProblem = string.Empty;
			var picker = Bound(catalog);
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo(ScenarioPicker.UnexplainedRefusal));

			catalog.StartProblem = null;
			Assert.That(picker.TryStartRun(), Is.True);
			Assert.That(picker.Problem, Is.Empty);
			Assert.That(picker.ProblemLabel.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None));
		}

		[Test]
		public void ChangingTheChoice_ClearsAnOldRefusal()
		{
			var catalog = Catalog();
			catalog.CreateProblem = "No.";
			var picker = Bound(catalog);
			picker.TryStartRun();
			Assert.That(picker.Problem, Is.EqualTo("No."));
			picker.Time.Step(5);
			Assert.That(picker.Problem, Is.Empty, "the refusal was about the previous choice");
		}

		[Test]
		public void AnExceptionFromTheCatalogOrLauncher_IsShownAndLogged_NotThrown()
		{
			var throwing = new ThrowingCatalog(Catalog());
			var picker = new ScenarioPicker();
			picker.Bind(throwing, throwing);

			throwing.ThrowOnCreate = true;
			LogAssert.Expect(LogType.Exception, "InvalidOperationException: catalog broke");
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo(ScenarioPicker.FailedToStart));

			throwing.ThrowOnCreate = false;
			throwing.ThrowOnStart = true;
			LogAssert.Expect(LogType.Exception, "InvalidOperationException: launcher broke");
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo(ScenarioPicker.FailedToStart));
		}

		[Test]
		public void StartIsDisabled_WithNothingToStart_AndSaysWhy()
		{
			var picker = new ScenarioPicker();
			picker.Bind(null, null);
			Assert.That(picker.CanStart, Is.False);
			Assert.That(picker.StartButton.enabledSelf, Is.False);
			Assert.That(picker.TryStartRun(), Is.False);
			Assert.That(picker.Problem, Is.EqualTo(ScenarioPicker.NoCatalog));

			var catalog = Catalog();
			picker.Bind(catalog, null);
			Assert.That(picker.CanStart, Is.False);
			Assert.That(picker.Problem, Is.EqualTo(ScenarioPicker.NoLauncher));
			Assert.That(catalog.Calls, Is.Empty);

			var empty = new FakeScenarioCatalog(new ScenarioLanding[0], catalog.Days, catalog.Vessels);
			picker.Bind(empty, empty);
			Assert.That(picker.CanStart, Is.False);
			Assert.That(picker.Problem, Is.EqualTo(ScenarioPicker.NothingToChoose));

			picker.Bind(catalog, catalog);
			Assert.That(picker.CanStart, Is.True);
			Assert.That(picker.StartButton.enabledSelf, Is.True);
			Assert.That(picker.Problem, Is.Empty);
		}

		[Test]
		public void TheChoice_IsRememberedForTheSession_AcrossPickers()
		{
			var catalog = Catalog();
			var first = Bound(catalog);
			first.Landings.SelectedIndex = 2;
			first.Days.SelectedIndex = 1;
			first.Time.Minutes = 6 * 60 + 30;
			first.Vessels.SelectedIndex = 1;

			// A new view (the host was disabled and enabled, or the scene reloaded) builds a new picker.
			var second = Bound(catalog);
			var choice = second.Choice;
			Assert.That(choice.StopId, Is.EqualTo("87"));
			Assert.That(choice.Date, Is.EqualTo("2025-01-14"));
			Assert.That(choice.LocalMinutes, Is.EqualTo(390));
			Assert.That(choice.VesselId, Is.EqualTo("k-class"));
		}

		[Test]
		public void ARememberedOptionTheCatalogNoLongerOffers_FallsBackToTheDefault_ForThatFieldOnly()
		{
			ScenarioPicker.SessionChoice = new ScenarioChoice("999", "2025-01-14", 600, "k-class");
			var picker = Bound(Catalog());
			var choice = picker.Choice;
			Assert.That(choice.StopId, Is.EqualTo("8"), "unknown landing: the first one");
			Assert.That(choice.Date, Is.EqualTo("2025-01-14"));
			Assert.That(choice.LocalMinutes, Is.EqualTo(600));
			Assert.That(choice.VesselId, Is.EqualTo("k-class"));
		}

		[Test]
		public void TheCatalogIsReadAgain_OnEveryBind()
		{
			var catalog = Catalog();
			var picker = Bound(catalog);
			var bigger = new FakeScenarioCatalog(
				new List<ScenarioLanding>(catalog.Landings) { new ScenarioLanding("114", "Stuyvesant Cove") }, catalog.Days, catalog.Vessels);
			picker.Bind(bigger, bigger);
			Assert.That(picker.Landings.Count, Is.EqualTo(4));
		}

		/// <summary>A catalog and launcher that fail the way a bug would: by throwing.</summary>
		private sealed class ThrowingCatalog : IScenarioCatalog, IScenarioLauncher
		{
			private readonly FakeScenarioCatalog _inner;

			public ThrowingCatalog(FakeScenarioCatalog inner)
			{
				_inner = inner;
			}

			public bool ThrowOnCreate { get; set; }

			public bool ThrowOnStart { get; set; }

			public IReadOnlyList<ScenarioLanding> Landings => _inner.Landings;

			public IReadOnlyList<ScenarioDay> Days => _inner.Days;

			public IReadOnlyList<ScenarioVessel> Vessels => _inner.Vessels;

			public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem)
			{
				if (ThrowOnCreate)
				{
					throw new InvalidOperationException("catalog broke");
				}

				return _inner.TryCreate(choice, out settings, out problem);
			}

			public bool TryStart(ScenarioSettings settings, out string problem)
			{
				if (ThrowOnStart)
				{
					throw new InvalidOperationException("launcher broke");
				}

				return _inner.TryStart(settings, out problem);
			}
		}
	}
}
