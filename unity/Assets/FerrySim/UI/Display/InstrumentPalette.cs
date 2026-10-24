using UnityEngine;

namespace FerrySim.UI
{
	public enum PaletteKind
	{
		Day = 0,
		Night = 1,
	}

	/// <summary>
	/// Every color the instruments, HUD and debrief use, for one lighting condition. Layout lives in the
	/// style sheet; color lives here so the contrast rules can be tested (PaletteTests).
	/// <para>
	/// Day is a dark console with bright numerals, like the real wheelhouse consoles that keep glare off
	/// the windows; text meets WCAG AA (primary numerals AAA). Night is dim red to amber with no blue,
	/// green or white light, to protect night vision. Port and starboard are red and green by day only;
	/// at night they are told apart by position and their P/S labels.
	/// </para>
	/// </summary>
	public sealed class InstrumentPalette
	{
		private InstrumentPalette()
		{
		}

		public static InstrumentPalette Day { get; } = CreateDay();

		public static InstrumentPalette Night { get; } = CreateNight();

		public PaletteKind Kind { get; private set; }

		/// <summary>Panel background.</summary>
		public Color Console { get; private set; }

		/// <summary>Instrument face behind dials and readouts.</summary>
		public Color Face { get; private set; }

		/// <summary>Borders, inactive tracks and dial rims.</summary>
		public Color Bezel { get; private set; }

		/// <summary>Plot grid lines; deliberately faint.</summary>
		public Color Grid { get; private set; }

		/// <summary>Primary numerals.</summary>
		public Color Text { get; private set; }

		/// <summary>Captions, units and secondary readouts.</summary>
		public Color TextDim { get; private set; }

		/// <summary>Dial ticks and scale numerals.</summary>
		public Color Scale { get; private set; }

		public Color Needle { get; private set; }

		/// <summary>Translucent fill between a zero point and the current value (levers, rudders).</summary>
		public Color Fill { get; private set; }

		public Color Port { get; private set; }

		public Color Starboard { get; private set; }

		/// <summary>Gear badge fill for AHEAD and ASTERN.</summary>
		public Color Engaged { get; private set; }

		public Color EngagedText { get; private set; }

		/// <summary>Gear badge fill for SHIFTING: caution, no reliable drive.</summary>
		public Color Shifting { get; private set; }

		public Color ShiftingText { get; private set; }

		/// <summary>Contact indicator fill.</summary>
		public Color Caution { get; private set; }

		public Color CautionText { get; private set; }

		/// <summary>Alarm fill (depth). The only color that flashes.</summary>
		public Color Alarm { get; private set; }

		public Color AlarmText { get; private set; }

		/// <summary>Tachometer band above rated speed.</summary>
		public Color Redline { get; private set; }

		/// <summary>First plot series (solid line).</summary>
		public Color SeriesA { get; private set; }

		/// <summary>Second plot series (dashed line, so it never depends on color alone).</summary>
		public Color SeriesB { get; private set; }

		/// <summary>Debrief time cursor.</summary>
		public Color Cursor { get; private set; }

		/// <summary>
		/// Backing behind the HUD and the development toolbar, which float over the 3D view. Opaque: a
		/// translucent backing lets bright sky or a sunlit hull wash their text out (3.16:1 was measured).
		/// </summary>
		public Color HudBacking { get; private set; }

		public static InstrumentPalette For(PaletteKind kind) => kind == PaletteKind.Night ? Night : Day;

		private static InstrumentPalette CreateDay() =>
			new InstrumentPalette
			{
				Kind = PaletteKind.Day,
				Console = Hex(0x0E1318),
				Face = Hex(0x161D24),
				Bezel = Hex(0x2C3743),
				Grid = Hex(0x26303A),
				Text = Hex(0xF2F5F7),
				TextDim = Hex(0xA2B1BE),
				Scale = Hex(0xC7D2DC),
				Needle = Hex(0xFFFFFF),
				Fill = Hex(0xF2F5F7, 0.22f),
				Port = Hex(0xFF6B6B),
				Starboard = Hex(0x3CD483),
				Engaged = Hex(0xE8EEF3),
				EngagedText = Hex(0x0E1318),
				Shifting = Hex(0xFFC53D),
				ShiftingText = Hex(0x14100A),
				Caution = Hex(0xFF9A3C),
				CautionText = Hex(0x160B02),
				Alarm = Hex(0xD7261E),
				AlarmText = Hex(0xFFFFFF),
				Redline = Hex(0xE5484D),
				SeriesA = Hex(0x5CC8FF),
				SeriesB = Hex(0xC9A7FF),
				Cursor = Hex(0xFFFFFF),
				HudBacking = Hex(0x0E1318),
			};

		private static InstrumentPalette CreateNight() =>
			new InstrumentPalette
			{
				Kind = PaletteKind.Night,
				Console = Hex(0x000000),
				Face = Hex(0x080202),
				Bezel = Hex(0x2A0B06),
				Grid = Hex(0x1E0704),
				Text = Hex(0xD03C1E),
				TextDim = Hex(0x8C2A12),
				Scale = Hex(0xA8341A),
				Needle = Hex(0xE84A24),
				Fill = Hex(0xD03C1E, 0.22f),
				Port = Hex(0xC8381C),
				Starboard = Hex(0x9A5A18),
				Engaged = Hex(0xB8341A),
				EngagedText = Hex(0x000000),
				Shifting = Hex(0xA8620E),
				ShiftingText = Hex(0x000000),
				Caution = Hex(0xB4501A),
				CautionText = Hex(0x000000),
				Alarm = Hex(0xF0381E),
				AlarmText = Hex(0x000000),
				Redline = Hex(0x5A1408),
				SeriesA = Hex(0xD03C1E),
				SeriesB = Hex(0x9A5A18),
				Cursor = Hex(0xE84A24),
				HudBacking = Hex(0x000000),
			};

		private static Color Hex(uint rgb, float alpha = 1f) =>
			new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
	}
}
