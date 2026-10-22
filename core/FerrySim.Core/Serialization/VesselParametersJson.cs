using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace FerrySim.Core.Serialization
{
	/// <summary>Thrown when a vessel parameter file is malformed or fails validation.</summary>
	public sealed class VesselParametersException : Exception
	{
		public VesselParametersException(string message, IReadOnlyList<string> errors)
			: base(errors.Count == 0 ? message : message + Environment.NewLine + string.Join(Environment.NewLine, errors))
		{
			Errors = errors;
		}

		public IReadOnlyList<string> Errors { get; }
	}

	/// <summary>
	/// Reads and writes <c>vessels/*.json</c>. Property names are camelCase. Unknown properties are
	/// errors, so a typo in a parameter file fails loudly instead of silently using a default.
	/// </summary>
	public static class VesselParametersJson
	{
		private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
		{
			ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
			MissingMemberHandling = MissingMemberHandling.Error,
			NullValueHandling = NullValueHandling.Ignore,
			FloatParseHandling = FloatParseHandling.Double,
			Formatting = Formatting.Indented,
		};

		/// <summary>Parses and validates a parameter file's contents.</summary>
		public static VesselParameters Parse(string json)
		{
			VesselParameters parameters;
			try
			{
				parameters = JsonConvert.DeserializeObject<VesselParameters>(json, Settings);
			}
			catch (JsonException exception)
			{
				throw new VesselParametersException("Vessel parameter JSON is malformed: " + exception.Message, Array.Empty<string>());
			}

			if (parameters == null)
			{
				throw new VesselParametersException("Vessel parameter JSON is empty.", Array.Empty<string>());
			}

			var errors = parameters.Validate();
			if (errors.Count > 0)
			{
				throw new VesselParametersException($"Vessel parameters '{parameters.Id}' failed validation:", errors);
			}

			return parameters;
		}

		/// <summary>Loads and validates a parameter file from disk.</summary>
		public static VesselParameters Load(string path) => Parse(File.ReadAllText(path));

		public static string Serialize(VesselParameters parameters) => JsonConvert.SerializeObject(parameters, Settings);
	}
}
