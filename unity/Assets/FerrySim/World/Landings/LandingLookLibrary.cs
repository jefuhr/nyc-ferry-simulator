using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.World.Landings
{
	/// <summary>A Resources asset references the original JSON and shader material, including in players.
	/// The asset prevents a second copy of the data, and keeps the shader from being stripped.</summary>
	public sealed class LandingLookLibrary : ScriptableObject
	{
		[SerializeField] private TextAsset source;
		[SerializeField] private Material surface;
		private string _parsedText;
		private JObject _values;
		public Material Surface => surface;
		public static LandingLookLibrary Load() => Resources.Load<LandingLookLibrary>("LandingLookLibrary")
			?? throw new InvalidOperationException("Missing landing look library resource.");

		public LandingStyle ForStop(string stopId)
		{
			if (source == null) throw new InvalidOperationException("Missing landing-looks.json reference.");
			if (_values == null || _parsedText != source.text)
			{
				var data = JObject.Parse(source.text);
				if ((int?)data["schema_version"] != 1 || (string)data["kind"] != "landing-looks") throw new FormatException("Unknown landing look schema.");
				_values = (JObject)data["values"] ?? throw new FormatException("Missing landing values.");
				_parsedText = source.text;
			}
			var style = new LandingStyle();
			Apply(style, (JObject)_values["defaults"] ?? throw new FormatException("Missing landing defaults."), true);
			if (stopId != null && _values[stopId] is JObject overrides) Apply(style, overrides, false);
			style.Validate();
			return style;
		}

		private static void Apply(LandingStyle s, JObject values, bool requireAll)
		{
			var setters = new Dictionary<string, Action<float>>(StringComparer.Ordinal) {
				["freeboard"] = v => s.Freeboard=v, ["depth"] = v => s.Depth=v,
				["fender_spacing"] = v => s.FenderSpacing=v, ["fender_height"] = v => s.FenderHeight=v, ["fender_width"] = v => s.FenderWidth=v,
				["canopy_length"] = v => s.CanopyLength=v, ["canopy_width"] = v => s.CanopyWidth=v, ["canopy_height"] = v => s.CanopyHeight=v, ["canopy_rise"] = v => s.CanopyRise=v,
				["gangway_length"] = v => s.GangwayLength=v, ["gangway_width"] = v => s.GangwayWidth=v, ["shore_height"] = v => s.ShoreHeight=v,
				["pile_height"] = v => s.PileHeight=v, ["pile_diameter"] = v => s.PileDiameter=v };
			if (requireAll && values.Count != setters.Count) throw new FormatException("Landing defaults must specify every dimension.");
			foreach (var field in values)
			{
				if (!setters.TryGetValue(field.Key, out var set)) throw new FormatException("Unknown landing dimension: " + field.Key);
				var entry = field.Value;
				var method=(string)entry["method"]; var confidence=(string)entry["confidence"];
				if ((string)entry["unit"] != "m" || string.IsNullOrWhiteSpace((string)entry["source"]) ||
					Array.IndexOf(new[] { "published", "simulator-estimate", "video-estimate", "photo-estimate", "drawing-scale", "known-dimension", "crew-confirmed" },method)<0 ||
					Array.IndexOf(new[] { "low", "medium", "high" },confidence)<0 || entry["value"] == null)
					throw new FormatException("Landing value needs units and source evidence: " + field.Key);
				set((float)entry["value"]);
			}
		}
	}
}
