using System;
using System.Reflection;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>Protects the strict recording schema and UI jog values without coupling assembly wiring.</summary>
	public class TelemetryOptionalColumnTests
	{
		private static Type UiCsv => Type.GetType("FerrySim.UI.TelemetryCsv, FerrySim.Unity.UI", true);
		private static object ParseUi(string text) => UiCsv.GetMethod("Parse").Invoke(null, new object[] { text, "optional-test" });

		[TestCase(false)]
		[TestCase(true)]
		public void BothReadersAcceptBaseAndOptionalJog(bool jog)
		{
			var row = TelemetryCsv.FormatRow(default, default, ControlInputs.Neutral);
			var text = "# ferrysim-csv: 1\n" + TelemetryCsv.ColumnRow + (jog ? ",nfu_jog" : "") + "\n" + row + (jog ? ",0.3750" : "") + "\n";
			TelemetryCsv.Split(text.Split('\n'), "optional-test", out _, out var rows);
			Assert.That(rows.Count, Is.EqualTo(1));
			var recording = ParseUi(text);
			var sample = recording.GetType().GetMethod("Sample").Invoke(recording, new object[] { 0 });
			var controls = (ControlInputs)sample.GetType().GetProperty("Controls").GetValue(sample);
			Assert.That(controls.Jog, Is.EqualTo(jog ? 0.375 : 0));
			var writer = Type.GetType("FerrySim.UI.TelemetryCsvWriter, FerrySim.Unity.UI", true);
			Assert.That(writer.GetMethod("Render").Invoke(null, new[] { recording }), Is.EqualTo(text), "UI export keeps optional jog, including a present zero column");
		}

		[TestCase(",other")]
		[TestCase(",nfu_jog,other")]
		[TestCase(",nfu_jog,nfu_jog")]
		public void StrictReaderRejectsEveryOtherExtension(string suffix) =>
			Assert.That(() => TelemetryCsv.Split(new[] { "# ferrysim-csv: 1", TelemetryCsv.ColumnRow + suffix }, "bad-columns", out _, out _), Throws.TypeOf<FormatException>());

		[TestCase("NaN")]
		[TestCase("Infinity")]
		[TestCase("1.2")]
		public void UiRejectsInvalidJogInsteadOfClamping(string jog)
		{
			var text = "# ferrysim-csv: 1\n" + TelemetryCsv.ColumnRow + ",nfu_jog\n" + TelemetryCsv.FormatRow(default, default, ControlInputs.Neutral) + "," + jog;
			var error = Assert.Throws<TargetInvocationException>(() => ParseUi(text));
			Assert.That(error.InnerException.Message, Does.Contain("nfu_jog"));
		}
	}
}
