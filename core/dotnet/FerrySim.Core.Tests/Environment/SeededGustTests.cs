using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>Failure modes: default drift, call-order randomness, missing gusts, direction reversal, overshoot and gap interpolation.</summary>
	public class SeededGustTests
	{
		[Test]
		public void DisabledIsBitIdenticalAndEnabledIsSeededBoundedAndOrderIndependent()
		{
			var pack = Pack(8, 8);
			var baseline = new DataEnvironment(pack, PackFixtures.Epoch);
			var disabled = Env(pack, new GustOptions());
			var enabled = Env(pack, new GustOptions(true, 42));
			var other = Env(pack, new GustOptions(true, 43));
			var changed = false;
			var differentSeed = false;
			for (var i = 0; i < 100; i++)
			{
				var time = i * .37;
				var mean = baseline.SampleWind(0, 0, time);
				Assert.That(disabled.SampleWind(0, 0, time), Is.EqualTo(mean));
				var gust = enabled.SampleWind(0, 0, time);
				enabled.SampleWind(400, 100, 125 - time);
				Assert.That(enabled.SampleWind(10, 20, time), Is.EqualTo(gust));
				Assert.That(gust.Magnitude, Is.InRange(5, 8));
				Assert.That(gust.North / gust.East, Is.EqualTo(.75).Within(1e-12));
				changed |= !gust.Equals(mean);
				differentSeed |= !gust.Equals(other.SampleWind(0, 0, time));
			}
			Assert.That(changed && differentSeed, Is.True);
		}

		[TestCase(double.NaN, double.NaN)]
		[TestCase(3, 3)]
		[TestCase(8, double.NaN)]
		public void MissingOrSubMeanGustDoesNotInventAnExcursion(double a, double b)
		{
			var env = Env(Pack(a, b), new GustOptions(true, 42));
			Assert.That(env.SampleWind(0, 0, 60), Is.EqualTo(new HorizontalVector(3, 4)));
		}

		[Test]
		public void HoldsCoverageAndUsesPackTimeSoRestartDoesNotChangeWind()
		{
			var pack = Pack(8, 8);
			var env = Env(pack, new GustOptions(true, 1));
			var restart = new DataEnvironment(pack, PackFixtures.Epoch.AddSeconds(37), new StationBlendCurrentField(pack.CurrentStations), new GustOptions(true, 1));
			Assert.That(restart.SampleWind(0, 0, 12), Is.EqualTo(env.SampleWind(0, 0, 49)));
			Assert.That(env.SampleWind(0, 0, 100000), Is.EqualTo(env.SampleWind(0, 0, 90000)));
		}

		[TestCase(120, 60, 9)]
		[TestCase(3600, 1800, 8)]
		[TestCase(3600, 1800.01, 10)]
		public void GustCeilingUsesTheWindSeriesInterpolationAndGapRules(double lastTime, double time, double ceiling)
		{
			var options = new GustOptions(true, 42);
			var environment = Env(Pack(8, 10, lastTime), options);
			Assert.That(environment.SampleWind(0, 0, time), Is.EqualTo(options.Apply(new HorizontalVector(3, 4), ceiling, time)));
		}

		[Test]
		public void LegacyThreeArgumentConstructorRemainsAvailable()
		{
			Assert.That(typeof(DataEnvironment).GetConstructor(new[] { typeof(EnvironmentPack), typeof(DateTimeOffset), typeof(ICurrentField) }), Is.Not.Null);
		}

		[Test]
		public void InvalidPulseDurationIsRefused()
		{
			foreach (var interval in new[] { 0.0, -1, double.NaN, double.PositiveInfinity })
				Assert.Throws<ArgumentOutOfRangeException>(() => new GustOptions(true, 1, interval));
		}

		private static DataEnvironment Env(EnvironmentPack pack, GustOptions options) =>
			new DataEnvironment(pack, PackFixtures.Epoch, new StationBlendCurrentField(pack.CurrentStations), options);

		private static EnvironmentPack Pack(double a, double b, double lastTime = 120) => PackFixtures.Pack(
			PackFixtures.Depth(50, PackFixtures.FlatGrid("flat", 0, 0, 10, 2, 2, -50)),
			new[] { PackFixtures.SteadyCurrent("c", 0, 0, HorizontalVector.Zero) },
			new[] { PackFixtures.Gauge("g", 0, 0, t => 0) },
			new[] { new WindStation("w", "Synthetic", 0, 0, 0, 0, null, "synthetic", "", new VectorSeries(new[] { 0.0, lastTime }, new[] { 3.0, 3 }, new[] { 4.0, 4 }), new[] { a, b }) });
	}
}
