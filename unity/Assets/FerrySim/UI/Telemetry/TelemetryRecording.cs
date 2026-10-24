using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	/// <summary>The state, telemetry and controls at one recorded instant, in SI units.</summary>
	public readonly struct TelemetrySample
	{
		public TelemetrySample(VesselState state, VesselTelemetry telemetry, ControlInputs controls)
		{
			State = state;
			Telemetry = telemetry;
			Controls = controls;
		}

		public VesselState State { get; }

		public VesselTelemetry Telemetry { get; }

		public ControlInputs Controls { get; }
	}

	/// <summary>
	/// A recorded run held as SI columns, from a CLI or Unity telemetry CSV or built in memory. Rows are
	/// in strictly increasing time but need not be evenly spaced. Values the CSV does not carry (roll,
	/// pitch, heave and their rates) read 0, and engine throttle reads NaN.
	/// </summary>
	public sealed class TelemetryRecording
	{
		private readonly Dictionary<string, string> _headerLookup;
		private readonly double[] _threshold;
		private readonly double[] _time;
		private readonly double[] _north;
		private readonly double[] _east;
		private readonly double[] _heading;
		private readonly double[] _surge;
		private readonly double[] _sway;
		private readonly double[] _yawRate;
		private readonly double[] _sog;
		private readonly double[] _cog;
		private readonly double[] _stw;
		private readonly double[] _portEngine;
		private readonly double[] _starboardEngine;
		private readonly double[] _portShaft;
		private readonly double[] _starboardShaft;
		private readonly GearState[] _portGear;
		private readonly GearState[] _starboardGear;
		private readonly double[] _portRudder;
		private readonly double[] _starboardRudder;
		private readonly double[] _depth;
		private readonly double[] _contact;
		private readonly double[] _portLever;
		private readonly double[] _starboardLever;
		private readonly double[] _helm;

		internal TelemetryRecording(string label, IReadOnlyList<KeyValuePair<string, string>> header, TelemetryRecordingBuilder columns)
		{
			Label = label ?? string.Empty;
			Header = header ?? Array.Empty<KeyValuePair<string, string>>();
			_headerLookup = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var pair in Header)
			{
				_headerLookup[pair.Key] = pair.Value;
			}

			Count = columns.Count;
			_time = columns.TimeColumn.ToArray();
			_north = columns.North.ToArray();
			_east = columns.East.ToArray();
			_heading = columns.Heading.ToArray();
			_surge = columns.Surge.ToArray();
			_sway = columns.Sway.ToArray();
			_yawRate = columns.YawRate.ToArray();
			_sog = columns.Sog.ToArray();
			_cog = columns.Cog.ToArray();
			_stw = columns.Stw.ToArray();
			_portEngine = columns.PortEngine.ToArray();
			_starboardEngine = columns.StarboardEngine.ToArray();
			_portShaft = columns.PortShaft.ToArray();
			_starboardShaft = columns.StarboardShaft.ToArray();
			_portGear = columns.PortGear.ToArray();
			_starboardGear = columns.StarboardGear.ToArray();
			_portRudder = columns.PortRudder.ToArray();
			_starboardRudder = columns.StarboardRudder.ToArray();
			_depth = columns.Depth.ToArray();
			_contact = columns.Contact.ToArray();
			_portLever = columns.PortLever.ToArray();
			_starboardLever = columns.StarboardLever.ToArray();
			_helm = columns.Helm.ToArray();
			_threshold = columns.HasThreshold ? columns.Threshold.ToArray() : null;
			if (TelemetryCsv.TryParseScenarioStart(HeaderValue(TelemetryCsv.ScenarioStartKey), out var start))
			{
				ScenarioStartUtc = start;
			}
		}

		/// <summary>Where the recording came from, for example the CSV file name.</summary>
		public string Label { get; }

		/// <summary>The <c># key: value</c> header comments in file order, including <c>ferrysim-csv</c>.</summary>
		public IReadOnlyList<KeyValuePair<string, string>> Header { get; }

		public int Count { get; }

		public double StartTime => _time[0];

		public double EndTime => _time[Count - 1];

		public double Duration => EndTime - StartTime;

		/// <summary>The <c>vessel</c> header, for example <c>t-class</c>; null when absent.</summary>
		public string VesselId => HeaderValue("vessel");

		/// <summary>The scenario's start from the <c>scenario_start_utc</c> header (Unity runs); null when absent (CLI runs).</summary>
		public DateTimeOffset? ScenarioStartUtc { get; }

		/// <summary>
		/// The shallow-water alarm threshold in force at each sample, metres under the keel, when the recording
		/// carries one (<see cref="TelemetryCsv.DepthAlarmThresholdColumn"/>); null when it does not.
		/// </summary>
		public IReadOnlyList<double> DepthAlarmThreshold => _threshold;

		public IReadOnlyList<double> Time => _time;

		public IReadOnlyList<double> North => _north;

		public IReadOnlyList<double> East => _east;

		/// <summary>True heading, radians.</summary>
		public IReadOnlyList<double> Heading => _heading;

		public IReadOnlyList<double> YawRate => _yawRate;

		public IReadOnlyList<double> SpeedOverGround => _sog;

		public IReadOnlyList<double> CourseOverGround => _cog;

		public IReadOnlyList<double> SpeedThroughWater => _stw;

		public IReadOnlyList<double> PortEngineSpeed => _portEngine;

		public IReadOnlyList<double> StarboardEngineSpeed => _starboardEngine;

		public IReadOnlyList<double> PortShaftSpeed => _portShaft;

		public IReadOnlyList<double> StarboardShaftSpeed => _starboardShaft;

		public IReadOnlyList<GearState> PortGear => _portGear;

		public IReadOnlyList<GearState> StarboardGear => _starboardGear;

		public IReadOnlyList<double> PortRudder => _portRudder;

		public IReadOnlyList<double> StarboardRudder => _starboardRudder;

		public IReadOnlyList<double> DepthUnderKeel => _depth;

		public IReadOnlyList<double> ContactForce => _contact;

		public IReadOnlyList<double> PortLever => _portLever;

		public IReadOnlyList<double> StarboardLever => _starboardLever;

		public IReadOnlyList<double> Helm => _helm;

		public string HeaderValue(string key, string fallback = null) =>
			key != null && _headerLookup.TryGetValue(key, out var value) ? value : fallback;

		public double HeaderDouble(string key, double fallback) =>
			double.TryParse(HeaderValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

		public TelemetrySample Sample(int index)
		{
			var state = new VesselState(
				_time[index], _north[index], _east[index], 0.0, 0.0, 0.0, _heading[index],
				_surge[index], _sway[index], 0.0, 0.0, 0.0, _yawRate[index]);
			var telemetry = new VesselTelemetry(
				new EngineTelemetry(_portEngine[index], _portShaft[index], _portGear[index], double.NaN),
				new EngineTelemetry(_starboardEngine[index], _starboardShaft[index], _starboardGear[index], double.NaN),
				_portRudder[index], _starboardRudder[index], _stw[index], _sog[index], _cog[index], _depth[index], _contact[index]);
			return new TelemetrySample(state, telemetry, new ControlInputs(_portLever[index], _starboardLever[index], _helm[index]));
		}

		/// <summary>Index of the last row at or before <paramref name="time"/>; the first row for earlier times.</summary>
		public int IndexAtOrBefore(double time)
		{
			if (double.IsNaN(time) || time <= _time[0])
			{
				return 0;
			}

			var low = 0;
			var high = Count - 1;
			while (low < high)
			{
				var mid = (low + high + 1) / 2;
				if (_time[mid] <= time)
				{
					low = mid;
				}
				else
				{
					high = mid - 1;
				}
			}

			return low;
		}
	}

	/// <summary>Collects samples column by column and freezes them into a <see cref="TelemetryRecording"/>.</summary>
	public sealed class TelemetryRecordingBuilder
	{
		private readonly string _label;
		private readonly List<KeyValuePair<string, string>> _header;

		public TelemetryRecordingBuilder(string label, IEnumerable<KeyValuePair<string, string>> header = null)
		{
			_label = label;
			_header = header != null ? new List<KeyValuePair<string, string>>(header) : new List<KeyValuePair<string, string>>();
		}

		public int Count => TimeColumn.Count;

		public double LastTime => Count == 0 ? double.NaN : TimeColumn[Count - 1];

		internal List<double> TimeColumn { get; } = new List<double>();

		internal List<double> North { get; } = new List<double>();

		internal List<double> East { get; } = new List<double>();

		internal List<double> Heading { get; } = new List<double>();

		internal List<double> Surge { get; } = new List<double>();

		internal List<double> Sway { get; } = new List<double>();

		internal List<double> YawRate { get; } = new List<double>();

		internal List<double> Sog { get; } = new List<double>();

		internal List<double> Cog { get; } = new List<double>();

		internal List<double> Stw { get; } = new List<double>();

		internal List<double> PortEngine { get; } = new List<double>();

		internal List<double> StarboardEngine { get; } = new List<double>();

		internal List<double> PortShaft { get; } = new List<double>();

		internal List<double> StarboardShaft { get; } = new List<double>();

		internal List<GearState> PortGear { get; } = new List<GearState>();

		internal List<GearState> StarboardGear { get; } = new List<GearState>();

		internal List<double> PortRudder { get; } = new List<double>();

		internal List<double> StarboardRudder { get; } = new List<double>();

		internal List<double> Depth { get; } = new List<double>();

		internal List<double> Contact { get; } = new List<double>();

		internal List<double> PortLever { get; } = new List<double>();

		internal List<double> StarboardLever { get; } = new List<double>();

		internal List<double> Helm { get; } = new List<double>();

		internal List<double> Threshold { get; } = new List<double>();

		internal bool HasThreshold => Count > 0 && Threshold.Count == Count;

		/// <summary>Appends one instant. Times must strictly increase.</summary>
		public void Add(VesselState state, VesselTelemetry telemetry, ControlInputs controls)
		{
			if (Threshold.Count > 0)
			{
				throw new ArgumentException("This recording carries the depth alarm threshold; give it for every sample.");
			}

			Append(state, telemetry, controls);
		}

		/// <summary>Appends one instant with the depth alarm threshold in force then, metres under the keel. Give it for every sample or none.</summary>
		public void Add(VesselState state, VesselTelemetry telemetry, ControlInputs controls, double depthAlarmThresholdMetres)
		{
			if (Threshold.Count != Count)
			{
				throw new ArgumentException("Earlier samples have no depth alarm threshold; give it for every sample or none.");
			}

			if (double.IsNaN(depthAlarmThresholdMetres) || double.IsInfinity(depthAlarmThresholdMetres) || depthAlarmThresholdMetres < 0.0)
			{
				throw new ArgumentException($"Depth alarm threshold {depthAlarmThresholdMetres} m is not a finite value of 0 or more.");
			}

			Append(state, telemetry, controls);
			// Normalizes -0.0 so the writer never prints "-0.000" for a threshold.
			Threshold.Add(depthAlarmThresholdMetres == 0.0 ? 0.0 : depthAlarmThresholdMetres);
		}

		public void Add(TelemetrySample sample) => Add(sample.State, sample.Telemetry, sample.Controls);

		public TelemetryRecording Build()
		{
			if (Count == 0)
			{
				throw new InvalidOperationException("A recording needs at least one sample.");
			}

			return new TelemetryRecording(_label, _header.ToArray(), this);
		}

		private void Append(VesselState state, VesselTelemetry telemetry, ControlInputs controls)
		{
			if (Count > 0 && !(state.Time > LastTime))
			{
				throw new ArgumentException($"Sample time {state.Time} does not follow {LastTime}; times must strictly increase.");
			}

			TimeColumn.Add(state.Time);
			North.Add(state.North);
			East.Add(state.East);
			Heading.Add(state.Heading);
			Surge.Add(state.Surge);
			Sway.Add(state.Sway);
			YawRate.Add(state.YawRate);
			Sog.Add(telemetry.SpeedOverGround);
			Cog.Add(telemetry.CourseOverGround);
			Stw.Add(telemetry.SpeedThroughWater);
			PortEngine.Add(telemetry.Port.EngineSpeed);
			StarboardEngine.Add(telemetry.Starboard.EngineSpeed);
			PortShaft.Add(telemetry.Port.ShaftSpeed);
			StarboardShaft.Add(telemetry.Starboard.ShaftSpeed);
			PortGear.Add(telemetry.Port.Gear);
			StarboardGear.Add(telemetry.Starboard.Gear);
			PortRudder.Add(telemetry.PortRudderAngle);
			StarboardRudder.Add(telemetry.StarboardRudderAngle);
			Depth.Add(telemetry.DepthUnderKeel);
			Contact.Add(telemetry.ContactForce);
			PortLever.Add(controls.PortLever);
			StarboardLever.Add(controls.StarboardLever);
			Helm.Add(controls.Helm);
		}
	}
}
