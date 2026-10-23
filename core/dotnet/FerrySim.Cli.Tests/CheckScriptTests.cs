using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded for the Unity step of scripts/check.sh: trusting Unity's exit code (it can
	// be 0 with failing tests), a stale XML from an earlier run passing the step, relative result paths
	// (Unity resolves them against the project directory), PlayMode run without graphics, Unity run
	// outside the shared lock, and a missing unity/ directory failing instead of printing SKIPPED.
	// A stand-in editor script plays Unity, so no editor is needed.
	[TestFixture]
	public sealed class CheckScriptTests
	{
		private const string FakeEditor = @"#!/bin/bash
mode=""${FAKE_UNITY_MODE:-pass}""
platform=""""
results=""""
args=(""$@"")
for ((i = 0; i < ${#args[@]}; i++)); do
	case ""${args[$i]}"" in
		-testPlatform) platform=""${args[$((i + 1))]}"" ;;
		-testResults) results=""${args[$((i + 1))]}"" ;;
	esac
done
echo ""$platform|$*"" >> ""$FAKE_UNITY_LOG""
passed='<test-run id=""2"" testcasecount=""3"" result=""Passed"" total=""3"" passed=""3"" failed=""0"" inconclusive=""0"" skipped=""0""><test-suite type=""Assembly"" result=""Passed"" failed=""0""/></test-run>'
failed='<test-run id=""2"" testcasecount=""3"" result=""Failed"" total=""3"" passed=""2"" failed=""1"" inconclusive=""0"" skipped=""0""><test-suite type=""Assembly"" result=""Failed"" failed=""1""/></test-run>'
setup='<test-run id=""2"" testcasecount=""0"" result=""Failed"" total=""0"" passed=""0"" failed=""0"" inconclusive=""0"" skipped=""0""><test-suite type=""Assembly"" result=""Failed"" failed=""0""/></test-run>'
case ""$mode"" in
	pass) echo ""$passed"" > ""$results"" ;;
	fail-xml) echo ""$failed"" > ""$results"" ;;
	fail-setup) echo ""$setup"" > ""$results"" ;;
	fail-playmode) if [[ ""$platform"" == PlayMode ]]; then echo ""$failed"" > ""$results""; else echo ""$passed"" > ""$results""; fi ;;
	exit1) echo ""$passed"" > ""$results""; exit 1 ;;
	no-xml) ;;
esac
exit 0
";

		private TestSupport.ScratchDirectory _scratch;
		private string _root;

		[SetUp]
		public void SetUp()
		{
			if (!File.Exists("/bin/bash") || !File.Exists("/usr/bin/lockf"))
			{
				Assert.Ignore("needs bash and lockf (macOS)");
			}

			_scratch = new TestSupport.ScratchDirectory();
			_root = Path.Combine(_scratch.Path, "repo");
			Directory.CreateDirectory(Path.Combine(_root, "scripts"));
			File.Copy(Path.Combine(TestSupport.RepoRoot, "scripts", "check.sh"), Path.Combine(_root, "scripts", "check.sh"));
		}

		[TearDown]
		public void TearDown()
		{
			_scratch?.Dispose();
		}

		private void AddUnityProject()
		{
			Directory.CreateDirectory(Path.Combine(_root, "unity", "ProjectSettings"));
			File.WriteAllText(Path.Combine(_root, "unity", "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.27f1\n");
		}

		private string AddFakeEditor()
		{
			var path = _scratch.File("fake-unity.sh");
			File.WriteAllText(path, FakeEditor.Replace("\r\n", "\n"));
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}

			return path;
		}

		private (int Code, string Output) Check(string editor, string mode, params string[] args)
		{
			var start = new ProcessStartInfo("/bin/bash")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				WorkingDirectory = _scratch.Path,
			};
			start.ArgumentList.Add(Path.Combine(_root, "scripts", "check.sh"));
			foreach (var arg in args)
			{
				start.ArgumentList.Add(arg);
			}

			start.Environment["HOME"] = _scratch.Path;
			start.Environment["UNITY_EDITOR"] = editor;
			start.Environment["FAKE_UNITY_MODE"] = mode;
			start.Environment["FAKE_UNITY_LOG"] = _scratch.File("unity-invocations.log");
			using (var process = Process.Start(start))
			{
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				Assert.That(process.WaitForExit(120000), Is.True, "check.sh timed out");
				return (process.ExitCode, stdout.Result + stderr.Result);
			}
		}

		private string[] Invocations() => File.ReadAllLines(_scratch.File("unity-invocations.log"));

		[Test]
		public void PassingResults_PassTheStep_WithAbsolutePathsHeadlessEditModeAndTheLock()
		{
			AddUnityProject();

			var (code, output) = Check(AddFakeEditor(), "pass", "--only", "unity");

			Assert.That(code, Is.EqualTo(0), output);
			Assert.That(output, Does.Contain("PASSED").And.Contain("Unity EditMode and PlayMode tests"));
			var calls = Invocations();
			Assert.That(calls, Has.Length.EqualTo(2));
			Assert.That(calls[0], Does.StartWith("EditMode|").And.Contain("-nographics"));
			Assert.That(calls[1], Does.StartWith("PlayMode|").And.Not.Contain("-nographics"));
			var artifacts = Path.Combine(_root, "artifacts", "unity");
			Assert.That(calls[0], Does.Contain("-testResults " + Path.Combine(artifacts, "editmode.xml")));
			Assert.That(calls[0], Does.Contain("-logFile " + Path.Combine(artifacts, "editmode.log")));
			Assert.That(calls[1], Does.Contain("-testResults " + Path.Combine(artifacts, "playmode.xml")));
			Assert.That(calls[0], Does.Contain("-projectPath " + Path.Combine(_root, "unity")));
			Assert.That(File.Exists(Path.Combine(_scratch.Path, "FerrySim-cache", "unity.lock")), Is.True, "run under lockf -k on the shared lock file");
		}

		[TestCase("fail-xml")]
		[TestCase("fail-setup")]
		[TestCase("no-xml")]
		[TestCase("exit1")]
		public void BadUnityOutcomes_FailTheStepEvenWhenTheEditorExitsZero(string mode)
		{
			AddUnityProject();

			var (code, output) = Check(AddFakeEditor(), mode, "--only", "unity");

			Assert.That(code, Is.EqualTo(1), output);
			Assert.That(output, Does.Contain("FAILED").And.Contain("RESULT: FAILED"));
		}

		[Test]
		public void FailingPlayModeFailsTheStepEvenThoughEditModePassed()
		{
			AddUnityProject();

			var (code, output) = Check(AddFakeEditor(), "fail-playmode", "--only", "unity");

			Assert.That(code, Is.EqualTo(1), output);
			Assert.That(Invocations(), Has.Length.EqualTo(2));
			Assert.That(output, Does.Contain("PlayMode tests did not pass"));
		}

		[Test]
		public void StaleResultsFromAnEarlierRunAreNotTrusted()
		{
			AddUnityProject();
			var artifacts = Path.Combine(_root, "artifacts", "unity");
			Directory.CreateDirectory(artifacts);
			File.WriteAllText(Path.Combine(artifacts, "editmode.xml"), "<test-run result=\"Passed\" failed=\"0\"></test-run>");

			var (code, output) = Check(AddFakeEditor(), "no-xml", "--only", "unity");

			Assert.That(code, Is.EqualTo(1), output);
		}

		[Test]
		public void MissingEditor_FailsWithAClearMessage()
		{
			AddUnityProject();

			var (code, output) = Check(_scratch.File("no-such-editor"), "pass", "--only", "unity");

			Assert.That(code, Is.EqualTo(1), output);
			Assert.That(output, Does.Contain("Unity editor not found"));
		}

		[Test]
		public void NoUnityProject_PrintsSkippedWithTheReasonAndExitsZero()
		{
			var (code, output) = Check(AddFakeEditor(), "pass", "--only", "unity");

			Assert.That(code, Is.EqualTo(0), output);
			Assert.That(output, Does.Contain("SKIPPED (unity/ProjectSettings/ProjectVersion.txt not found)"));
			Assert.That(File.Exists(_scratch.File("unity-invocations.log")), Is.False);
		}

		[Test]
		public void UnknownOnlyStep_IsAUsageError()
		{
			var (code, output) = Check(AddFakeEditor(), "pass", "--only", "bogus");

			Assert.That(code, Is.EqualTo(2), output);
			Assert.That(output, Does.Contain("usage:"));
		}
	}
}
