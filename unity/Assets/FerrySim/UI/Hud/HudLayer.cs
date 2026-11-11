using System;
using System.Collections.Generic;
using FerrySim.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The player's HUD over the 3D view: places every widget the layout in force shows (<see cref="HudController.Current"/>),
	/// refreshes the shown ones each frame, and runs edit mode. A widget is anchored by percentages and a margin, so a
	/// placement survives any screen size without measuring; scale and drags use the transform (translate and scale with
	/// a dynamic-transform usage hint), and layout properties are written only when a drop or a command commits a placement,
	/// never per frame. The classic panel is not a child: it stays where the view puts it and the layer reports whether the
	/// layout shows it (<see cref="ClassicPanelShown"/>).
	/// </summary>
	public sealed class HudLayer : VisualElement, IPaletteAware, IDisposable
	{
		private readonly HudController _hud;
		private readonly WheelhousePanel _panel;
		private readonly Dictionary<string, HudWidget> _widgets = new Dictionary<string, HudWidget>();
		private readonly List<HudWidget> _shown = new List<HudWidget>();
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private string _dragging;
		private int _dragPointer = -1;
		private bool _disposed;

		public HudLayer(HudController hud, WheelhousePanel panel)
		{
			_hud = hud ?? throw new ArgumentNullException(nameof(hud));
			_panel = panel;
			name = "hud-layer";
			AddToClassList("fs-layer");
			AddToClassList("fs-hud-layer");
			pickingMode = PickingMode.Ignore;
			EditBar = new HudEditBar(hud);
			EditBar.DoneRequested += _hud.EndEdit;
			EditBar.Commanded += RefreshEditChrome;
			Add(EditBar);
			EditBar.style.display = DisplayStyle.None;
			_hud.LayoutChanged += OnLayoutChanged;
			_hud.EditModeChanged += OnEditModeChanged;
			ApplyLayout();
		}

		/// <summary>Raised when the layout starts or stops showing the classic panel.</summary>
		public event Action<bool> ClassicPanelShownChanged;

		public HudEditBar EditBar { get; }

		/// <summary>Disconnects this layer from its controller and edit bar, releasing any captured drag pointer.</summary>
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_hud.LayoutChanged -= OnLayoutChanged;
			_hud.EditModeChanged -= OnEditModeChanged;
			EditBar.DoneRequested -= _hud.EndEdit;
			EditBar.Commanded -= RefreshEditChrome;
			ClassicPanelShownChanged = null;
			var overlay = Widget(_dragging)?.EditOverlay;
			_dragging = null;
			_hud.Session?.CancelDrag();
			if (overlay != null && overlay.HasPointerCapture(_dragPointer))
			{
				overlay.ReleasePointer(_dragPointer);
			}

			_dragPointer = -1;
			ScreenSource = null;
		}

		/// <summary>The pop-outs' pictures; null shows NO SIGNAL on every pop-out.</summary>
		public IHudScreenSource ScreenSource { get; set; }

		public bool ClassicPanelShown { get; private set; }

		/// <summary>The widgets shown now, in drawing order, not counting the classic panel.</summary>
		public IReadOnlyList<HudWidget> ShownWidgets => _shown;

		/// <summary>A widget that has been made (it was shown at some point), or null.</summary>
		public HudWidget Widget(string id) => id != null && _widgets.TryGetValue(id, out var widget) ? widget : null;

		/// <summary>The frame's size in reference pixels: the laid-out size, or 1280×720 before layout.</summary>
		public Vector2 FrameSize
		{
			get
			{
				var size = layout.size;
				return size.x > 1f && size.y > 1f && !float.IsNaN(size.x) && !float.IsNaN(size.y) ? size : HudGeometry.ReferenceScreen;
			}
		}

		/// <summary>A widget's size at scale 1: as laid out once it has been, else the registry's.</summary>
		public Vector2 SizeOf(string id)
		{
			var widget = Widget(id);
			if (widget != null && !(widget is ClassicPanelWidget))
			{
				var size = widget.Element.layout.size;
				if (size.x > 1f && size.y > 1f && !float.IsNaN(size.x) && !float.IsNaN(size.y))
				{
					return size;
				}
			}

			return _hud.Registry.Find(id)?.Size ?? Vector2.zero;
		}

		public void ApplyPalette(InstrumentPalette palette)
		{
			// Widgets are reached by PaletteRoles.Apply walking the tree; this keeps the palette for widgets made later.
			_palette = palette ?? InstrumentPalette.Day;
			RefreshEditChrome();
		}

		/// <summary>Refreshes every shown widget. The panel must have been refreshed this frame first.</summary>
		public void Refresh(IVesselTelemetrySource source, DistanceUnit unit, double thresholdMetres, double realTime)
		{
			if (_shown.Count == 0)
			{
				return;
			}

			var frame = new HudFrame(source, _panel, unit, thresholdMetres, realTime, ScreenSource);
			for (var i = 0; i < _shown.Count; i++)
			{
				_shown[i].Refresh(frame);
			}
		}

		/// <summary>Places every widget from the layout in force.</summary>
		public void ApplyLayout()
		{
			var layout = _hud.Current;
			_shown.Clear();
			foreach (var placement in layout.Placements)
			{
				Place(Effective(placement));
			}

			UpdateClassic(_hud.IsShown(HudWidgetIds.ClassicPanel));
			RefreshEditChrome();
		}

		private void OnLayoutChanged(string id)
		{
			if (_disposed)
			{
				return;
			}

			if (id == null)
			{
				ApplyLayout();
				return;
			}

			var layout = _hud.Current;
			if (layout.TryGet(id, out var placement))
			{
				_shown.Clear();
				foreach (var item in layout.Placements)
				{
					if (item.Id == id)
					{
						Place(Effective(placement));
					}
					else if (_hud.IsShown(item.Id) && item.Id != HudWidgetIds.ClassicPanel && Widget(item.Id) != null)
					{
						_shown.Add(Widget(item.Id));
					}
				}

				UpdateClassic(_hud.IsShown(HudWidgetIds.ClassicPanel));
			}

			RefreshEditChrome();
		}

		/// <summary>The placement as drawn: the layout's place, with the controller's effective visibility (session toggles).</summary>
		private HudPlacement Effective(HudPlacement placement)
		{
			var shown = _hud.IsShown(placement.Id);
			return shown == placement.Visible ? placement : placement.WithVisible(shown);
		}

		private void Place(HudPlacement placement)
		{
			if (placement.Id == HudWidgetIds.ClassicPanel)
			{
				return;
			}

			var widget = Widget(placement.Id);
			if (!placement.Visible)
			{
				if (widget != null)
				{
					widget.Element.style.display = DisplayStyle.None;
				}

				return;
			}

			widget ??= Make(placement.Id);
			if (widget == null)
			{
				return;
			}

			var element = widget.Element;
			var fraction = HudAnchors.Fraction(placement.Anchor);
			element.style.display = DisplayStyle.Flex;
			element.style.left = Length.Percent(fraction.x * 100f);
			element.style.top = Length.Percent(fraction.y * 100f);
			element.style.marginLeft = placement.Offset.x;
			element.style.marginTop = placement.Offset.y;
			element.style.translate = new Translate(Length.Percent(-fraction.x * 100f), Length.Percent(-fraction.y * 100f));
			element.style.transformOrigin = new TransformOrigin(Length.Percent(fraction.x * 100f), Length.Percent(fraction.y * 100f));
			element.style.scale = new Scale(new Vector3(placement.Scale, placement.Scale, 1f));
			_shown.Add(widget);
		}

		private HudWidget Make(string id)
		{
			var definition = _hud.Registry.Find(id);
			var widget = definition != null ? HudWidget.Create(definition, _panel) : null;
			if (widget == null)
			{
				return null;
			}

			_widgets[id] = widget;
			var element = widget.Element;
			// Absolute, so a widget never moves another; drawn in registry order, under the edit bar.
			element.style.position = Position.Absolute;
			Insert(IndexOf(EditBar), element);
			if (widget.EditOverlay != null)
			{
				var overlay = widget.EditOverlay;
				overlay.RegisterCallback<PointerDownEvent>(evt => OnPointerDown(evt, id));
				overlay.RegisterCallback<PointerMoveEvent>(evt => OnPointerMove(evt, id));
				overlay.RegisterCallback<PointerUpEvent>(evt => OnPointerUp(evt, id));
				overlay.RegisterCallback<PointerCaptureOutEvent>(_ => CancelDrag(id));
				overlay.style.display = _hud.Editing ? DisplayStyle.Flex : DisplayStyle.None;
			}

			PaletteRoles.Apply(element, _palette);
			return widget;
		}

		private void UpdateClassic(bool shown)
		{
			if (shown == ClassicPanelShown)
			{
				return;
			}

			ClassicPanelShown = shown;
			ClassicPanelShownChanged?.Invoke(shown);
		}

		private void OnEditModeChanged(bool editing)
		{
			if (_disposed)
			{
				return;
			}

			_dragging = null;
			EditBar.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
			foreach (var widget in _widgets.Values)
			{
				if (widget.EditOverlay != null)
				{
					widget.EditOverlay.style.display = editing ? DisplayStyle.Flex : DisplayStyle.None;
				}
			}

			if (editing)
			{
				EditBar.Begin();
				EditBar.BringToFront();
			}

			ApplyLayout();
		}

		/// <summary>Edit-mode chrome: the selected widget's outline and the bar's text.</summary>
		private void RefreshEditChrome()
		{
			var session = _hud.Session;
			foreach (var widget in _widgets.Values)
			{
				if (widget.EditOverlay == null)
				{
					continue;
				}

				var selected = session != null && session.Selected == widget.Id;
				var overlay = widget.EditOverlay;
				PaletteRoles.SetBorderColor(overlay, selected ? _palette.Caution : _palette.Text);
				overlay.style.backgroundColor = new Color(_palette.Console.r, _palette.Console.g, _palette.Console.b, 0.25f);
				widget.EditLabel.style.backgroundColor = selected ? _palette.Caution : _palette.Console;
				widget.EditLabel.style.color = selected ? _palette.CautionText : _palette.Text;
			}

			EditBar.Refresh(_palette);
		}

		private Vector2 Local(Vector2 panelPosition) => this.WorldToLocal(panelPosition);

		private void OnPointerDown(PointerDownEvent evt, string id)
		{
			var session = _hud.Session;
			if (session == null || evt.button != 0)
			{
				return;
			}

			session.Screen = FrameSize;
			session.Select(id);
			if (session.BeginDrag(id, Local(evt.position)))
			{
				_dragging = id;
				_dragPointer = evt.pointerId;
				Widget(id).EditOverlay.CapturePointer(evt.pointerId);
			}

			RefreshEditChrome();
			evt.StopPropagation();
		}

		private void OnPointerMove(PointerMoveEvent evt, string id)
		{
			var session = _hud.Session;
			if (session == null || _dragging != id || evt.pointerId != _dragPointer)
			{
				return;
			}

			// Preview with translate only; left, top and the margins are written on drop.
			var delta = session.DragTo(Local(evt.position));
			var widget = Widget(id);
			var placement = session.Layout.Get(id);
			var fraction = HudAnchors.Fraction(placement.Anchor);
			var size = widget.Element.layout.size;
			widget.Element.style.translate = new Translate(new Length(-fraction.x * size.x + delta.x), new Length(-fraction.y * size.y + delta.y));
			evt.StopPropagation();
		}

		private void OnPointerUp(PointerUpEvent evt, string id)
		{
			var session = _hud.Session;
			if (session == null || _dragging != id || evt.pointerId != _dragPointer)
			{
				return;
			}

			_dragging = null;
			var overlay = Widget(id).EditOverlay;
			if (overlay.HasPointerCapture(evt.pointerId))
			{
				overlay.ReleasePointer(evt.pointerId);
			}

			session.Drop(Local(evt.position));
			// A drop to the same place raises nothing; put the preview translate back either way.
			Place(session.Layout.Get(id));
			OnLayoutChanged(id);
			evt.StopPropagation();
		}

		private void CancelDrag(string id)
		{
			if (_dragging != id)
			{
				return;
			}

			_dragging = null;
			_hud.Session?.CancelDrag();
			if (_hud.Session != null)
			{
				OnLayoutChanged(id);
			}
		}
	}

	/// <summary>
	/// The edit-mode toolbar: which widget is selected (Previous and Next walk every widget, shown or not), show or hide it,
	/// scale it, reset it or the whole layout, save under a name, and Done. Every button is a tab stop.
	/// </summary>
	public sealed class HudEditBar : VisualElement
	{
		private readonly HudController _hud;
		private readonly Label _title;
		private readonly Label _selected;
		private readonly Label _status;
		private readonly List<Button> _buttons = new List<Button>();

		public HudEditBar(HudController hud)
		{
			_hud = hud;
			name = "hud-edit-bar";
			AddToClassList("fs-hud-edit-bar");
			AddToClassList(PaletteRoles.ConsoleBackground);
			pickingMode = PickingMode.Position;

			var top = Row("fs-hud-edit-row");
			_title = PaletteRoles.Label("EDIT HUD", PaletteRoles.Text, "fs-value-s", "fs-strong");
			top.Add(_title);
			top.Add(PaletteRoles.Label("DRAG A WIDGET TO MOVE IT · IT SNAPS TO EDGES AND ANCHORS · THE RUN KEEPS GOING", PaletteRoles.Dim, "fs-caption", "fs-hud-edit-hint"));
			Add(top);

			var widgets = Row("fs-hud-edit-row");
			PreviousButton = MakeButton("◀", () => Step(-1), "hud-edit-previous");
			widgets.Add(PreviousButton);
			_selected = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-caption", "fs-hud-edit-selected");
			_selected.name = "hud-edit-selected";
			widgets.Add(_selected);
			NextButton = MakeButton("▶", () => Step(1), "hud-edit-next");
			widgets.Add(NextButton);
			ShowHideButton = MakeButton("SHOW", () => Command(session => session.ToggleVisible(Selected)), "hud-edit-show");
			widgets.Add(ShowHideButton);
			widgets.Add(MakeButton("−", () => Command(session => session.StepScale(Selected, -1)), "hud-edit-smaller"));
			widgets.Add(MakeButton("+", () => Command(session => session.StepScale(Selected, 1)), "hud-edit-larger"));
			widgets.Add(MakeButton("RESET WIDGET", () => Command(session => session.ResetWidget(Selected)), "hud-edit-reset-widget"));
			widgets.Add(MakeButton("RESET ALL", () => Command(session => session.ResetLayout()), "hud-edit-reset-all"));
			Add(widgets);

			var save = Row("fs-hud-edit-row");
			NameField = new TextField { name = "hud-edit-name", isDelayed = false, maxLength = SettingsStore.MaxLayoutNameLength };
			NameField.AddToClassList("fs-hud-edit-name");
			NameField.RegisterCallback<FocusInEvent>(_ => TextEntryChanged?.Invoke(true));
			NameField.RegisterCallback<FocusOutEvent>(_ => TextEntryChanged?.Invoke(false));
			save.Add(PaletteRoles.Label("SAVE AS", PaletteRoles.Dim, "fs-caption"));
			save.Add(NameField);
			SaveButton = MakeButton("SAVE", () => Save(NameField.value), "hud-edit-save");
			save.Add(SaveButton);
			save.Add(Row("fs-spacer"));
			DoneButton = MakeButton("DONE", () => DoneRequested?.Invoke(), "hud-edit-done");
			save.Add(DoneButton);
			Add(save);
			_status = PaletteRoles.Label(string.Empty, PaletteRoles.Text, "fs-caption", "fs-hud-edit-status");
			_status.name = "hud-edit-status";
			Add(_status);
		}

		/// <summary>Done: the host ends edit mode.</summary>
		public event Action DoneRequested;

		/// <summary>After any command, so the layer can restyle.</summary>
		public event Action Commanded;

		/// <summary>True while the name field has the keyboard focus (letters typed there must not reach the helm), false after.</summary>
		public event Action<bool> TextEntryChanged;

		public Button PreviousButton { get; }

		public Button NextButton { get; }

		public Button ShowHideButton { get; }

		public Button SaveButton { get; }

		public Button DoneButton { get; }

		public TextField NameField { get; }

		public string Selected => _hud.Session?.Selected;

		public string Status => _status.text;

		/// <summary>When edit mode starts: selects the first shown widget and suggests a name.</summary>
		public void Begin()
		{
			var session = _hud.Session;
			if (session == null)
			{
				return;
			}

			foreach (var placement in session.Layout.Placements)
			{
				if (placement.Visible)
				{
					session.Select(placement.Id);
					break;
				}
			}

			if (session.Selected == null)
			{
				session.Select(_hud.Registry.Widgets[0].Id);
			}

			var current = session.Layout.Name;
			NameField.SetValueWithoutNotify(HudPresets.IsPreset(current) ? "my layout" : current);
			ShowStatus(string.Empty);
			if (panel != null)
			{
				DoneButton.Focus();
			}
		}

		/// <summary>Saves under a name (the explicit commit the Save button uses).</summary>
		public bool Save(string layoutName)
		{
			var saved = _hud.SaveAs(layoutName, out var problem);
			ShowStatus(saved ? $"Saved as '{layoutName.Trim()}'." : problem);
			Commanded?.Invoke();
			return saved;
		}

		public void Refresh(InstrumentPalette palette)
		{
			foreach (var button in _buttons)
			{
				DebriefScreen.StyleButton(button, palette);
			}

			var input = NameField.Q(className: TextField.inputUssClassName);
			if (input != null)
			{
				input.style.backgroundColor = palette.Face;
				input.style.color = palette.Text;
				PaletteRoles.SetBorderColor(input, palette.Bezel);
			}

			var session = _hud.Session;
			if (session == null || Selected == null)
			{
				return;
			}

			var placement = session.Layout.Get(Selected);
			var definition = _hud.Registry.Find(Selected);
			PaletteRoles.SetText(_selected, $"{definition.Title.ToUpperInvariant()} · {(placement.Visible ? "SHOWN" : "HIDDEN")} · ×{placement.Scale:0.0}");
			ShowHideButton.text = placement.Visible ? "HIDE" : "SHOW";
		}

		private void Step(int direction)
		{
			var session = _hud.Session;
			if (session == null)
			{
				return;
			}

			var widgets = _hud.Registry.Widgets;
			var index = 0;
			for (var i = 0; i < widgets.Count; i++)
			{
				if (widgets[i].Id == session.Selected)
				{
					index = i;
				}
			}

			session.Select(widgets[(index + direction + widgets.Count) % widgets.Count].Id);
			Commanded?.Invoke();
		}

		private void Command(Action<HudEditSession> command)
		{
			var session = _hud.Session;
			if (session == null || session.Selected == null)
			{
				return;
			}

			command(session);
			Commanded?.Invoke();
		}

		private void ShowStatus(string text) => PaletteRoles.SetText(_status, text ?? string.Empty);

		private Button MakeButton(string text, Action onClick, string buttonName)
		{
			var button = new Button(onClick) { text = text, name = buttonName };
			button.AddToClassList("fs-button");
			_buttons.Add(button);
			return button;
		}

		private static VisualElement Row(string className)
		{
			var element = new VisualElement { pickingMode = PickingMode.Ignore };
			element.AddToClassList(className);
			return element;
		}
	}
}
