using System;
using System.Collections.Generic;
using FerrySim.Core.Instruments;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Instruments
{
	public class ChannelNamesTests
	{
		[Test]
		public void EveryChannelHasExactlyOneName()
		{
			var channels = (Channel[])Enum.GetValues(typeof(Channel));
			Assert.That(ChannelNames.Count, Is.EqualTo(channels.Length), "the name list and the enum must stay in step");

			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var channel in channels)
			{
				var name = ChannelNames.Name(channel);
				Assert.That(name, Is.Not.Null.And.Not.Empty, $"{channel} has no name");
				Assert.That(seen.Add(name), Is.True, $"{name} names more than one channel");
			}
		}

		[Test]
		public void NamesRoundTrip()
		{
			foreach (Channel channel in Enum.GetValues(typeof(Channel)))
			{
				Assert.That(ChannelNames.TryParse(ChannelNames.Name(channel), out var parsed), Is.True, channel.ToString());
				Assert.That(parsed, Is.EqualTo(channel));
			}
		}

		[Test]
		public void NamesAreLowerCaseDotSeparatedWords()
		{
			foreach (Channel channel in Enum.GetValues(typeof(Channel)))
			{
				Assert.That(ChannelNames.Name(channel), Does.Match("^[a-z]+(-[a-z]+)*(\\.[a-z]+(-[a-z]+)*)+$"), channel.ToString());
			}
		}

		[Test]
		public void UnknownNamesAreRejected()
		{
			Assert.That(ChannelNames.TryParse("engine.port.rpm", out _), Is.False, "display units are not channel names");
			Assert.That(ChannelNames.TryParse("NAV.SOG", out _), Is.False, "names are case-sensitive");
			Assert.That(ChannelNames.TryParse(null, out _), Is.False);
		}

		[Test]
		public void TheTwoEnginesDifferOnlyInTheSideToken()
		{
			var port = new List<string>();
			var starboard = new List<string>();
			foreach (Channel channel in Enum.GetValues(typeof(Channel)))
			{
				var name = ChannelNames.Name(channel);
				if (name.StartsWith("engine.port.", StringComparison.Ordinal)) port.Add(name.Replace("engine.port.", "engine.{side}."));
				if (name.StartsWith("engine.starboard.", StringComparison.Ordinal)) starboard.Add(name.Replace("engine.starboard.", "engine.{side}."));
			}

			Assert.That(port, Is.Not.Empty);
			Assert.That(starboard, Is.EqualTo(port), "a skin written with {side} must work for either engine");
		}

		[Test]
		public void OutOfRangeChannelThrows()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => ChannelNames.Name((Channel)(-1)));
			Assert.Throws<ArgumentOutOfRangeException>(() => ChannelNames.Name((Channel)ChannelNames.Count));
		}
	}
}
