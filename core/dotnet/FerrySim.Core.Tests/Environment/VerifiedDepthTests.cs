using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	// Failure modes: a fabricated digest masquerading as verified, rereading/copying depth between days,
	// mutable backing collections, lost day checks, mismatched linkage and unaccounted load phases.
	public class VerifiedDepthTests
	{
		private string _root;
		[SetUp]
		public void Setup()
		{
			_root = Path.Combine(Path.GetTempPath(), "verified-depth-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_root);
		}

		[TearDown]
		public void Cleanup() => Directory.Delete(_root, true);
		[Test]
		public void TwoDays_ReuseTheSameDepthAndGridInstances_WithoutRereadingDepth()
		{
			var firstDay = PythonPackFixture.Write(_root);
			var depth = EnvironmentPackJson.LoadVerifiedDepth(EnvironmentPackJson.DepthFolderOf(firstDay));
			var first = EnvironmentPackJson.Load(firstDay, depth, out var timing);
			var next = new EnvironmentPack(first.Region, "2025-07-15", first.EpochUtc.AddDays(1), first.CoverageStart, first.CoverageEnd, first.Depth, first.CurrentStations, first.WaterLevelStations, first.WindStations, first.Notes, "");
			var secondDay = EnvironmentPackWriter.Write(next, Path.Combine(_root, "lower-east-river/env"));
			Directory.Delete(EnvironmentPackJson.DepthFolderOf(firstDay), true);
			var second = EnvironmentPackJson.Load(secondDay, depth);
			Assert.That(first.Depth, Is.SameAs(depth.Depth));
			Assert.That(second.Depth, Is.SameAs(first.Depth));
			Assert.That(second.Depth.Grids[0], Is.SameAs(first.Depth.Grids[0]));
			Assert.That(second.Depth.Grids, Is.Not.InstanceOf<DepthGrid[]>());
			Assert.That(second.Depth.Sources, Is.Not.InstanceOf<DepthSource[]>());
			Assert.That(timing.ReadMilliseconds, Is.GreaterThanOrEqualTo(0));
			Assert.That(timing.HashMilliseconds, Is.GreaterThanOrEqualTo(0));
			Assert.That(timing.DecodeMilliseconds, Is.GreaterThanOrEqualTo(0));
			Assert.That(timing.ValidationMilliseconds, Is.GreaterThanOrEqualTo(0));
			Assert.That(timing.TotalMilliseconds, Is.EqualTo(timing.ReadMilliseconds + timing.HashMilliseconds + timing.DecodeMilliseconds + timing.ValidationMilliseconds).Within(1e-9));
		}

		[Test]
		public void VerifiedDepth_HasNoPublicConstructorOrCallerDigestConstructor()
		{
			Assert.That(typeof(VerifiedDepth).GetConstructors(), Is.Empty);
			Assert.That(typeof(VerifiedDepth).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SelectMany(c => c.GetParameters()).Any(p => p.ParameterType == typeof(string)), Is.False);
		}

		[Test]
		public void DifferentVerifiedDepth_IsRefused_NamingBothHashes()
		{
			var day = PythonPackFixture.Write(_root);
			var pack = EnvironmentPackJson.Load(day);
			var otherFolder = Path.Combine(_root, "other");
			var other = new DepthSet(pack.Depth.Region, "NAVD88", 50, pack.Depth.Grids, pack.Depth.Sources, "caller-digest-is-not-trusted");
			EnvironmentPackWriter.WriteDepthSet(other, otherFolder);
			var verified = EnvironmentPackJson.LoadVerifiedDepth(otherFolder);
			var error = Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day, verified));
			Assert.That(error.Message, Does.Contain(verified.Digest));
			Assert.That(error.Message, Does.Contain(pack.Depth.Digest));
		}

		[Test]
		public void SharedDepth_DoesNotBypassDayHashes()
		{
			var day = PythonPackFixture.Write(_root);
			var verified = EnvironmentPackJson.LoadVerifiedDepth(EnvironmentPackJson.DepthFolderOf(day));
			var file = Path.Combine(day, "currents.csv");
			File.AppendAllText(file, " ");
			Assert.That(Assert.Throws<EnvironmentPackException>(() => EnvironmentPackJson.Load(day, verified)).File, Is.EqualTo(file));
		}

		[Explicit("requires copied harbor environment packs")]
		[Test]
		public void RealHarbor_SharedDepthTimings()
		{
			var env = Path.Combine(RepoPaths.Root, "data/processed/harbor/env");
			var depth = EnvironmentPackJson.LoadVerifiedDepth(Path.Combine(env, "depth"));
			var days = Directory.GetDirectories(Path.Combine(env, "days")).OrderBy(p => p, StringComparer.Ordinal).Take(2).ToArray();
			var first = EnvironmentPackJson.Load(days[0], depth, out var firstTime);
			var second = EnvironmentPackJson.Load(days[1], depth, out var secondTime);
			Assert.That(first.Depth, Is.SameAs(second.Depth));
			var lines = new[]
			{
				"phase,read_ms,hash_ms,decode_ms,validation_ms,total_ms",
				"depth," + depth.Timings.ToCsv(),
				"first_day," + firstTime.ToCsv(),
				"second_day," + secondTime.ToCsv()
			};
			File.WriteAllLines(Path.Combine(RepoPaths.Root, "artifacts/harbor-runtime/depth-timings.csv"), lines);
			TestContext.Out.WriteLine(string.Join("\n", lines));
		}
	}
}
