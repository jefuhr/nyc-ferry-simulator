using System;
using System.Collections.Generic;
using FerrySim.Core.Instruments;
using UnityEngine;

namespace FerrySim.UI.Displays
{
	/// <summary>A coloured range on a dial or bar (a warning arc, a red line), in the element's display unit.</summary>
	public readonly struct SkinBand
	{
		public SkinBand(double low, double high, SkinColour colour)
		{
			Low = low;
			High = high;
			Colour = colour;
		}

		public double Low { get; }

		public double High { get; }

		public SkinColour Colour { get; }
	}

	/// <summary>
	/// One element of a loaded skin, already validated: every name in it is known and every property applies to its
	/// type. Channel and alarm names keep their <c>{side}</c> token until a view is created for a side.
	/// </summary>
	public sealed class SkinElement
	{
		public const string SideToken = "{side}";

		/// <summary>Default needle sweep, degrees clockwise from straight up: the panel tachometer's 240°.</summary>
		public const float DefaultSweepStart = -120f;

		public const float DefaultSweepEnd = 120f;

		private static readonly IReadOnlyList<SkinBand> NoBands = new SkinBand[0];

		internal SkinElement(int number)
		{
			Number = number;
			Unit = DisplayUnit.None;
			Decimals = -1;
			SweepStart = DefaultSweepStart;
			SweepEnd = DefaultSweepEnd;
			Bands = NoBands;
		}

		/// <summary>Position in the skin's element list, counting from 1 as error messages do.</summary>
		public int Number { get; }

		public SkinElementType Type { get; internal set; }

		/// <summary>Where the element goes, in reference pixels from the top left.</summary>
		public Rect Rect { get; internal set; }

		/// <summary>The page it belongs to; null for every page.</summary>
		public string Page { get; internal set; }

		/// <summary>The channel name as written, with any <c>{side}</c> token; null when the element has none.</summary>
		public string ChannelName { get; internal set; }

		/// <summary>The alarm id as written, with any <c>{side}</c> token; null when the element has none.</summary>
		public string AlarmId { get; internal set; }

		public bool HasRange { get; internal set; }

		/// <summary>The low end of a dial or bar, in the display unit.</summary>
		public double Low { get; internal set; }

		/// <summary>The high end of a dial or bar, in the display unit.</summary>
		public double High { get; internal set; }

		public float SweepStart { get; internal set; }

		public float SweepEnd { get; internal set; }

		public DisplayUnit Unit { get; internal set; }

		/// <summary>Digits after the decimal point; -1 when the skin gives none (a dial then shows no digital readout).</summary>
		public int Decimals { get; internal set; }

		/// <summary>The caption as written, with any <c>{side}</c> token; null when absent.</summary>
		public string Text { get; internal set; }

		/// <summary>The page a softkey opens; null when it has none.</summary>
		public string Target { get; internal set; }

		public SoftkeyAction Action { get; internal set; }

		/// <summary>The skin's colour for this element; null for the type's default.</summary>
		public SkinColour? Colour { get; internal set; }

		public SkinValueFormat Format { get; internal set; }

		/// <summary>A list's group filter; null for every group.</summary>
		public AlarmGroup? Group { get; internal set; }

		public IReadOnlyList<SkinBand> Bands { get; internal set; }

		/// <summary>True when a channel, alarm or caption of this element names <c>{side}</c>.</summary>
		public bool UsesSide =>
			Contains(ChannelName) || Contains(AlarmId) || Contains(Text);

		/// <summary>"element 3 (value engine.{side}.oil-pressure)", as error messages name it.</summary>
		public string Describe()
		{
			var name = ChannelName ?? AlarmId ?? Target ?? Text;
			return "element " + Number + " (" + TypeName(Type) + (name != null ? " " + name : "") + ")";
		}

		/// <summary>The channel this element shows on <paramref name="side"/>'s display.</summary>
		public Channel ResolveChannel(DisplaySide side)
		{
			if (ChannelName == null)
			{
				throw new InvalidOperationException(Describe() + " has no channel");
			}

			var name = Substitute(ChannelName, side, Describe());
			if (!ChannelNames.TryParse(name, out var channel))
			{
				throw new InvalidOperationException(Describe() + ": unknown channel '" + name + "'");
			}

			return channel;
		}

		/// <summary>The alarm id this element follows on <paramref name="side"/>'s display.</summary>
		public string ResolveAlarm(DisplaySide side) =>
			AlarmId == null ? null : Substitute(AlarmId, side, Describe());

		/// <summary>The caption with <c>{side}</c> replaced by PORT or STARBOARD.</summary>
		public string ResolveText(DisplaySide side)
		{
			if (Text == null || !Contains(Text))
			{
				return Text;
			}

			if (side == DisplaySide.None)
			{
				throw new InvalidOperationException(Describe() + " names {side} but the display has no side");
			}

			return Text.Replace(SideToken, side == DisplaySide.Port ? "PORT" : "STARBOARD");
		}

		public static string TypeName(SkinElementType type)
		{
			switch (type)
			{
				case SkinElementType.Dial: return "dial";
				case SkinElementType.Bar: return "bar";
				case SkinElementType.Value: return "value";
				case SkinElementType.Lamp: return "lamp";
				case SkinElementType.Text: return "text";
				case SkinElementType.Softkey: return "softkey";
				case SkinElementType.Frame: return "frame";
				default: return "list";
			}
		}

		/// <summary>Replaces <c>{side}</c> in a channel or alarm name with <c>port</c> or <c>starboard</c>.</summary>
		public static string Substitute(string name, DisplaySide side, string owner)
		{
			if (!Contains(name))
			{
				return name;
			}

			if (side == DisplaySide.None)
			{
				throw new InvalidOperationException(owner + " names {side} but the display has no side");
			}

			return name.Replace(SideToken, side == DisplaySide.Port ? "port" : "starboard");
		}

		private static bool Contains(string text) => text != null && text.IndexOf(SideToken, StringComparison.Ordinal) >= 0;
	}
}
