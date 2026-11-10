using System;
using System.Globalization;
using UnityEngine;

namespace FerrySim.UI.Displays
{
	/// <summary>The <see cref="InstrumentPalette"/> colours a skin may name, written in skins in kebab case (<c>text-dim</c>).</summary>
	public enum PaletteRole
	{
		Console,
		Face,
		Bezel,
		Grid,
		Text,
		TextDim,
		Scale,
		Needle,
		Fill,
		Port,
		Starboard,
		Engaged,
		Shifting,
		Caution,
		Alarm,
		Redline,
		SeriesA,
		SeriesB,
		Cursor,
	}

	/// <summary>
	/// A skin colour: a palette role, which follows the palette (day or night) like every other instrument, or a
	/// <c>#RRGGBB</c> day colour whose night colour is derived by <see cref="NightFromDay"/>, so a skin copied from a
	/// daylight photo can never put blue, green or white light into a dark wheelhouse.
	/// </summary>
	public readonly struct SkinColour
	{
		private static readonly string[] RoleNames =
		{
			"console", "face", "bezel", "grid", "text", "text-dim", "scale", "needle", "fill", "port", "starboard",
			"engaged", "shifting", "caution", "alarm", "redline", "series-a", "series-b", "cursor",
		};

		// The palette's own night numerals: red with green and blue at these fractions of red (sRGB), and the
		// brightness they have compared with the day numerals. A neutral day colour maps onto this red.
		private static readonly float NightGreenRatio = InstrumentPalette.Night.Text.g / InstrumentPalette.Night.Text.r;
		private static readonly float NightBlueRatio = InstrumentPalette.Night.Text.b / InstrumentPalette.Night.Text.r;
		private static readonly double NightBrightness = Luminance(InstrumentPalette.Night.Text) / Luminance(InstrumentPalette.Day.Text);

		/// <summary>Night colours stay red to amber: green at most this fraction of red (the palette's night rule).</summary>
		public const float NightMaximumGreenRatio = 0.65f;

		/// <summary>No derived night colour is brighter than this relative luminance (the palette's cap for every role but the alarm).</summary>
		public const double NightLuminanceCap = 0.25;

		private SkinColour(bool isRole, PaletteRole role, Color day, Color night)
		{
			IsRole = isRole;
			Role = role;
			Day = day;
			Night = night;
		}

		/// <summary>True for a palette role; false for a hex colour.</summary>
		public bool IsRole { get; }

		public PaletteRole Role { get; }

		/// <summary>The day colour of a hex colour (unset for a role).</summary>
		public Color Day { get; }

		/// <summary>The derived night colour of a hex colour (unset for a role).</summary>
		public Color Night { get; }

		public static SkinColour FromRole(PaletteRole role) => new SkinColour(true, role, default, default);

		public static SkinColour FromDay(Color day) => new SkinColour(false, default, day, NightFromDay(day));

		/// <summary>Parses a role name or <c>#RRGGBB</c>.</summary>
		public static bool TryParse(string text, out SkinColour colour)
		{
			colour = default;
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			if (text[0] == '#')
			{
				if (text.Length != 7 || !uint.TryParse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
				{
					return false;
				}

				colour = FromDay(new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f));
				return true;
			}

			for (var i = 0; i < RoleNames.Length; i++)
			{
				if (string.Equals(RoleNames[i], text, StringComparison.Ordinal))
				{
					colour = FromRole((PaletteRole)i);
					return true;
				}
			}

			return false;
		}

		/// <summary>The skin name of a role.</summary>
		public static string Name(PaletteRole role) => RoleNames[(int)role];

		/// <summary>This colour in <paramref name="palette"/>.</summary>
		public Color Resolve(InstrumentPalette palette)
		{
			palette = palette ?? InstrumentPalette.Day;
			if (!IsRole)
			{
				return palette.Kind == PaletteKind.Night ? Night : Day;
			}

			return RoleColour(palette, Role);
		}

		/// <summary>
		/// The colour of text drawn on this colour as a fill (a lit lamp, a selected key): the palette's own pairing
		/// for the badge roles, otherwise whichever of the console and text colours contrasts more.
		/// </summary>
		public Color TextOn(InstrumentPalette palette)
		{
			palette = palette ?? InstrumentPalette.Day;
			if (IsRole)
			{
				switch (Role)
				{
					case PaletteRole.Alarm: return palette.AlarmText;
					case PaletteRole.Caution: return palette.CautionText;
					case PaletteRole.Shifting: return palette.ShiftingText;
					case PaletteRole.Engaged: return palette.EngagedText;
				}
			}

			var fill = Luminance(Resolve(palette));
			var dark = Luminance(palette.Console);
			var light = Luminance(palette.Text);
			return Ratio(fill, dark) >= Ratio(fill, light) ? palette.Console : palette.Text;
		}

		public static Color RoleColour(InstrumentPalette palette, PaletteRole role)
		{
			switch (role)
			{
				case PaletteRole.Console: return palette.Console;
				case PaletteRole.Face: return palette.Face;
				case PaletteRole.Bezel: return palette.Bezel;
				case PaletteRole.Grid: return palette.Grid;
				case PaletteRole.Text: return palette.Text;
				case PaletteRole.TextDim: return palette.TextDim;
				case PaletteRole.Scale: return palette.Scale;
				case PaletteRole.Needle: return palette.Needle;
				case PaletteRole.Fill: return palette.Fill;
				case PaletteRole.Port: return palette.Port;
				case PaletteRole.Starboard: return palette.Starboard;
				case PaletteRole.Engaged: return palette.Engaged;
				case PaletteRole.Shifting: return palette.Shifting;
				case PaletteRole.Caution: return palette.Caution;
				case PaletteRole.Alarm: return palette.Alarm;
				case PaletteRole.Redline: return palette.Redline;
				case PaletteRole.SeriesA: return palette.SeriesA;
				case PaletteRole.SeriesB: return palette.SeriesB;
				default: return palette.Cursor;
			}
		}

		/// <summary>
		/// The night colour of a day colour, by the palette's night rules (Display/InstrumentPalette.cs, measured by
		/// PaletteTests): red ≥ green ≥ blue, green at most 0.65 of red, and dim. A warm day colour (amber, orange,
		/// red) keeps its warmth up to that amber limit; any other colour, white and green included, becomes the red
		/// of the night numerals. Brightness keeps its order: the night colour has the day colour's luminance scaled
		/// by the night-to-day ratio of the palette's own numerals (white becomes as bright as the night numerals),
		/// capped at <see cref="NightLuminanceCap"/>.
		/// </summary>
		public static Color NightFromDay(Color day)
		{
			var green = NightGreenRatio;
			var blue = NightBlueRatio;
			var warm = day.r > 0.05f && day.r >= day.g && day.g >= day.b && day.r - day.b >= 0.25f * day.r;
			if (warm)
			{
				green = Mathf.Clamp(day.g / day.r, NightGreenRatio, NightMaximumGreenRatio);
				blue = Mathf.Min(day.b / day.r, green * NightBlueRatio / NightGreenRatio);
			}

			var target = Math.Min(NightLuminanceCap, Luminance(day) * NightBrightness);
			if (target <= 0.0)
			{
				return new Color(0f, 0f, 0f, day.a);
			}

			// Luminance rises with red at fixed ratios, so bisect for the red that gives the target.
			float low = 0f, high = 1f;
			for (var i = 0; i < 32; i++)
			{
				var mid = 0.5f * (low + high);
				if (Luminance(new Color(mid, mid * green, mid * blue)) < target)
				{
					low = mid;
				}
				else
				{
					high = mid;
				}
			}

			return new Color(low, low * green, low * blue, day.a);
		}

		/// <summary>WCAG 2.x relative luminance of an sRGB colour.</summary>
		public static double Luminance(Color c) => 0.2126 * Linear(c.r) + 0.7152 * Linear(c.g) + 0.0722 * Linear(c.b);

		/// <summary>WCAG 2.x contrast ratio of two relative luminances.</summary>
		public static double Ratio(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

		private static double Linear(float v) => v <= 0.04045f ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

		public override string ToString() =>
			IsRole ? Name(Role) : "#" + ColorUtility.ToHtmlStringRGB(Day);
	}
}
