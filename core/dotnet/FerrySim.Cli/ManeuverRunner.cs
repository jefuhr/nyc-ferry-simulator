using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>What every run needs: the vessel, the world around it and how to sample.</summary>
	public sealed class RunSetup
	{
		public RunSetup(ISimulationFactory factory, VesselParameters vessel, EnvironmentSpec environment, int sampleHz, double headingDegrees)
			: this(factory, vessel, RunEnvironment.Uniform(environment), sampleHz, headingDegrees)
		{
		}

		public RunSetup(ISimulationFactory factory, VesselParameters vessel, RunEnvironment environment, int sampleHz, double headingDegrees)
		{
			Factory = factory;
			Vessel = vessel;
			Environment = environment;
			SampleHz = sampleHz;
			HeadingDegrees = headingDegrees;
		}

		public ISimulationFactory Factory { get; }

		public VesselParameters Vessel { get; }

		public RunEnvironment Environment { get; }

		/// <summary>CSV rows per simulated second; divides the physics rate.</summary>
		public int SampleHz { get; }

		public double HeadingDegrees { get; }

		public int StepsPerSample => SimulationClock.StepsPerSecond / SampleHz;
	}

	public sealed class RunResult
	{
		public RunResult(CsvRecorder recorder, List<KeyValuePair<string, string>> header)
		{
			Recorder = recorder;
			Header = header;
		}

		public CsvRecorder Recorder { get; }

		public List<KeyValuePair<string, string>> Header { get; }

		public string SettleSummary { get; set; }

		/// <summary>What the environment had to make up during the recorded run, or null.</summary>
		public string CoverageWarning { get; set; }
	}

	/// <summary>Drives a simulation with a controller: settling at a test speed, running maneuvers and replaying traces.</summary>
	internal static class ManeuverRunner
	{
		public const double SpeedToleranceMetresPerSecond = 0.01;
		public const int MaxBisections = 40;
		public const double DefaultLeadInSeconds = 10.0;

		public static RunResult RunManeuver(RunSetup setup, ManeuverSpec spec, ManeuverPlan plan, double? testSpeedMps, double? fixedDuration, double leadInSeconds)
		{
			var environment = setup.Environment.Create();
			var recorder = new CsvRecorder();
			string settleNote = null;
			var extra = new List<KeyValuePair<string, string>>();

			ISimulation sim;
			double lever = 0.0;
			if (spec.StartsAtRest)
			{
				sim = CreateSimulation(setup, environment, 0.0);
			}
			else
			{
				var speed = testSpeedMps.Value;
				lever = FindLever(setup, environment, speed, out var achieved);
				RunEnvironment.ResetCounters(environment);
				sim = CreateSimulation(setup, environment, speed);
				Drive(sim, new SettleController(lever), recorder, setup.StepsPerSample, recordFinalRow: false);
				recorder.TrimBefore(sim.State.Time - leadInSeconds - 1e-9);
				settleNote = $"lever {lever:F4} gives {Units.MetresPerSecondToKnots(sim.Telemetry.SpeedThroughWater):F3} kn through water after {sim.State.Time:F0} s";
				extra.Add(Maneuvers.Pair("settle.lever", Maneuvers.Number(Math.Round(lever, 6))));
				extra.Add(Maneuvers.Pair("settle.stw_kn", Maneuvers.Number(Math.Round(Units.MetresPerSecondToKnots(sim.Telemetry.SpeedThroughWater), 4))));
				extra.Add(Maneuvers.Pair("settle.duration_s", Maneuvers.Number(sim.State.Time)));
			}

			var start = sim.State.Time;
			Drive(sim, plan.Create(lever), recorder, setup.StepsPerSample, recordFinalRow: true);

			var header = BaseHeader(setup, sim, environment, spec.Name, start);
			if (!spec.StartsAtRest)
			{
				header.Add(Maneuvers.Pair("param.speed_kn", Maneuvers.Number(Math.Round(Units.MetresPerSecondToKnots(testSpeedMps.Value), 4))));
			}

			header.AddRange(Prefixed(plan.Parameters));
			header.Add(Maneuvers.Pair("duration_s", fixedDuration.HasValue ? Maneuvers.Number(fixedDuration.Value) : "auto"));
			header.Add(Maneuvers.Pair("lead_in_s", spec.StartsAtRest ? "0" : Maneuvers.Number(leadInSeconds)));
			header.AddRange(extra);
			return new RunResult(recorder, header) { SettleSummary = settleNote, CoverageWarning = RunEnvironment.CoverageWarning(environment) };

			static IEnumerable<KeyValuePair<string, string>> Prefixed(List<KeyValuePair<string, string>> parameters)
			{
				foreach (var pair in parameters)
				{
					yield return new KeyValuePair<string, string>("param." + pair.Key, pair.Value);
				}
			}
		}

		public static RunResult RunTrace(RunSetup setup, TraceInputs trace, string traceName, double? fixedDuration)
		{
			var environment = setup.Environment.Create();
			var recorder = new CsvRecorder();
			var sim = CreateSimulation(setup, environment, 0.0);
			var duration = fixedDuration ?? trace.EndTime;
			Drive(sim, new TraceController(trace, duration), recorder, setup.StepsPerSample, recordFinalRow: true);

			var header = BaseHeader(setup, sim, environment, "trace-replay", 0.0);
			header.Add(Maneuvers.Pair("param.inputs", traceName));
			header.Add(Maneuvers.Pair("param.rows", trace.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			header.Add(Maneuvers.Pair("duration_s", Maneuvers.Number(duration)));
			header.Add(Maneuvers.Pair("lead_in_s", "0"));
			return new RunResult(recorder, header) { CoverageWarning = RunEnvironment.CoverageWarning(environment) };
		}

		/// <summary>
		/// Advances <paramref name="sim"/> under <paramref name="controller"/>, recording a row every
		/// <paramref name="stepsPerSample"/> steps. The row that ends the run is always recorded unless
		/// <paramref name="recordFinalRow"/> is false (the settle phase hands that instant to the maneuver).
		/// </summary>
		public static void Drive(ISimulation sim, IController controller, CsvRecorder recorder, int stepsPerSample, bool recordFinalRow)
		{
			var startStep = StepOf(sim.State.Time);
			while (true)
			{
				var step = StepOf(sim.State.Time);
				var elapsed = (step - startStep) / (double)SimulationClock.StepsPerSecond;
				var controls = controller.Command(elapsed, sim.State, sim.Telemetry);
				if (controller.Finished && !recordFinalRow)
				{
					return;
				}

				if (recorder != null && (step % stepsPerSample == 0 || controller.Finished))
				{
					recorder.Add(sim.State, sim.Telemetry, controls);
				}

				if (controller.Finished)
				{
					return;
				}

				sim.Step(controls);
				if (!sim.State.IsFinite())
				{
					throw new CliException($"The model produced a non-finite state at t={sim.State.Time:F2} s.");
				}
			}
		}

		/// <summary>
		/// Bisects on a common lever position for both engines until the settled speed through water
		/// matches the target. Full ahead must reach it; zero ahead is assumed below it.
		/// </summary>
		public static double FindLever(RunSetup setup, IEnvironment environment, double targetMetresPerSecond, out double achievedMetresPerSecond)
		{
			var top = SettledSpeed(setup, environment, targetMetresPerSecond, 1.0);
			if (top < targetMetresPerSecond - SpeedToleranceMetresPerSecond)
			{
				throw new CliException(
					$"{setup.Vessel.Id} cannot reach {Units.MetresPerSecondToKnots(targetMetresPerSecond):F2} kn through water in this environment; " +
					$"full ahead settles at {Units.MetresPerSecondToKnots(top):F2} kn.");
			}

			if (top - targetMetresPerSecond <= SpeedToleranceMetresPerSecond)
			{
				achievedMetresPerSecond = top;
				return 1.0;
			}

			double low = 0.0, high = 1.0, mid = 0.5, speed = 0.0;
			for (var i = 0; i < MaxBisections; i++)
			{
				mid = 0.5 * (low + high);
				speed = SettledSpeed(setup, environment, targetMetresPerSecond, mid);
				if (Math.Abs(speed - targetMetresPerSecond) <= SpeedToleranceMetresPerSecond)
				{
					break;
				}

				if (speed < targetMetresPerSecond)
				{
					low = mid;
				}
				else
				{
					high = mid;
				}
			}

			achievedMetresPerSecond = speed;
			return mid;
		}

		private static double SettledSpeed(RunSetup setup, IEnvironment environment, double startSpeed, double lever)
		{
			var sim = CreateSimulation(setup, environment, startSpeed);
			Drive(sim, new SettleController(lever), null, setup.StepsPerSample, recordFinalRow: false);
			return sim.Telemetry.SpeedThroughWater;
		}

		/// <summary>Creates a simulation at the origin, pointing at the run heading, already moving at the given speed through the water.</summary>
		public static ISimulation CreateSimulation(RunSetup setup, IEnvironment environment, double waterSpeed)
		{
			var heading = Angles.ToRadians(setup.HeadingDegrees);
			var current = environment.SampleCurrent(0.0, 0.0, 0.0);
			var ground = HorizontalVector.FromSpeedToward(waterSpeed, heading) + current;
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var surge = ground.North * cos + ground.East * sin;
			var sway = -ground.North * sin + ground.East * cos;
			var initial = VesselState.Planar(0.0, 0.0, 0.0, heading, surge, sway);
			return setup.Factory.Create(setup.Vessel, environment, initial);
		}

		private static List<KeyValuePair<string, string>> BaseHeader(RunSetup setup, ISimulation sim, IEnvironment environment, string maneuver, double startTime)
		{
			var header = new List<KeyValuePair<string, string>>
			{
				Maneuvers.Pair("vessel", setup.Vessel.Id),
				Maneuvers.Pair("maneuver", maneuver),
				Maneuvers.Pair("maneuver_start_s", Maneuvers.Number(startTime)),
				Maneuvers.Pair("environment", setup.Environment.Text),
			};

			// A pack names the day, its digest and the start; a uniform environment adds nothing.
			header.AddRange(setup.Environment.Header(environment));
			header.Add(Maneuvers.Pair("model", sim.GetType().FullName));
			header.Add(Maneuvers.Pair("factory", setup.Factory.GetType().FullName));
			header.Add(Maneuvers.Pair("physics_hz", SimulationClock.StepsPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			header.Add(Maneuvers.Pair("sample_hz", setup.SampleHz.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			header.Add(Maneuvers.Pair("initial_heading_deg", Maneuvers.Number(setup.HeadingDegrees)));
			return header;
		}

		private static long StepOf(double time) => (long)Math.Round(time * SimulationClock.StepsPerSecond);
	}
}
