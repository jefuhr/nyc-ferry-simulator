using System;
using System.IO;
using System.Linq;
using System.Text;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Sim.Tests
{
	/// <summary>Guards legacy bytes, dropped jog demand, lossy v2 values and header/row disagreement.</summary>
	public class ControlTraceV2Tests
	{
		public const string V2Header = ControlTrace.HeaderRow + ",nfu_jog";

		[Test]
		public void LegacyFixtureWritesIdenticalBytes()
		{
			var lines = File.ReadAllLines(TestSupport.FixturePath("ahead-then-hard-over.csv"));
			var comments = lines.Where(line => line.StartsWith("# ", StringComparison.Ordinal)).Select(line => line.Substring(2));
			var writer = new StringWriter();
			ControlTrace.Parse(lines).Write(writer, comments);
			var legacy = string.Join("\n", lines.Take(3)) + "\n0.00,1,1,0\n60.00,1,1,1\n120.00,1,1,1\n";
			Assert.That(Encoding.UTF8.GetBytes(writer.ToString()), Is.EqualTo(Encoding.UTF8.GetBytes(legacy)));
		}

		[Test]
		public void FiveColumnsRoundTripLosslessly()
		{
			var text = V2Header + "\n0.00,0.2,-0.4,0.7,0.12345678901234566\n0.03,0,0,-1,-1\n";
			var trace = ControlTrace.Parse(text.Split('\n'));
			Assert.That(trace.At(0).Jog, Is.EqualTo(0.12345678901234566));
			Assert.That(trace.At(3).Jog, Is.EqualTo(-1));
			var writer = new StringWriter();
			trace.Write(writer);
			Assert.That(writer.ToString(), Is.EqualTo(text));
		}

		[TestCase(ControlTrace.HeaderRow, "0,0,0,0,1")]
		[TestCase(V2Header, "0,0,0,0")]
		[TestCase(V2Header, "0,0,0,0,NaN")]
		[TestCase(V2Header, "0,0,0,0,1.1")]
		public void HeaderDeterminesExactCellCountAndJogRange(string header, string row) =>
			Assert.That(() => ControlTrace.Parse(new[] { header, row }), Throws.TypeOf<FormatException>());

		[Test]
		public void CanonicalPreservesJogAndNormalizesNegativeZero()
		{
			Assert.That(FixedStepDriver.Canonical(new ControlInputs(0, 0, 0, -0.75)).Jog, Is.EqualTo(-0.75));
			Assert.That(BitConverter.DoubleToInt64Bits(FixedStepDriver.Canonical(new ControlInputs(0, 0, 0, -0.0)).Jog), Is.Zero);
		}
	}
}
