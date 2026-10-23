using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: a typo silently ignored, a usage error that turns into a crash or an
	// exit code other than 2, a usage error reported only after slow work or file access, and
	// help requests treated as errors.
	[TestFixture]
	public sealed class ArgumentParsingTests
	{
		private static readonly string Vessel = TestSupport.Vessel("t-class.json");

		[TestCase("")]
		[TestCase("bogus")]
		[TestCase("maneuver")]
		[TestCase("maneuver --vessel v.json --out o.csv")]
		[TestCase("maneuver nonsense --vessel v.json --out o.csv")]
		[TestCase("maneuver turning-circle --out o.csv")]
		[TestCase("maneuver turning-circle --vessel v.json")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --bogus 1")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --speed-kn")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --speed-kn fast")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --speed-kn -5")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --speed-kn 5 --speed-kn 6")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --direction up")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --env current=fast")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --sample-hz 3")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --sample-hz 0")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv --duration-s 0")]
		[TestCase("maneuver turning-circle --vessel v.json --out o.csv extra")]
		[TestCase("maneuver pivot --vessel v.json --out o.csv --speed-kn 5")]
		[TestCase("maneuver acceleration --vessel v.json --out o.csv --speed-kn 5")]
		[TestCase("maneuver crash-stop --vessel v.json --out o.csv --direction port")]
		[TestCase("maneuver zigzag --vessel v.json --out o.csv --rudder-deg 40 --max-rudder-deg 35")]
		[TestCase("run --vessel v.json --out o.csv")]
		[TestCase("run --vessel v.json --inputs t.csv")]
		[TestCase("summarize")]
		[TestCase("summarize a.csv b.csv")]
		[TestCase("summarize a.csv --bogus")]
		[TestCase("summarize a.csv --json=yes")]
		public void BadArguments_ExitWithCode2AndPrintUsage(string commandLine)
		{
			var args = commandLine.Length == 0 ? new string[0] : commandLine.Split(' ');

			var (code, stdout, stderr) = TestSupport.Run(new FakeFactory(), args);

			Assert.That(code, Is.EqualTo(2), stderr);
			Assert.That(stderr, Does.StartWith(args.Length == 0 ? "ferrysim: No command given." : "ferrysim: "));
			Assert.That(stderr, Does.Contain("Usage:"));
			Assert.That(stdout, Is.Empty);
		}

		[Test]
		public void UsageErrorsAreReportedBeforeAnyFileIsTouched()
		{
			// The vessel file does not exist, but the bad option must win: exit 2, not 1.
			var (code, _, stderr) = TestSupport.Run(new FakeFactory(), "maneuver", "turning-circle", "--vessel", "missing.json", "--out", "o.csv", "--sample-hz", "7");

			Assert.That(code, Is.EqualTo(2), stderr);
			Assert.That(stderr, Does.Contain("--sample-hz"));
		}

		[TestCase("--help")]
		[TestCase("-h")]
		[TestCase("help")]
		[TestCase("maneuver --help")]
		[TestCase("summarize --help")]
		public void Help_PrintsUsageAndExits0(string commandLine)
		{
			var (code, stdout, stderr) = TestSupport.Run(new FakeFactory(), commandLine.Split(' '));

			Assert.That(code, Is.EqualTo(0));
			Assert.That(stdout, Does.Contain("Usage:").And.Contain("turning-circle").And.Contain("--env"));
			Assert.That(stderr, Is.Empty);
		}

		[Test]
		public void MissingFiles_ExitWithCode1NotUsage()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var missingVessel = TestSupport.Run(new FakeFactory(), "maneuver", "pivot", "--vessel", scratch.File("none.json"), "--out", scratch.File("o.csv"));
				var missingCsv = TestSupport.Run(new FakeFactory(), "summarize", scratch.File("none.csv"));
				var missingTrace = TestSupport.Run(new FakeFactory(), "run", "--vessel", Vessel, "--inputs", scratch.File("none.csv"), "--out", scratch.File("o.csv"));

				Assert.That(missingVessel.Code, Is.EqualTo(1), missingVessel.Err);
				Assert.That(missingCsv.Code, Is.EqualTo(1), missingCsv.Err);
				Assert.That(missingCsv.Err, Does.Contain("not found"));
				Assert.That(missingTrace.Code, Is.EqualTo(1), missingTrace.Err);
				Assert.That(missingVessel.Err, Does.Not.Contain("Usage:"));
			}
		}

		[Test]
		public void InlineEqualsFormIsAccepted()
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var (code, _, stderr) = TestSupport.Run(
					new FakeFactory(), "maneuver", "pivot", "--vessel=" + Vessel, "--out=" + scratch.File("o.csv"), "--duration-s=5");

				Assert.That(code, Is.EqualTo(0), stderr);
			}
		}
	}
}
