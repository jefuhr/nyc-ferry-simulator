using System.Collections.Generic;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.UI
{
	/// <summary>
	/// Puts a <see cref="FakeScenarioCatalog"/> in a UI development scene, so <see cref="WheelhouseUI"/> finds a
	/// catalog and a launcher there. Starting a run restarts the <see cref="FakeTelemetrySource"/>'s recording as a new
	/// run at the chosen start time and gives the <see cref="FakeEnvironmentSource"/> the chosen day. The recording is
	/// the T-class sample, so a K-class run is refused, which also shows how the picker reports a refusal.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class FakeScenarioHost : MonoBehaviour, IScenarioCatalog, IScenarioLauncher
	{
		public const string RecordedVessel = "t-class";
		public const string OnlyTheTClass = "The UI development scene only has a recorded T-class run, so choose the T-class.";

		[Tooltip("The fake telemetry to restart. Empty: the first one in the scene.")]
		[SerializeField]
		private FakeTelemetrySource telemetry;

		[Tooltip("The fake environment to give the chosen day. Empty: the first one in the scene.")]
		[SerializeField]
		private FakeEnvironmentSource environment;

		[Tooltip("Days the fake marks as having no wind data, only to show how the UI says so: the packs built so far have wind on every day.")]
		[SerializeField]
		private string[] daysWithoutWind = { "2025-01-15" };

		private FakeScenarioCatalog _catalog;

		public FakeScenarioCatalog Catalog
		{
			get
			{
				if (_catalog == null)
				{
					_catalog = FakeScenarioCatalog.Slice(daysWithoutWind ?? new string[0]);
					_catalog.RunStarted += (choice, day, settings) =>
					{
						if (environment != null)
						{
							environment.StartDay(day);
						}

						if (telemetry != null)
						{
							telemetry.StartScenario(settings.StartUtc);
						}
					};
				}

				return _catalog;
			}
		}

		public IReadOnlyList<ScenarioLanding> Landings => Catalog.Landings;

		public IReadOnlyList<ScenarioDay> Days => Catalog.Days;

		public IReadOnlyList<ScenarioVessel> Vessels => Catalog.Vessels;

		public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem) => Catalog.TryCreate(choice, out settings, out problem);

		public bool TryStart(ScenarioSettings settings, out string problem)
		{
			if (Catalog.TryFindChoice(settings, out var choice) && choice.VesselId != RecordedVessel)
			{
				problem = OnlyTheTClass;
				return false;
			}

			return Catalog.TryStart(settings, out problem);
		}

		private void Awake()
		{
			if (telemetry == null)
			{
				telemetry = FindFirstObjectByType<FakeTelemetrySource>();
			}

			if (environment == null)
			{
				environment = FindFirstObjectByType<FakeEnvironmentSource>();
			}
		}
	}
}
