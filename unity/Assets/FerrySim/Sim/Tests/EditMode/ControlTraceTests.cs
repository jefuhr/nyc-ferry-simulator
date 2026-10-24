using System;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>
	/// The control trace shared with <c>ferrysim run --inputs</c>. Failure modes guarded: lossy number formatting
	/// (a replay then diverges in the last bits), times that drift off the 10 ms grid when read back
	/// (0.07 × 100 is 7.000000000000001), a row applied a step early or late, controls before the first row
	/// not neutral, and accepting traces the CLI would reject.
	/// </summary>
	public class ControlTraceTests
	{
		[Test]
		public void Parse_FollowsTheCliRules()
		{
			var trace = ControlTrace.Parse(new[]
			{
				"# a comment",
				string.Empty,
				"time_s, port_lever, starboard_lever, helm",
				"0.5,0.2,0.2,0",
				"# comments may sit between rows",
				"1.014,1,-1,0.5",
				"1.016,-0.25,0.75,-1",
			});

			Assert.That(trace.Count, Is.EqualTo(3));
			Assert.That(trace.StepAt(0), Is.EqualTo(50L));
			Assert.That(trace.StepAt(1), Is.EqualTo(101L), "1.014 s rounds to the 101st step");
			Assert.That(trace.StepAt(2), Is.EqualTo(102L), "1.016 s rounds to the 102nd step");
			Assert.That(trace.At(0), Is.EqualTo(ControlInputs.Neutral), "neutral before the first row");
			Assert.That(trace.At(49), Is.EqualTo(ControlInputs.Neutral));
			Assert.That(trace.At(50), Is.EqualTo(new ControlInputs(0.2, 0.2, 0.0)));
			Assert.That(trace.At(100), Is.EqualTo(new ControlInputs(0.2, 0.2, 0.0)), "a row holds until the next");
			Assert.That(trace.At(101), Is.EqualTo(new ControlInputs(1.0, -1.0, 0.5)));
			Assert.That(trace.At(1000000), Is.EqualTo(new ControlInputs(-0.25, 0.75, -1.0)), "the last row holds forever");
			Assert.That(trace.EndStep, Is.EqualTo(102L));
		}

		[TestCase("0,0,0,0", Description = "no header row")]
		[TestCase("time_s,port_lever,starboard_lever,helm|1,0,0,0|1,0,0,0", Description = "time does not increase")]
		[TestCase("time_s,port_lever,starboard_lever,helm|1,0,0,0|1.004,0,0,0", Description = "same step after rounding")]
		[TestCase("time_s,port_lever,starboard_lever,helm|1,1.5,0,0", Description = "lever out of range")]
		[TestCase("time_s,port_lever,starboard_lever,helm|-1,0,0,0", Description = "negative time")]
		[TestCase("time_s,port_lever,starboard_lever,helm|1,0,0", Description = "three columns")]
		[TestCase("time_s,port_lever,starboard_lever,helm|1,NaN,0,0", Description = "not a number")]
		[TestCase("time_s,port_lever,starboard_lever,helm", Description = "no data rows")]
		public void Parse_RejectsWhatTheCliRejects(string text)
		{
			Assert.That(() => ControlTrace.Parse(text.Split('|')), Throws.TypeOf<FormatException>());
		}

		[Test]
		public void FormatValue_RoundTripsEveryDoubleExactly()
		{
			var random = new Random(11);
			for (var i = 0; i < 100000; i++)
			{
				var value = (random.NextDouble() * 2.0 - 1.0) * Math.Pow(10.0, -random.Next(0, 15));
				var text = ControlTrace.FormatValue(value);
				Assert.That(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), Is.EqualTo(value), text);
			}

			Assert.That(ControlTrace.FormatValue(0.2), Is.EqualTo("0.2"), "short when short is exact");
			Assert.That(ControlTrace.FormatValue(-0.0), Is.EqualTo("0"), "-0 is written as 0");
			Assert.That(ControlTrace.FormatValue(1.0), Is.EqualTo("1"));
		}

		[Test]
		public void FormatTime_ReadsBackToTheSameStep()
		{
			for (long step = 0; step < 200000; step += 7)
			{
				var text = ControlTrace.FormatTime(step);
				var time = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
				Assert.That((long)Math.Round(time * SimulationClock.StepsPerSecond), Is.EqualTo(step), text);
			}
		}

		[Test]
		public void WriteThenParse_ReproducesTheTraceExactly()
		{
			var random = new Random(5);
			var trace = new ControlTrace();
			long step = 0;
			for (var i = 0; i < 2000; i++)
			{
				step += 1 + random.Next(0, 40);
				trace.Append(step, new ControlInputs(random.NextDouble() * 2.0 - 1.0, random.NextDouble() * 2.0 - 1.0, Math.Sin(i)));
			}

			var writer = new StringWriter();
			trace.Write(writer, new[] { "written by a test" });
			var read = ControlTrace.Parse(writer.ToString().Split('\n'));

			Assert.That(read.Count, Is.EqualTo(trace.Count));
			for (var i = 0; i < trace.Count; i++)
			{
				Assert.That(read.StepAt(i), Is.EqualTo(trace.StepAt(i)));
				Assert.That(read.ControlsAt(i), Is.EqualTo(trace.ControlsAt(i)));
			}
		}

		[Test]
		public void Append_RejectsStepsThatDoNotIncrease()
		{
			var trace = new ControlTrace();
			trace.Append(10, ControlInputs.Neutral);

			Assert.That(() => trace.Append(10, ControlInputs.Neutral), Throws.ArgumentException);
			Assert.That(() => trace.Append(-1, ControlInputs.Neutral), Throws.InstanceOf<ArgumentOutOfRangeException>());
		}

		[Test]
		public void TrajectoryFixture_IsTheAheadThenHardOverTrace()
		{
			var trace = ControlTrace.Load(TestSupport.FixturePath("ahead-then-hard-over.csv"));

			Assert.That(trace.EndStep, Is.EqualTo(12000L), "120 s");
			Assert.That(trace.At(0), Is.EqualTo(new ControlInputs(1.0, 1.0, 0.0)));
			Assert.That(trace.At(5999), Is.EqualTo(new ControlInputs(1.0, 1.0, 0.0)));
			Assert.That(trace.At(6000), Is.EqualTo(new ControlInputs(1.0, 1.0, 1.0)), "hard over to starboard at 60 s");
		}
	}
}
