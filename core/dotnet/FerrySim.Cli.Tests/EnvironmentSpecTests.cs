using System;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure modes guarded: current and wind directions swapped or reversed (the classic from/toward
	// mix-up), knots read as m/s, silently ignored items, and negative or nonsense values reaching the model.
	[TestFixture]
	public sealed class EnvironmentSpecTests
	{
		private const double Tolerance = 1e-9;

		[Test]
		public void Calm_IsStillWaterAndAirInUnrestrictedDepth()
		{
			var spec = EnvironmentSpec.Parse("calm");

			Assert.That(spec.Current.Magnitude, Is.EqualTo(0.0));
			Assert.That(spec.Wind.Magnitude, Is.EqualTo(0.0));
			Assert.That(spec.Depth, Is.EqualTo(UniformEnvironment.Calm.Depth));
			Assert.That(EnvironmentSpec.Calm.Text, Is.EqualTo("calm"));
		}

		[Test]
		public void CurrentDirectionIsWhereItFlowsToward()
		{
			var south = EnvironmentSpec.Parse("current=2kn@180").Current;
			var east = EnvironmentSpec.Parse("current=1.5kn@90").Current;

			Assert.That(south.North, Is.EqualTo(-Units.KnotsToMetresPerSecond(2.0)).Within(Tolerance));
			Assert.That(south.East, Is.EqualTo(0.0).Within(Tolerance));
			Assert.That(east.East, Is.EqualTo(Units.KnotsToMetresPerSecond(1.5)).Within(Tolerance));
			Assert.That(east.North, Is.EqualTo(0.0).Within(Tolerance));
		}

		[Test]
		public void SpecExample_CurrentAndWindUseOppositeConventions()
		{
			var spec = EnvironmentSpec.Parse("current=1.5kn@200,wind=15kn@from300,depth=8m");

			Assert.That(spec.Current.Magnitude, Is.EqualTo(Units.KnotsToMetresPerSecond(1.5)).Within(Tolerance));
			Assert.That(Angles.ToDegrees(spec.Current.Direction), Is.EqualTo(200.0).Within(1e-6), "current flows toward 200");
			Assert.That(spec.Wind.Magnitude, Is.EqualTo(Units.KnotsToMetresPerSecond(15.0)).Within(Tolerance));
			Assert.That(Angles.ToDegrees(spec.Wind.Direction), Is.EqualTo(120.0).Within(1e-6), "wind from 300 blows toward 120");
			Assert.That(spec.Depth, Is.EqualTo(8.0));
		}

		[Test]
		public void NorthWind_BlowsSouth()
		{
			var wind = EnvironmentSpec.Parse("wind=10m/s@from0").Wind;

			Assert.That(wind.North, Is.EqualTo(-10.0).Within(Tolerance));
			Assert.That(wind.East, Is.EqualTo(0.0).Within(Tolerance));
		}

		[Test]
		public void BareWindDirectionMeansFrom_AndBareCurrentMeansToward()
		{
			Assert.That(EnvironmentSpec.Parse("wind=5kn@90").Wind.East, Is.LessThan(0.0), "wind from the east blows west");
			Assert.That(EnvironmentSpec.Parse("current=5kn@90").Current.East, Is.GreaterThan(0.0), "current toward the east flows east");
		}

		[Test]
		public void ExplicitToAndFromOverrideTheDefaults()
		{
			var windTo = EnvironmentSpec.Parse("wind=5kn@to90").Wind;
			var currentFrom = EnvironmentSpec.Parse("current=5kn@from90").Current;

			Assert.That(windTo.East, Is.GreaterThan(0.0));
			Assert.That(currentFrom.East, Is.LessThan(0.0));
		}

		[Test]
		public void UnitsAndWhitespaceAndCase()
		{
			var spec = EnvironmentSpec.Parse(" Current=3 m/s@0 , DEPTH=30ft ");

			Assert.That(spec.Current.North, Is.EqualTo(3.0).Within(Tolerance));
			Assert.That(spec.Depth, Is.EqualTo(Units.FeetToMetres(30.0)).Within(Tolerance));
			Assert.That(spec.Text, Is.EqualTo("current=3 m/s@0 , depth=30ft"));
		}

		[Test]
		public void MissingItemsKeepCalmDefaults()
		{
			var spec = EnvironmentSpec.Parse("depth=12m");

			Assert.That(spec.Current.Magnitude, Is.EqualTo(0.0));
			Assert.That(spec.Wind.Magnitude, Is.EqualTo(0.0));
			Assert.That(spec.Depth, Is.EqualTo(12.0));
		}

		[Test]
		public void CreateEnvironment_ReturnsTheSameValuesEverywhere()
		{
			var spec = EnvironmentSpec.Parse("current=1kn@45,depth=9m");
			var environment = spec.CreateEnvironment();

			Assert.That(environment.SampleDepth(100.0, -50.0, 3600.0), Is.EqualTo(9.0));
			Assert.That(environment.SampleCurrent(0.0, 0.0, 0.0), Is.EqualTo(environment.SampleCurrent(500.0, 500.0, 99.0)));
		}

		[TestCase("")]
		[TestCase("   ")]
		[TestCase("windy")]
		[TestCase("current")]
		[TestCase("current=")]
		[TestCase("=5kn@90")]
		[TestCase("current=1.5kn")]
		[TestCase("current=1.5kn@")]
		[TestCase("current=1.5@200")]
		[TestCase("current=fastkn@200")]
		[TestCase("current=-1kn@200")]
		[TestCase("current=1kn@361")]
		[TestCase("current=1kn@-10")]
		[TestCase("current=1kn@north")]
		[TestCase("wind=1furlong@90")]
		[TestCase("depth=8")]
		[TestCase("depth=0m")]
		[TestCase("depth=-3m")]
		[TestCase("tide=1m")]
		[TestCase("current=1kn@90,current=2kn@90")]
		[TestCase("current=1kn@90,,depth=8m")]
		[TestCase("calm,depth=8m")]
		public void InvalidSpecs_AreUsageErrors(string spec)
		{
			Assert.Throws<UsageException>(() => EnvironmentSpec.Parse(spec));
		}

		[Test]
		public void NullSpec_IsAUsageError()
		{
			Assert.Throws<UsageException>(() => EnvironmentSpec.Parse(null));
		}
	}
}
