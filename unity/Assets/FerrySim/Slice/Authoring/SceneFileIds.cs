using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FerrySim.Slice.Authoring
{
	/// <summary>
	/// Makes a saved scene's bytes reproducible. Unity gives new objects random file ids; this replaces the id of
	/// every object not in <c>keep</c> with one hashed from the object's class, its GameObject's name and (for
	/// scripts) the script reference, rewrites every reference to it, and sorts the objects by id. Objects in
	/// <c>keep</c> (the world scene's, already reproducible) and Unity's small fixed ids are left alone, so prefab
	/// instances copied from the world keep the ids their stripped objects derive from. New objects need unique
	/// GameObject names, or the hash falls back to their order.
	/// </summary>
	internal static class SceneFileIds
	{
		private static readonly Regex BlockHeader = new Regex(@"^--- !u!(\d+) &(-?\d+)(.*)$", RegexOptions.Multiline);
		private static readonly Regex FileIdReference = new Regex(@"fileID: (-?\d+)");

		/// <summary>The object ids a scene file defines.</summary>
		public static HashSet<string> Ids(string yaml) =>
			new HashSet<string>(BlockHeader.Matches(yaml).Cast<Match>().Select(match => match.Groups[2].Value), StringComparer.Ordinal);

		public static string Canonicalize(string yaml, ISet<string> keep)
		{
			var headers = BlockHeader.Matches(yaml).Cast<Match>().ToList();
			if (headers.Count == 0)
			{
				return yaml;
			}

			var preamble = yaml.Substring(0, headers[0].Index);
			var blocks = new List<(string classId, string id, string text)>();
			for (var i = 0; i < headers.Count; i++)
			{
				var end = i + 1 < headers.Count ? headers[i + 1].Index : yaml.Length;
				blocks.Add((headers[i].Groups[1].Value, headers[i].Groups[2].Value, yaml.Substring(headers[i].Index, end - headers[i].Index)));
			}

			var gameObjectNames = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var block in blocks.Where(b => b.classId == "1"))
			{
				gameObjectNames[block.id] = Field(block.text, "m_Name") ?? string.Empty;
			}

			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			var used = new HashSet<string>(StringComparer.Ordinal);
			foreach (var block in blocks)
			{
				if (keep.Contains(block.id) || (long.TryParse(block.id, out var small) && small > 0 && small < 1000))
				{
					map[block.id] = block.id;
					used.Add(block.id);
				}
			}

			// Hash new objects in a stable order (by key), so collisions resolve the same way every build.
			var fresh = blocks.Where(b => !map.ContainsKey(b.id))
				.Select(b =>
				{
					var owner = b.classId == "1" ? b.id : Reference(b.text, "m_GameObject");
					var ownerName = owner != null && gameObjectNames.TryGetValue(owner, out var name) ? name : string.Empty;
					return (b.id, key: b.classId + "|" + ownerName + "|" + (Field(b.text, "m_Script") ?? string.Empty));
				})
				.OrderBy(b => b.key, StringComparer.Ordinal)
				.ToList();
			foreach (var (id, key) in fresh)
			{
				var salted = key;
				var stable = StableId(salted);
				while (!used.Add(stable))
				{
					salted += "+";
					stable = StableId(salted);
				}

				map[id] = stable;
			}

			var rewritten = blocks
				.Select(block =>
				{
					var text = FileIdReference.Replace(block.text, match =>
						map.TryGetValue(match.Groups[1].Value, out var replacement) ? "fileID: " + replacement : match.Value);
					text = BlockHeader.Replace(text, match => $"--- !u!{match.Groups[1].Value} &{map[match.Groups[2].Value]}{match.Groups[3].Value}", 1);
					return (id: long.Parse(map[block.id], CultureInfo.InvariantCulture), text);
				})
				.OrderBy(block => block.id)
				.Select(block => block.text);
			return preamble + string.Concat(rewritten);
		}

		private static string Field(string block, string field)
		{
			var match = Regex.Match(block, "^\\s*" + Regex.Escape(field) + ": (.*)$", RegexOptions.Multiline);
			return match.Success ? match.Groups[1].Value.Trim() : null;
		}

		private static string Reference(string block, string field)
		{
			var value = Field(block, field);
			if (value == null)
			{
				return null;
			}

			var match = FileIdReference.Match(value);
			return match.Success ? match.Groups[1].Value : null;
		}

		/// <summary>FNV-1a over the key, folded into a positive id well away from Unity's small fixed ones.</summary>
		private static string StableId(string key)
		{
			unchecked
			{
				var hash = 14695981039346656037UL;
				foreach (var c in Encoding.UTF8.GetBytes(key))
				{
					hash = (hash ^ c) * 1099511628211UL;
				}

				return (1000000000L + (long)(hash % 8000000000000000000UL)).ToString(CultureInfo.InvariantCulture);
			}
		}
	}
}
