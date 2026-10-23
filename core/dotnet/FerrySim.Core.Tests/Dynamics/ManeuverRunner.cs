using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>Turning circle results (ITTC 7.5-04-02-01 definitions), lengths in metres.</summary>
	public sealed class TurningCircleResult
	{
		public double ApproachSpeed;
		public double Advance;
		public double Transfer;
		public double TacticalDiameter;
		public double TimeTo90;
		public double TimeTo180;
		public double SteadyTurningDiameter;
		public double SteadyYawRate;
		public double SteadySpeed;
		public double SteadyRoll;
		public double MaximumRoll;
	}

	public sealed class ZigZagResult
	{
		public double FirstOvershoot;
		public double SecondOvershoot;
		public double ThirdOvershoot;
		public double TimeToFirstExecute;
	}

	public sealed class CrashStopResult
	{
		public double ApproachSpeed;
		public double HeadReach;
		public double LateralDeviation;
		public double TrackReach;
		public double TimeToStop;
		public double HeadingChange;
		public double TimeToAsternEngaged;
		public double TimeToShaftsReversed;
	}

	public sealed class PivotResult
	{
		public double YawRate;
		public double DriftSpeed;
		public double DriftDirectionRelativeToHeading;
		public double PortShaftRpm;
		public double StarboardShaftRpm;
	}

	/// <summary>
	/// Standard manoeuvres run against <see cref="VesselDynamicsSimulation"/> in calm deep water, for
	/// the plausibility tests and the dynamics report. Every run starts from rest with machinery idling in
	/// neutral, as <see cref="ISimulation.Reset"/> defines, so approach phases include the spin-up.
	/// </summary>
	public static class ManeuverRunner
	{
		public const int StepsPerSecond = SimulationClock.StepsPerSecond;

		public static VesselParameters Load(string vesselFile) => VesselParametersJson.Load(RepoPaths.Vessel(vesselFile));

		public static VesselDynamicsSimulation Create(VesselParameters parameters, IEnvironment environment = null, VesselState? initial = null) =>
			new VesselDynamicsSimulation(parameters, environment ?? UniformEnvironment.Calm, initial ?? VesselState.Planar(0.0, 0.0, 0.0, 0.0));

		public static void Run(ISimulation simulation, ControlInputs inputs, double seconds)
		{
			var steps = (int)Math.Round(seconds * StepsPerSecond);
			for (var i = 0; i < steps; i++)
			{
				simulation.Step(inputs);
			}
		}

		/// <summary>Runs both levers at <paramref name="lever"/> until the speed through the water settles.</summary>
		public static double SteadySpeed(VesselParameters parameters, double lever, out VesselDynamicsSimulation simulation, double maxSeconds = 600.0)
		{
			simulation = Create(parameters);
			var inputs = new ControlInputs(lever, lever, 0.0);
			Run(simulation, inputs, 60.0);
			var previous = simulation.Telemetry.SpeedThroughWater;
			for (var t = 60.0; t < maxSeconds; t += 20.0)
			{
				Run(simulation, inputs, 20.0);
				var speed = simulation.Telemetry.SpeedThroughWater;
				if (Math.Abs(speed - previous) < 0.002)
				{
					return speed;
				}

				previous = speed;
			}

			return simulation.Telemetry.SpeedThroughWater;
		}

		/// <summary>Both-lever position giving <paramref name="targetSpeed"/> steady in calm deep water.</summary>
		public static double LeverForSpeed(VesselParameters parameters, double targetSpeed)
		{
			var low = parameters.Controls.ThrottleStartLever.Value;
			var high = 1.0;
			for (var i = 0; i < 14; i++)
			{
				var mid = 0.5 * (low + high);
				if (SteadySpeed(parameters, mid, out _) < targetSpeed)
				{
					low = mid;
				}
				else
				{
					high = mid;
				}
			}

			return 0.5 * (low + high);
		}

		/// <summary>Brings the vessel to steady straight running at <paramref name="lever"/>.</summary>
		public static VesselDynamicsSimulation Approach(VesselParameters parameters, double lever, double seconds = 240.0)
		{
			var simulation = Create(parameters);
			Run(simulation, new ControlInputs(lever, lever, 0.0), seconds);
			return simulation;
		}

		public static TurningCircleResult TurningCircle(VesselParameters parameters, double lever, double helm)
		{
			var simulation = Approach(parameters, lever);
			var result = new TurningCircleResult { ApproachSpeed = simulation.Telemetry.SpeedOverGround };
			var start = simulation.State;
			var course = start.Heading;
			var inputs = new ControlInputs(lever, lever, helm);
			var turned = 0.0;
			var lastHeading = start.Heading;
			var time = 0.0;
			var maxRoll = 0.0;
			while (turned < 2.0 * Math.PI + Math.PI && time < 900.0)
			{
				simulation.Step(inputs);
				time += SimulationClock.FixedTimeStep;
				var state = simulation.State;
				turned += Math.Abs(Angles.WrapPi(state.Heading - lastHeading));
				lastHeading = state.Heading;
				maxRoll = Math.Max(maxRoll, Math.Abs(state.Roll));
				Along(start, course, state, out var along, out var across);
				if (result.TimeTo90 == 0.0 && turned >= 0.5 * Math.PI)
				{
					result.TimeTo90 = time;
					result.Advance = along;
					result.Transfer = Math.Abs(across);
				}

				if (result.TimeTo180 == 0.0 && turned >= Math.PI)
				{
					result.TimeTo180 = time;
					result.TacticalDiameter = Math.Abs(across);
				}
			}

			// Steady turn: diameter from the speed and yaw rate once the turn has settled.
			var settled = simulation.State;
			result.SteadyYawRate = Math.Abs(settled.YawRate);
			result.SteadySpeed = simulation.Telemetry.SpeedOverGround;
			result.SteadyTurningDiameter = 2.0 * result.SteadySpeed / Math.Max(result.SteadyYawRate, 1e-9);
			result.SteadyRoll = settled.Roll;
			result.MaximumRoll = maxRoll;
			return result;
		}

		/// <summary>
		/// Zig-zag with <paramref name="rudderAngle"/> and <paramref name="headingChange"/> (rad), first to
		/// starboard. Overshoots are the heading excursions beyond the check heading after each execute (rad).
		/// </summary>
		public static ZigZagResult ZigZag(VesselParameters parameters, double lever, double rudderAngle, double headingChange)
		{
			var simulation = Approach(parameters, lever);
			var helm = rudderAngle / parameters.Controls.HardOverAngle.Value;
			var course = simulation.State.Heading;
			var side = 1;
			var overshoots = new double[3];
			var executes = 0;
			var peak = 0.0;
			var time = 0.0;
			var result = new ZigZagResult();
			while (executes <= 3 && time < 600.0)
			{
				simulation.Step(new ControlInputs(lever, lever, side * helm));
				time += SimulationClock.FixedTimeStep;
				var deviation = Angles.WrapPi(simulation.State.Heading - course);
				if (side * deviation >= headingChange)
				{
					if (executes == 0)
					{
						result.TimeToFirstExecute = time;
					}
					else if (executes <= 3)
					{
						overshoots[executes - 1] = peak - headingChange;
					}

					executes++;
					side = -side;
					peak = 0.0;
				}
				else if (executes > 0)
				{
					peak = Math.Max(peak, -side * deviation);
				}
			}

			result.FirstOvershoot = overshoots[0];
			result.SecondOvershoot = overshoots[1];
			result.ThirdOvershoot = overshoots[2];
			return result;
		}

		public static CrashStopResult CrashStop(VesselParameters parameters, double lever)
		{
			var simulation = Approach(parameters, lever);
			var start = simulation.State;
			var course = start.Heading;
			var result = new CrashStopResult { ApproachSpeed = simulation.Telemetry.SpeedThroughWater };
			var inputs = new ControlInputs(-1.0, -1.0, 0.0);
			var time = 0.0;
			var track = 0.0;
			var previous = start.Position;
			while (time < 300.0)
			{
				simulation.Step(inputs);
				time += SimulationClock.FixedTimeStep;
				var position = simulation.State.Position;
				track += (position - previous).Magnitude;
				previous = position;
				if (result.TimeToAsternEngaged == 0.0 &&
					simulation.Telemetry.Port.Gear == GearState.Astern && simulation.Telemetry.Starboard.Gear == GearState.Astern)
				{
					result.TimeToAsternEngaged = time;
				}

				if (result.TimeToShaftsReversed == 0.0 &&
					simulation.Telemetry.Port.ShaftSpeed < 0.0 && simulation.Telemetry.Starboard.ShaftSpeed < 0.0)
				{
					result.TimeToShaftsReversed = time;
				}

				if (simulation.Telemetry.SpeedThroughWater <= 0.0)
				{
					break;
				}
			}

			Along(start, course, simulation.State, out var along, out var across);
			result.HeadReach = along;
			result.LateralDeviation = across;
			result.TrackReach = track;
			result.TimeToStop = time;
			result.HeadingChange = Angles.WrapPi(simulation.State.Heading - course);
			return result;
		}

		public static PivotResult Pivot(VesselParameters parameters, double portLever, double starboardLever, double seconds = 90.0)
		{
			var simulation = Create(parameters);
			Run(simulation, new ControlInputs(portLever, starboardLever, 0.0), seconds);
			var state = simulation.State;
			var ground = state.GroundVelocity;
			return new PivotResult
			{
				YawRate = state.YawRate,
				DriftSpeed = ground.Magnitude,
				DriftDirectionRelativeToHeading = ground.Magnitude > 1e-6 ? Angles.WrapPi(ground.Direction - state.Heading) : 0.0,
				PortShaftRpm = Units.RadiansPerSecondToRpm(simulation.Telemetry.Port.ShaftSpeed),
				StarboardShaftRpm = Units.RadiansPerSecondToRpm(simulation.Telemetry.Starboard.ShaftSpeed),
			};
		}

		/// <summary>Seconds from rest with both levers full ahead to reach <paramref name="targetSpeed"/> through the water.</summary>
		public static double TimeToSpeed(VesselParameters parameters, double targetSpeed, double lever = 1.0)
		{
			var simulation = Create(parameters);
			var inputs = new ControlInputs(lever, lever, 0.0);
			for (var step = 1; step <= 600 * StepsPerSecond; step++)
			{
				simulation.Step(inputs);
				if (simulation.Telemetry.SpeedThroughWater >= targetSpeed)
				{
					return step / (double)StepsPerSecond;
				}
			}

			return double.PositiveInfinity;
		}

		private static void Along(VesselState start, double course, VesselState state, out double along, out double across)
		{
			var d = state.Position - start.Position;
			along = d.North * Math.Cos(course) + d.East * Math.Sin(course);
			across = -d.North * Math.Sin(course) + d.East * Math.Cos(course);
		}
	}
}
