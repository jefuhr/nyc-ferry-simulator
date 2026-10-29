using System;
using System.Collections.Generic;
using FerrySim.Sim;
using FerrySim.Core.Traffic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// Chooses a run: a landing (and its berth, when it has several), one of the scenario days, a local start time and a
	/// vessel, then Start. Start asks the <see cref="IScenarioCatalog"/> to build the scenario and hands what it built to
	/// the <see cref="IScenarioLauncher"/>; a refusal from either shows inline as its one-sentence reason. It works with
	/// the mouse (click an option, a day, a step button, Start) and the keyboard (Tab between fields, Up and Down to
	/// change one, Enter to start, Escape to close; see <see cref="PickerField"/>). The landing list is searchable: typing
	/// in the box above it narrows the list to the names containing the text, and Up, Down and Enter work from the box too.
	/// A landing's or berth's note (its evidence, or the crew restriction that stops it starting) shows under its name in
	/// the list and, for the selected one, on the line under the fields; the catalog's refusal says the rest.
	/// <para>
	/// The choice is remembered for the session in <see cref="SessionChoice"/>, in memory only: nothing is written to
	/// disk. A picker built later in the session (another view, a reloaded scene) starts from it; a remembered option
	/// the catalog no longer offers falls back to that field's default.
	/// </para>
	/// </summary>
	public sealed class ScenarioPicker : VisualElement, IPaletteAware
	{
		/// <summary>The start time offered first: 09:00 local, as the existing scenarios start.</summary>
		public const int DefaultLocalMinutes = 9 * 60;

		public const string NoCatalog = "No scenario catalog is connected, so there is nothing to choose from.";
		public const string NoLauncher = "No scenario launcher is connected, so a run cannot start from here.";
		public const string NothingToChoose = "The catalog offers no landing, day or vessel to choose.";
		public const string UnexplainedRefusal = "The run could not start, and no reason was given.";
		public const string FailedToStart = "The run could not start because of an error; the log has the details.";

		private readonly List<string> _labels = new List<string>();
		private readonly List<string> _notes = new List<string>();
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private IReadOnlyList<ScenarioLanding> _landings = Array.Empty<ScenarioLanding>();
		private IReadOnlyList<ScenarioDay> _days = Array.Empty<ScenarioDay>();
		private IReadOnlyList<ScenarioVessel> _vessels = Array.Empty<ScenarioVessel>();
		private bool _restoring;
		private string _berthStop;
		private VisualElement _berthField;
		private IReadOnlyList<ScenarioBerth> _berths = Array.Empty<ScenarioBerth>();
		public const string NoTraffic = "Traffic isn't available for this day in this build.";
		private static readonly string[] VisibilityLabels = { "Clear", "2 nm", "½ nm", "¼ nm", "100 m" };
		private static readonly double[] VisibilityValues = { 0, 3704, 926, 463, 100 };

		public ScenarioPicker()
		{
			name = "scenario-picker";
			AddToClassList("fs-picker");
			AddToClassList(PaletteRoles.ConsoleBackground);
			pickingMode = PickingMode.Position;

			var header = Row("fs-picker-header");
			Title = PaletteRoles.Label("NEW RUN", PaletteRoles.Text, "fs-value-s", "fs-strong");
			Title.name = "picker-title";
			header.Add(Title);
			header.Add(PaletteRoles.Label("LOCAL TIME, 24 H · UP AND DOWN CHANGE A FIELD · ENTER STARTS · ESC CLOSES", PaletteRoles.Dim, "fs-caption", "fs-picker-hint"));
			header.Add(Row("fs-spacer"));
			CloseButton = Button("Close", () => CloseRequested?.Invoke(), "picker-close");
			header.Add(CloseButton);
			Add(header);

			var body = Row("fs-picker-body");
			Landings = new OptionList("picker-landings");
			Search = new TextField { name = "picker-search", isDelayed = false };
			Search.AddToClassList("fs-picker-search");
			Search.RegisterValueChangedCallback(evt => OnSearchChanged(evt.newValue));
			Search.RegisterCallback<KeyDownEvent>(OnSearchKeyDown, TrickleDown.TrickleDown);
			var landingBox = Field("LANDING · TYPE TO SEARCH", Landings, "fs-picker-landings");
			landingBox.Insert(1, Search);
			NoteLabel = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption", "fs-picker-note");
			NoteLabel.name = "picker-note";
			NoteLabel.style.whiteSpace = WhiteSpace.Normal;
			NoteLabel.style.display = DisplayStyle.None;
			landingBox.Add(NoteLabel);
			body.Add(landingBox);
			Days = new DayCalendar("picker-days");
			body.Add(Field("DAY", Days, "fs-picker-days"));
			var side = Column("fs-picker-side");
			Time = new TimeStepper("picker-time");
			side.Add(Field("START TIME (LOCAL)", Time, "fs-picker-time"));
			Vessels = new OptionList("picker-vessels");
			side.Add(Field("VESSEL", Vessels, "fs-picker-vessels"));
			body.Add(side);
			Add(body);

			var options = Column("fs-picker-field-box");
			options.style.width = 240;
			Berths = new OptionList("picker-berths");
			Berths.style.width = 214; Berths.style.height = 98;
			_berthField = Field("BERTH", Berths, "fs-picker-berths");
			options.Add(_berthField);
			var trafficFields = Column("fs-picker-field-box");
			trafficFields.style.width = 240;
			Traffic = new PickerToggle("picker-traffic", "Traffic");
			LeaveOut = new PickerToggle("picker-leave-out", "Leave out the ferry that ran this trip");
			LeaveOut.SetWithoutNotify(true);
			trafficFields.Add(Field("TRAFFIC", Traffic, "fs-picker-traffic"));
			trafficFields.Add(LeaveOut);
			TrafficReason = PaletteRoles.Label(string.Empty, PaletteRoles.Dim, "fs-caption");
			TrafficReason.style.whiteSpace = WhiteSpace.Normal;
			trafficFields.Add(TrafficReason);
			options.Add(trafficFields);
			Visibility = new OptionList("picker-visibility");
			Visibility.style.width = 90; Visibility.style.height = 140;
			Visibility.SetOptions(VisibilityLabels, 0);
			body.Add(options);
			body.Add(Field("VISIBILITY", Visibility, "fs-picker-visibility"));
			DepthWarning = PaletteRoles.Label(string.Empty, null, "fs-caption");
			DepthWarning.name = "picker-start-depth";
			DepthWarning.style.whiteSpace = WhiteSpace.Normal;
			DepthWarning.style.display = DisplayStyle.None;
			Add(DepthWarning);

			var footer = Row("fs-picker-footer");
			Summary = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-caption", "fs-picker-summary");
			Summary.name = "picker-summary";
			footer.Add(Summary);
			StartButton = Button("Start", () => TryStartRun(), "picker-start");
			StartButton.AddToClassList("fs-picker-start");
			footer.Add(StartButton);
			Add(footer);
			ProblemLabel = PaletteRoles.Label(string.Empty, null, "fs-caption", "fs-picker-problem");
			ProblemLabel.name = "picker-problem";
			ProblemLabel.style.display = DisplayStyle.None;
			Add(ProblemLabel);

			foreach (var field in new PickerField[] { Landings, Days, Time, Vessels, Berths, Traffic, LeaveOut, Visibility })
			{
				field.SubmitRequested += () => TryStartRun();
			}

			Berths.SelectionChanged += OnChoiceChanged;
			Traffic.ValueChanged += OnChoiceChanged;
			LeaveOut.ValueChanged += OnChoiceChanged;
			Visibility.SelectionChanged += OnChoiceChanged;
			Landings.SelectionChanged += OnChoiceChanged;
			Days.SelectionChanged += OnChoiceChanged;
			Time.MinutesChanged += OnChoiceChanged;
			Vessels.SelectionChanged += OnChoiceChanged;
			RegisterCallback<KeyDownEvent>(OnKeyDown);
			Bind(null, null);
		}

		/// <summary>
		/// The last choice in this session, kept in memory only. Every picker updates it as its choice changes and
		/// starts from it when bound. Null until something is chosen; tests and hosts may set or clear it.
		/// </summary>
		public static ScenarioChoice? SessionChoice { get; set; }

		/// <summary>Raised after a run started, with the choice it started from.</summary>
		public event Action<ScenarioChoice> Started;

		/// <summary>The Close button or Escape.</summary>
		public event Action CloseRequested;

		public Label Title { get; }

		public OptionList Landings { get; }

		/// <summary>The search box above the landing list: its text narrows the list to the names containing it.</summary>
		public TextField Search { get; }

		/// <summary>The selected landing's note (or the selected berth's, at a landing with several), under the landing list, two lines at most; hidden when there is none.</summary>
		public Label NoteLabel { get; }

		public DayCalendar Days { get; }

		public TimeStepper Time { get; }

		public OptionList Vessels { get; }
		public OptionList Berths { get; }
		public PickerToggle Traffic { get; }
		public PickerToggle LeaveOut { get; }
		public OptionList Visibility { get; }
		public Label TrafficReason { get; }
		public Label DepthWarning { get; }

		/// <summary>The choice in words: "MON 14 JUL 2025 · 09:00 EDT · Wall St/Pier 11 · T-class".</summary>
		public Label Summary { get; }

		public Button StartButton { get; }

		public Button CloseButton { get; }

		/// <summary>The one-sentence reason a run cannot start or did not start, under Start; hidden when empty.</summary>
		public Label ProblemLabel { get; }

		public string Problem { get; private set; } = string.Empty;

		public IScenarioCatalog Catalog { get; private set; }

		public IScenarioLauncher Launcher { get; private set; }

		/// <summary>True when there is a catalog, a launcher and something to choose in every field.</summary>
		public bool CanStart { get; private set; }

		/// <summary>The current choice. Its fields are null while the catalog offers nothing for them.</summary>
		public ScenarioChoice Choice => new ScenarioChoice(
			Landings.SelectedIndex >= 0 ? _landings[Landings.SelectedIndex].StopId : null,
			Days.SelectedIndex >= 0 ? _days[Days.SelectedIndex].Date : null,
			Time.Minutes,
			Vessels.SelectedIndex >= 0 ? _vessels[Vessels.SelectedIndex].Id : null,
			Berths.SelectedIndex >= 0 ? _berths[Berths.SelectedIndex].Id : null,
			Traffic.enabledSelf && Traffic.Value, LeaveOut.Value ? TrafficLeaveOut.Automatic : TrafficLeaveOut.Off,
			visibilityMetres: Visibility.SelectedIndex >= 0 ? VisibilityValues[Visibility.SelectedIndex] : 0);

		/// <summary>
		/// Reads the catalog's lists (again) and restores <see cref="SessionChoice"/> where the catalog still offers it.
		/// Either argument may be null; Start is then disabled and the problem line says why.
		/// </summary>
		public void Bind(IScenarioCatalog catalog, IScenarioLauncher launcher)
		{
			Catalog = catalog;
			Launcher = launcher;
			_landings = catalog?.Landings ?? Array.Empty<ScenarioLanding>();
			_days = catalog?.Days ?? Array.Empty<ScenarioDay>();
			_vessels = catalog?.Vessels ?? Array.Empty<ScenarioVessel>();
			var remembered = SessionChoice;
			_restoring = true;
			try
			{
				_labels.Clear();
				_notes.Clear();
				foreach (var landing in _landings)
				{
					_labels.Add(landing.Name);
					_notes.Add(landing.Note);
				}

				Search.SetValueWithoutNotify(string.Empty);
				Landings.SetOptions(_labels, _notes, IndexOf(_landings, remembered?.StopId, landing => landing.StopId));
				Days.SetDays(_days, IndexOf(_days, remembered?.Date, day => day.Date));
				Time.SetWithoutNotify(remembered?.LocalMinutes ?? DefaultLocalMinutes);
				_labels.Clear();
				foreach (var vessel in _vessels)
				{
					_labels.Add(vessel.DisplayName);
				}

				Vessels.SetOptions(_labels, IndexOf(_vessels, remembered?.VesselId, vessel => vessel.Id));
				_berthStop = null;
				RefreshBerths(remembered?.BerthId);
				Traffic.SetWithoutNotify(remembered?.Traffic ?? false);
				LeaveOut.SetWithoutNotify(!remembered.HasValue || remembered.Value.LeaveOut != TrafficLeaveOut.Off);
				Visibility.SetOptions(VisibilityLabels, Math.Max(0, Array.IndexOf(VisibilityValues, remembered?.VisibilityMetres ?? 0)));
				RefreshTraffic();
			}
			finally
			{
				_restoring = false;
			}

			CanStart = catalog != null && launcher != null && _landings.Count > 0 && _days.Count > 0 && _vessels.Count > 0;
			StartButton.SetEnabled(CanStart);
			ShowProblem(catalog == null ? NoCatalog : launcher == null ? NoLauncher : !CanStart ? NothingToChoose : string.Empty);
			UpdateSummary();
			UpdateNote();
			RefreshDepthWarning();
		}

		/// <summary>
		/// What Start does: <see cref="IScenarioCatalog.TryCreate"/>, then <see cref="IScenarioLauncher.TryStart"/> with the
		/// settings it made. True when the run started. A refusal shows inline; an exception from either side is logged
		/// and shown as one sentence, so a bug there cannot stop the UI.
		/// </summary>
		public bool TryStartRun()
		{
			if (!CanStart)
			{
				ShowProblem(Catalog == null ? NoCatalog : Launcher == null ? NoLauncher : NothingToChoose);
				return false;
			}

			var choice = Choice;
			try
			{
				if (!Catalog.TryCreate(choice, out var settings, out var problem))
				{
					ShowProblem(Sentence(problem));
					return false;
				}

				if (!Launcher.TryStart(settings, out problem))
				{
					ShowProblem(Sentence(problem));
					return false;
				}
			}
			catch (Exception error)
			{
				Debug.LogException(error);
				ShowProblem(FailedToStart);
				return false;
			}

			SessionChoice = choice;
			ShowProblem(string.Empty);
			Started?.Invoke(choice);
			return true;
		}

		/// <summary>Puts the keyboard focus on the first field, for when the picker opens.</summary>
		public void FocusFirstField()
		{
			if (Landings.Count > 0)
			{
				Landings.Focus();
			}
			else
			{
				CloseButton.Focus();
			}
		}

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			DebriefScreen.StyleButton(CloseButton, _palette);
			DebriefScreen.StyleButton(StartButton, _palette);
			// Start is the one action here, so it stands out like an engaged gear.
			StartButton.style.backgroundColor = _palette.Engaged;
			StartButton.style.color = _palette.EngagedText;
			PaletteRoles.SetBorderColor(StartButton, _palette.Engaged);
			ProblemLabel.style.color = _palette.Caution;
			DepthWarning.style.color = _palette.Caution;
			// The search box takes palette colors too, so nothing shows the theme's grey light at night.
			Search.style.color = _palette.Text;
			var input = Search.Q(className: TextField.inputUssClassName);
			if (input != null)
			{
				input.style.backgroundColor = _palette.Face;
				input.style.color = _palette.Text;
				PaletteRoles.SetBorderColor(input, _palette.Bezel);
			}
		}

		private void OnChoiceChanged()
		{
			if (_restoring)
			{
				return;
			}

			RefreshBerths(null);
			RefreshTraffic();
			if (CanStart)
			{
				SessionChoice = Choice;
				// A refusal was about the previous choice.
				ShowProblem(string.Empty);
			}

			UpdateSummary();
			UpdateNote();
			RefreshDepthWarning();
		}

		/// <summary>
		/// Narrows the landing list to the names containing <paramref name="text"/>, as typing in <see cref="Search"/> does
		/// (the box's own change event reaches the picker only inside a panel; hosts and tests can call this directly).
		/// </summary>
		public void SetSearch(string text)
		{
			text ??= string.Empty;
			if (Search.value != text)
			{
				Search.SetValueWithoutNotify(text);
			}

			// Filtering may move the selection, which refreshes everything else through OnChoiceChanged.
			Landings.Filter(text);
			UpdateNote();
		}

		private void OnSearchChanged(string text) => SetSearch(text);

		// The box is a text field, so the picker's field keys are handled here for it: the landing list follows.
		private void OnSearchKeyDown(KeyDownEvent evt)
		{
			switch (evt.keyCode)
			{
				case KeyCode.UpArrow:
					Landings.Step(-1);
					break;
				case KeyCode.DownArrow:
					Landings.Step(1);
					break;
				case KeyCode.PageUp:
					Landings.Step(-OptionList.PageSize);
					break;
				case KeyCode.PageDown:
					Landings.Step(OptionList.PageSize);
					break;
				case KeyCode.Return:
				case KeyCode.KeypadEnter:
					TryStartRun();
					break;
				case KeyCode.Escape:
					CloseRequested?.Invoke();
					break;
				default:
					return;
			}

			evt.StopPropagation();
			focusController?.IgnoreEvent(evt);
		}

		private void UpdateNote()
		{
			var text = string.Empty;
			if (Landings.Count > 0 && Landings.VisibleCount == 0)
			{
				text = "No landing name contains '" + Landings.FilterText.Trim() + "'.";
			}
			else if (Landings.SelectedIndex >= 0)
			{
				text = _landings[Landings.SelectedIndex].Note;
				if (_berths.Count > 1 && Berths.SelectedIndex >= 0 && _berths[Berths.SelectedIndex].Note.Length > 0)
				{
					text = _berths[Berths.SelectedIndex].Name + ": " + _berths[Berths.SelectedIndex].Note;
				}
			}

			PaletteRoles.SetText(NoteLabel, text);
			NoteLabel.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void RefreshBerths(string remembered)
		{
			var landing = Landings.SelectedIndex >= 0 ? _landings[Landings.SelectedIndex] : null;
			if (landing != null && _berthStop == landing.StopId) return;
			_berthStop = landing?.StopId;
			_berths = landing?.Berths ?? Array.Empty<ScenarioBerth>();
			_labels.Clear();
			_notes.Clear();
			foreach (var berth in _berths)
			{
				_labels.Add(berth.Name);
				_notes.Add(berth.Note);
			}

			Berths.SetOptions(_labels, _notes, IndexOf(_berths, remembered, b => b.Id));
			_berthField.style.display = _berths.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void RefreshTraffic()
		{
			var available = Days.SelectedIndex >= 0 && _days[Days.SelectedIndex].HasTraffic;
			Traffic.SetEnabled(available);
			if (!available) Traffic.SetWithoutNotify(false);
			TrafficReason.text = available ? string.Empty : NoTraffic;
			TrafficReason.style.display = available ? DisplayStyle.None : DisplayStyle.Flex;
			LeaveOut.SetEnabled(available && Traffic.Value);
		}

		private void RefreshDepthWarning()
		{
			var text = string.Empty;
			if (CanStart && Catalog is IScenarioStartDepthSource source)
			{
				try
				{
					if (source.TryGetStartDepth(Choice, out var depth) && depth.UnderKeel < 0.3)
						text = string.Format(CultureInfo.InvariantCulture, "{0}: depth {1:F2} m, draft {2:F2} m, under keel {3:F2} m.",
							depth.UnderKeel < 0 ? "Start refused: aground" : "Low under-keel clearance", depth.MinimumDepth, depth.MaximumDraft, depth.UnderKeel);
				}
				catch (Exception error) { Debug.LogWarning("[FerrySim] Start depth unavailable: " + error.Message); }
			}
			DepthWarning.text = text;
			DepthWarning.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void UpdateSummary()
		{
			var day = Days.SelectedIndex >= 0 ? _days[Days.SelectedIndex] : null;
			var zone = day != null ? EnvironmentFormat.Zone(EnvironmentKind.Pack, day.LocalUtcOffset) : string.Empty;
			Time.Zone = zone;
			if (!CanStart)
			{
				PaletteRoles.SetText(Summary, string.Empty);
				return;
			}

			var date = DayCalendar.TryParseDate(day.Date, out var parsed) ? EnvironmentFormat.Date(parsed) : day.Date;
			var text = date + " · " + Time.Text + " " + zone + " · " + _landings[Landings.SelectedIndex].Name + " · " +
				_vessels[Vessels.SelectedIndex].DisplayName;
			if (day.CurrentMethod.Length > 0)
			{
				text += " · " + day.CurrentMethod.ToUpperInvariant() + " CURRENTS";
			}

			PaletteRoles.SetText(Summary, day.WindMissing ? text + " · NO WIND DATA" : text);
		}

		private void ShowProblem(string problem)
		{
			Problem = problem;
			PaletteRoles.SetText(ProblemLabel, problem);
			ProblemLabel.style.display = problem.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
		}

		private void OnKeyDown(KeyDownEvent evt)
		{
			if (evt.keyCode == KeyCode.Escape)
			{
				CloseRequested?.Invoke();
				evt.StopPropagation();
			}
		}

		private static string Sentence(string problem) => string.IsNullOrWhiteSpace(problem) ? UnexplainedRefusal : problem.Trim();

		private static int IndexOf<T>(IReadOnlyList<T> items, string key, Func<T, string> keyOf)
		{
			if (key == null)
			{
				return 0;
			}

			for (var i = 0; i < items.Count; i++)
			{
				if (keyOf(items[i]) == key)
				{
					return i;
				}
			}

			return 0;
		}

		private static VisualElement Field(string caption, PickerField field, string className)
		{
			var column = Column("fs-picker-field-box");
			column.AddToClassList(className);
			column.Add(PaletteRoles.Label(caption, PaletteRoles.Dim, "fs-caption", "fs-picker-caption"));
			column.Add(field);
			return column;
		}

		private static Button Button(string text, Action onClick, string buttonName)
		{
			var button = new Button(onClick) { text = text, name = buttonName };
			button.AddToClassList("fs-button");
			return button;
		}

		private static VisualElement Row(string className)
		{
			var element = new VisualElement { pickingMode = PickingMode.Ignore };
			element.AddToClassList(className);
			return element;
		}

		private static VisualElement Column(string className) => Row(className);
	}
}
