using System;
using System.Collections.Generic;
using FerrySim.Core.Instruments;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// Renders any display skin (contract C6) against an <see cref="IInstrumentReadout"/>. The view is the skin's
	/// reference size in panel pixels, with every element at its rect, so it renders correctly into a
	/// <see cref="PanelSettings"/> whose reference resolution is the skin's (<see cref="DisplayPanelSettings"/>), at
	/// that size or any multiple. Static artwork is drawn once; moving parts are rotated or scaled elements; text is
	/// set only when it changes; nothing allocates in steady state.
	/// <para>
	/// Pages: elements with a page show only on that page; <see cref="ShowPage"/> and <see cref="NextPage"/> switch, and a
	/// softkey with a target switches on release. Softkeys with an action raise <see cref="AcknowledgeRequested"/>,
	/// <see cref="SilenceRequested"/> or <see cref="LampTestChanged"/>, which the host forwards to its
	/// <c>IInstrumentSource</c>; the view never acts on the alarms itself.
	/// </para>
	/// </summary>
	public class SkinnedDisplayView : VisualElement, IPaletteAware
	{
		public const string StyleSheetResource = "FerrySimUI/Displays";

		/// <summary>Class on every reading (numbers, gear words, alarm captions): the night contrast rule for these is 3:1, not 2:1.</summary>
		public const string PrimaryTextClass = "fs-display-primary";

		private static StyleSheet _sheet;
		private static bool _sheetLoaded;

		private readonly SkinElementView[] _elements;
		private readonly SoftkeyView[] _softkeys;
		private readonly double _interval;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private IInstrumentReadout _bound;
		private int _boundAlarmCount = -1;
		private bool _hasBound;
		private bool _valuesDue = true;
		private double _lastValues = double.NegativeInfinity;

		public SkinnedDisplayView(DisplaySkin skin, DisplaySide side = DisplaySide.None)
		{
			Skin = skin ?? throw new ArgumentNullException(nameof(skin));
			if (skin.UsesSide && side == DisplaySide.None)
			{
				throw new ArgumentException("skin " + skin.Display + "/" + skin.Id + " names {side}; give the view a side", nameof(side));
			}

			Side = side;
			_interval = 1.0 / skin.RefreshHz;
			name = "display-" + skin.Display + "-" + skin.Id + (side == DisplaySide.None ? "" : "-" + (side == DisplaySide.Port ? "port" : "starboard"));
			AddToClassList("fs-display");
			pickingMode = PickingMode.Ignore;
			if (!_sheetLoaded)
			{
				_sheet = Resources.Load<StyleSheet>(StyleSheetResource);
				_sheetLoaded = true;
			}

			if (_sheet != null)
			{
				styleSheets.Add(_sheet);
			}

			style.position = Position.Relative;
			style.width = skin.Reference.x;
			style.height = skin.Reference.y;
			style.minWidth = skin.Reference.x;
			style.minHeight = skin.Reference.y;
			style.flexShrink = 0;
			style.overflow = Overflow.Hidden;

			_elements = new SkinElementView[skin.Elements.Count];
			var softkeys = new List<SoftkeyView>();
			for (var i = 0; i < _elements.Length; i++)
			{
				var element = Create(skin.Elements[i], side, i);
				_elements[i] = element;
				Add(element);
				if (element is SoftkeyView key)
				{
					softkeys.Add(key);
				}
			}

			_softkeys = softkeys.ToArray();
			CurrentPage = skin.Pages.Count > 0 ? skin.Pages[0] : null;
			ApplyPage();
			ApplyPalette(InstrumentPalette.Day);
			RegisterCallback<DetachFromPanelEvent>(_ => CancelPresses());
		}

		public DisplaySkin Skin { get; }

		public DisplaySide Side { get; }

		/// <summary>One view per skin element, in skin order: element n of the skin is <c>Elements[n - 1]</c>.</summary>
		public IReadOnlyList<SkinElementView> Elements => _elements;

		/// <summary>The page shown; null for a skin without pages.</summary>
		public string CurrentPage { get; private set; }

		public event Action<string> PageChanged;

		/// <summary>An acknowledge key was pressed and released.</summary>
		public event Action AcknowledgeRequested;

		/// <summary>A silence key was pressed and released.</summary>
		public event Action SilenceRequested;

		/// <summary>A lamp-test key went down (true) or up (false).</summary>
		public event Action<bool> LampTestChanged;

		public PaletteKind Palette
		{
			get => _palette.Kind;
			set => ApplyPalette(InstrumentPalette.For(value));
		}

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			style.backgroundColor = _palette.Console;
			for (var i = 0; i < _elements.Length; i++)
			{
				_elements[i].ApplyPalette(_palette);
			}
		}

		/// <summary>Shows a page by name; false (and nothing changes) for a page the skin does not have.</summary>
		public bool ShowPage(string page)
		{
			var index = IndexOfPage(page);
			if (index < 0)
			{
				return false;
			}

			if (string.Equals(page, CurrentPage, StringComparison.Ordinal))
			{
				return true;
			}

			CurrentPage = Skin.Pages[index];
			ApplyPage();
			_valuesDue = true;
			PageChanged?.Invoke(CurrentPage);
			return true;
		}

		/// <summary>Shows the next page, wrapping to the first; does nothing for a skin without pages.</summary>
		public void NextPage()
		{
			if (Skin.Pages.Count == 0)
			{
				return;
			}

			var index = IndexOfPage(CurrentPage);
			ShowPage(Skin.Pages[(index + 1) % Skin.Pages.Count]);
		}

		/// <summary>A softkey goes down, by click, touch or a host's physical button. Element indices count from 0.</summary>
		public void PressSoftkey(int elementIndex)
		{
			var key = Softkey(elementIndex);
			if (key.Pressed)
			{
				return;
			}

			key.SetPressed(true);
			if (key.Spec.Action == SoftkeyAction.LampTest)
			{
				LampTestChanged?.Invoke(true);
			}
		}

		/// <summary>A softkey comes up: its target page opens, or its action is raised (a lamp test ends).</summary>
		public void ReleaseSoftkey(int elementIndex)
		{
			var key = Softkey(elementIndex);
			if (!key.Pressed)
			{
				return;
			}

			key.SetPressed(false);
			if (key.Spec.Target != null)
			{
				ShowPage(key.Spec.Target);
				return;
			}

			switch (key.Spec.Action)
			{
				case SoftkeyAction.Acknowledge:
					AcknowledgeRequested?.Invoke();
					break;
				case SoftkeyAction.Silence:
					SilenceRequested?.Invoke();
					break;
				case SoftkeyAction.LampTest:
					LampTestChanged?.Invoke(false);
					break;
			}
		}

		/// <summary>The pointer left a held key: it comes up without acting, except that a lamp test ends.</summary>
		public void CancelSoftkey(int elementIndex)
		{
			var key = Softkey(elementIndex);
			if (!key.Pressed)
			{
				return;
			}

			key.SetPressed(false);
			if (key.Spec.Action == SoftkeyAction.LampTest)
			{
				LampTestChanged?.Invoke(false);
			}
		}

		/// <summary>Lets go of every held key without acting (a held lamp test ends).</summary>
		public void CancelPresses()
		{
			for (var i = 0; i < _elements.Length; i++)
			{
				if (_elements[i] is SoftkeyView key && key.Pressed)
				{
					CancelSoftkey(i);
				}
			}
		}

		/// <summary>
		/// Shows <paramref name="readout"/> (null when there is no data: every reading shows dashes and every lamp is
		/// dark). Values update at the skin's refresh rate, or at once after a page change, a new readout, or time going
		/// backwards (a new run); lamps follow their flash rhythm at <paramref name="time"/> on every call. Pass simulation
		/// time so the flash agrees with the annunciator's own.
		/// </summary>
		public void Refresh(IInstrumentReadout readout, double time)
		{
			var alarmCount = readout?.AlarmCount ?? -1;
			if (!_hasBound || !ReferenceEquals(readout, _bound) || alarmCount != _boundAlarmCount)
			{
				_hasBound = true;
				_bound = readout;
				_boundAlarmCount = alarmCount;
				for (var i = 0; i < _elements.Length; i++)
				{
					_elements[i].Bind(readout);
				}

				_valuesDue = true;
			}

			if (_valuesDue || time < _lastValues || time - _lastValues >= _interval * (1.0 - 1e-6))
			{
				_valuesDue = false;
				_lastValues = time;
				for (var i = 0; i < _elements.Length; i++)
				{
					if (_elements[i].OnPage)
					{
						_elements[i].RefreshValue(readout);
					}
				}
			}

			for (var i = 0; i < _elements.Length; i++)
			{
				if (_elements[i].OnPage)
				{
					_elements[i].RefreshLamps(readout, time);
				}
			}
		}

		private SoftkeyView Softkey(int elementIndex)
		{
			if (elementIndex < 0 || elementIndex >= _elements.Length || !(_elements[elementIndex] is SoftkeyView key))
			{
				throw new ArgumentException("element " + (elementIndex + 1) + " of " + Skin.Id + " is not a softkey", nameof(elementIndex));
			}

			return key;
		}

		private int IndexOfPage(string page)
		{
			for (var i = 0; i < Skin.Pages.Count; i++)
			{
				if (string.Equals(Skin.Pages[i], page, StringComparison.Ordinal))
				{
					return i;
				}
			}

			return -1;
		}

		private void ApplyPage()
		{
			for (var i = 0; i < _elements.Length; i++)
			{
				var page = _elements[i].Spec.Page;
				_elements[i].SetOnPage(page == null || string.Equals(page, CurrentPage, StringComparison.Ordinal));
			}

			foreach (var key in _softkeys)
			{
				key.SetSelected(key.Spec.Target != null && string.Equals(key.Spec.Target, CurrentPage, StringComparison.Ordinal));
			}
		}

		private SkinElementView Create(SkinElement spec, DisplaySide side, int index)
		{
			switch (spec.Type)
			{
				case SkinElementType.Dial: return new DialView(spec, side);
				case SkinElementType.Bar: return new BarView(spec, side);
				case SkinElementType.Value: return new ValueView(spec, side);
				case SkinElementType.Lamp: return new LampView(spec, side);
				case SkinElementType.Text: return new TextView(spec, side);
				case SkinElementType.Softkey: return new SoftkeyView(spec, side, this, index);
				case SkinElementType.Frame: return new FrameView(spec, side);
				default: return new AlarmListView(spec, side);
			}
		}
	}

	/// <summary>One engine's monitor: a skin for the <c>engine</c> display, shown for the port or the starboard engine.</summary>
	public sealed class EngineMonitorView : SkinnedDisplayView
	{
		public const string Display = "engine";

		public EngineMonitorView(DisplaySide side, DisplaySkin skin)
			: base(Check(side, skin), side)
		{
		}

		private static DisplaySkin Check(DisplaySide side, DisplaySkin skin)
		{
			if (skin == null)
			{
				throw new ArgumentNullException(nameof(skin));
			}

			if (skin.Display != Display)
			{
				throw new ArgumentException("an engine monitor needs an engine skin, not " + skin.Display + "/" + skin.Id, nameof(skin));
			}

			if (side == DisplaySide.None)
			{
				throw new ArgumentException("an engine monitor shows the port or the starboard engine", nameof(side));
			}

			return skin;
		}
	}

	/// <summary>
	/// The alarm panel: a skin for the <c>alarm-panel</c> display, whose list shows every alarm the readout defines.
	/// <see cref="Connect"/> forwards its acknowledge, silence and lamp-test keys to an instrument source.
	/// </summary>
	public sealed class AlarmPanelView : SkinnedDisplayView
	{
		public const string Display = "alarm-panel";

		private FerrySim.Sim.IInstrumentSource _source;

		public AlarmPanelView(DisplaySkin skin)
			: base(Check(skin), DisplaySide.None)
		{
			foreach (var element in Elements)
			{
				if (element is AlarmListView list)
				{
					List = list;
					break;
				}
			}
		}

		/// <summary>The panel's alarm list (the first in the skin), or null when the skin has none.</summary>
		public AlarmListView List { get; }

		/// <summary>Forwards the keys to <paramref name="source"/> until <see cref="Disconnect"/>. Replaces any earlier source.</summary>
		public void Connect(FerrySim.Sim.IInstrumentSource source)
		{
			Disconnect();
			_source = source ?? throw new ArgumentNullException(nameof(source));
			AcknowledgeRequested += OnAcknowledge;
			SilenceRequested += OnSilence;
			LampTestChanged += OnLampTest;
		}

		/// <summary>Stops forwarding; a lamp test still held is released first, so it cannot stay on.</summary>
		public void Disconnect()
		{
			if (_source == null)
			{
				return;
			}

			CancelPresses();
			AcknowledgeRequested -= OnAcknowledge;
			SilenceRequested -= OnSilence;
			LampTestChanged -= OnLampTest;
			_source = null;
		}

		private void OnAcknowledge() => _source?.AcknowledgeAlarms();

		private void OnSilence() => _source?.SilenceAlarms();

		private void OnLampTest(bool on) => _source?.SetLampTest(on);

		private static DisplaySkin Check(DisplaySkin skin)
		{
			if (skin == null)
			{
				throw new ArgumentNullException(nameof(skin));
			}

			if (skin.Display != Display)
			{
				throw new ArgumentException("an alarm panel needs an alarm-panel skin, not " + skin.Display + "/" + skin.Id, nameof(skin));
			}

			return skin;
		}
	}
}
