using FerrySim.Core.Contracts;

namespace FerrySim.Core.Instruments
{
	/// <summary>Data-defined annunciator and its simulation condition. Thresholds carry SI provenance.</summary>
	public sealed class AlarmDefinition
	{
		public string Id { get; set; }
		public AlarmGroup Group { get; set; }
		/// <summary>shallow-water, grounding, contact or steering. No fault injection or engine failure conditions.</summary>
		public string Condition { get; set; }
		public ParameterValue Threshold { get; set; }
		/// <summary>Extra time after a full hard-over-to-hard-over transit before steering discrepancy can alarm.</summary>
		public ParameterValue DelaySeconds { get; set; }
		/// <summary>Absent or zero means never repeat.</summary>
		public ParameterValue ReRaiseSeconds { get; set; }
	}
}
