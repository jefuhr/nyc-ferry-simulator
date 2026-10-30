using UnityEngine;
using FerrySim.UI;

namespace FerrySim.Chartplotter
{
	public enum ChartPaletteKind
	{
		Day = 0,
		Dusk = 1,
		Night = 2,
	}

	/// <summary>
	/// Every colour the chartplotter draws with, for one lighting condition. Like the wheelhouse instruments
	/// (<see cref="InstrumentPalette"/>), day is a dark console: deep water is the darkest field and the water gets
	/// brighter as it shoals, so the shallow side of the safety contour is the emphasised one and the unsafe band is the
	/// brightest water. Dusk is the day palette dimmed. Night is red to amber only (red ≥ green ≥ blue, green at most
	/// 0.65 of red), with the same brightness order, so the screen never brightens the wheelhouse; port and starboard
	/// marks are then told apart by shape, not colour. Text colours are the instrument palette's own, so captions on the
	/// plotter match the panel beside it. The palette is an original design: no manufacturer's look is copied.
	/// </summary>
	public sealed class ChartPalette
	{
		private ChartPalette()
		{
		}

		public static ChartPalette Day { get; } = CreateDay();

		public static ChartPalette Dusk { get; } = CreateDusk();

		public static ChartPalette Night { get; } = CreateNight();

		public static ChartPalette For(ChartPaletteKind kind) => kind == ChartPaletteKind.Night ? Night : kind == ChartPaletteKind.Dusk ? Dusk : Day;

		public ChartPaletteKind Kind { get; private set; }

		/// <summary>The instrument palette whose text and alarm colours this chart shares.</summary>
		public InstrumentPalette Instruments { get; private set; }

		/// <summary>Behind the chart where there is no chart data.</summary>
		public Color NoData { get; private set; }

		/// <summary>Bars and the CPA list backing.</summary>
		public Color Panel { get; private set; }

		public Color Bezel { get; private set; }

		public Color Text { get; private set; }

		public Color TextDim { get; private set; }

		/// <summary>Sounding numerals and aid light characters, drawn over water of any band.</summary>
		public Color Sounding { get; private set; }

		public Color Land { get; private set; }

		/// <summary>Depth areas that dry or carry no depth range.</summary>
		public Color Drying { get; private set; }

		/// <summary>Shallower than the safety contour: the emphasised side.</summary>
		public Color Unsafe { get; private set; }

		/// <summary>Between the safety and the deep contour.</summary>
		public Color Caution { get; private set; }

		public Color Deep { get; private set; }

		public Color Contour { get; private set; }

		public Color SafetyContour { get; private set; }

		public Color Coastline { get; private set; }

		public Color Channel { get; private set; }

		public Color Hazard { get; private set; }

		public Color Bridge { get; private set; }

		public Color Barge { get; private set; }

		public Color Landing { get; private set; }

		public Color AidPort { get; private set; }

		public Color AidStarboard { get; private set; }

		public Color AidSpecial { get; private set; }

		public Color AidOther { get; private set; }

		public Color LightFlare { get; private set; }

		public Color OwnShip { get; private set; }

		public Color HeadingLine { get; private set; }

		/// <summary>The COG/SOG vector, the compass's course colour.</summary>
		public Color CourseVector { get; private set; }

		public Color Track { get; private set; }

		public Color Target { get; private set; }

		public Color TargetAlarm { get; private set; }

		public Color TargetAlarmText { get; private set; }

		public Color Selection { get; private set; }

		public Color Visibility { get; private set; }

		private static ChartPalette CreateDay()
		{
			var instruments = InstrumentPalette.Day;
			return new ChartPalette
			{
				Kind = ChartPaletteKind.Day,
				Instruments = instruments,
				NoData = Hex(0x0B0F14),
				Panel = instruments.Console,
				Bezel = instruments.Bezel,
				Text = instruments.Text,
				TextDim = instruments.TextDim,
				Sounding = Hex(0xE6EDF3),
				Land = Hex(0x4C4A3C),
				Drying = Hex(0x3F5A3A),
				Unsafe = Hex(0x2C78B8),
				Caution = Hex(0x1A4A78),
				Deep = Hex(0x142C4A),
				Contour = Hex(0x5F93C4),
				SafetyContour = Hex(0xA8E0FF),
				Coastline = Hex(0xD8D3B8),
				Channel = Hex(0xD98BE0),
				Hazard = Hex(0xF2F5F7),
				Bridge = Hex(0xB8C4CF),
				Barge = Hex(0xFF9A3C),
				Landing = instruments.Text,
				AidPort = instruments.Port,
				AidStarboard = instruments.Starboard,
				AidSpecial = Hex(0xFFC53D),
				AidOther = Hex(0xF2F5F7),
				LightFlare = Hex(0xFFE28A),
				OwnShip = Hex(0xF2F5F7),
				HeadingLine = Hex(0xFFFFFF),
				CourseVector = instruments.SeriesB,
				Track = Hex(0xA2B1BE),
				Target = instruments.SeriesA,
				TargetAlarm = instruments.Alarm,
				TargetAlarmText = instruments.AlarmText,
				Selection = Hex(0xFFFFFF),
				Visibility = Hex(0xA2B1BE),
			};
		}

		private static ChartPalette CreateDusk()
		{
			var day = CreateDay();
			// Dimmed, slightly warmed day colours: the console stays as it is, the chart field drops to about 60%.
			return new ChartPalette
			{
				Kind = ChartPaletteKind.Dusk,
				Instruments = day.Instruments,
				NoData = Dim(day.NoData),
				Panel = day.Panel,
				Bezel = day.Bezel,
				Text = day.Text,
				TextDim = day.TextDim,
				Sounding = day.Sounding,
				Land = Dim(day.Land),
				Drying = Dim(day.Drying),
				Unsafe = Dim(day.Unsafe),
				Caution = Dim(day.Caution),
				Deep = Dim(day.Deep),
				Contour = Dim(day.Contour),
				SafetyContour = Dim(day.SafetyContour, 0.8f),
				Coastline = Dim(day.Coastline, 0.75f),
				Channel = Dim(day.Channel),
				Hazard = Dim(day.Hazard, 0.8f),
				Bridge = Dim(day.Bridge),
				Barge = Dim(day.Barge, 0.8f),
				Landing = day.Landing,
				AidPort = Dim(day.AidPort, 0.85f),
				AidStarboard = Dim(day.AidStarboard, 0.85f),
				AidSpecial = Dim(day.AidSpecial, 0.85f),
				AidOther = Dim(day.AidOther, 0.8f),
				LightFlare = Dim(day.LightFlare, 0.85f),
				OwnShip = day.OwnShip,
				HeadingLine = Dim(day.HeadingLine, 0.9f),
				CourseVector = day.CourseVector,
				Track = Dim(day.Track, 0.8f),
				Target = day.Target,
				TargetAlarm = day.TargetAlarm,
				TargetAlarmText = day.TargetAlarmText,
				Selection = Dim(day.Selection, 0.9f),
				Visibility = Dim(day.Visibility),
			};
		}

		private static ChartPalette CreateNight()
		{
			var instruments = InstrumentPalette.Night;
			return new ChartPalette
			{
				Kind = ChartPaletteKind.Night,
				Instruments = instruments,
				NoData = Hex(0x000000),
				Panel = instruments.Console,
				Bezel = instruments.Bezel,
				Text = instruments.Text,
				TextDim = instruments.TextDim,
				Sounding = instruments.Text,
				Land = Hex(0x2A1408),
				Drying = Hex(0x301A06),
				Unsafe = Hex(0x4A1408),
				Caution = Hex(0x2A0C05),
				Deep = Hex(0x120402),
				Contour = Hex(0x5A1E0E),
				SafetyContour = Hex(0xB8341A),
				Coastline = Hex(0x8C3A1C),
				Channel = Hex(0x7A2A14),
				Hazard = Hex(0xC0401E),
				Bridge = Hex(0x7A3018),
				Barge = Hex(0xA8501A),
				Landing = instruments.Text,
				AidPort = Hex(0xB8341A),
				AidStarboard = Hex(0x9A5A18),
				AidSpecial = Hex(0xA8620E),
				AidOther = Hex(0xA8341A),
				LightFlare = Hex(0xB0601A),
				OwnShip = instruments.Needle,
				HeadingLine = instruments.Needle,
				CourseVector = instruments.SeriesB,
				Track = Hex(0x6A2410),
				Target = Hex(0xC03C1E),
				TargetAlarm = instruments.Alarm,
				TargetAlarmText = instruments.AlarmText,
				Selection = instruments.Needle,
				Visibility = Hex(0x7A2A14),
			};
		}

		private static Color Dim(Color color, float factor = 0.6f) => new Color(color.r * factor, color.g * factor, color.b * factor, color.a);

		private static Color Hex(uint rgb, float alpha = 1f) =>
			new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
	}
}
