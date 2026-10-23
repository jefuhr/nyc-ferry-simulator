using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: an input applied a step early or late, a row not held until the next,
	// controls before the first row, rows out of order, and out-of-range values being silently clamped.
	[TestFixture]
	public sealed class TraceReplayTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		private static FakeFactory Replay(TestSupport.ScratchDirectory scratch, string trace, out string outPath, params string[] extra)
		{
			var factory = new FakeFactory();
			var inputs = scratch.File("trace.csv");
			outPath = scratch.File("out.csv");
			File.WriteAllText(inputs, trace);
			var args = new[] { "run", "--vessel", Vessel, "--inputs", inputs, "--out", outPath }.Concat(extra).ToArray();
			var (code, _, stderr) = TestSupport.Run(factory, args);
			Assert.That(code, Is.EqualTo(0), stderr);
			return factory;
		}

		[Test]
		public void EachRowApplies_FromItsTimeUntilTheNextRow()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = Replay(scratch, "time_s,port_lever,starboard_lever,helm\n0,0,0,0\n1.0,1,0.5,0.25\n2.5,-1,-1,0\n3.0,0,0,0\n", out _);

				var received = factory.Created.Single().Received;
				Assert.That(received.Count, Is.EqualTo(300), "ends at the last row's time");
				Assert.That(received[0], Is.EqualTo(new ControlInputs(0, 0, 0)));
				Assert.That(received[99], Is.EqualTo(new ControlInputs(0, 0, 0)), "still the first row at t = 0.99 s");
				Assert.That(received[100], Is.EqualTo(new ControlInputs(1, 0.5, 0.25)), "second row starts exactly at t = 1.00 s");
				Assert.That(received[249], Is.EqualTo(new ControlInputs(1, 0.5, 0.25)), "held until t = 2.50 s");
				Assert.That(received[250], Is.EqualTo(new ControlInputs(-1, -1, 0)));
				Assert.That(received[299], Is.EqualTo(new ControlInputs(-1, -1, 0)));
			}
		}

		[Test]
		public void ControlsAreNeutralBeforeTheFirstRow()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var factory = Replay(scratch, "time_s,port_lever,starboard_lever,helm\n0.5,1,1,1\n1.0,1,1,1\n", out _);

				var received = factory.Created.Single().Received;
				Assert.That(received[49], Is.EqualTo(ControlInputs.Neutral));
				Assert.That(received[50], Is.EqualTo(new ControlInputs(1, 1, 1)));
			}
		}

		[Test]
		public void DurationOption_ExtendsOrShortensTheReplay()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var longer = Replay(scratch, "time_s,port_lever,starboard_lever,helm\n0,0.5,0.5,0\n1,0.5,0.5,0\n", out var outPath, "--duration-s", "4");

				Assert.That(longer.Created.Single().Received.Count, Is.EqualTo(400));
				var table = TelemetryTable.Load(outPath);
				Assert.That(table.Time[table.RowCount - 1], Is.EqualTo(4.0).Within(1e-9));
				Assert.That(table.PortLever[table.RowCount - 1], Is.EqualTo(0.5), "last row is held to the end");
			}
		}

		[Test]
		public void RecordedControlColumns_ShowTheCommandApplyingAtEachRow()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				Replay(scratch, "time_s,port_lever,starboard_lever,helm\n0,0,0,0\n1.0,1,0.5,0.25\n2.0,1,0.5,0.25\n", out var outPath);

				var table = TelemetryTable.Load(outPath);
				Assert.That(table.Time[9], Is.EqualTo(0.9).Within(1e-9));
				Assert.That(table.PortLever[9], Is.EqualTo(0.0));
				Assert.That(table.Time[10], Is.EqualTo(1.0).Within(1e-9));
				Assert.That(table.PortLever[10], Is.EqualTo(1.0));
				Assert.That(table.StarboardLever[10], Is.EqualTo(0.5));
				Assert.That(table.Helm[10], Is.EqualTo(0.25));
				Assert.That(table.HeaderValue("maneuver"), Is.EqualTo("trace-replay"));
				Assert.That(table.HeaderValue("param.inputs"), Is.EqualTo("trace.csv"));
			}
		}

		[Test]
		public void TimesRoundToTheNearestPhysicsStep()
		{
			var trace = TraceInputs.Parse(new[] { "time_s,port_lever,starboard_lever,helm", "0,0,0,0", "0.104,1,1,0" });

			Assert.That(trace.At(9), Is.EqualTo(ControlInputs.Neutral));
			Assert.That(trace.At(10), Is.EqualTo(new ControlInputs(1, 1, 0)));
		}

		[TestCase("0,0,0,0\n1,1,1,0\n", TestName = "missing header")]
		[TestCase("time_s,port,starboard,helm\n0,0,0,0\n", TestName = "wrong header")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n", TestName = "no rows")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n0,0,0\n", TestName = "short row")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n0,0,0,x\n", TestName = "not a number")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n0,1.5,0,0\n", TestName = "lever above range")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n0,0,0,-2\n", TestName = "helm below range")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n-1,0,0,0\n", TestName = "negative time")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n2,0,0,0\n1,0,0,0\n", TestName = "time goes backwards")]
		[TestCase("time_s,port_lever,starboard_lever,helm\n1,0,0,0\n1,1,1,0\n", TestName = "duplicate time")]
		public void MalformedTraces_AreRejectedWithExitCode1(string text)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var inputs = scratch.File("bad.csv");
				File.WriteAllText(inputs, text);

				var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "run", "--vessel", Vessel, "--inputs", inputs, "--out", scratch.File("o.csv"));

				Assert.That(code, Is.EqualTo(1), stderr);
				Assert.That(stderr, Does.Contain("ferrysim: error:"));
				Assert.That(File.Exists(scratch.File("o.csv")), Is.False, "no partial output");
			}
		}

		[Test]
		public void CommentsAndBlankLinesAreIgnored()
		{
			var trace = TraceInputs.Parse(new[] { "# a trace", "", "time_s,port_lever,starboard_lever,helm", "# neutral", "0,0,0,0", "", "1,1,1,1" });

			Assert.That(trace.Count, Is.EqualTo(2));
			Assert.That(trace.EndTime, Is.EqualTo(1.0));
		}
	}
}
