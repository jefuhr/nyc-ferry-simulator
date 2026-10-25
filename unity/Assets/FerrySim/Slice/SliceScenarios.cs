using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.Slice
{
	/// <summary>
	/// The slice's scenario picker back end, which <see cref="SliceBootstrap"/> exposes: the catalog
	/// (<see cref="IScenarioCatalog"/>, a <see cref="SliceScenarioCatalog"/> built on first use from the bootstrap's landing
	/// faces, the packs in the environment folder and the vessel files) and the launcher (<see cref="IScenarioLauncher"/>),
	/// which starts a run on the slice's runner.
	/// </summary>
	public sealed class SliceScenarios : IScenarioCatalog, IScenarioLauncher
	{
		private readonly SliceBootstrap _bootstrap;
		private readonly string[] _vesselFiles;
		private readonly string _environmentFolder;
		private readonly UnityEngine.Object _context;
		private SliceScenarioCatalog _catalog;

		/// <param name="bootstrap">The slice: its runner, landing faces and start checks.</param>
		/// <param name="vesselFiles">Vessel files a run can use, in the order a picker shows them, relative to the repository root.</param>
		/// <param name="environmentFolder">The region's pack folder, <c>data/processed/&lt;region&gt;/env</c>.</param>
		public SliceScenarios(SliceBootstrap bootstrap, IReadOnlyList<string> vesselFiles, string environmentFolder)
		{
			_bootstrap = bootstrap;
			_context = bootstrap;
			_vesselFiles = vesselFiles == null ? Array.Empty<string>() : new List<string>(vesselFiles).ToArray();
			_environmentFolder = environmentFolder;
		}

		/// <summary>The catalog, built on first use (after the bootstrap has loaded the landings).</summary>
		public SliceScenarioCatalog Catalog => _catalog ??= Build();

		public IReadOnlyList<ScenarioLanding> Landings => Catalog.Landings;

		public IReadOnlyList<ScenarioDay> Days => Catalog.Days;

		public IReadOnlyList<ScenarioVessel> Vessels => Catalog.Vessels;

		/// <summary>Forgets the catalog, so the next use reads the landings, packs and vessel files again.</summary>
		public void Refresh() => _catalog = null;

		public bool TryCreate(ScenarioChoice choice, out ScenarioSettings settings, out string problem) => Catalog.TryCreate(choice, out settings, out problem);

		/// <summary>
		/// Ends the slice's current run and starts <paramref name="settings"/> with a new run id. False, with the reason, when it
		/// cannot start: the slice's landings or player data failed, the scenario is invalid or its vessel file or pack is
		/// missing or does not verify (the current run then goes on), or the start is refused for being too close to a face
		/// (<see cref="SliceBootstrap.StartRefusal"/>).
		/// </summary>
		public bool TryStart(ScenarioSettings settings, out string problem)
		{
			var runner = _bootstrap != null ? _bootstrap.Runner : null;
			if (settings == null)
			{
				problem = "No scenario was given to start.";
				return false;
			}

			if (runner == null)
			{
				problem = "The slice has no simulation runner to start the scenario on.";
				return false;
			}

			if (_bootstrap.LoadError != null)
			{
				problem = $"No run can start, because the slice's landing faces did not load: {_bootstrap.LoadError}.";
				return false;
			}

			if (_bootstrap.PlayerError != null)
			{
				problem = _bootstrap.PlayerError;
				return false;
			}

			try
			{
				runner.Initialize(settings);
			}
			catch (Exception error) when (error is ArgumentException || error is FormatException || error is IOException || error is EnvironmentPackException ||
				error is VesselParametersException || error is InvalidOperationException || error is UnauthorizedAccessException)
			{
				problem = error.Message;
				Debug.LogWarning($"[FerrySim] Scenario '{settings.name}' did not start: {problem}", _context);
				return false;
			}

			if (_bootstrap.StartRefusal != null)
			{
				problem = _bootstrap.StartRefusal;
				return false;
			}

			// A runner whose own first scenario could not start disabled itself; a scenario that starts brings it back.
			runner.enabled = true;
			problem = null;
			return true;
		}

		/// <summary>The region of <c>data/processed/&lt;region&gt;/env</c>, or null with the reason for any other folder.</summary>
		public static string RegionOf(string folder, out string problem)
		{
			const string prefix = EnvironmentPacks.ProcessedFolder + "/";
			const string suffix = "/env";
			if (folder != null && folder.StartsWith(prefix, StringComparison.Ordinal) && folder.EndsWith(suffix, StringComparison.Ordinal) && folder.Length > prefix.Length + suffix.Length)
			{
				var region = folder.Substring(prefix.Length, folder.Length - prefix.Length - suffix.Length);
				if (EnvironmentPacks.TryParseName(region + "/2000-01-01", out _, out _, out _))
				{
					problem = null;
					return region;
				}
			}

			problem = $"the environment folder '{folder}': it must be {EnvironmentPacks.RegionFolder("<region>")}, so no days are offered";
			return null;
		}

		private SliceScenarioCatalog Build()
		{
			var region = RegionOf(_environmentFolder, out var folderProblem);
			var vessels = new List<KeyValuePair<string, VesselParameters>>();
			var problems = new List<string>();
			foreach (var file in _vesselFiles)
			{
				var path = SimPaths.Resolve(file);
				try
				{
					vessels.Add(new KeyValuePair<string, VesselParameters>(file, VesselParametersJson.Load(path)));
				}
				catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is VesselParametersException || error is ArgumentException)
				{
					problems.Add($"vessel {file}: {error.Message} (looked at {path})");
				}
			}

			var runner = _bootstrap != null ? _bootstrap.Runner : null;
			var packs = runner != null ? runner.EnvironmentPacks : EnvironmentPacks.Default;
			var catalog = new SliceScenarioCatalog(_bootstrap != null ? _bootstrap.Landings : null, vessels, packs, region ?? string.Empty,
				_bootstrap == null ? "the slice has no SliceBootstrap" : _bootstrap.LoadError);
			problems.AddRange(catalog.Excluded);
			if (folderProblem != null)
			{
				problems.Insert(0, folderProblem);
			}

			foreach (var problem in problems)
			{
				Debug.LogWarning("[FerrySim] Scenario catalog left out " + problem, _context);
			}

			Debug.Log($"[FerrySim] Scenario catalog: {catalog.Landings.Count} landing(s), {catalog.Days.Count} day(s) of {catalog.Region}, {catalog.Vessels.Count} vessel(s).", _context);
			return catalog;
		}
	}
}
