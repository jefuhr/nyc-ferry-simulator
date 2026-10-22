using System;
using System.Collections.Generic;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// Everything the simulation knows about one vessel class, loaded from <c>vessels/*.json</c>.
	/// This part holds identity and published particulars. Model coefficients (hull, propeller,
	/// rudder, control-model sections) are added as other parts of this partial class by the
	/// dynamics code, so the contract stays small while the model grows.
	/// </summary>
	public sealed partial class VesselParameters
	{
		public const int CurrentSchemaVersion = 1;

		public int SchemaVersion { get; set; }

		/// <summary>Stable identifier, for example <c>t-class</c>.</summary>
		public string Id { get; set; }

		public string DisplayName { get; set; }

		public string Notes { get; set; }

		/// <summary>Citation keys used by <see cref="ParameterValue.Source"/>, mapped to URLs or references.</summary>
		public Dictionary<string, string> Sources { get; set; } = new Dictionary<string, string>();

		public VesselParticulars Particulars { get; set; }

		public VesselMachinery Machinery { get; set; }

		public VesselPerformance Performance { get; set; }

		/// <summary>Returns every problem found; an empty list means the parameters are well formed.</summary>
		public IReadOnlyList<string> Validate() => VesselParametersValidator.Validate(this);
	}

	/// <summary>Hull dimensions and capacities.</summary>
	public sealed class VesselParticulars
	{
		public ParameterValue LengthOverall { get; set; }

		public ParameterValue LengthWaterline { get; set; }

		public ParameterValue Beam { get; set; }

		/// <summary>Moulded depth, keel to main deck.</summary>
		public ParameterValue Depth { get; set; }

		/// <summary>Hull draft at the operating condition, used for hydrodynamics.</summary>
		public ParameterValue Draft { get; set; }

		/// <summary>Deepest point including propellers and appendages, used for grounding and depth under keel.</summary>
		public ParameterValue MaxDraft { get; set; }

		public ParameterValue Freeboard { get; set; }

		public ParameterValue Displacement { get; set; }

		public ParameterValue Passengers { get; set; }

		public ParameterValue Crew { get; set; }
	}

	/// <summary>Published machinery description. The dynamic model's coefficients live elsewhere.</summary>
	public sealed class VesselMachinery
	{
		public ParameterValue EngineCount { get; set; }

		public ParameterText EngineModel { get; set; }

		public ParameterValue RatedPowerPerEngine { get; set; }

		public ParameterValue RatedEngineSpeed { get; set; }

		public ParameterText GearboxModel { get; set; }

		public ParameterValue GearRatio { get; set; }

		public ParameterValue PropellerCount { get; set; }

		public ParameterValue PropellerBlades { get; set; }

		public ParameterValue PropellerDiameter { get; set; }

		public ParameterText PropellerMaker { get; set; }

		public ParameterText PropellerMaterial { get; set; }
	}

	public sealed class VesselPerformance
	{
		public ParameterValue ServiceSpeed { get; set; }

		public ParameterValue MaxSpeed { get; set; }
	}

	/// <summary>
	/// A number with provenance. <see cref="Value"/> is always in the SI unit named by
	/// <see cref="Unit"/>; the original figure goes in <see cref="AsPublished"/>.
	/// </summary>
	public sealed class ParameterValue
	{
		public double Value { get; set; }

		/// <summary>One of <see cref="ParameterUnits.Allowed"/>.</summary>
		public string Unit { get; set; }

		/// <summary>A key into <see cref="VesselParameters.Sources"/>, or free text for estimates.</summary>
		public string Source { get; set; }

		/// <summary>One of the <see cref="ParameterConfidence"/> values.</summary>
		public string Confidence { get; set; }

		/// <summary>The figure exactly as the source gave it, for example <c>85'4"</c>.</summary>
		public string AsPublished { get; set; }

		public string Note { get; set; }
	}

	/// <summary>A text fact with provenance, for example an engine model.</summary>
	public sealed class ParameterText
	{
		public string Value { get; set; }

		public string Source { get; set; }

		public string Confidence { get; set; }

		public string Note { get; set; }
	}

	/// <summary>How much to trust a value. Stored as lowercase strings in JSON.</summary>
	public static class ParameterConfidence
	{
		/// <summary>From a builder, designer, operator or trade-press source.</summary>
		public const string Published = "published";

		/// <summary>Fitted from AIS tracks of the real vessels.</summary>
		public const string AisDerived = "ais-derived";

		/// <summary>From a captain or crew member's account or measurement.</summary>
		public const string CrewReported = "crew-reported";

		/// <summary>Engineering estimate; the source field states the basis.</summary>
		public const string Estimated = "estimated";

		private static readonly string[] Values = { Published, AisDerived, CrewReported, Estimated };

		public static IReadOnlyList<string> All => Values;

		public static bool IsValid(string confidence) => Array.IndexOf(Values, confidence) >= 0;

		/// <summary>True when the confidence level requires a citation key rather than free text.</summary>
		public static bool RequiresCitation(string confidence) => confidence != Estimated;
	}

	/// <summary>The only units allowed in parameter files: SI plus counts and dimensionless ratios.</summary>
	public static class ParameterUnits
	{
		private static readonly string[] Values =
		{
			"m", "m2", "m3", "kg", "kg*m2", "s", "W", "N", "N*m",
			"m/s", "m/s2", "rad", "rad/s", "rad/s2", "1", "count",
		};

		public static IReadOnlyList<string> Allowed => Values;

		public static bool IsAllowed(string unit) => Array.IndexOf(Values, unit) >= 0;
	}
}
