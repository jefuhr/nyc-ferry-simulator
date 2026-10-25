using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;

namespace FerrySim.Cli
{
	/// <summary>
	/// The water and air a run uses: a uniform <see cref="EnvironmentSpec"/> (<c>--env</c>, calm by default), or one real
	/// day from an environment pack (<c>--env-pack &lt;day folder&gt;</c> with <c>--start-utc &lt;ISO time&gt;</c>).
	/// </summary>
	public sealed class RunEnvironment
	{
		/// <summary>The option naming a pack's day folder.</summary>
		public const string PackOption = "env-pack";

		/// <summary>The option giving the scenario's absolute start.</summary>
		public const string StartOption = "start-utc";

		// The forms the Unity recorder's scenario_start_utc header accepts.
		private static readonly string[] StartFormats = { "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK" };

		private readonly string _packFolder;

		private RunEnvironment(EnvironmentSpec spec, string packFolder, DateTimeOffset start)
		{
			Spec = spec;
			_packFolder = packFolder;
			StartUtc = start;
		}

		/// <summary>The uniform spec, or null for a pack.</summary>
		public EnvironmentSpec Spec { get; }

		/// <summary>The loaded pack, or null for a uniform spec (or before <see cref="Load"/>).</summary>
		public EnvironmentPack Pack { get; private set; }

		/// <summary>The scenario start for a pack: simulation time zero.</summary>
		public DateTimeOffset StartUtc { get; }

		public bool IsPack => _packFolder != null;

		/// <summary>The CSV header's <c>environment</c> value: the spec as written, or <c>pack</c>.</summary>
		public string Text => IsPack ? "pack" : Spec.Text;

		/// <summary>A uniform spec.</summary>
		public static RunEnvironment Uniform(EnvironmentSpec spec) => new RunEnvironment(spec ?? throw new ArgumentNullException(nameof(spec)), null, default);

		/// <summary>
		/// The environment the options ask for, checked before any file is read: <c>--env</c> and <c>--env-pack</c> are
		/// exclusive, and <c>--env-pack</c> needs <c>--start-utc</c>, which applies only to a pack.
		/// </summary>
		public static RunEnvironment Parse(ParsedArguments parsed)
		{
			var pack = parsed.GetString(PackOption);
			var start = parsed.GetString(StartOption);
			if (pack == null)
			{
				if (start != null)
				{
					throw new UsageException($"--{StartOption} applies only with --{PackOption}.");
				}

				return Uniform(EnvironmentSpec.Parse(parsed.GetString("env", "calm")));
			}

			if (parsed.Has("env"))
			{
				throw new UsageException($"--env and --{PackOption} are alternatives; give one.");
			}

			if (pack.Trim().Length == 0)
			{
				throw new UsageException($"--{PackOption} needs a day folder.");
			}

			if (start == null)
			{
				throw new UsageException($"--{PackOption} needs --{StartOption}, the scenario's start as an ISO 8601 UTC time such as 2025-07-14T16:00:00Z.");
			}

			if (!DateTimeOffset.TryParseExact(start.Trim(), StartFormats, CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startUtc))
			{
				throw new UsageException($"--{StartOption} '{start}' is not an ISO 8601 UTC time such as 2025-07-14T16:00:00Z.");
			}

			return new RunEnvironment(null, pack, startUtc);
		}

		/// <summary>Loads and verifies the pack, and checks the start lies inside its coverage. Nothing to do for a uniform spec.</summary>
		public void Load()
		{
			if (!IsPack || Pack != null)
			{
				return;
			}

			var pack = EnvironmentPackJson.Load(_packFolder);
			var first = pack.EpochUtc.AddTicks((long)Math.Round(pack.CoverageStart * TimeSpan.TicksPerSecond));
			var last = pack.EpochUtc.AddTicks((long)Math.Round(pack.CoverageEnd * TimeSpan.TicksPerSecond));
			if (StartUtc < first || StartUtc > last)
			{
				throw new CliException($"--{StartOption} {Iso(StartUtc)} is outside {pack.Name}, which covers {Iso(first)} to {Iso(last)}.");
			}

			Pack = pack;
		}

		/// <summary>A fresh environment for the run: uniform, or the pack's day from the start time.</summary>
		public IEnvironment Create()
		{
			if (!IsPack)
			{
				return Spec.CreateEnvironment();
			}

			Load();
			return new DataEnvironment(Pack, StartUtc);
		}

		/// <summary>Forgets what earlier simulations sampled, so the header reports only the recorded run.</summary>
		public static void ResetCounters(IEnvironment environment)
		{
			if (environment is DataEnvironment data)
			{
				data.ResetCounters();
			}
		}

		/// <summary>The header lines a pack adds after <c>environment</c>; none for a uniform spec.</summary>
		public List<KeyValuePair<string, string>> Header(IEnvironment environment)
		{
			var header = new List<KeyValuePair<string, string>>();
			if (!IsPack || !(environment is DataEnvironment data))
			{
				return header;
			}

			var coverage = data.Coverage;
			header.Add(Maneuvers.Pair("environment_pack", Pack.Name));
			header.Add(Maneuvers.Pair("environment_digest", Pack.Digest));
			header.Add(Maneuvers.Pair("environment_epoch_utc", Iso(Pack.EpochUtc)));
			header.Add(Maneuvers.Pair("scenario_start_utc", Iso(StartUtc)));
			header.Add(Maneuvers.Pair("environment_wind_station", Pack.WindStations.Count > 0 ? Pack.WindStations[0].Id : "none (still air)"));
			header.Add(Maneuvers.Pair("environment_depth_misses", coverage.DepthMisses.ToString(CultureInfo.InvariantCulture)));
			header.Add(Maneuvers.Pair("environment_time_holds", coverage.TimeHolds.ToString(CultureInfo.InvariantCulture)));
			return header;
		}

		/// <summary>A line for the console when the run had to make something up, or null.</summary>
		public static string CoverageWarning(IEnvironment environment)
		{
			if (!(environment is DataEnvironment data) || !data.Coverage.HasMisses)
			{
				return null;
			}

			var coverage = data.Coverage;
			var parts = new List<string>();
			if (coverage.DepthMisses > 0)
			{
				parts.Add($"{coverage.DepthMisses} of {coverage.DepthSamples} depth samples were off the depth grids, so {Maneuvers.Number(coverage.OutsideDepth)} m was used");
			}

			if (coverage.TimeHolds > 0)
			{
				parts.Add($"{coverage.TimeHolds} samples fell outside the pack's time coverage and were held at its edge");
			}

			if (coverage.WindMissing)
			{
				parts.Add("the day has no wind data, so the air was still");
			}

			return "environment: " + string.Join("; ", parts) + ".";
		}

		private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
	}
}
