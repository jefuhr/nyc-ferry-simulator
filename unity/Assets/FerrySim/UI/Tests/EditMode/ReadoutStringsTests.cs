using System;
using System.Globalization;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.UI.Tests.EditMode
{
	public sealed class ReadoutStringsTests
	{
		[Test]
		public void SharedCaches_StayWithinTwoMiB()
		{
			Assert.That(ReadoutStrings.RetainedBytesUpperBound, NUnit.Framework.Is.LessThanOrEqualTo(2 * 1024 * 1024));
			TestContext.WriteLine("shared cache upper bound: " + ReadoutStrings.RetainedBytesUpperBound + " bytes");
		}

		[Test]
		public void Numbers_MatchIndependentInvariantFormatting_AtEveryCachedResolution()
		{
			for (var i = -1000; i <= 4000; i++)
				Assert.That(InstrumentFormat.Fixed(i, 0), NUnit.Framework.Is.EqualTo(i.ToString("F0", CultureInfo.InvariantCulture)));
			for (var i = -1000; i <= 1000; i++)
				Assert.That(InstrumentFormat.Fixed(i / 10.0, 1), NUnit.Framework.Is.EqualTo((i / 10.0).ToString("F1", CultureInfo.InvariantCulture)));
			Assert.That(ReadoutStrings.Contact(10, false, false), NUnit.Framework.Is.EqualTo("10 kN"));
			Assert.That(ReadoutStrings.Contact(9.9, true, true), NUnit.Framework.Is.EqualTo("PEAK 9.9 kN"));
		}

		[Test]
		public void ChangingOnlyTheScenarioDate_DoesNotReformatTheSameClockText()
		{
			var view = new WheelhouseView { HudVisible = true };
			var source = StubTelemetrySource.Underway();
			view.Refresh(source, 0);
			view.Refresh(source, 0.1);
			var clock = view.Panel.Clock.text;
			source.ScenarioStartUtc = source.ScenarioStartUtc.AddDays(1);
			Assert.That(() => view.Refresh(source, 0.2), Is.Not.AllocatingGCMemory());
			Assert.That(view.Panel.Clock.text, NUnit.Framework.Is.EqualTo(clock));
		}

		[Test]
		public void UnchangedDisplayedFallbackValues_AllocateNothing()
		{
			// Out-of-cache values still format exactly, and sub-resolution movement must not reformat them.
			var rpm = new ReadoutText();
			var speed = new ReadoutText();
			var depth = new ReadoutText();
			var rudder = new ReadoutText();
			var lever = new ReadoutText();
			var elapsed = new ReadoutText();
			rpm.Fixed(12345, 0);
			speed.Fixed(-123.4, 1);
			depth.Fixed(98765, 0);
			rudder.Rudder(Math.PI);
			lever.Lever(1.5);
			elapsed.Elapsed(ReadoutStrings.ElapsedSeconds + 1.2);
			Assert.That(() =>
			{
				rpm.Fixed(12345.01, 0);
				speed.Fixed(-123.401, 1);
				depth.Fixed(98765.1, 0);
				rudder.Rudder(Math.PI + 0.0001);
				lever.Lever(1.5001);
				elapsed.Elapsed(ReadoutStrings.ElapsedSeconds + 1.8);
			}, Is.Not.AllocatingGCMemory());
		}
	}
}
