using System;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	public class NfuTraceTests
	{
		[TestCase(false, 0)]
		[TestCase(true, 1)]
		[TestCase(true, 0)]
		public void BothTraceVersionsReplay_AndTelemetryPreservesTheFormat(bool jog, double demand)
		{
			using var scratch = new TestSupport.ScratchDirectory();
			var trace = scratch.File("trace.csv"); var output = scratch.File("output.csv");
			File.WriteAllText(trace, "time_s,port_lever,starboard_lever,helm" + (jog ? $",nfu_jog\n0,0,0,-1,{demand}\n.1,0,0,0,0\n" : "\n0,0,0,-1\n.1,0,0,0\n"));
			var factory = new FakeFactory();
			var (code, _, error) = TestSupport.Run(factory, "run", "--vessel", TestSupport.Vessel("t-class.json"), "--inputs", trace, "--out", output);
			Assert.That(code, Is.Zero, error);
			Assert.That(factory.Created.Single().Received[0].Jog, Is.EqualTo(demand));
			var lines = File.ReadAllLines(output);
			Assert.That(lines[0], Is.EqualTo("# ferrysim-csv: 1"));
			Assert.That(lines.First(l => !l.StartsWith("#")).EndsWith(jog ? ",helm,nfu_jog" : ",helm"), Is.True);
			var table = TelemetryTable.Parse(lines);
			if (jog) Assert.That(table.Column("nfu_jog")[0], Is.EqualTo(demand));
		}

		[TestCase("time_s,port_lever,starboard_lever,helm", "0,0,0,0,1")]
		[TestCase("time_s,port_lever,starboard_lever,helm,nfu_jog", "0,0,0,0")]
		[TestCase("time_s,port_lever,starboard_lever,helm,nfu_jog", "0,0,0,0,2")]
		public void WidthAndJogRangeAreStrict(string header, string row) => Assert.Throws<CliException>(() => TraceInputs.Parse(new[] { header, row }));

		[Test]
		public void LegacyRecorderRefusesJog_AndOptInAddsOnlyTheTrailingColumn()
		{
			var inputs = new ControlInputs(0, 0, 0, 1);
			Assert.Throws<CliException>(() => new CsvRecorder().Add(default, default, inputs));
			var recorder = new CsvRecorder(true); recorder.Add(default, default, inputs);
			var text = recorder.Render(Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>());
			Assert.That(TelemetryTable.Parse(text.Split('\n')).Column("nfu_jog")[0], Is.EqualTo(1));
		}
	}
}
