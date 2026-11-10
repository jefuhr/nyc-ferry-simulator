using FerrySim.Core.Instruments;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// The view of one skin element, placed at its rect in reference pixels. The display calls <see cref="Bind"/> when
	/// the readout changes, <see cref="RefreshValue"/> at the skin's refresh rate and <see cref="RefreshLamps"/> on every
	/// refresh; none of them may allocate once bound, and each touches a style only when what it shows has changed.
	/// </summary>
	public abstract class SkinElementView : VisualElement
	{
		protected SkinElementView(SkinElement spec, DisplaySide side)
		{
			Spec = spec;
			Side = side;
			name = "element-" + spec.Number;
			pickingMode = PickingMode.Ignore;
			AddToClassList("fs-display-element");
			var rect = spec.Rect;
			style.position = Position.Absolute;
			style.left = rect.x;
			style.top = rect.y;
			style.width = rect.width;
			style.height = rect.height;
		}

		public SkinElement Spec { get; }

		public DisplaySide Side { get; }

		/// <summary>True while the element is on the page shown (always, for an element without a page).</summary>
		public bool OnPage { get; private set; } = true;

		internal void SetOnPage(bool on)
		{
			OnPage = on;
			style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
		}

		public abstract void ApplyPalette(InstrumentPalette palette);

		/// <summary>A new readout (or none): resolve alarm indices and rebuild anything that depends on its alarm list.</summary>
		internal virtual void Bind(IInstrumentReadout readout)
		{
		}

		/// <summary>Show the readout's values now. A null readout shows every reading as not available.</summary>
		internal virtual void RefreshValue(IInstrumentReadout readout)
		{
		}

		/// <summary>Follow the lamps' flash rhythm at <paramref name="time"/>.</summary>
		internal virtual void RefreshLamps(IInstrumentReadout readout, double time)
		{
		}

		/// <summary>The element's own colour from the skin, or the role it takes by default.</summary>
		protected Color Colour(InstrumentPalette palette, PaletteRole fallback) =>
			Spec.Colour.HasValue ? Spec.Colour.Value.Resolve(palette) : SkinColour.RoleColour(palette, fallback);

		protected SkinColour ColourSpec(PaletteRole fallback) =>
			Spec.Colour ?? SkinColour.FromRole(fallback);

		/// <summary>An absolutely placed label inside the element, in element pixels.</summary>
		protected static Label MakeLabel(VisualElement parent, string text, Rect rect, float fontSize, TextAnchor align, bool bold = false, bool primary = false)
		{
			var label = new Label(text ?? "") { pickingMode = PickingMode.Ignore };
			label.AddToClassList("fs-display-label");
			if (primary)
			{
				label.AddToClassList(SkinnedDisplayView.PrimaryTextClass);
			}

			Place(label, rect);
			label.style.fontSize = Mathf.Max(6f, fontSize);
			label.style.unityTextAlign = align;
			label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
			label.style.whiteSpace = WhiteSpace.NoWrap;
			label.style.overflow = Overflow.Hidden;
			parent.Add(label);
			return label;
		}

		/// <summary>A label centred on a point, sized by its text.</summary>
		protected static Label CentredLabel(VisualElement parent, string text, Vector2 centre, float fontSize, bool bold = false, bool primary = false)
		{
			var label = new Label(text ?? "") { pickingMode = PickingMode.Ignore };
			label.AddToClassList("fs-display-label");
			if (primary)
			{
				label.AddToClassList(SkinnedDisplayView.PrimaryTextClass);
			}

			ZeroBox(label);
			label.style.position = Position.Absolute;
			label.style.left = centre.x;
			label.style.top = centre.y;
			label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
			label.style.fontSize = Mathf.Max(6f, fontSize);
			label.style.unityTextAlign = TextAnchor.MiddleCenter;
			label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
			label.style.whiteSpace = WhiteSpace.NoWrap;
			parent.Add(label);
			return label;
		}

		protected static void Place(VisualElement element, Rect rect)
		{
			ZeroBox(element);
			element.style.position = Position.Absolute;
			element.style.left = rect.x;
			element.style.top = rect.y;
			element.style.width = rect.width;
			element.style.height = rect.height;
		}

		protected static void Fill(VisualElement element)
		{
			ZeroBox(element);
			element.style.position = Position.Absolute;
			element.style.left = 0;
			element.style.top = 0;
			element.style.right = 0;
			element.style.bottom = 0;
		}

		protected static void Border(VisualElement element, float width, Color colour)
		{
			element.style.borderTopWidth = width;
			element.style.borderRightWidth = width;
			element.style.borderBottomWidth = width;
			element.style.borderLeftWidth = width;
			PaletteRoles.SetBorderColor(element, colour);
		}

		protected static void SetText(Label label, string text)
		{
			if (!ReferenceEquals(label.text, text) && label.text != text)
			{
				label.text = text;
			}
		}

		private static void ZeroBox(VisualElement element)
		{
			element.style.marginLeft = 0;
			element.style.marginRight = 0;
			element.style.marginTop = 0;
			element.style.marginBottom = 0;
			element.style.paddingLeft = 0;
			element.style.paddingRight = 0;
			element.style.paddingTop = 0;
			element.style.paddingBottom = 0;
		}

		/// <summary>A readout value in the element's unit; NaN when the readout or the reading is missing.</summary>
		protected static double Read(IInstrumentReadout readout, Channel channel, DisplayUnit unit) =>
			readout == null ? double.NaN : SkinUnits.FromSi(unit, readout.Value(channel));

		/// <summary>The index of an alarm in a readout, or -1.</summary>
		protected static int FindAlarm(IInstrumentReadout readout, string id)
		{
			if (readout == null || id == null)
			{
				return -1;
			}

			for (var i = 0; i < readout.AlarmCount; i++)
			{
				if (string.Equals(readout.AlarmId(i), id, System.StringComparison.Ordinal))
				{
					return i;
				}
			}

			return -1;
		}
	}

	/// <summary>A box with an optional title: the artwork that groups other elements. Static.</summary>
	public sealed class FrameView : SkinElementView
	{
		private readonly Label _caption;

		public FrameView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			Border(this, 2f, Color.clear);
			var text = spec.ResolveText(side);
			if (!string.IsNullOrEmpty(text))
			{
				var rect = spec.Rect;
				var font = Mathf.Min(rect.height * 0.5f, 20f);
				var height = font * 1.5f;
				var top = Mathf.Max(0f, (Mathf.Min(rect.height, font * 2f) - height) * 0.5f);
				_caption = MakeLabel(this, text, new Rect(font * 0.5f, top, Mathf.Max(1f, rect.width - font - 4f), height), font, TextAnchor.MiddleLeft, bold: true);
			}
		}

		public string CaptionText => _caption?.text;

		public override void ApplyPalette(InstrumentPalette palette)
		{
			style.backgroundColor = palette.Face;
			PaletteRoles.SetBorderColor(this, Colour(palette, PaletteRole.Bezel));
			if (_caption != null)
			{
				_caption.style.color = palette.Text;
			}
		}
	}

	/// <summary>A fixed caption. Static.</summary>
	public sealed class TextView : SkinElementView
	{
		public TextView(SkinElement spec, DisplaySide side)
			: base(spec, side)
		{
			var rect = spec.Rect;
			Label = MakeLabel(this, spec.ResolveText(side), new Rect(0, 0, rect.width, rect.height), rect.height * 0.7f, TextAnchor.MiddleLeft);
		}

		public Label Label { get; }

		public override void ApplyPalette(InstrumentPalette palette) => Label.style.color = Colour(palette, PaletteRole.Text);
	}

	/// <summary>
	/// A key: on screen it takes a click or a touch, and a host can drive it from a physical button through
	/// <see cref="SkinnedDisplayView.PressSoftkey"/>. A key with a target shows filled while its page is shown; any key
	/// shows filled while held.
	/// </summary>
	public sealed class SoftkeyView : SkinElementView
	{
		private readonly Label _label;
		private InstrumentPalette _palette = InstrumentPalette.Day;
		private bool _filled;

		internal SoftkeyView(SkinElement spec, DisplaySide side, SkinnedDisplayView owner, int index)
			: base(spec, side)
		{
			pickingMode = PickingMode.Position;
			Border(this, 2f, Color.clear);
			var rect = spec.Rect;
			_label = MakeLabel(this, spec.ResolveText(side), new Rect(0, 0, rect.width - 4f, rect.height - 4f), Mathf.Min(rect.height * 0.42f, rect.width * 0.16f), TextAnchor.MiddleCenter, bold: true);
			RegisterCallback<PointerDownEvent>(_ => owner.PressSoftkey(index));
			RegisterCallback<PointerUpEvent>(_ => owner.ReleaseSoftkey(index));
			RegisterCallback<PointerLeaveEvent>(_ => owner.CancelSoftkey(index));
			RegisterCallback<PointerCancelEvent>(_ => owner.CancelSoftkey(index));
		}

		/// <summary>True while the key's target page is shown.</summary>
		public bool Selected { get; private set; }

		/// <summary>True while the key is held down.</summary>
		public bool Pressed { get; private set; }

		public string LabelText => _label.text;

		internal void SetSelected(bool selected)
		{
			Selected = selected;
			Restyle();
		}

		internal void SetPressed(bool pressed)
		{
			Pressed = pressed;
			Restyle();
		}

		public override void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette;
			_filled = !(Selected || Pressed);
			Restyle();
		}

		private void Restyle()
		{
			var filled = Selected || Pressed;
			if (filled == _filled)
			{
				return;
			}

			_filled = filled;
			var fill = ColourSpec(PaletteRole.Engaged);
			style.backgroundColor = filled ? fill.Resolve(_palette) : _palette.Face;
			PaletteRoles.SetBorderColor(this, filled ? fill.Resolve(_palette) : _palette.Bezel);
			_label.style.color = filled ? fill.TextOn(_palette) : _palette.Text;
		}
	}
}
