using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>
	/// The dynamics sections of the shipped parameter files. Failure modes guarded: a section missing from
	/// one file, C# and JSON drifting apart (load and round trip), and a model coefficient claiming more
	/// confidence than an estimate.
	/// </summary>
	[TestFixture("t-class.json")]
	[TestFixture("k-class.json")]
	public class DynamicsParameterFileTests
	{
		private readonly string _vesselFile;

		public DynamicsParameterFileTests(string vesselFile)
		{
			_vesselFile = vesselFile;
		}

		[Test]
		public void EveryDynamicsSection_IsPresent()
		{
			var parameters = VesselParametersJson.Load(RepoPaths.Vessel(_vesselFile));
			Assert.That(parameters.Particulars.Displacement, Is.Not.Null);
			Assert.That(parameters.Particulars.LengthWaterline, Is.Not.Null);
			Assert.That(parameters.Particulars.MaxDraft, Is.Not.Null);
			Assert.That(parameters.Machinery.GearRatio, Is.Not.Null);
			Assert.That(parameters.Machinery.PropellerDiameter, Is.Not.Null);
			foreach (var section in Sections(parameters))
			{
				Assert.That(section, Is.Not.Null);
				foreach (var property in section.GetType().GetProperties())
				{
					Assert.That(property.GetValue(section), Is.Not.Null, $"{section.GetType().Name}.{property.Name}");
				}
			}
		}

		[Test]
		public void ModelCoefficients_AreAllMarkedEstimated_WithABasis()
		{
			var parameters = VesselParametersJson.Load(RepoPaths.Vessel(_vesselFile));
			foreach (var section in Sections(parameters))
			{
				foreach (var leaf in Leaves(section))
				{
					Assert.That(leaf.Confidence, Is.EqualTo(ParameterConfidence.Estimated), leaf.Source);
					Assert.That(leaf.Source.Length, Is.GreaterThan(20), "an estimate states its basis");
				}
			}
		}

		[Test]
		public void DynamicsSections_SurviveARoundTrip()
		{
			var original = VesselParametersJson.Load(RepoPaths.Vessel(_vesselFile));
			var copy = VesselParametersJson.Parse(VesselParametersJson.Serialize(original));
			Assert.That(copy.Hull.ResiduaryResistance.Count, Is.EqualTo(original.Hull.ResiduaryResistance.Count));
			Assert.That(copy.Engines.TorqueCurve[3].Torque.Value, Is.EqualTo(original.Engines.TorqueCurve[3].Torque.Value));
			Assert.That(copy.Controls.SteeringMode.Value, Is.EqualTo(original.Controls.SteeringMode.Value));
			Assert.That(new VesselDynamicsFactory().Create(copy, UniformEnvironment.Calm, VesselState.Planar(0.0, 0.0, 0.0, 0.0)), Is.Not.Null);
		}

		private static IEnumerable<object> Sections(VesselParameters parameters)
		{
			yield return parameters.MassProperties;
			yield return parameters.Hull;
			yield return parameters.Propellers;
			yield return parameters.Engines;
			yield return parameters.Transmission;
			yield return parameters.Controls;
			yield return parameters.Rudders;
			yield return parameters.Windage;
			yield return parameters.ShallowWater;
			yield return parameters.MotionResponse;
		}

		private static IEnumerable<(string Source, string Confidence)> Leaves(object node)
		{
			switch (node)
			{
				case null:
					yield break;
				case ParameterValue value:
					yield return (value.Source, value.Confidence);
					yield break;
				case ParameterText text:
					yield return (text.Source, text.Confidence);
					yield break;
				case IEnumerable items:
					foreach (var item in items)
					{
						foreach (var leaf in Leaves(item))
						{
							yield return leaf;
						}
					}

					yield break;
			}

			foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				foreach (var leaf in Leaves(property.GetValue(node)))
				{
					yield return leaf;
				}
			}
		}
	}
}
