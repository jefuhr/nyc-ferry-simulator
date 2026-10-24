using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Sim
{
	/// <summary>Sees every fixed step the driver takes, in order. The run recorder and tests use it.</summary>
	public interface IStepObserver
	{
		/// <summary>
		/// Called just before step <paramref name="step"/> runs, with the state and telemetry at that
		/// instant and the controls the step applies. This is one row of the telemetry CSV.
		/// </summary>
		void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls);

		/// <summary>Called after a step, when <paramref name="stepCount"/> steps have run since the start of the run.</summary>
		void AfterStep(long stepCount, in VesselState state, in VesselTelemetry telemetry);
	}

	/// <summary>What one call to <see cref="FixedStepDriver.Advance"/> did.</summary>
	public readonly struct FrameAdvance
	{
		public FrameAdvance(int steps, double droppedTime)
		{
			Steps = steps;
			DroppedTime = droppedTime;
		}

		/// <summary>Physics steps taken this frame.</summary>
		public int Steps { get; }

		/// <summary>Simulated time discarded because the frame fell too far behind, seconds.</summary>
		public double DroppedTime { get; }

		public bool Dropped => DroppedTime > 0.0;
	}

	/// <summary>
	/// Advances an <see cref="ISimulation"/> in exact <see cref="SimulationClock.FixedTimeStep"/> steps from
	/// variable frame times. Frame time accumulates, whole steps run, and the remainder carries over to the
	/// next frame, where it also sets the interpolation factor between the last two states. Physics only
	/// ever sees whole steps and step-indexed controls, so the frame rate cannot change the trajectory.
	/// Engine-independent so tests can feed it simulated frame times.
	/// </summary>
	public sealed class FixedStepDriver
	{
		/// <summary>A quarter of a simulated second per frame; a slower frame drops the excess.</summary>
		public const int DefaultMaxStepsPerFrame = 25;

		private readonly List<IStepObserver> _observers = new List<IStepObserver>();
		private ISimulation _simulation;
		private IControlInputSource _controls;
		private int _maxStepsPerFrame;
		private double _accumulator;
		private long _stepCount;
		private VesselState _previous;
		private ControlInputs _lastControls;

		public FixedStepDriver(ISimulation simulation, IControlInputSource controls, int maxStepsPerFrame = DefaultMaxStepsPerFrame)
		{
			_controls = controls ?? throw new ArgumentNullException(nameof(controls));
			MaxStepsPerFrame = maxStepsPerFrame;
			Restart(simulation);
		}

		public ISimulation Simulation => _simulation;

		/// <summary>Where each step's controls come from. Can be swapped mid-run, for example to hand over to a replay.</summary>
		public IControlInputSource Controls
		{
			get => _controls;
			set => _controls = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>Most physics steps one frame may run; frames that owe more drop the excess time.</summary>
		public int MaxStepsPerFrame
		{
			get => _maxStepsPerFrame;
			set
			{
				if (value < 1)
				{
					throw new ArgumentOutOfRangeException(nameof(value), "At least one step per frame is needed.");
				}

				_maxStepsPerFrame = value;
			}
		}

		/// <summary>
		/// When set, the run stops at this many steps: <see cref="Advance"/> never steps past it and discards
		/// frame time owed beyond it (not counted as dropped). Replays and fixed-length tests use it.
		/// </summary>
		public long? StepLimit { get; set; }

		/// <summary>True when <see cref="StepLimit"/> is set and reached.</summary>
		public bool AtStepLimit => StepLimit.HasValue && _stepCount >= StepLimit.Value;

		/// <summary>Steps since the start of the run.</summary>
		public long StepCount => _stepCount;

		/// <summary>Simulated seconds since the start of the run, derived from the step count.</summary>
		public double SimulationTime => SimulationClock.TimeAtStep(_stepCount);

		/// <summary>State before the most recent step (equal to <see cref="CurrentState"/> before the first).</summary>
		public VesselState PreviousState => _previous;

		/// <summary>State after the most recent step.</summary>
		public VesselState CurrentState => _simulation.State;

		/// <summary>Controls applied in the most recent step; neutral before the first.</summary>
		public ControlInputs LastControls => _lastControls;

		/// <summary>Frame time owed to the physics but not yet stepped, in [0, one step).</summary>
		public double Accumulator => _accumulator;

		/// <summary>
		/// Fraction of a step the renderer is past <see cref="PreviousState"/> toward <see cref="CurrentState"/>,
		/// in [0, 1].
		/// </summary>
		public double InterpolationFactor => Math.Min(1.0, Math.Max(0.0, _accumulator / SimulationClock.FixedTimeStep));

		/// <summary>Total simulated time dropped by the per-frame guard since the run started, seconds.</summary>
		public double TotalDroppedTime { get; private set; }

		/// <summary>Frames that hit the per-frame guard since the run started.</summary>
		public int DroppedFrameCount { get; private set; }

		/// <summary>True once the model produced a non-finite state; the driver then stops stepping.</summary>
		public bool Faulted { get; private set; }

		public void AddObserver(IStepObserver observer)
		{
			if (observer == null)
			{
				throw new ArgumentNullException(nameof(observer));
			}

			_observers.Add(observer);
		}

		public bool RemoveObserver(IStepObserver observer) => _observers.Remove(observer);

		/// <summary>Starts a new run on <paramref name="simulation"/>: the clock, accumulator and drop counters return to zero.</summary>
		public void Restart(ISimulation simulation)
		{
			_simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
			_accumulator = 0.0;
			_stepCount = 0;
			_previous = simulation.State;
			_lastControls = ControlInputs.Neutral;
			TotalDroppedTime = 0.0;
			DroppedFrameCount = 0;
			Faulted = false;
		}

		/// <summary>
		/// Adds one frame of <paramref name="deltaTime"/> seconds and runs every whole step now due, up to
		/// <see cref="MaxStepsPerFrame"/>. Zero, negative and non-finite frame times run nothing.
		/// </summary>
		public FrameAdvance Advance(double deltaTime)
		{
			if (Faulted || AtStepLimit || !(deltaTime > 0.0) || double.IsInfinity(deltaTime))
			{
				return new FrameAdvance(0, 0.0);
			}

			_accumulator += deltaTime;
			var due = (long)Math.Floor(_accumulator / SimulationClock.FixedTimeStep);
			if (StepLimit.HasValue && due >= StepLimit.Value - _stepCount)
			{
				due = StepLimit.Value - _stepCount;
				_accumulator = due * SimulationClock.FixedTimeStep;
			}

			var dropped = 0.0;
			if (due > _maxStepsPerFrame)
			{
				dropped = (due - _maxStepsPerFrame) * SimulationClock.FixedTimeStep;
				due = _maxStepsPerFrame;
				_accumulator -= dropped;
				TotalDroppedTime += dropped;
				DroppedFrameCount++;
			}

			var taken = 0;
			for (; taken < due; taken++)
			{
				_accumulator -= SimulationClock.FixedTimeStep;
				if (!StepOnce())
				{
					_accumulator = 0.0;
					break;
				}
			}

			// Repeated subtraction can leave a hair below zero; the remainder is never negative.
			if (_accumulator < 0.0)
			{
				_accumulator = 0.0;
			}

			return new FrameAdvance(taken, dropped);
		}

		/// <summary>Runs exactly one step regardless of frame time, for replays and tests.</summary>
		public bool StepOnce()
		{
			if (Faulted)
			{
				return false;
			}

			var controls = Canonical(_controls.ControlsForStep(_stepCount));
			var before = _simulation.State;
			var telemetry = _simulation.Telemetry;
			for (var i = 0; i < _observers.Count; i++)
			{
				_observers[i].BeforeStep(_stepCount, before, telemetry, controls);
			}

			_previous = before;
			_simulation.Step(controls);
			_stepCount++;
			_lastControls = controls;
			var after = _simulation.State;
			if (!after.IsFinite())
			{
				Faulted = true;
				return false;
			}

			var afterTelemetry = _simulation.Telemetry;
			for (var i = 0; i < _observers.Count; i++)
			{
				_observers[i].AfterStep(_stepCount, after, afterTelemetry);
			}

			return true;
		}

		/// <summary>
		/// Turns −0.0 into +0.0 so that what the physics receives is exactly what a recorder writes and a
		/// replay reads back (some runtimes parse "-0" as +0).
		/// </summary>
		public static ControlInputs Canonical(in ControlInputs controls) =>
			new ControlInputs(controls.PortLever + 0.0, controls.StarboardLever + 0.0, controls.Helm + 0.0);
	}
}
