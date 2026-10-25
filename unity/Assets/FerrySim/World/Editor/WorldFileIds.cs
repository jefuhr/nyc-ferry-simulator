using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace FerrySim.World.EditorTools
{
	/// <summary>
	/// Keeps existing ids by hierarchy path, component class and script reference. New identities get
	/// FNV-1a ids from the same keys. Rewrites only local references and sorts blocks, so an identical
	/// rebuild has identical bytes even when Unity created its objects in a different order.
	/// Repeated child names (Hull, Label, Backing) are distinguished by their complete hierarchy.
	/// </summary>
	public static class WorldFileIds
	{
		private static readonly Regex Header = new Regex(@"^--- !u!(\d+) &(-?\d+)([^\r\n]*)", RegexOptions.Multiline);
		private static readonly Regex Reference = new Regex(@"\{fileID: (-?\d+)([^}]*)\}");

		public static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

		public static void Stabilize(string path, string previous)
		{
			var yaml = File.ReadAllText(path);
			var stable = Canonicalize(yaml, previous, path.EndsWith(".prefab", StringComparison.Ordinal));
			if (stable == yaml) return;
			File.WriteAllText(path, stable, new UTF8Encoding(false));
			AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
		}

		public static string Canonicalize(string yaml, string previous, bool groupComponents = false)
		{
			var current = new Document(yaml);
			if (current.Blocks.Count == 0) return yaml;
			var old = new Document(previous);
			var oldIds = old.Blocks.ToDictionary(block => old.Key(block.Id), block => block.Id, StringComparer.Ordinal);
			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			var used = new HashSet<string>(StringComparer.Ordinal);
			foreach (var block in current.Blocks)
			{
				var number = long.Parse(block.Id, CultureInfo.InvariantCulture);
				if ((number > 0 && number < 1000) || block.ClassId == "1660057539")
					map.Add(block.Id, block.Id);
				else if (oldIds.TryGetValue(current.Key(block.Id), out var existing))
					map.Add(block.Id, existing);
				if (map.TryGetValue(block.Id, out var kept) && !used.Add(kept))
					throw new InvalidOperationException("Duplicate preserved World file id: " + kept);
			}

			foreach (var block in current.Blocks.Where(block => !map.ContainsKey(block.Id)).OrderBy(block => current.Key(block.Id), StringComparer.Ordinal))
			{
				var key = current.Key(block.Id);
				var stable = Hash(key);
				while (!used.Add(stable)) stable = Hash(key += "+");
				map.Add(block.Id, stable);
			}

			return current.Preamble + string.Concat(current.Blocks.Select(block =>
			{
				var text = Reference.Replace(block.Text, match =>
					// A matching number in another asset is not this document's object.
					match.Groups[2].Value.Contains("guid:") || !map.TryGetValue(match.Groups[1].Value, out var id)
						? match.Value : "{fileID: " + id + match.Groups[2].Value + "}");
				text = Header.Replace(text, match => "--- !u!" + block.ClassId + " &" + map[block.Id] + match.Groups[3].Value, 1);
				var group = groupComponents ? current.Group(block) : (Owner: block.Id, Rank: 0);
				return (Owner: long.Parse(map[group.Owner], CultureInfo.InvariantCulture), Rank: group.Rank,
					Id: long.Parse(map[block.Id], CultureInfo.InvariantCulture), Text: text);
			}).OrderBy(block => block.Owner).ThenBy(block => block.Rank).ThenBy(block => block.Id).Select(block => block.Text));
		}

		private static string Hash(string key)
		{
			unchecked
			{
				var hash = 14695981039346656037UL;
				foreach (var value in Encoding.UTF8.GetBytes(key)) hash = (hash ^ value) * 1099511628211UL;
				return (1000000000L + (long)(hash % 8000000000000000000UL)).ToString(CultureInfo.InvariantCulture);
			}
		}

		private sealed class Block
		{
			public string ClassId;
			public string Id;
			public string Text;
			public string Field(string name)
			{
				var match = Regex.Match(Text, @"^\s*" + Regex.Escape(name) + @": ([^\r\n]*)", RegexOptions.Multiline);
				return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
			}
			public string Ref(string name)
			{
				var match = Reference.Match(Field(name));
				return match.Success ? match.Groups[1].Value : "0";
			}
		}

		private sealed class Document
		{
			public readonly List<Block> Blocks = new List<Block>();
			public readonly string Preamble;
			private readonly Dictionary<string, Block> _byId = new Dictionary<string, Block>(StringComparer.Ordinal);
			private readonly Dictionary<string, Block> _transforms = new Dictionary<string, Block>(StringComparer.Ordinal);
			private readonly Dictionary<string, string> _keys = new Dictionary<string, string>(StringComparer.Ordinal);
			private readonly Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.Ordinal);

			public Document(string yaml)
			{
				var headers = Header.Matches(yaml);
				Preamble = headers.Count == 0 ? yaml : yaml.Substring(0, headers[0].Index);
				for (var i = 0; i < headers.Count; i++)
				{
					var end = i + 1 < headers.Count ? headers[i + 1].Index : yaml.Length;
					var block = new Block { ClassId = headers[i].Groups[1].Value, Id = headers[i].Groups[2].Value, Text = yaml.Substring(headers[i].Index, end - headers[i].Index) };
					Blocks.Add(block);
					_byId.Add(block.Id, block);
					if ((block.ClassId == "4" || block.ClassId == "224") && block.Ref("m_GameObject") != "0")
						_transforms.Add(block.Ref("m_GameObject"), block);
				}
				// Ambiguous identities must fail visibly rather than pick a random object's id.
				var unique = new HashSet<string>(StringComparer.Ordinal);
				foreach (var block in Blocks)
					if (!unique.Add(Key(block.Id))) throw new InvalidOperationException("Duplicate generated World identity: " + Key(block.Id));
			}

			public string Key(string id)
			{
				if (_keys.TryGetValue(id, out var key)) return key;
				if (!_byId.TryGetValue(id, out var block)) return string.Empty;
				var source = block.Field("m_CorrespondingSourceObject");
				if (source.Contains("guid:"))
					key = block.ClassId + "|stripped|" + Key(block.Ref("m_PrefabInstance")) + "|" + source;
				else if (block.ClassId == "1001")
					key = "1001|" + TransformPath(block.Ref("m_TransformParent")) + "|" + block.Field("m_SourcePrefab");
				else
					key = block.ClassId + "|" + GameObjectPath(block.ClassId == "1" ? id : block.Ref("m_GameObject")) + "|" + block.Field("m_Script");
				_keys.Add(id, key);
				return key;
			}


			public (string Owner, int Rank) Group(Block block)
			{
				if (block.ClassId == "1") return (block.Id, 0);
				var owner = block.Ref("m_GameObject");
				if (!_byId.TryGetValue(owner, out var gameObject)) return (block.Id, 0);
				var components = Regex.Matches(gameObject.Text, @"- component: \{fileID: (-?\d+)\}");
				for (var i = 0; i < components.Count; i++)
					if (components[i].Groups[1].Value == block.Id) return (owner, i + 1);
				return (owner, int.MaxValue);
			}

			private string TransformPath(string id) => _byId.TryGetValue(id, out var transform)
				? transform.Ref("m_GameObject") != "0" ? GameObjectPath(transform.Ref("m_GameObject")) : Key(id)
				: string.Empty;

			private string GameObjectPath(string id)
			{
				if (_paths.TryGetValue(id, out var path)) return path;
				if (!_byId.TryGetValue(id, out var gameObject)) return string.Empty;
				var parent = _transforms.TryGetValue(id, out var transform) ? TransformPath(transform.Ref("m_Father")) : string.Empty;
				var name = gameObject.Field("m_Name");
				// A length prefix distinguishes names containing slashes or separators.
				path = parent + "/" + name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name;
				_paths.Add(id, path);
				return path;
			}
		}
	}
}
