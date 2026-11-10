using System;
using System.Collections.Generic;
using System.Reflection;
using FerrySim.Controls;
using FerrySim.Core.Instruments;
using FerrySim.Vessels;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// C3 presentation bindings. Parse validates the entire file before publishing any entry.
	/// LoadShipped binds to one interior instance; do not retain its entries after that instance is destroyed.
	/// Popup consumers use Entries: MountId, Transform, Actions (priority order) and Label.
	/// Parsing, name resolution and reflection run at bind time only, never during Refresh.
	/// </summary>
	public sealed class ControlMapFile
	{
		[Serializable] private sealed class Document { public int version; public Data[] controls; }
		[Serializable] private sealed class Data
		{
			public string mount, channel, alarm, label;
			public double[] range, lit;
			public string[] actions;
		}

		public sealed class Entry
		{
			public string MountId { get; }
			public Transform Transform { get; }
			public IReadOnlyList<string> Actions { get; }
			public string Label { get; }
			internal readonly WheelhouseControlMap.ControlMount Mount;
			internal readonly Quaternion Rest;
			internal readonly Channel? Channel;
			internal readonly string Alarm;
			internal readonly double Low, High, LitLow, LitHigh;
			internal readonly bool HasLit, IsCompass;
			internal int AlarmIndex = -1;

			internal Entry(WheelhouseControlMap.ControlMount mount, Channel? channel, string alarm,
				double[] range, double[] lit, string[] actions, string label)
			{
				MountId = mount.Id; Transform = mount.Mount; Mount = mount; Rest = mount.Mount.localRotation;
				IsCompass = channel == FerrySim.Core.Instruments.Channel.Heading && mount.Kind == "card";
				Channel = channel; Alarm = alarm; Low = range == null ? 0 : range[0]; High = range == null ? 1 : range[1];
				HasLit = lit != null; LitLow = lit == null ? 0 : lit[0]; LitHigh = lit == null ? 0 : lit[1];
				Actions = Array.AsReadOnly(actions ?? Array.Empty<string>()); Label = label ?? string.Empty;
			}
		}

		private readonly Entry[] _entries;
		public IReadOnlyList<Entry> Entries { get; }
		internal Entry[] BoundEntries => _entries;
		private ControlMapFile(Entry[] entries) { _entries = entries; Entries = Array.AsReadOnly(entries); }

		public static ControlMapFile LoadShipped(WheelhouseControlMap mounts)
		{
			var file = Resources.Load<TextAsset>("FerrySimWheelhouse/controls-v1");
			if (file == null) throw new FormatException("Missing controls-v1.json resource.");
			return Parse(file.text, mounts);
		}

		public static ControlMapFile Parse(string json, WheelhouseControlMap mounts)
		{
			if (mounts == null) throw new ArgumentNullException(nameof(mounts));
			Document document;
			try { document = JsonUtility.FromJson<Document>(json); }
			catch (ArgumentException e) { throw new FormatException("Invalid controls-v1.json: " + e.Message, e); }
			if (document == null || document.version != 1 || document.controls == null)
				throw new FormatException("Expected controls-v1.json version 1 and controls array.");
			var actions = new HashSet<string>(StringComparer.Ordinal);
			foreach (var field in typeof(ActionIds).GetFields(BindingFlags.Public | BindingFlags.Static))
				if (field.IsLiteral && field.FieldType == typeof(string)) actions.Add((string)field.GetRawConstantValue());
			var byId = new Dictionary<string, WheelhouseControlMap.ControlMount>(StringComparer.Ordinal);
			foreach (var mount in mounts.Controls)
			{
				if (string.IsNullOrEmpty(mount.Id) || byId.ContainsKey(mount.Id)) throw new FormatException("Duplicate/empty layout mount: " + mount.Id);
				byId.Add(mount.Id, mount);
			}
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var result = new Entry[document.controls.Length];
			for (var i = 0; i < result.Length; i++)
			{
				var row = document.controls[i];
				var id = row?.mount;
				var context = "controls[" + i + "] (" + id + "): ";
				if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out var mount) || mount.Mount == null)
					throw new FormatException(context + "unknown mount for this interior");
				if (!seen.Add(id)) throw new FormatException(context + "duplicate mount");
				Channel? channel = null;
				if (row.channel != null)
				{
					if (!ChannelNames.TryParse(row.channel, out var parsed)) throw new FormatException(context + "unknown channel " + row.channel);
					channel = parsed;
				}
				if (row.alarm != null && (string.IsNullOrWhiteSpace(row.alarm) || channel.HasValue || mount.LampIndex < 0))
					throw new FormatException(context + "alarm requires a lamp and cannot also bind a channel");
				if (channel.HasValue && mount.LampIndex < 0 && row.range == null) throw new FormatException(context + "moving channel requires range");
				if (row.range != null && (!Pair(row.range) || row.range[0] == row.range[1])) throw new FormatException(context + "invalid range");
				if (row.lit != null && (!Pair(row.lit) || row.lit[0] > row.lit[1] || !channel.HasValue || mount.LampIndex < 0))
					throw new FormatException(context + "lit requires an inclusive channel interval on a lamp");
				foreach (var action in row.actions ?? Array.Empty<string>())
					if (!actions.Contains(action)) throw new FormatException(context + "unknown action " + action);
				result[i] = new Entry(mount, channel, row.alarm, row.range, row.lit, row.actions, row.label);
			}
			return new ControlMapFile(result);
		}

		private static bool Pair(double[] values) => values.Length == 2 && Finite(values[0]) && Finite(values[1]);
		internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

		internal void BindAlarms(IInstrumentReadout readout, UnityEngine.Object context)
		{
			foreach (var entry in _entries)
			{
				entry.AlarmIndex = -1;
				if (entry.Alarm == null) continue;
				for (var i = 0; i < readout.AlarmCount; i++)
					if (readout.AlarmId(i) == entry.Alarm) { entry.AlarmIndex = i; break; }
				if (entry.AlarmIndex < 0) Debug.LogWarning("Wheelhouse " + entry.MountId + ": vessel does not define alarm " + entry.Alarm + "; lamp remains dark outside lamp test.", context);
			}
		}
	}
}
