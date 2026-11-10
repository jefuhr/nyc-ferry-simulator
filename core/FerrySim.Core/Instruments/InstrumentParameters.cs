using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Instruments
{
	/// <summary>Steady reading = idle + speedRise × normalized speed + loadRise × load, with a first-order lag.</summary>
	public sealed class EngineInstrumentCurve
	{
		public ParameterValue Idle { get; set; }
		public ParameterValue SpeedRise { get; set; }
		public ParameterValue LoadRise { get; set; }
		public ParameterValue TimeConstant { get; set; }
	}

	/// <summary>Separate display parameters, never included in the vessel hash or dynamics state.</summary>
	public sealed class InstrumentParameters
	{
		public int Version { get; set; }
		public string VesselClass { get; set; }
		public Dictionary<string, string> Sources { get; set; } = new Dictionary<string, string>();
		public ParameterValue IdleSpeed { get; set; }
		public ParameterValue RatedSpeed { get; set; }
		public ParameterValue RatedPower { get; set; }
		public ParameterValue BusVoltage { get; set; }
		public ParameterValue LoadTimeConstant { get; set; }
		public EngineInstrumentCurve OilPressure { get; set; }
		public EngineInstrumentCurve CoolantTemperature { get; set; }
		public EngineInstrumentCurve ExhaustTemperature { get; set; }
		public EngineInstrumentCurve BoostPressure { get; set; }
		public EngineInstrumentCurve FuelRate { get; set; }
		public EngineInstrumentCurve GearOilPressure { get; set; }
		public List<AlarmDefinition> Alarms { get; set; }

		/// <summary>Validate at the loading boundary. Instrument-only SI units include K, V and kg/s.</summary>
		public void Validate()
		{
			if (Version != 1) throw new ArgumentException("Unsupported instrument parameter version.");
			if (string.IsNullOrWhiteSpace(VesselClass)) throw new ArgumentException("vesselClass is required.");
			Check(IdleSpeed, "rad/s", true); Check(RatedSpeed, "rad/s", true); Check(RatedPower, "W", true);
			if (RatedSpeed.Value <= IdleSpeed.Value) throw new ArgumentException("Rated speed must exceed idle speed.");
			Check(BusVoltage, "V", true); Check(LoadTimeConstant, "s", true);
			Curve(OilPressure, "N/m2"); Curve(CoolantTemperature, "K"); Curve(ExhaustTemperature, "K");
			Curve(BoostPressure, "N/m2"); Curve(FuelRate, "kg/s"); Curve(GearOilPressure, "N/m2");
			if (Alarms == null || Alarms.Count != 4) throw new ArgumentException("v0.5 requires the four modelled alarm definitions.");
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var conditions = new HashSet<string>(StringComparer.Ordinal);
			foreach (var alarm in Alarms)
			{
				if (alarm == null || string.IsNullOrWhiteSpace(alarm.Id) || !seen.Add(alarm.Id)) throw new ArgumentException("Alarm ids must be unique and nonempty.");
				if (!Enum.IsDefined(typeof(AlarmGroup), alarm.Group)) throw new ArgumentException("Unknown alarm group.");
				if (alarm.Condition != "shallow-water" && alarm.Condition != "grounding" && alarm.Condition != "contact" && alarm.Condition != "steering")
					throw new ArgumentException("Unsupported v0.5 alarm condition: " + alarm.Condition);
				if (!conditions.Add(alarm.Condition)) throw new ArgumentException("Duplicate alarm condition.");
				Check(alarm.Threshold, alarm.Condition == "steering" ? "rad" : alarm.Condition == "contact" ? "N" : "m", false);
				if (alarm.DelaySeconds != null) Check(alarm.DelaySeconds, "s", false);
				if (alarm.ReRaiseSeconds != null) Check(alarm.ReRaiseSeconds, "s", false);
				if (alarm.Condition == "grounding" && alarm.Threshold.Value != 0) throw new ArgumentException("Grounding threshold is zero under-keel clearance.");
			}
		}

		private void Curve(EngineInstrumentCurve curve, string unit)
		{
			if (curve == null) throw new ArgumentException("Missing engine instrument curve.");
			Check(curve.Idle, unit, false); Check(curve.SpeedRise, unit, false); Check(curve.LoadRise, unit, false); Check(curve.TimeConstant, "s", true);
		}

		private void Check(ParameterValue p, string unit, bool positive)
		{
			if (p == null || double.IsNaN(p.Value) || double.IsInfinity(p.Value) || (positive ? p.Value <= 0 : p.Value < 0) || p.Unit != unit)
				throw new ArgumentException("Invalid instrument parameter; expected " + unit + (positive ? " > 0." : " >= 0."));
			if (!ParameterConfidence.IsValid(p.Confidence) || string.IsNullOrWhiteSpace(p.Source)) throw new ArgumentException("Instrument parameter provenance is required.");
			if (ParameterConfidence.RequiresCitation(p.Confidence) && (Sources == null || !Sources.ContainsKey(p.Source))) throw new ArgumentException("Unknown instrument source: " + p.Source);
		}
	}
}
