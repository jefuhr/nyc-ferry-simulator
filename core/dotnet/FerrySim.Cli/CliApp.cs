using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.Core.Contracts;
using FerrySim.Core.Serialization;

namespace FerrySim.Cli
{
	/// <summary>
	/// The <c>ferrysim</c> command line. Exit codes: 0 success, 1 the command could not be carried out,
	/// 2 bad arguments (usage is printed to the error stream).
	/// </summary>
	public sealed class CliApp
	{
		private static readonly string[] CommonManeuverOptions = { "vessel", "out", "speed-kn", "env", "duration-s", "sample-hz", "heading-deg", "lead-in-s" };

		private readonly ISimulationFactory _factory;

		public CliApp(ISimulationFactory factory)
		{
			_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		}

		public int Run(string[] args, TextWriter stdout, TextWriter stderr)
		{
			try
			{
				if (args.Length == 0)
				{
					throw new UsageException("No command given.");
				}

				if (args[0] == "help" || args[0] == "--help" || args[0] == "-h")
				{
					stdout.Write(UsageText());
					return 0;
				}

				if (args.Skip(1).Any(a => a == "--help" || a == "-h"))
				{
					stdout.Write(UsageText());
					return 0;
				}

				var rest = args.Skip(1).ToArray();
				switch (args[0])
				{
					case "maneuver":
						return RunManeuver(rest, stdout);
					case "run":
						return RunTrace(rest, stdout);
					case "summarize":
						return Summarize(rest, stdout);
					default:
						throw new UsageException($"Unknown command '{args[0]}'.");
				}
			}
			catch (UsageException exception)
			{
				stderr.WriteLine("ferrysim: " + exception.Message);
				stderr.WriteLine();
				stderr.Write(UsageText());
				return 2;
			}
			catch (CliException exception)
			{
				stderr.WriteLine("ferrysim: error: " + exception.Message);
				return 1;
			}
			catch (VesselParametersException exception)
			{
				stderr.WriteLine("ferrysim: error: " + exception.Message);
				return 1;
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				stderr.WriteLine("ferrysim: error: " + exception.Message);
				return 1;
			}
		}

		private int RunManeuver(string[] args, TextWriter stdout)
		{
			if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
			{
				throw new UsageException("maneuver needs a name: " + string.Join(", ", Maneuvers.All.Select(m => m.Name)) + ".");
			}

			var spec = Maneuvers.Find(args[0]);
			if (spec == null)
			{
				throw new UsageException($"Unknown maneuver '{args[0]}'; expected one of {string.Join(", ", Maneuvers.All.Select(m => m.Name))}.");
			}

			var parsed = ParsedArguments.Parse(args.Skip(1).ToArray(), CommonManeuverOptions.Concat(spec.Options), Array.Empty<string>());
			if (parsed.Positionals.Count > 0)
			{
				throw new UsageException($"Unexpected argument '{parsed.Positionals[0]}'.");
			}

			if (spec.StartsAtRest && parsed.Has("speed-kn"))
			{
				throw new UsageException($"{spec.Name} starts at rest; --speed-kn does not apply.");
			}

			var vesselPath = parsed.Require("vessel");
			var outPath = parsed.Require("out");
			var environment = EnvironmentSpec.Parse(parsed.GetString("env", "calm"));
			var duration = parsed.GetOptionalDouble("duration-s", 1.0, 86400.0);
			var sampleHz = ParseSampleHz(parsed);
			var heading = parsed.GetDouble("heading-deg", 0.0, 0.0, 360.0);
			var leadIn = parsed.GetDouble("lead-in-s", ManeuverRunner.DefaultLeadInSeconds, 0.0, 600.0);
			var speedKnots = parsed.GetOptionalDouble("speed-kn", 0.1, 100.0);
			var plan = spec.Build(parsed, duration);

			var vessel = VesselParametersJson.Load(vesselPath);
			double? speed = null;
			if (!spec.StartsAtRest)
			{
				if (speedKnots.HasValue)
				{
					speed = Units.KnotsToMetresPerSecond(speedKnots.Value);
				}
				else
				{
					var service = vessel.Performance?.ServiceSpeed?.Value ?? 0.0;
					if (service <= 0.0)
					{
						throw new UsageException($"{vessel.Id} has no published service speed; pass --speed-kn.");
					}

					speed = service;
				}
			}

			var setup = new RunSetup(_factory, vessel, environment, sampleHz, heading);
			var result = ManeuverRunner.RunManeuver(setup, spec, plan, speed, duration, leadIn);
			result.Recorder.Write(outPath, result.Header);
			stdout.WriteLine($"{vessel.Id} {spec.Name}: wrote {result.Recorder.Count} rows to {outPath}");
			if (result.SettleSummary != null)
			{
				stdout.WriteLine("settled: " + result.SettleSummary);
			}

			return 0;
		}

		private int RunTrace(string[] args, TextWriter stdout)
		{
			var parsed = ParsedArguments.Parse(args, new[] { "vessel", "inputs", "env", "out", "duration-s", "sample-hz", "heading-deg" }, Array.Empty<string>());
			if (parsed.Positionals.Count > 0)
			{
				throw new UsageException($"Unexpected argument '{parsed.Positionals[0]}'.");
			}

			var vesselPath = parsed.Require("vessel");
			var inputsPath = parsed.Require("inputs");
			var outPath = parsed.Require("out");
			var environment = EnvironmentSpec.Parse(parsed.GetString("env", "calm"));
			var duration = parsed.GetOptionalDouble("duration-s", 0.01, 86400.0);
			var sampleHz = ParseSampleHz(parsed);
			var heading = parsed.GetDouble("heading-deg", 0.0, 0.0, 360.0);

			var vessel = VesselParametersJson.Load(vesselPath);
			var trace = TraceInputs.Load(inputsPath);
			var setup = new RunSetup(_factory, vessel, environment, sampleHz, heading);
			var result = ManeuverRunner.RunTrace(setup, trace, Path.GetFileName(inputsPath), duration);
			result.Recorder.Write(outPath, result.Header);
			stdout.WriteLine($"{vessel.Id} trace-replay: wrote {result.Recorder.Count} rows to {outPath}");
			return 0;
		}

		private int Summarize(string[] args, TextWriter stdout)
		{
			var parsed = ParsedArguments.Parse(args, Array.Empty<string>(), new[] { "json" });
			if (parsed.Positionals.Count != 1)
			{
				throw new UsageException("summarize needs exactly one CSV file.");
			}

			var report = ManeuverMetrics.Compute(TelemetryTable.Load(parsed.Positionals[0]));
			stdout.Write(parsed.HasFlag("json") ? SummaryFormatter.Json(report) : SummaryFormatter.Text(report));
			return 0;
		}

		private static int ParseSampleHz(ParsedArguments parsed)
		{
			var value = parsed.GetDouble("sample-hz", 10.0, 1.0, SimulationClock.StepsPerSecond);
			var hz = (int)value;
			if (hz != value || SimulationClock.StepsPerSecond % hz != 0)
			{
				throw new UsageException($"--sample-hz must divide the {SimulationClock.StepsPerSecond} Hz physics rate (1, 2, 4, 5, 10, 20, 25, 50 or 100), got {Maneuvers.Number(value)}.");
			}

			return hz;
		}

		public static string UsageText() =>
			string.Join(
				"\n",
				"Usage:",
				"  ferrysim maneuver <name> --vessel <file.json> --out <file.csv> [options]",
				"  ferrysim run --vessel <file.json> --inputs <trace.csv> --out <file.csv> [options]",
				"  ferrysim summarize <file.csv> [--json]",
				"",
				"Maneuvers:",
				string.Join("\n", Maneuvers.All.Select(m => $"  {m.Name,-15}{m.Summary}")),
				"",
				"Options for maneuver and run:",
				"  --env <spec>          calm (default) or items such as current=1.5kn@200,wind=15kn@from300,depth=8m",
				"                        current: direction it flows toward; wind: direction it blows from (prefix to/from to override)",
				"  --duration-s <n>      fixed length of the maneuver (default: run until it is complete)",
				"  --sample-hz <n>       CSV rows per second, a divisor of 100 (default 10)",
				"  --heading-deg <n>     initial heading, degrees true (default 0)",
				"Options for maneuver:",
				"  --speed-kn <n>        test speed through water (default: the vessel's service speed)",
				"  --lead-in-s <n>       seconds of steady running recorded before the maneuver (default 10)",
				"  --direction <side>    turning-circle, pivot: starboard (default) or port",
				"  --helm <n>            turning-circle: helm in [0, 1] (default 1); zigzag: helm override",
				"  --first <side>        zigzag: side of the first helm order (default starboard)",
				"  --angle-deg <n>       zigzag: heading change that reverses the helm (default 10)",
				"  --rudder-deg <n>      zigzag: rudder angle (default 10), with --max-rudder-deg (default 35) giving helm",
				"  --lever <n>           pivot: lever magnitude in (0, 1] (default 0.5)",
				"Exit codes: 0 ok, 1 failed, 2 bad arguments",
				string.Empty);
	}
}
