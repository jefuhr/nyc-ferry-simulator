using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	/// <summary>Locates repository files and runs the CLI in-process.</summary>
	public static class TestSupport
	{
		public static string RepoRoot
		{
			get
			{
				var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
				while (directory != null)
				{
					if (Directory.Exists(Path.Combine(directory.FullName, "vessels")) &&
						Directory.Exists(Path.Combine(directory.FullName, "core")))
					{
						return directory.FullName;
					}

					directory = directory.Parent;
				}

				throw new InvalidOperationException("Could not find the repository root above " + TestContext.CurrentContext.TestDirectory);
			}
		}

		public static string Vessel(string fileName) => Path.Combine(RepoRoot, "vessels", fileName);

		public static (int Code, string Out, string Err) Run(ISimulationFactory factory, params string[] args)
		{
			var stdout = new StringWriter();
			var stderr = new StringWriter();
			var code = new CliApp(factory).Run(args, stdout, stderr);
			return (code, stdout.ToString(), stderr.ToString());
		}

		/// <summary>A fresh scratch directory that is deleted after the test.</summary>
		public sealed class ScratchDirectory : IDisposable
		{
			public ScratchDirectory()
			{
				Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ferrysim-cli-tests", Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(Path);
			}

			public string Path { get; }

			public string File(string name) => System.IO.Path.Combine(Path, name);

			public void Dispose()
			{
				try
				{
					Directory.Delete(Path, true);
				}
				catch (IOException)
				{
				}
			}
		}
	}

	/// <summary>
	/// A stand-in model for the CLI tests: speed through water follows the mean lever with a first-order
	/// lag, heading turns at a rate proportional to helm. It records every control it receives.
	/// </summary>
	public sealed class FakeSimulation : ISimulation
	{
		private const double TimeConstant = 5.0;
		private readonly double _maxSpeed;
		private long _steps;
		private double _north;
		private double _east;
		private double _heading;
		private double _speed;
		private ControlInputs _last;

		public FakeSimulation(VesselParameters parameters, double maxSpeed, in VesselState initial)
		{
			Parameters = parameters;
			_maxSpeed = maxSpeed;
			Received = new List<ControlInputs>();
			Reset(initial);
		}

		public List<ControlInputs> Received { get; }

		public VesselParameters Parameters { get; }

		public VesselState State { get; private set; }

		public VesselTelemetry Telemetry { get; private set; }

		public void Reset(in VesselState initial)
		{
			_steps = 0;
			_north = initial.North;
			_east = initial.East;
			_heading = initial.Heading;
			_speed = initial.Surge;
			Publish();
		}

		public void Step(in ControlInputs controls)
		{
			Received.Add(controls);
			const double dt = SimulationClock.FixedTimeStep;
			var target = 0.5 * (controls.PortLever + controls.StarboardLever) * _maxSpeed;
			_speed += (target - _speed) * (dt / TimeConstant);
			_heading += controls.Helm * 0.05 * dt;
			_north += _speed * Math.Cos(_heading) * dt;
			_east += _speed * Math.Sin(_heading) * dt;
			_steps++;
			_last = controls;
			Publish();
		}

		private void Publish()
		{
			var time = SimulationClock.TimeAtStep(_steps);
			State = VesselState.Planar(time, _north, _east, _heading, _speed, 0.0, _last.Helm * 0.05);
			var engine = new EngineTelemetry(100.0, 40.0, GearState.Ahead, Math.Abs(_last.PortLever));
			Telemetry = new VesselTelemetry(engine, engine, _last.Helm * 0.5, _last.Helm * 0.5, _speed, Math.Abs(_speed), _heading, 20.0, 0.0);
		}
	}

	public sealed class FakeFactory : ISimulationFactory
	{
		public FakeFactory(double maxSpeed = 15.0)
		{
			MaxSpeed = maxSpeed;
		}

		public double MaxSpeed { get; }

		public List<FakeSimulation> Created { get; } = new List<FakeSimulation>();

		public List<VesselState> Initials { get; } = new List<VesselState>();

		public ISimulation Create(VesselParameters parameters, IEnvironment environment, in VesselState initialState)
		{
			var sim = new FakeSimulation(parameters, MaxSpeed, initialState);
			Created.Add(sim);
			Initials.Add(initialState);
			return sim;
		}

		public ISimulation Create(VesselParameters parameters, IEnvironment environment, IWorldGeometry geometry, in VesselState initialState) =>
			Create(parameters, environment, initialState);
	}

	/// <summary>Builds telemetry CSVs from known tracks, so metrics can be checked against closed-form answers.</summary>
	public sealed class SyntheticCsv
	{
		private readonly CsvRecorder _recorder = new CsvRecorder();
		private readonly List<KeyValuePair<string, string>> _header = new List<KeyValuePair<string, string>>();

		public SyntheticCsv(string maneuver, double startSeconds = 0.0)
		{
			_header.Add(new KeyValuePair<string, string>("vessel", "synthetic"));
			_header.Add(new KeyValuePair<string, string>("maneuver", maneuver));
			_header.Add(new KeyValuePair<string, string>("maneuver_start_s", startSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			_header.Add(new KeyValuePair<string, string>("environment", "calm"));
			_header.Add(new KeyValuePair<string, string>("model", "Synthetic"));
		}

		public SyntheticCsv With(string key, string value)
		{
			_header.Add(new KeyValuePair<string, string>(key, value));
			return this;
		}

		/// <summary>Adds one row. Angles are degrees, speeds m/s; speed through water is <paramref name="stw"/>.</summary>
		public SyntheticCsv Row(double time, double north, double east, double headingDeg, double stw, double yawRateDegps = 0.0, double helm = 0.0, double lever = 0.0)
		{
			var state = VesselState.Planar(time, north, east, Angles.ToRadians(headingDeg), stw, 0.0, Angles.ToRadians(yawRateDegps));
			var engine = new EngineTelemetry(100.0, 40.0, GearState.Ahead, 0.5);
			var telemetry = new VesselTelemetry(engine, engine, 0.0, 0.0, stw, Math.Abs(stw), Angles.ToRadians(headingDeg), 20.0, 0.0);
			_recorder.Add(state, telemetry, new ControlInputs(lever, lever, helm));
			return this;
		}

		public string Render() => _recorder.Render(_header);

		public string WriteTo(TestSupport.ScratchDirectory scratch, string name)
		{
			var path = scratch.File(name);
			File.WriteAllText(path, Render());
			return path;
		}

		public MetricsReport Metrics() => ManeuverMetrics.Compute(TelemetryTable.Parse(Render().Split('\n')));
	}
}
