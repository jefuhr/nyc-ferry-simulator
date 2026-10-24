using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FerrySim.Slice
{
	/// <summary>One landing's fender as the core's <see cref="ContactSurface"/>, with every value's provenance.</summary>
	public sealed class LandingSurface
	{
		public LandingSurface(string key, ParameterValue stiffness, ParameterValue damping, ParameterValue friction, ParameterValue maxCompression, string note)
		{
			Key = key ?? throw new ArgumentNullException(nameof(key));
			Stiffness = stiffness ?? throw new ArgumentNullException(nameof(stiffness));
			Damping = damping ?? throw new ArgumentNullException(nameof(damping));
			Friction = friction ?? throw new ArgumentNullException(nameof(friction));
			MaxCompression = maxCompression ?? throw new ArgumentNullException(nameof(maxCompression));
			Note = note;
			Surface = new ContactSurface(stiffness.Value, damping.Value, friction.Value, maxCompression.Value);
			var confidence = stiffness.Confidence;
			foreach (var value in new[] { damping, friction, maxCompression })
			{
				if (value.Confidence != confidence)
				{
					confidence = "mixed";
				}
			}

			Confidence = confidence;
		}

		/// <summary><see cref="LandingSurfaceTable.DefaultKey"/>, or the GTFS stop_id of a landing with its own entry.</summary>
		public string Key { get; }

		public ContactSurface Surface { get; }

		/// <summary>N/m per metre of contact length (<c>N/m2</c>).</summary>
		public ParameterValue Stiffness { get; }

		/// <summary>N·s/m per metre of contact length (<c>N*s/m2</c>).</summary>
		public ParameterValue Damping { get; }

		/// <summary>Coulomb friction coefficient (<c>1</c>).</summary>
		public ParameterValue Friction { get; }

		/// <summary>Fender travel before it bottoms out (<c>m</c>).</summary>
		public ParameterValue MaxCompression { get; }

		public string Note { get; }

		/// <summary>The confidence all four values share, or <c>mixed</c>.</summary>
		public string Confidence { get; }
	}

	/// <summary>
	/// The slice's landing fender parameters (<c>Slice/Data/landing-contact-surfaces.json</c>): a default
	/// <see cref="ContactSurface"/> and optional per-landing entries keyed by GTFS stop_id. Every value is a
	/// <see cref="ParameterValue"/> (value, unit, source, confidence) in the unit the contract defines. Unknown
	/// properties are errors, as in vessel files, so a typo never falls back to a default silently.
	/// </summary>
	public sealed class LandingSurfaceTable
	{
		public const int SchemaVersion = 1;

		/// <summary>The entry every landing without its own uses.</summary>
		public const string DefaultKey = "default";

		/// <summary>The shipped file, relative to the Unity project.</summary>
		public const string DefaultAssetPath = "Assets/FerrySim/Slice/Data/landing-contact-surfaces.json";

		public const string StiffnessUnit = "N/m2";
		public const string DampingUnit = "N*s/m2";
		public const string FrictionUnit = "1";
		public const string MaxCompressionUnit = "m";

		private static readonly string[] TopLevel = { "schemaVersion", "description", "sources", DefaultKey, "landings" };
		private static readonly string[] EntryFields = { "stiffness", "damping", "friction", "maxCompression", "note" };
		private static readonly string[] ValueFields = { "value", "unit", "source", "confidence", "asPublished", "note" };

		private LandingSurfaceTable()
		{
		}

		/// <summary>The file the table was read from, for messages.</summary>
		public string File { get; private set; }

		public LandingSurface Default { get; private set; }

		/// <summary>Entries for single landings, by GTFS stop_id.</summary>
		public IReadOnlyDictionary<string, LandingSurface> Landings { get; private set; }

		/// <summary>Citations that non-estimated values refer to.</summary>
		public IReadOnlyDictionary<string, string> Sources { get; private set; }

		/// <summary>The landing's own entry, else the default.</summary>
		public LandingSurface For(string stopId) =>
			stopId != null && Landings.TryGetValue(stopId, out var own) ? own : Default;

		/// <summary>Parses and validates the file's text; <paramref name="file"/> names it in errors.</summary>
		public static LandingSurfaceTable Parse(string json, string file)
		{
			var root = Json.ParseRoot(json, file) as JObject ?? throw new LandingDataException(file, new[] { "expected a JSON object with schemaVersion, sources, default and landings" });
			var problems = new List<string>();
			Json.RejectUnknown(root, TopLevel, string.Empty, problems);
			// Compare the value as read: an integer past Int64 arrives as a BigInteger, and casting it to long throws.
			if (!(root["schemaVersion"] is JValue version) || !(version.Value is long schema) || schema != SchemaVersion)
			{
				problems.Add($"schemaVersion must be {SchemaVersion}");
			}

			var sources = ReadSources(root["sources"], problems);
			LandingSurface fallback = null;
			if (root[DefaultKey] == null)
			{
				problems.Add("default: missing (the fender every landing without its own entry uses)");
			}
			else
			{
				fallback = ReadEntry(DefaultKey, root[DefaultKey], DefaultKey, sources, problems);
			}

			var landings = new Dictionary<string, LandingSurface>(StringComparer.Ordinal);
			var landingsToken = root["landings"];
			if (landingsToken != null && landingsToken.Type != JTokenType.Object)
			{
				problems.Add("landings: must be an object keyed by GTFS stop_id");
			}
			else if (landingsToken != null)
			{
				foreach (var property in ((JObject)landingsToken).Properties())
				{
					var entry = ReadEntry(property.Name, property.Value, "landings." + property.Name, sources, problems);
					if (entry != null)
					{
						landings[property.Name] = entry;
					}
				}
			}

			if (problems.Count > 0)
			{
				throw new LandingDataException(file, problems);
			}

			return new LandingSurfaceTable { File = file, Default = fallback, Landings = landings, Sources = sources };
		}

		public static LandingSurfaceTable Load(string path) => Parse(Json.ReadFile(path), path);

		private static Dictionary<string, string> ReadSources(JToken token, List<string> problems)
		{
			var sources = new Dictionary<string, string>(StringComparer.Ordinal);
			if (token == null)
			{
				return sources;
			}

			if (token.Type != JTokenType.Object)
			{
				problems.Add("sources: must be an object of citation key to citation");
				return sources;
			}

			foreach (var property in ((JObject)token).Properties())
			{
				if (property.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)property.Value))
				{
					problems.Add($"sources.{property.Name}: must be a citation");
				}
				else
				{
					sources[property.Name] = (string)property.Value;
				}
			}

			return sources;
		}

		private static LandingSurface ReadEntry(string key, JToken token, string path, IReadOnlyDictionary<string, string> sources, List<string> problems)
		{
			if (!(token is JObject entry))
			{
				problems.Add($"{path}: must be an object with stiffness, damping, friction and maxCompression");
				return null;
			}

			var before = problems.Count;
			Json.RejectUnknown(entry, EntryFields, path + ".", problems);
			var stiffness = ReadValue(entry, "stiffness", StiffnessUnit, path, sources, problems);
			var damping = ReadValue(entry, "damping", DampingUnit, path, sources, problems);
			var friction = ReadValue(entry, "friction", FrictionUnit, path, sources, problems);
			var maxCompression = ReadValue(entry, "maxCompression", MaxCompressionUnit, path, sources, problems);
			var note = Json.OptionalText(entry, "note", path, problems);
			if (problems.Count > before)
			{
				return null;
			}

			try
			{
				return new LandingSurface(key, stiffness, damping, friction, maxCompression, note);
			}
			catch (ArgumentException exception)
			{
				problems.Add($"{path}: {exception.Message}");
				return null;
			}
		}

		private static ParameterValue ReadValue(JObject entry, string name, string unit, string path, IReadOnlyDictionary<string, string> sources, List<string> problems)
		{
			var where = path + "." + name;
			if (!(entry[name] is JObject token))
			{
				problems.Add($"{where}: missing (an object with value, unit, source and confidence)");
				return null;
			}

			Json.RejectUnknown(token, ValueFields, where + ".", problems);
			var value = new ParameterValue
			{
				Unit = Json.OptionalText(token, "unit", where, problems),
				Source = Json.OptionalText(token, "source", where, problems),
				Confidence = Json.OptionalText(token, "confidence", where, problems),
				AsPublished = Json.OptionalText(token, "asPublished", where, problems),
				Note = Json.OptionalText(token, "note", where, problems),
			};
			if (!Json.TryNumber(token["value"], out var number))
			{
				problems.Add($"{where}.value: must be a finite number");
			}

			value.Value = number;
			if (value.Unit != unit)
			{
				problems.Add($"{where}.unit: '{value.Unit}' must be '{unit}'");
			}

			if (string.IsNullOrWhiteSpace(value.Source))
			{
				problems.Add($"{where}.source: missing");
			}

			if (!ParameterConfidence.IsValid(value.Confidence))
			{
				problems.Add($"{where}.confidence: '{value.Confidence}' is not one of {string.Join(", ", ParameterConfidence.All)}");
			}
			else if (ParameterConfidence.RequiresCitation(value.Confidence) && value.Source != null && !sources.ContainsKey(value.Source))
			{
				problems.Add($"{where}.source: '{value.Source}' is not a key in sources");
			}

			return value;
		}
	}

	/// <summary>Strict JSON reading shared by the landing data files.</summary>
	internal static class Json
	{
		private static readonly JsonLoadSettings Settings = new JsonLoadSettings
		{
			DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
			CommentHandling = CommentHandling.Ignore,
			LineInfoHandling = LineInfoHandling.Load,
		};

		public static string ReadFile(string path)
		{
			if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
			{
				throw new LandingDataException(path, new[] { "file not found" });
			}

			try
			{
				return System.IO.File.ReadAllText(path);
			}
			catch (IOException exception)
			{
				throw new LandingDataException(path, new[] { "unreadable: " + exception.Message }, exception);
			}
		}

		public static JToken ParseRoot(string json, string file)
		{
			if (string.IsNullOrWhiteSpace(json))
			{
				throw new LandingDataException(file, new[] { "empty" });
			}

			try
			{
				using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
				{
					var root = JToken.ReadFrom(reader, Settings);
					if (reader.Read())
					{
						throw new JsonReaderException("additional content after the end of the document");
					}

					return root;
				}
			}
			catch (JsonException exception)
			{
				throw new LandingDataException(file, new[] { "not valid JSON: " + exception.Message }, exception);
			}
		}

		public static void RejectUnknown(JObject token, string[] known, string prefix, List<string> problems)
		{
			foreach (var property in token.Properties())
			{
				if (Array.IndexOf(known, property.Name) < 0)
				{
					problems.Add($"{prefix}{property.Name}: unknown property (expected one of {string.Join(", ", known)})");
				}
			}
		}

		public static string OptionalText(JObject token, string name, string path, List<string> problems)
		{
			var value = token[name];
			if (value == null || value.Type == JTokenType.Null)
			{
				return null;
			}

			if (value.Type != JTokenType.String)
			{
				problems.Add($"{path}.{name}: must be text");
				return null;
			}

			return (string)value;
		}

		public static bool TryNumber(JToken token, out double value)
		{
			value = double.NaN;
			if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
			{
				return false;
			}

			value = (double)token;
			return !double.IsNaN(value) && !double.IsInfinity(value);
		}
	}
}
