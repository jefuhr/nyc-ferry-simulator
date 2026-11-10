using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Instruments;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FerrySim.UI.Displays
{
	/// <summary>A skin that could not be loaded. The message names the element and the name that is wrong.</summary>
	public sealed class DisplaySkinException : Exception
	{
		public DisplaySkinException(string message, int elementNumber = 0)
			: base(message)
		{
			ElementNumber = elementNumber;
		}

		/// <summary>The element at fault, counting from 1; 0 when the fault is in the skin as a whole.</summary>
		public int ElementNumber { get; }
	}

	/// <summary>
	/// One console display's look, loaded from JSON (contract C6, docs/contracts/display-skin-v1.schema.json). Loading
	/// is all or nothing: a skin that names an unknown channel, alarm, element type, unit, page or colour, or gives a
	/// property that does not apply to its element, is rejected whole with a message naming the element. Validation
	/// follows the schema and adds the checks a schema cannot make: names against the instrument model, units against
	/// the channel's quantity, rectangles inside the reference size, bands inside their element's range.
	/// </summary>
	public sealed class DisplaySkin
	{
		public const int SchemaVersion = 1;

		private static readonly string[] Displays = { "engine", "alarm-panel", "radar", "plotter" };
		private static readonly string[] TopLevel = { "version", "display", "id", "reference", "refreshHz", "viewport", "pages", "elements" };
		private static readonly string[] TypeNames = { "dial", "bar", "value", "lamp", "text", "softkey", "frame", "list" };
		private static readonly string[] ElementProperties = { "type", "rect", "page", "channel", "alarm", "range", "sweep", "unit", "decimals", "text", "target", "colour", "format", "group", "action", "bands" };
		private static readonly string[] Common = { "type", "rect", "page", "text", "colour" };

		private DisplaySkin()
		{
		}

		/// <summary>engine, alarm-panel, radar or plotter.</summary>
		public string Display { get; private set; }

		/// <summary>Unique among skins for the same display, for example generic-a.</summary>
		public string Id { get; private set; }

		/// <summary>[width, height] in reference pixels: the size the rectangles are written against.</summary>
		public Vector2 Reference { get; private set; }

		/// <summary>How often values redraw. Lamps follow their flash rhythm at every refresh regardless.</summary>
		public double RefreshHz { get; private set; }

		/// <summary>Radar and plotter only: where the live picture goes.</summary>
		public Rect? Viewport { get; private set; }

		/// <summary>Page names, the first shown at start; empty for a single page.</summary>
		public IReadOnlyList<string> Pages { get; private set; }

		public IReadOnlyList<SkinElement> Elements { get; private set; }

		/// <summary>True when any element names <c>{side}</c>: the skin is per engine and a view needs a side.</summary>
		public bool UsesSide { get; private set; }

		/// <summary>Loads a skin, or returns false with the reason.</summary>
		/// <param name="alarmIds">The alarm ids the vessel defines (<see cref="DisplaySkinAlarms"/>); a lamp naming any other is rejected.</param>
		public static bool TryParse(string json, IReadOnlyCollection<string> alarmIds, out DisplaySkin skin, out string error)
		{
			try
			{
				skin = Parse(json, alarmIds);
				error = null;
				return true;
			}
			catch (DisplaySkinException exception)
			{
				skin = null;
				error = exception.Message;
				return false;
			}
		}

		/// <summary>Loads a skin or throws <see cref="DisplaySkinException"/>.</summary>
		public static DisplaySkin Parse(string json, IReadOnlyCollection<string> alarmIds)
		{
			if (alarmIds == null)
			{
				throw new ArgumentNullException(nameof(alarmIds));
			}

			var root = ReadJson(json);
			foreach (var property in root.Properties())
			{
				if (Array.IndexOf(TopLevel, property.Name) < 0)
				{
					throw new DisplaySkinException("skin: unknown property '" + property.Name + "'");
				}
			}

			var skin = new DisplaySkin();
			var version = Required(root, "version", "skin");
			if (version.Type != JTokenType.Integer || (long)version != SchemaVersion)
			{
				throw new DisplaySkinException("skin: version must be " + SchemaVersion + ", not " + version.ToString(Formatting.None));
			}

			skin.Display = String(Required(root, "display", "skin"), "skin: display");
			if (Array.IndexOf(Displays, skin.Display) < 0)
			{
				throw new DisplaySkinException("skin: unknown display '" + skin.Display + "' (engine, alarm-panel, radar or plotter)");
			}

			skin.Id = String(Required(root, "id", "skin"), "skin: id");
			if (skin.Id.Length == 0)
			{
				throw new DisplaySkinException("skin: id must not be empty");
			}

			var reference = Numbers(Required(root, "reference", "skin"), 2, "skin: reference must be [width, height]");
			if (!(reference[0] > 0 && reference[1] > 0))
			{
				throw new DisplaySkinException("skin: reference width and height must be positive");
			}

			skin.Reference = new Vector2((float)reference[0], (float)reference[1]);
			var refresh = Number(Required(root, "refreshHz", "skin"), "skin: refreshHz");
			if (!(refresh > 0 && refresh <= 120))
			{
				throw new DisplaySkinException("skin: refreshHz must be above 0 and at most 120, not " + refresh);
			}

			skin.RefreshHz = refresh;
			if (root.TryGetValue("viewport", out var viewport))
			{
				var v = Numbers(viewport, 4, "skin: viewport must be [x, y, width, height]");
				skin.Viewport = new Rect((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
			}

			skin.Pages = ReadPages(root);
			var elements = Required(root, "elements", "skin");
			if (elements.Type != JTokenType.Array)
			{
				throw new DisplaySkinException("skin: elements must be an array");
			}

			var list = new List<SkinElement>();
			var number = 0;
			foreach (var token in (JArray)elements)
			{
				number++;
				var element = ReadElement(token, number, skin, alarmIds);
				list.Add(element);
				skin.UsesSide |= element.UsesSide;
			}

			skin.Elements = list;
			return skin;
		}

		private static JObject ReadJson(string json)
		{
			if (string.IsNullOrWhiteSpace(json))
			{
				throw new DisplaySkinException("skin is empty, not JSON");
			}

			try
			{
				using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
				{
					var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Ignore });
					while (reader.Read())
					{
						if (reader.TokenType != JsonToken.Comment)
						{
							throw new DisplaySkinException("skin is not valid JSON: text after the end of the skin");
						}
					}

					if (!(token is JObject root))
					{
						throw new DisplaySkinException("skin is not valid JSON: it must be one object");
					}

					return root;
				}
			}
			catch (JsonException exception)
			{
				throw new DisplaySkinException("skin is not valid JSON: " + exception.Message);
			}
		}

		private static IReadOnlyList<string> ReadPages(JObject root)
		{
			var pages = new List<string>();
			if (!root.TryGetValue("pages", out var token))
			{
				return pages;
			}

			if (token.Type != JTokenType.Array)
			{
				throw new DisplaySkinException("skin: pages must be an array of names");
			}

			foreach (var page in (JArray)token)
			{
				var name = String(page, "skin: pages");
				if (name.Length == 0)
				{
					throw new DisplaySkinException("skin: pages must not contain an empty name");
				}

				if (pages.Contains(name))
				{
					throw new DisplaySkinException("skin: pages names '" + name + "' twice");
				}

				pages.Add(name);
			}

			return pages;
		}

		private static SkinElement ReadElement(JToken token, int number, DisplaySkin skin, IReadOnlyCollection<string> alarmIds)
		{
			var where = "element " + number;
			if (!(token is JObject json))
			{
				throw new DisplaySkinException(where + ": must be an object", number);
			}

			var typeName = String(Required(json, "type", where, number), where + ": type", number);
			var typeIndex = Array.IndexOf(TypeNames, typeName);
			if (typeIndex < 0)
			{
				throw new DisplaySkinException(where + ": unknown element type '" + typeName + "' (" + string.Join(", ", TypeNames) + ")", number);
			}

			var element = new SkinElement(number) { Type = (SkinElementType)typeIndex };
			if (json.TryGetValue("channel", out var channelToken) && channelToken.Type == JTokenType.String)
			{
				element.ChannelName = (string)channelToken;
			}
			else if (json.TryGetValue("alarm", out var alarmToken) && alarmToken.Type == JTokenType.String)
			{
				element.AlarmId = (string)alarmToken;
			}

			where = element.Describe();
			foreach (var property in json.Properties())
			{
				if (Array.IndexOf(ElementProperties, property.Name) < 0)
				{
					throw new DisplaySkinException(where + ": unknown property '" + property.Name + "'", number);
				}

				if (!Applies(element.Type, property.Name))
				{
					throw new DisplaySkinException(where + ": '" + property.Name + "' does not apply to a " + typeName, number);
				}
			}

			var rect = Numbers(Required(json, "rect", where, number), 4, where + ": rect must be [x, y, width, height]", number);
			element.Rect = new Rect((float)rect[0], (float)rect[1], (float)rect[2], (float)rect[3]);
			const double slack = 0.5;
			if (!(rect[2] > 0 && rect[3] > 0))
			{
				throw new DisplaySkinException(where + ": rect width and height must be positive", number);
			}

			if (rect[0] < -slack || rect[1] < -slack || rect[0] + rect[2] > skin.Reference.x + slack || rect[1] + rect[3] > skin.Reference.y + slack)
			{
				throw new DisplaySkinException(where + ": rect " + json["rect"].ToString(Formatting.None) + " lies outside the reference " + skin.Reference.x + " × " + skin.Reference.y, number);
			}

			if (json.TryGetValue("page", out var page))
			{
				element.Page = PageName(page, skin, where + ": page", number);
			}

			if (json.TryGetValue("text", out var text))
			{
				element.Text = String(text, where + ": text", number);
			}

			if (json.TryGetValue("colour", out var colourToken))
			{
				element.Colour = Colour(colourToken, where + ": colour", number);
			}

			ReadChannel(json, element, where, number);
			ReadAlarm(json, element, where, number, alarmIds);
			ReadScale(json, element, where, number);
			ReadValueFormat(json, element, where, number);
			ReadSoftkey(json, element, skin, where, number);
			if (json.TryGetValue("group", out var group))
			{
				var name = String(group, where + ": group", number);
				if (!Enum.TryParse(name, false, out AlarmGroup parsed) || !Enum.IsDefined(typeof(AlarmGroup), parsed) || name != parsed.ToString())
				{
					throw new DisplaySkinException(where + ": unknown alarm group '" + name + "' (" + string.Join(", ", Enum.GetNames(typeof(AlarmGroup))) + ")", number);
				}

				element.Group = parsed;
			}

			return element;
		}

		private static bool Applies(SkinElementType type, string property)
		{
			if (Array.IndexOf(Common, property) >= 0)
			{
				return true;
			}

			switch (type)
			{
				case SkinElementType.Dial: return property == "channel" || property == "range" || property == "sweep" || property == "unit" || property == "decimals" || property == "bands";
				case SkinElementType.Bar: return property == "channel" || property == "range" || property == "unit" || property == "decimals" || property == "bands";
				case SkinElementType.Value: return property == "channel" || property == "unit" || property == "decimals" || property == "format";
				case SkinElementType.Lamp: return property == "alarm";
				case SkinElementType.Softkey: return property == "target" || property == "action";
				case SkinElementType.List: return property == "group";
				default: return false;
			}
		}

		private static void ReadChannel(JObject json, SkinElement element, string where, int number)
		{
			var needs = element.Type == SkinElementType.Dial || element.Type == SkinElementType.Bar || element.Type == SkinElementType.Value;
			if (!json.TryGetValue("channel", out var token))
			{
				if (needs)
				{
					throw new DisplaySkinException(where + ": a " + SkinElement.TypeName(element.Type) + " needs a channel", number);
				}

				return;
			}

			var name = String(token, where + ": channel", number);
			element.ChannelName = name;
			foreach (var side in SidesFor(name))
			{
				var resolved = SkinElement.Substitute(name, side, where);
				if (!ChannelNames.TryParse(resolved, out _))
				{
					var detail = resolved == name ? "" : " (as '" + resolved + "')";
					throw new DisplaySkinException(where + ": unknown channel '" + name + "'" + detail, number);
				}
			}

			if (json.TryGetValue("unit", out var unitToken))
			{
				var unitName = String(unitToken, where + ": unit", number);
				if (!SkinUnits.TryParse(unitName, out var unit))
				{
					throw new DisplaySkinException(where + ": unknown unit '" + unitName + "'", number);
				}

				foreach (var side in SidesFor(name))
				{
					ChannelNames.TryParse(SkinElement.Substitute(name, side, where), out var channel);
					if (!SkinUnits.Fits(unit, channel))
					{
						throw new DisplaySkinException(where + ": unit '" + unitName + "' does not fit channel '" + name + "', which measures " + SkinUnits.Quantity(channel), number);
					}
				}

				element.Unit = unit;
			}
		}

		private static void ReadAlarm(JObject json, SkinElement element, string where, int number, IReadOnlyCollection<string> alarmIds)
		{
			if (!json.TryGetValue("alarm", out var token))
			{
				if (element.Type == SkinElementType.Lamp)
				{
					throw new DisplaySkinException(where + ": a lamp needs an alarm", number);
				}

				return;
			}

			var id = String(token, where + ": alarm", number);
			element.AlarmId = id;
			foreach (var side in SidesFor(id))
			{
				var resolved = SkinElement.Substitute(id, side, where);
				if (!Contains(alarmIds, resolved))
				{
					var detail = resolved == id ? "" : " (no alarm '" + resolved + "')";
					throw new DisplaySkinException(where + ": unknown alarm '" + id + "'" + detail + "; the vessel defines " + string.Join(", ", alarmIds), number);
				}
			}
		}

		private static void ReadScale(JObject json, SkinElement element, string where, int number)
		{
			var needsRange = element.Type == SkinElementType.Dial || element.Type == SkinElementType.Bar;
			if (json.TryGetValue("range", out var rangeToken))
			{
				var range = Numbers(rangeToken, 2, where + ": range must be [low, high]", number);
				if (!(range[0] < range[1]))
				{
					throw new DisplaySkinException(where + ": range must be [low, high] with low below high, not " + rangeToken.ToString(Formatting.None), number);
				}

				element.HasRange = true;
				element.Low = range[0];
				element.High = range[1];
			}
			else if (needsRange)
			{
				throw new DisplaySkinException(where + ": a " + SkinElement.TypeName(element.Type) + " needs a range", number);
			}

			if (json.TryGetValue("sweep", out var sweepToken))
			{
				var sweep = Numbers(sweepToken, 2, where + ": sweep must be [start, end] in degrees", number);
				if (sweep[0] == sweep[1] || Math.Abs(sweep[0]) > 360 || Math.Abs(sweep[1]) > 360)
				{
					throw new DisplaySkinException(where + ": sweep must be two different angles within ±360°", number);
				}

				element.SweepStart = (float)sweep[0];
				element.SweepEnd = (float)sweep[1];
			}

			if (json.TryGetValue("decimals", out var decimalsToken))
			{
				var decimals = Number(decimalsToken, where + ": decimals", number);
				if (decimals != Math.Floor(decimals) || decimals < 0 || decimals > SkinValueText.MaximumDecimals)
				{
					throw new DisplaySkinException(where + ": decimals must be a whole number from 0 to " + SkinValueText.MaximumDecimals + ", not " + decimalsToken.ToString(Formatting.None), number);
				}

				element.Decimals = (int)decimals;
			}

			if (json.TryGetValue("bands", out var bandsToken))
			{
				if (bandsToken.Type != JTokenType.Array)
				{
					throw new DisplaySkinException(where + ": bands must be an array", number);
				}

				var bands = new List<SkinBand>();
				foreach (var bandToken in (JArray)bandsToken)
				{
					var label = where + ": band " + (bands.Count + 1);
					if (!(bandToken is JObject band))
					{
						throw new DisplaySkinException(label + " must be an object with range and colour", number);
					}

					foreach (var property in band.Properties())
					{
						if (property.Name != "range" && property.Name != "colour")
						{
							throw new DisplaySkinException(label + ": unknown property '" + property.Name + "'", number);
						}
					}

					var range = Numbers(Required(band, "range", label, number), 2, label + ": range must be [low, high]", number);
					if (!(range[0] < range[1]))
					{
						throw new DisplaySkinException(label + ": range must have low below high", number);
					}

					if (range[0] < element.Low || range[1] > element.High)
					{
						throw new DisplaySkinException(label + ": range " + band["range"].ToString(Formatting.None) + " lies outside the element's range [" + element.Low + ", " + element.High + "]", number);
					}

					var colour = Colour(Required(band, "colour", label, number), label + ": colour", number);
					bands.Add(new SkinBand(range[0], range[1], colour));
				}

				element.Bands = bands;
			}
		}

		private static void ReadValueFormat(JObject json, SkinElement element, string where, int number)
		{
			if (!json.TryGetValue("format", out var token))
			{
				return;
			}

			var format = String(token, where + ": format", number);
			if (format == "number")
			{
				element.Format = SkinValueFormat.Number;
				return;
			}

			if (format != "gear")
			{
				throw new DisplaySkinException(where + ": unknown format '" + format + "' (number or gear)", number);
			}

			foreach (var side in SidesFor(element.ChannelName))
			{
				ChannelNames.TryParse(SkinElement.Substitute(element.ChannelName, side, where), out var channel);
				if (!GearWords.IsShiftPhase(channel))
				{
					throw new DisplaySkinException(where + ": format 'gear' needs a shift-phase channel, not '" + element.ChannelName + "'", number);
				}
			}

			element.Format = SkinValueFormat.Gear;
		}

		private static void ReadSoftkey(JObject json, SkinElement element, DisplaySkin skin, string where, int number)
		{
			var hasTarget = json.TryGetValue("target", out var target);
			var hasAction = json.TryGetValue("action", out var action);
			if (hasTarget && hasAction)
			{
				throw new DisplaySkinException(where + ": a softkey has a target or an action, not both", number);
			}

			if (hasTarget)
			{
				element.Target = PageName(target, skin, where + ": target", number);
			}

			if (hasAction)
			{
				var name = String(action, where + ": action", number);
				switch (name)
				{
					case "acknowledge": element.Action = SoftkeyAction.Acknowledge; break;
					case "silence": element.Action = SoftkeyAction.Silence; break;
					case "lamp-test": element.Action = SoftkeyAction.LampTest; break;
					default: throw new DisplaySkinException(where + ": unknown action '" + name + "' (acknowledge, silence or lamp-test)", number);
				}
			}
		}

		private static string PageName(JToken token, DisplaySkin skin, string what, int number)
		{
			var name = String(token, what, number);
			if (!Contains(skin.Pages, name))
			{
				var known = skin.Pages.Count == 0 ? "the skin has no pages" : "pages are " + string.Join(", ", skin.Pages);
				throw new DisplaySkinException(what + ": unknown page '" + name + "'; " + known, number);
			}

			return name;
		}

		private static SkinColour Colour(JToken token, string what, int number)
		{
			var text = String(token, what, number);
			if (!SkinColour.TryParse(text, out var colour))
			{
				throw new DisplaySkinException(what + ": unknown colour '" + text + "' (a palette role such as text-dim, or #RRGGBB)", number);
			}

			return colour;
		}

		/// <summary>The sides a name must resolve for: both when it names {side}, otherwise just as written.</summary>
		private static DisplaySide[] SidesFor(string name) =>
			name != null && name.IndexOf(SkinElement.SideToken, StringComparison.Ordinal) >= 0 ? BothSides : NoSide;

		private static readonly DisplaySide[] BothSides = { DisplaySide.Port, DisplaySide.Starboard };
		private static readonly DisplaySide[] NoSide = { DisplaySide.None };

		private static bool Contains(IEnumerable<string> names, string name)
		{
			foreach (var candidate in names)
			{
				if (string.Equals(candidate, name, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		private static JToken Required(JObject json, string name, string where, int number = 0)
		{
			if (!json.TryGetValue(name, out var token) || token.Type == JTokenType.Null)
			{
				throw new DisplaySkinException(where + ": missing " + name, number);
			}

			return token;
		}

		private static string String(JToken token, string what, int number = 0)
		{
			if (token.Type != JTokenType.String)
			{
				throw new DisplaySkinException(what + " must be a string, not " + token.ToString(Formatting.None), number);
			}

			return (string)token;
		}

		private static double Number(JToken token, string what, int number = 0)
		{
			if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
			{
				throw new DisplaySkinException(what + " must be a number, not " + token.ToString(Formatting.None), number);
			}

			var value = (double)token;
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new DisplaySkinException(what + " must be a finite number", number);
			}

			return value;
		}

		private static double[] Numbers(JToken token, int count, string what, int number = 0)
		{
			if (!(token is JArray array) || array.Count != count)
			{
				throw new DisplaySkinException(what + ", not " + token.ToString(Formatting.None), number);
			}

			var values = new double[count];
			for (var i = 0; i < count; i++)
			{
				values[i] = Number(array[i], what, number);
			}

			return values;
		}
	}
}
