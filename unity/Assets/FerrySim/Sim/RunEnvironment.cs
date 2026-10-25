using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;

namespace FerrySim.Sim
{
	/// <summary>
	/// The water and air of one scenario: calm, uniform, or one day of an environment pack from the scenario's start. Each
	/// run gets its simulation environment from <see cref="StartRun"/>: the same uniform one, or a fresh
	/// <see cref="DataEnvironment"/> whose coverage counts start at zero. Everything that only looks (the start state, the
	/// readout, the drawn tide) reads <see cref="Observer"/>, a second environment over the same pack, so looking never
	/// changes the run's counts. The pack's samples are pure functions of position and time, so both agree to the bit.
	/// Deterministic and allocation-free per sample.
	/// </summary>
	public sealed class RunEnvironment
	{
		/// <summary>Half the span the water level rate is taken over, s: the rate is the change over the minute centred on the step.</summary>
		public const double RateHalfSpan = 30.0;

		/// <summary>The recording header key for the pack, region/date.</summary>
		public const string PackKey = "environment_pack";

		/// <summary>The recording header key for the pack's day digest.</summary>
		public const string DigestKey = "environment_digest";

		/// <summary>The recording header key for the pack's local-midnight epoch, UTC.</summary>
		public const string EpochKey = "environment_epoch_utc";

		/// <summary>The recording header key for the wind station the uniform wind comes from (the CLI writes it too).</summary>
		public const string WindStationKey = "environment_wind_station";

		/// <summary>The header's <c>environment</c> value for a pack run.</summary>
		public const string PackText = "pack";

		private readonly DataEnvironment _observer;
		private readonly EnvironmentReadout _uniformReadout;
		private DataEnvironment _run;

		private RunEnvironment(EnvironmentKind kind, UniformEnvironment uniform)
		{
			Kind = kind;
			UniformEnvironment = uniform ?? throw new ArgumentNullException(nameof(uniform));
			_uniformReadout = new EnvironmentReadout(kind, string.Empty, string.Empty, TimeSpan.Zero, double.NaN, double.NaN, uniform.Current, uniform.Wind, false, 0, 0);
		}

		private RunEnvironment(EnvironmentPack pack, DateTimeOffset scenarioStartUtc, string packFolder)
		{
			Kind = EnvironmentKind.Pack;
			Pack = pack ?? throw new ArgumentNullException(nameof(pack));
			PackName = pack.Name;
			PackFolder = packFolder;
			ScenarioStartUtc = scenarioStartUtc.ToUniversalTime();
			LocalUtcOffset = EnvironmentPacks.LocalUtcOffset(pack.Date, pack.EpochUtc);
			_observer = new DataEnvironment(pack, ScenarioStartUtc);
		}

		public EnvironmentKind Kind { get; }

		/// <summary>The calm or uniform environment; null for a pack.</summary>
		public UniformEnvironment UniformEnvironment { get; }

		/// <summary>The pack's day; null for calm and uniform.</summary>
		public EnvironmentPack Pack { get; }

		/// <summary>The pack as region/date, worked out once (the pack's own name is built on every read); empty for calm and uniform.</summary>
		public string PackName { get; } = string.Empty;

		/// <summary>The day folder the pack was read from, when known.</summary>
		public string PackFolder { get; }

		/// <summary>Simulation time zero for a pack, UTC.</summary>
		public DateTimeOffset ScenarioStartUtc { get; }

		/// <summary>America/New_York's offset from UTC on the pack's day; zero for calm and uniform.</summary>
		public TimeSpan LocalUtcOffset { get; }

		/// <summary>True when the water level follows a real tide.</summary>
		public bool HasTide => Kind == EnvironmentKind.Pack;

		/// <summary>The current run's simulation environment: the uniform one, or the pack environment of the latest <see cref="StartRun"/>.</summary>
		public IEnvironment Simulation => Kind == EnvironmentKind.Pack ? (IEnvironment)_run : UniformEnvironment;

		/// <summary>The same water and air for looking: the uniform environment, or a pack environment no simulation samples.</summary>
		public IEnvironment Observer => Kind == EnvironmentKind.Pack ? (IEnvironment)_observer : UniformEnvironment;

		/// <summary>The current run's pack environment, or null: its coverage report says what it made up.</summary>
		public DataEnvironment PackRun => _run;

		/// <summary>Calm or uniform, from a scenario's settings (which must not name a pack).</summary>
		public static RunEnvironment Uniform(ScenarioEnvironment settings)
		{
			if (settings == null)
			{
				throw new ArgumentNullException(nameof(settings));
			}

			return new RunEnvironment(settings.calm ? EnvironmentKind.Calm : EnvironmentKind.Uniform, settings.Create());
		}

		/// <summary>A uniform environment as given, for example from a recording's header.</summary>
		public static RunEnvironment Uniform(UniformEnvironment environment, bool calm) =>
			new RunEnvironment(calm ? EnvironmentKind.Calm : EnvironmentKind.Uniform, environment);

		/// <summary>
		/// The pack's day from <paramref name="scenarioStartUtc"/>. Throws <see cref="ArgumentException"/> when the start is
		/// outside the day's coverage.
		/// </summary>
		public static RunEnvironment ForPack(EnvironmentPack pack, DateTimeOffset scenarioStartUtc, string packFolder = null)
		{
			if (pack == null)
			{
				throw new ArgumentNullException(nameof(pack));
			}

			var first = EnvironmentPacks.AtPackTime(pack.EpochUtc, pack.CoverageStart);
			var last = EnvironmentPacks.AtPackTime(pack.EpochUtc, pack.CoverageEnd);
			if (scenarioStartUtc < first || scenarioStartUtc > last)
			{
				throw new ArgumentException(
					$"The scenario starts at {EnvironmentPacks.Iso(scenarioStartUtc)}, outside environment pack '{pack.Name}', which covers {EnvironmentPacks.Iso(first)} to {EnvironmentPacks.Iso(last)}.");
			}

			return new RunEnvironment(pack, scenarioStartUtc, packFolder);
		}

		/// <summary>
		/// A recording's environment: its uniform values, or its pack, which must be the one it was recorded with. A pack run
		/// is replayed against <paramref name="pack"/>, or the pack its header names from <paramref name="packs"/>. Throws
		/// <see cref="InvalidOperationException"/>, naming both digests, when the pack's day digest is not the recorded one.
		/// </summary>
		public static RunEnvironment ForRecording(RecordedRun run, EnvironmentPack pack = null, EnvironmentPacks packs = null)
		{
			if (!TryForRecording(run, pack, packs, out var environment, out var recorded, out var replayed))
			{
				throw new InvalidOperationException($"{System.IO.Path.GetFileName(run.TelemetryPath)} was recorded against {recorded}, but the replay has {replayed}; replay it against the same environment.");
			}

			return environment;
		}

		/// <summary>As <see cref="ForRecording"/>, but false, with what was recorded and what the replay has, instead of throwing.</summary>
		public static bool TryForRecording(RecordedRun run, EnvironmentPack pack, EnvironmentPacks packs, out RunEnvironment environment, out string recorded, out string replayed)
		{
			if (run == null)
			{
				throw new ArgumentNullException(nameof(run));
			}

			environment = null;
			if (run.EnvironmentKind != EnvironmentKind.Pack)
			{
				recorded = "environment " + (run.Header.TryGetValue("environment", out var text) ? text : "unknown");
				if (pack != null)
				{
					replayed = $"environment pack {pack.Name} (digest {pack.Digest})";
					return false;
				}

				replayed = recorded;
				environment = Uniform(run.Environment, run.EnvironmentKind == EnvironmentKind.Calm);
				return true;
			}

			recorded = $"environment pack {run.EnvironmentPack} (digest {run.EnvironmentDigest})";
			string folder = null;
			if (pack == null)
			{
				try
				{
					pack = (packs ?? EnvironmentPacks.Default).Load(run.EnvironmentPack, out folder);
				}
				catch (Exception error) when (error is System.IO.IOException || error is ArgumentException || error is FerrySim.Core.Serialization.EnvironmentPackException)
				{
					replayed = "no usable pack: " + error.Message.TrimEnd('.');
					return false;
				}
			}

			replayed = $"environment pack {pack.Name} (digest {pack.Digest})";
			if (!string.Equals(pack.Digest, run.EnvironmentDigest, StringComparison.Ordinal))
			{
				return false;
			}

			environment = new RunEnvironment(pack, run.ScenarioStartUtc, folder);
			return true;
		}

		/// <summary>The environment a new run's simulation samples: the uniform one, or a fresh pack environment with zero counts.</summary>
		public IEnvironment StartRun()
		{
			if (Kind != EnvironmentKind.Pack)
			{
				return UniformEnvironment;
			}

			_run = new DataEnvironment(Pack, ScenarioStartUtc);
			return _run;
		}

		/// <summary>Water surface height above NAVD88 at a world position and simulation time, m; NaN without a tide.</summary>
		public double WaterLevel(double north, double east, double time) =>
			Kind == EnvironmentKind.Pack ? _observer.SampleWaterLevel(north, east, time) : double.NaN;

		/// <summary>
		/// The water and the air at a world position and simulation time: for a pack, the level and its rate, the current, the
		/// wind and the current run's time holds and depth misses; for calm and uniform, the uniform current and wind and no
		/// tide (NaN).
		/// </summary>
		public EnvironmentReadout Readout(double north, double east, double time)
		{
			if (Kind != EnvironmentKind.Pack)
			{
				return _uniformReadout;
			}

			var level = _observer.SampleWaterLevel(north, east, time);
			var rate = (_observer.SampleWaterLevel(north, east, time + RateHalfSpan) - _observer.SampleWaterLevel(north, east, time - RateHalfSpan)) / (2.0 * RateHalfSpan);
			var current = _observer.SampleCurrent(north, east, time);
			var wind = _observer.SampleWind(north, east, time);
			long holds = 0;
			long misses = 0;
			if (_run != null)
			{
				var coverage = _run.Coverage;
				holds = coverage.TimeHolds;
				misses = coverage.DepthMisses;
			}

			return new EnvironmentReadout(EnvironmentKind.Pack, PackName, Pack.Digest, LocalUtcOffset, level, rate, current, wind, Pack.WindStations.Count == 0, holds, misses);
		}

		/// <summary>
		/// The lines a pack run's recording adds after <c>environment</c> (= <see cref="PackText"/>): the pack, its day digest,
		/// its epoch and the wind station; none for calm and uniform.
		/// </summary>
		public List<KeyValuePair<string, string>> RecordingHeader()
		{
			var header = new List<KeyValuePair<string, string>>();
			if (Kind != EnvironmentKind.Pack)
			{
				return header;
			}

			header.Add(new KeyValuePair<string, string>(PackKey, Pack.Name));
			header.Add(new KeyValuePair<string, string>(DigestKey, Pack.Digest));
			header.Add(new KeyValuePair<string, string>(EpochKey, EnvironmentPacks.Iso(Pack.EpochUtc)));
			header.Add(new KeyValuePair<string, string>(WindStationKey, Pack.WindStations.Count > 0 ? Pack.WindStations[0].Id : "none (still air)"));
			return header;
		}

		public override string ToString() => Kind == EnvironmentKind.Pack
			? $"environment pack {Pack.Name} (digest {Pack.Digest}) from {EnvironmentPacks.Iso(ScenarioStartUtc)}"
			: $"{Kind.ToString().ToLowerInvariant()} environment (current {UniformEnvironment.Current}, wind {UniformEnvironment.Wind}, depth {UniformEnvironment.Depth} m)";
	}
}
