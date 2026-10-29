using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.Slice;
using UnityEngine;

namespace FerrySim.Harbor
{
	/// <summary>
	/// The harbor's atomic start behind the picker's <see cref="IScenarioLauncher"/>: the runner prepares the whole
	/// bundle (depth, day, runtime pack, contacts, traffic, vessel bytes) and checks the start clear of every landing
	/// face before the current run ends (<see cref="VesselSimulationRunner.InitializeHarbor"/>). A refusal names the
	/// exact file or face and leaves the current run going.
	/// </summary>
	public sealed class HarborLauncher : IScenarioLauncher
	{
		private readonly VesselSimulationRunner _runner;
		private readonly string _runtimeFolder;
		private readonly LandingGeometry _landings;
		private readonly LandingSurfaceTable _surfaces;

		public HarborLauncher(VesselSimulationRunner runner, string runtimeFolder, LandingGeometry landings, LandingSurfaceTable surfaces)
		{
			_runner = runner != null ? runner : throw new ArgumentNullException(nameof(runner));
			_runtimeFolder = runtimeFolder ?? throw new ArgumentNullException(nameof(runtimeFolder));
			_landings = landings ?? throw new ArgumentNullException(nameof(landings));
			_surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
		}

		/// <summary>Why the last start was refused, or null after a start.</summary>
		public string LastProblem { get; private set; }

		public bool TryStart(ScenarioSettings settings, out string problem)
		{
			if (settings == null)
			{
				problem = LastProblem = "No scenario was given to start.";
				return false;
			}

			try
			{
				_runner.InitializeHarbor(settings, _runtimeFolder, ParseContacts, validateStart: ValidateStart);
			}
			catch (Exception error) when (error is ArgumentException || error is FormatException || error is IOException || error is EnvironmentPackException ||
				error is HarborRuntimePackException || error is VesselParametersException || error is InvalidOperationException || error is UnauthorizedAccessException)
			{
				problem = LastProblem = error.Message;
				Debug.LogWarning($"[FerrySim] Harbor scenario '{settings.name}' did not start: {problem}", _runner);
				return false;
			}

			// A runner whose own first scenario could not start disabled itself; a scenario that starts brings it back.
			_runner.enabled = true;
			problem = LastProblem = null;
			return true;
		}

		/// <summary>The runtime pack's verified contacts text through the existing slice parser and surface policy.</summary>
		private IWorldGeometry ParseContacts(string json, string file) => LandingGeometry.Parse(json, file, _surfaces).Geometry;

		/// <summary>The slice's start gate, run against the candidate before anything is replaced.</summary>
		private void ValidateStart(PreparedHarborStart candidate, VesselParameters vessel, VesselState start)
		{
			var clearance = FaceClearance.Check(vessel, start.North, start.East, start.Heading, _landings.Geometry.Segments);
			if (!clearance.IsClear())
			{
				throw new InvalidOperationException("The harbor start is too close to landing face " + clearance.NearestSegmentId + ".");
			}
		}
	}
}
