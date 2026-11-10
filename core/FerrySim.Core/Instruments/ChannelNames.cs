using System;

namespace FerrySim.Core.Instruments
{
	/// <summary>
	/// The stable text name of each <see cref="Channel"/>, as written in display skins and the control
	/// map. Names are lower-case, dot-separated and never change once published. The two engines differ
	/// only in the side token, so a skin can write <c>engine.{side}.speed</c> and be used for either.
	/// </summary>
	public static class ChannelNames
	{
		// Indexed by the numeric value of Channel; keep the two lists in step (ChannelNamesTests).
		private static readonly string[] Names =
		{
			"lever.port",
			"lever.starboard",
			"tiller.order",
			"steering.jog",
			"steering.nfu-active",
			"rudder.order",
			"rudder.port",
			"rudder.starboard",
			"nav.heading",
			"nav.rate-of-turn",
			"nav.cog",
			"nav.sog",
			"nav.stw",
			"nav.depth-under-keel",
			"engine.port.speed",
			"engine.port.shaft-speed",
			"engine.port.shift-phase",
			"engine.port.load",
			"engine.port.oil-pressure",
			"engine.port.coolant-temperature",
			"engine.port.exhaust-temperature",
			"engine.port.boost-pressure",
			"engine.port.fuel-rate",
			"engine.port.gear-oil-pressure",
			"engine.starboard.speed",
			"engine.starboard.shaft-speed",
			"engine.starboard.shift-phase",
			"engine.starboard.load",
			"engine.starboard.oil-pressure",
			"engine.starboard.coolant-temperature",
			"engine.starboard.exhaust-temperature",
			"engine.starboard.boost-pressure",
			"engine.starboard.fuel-rate",
			"engine.starboard.gear-oil-pressure",
			"electrical.bus-voltage",
		};

		/// <summary>Number of channels. Readouts size their storage with this.</summary>
		public static int Count => Names.Length;

		/// <summary>The published name of <paramref name="channel"/>.</summary>
		public static string Name(Channel channel)
		{
			var index = (int)channel;
			if (index < 0 || index >= Names.Length) throw new ArgumentOutOfRangeException(nameof(channel));
			return Names[index];
		}

		/// <summary>Finds a channel by its published name. Names are case-sensitive.</summary>
		public static bool TryParse(string name, out Channel channel)
		{
			for (var i = 0; i < Names.Length; i++)
			{
				if (string.Equals(Names[i], name, StringComparison.Ordinal))
				{
					channel = (Channel)i;
					return true;
				}
			}

			channel = default;
			return false;
		}
	}
}
