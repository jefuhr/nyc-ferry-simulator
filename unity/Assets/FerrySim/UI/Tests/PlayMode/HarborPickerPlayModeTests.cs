using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using FerrySim.Sim;
using Object = UnityEngine.Object;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// The picker as rendered on the harbor's catalog shape (24 landings with notes, three berths at Pier 11, refused
	/// landings), at the 1280x720 minimum and at 1440p, by day and by night. Failure modes guarded: the taller list, the
	/// search box or the note line pushing the picker into the panel or the HUD; the selected landing scrolled out of view
	/// after a selection or a filter; a long name spilling out of its row or the picker; typing in the search box not
	/// narrowing the list, not selecting the match, or Enter from the box not starting; a refusal not shown as the
	/// catalog's sentence, or a retry at another landing not starting; and a host's pause and helm hold dropped while the
	/// picker is open through a vessel change and a rebind, or kept after the started run closes it. Screenshots go to
	/// artifacts/unity/ui-picker-harbor-*.png.
	/// </summary>
	public class HarborPickerPlayModeTests
	{
		private const int EastNinety = 15;
		private const int StGeorge = 21;
		private const int Pier11 = 10;

		private UITestRig _rig;
		private GameObject _environmentHost;

		[SetUp]
		public void ForgetTheSession() => ScenarioPicker.SessionChoice = null;

		[TearDown]
		public void TearDown()
		{
			ScenarioPicker.SessionChoice = null;
			_rig?.Dispose();
			_rig = null;
			if (_environmentHost != null)
			{
				Object.Destroy(_environmentHost);
			}
		}

		private FakeScenarioCatalog Open(int width = 1280, int height = 720, bool withEnvironment = false, FakeScenarioCatalog catalog = null)
		{
			_rig = new UITestRig(width, height);
			Assert.That(_rig.Source.TryLoadCsv(UITestRig.SamplePath), Is.True, _rig.Source.LoadError);
			_rig.Source.Seek(46.0);
			catalog ??= FakeScenarioCatalog.Harbor();
			StubEnvironmentRunner environment = null;
			if (withEnvironment)
			{
				_environmentHost = new GameObject("environment readout (test)");
				environment = _environmentHost.AddComponent<StubEnvironmentRunner>();
				environment.HasEnvironment = true;
				environment.Environment = TestReadouts.Pack(0.9, 0.35, 1.8, 47.0, 12.0, 250.0);
			}

			_rig.UI.ConnectScenario(environment, catalog, catalog);
			return catalog;
		}

		[UnityTest]
		public IEnumerator ThePicker_FitsAboveThePanelAndTheHud_WithTheSelectedLandingInView_InDayAndNight_At720pAnd1440p()
		{
			foreach (var (width, height, resolution) in new[] { (1280, 720, "720p"), (2560, 1440, "1440p") })
			{
				_rig?.Dispose();
				if (_environmentHost != null)
				{
					Object.Destroy(_environmentHost);
				}

				Open(width, height, withEnvironment: true);
				yield return UITestRig.Frames();
				_rig.View.PickerVisible = true;
				var picker = _rig.View.Picker;
				picker.Days.SelectedIndex = 9;
				picker.Landings.SelectedIndex = StGeorge;
				yield return UITestRig.Frames();
				Assert.That(picker.NoteLabel.text, Is.EqualTo("Waiting for a crew answer on the landing side"));
				foreach (var (view, panelShown) in new[] { ("panel", true), ("hud", false) })
				{
					_rig.View.PanelVisible = panelShown;
					_rig.View.HudVisible = !panelShown;
					foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
					{
						_rig.SetPalette(palette);
						yield return UITestRig.Frames();
						var root = _rig.View.worldBound;
						Assert.That(Contains(root, picker.worldBound), Is.True, $"{resolution}: the picker {picker.worldBound} fits on screen");
						var below = panelShown ? _rig.View.Panel.worldBound : _rig.View.Hud.worldBound;
						Assert.That(picker.worldBound.yMax, Is.LessThanOrEqualTo(below.yMin + 0.5f), $"{resolution}: the picker {picker.worldBound} reaches into the {view} {below}");
						foreach (var part in new VisualElement[] { picker.Search, picker.Landings, picker.Days, picker.Time, picker.Vessels, picker.StartButton, picker.CloseButton, picker.Days.Legend, picker.NoteLabel, picker.Summary, picker.Berths })
						{
							Assert.That(Contains(picker.worldBound, part.worldBound), Is.True, $"{resolution}: {part.name} {part.worldBound} spills out of the picker");
						}

						Assert.That(Contains(picker.Days.worldBound, picker.Days.Cell(27).worldBound), Is.True, $"{resolution}: the last week is visible without scrolling");
						var viewport = picker.Landings.Scroll.contentViewport.worldBound;
						Assert.That(Contains(viewport, picker.Landings.Option(StGeorge).worldBound), Is.True,
							$"{resolution}: the selected landing {picker.Landings.Option(StGeorge).worldBound} is scrolled into view {viewport}");
						Object.Destroy(_rig.Screenshot($"ui-picker-harbor-{view}-{palette.ToString().ToLowerInvariant()}-{resolution}.png"));
					}
				}
			}
		}

		[UnityTest]
		public IEnumerator LongNames_StayInsideTheirRowsAndThePicker()
		{
			var harbor = FakeScenarioCatalog.Harbor();
			const string longName = "An Unusually Long Landing Name For The Narrowest Window There Is";
			var landings = new List<ScenarioLanding>(harbor.Landings)
			{
				new ScenarioLanding("900", longName, new[]
				{
					new ScenarioBerth("long:a", "The first pontoon with a long name of its own", "A berth note long enough to need clipping in its row and wrapping under the fields of the picker"),
					new ScenarioBerth("long:b", "The second pontoon with a long name of its own", "Waiting for a crew answer on the landing side"),
				}, "A landing note long enough to need clipping in its row and wrapping under the fields of the picker, at the minimum window size"),
			};
			var catalog = new FakeScenarioCatalog(landings, harbor.Days, harbor.Vessels);
			Open(catalog: catalog);
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			var picker = _rig.View.Picker;
			picker.Landings.SelectedIndex = 24;
			yield return UITestRig.Frames();
			Assert.That(picker.Berths.Count, Is.EqualTo(2));
			Assert.That(_rig.View.PanelVisible, Is.True);
			Assert.That(picker.worldBound.yMax, Is.LessThanOrEqualTo(_rig.View.Panel.worldBound.yMin + 0.5f), $"the picker {picker.worldBound} reaches into the panel");
			Assert.That(Contains(_rig.View.worldBound, picker.worldBound), Is.True, "the picker fits the 1280x720 design");
			var list = picker.Landings.worldBound;
			var option = picker.Landings.Option(24);
			Assert.That(option.worldBound.xMax, Is.LessThanOrEqualTo(list.xMax + 0.5f), $"the long name's row {option.worldBound} spills out of the list {list}");
			foreach (var child in option.Children())
			{
				Assert.That(child.worldBound.xMax, Is.LessThanOrEqualTo(list.xMax + 0.5f), $"{child.name} {child.worldBound} spills out of the list {list}");
			}

			var berth = picker.Berths.Option(0);
			Assert.That(berth.worldBound.xMax, Is.LessThanOrEqualTo(picker.Berths.worldBound.xMax + 0.5f), "the long berth name's row spills out of the berth list");
			Assert.That(Contains(picker.worldBound, picker.NoteLabel.worldBound), Is.True, "the wrapped note stays inside the picker");
			Assert.That(Contains(picker.worldBound, picker.Summary.worldBound), Is.True, "the wrapped summary stays inside the picker");
			Object.Destroy(_rig.Screenshot("ui-picker-harbor-long-names-day-720p.png"));
		}

		[UnityTest]
		public IEnumerator TypingInTheSearchBox_NarrowsTheList_AndEnterStartsThere()
		{
			var catalog = Open();
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;
			UIInput.Click(picker.Search);
			yield return null;
			var focused = UIInput.Focused(picker);
			Assert.That(picker.Search.Contains(focused) || ReferenceEquals(focused, picker.Search), Is.True, "clicking the box focuses it");
			foreach (var character in "wall")
			{
				using (var evt = KeyDownEvent.GetPooled(character, KeyCode.None, EventModifiers.None))
				{
					UIInput.Focused(picker).SendEvent(evt);
				}
			}

			yield return null;
			Assert.That(picker.Search.value, Is.EqualTo("wall"), "the typed text reaches the box");
			Assert.That(picker.Landings.VisibleCount, Is.EqualTo(1));
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(Pier11));
			Assert.That(picker.Choice.StopId, Is.EqualTo("87"));
			yield return UITestRig.Frames();
			var viewport = picker.Landings.Scroll.contentViewport.worldBound;
			Assert.That(Contains(viewport, picker.Landings.Option(Pier11).worldBound), Is.True, "the match is in view");
			UIInput.Key(picker, KeyCode.DownArrow);
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(Pier11), "Down from the box stays on the only match");
			Assert.That(catalog.Calls, Is.Empty);

			UIInput.Key(picker, KeyCode.Return);
			yield return UITestRig.Frames(2);
			Assert.That(catalog.Calls.Count, Is.EqualTo(2), string.Join(" | ", catalog.Calls));
			Assert.That(catalog.Calls[0], Does.StartWith("create 87 "), "Enter from the search box starts the matched landing");
			Assert.That(_rig.View.PickerVisible, Is.False);

			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			Assert.That(_rig.View.Picker.Search.value, Is.Empty, "opened again, the search is clear");
			Assert.That(_rig.View.Picker.Landings.VisibleCount, Is.EqualTo(24));
			Assert.That(_rig.View.Picker.Choice.StopId, Is.EqualTo("87"), "and the choice is remembered by id");
		}

		[UnityTest]
		public IEnumerator ARefusedLanding_ShowsItsReason_AndAnotherLanding_Starts_InDayAndNightScreenshots()
		{
			var catalog = Open();
			yield return UITestRig.Frames();
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;
			// East 90th St is below the list's first rows: scroll to it, as a person would, then click it.
			picker.Landings.Scroll.ScrollTo(picker.Landings.Option(EastNinety));
			yield return UITestRig.Frames();
			var viewport = picker.Landings.Scroll.contentViewport.worldBound;
			Assert.That(Contains(viewport, picker.Landings.Option(EastNinety).worldBound), Is.True, "scrolled into view");
			UIInput.Click(picker.Landings.Option(EastNinety));
			yield return UITestRig.Frames();
			Assert.That(picker.Landings.SelectedIndex, Is.EqualTo(EastNinety));
			UIInput.Click(picker.StartButton);
			yield return UITestRig.Frames(2);
			Assert.That(_rig.View.PickerVisible, Is.True, "a refused run leaves the choice on screen");
			Assert.That(picker.ProblemLabel.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
			Assert.That(picker.ProblemLabel.text, Is.EqualTo("East 90th St: waiting for a crew answer on the landing side."));
			Assert.That(Contains(picker.worldBound, picker.ProblemLabel.worldBound), Is.True, "inside the picker");
			Assert.That(picker.worldBound.yMax, Is.LessThanOrEqualTo(_rig.View.Panel.worldBound.yMin + 0.5f), "with the reason shown, the picker stays clear of the panel");
			foreach (var palette in new[] { PaletteKind.Day, PaletteKind.Night })
			{
				_rig.SetPalette(palette);
				yield return UITestRig.Frames();
				Object.Destroy(_rig.Screenshot($"ui-picker-harbor-refusal-{palette.ToString().ToLowerInvariant()}.png"));
			}

			picker.Landings.Scroll.ScrollTo(picker.Landings.Option(0));
			yield return UITestRig.Frames();
			UIInput.Click(picker.Landings.Option(0));
			yield return UITestRig.Frames(2);
			Assert.That(picker.Landings.SelectedIndex, Is.Zero, "the click reached the scrolled-to landing");
			Assert.That(picker.ProblemLabel.resolvedStyle.display, Is.EqualTo(DisplayStyle.None), "another landing clears the refusal");
			UIInput.Click(picker.StartButton);
			yield return UITestRig.Frames(2);
			Assert.That(catalog.LastStartedChoice?.StopId, Is.EqualTo("4"), string.Join(" | ", catalog.Calls));
			Assert.That(_rig.View.PickerVisible, Is.False, "the retry started and closed the picker");

			// The tallest the card gets: an aground start shows the depth line and, on Start, the catalog's refusal under it.
			var aground = new AgroundCatalog(catalog);
			_rig.UI.ConnectScenario(null, aground, aground);
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			picker = _rig.View.Picker;
			Assert.That(picker.DepthWarning.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
			Assert.That(picker.DepthWarning.text, Does.StartWith("Start refused: aground"));
			UIInput.Click(picker.StartButton);
			yield return UITestRig.Frames(2);
			Assert.That(picker.ProblemLabel.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
			Assert.That(picker.ProblemLabel.text, Does.StartWith("This start would be aground"));
			Assert.That(picker.worldBound.yMax, Is.LessThanOrEqualTo(_rig.View.Panel.worldBound.yMin + 0.5f), $"with the depth line and the refusal shown, the picker {picker.worldBound} reaches into the panel");
			Assert.That(Contains(picker.worldBound, picker.ProblemLabel.worldBound) && Contains(picker.worldBound, picker.DepthWarning.worldBound), Is.True);
			Object.Destroy(_rig.Screenshot("ui-picker-harbor-aground-day-720p.png"));
		}

		/// <summary>The harbor fake with every start aground: the depth preview warns and the catalog refuses, as the real one does.</summary>
		private sealed class AgroundCatalog : IScenarioCatalog, IScenarioStartDepthSource, IScenarioLauncher
		{
			private readonly FakeScenarioCatalog _inner;

			public AgroundCatalog(FakeScenarioCatalog inner) => _inner = inner;

			public IReadOnlyList<ScenarioLanding> Landings => _inner.Landings;

			public IReadOnlyList<ScenarioDay> Days => _inner.Days;

			public IReadOnlyList<ScenarioVessel> Vessels => _inner.Vessels;

			public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem)
			{
				settings = null;
				problem = "This start would be aground: minimum depth is 1.50 m and vessel draft is 1.90 m.";
				return false;
			}

			public bool TryGetStartDepth(ScenarioChoice choice, out ScenarioStartDepth depth)
			{
				depth = new ScenarioStartDepth(1.5, 1.9);
				return true;
			}

			public bool TryStart(ScenarioSettings settings, out string problem) => _inner.TryStart(settings, out problem);
		}

		/// <summary>
		/// What a scene host does on <see cref="WheelhouseView.PickerVisibilityChanged"/>: pauses the run and holds the helm
		/// while the picker is open. The slice's bootstrap is the real one; the harbor's composition root binds the same way.
		/// </summary>
		private sealed class HoldingHost
		{
			public HoldingHost(WheelhouseView view) => view.PickerVisibilityChanged += OnPickerVisibilityChanged;

			public bool Paused { get; private set; }

			public bool HelmHeld { get; private set; }

			public int Changes { get; private set; }

			private void OnPickerVisibilityChanged(bool visible)
			{
				Changes++;
				Paused = visible;
				HelmHeld = visible;
			}
		}

		[UnityTest]
		public IEnumerator AHost_HoldsTheRunAndTheHelm_ThroughAVesselChangeAndARebind_UntilTheStartedRunClosesThePicker()
		{
			var catalog = Open();
			yield return UITestRig.Frames();
			var host = new HoldingHost(_rig.View);
			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			var picker = _rig.View.Picker;
			Assert.That(host.Paused && host.HelmHeld, Is.True, "held while the picker is open");
			Assert.That(host.Changes, Is.EqualTo(1));

			UIInput.Click(picker.Vessels.Option(1));
			yield return null;
			Assert.That(picker.Choice.VesselId, Is.EqualTo("k-class"));
			Assert.That(host.Paused && host.HelmHeld, Is.True, "a vessel change does not give the helm back");
			Assert.That(host.Changes, Is.EqualTo(1));

			// The host rebinds (a new catalog instance after a vessel or mapper change): the picker stays open and held.
			var rebound = FakeScenarioCatalog.Harbor();
			_rig.UI.ConnectScenario(null, rebound, rebound);
			yield return UITestRig.Frames();
			Assert.That(_rig.View.PickerVisible, Is.True);
			Assert.That(host.Paused && host.HelmHeld, Is.True, "a rebind while open keeps the holds");
			Assert.That(host.Changes, Is.EqualTo(1), "no visibility change was raised by the rebind");
			Assert.That(picker.Choice.VesselId, Is.EqualTo("k-class"), "the choice survived the rebind");

			UIInput.Click(picker.StartButton);
			yield return UITestRig.Frames(2);
			Assert.That(rebound.LastStartedChoice?.VesselId, Is.EqualTo("k-class"), string.Join(" | ", rebound.Calls));
			Assert.That(_rig.View.PickerVisible, Is.False);
			Assert.That(host.Paused || host.HelmHeld, Is.False, "the started run gets the helm and the clock back");
			Assert.That(host.Changes, Is.EqualTo(2));

			_rig.View.PickerVisible = true;
			yield return UITestRig.Frames();
			Assert.That(host.Paused && host.HelmHeld, Is.True, "opened again, held again");
			Assert.That(host.Changes, Is.EqualTo(3));
			Assert.That(catalog.Calls, Is.Empty, "the first catalog was not asked after the rebind");
		}

		private static bool Contains(Rect outer, Rect inner) =>
			inner.xMin >= outer.xMin - 0.5f && inner.xMax <= outer.xMax + 0.5f && inner.yMin >= outer.yMin - 0.5f && inner.yMax <= outer.yMax + 0.5f;
	}
}
