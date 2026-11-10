using System;
using System.IO;
using FerrySim.Core.Instruments;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace FerrySim.Core.Serialization
{
	/// <summary>Strict loader for the separate display curves and alarm definitions.</summary>
	public static class InstrumentParametersJson
	{
		private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
		{
			ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
			MissingMemberHandling = MissingMemberHandling.Error,
			NullValueHandling = NullValueHandling.Ignore,
			FloatParseHandling = FloatParseHandling.Double,
			Converters = { new StringEnumConverter { AllowIntegerValues = false } },
		};

		public static InstrumentParameters Parse(string json)
		{
			var parameters = JsonConvert.DeserializeObject<InstrumentParameters>(json, Settings);
			if (parameters == null) throw new ArgumentException("Instrument JSON is empty.");
			parameters.Validate();
			return parameters;
		}

		public static InstrumentParameters Load(string path) => Parse(File.ReadAllText(path));
	}
}
