using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.World.Tiles3D.Tests
{
	/// <summary>
	/// The probe's command line and where its report goes (<see cref="ProbeOptions"/>). Failure modes guarded: the report
	/// written inside the .app bundle or the data folder rather than beside the executable; a -probe-out without a folder
	/// silently ignored; a relative folder left relative.
	/// </summary>
	public sealed class ProbeOptionsTests
	{
		[Test]
		public void TheOutSwitchNamesTheFolder()
		{
			var options = ProbeOptions.Parse(new[] { "probe", "-logFile", "x.log", "-probe-out", "/tmp/ferry-probe" }, "/default");

			Assert.That(options.OutputFolder, Is.EqualTo(Path.GetFullPath("/tmp/ferry-probe")));
		}

		[Test]
		public void WithoutTheSwitchTheReportGoesToTheDefault()
		{
			Assert.That(ProbeOptions.Parse(new[] { "probe" }, "/default/folder").OutputFolder, Is.EqualTo(Path.GetFullPath("/default/folder")));
		}

		[Test]
		public void TheSwitchWithoutAFolderIsAnError()
		{
			Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(new[] { "probe", "-probe-out" }, "/default"));
			Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(new[] { "probe", "-probe-out", "-logFile" }, "/default"));
		}

		[Test]
		public void ARelativeFolderIsTakenFromTheWorkingDirectory()
		{
			Assert.That(ProbeOptions.Parse(new[] { "probe", "-probe-out", "out" }, "/default").OutputFolder,
				Is.EqualTo(Path.Combine(Directory.GetCurrentDirectory(), "out")));
		}

		[Test]
		public void TheDefaultIsBesideTheExecutable()
		{
			Assert.That(ProbeOptions.DefaultFolder("C:/Games/FerrySim Tiles3D Probe/Tiles3DProbe_Data", RuntimePlatform.WindowsPlayer).Replace('\\', '/'),
				Is.EqualTo("C:/Games/FerrySim Tiles3D Probe"));
			Assert.That(ProbeOptions.DefaultFolder("/Users/crew/probe/Tiles3DProbe.app/Contents", RuntimePlatform.OSXPlayer),
				Is.EqualTo("/Users/crew/probe"));
			Assert.That(ProbeOptions.DefaultFolder("/repo/unity/Assets", RuntimePlatform.OSXEditor),
				Is.EqualTo(Path.Combine("/repo", "artifacts", "tiles3d-probe", "editor")));
		}
	}
}
