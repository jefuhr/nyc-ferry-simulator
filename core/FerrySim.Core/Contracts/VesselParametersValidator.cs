using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace FerrySim.Core.Contracts
{
	/// <summary>
	/// Checks that every <see cref="ParameterValue"/> and <see cref="ParameterText"/> reachable from a
	/// <see cref="VesselParameters"/> is well formed. Walks properties by reflection so sections added
	/// by other parts of the partial class are covered without changes here.
	/// </summary>
	public static class VesselParametersValidator
	{
		private const int MaxDepth = 8;

		public static IReadOnlyList<string> Validate(VesselParameters parameters)
		{
			var errors = new List<string>();
			if (parameters == null)
			{
				errors.Add("parameters: missing");
				return errors;
			}

			if (parameters.SchemaVersion != VesselParameters.CurrentSchemaVersion)
			{
				errors.Add($"schemaVersion: expected {VesselParameters.CurrentSchemaVersion}, found {parameters.SchemaVersion}");
			}

			if (string.IsNullOrWhiteSpace(parameters.Id))
			{
				errors.Add("id: missing");
			}

			var sources = parameters.Sources ?? new Dictionary<string, string>();
			foreach (var source in sources)
			{
				if (string.IsNullOrWhiteSpace(source.Value))
				{
					errors.Add($"sources.{source.Key}: empty reference");
				}
			}

			Walk(parameters, "", sources, errors, 0);
			return errors;
		}

		private static void Walk(object node, string path, IReadOnlyDictionary<string, string> sources, List<string> errors, int depth)
		{
			if (node == null || depth > MaxDepth)
			{
				return;
			}

			switch (node)
			{
				case ParameterValue value:
					CheckValue(value, path, sources, errors);
					return;
				case ParameterText text:
					CheckText(text, path, sources, errors);
					return;
				case string _:
					return;
				case IDictionary _:
					return;
				case IEnumerable items:
					var index = 0;
					foreach (var item in items)
					{
						Walk(item, $"{path}[{index}]", sources, errors, depth + 1);
						index++;
					}

					return;
			}

			var type = node.GetType();
			if (type.IsPrimitive || type.IsEnum || !IsFerrySimType(type))
			{
				return;
			}

			foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (property.GetIndexParameters().Length > 0 || !property.CanRead)
				{
					continue;
				}

				var childPath = path.Length == 0 ? CamelCase(property.Name) : $"{path}.{CamelCase(property.Name)}";
				Walk(property.GetValue(node), childPath, sources, errors, depth + 1);
			}
		}

		private static void CheckValue(ParameterValue value, string path, IReadOnlyDictionary<string, string> sources, List<string> errors)
		{
			if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
			{
				errors.Add($"{path}.value: must be finite");
			}

			if (!ParameterUnits.IsAllowed(value.Unit))
			{
				errors.Add($"{path}.unit: '{value.Unit}' is not an allowed SI unit ({string.Join(", ", ParameterUnits.Allowed)})");
			}

			if (value.Unit == "count" && Math.Abs(value.Value - Math.Round(value.Value)) > 0.0)
			{
				errors.Add($"{path}.value: counts must be whole numbers");
			}

			CheckProvenance(value.Source, value.Confidence, path, sources, errors);
		}

		private static void CheckText(ParameterText text, string path, IReadOnlyDictionary<string, string> sources, List<string> errors)
		{
			if (string.IsNullOrWhiteSpace(text.Value))
			{
				errors.Add($"{path}.value: missing");
			}

			CheckProvenance(text.Source, text.Confidence, path, sources, errors);
		}

		private static void CheckProvenance(string source, string confidence, string path, IReadOnlyDictionary<string, string> sources, List<string> errors)
		{
			if (!ParameterConfidence.IsValid(confidence))
			{
				errors.Add($"{path}.confidence: '{confidence}' is not one of {string.Join(", ", ParameterConfidence.All)}");
				return;
			}

			if (string.IsNullOrWhiteSpace(source))
			{
				errors.Add($"{path}.source: missing");
			}
			else if (ParameterConfidence.RequiresCitation(confidence) && !sources.ContainsKey(source))
			{
				errors.Add($"{path}.source: '{source}' is not a key in sources");
			}
		}

		private static bool IsFerrySimType(Type type) =>
			type.Namespace != null && type.Namespace.StartsWith("FerrySim", StringComparison.Ordinal);

		private static string CamelCase(string name) =>
			string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
	}
}
