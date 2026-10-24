using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The gear state as the control head shows it. AHEAD and ASTERN are filled (drive is engaged),
	/// SHIFTING is filled amber (no reliable drive yet), NEUTRAL is only outlined. Text, fill and outline
	/// all differ, so the state never depends on color alone, and none of them flash.
	/// </summary>
	public sealed class GearBadge : VisualElement, IPaletteAware
	{
		private readonly Label _label;
		private InstrumentPalette _palette = InstrumentPalette.Day;

		public GearBadge()
		{
			AddToClassList("fs-gear");
			pickingMode = PickingMode.Ignore;
			_label = PaletteRoles.Label(InstrumentFormat.NoValue, null, "fs-gear-label");
			Add(_label);
			Restyle();
		}

		public string Text => _label.text;

		public GearTone Tone { get; private set; } = GearTone.Unknown;

		public void ApplyPalette(InstrumentPalette palette)
		{
			_palette = palette ?? InstrumentPalette.Day;
			Restyle();
		}

		public void Show(GearReadout readout) => Show(readout.Label, readout.Tone);

		public void Show(string text, GearTone tone)
		{
			PaletteRoles.SetText(_label, text);
			if (tone != Tone)
			{
				Tone = tone;
				Restyle();
			}
		}

		private void Restyle()
		{
			Color fill;
			Color border;
			Color text;
			switch (Tone)
			{
				case GearTone.Engaged:
					fill = _palette.Engaged;
					border = _palette.Engaged;
					text = _palette.EngagedText;
					break;
				case GearTone.Transition:
					fill = _palette.Shifting;
					border = _palette.Shifting;
					text = _palette.ShiftingText;
					break;
				case GearTone.Idle:
					fill = Color.clear;
					border = _palette.Text;
					text = _palette.Text;
					break;
				default:
					fill = Color.clear;
					border = _palette.Bezel;
					text = _palette.TextDim;
					break;
			}

			style.backgroundColor = fill;
			PaletteRoles.SetBorderColor(this, border);
			_label.style.color = text;
		}
	}

	/// <summary>Shaft rotation arrow: up for ahead, down for astern, a bar when the shaft reads 0 rpm, nothing when unknown.</summary>
	public sealed class ShaftArrow : InstrumentElement
	{
		public ShaftArrow()
		{
			AddToClassList("fs-shaft-arrow");
		}

		public ShaftRotation Rotation { get; private set; } = ShaftRotation.Unknown;

		public void Show(ShaftRotation rotation)
		{
			if (rotation == Rotation)
			{
				return;
			}

			Rotation = rotation;
			MarkDirtyRepaint();
		}

		protected override void Draw(Painter2D painter, Rect rect)
		{
			var centre = rect.center;
			var half = Mathf.Min(rect.width, rect.height) * 0.45f;
			switch (Rotation)
			{
				case ShaftRotation.Ahead:
					VectorDraw.Triangle(painter, new Vector2(centre.x, centre.y - half), new Vector2(centre.x - half, centre.y + half * 0.7f),
						new Vector2(centre.x + half, centre.y + half * 0.7f), Palette.Text);
					break;
				case ShaftRotation.Astern:
					VectorDraw.Triangle(painter, new Vector2(centre.x, centre.y + half), new Vector2(centre.x - half, centre.y - half * 0.7f),
						new Vector2(centre.x + half, centre.y - half * 0.7f), Palette.Text);
					break;
				case ShaftRotation.Stopped:
					VectorDraw.Rect(painter, new Rect(centre.x - half, centre.y - 1.5f, half * 2f, 3f), Palette.TextDim);
					break;
			}
		}
	}
}
