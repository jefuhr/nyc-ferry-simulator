using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.UI.Tests.EditMode
{
	/// <summary>
	/// Dial scales come from the vessel file when it has the numbers and fall back to generic scales
	/// with no redline when it does not. Guards against: a redline invented for an engine whose rating
	/// is unknown, a rating in the wrong unit, and a null section crashing the panel.
	/// </summary>
	public class InstrumentScalesTests
	{
		private static string RepoRoot => UIRepoPaths.Root;

		[Test]
		public void TClass_UsesRatedSpeedAndHardOverAngle()
		{
			var parameters = VesselParametersJson.Load(Path.Combine(RepoRoot, "vessels", "t-class.json"));

			var scales = InstrumentScales.From(parameters);

			Assert.That(scales.VesselName, Is.EqualTo(parameters.DisplayName));
			Assert.That(scales.HasRedline, Is.True);
			Assert.That(scales.RatedRpm, Is.EqualTo(2100.0).Within(0.01));
			Assert.That(scales.TachMaxRpm, Is.EqualTo(2500.0));
			Assert.That(scales.HardOverDegrees, Is.EqualTo(35.0).Within(0.01));
			Assert.That(scales.RudderScaleDegrees, Is.EqualTo(45.0));
			Assert.That(scales.ClutchInLever, Is.EqualTo(0.12).Within(1e-9));
		}

		[Test]
		public void NullParameters_GiveGenericScalesWithoutRedline()
		{
			var scales = InstrumentScales.From(null);

			Assert.That(scales.HasRedline, Is.False);
			Assert.That(double.IsNaN(scales.RatedRpm), Is.True);
			Assert.That(scales.TachMaxRpm, Is.EqualTo(InstrumentScales.GenericTachMaxRpm));
			Assert.That(double.IsNaN(scales.HardOverDegrees), Is.True);
			Assert.That(scales.RudderScaleDegrees, Is.EqualTo(45.0));
			Assert.That(double.IsNaN(scales.ClutchInLever), Is.True);
			Assert.That(scales.VesselName, Is.EqualTo(InstrumentScales.UnknownVesselName));
		}

		[Test]
		public void MissingSections_FallBackPerField()
		{
			var parameters = new VesselParameters
			{
				Id = "test-boat",
				Machinery = new VesselMachinery
				{
					RatedEngineSpeed = new ParameterValue { Value = Units.RpmToRadiansPerSecond(1800.0), Unit = "rad/s" },
				},
			};

			var scales = InstrumentScales.From(parameters);

			Assert.That(scales.VesselName, Is.EqualTo("test-boat"), "falls back to the id when there is no display name");
			Assert.That(scales.TachMaxRpm, Is.EqualTo(2000.0));
			Assert.That(double.IsNaN(scales.HardOverDegrees), Is.True);
		}

		[Test]
		public void RatingInTheWrongUnit_IsTreatedAsUnknown()
		{
			var parameters = new VesselParameters
			{
				Machinery = new VesselMachinery
				{
					RatedEngineSpeed = new ParameterValue { Value = 2100.0, Unit = "1" },
				},
			};

			Assert.That(InstrumentScales.From(parameters).HasRedline, Is.False);
		}

		[TestCase(2100.0, 2500.0)]
		[TestCase(1800.0, 2000.0)]
		[TestCase(2300.0, 3000.0)]
		[TestCase(600.0, 1000.0)]
		public void TachScale_IsTheNext500AboveTenPercentOverRated(double ratedRpm, double expectedMax)
		{
			Assert.That(InstrumentScales.TachMaxFor(ratedRpm), Is.EqualTo(expectedMax));
		}

		[Test]
		public void WideRudders_GetAWiderScale()
		{
			Assert.That(InstrumentScales.RudderScaleFor(35.0), Is.EqualTo(45.0));
			Assert.That(InstrumentScales.RudderScaleFor(45.0), Is.EqualTo(50.0));
			Assert.That(InstrumentScales.RudderScaleFor(double.NaN), Is.EqualTo(45.0));
		}
	}
}
