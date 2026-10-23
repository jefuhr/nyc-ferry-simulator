using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Tests.EditMode
{
	/// <summary>
	/// Proves the shared sim core compiles inside Unity and runs: parameter file loads, the factory
	/// builds a simulation and stepping it with both levers ahead moves the vessel forward.
	/// </summary>
	public class CoreSmokeTests
	{
		private const int Steps = 1000;

		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		private static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		private static VesselParameters LoadTClass() =>
			VesselParametersJson.Load(Path.Combine(RepoRoot, "vessels", "t-class.json"));

		[Test]
		public void RepoRoot_ContainsVesselsAndCore()
		{
			Assert.That(Directory.Exists(Path.Combine(RepoRoot, "vessels")), Is.True, RepoRoot);
			Assert.That(Directory.Exists(Path.Combine(RepoRoot, "core")), Is.True, RepoRoot);
		}

		[Test]
		public void TClassParameters_LoadAndValidate()
		{
			var parameters = LoadTClass();

			Assert.That(parameters.Id, Is.Not.Empty);
		}

		[Test]
		public void BothLeversAhead_MovesVesselForwardWithFiniteState()
		{
			var parameters = LoadTClass();
			var initial = VesselState.Planar(0.0, 0.0, 0.0, 0.5);
			var simulation = new VesselDynamicsFactory().Create(parameters, UniformEnvironment.Calm, initial);
			var ahead = new ControlInputs(1.0, 1.0, 0.0);

			for (var step = 0; step < Steps; step++)
			{
				simulation.Step(ahead);
				Assert.That(simulation.State.IsFinite(), Is.True, "state went non-finite at step " + step);
			}

			var state = simulation.State;
			var alongHeading = (state.North - initial.North) * Math.Cos(initial.Heading) + (state.East - initial.East) * Math.Sin(initial.Heading);
			Assert.That(state.Time, Is.EqualTo(SimulationClock.TimeAtStep(Steps)).Within(1e-9));
			Assert.That(alongHeading, Is.GreaterThan(1.0), "vessel did not move forward");
			Assert.That(state.Surge, Is.GreaterThan(0.0));
		}
	}
}
