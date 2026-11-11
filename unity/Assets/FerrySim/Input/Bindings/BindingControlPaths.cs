using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.Utilities;

namespace FerrySim.Controls
{
	/// <summary>
	/// Resolves registered layout metadata, without creating devices. Caches last only for one validation
	/// operation, so layout registration changes and Input System resets cannot leave stale identities.
	/// </summary>
	internal sealed class BindingControlPaths
	{
		public enum Validation
		{
			Invalid,
			UnknownLayout,
			Valid,
		}

		private readonly Dictionary<string, InputControlLayout> _layouts = new Dictionary<string, InputControlLayout>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, List<Control>> _resolved = new Dictionary<string, List<Control>>(StringComparer.OrdinalIgnoreCase);

		private readonly struct Control
		{
			public Control(string path, string layout) { Path = path; Layout = layout; }
			public string Path { get; }
			public string Layout { get; }
		}

		private readonly struct Ancestor
		{
			public Ancestor(InputControlLayout layout, string prefix) { Layout = layout; Prefix = prefix; }
			public InputControlLayout Layout { get; }
			public string Prefix { get; }
		}

		public bool ValidButton(string path, string scheme, out string reason) => ValidateButton(path, scheme, out reason) == Validation.Valid;

		public Validation ValidateButton(string path, string scheme, out string reason)
		{
			reason = "Expected a resolvable button control path.";
			if (!TryParse(path, out var parts)) return Validation.Invalid;
			var device = Load(parts[0].layout);
			if (device == null) { reason = "Device layout is not registered."; return Validation.UnknownLayout; }
			if (!device.isDeviceLayout) return Validation.Invalid;
			var inScheme = scheme == "Keyboard" ? BasedOn(parts[0].layout, "Keyboard") || BasedOn(parts[0].layout, "Mouse") : BasedOn(parts[0].layout, scheme);
			if (!inScheme) { reason = "Control does not belong to the selected scheme."; return Validation.Invalid; }
			var controls = Resolve(path, parts, device);
			if (controls.Count == 0 || controls.Any(control => !typeof(ButtonControl).IsAssignableFrom(Load(control.Layout)?.type))) return Validation.Invalid;
			reason = null;
			return Validation.Valid;
		}

		public bool Overlap(string first, string second)
		{
			if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
			if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
			if (!TryParse(first, out var firstParts) || !TryParse(second, out var secondParts)) return false;
			// Resolve both paths on the more specific layout. Unrelated layouts cannot name the same
			// device, even when their controls have identical names.
			var layout = BasedOn(firstParts[0].layout, secondParts[0].layout) ? firstParts[0].layout :
				BasedOn(secondParts[0].layout, firstParts[0].layout) ? secondParts[0].layout : null;
			var device = Load(layout);
			if (device == null || !device.isDeviceLayout) return false;
			foreach (var left in Resolve(first, firstParts, device))
				foreach (var right in Resolve(second, secondParts, device))
					if (string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) ||
						BasedOn(layout, "Keyboard") && ModifiersOverlap(left.Path, right.Path)) return true;
			return false;
		}

		private List<Control> Resolve(string path, InputControlPath.ParsedPathComponent[] parts, InputControlLayout device)
		{
			var key = device.name + "\n" + path;
			if (_resolved.TryGetValue(key, out var result)) return result;
			result = new List<Control>();
			Walk(device, "", new List<Ancestor>(), parts, 1, result, device, IsUsage(parts[1]));
			_resolved[key] = result;
			return result;
		}

		private void Walk(InputControlLayout layout, string prefix, List<Ancestor> ancestors,
			InputControlPath.ParsedPathComponent[] parts, int index, List<Control> result, InputControlLayout device, bool searchDescendants)
		{
			if (layout == null || ancestors.Any(ancestor => ancestor.Layout.name == layout.name)) return;
			ancestors.Add(new Ancestor(layout, prefix));
			foreach (var item in layout.controls)
			{
				// Slash entries modify a control in a child layout; merge them when visiting that child.
				if (item.name.ToString().IndexOf('/') >= 0) continue;
				for (var element = 0; element < (item.isArray ? item.arraySize : 1); element++)
				{
					var name = item.name + (item.isArray ? element.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
					var controlPath = prefix + name;
					var effective = item;
					for (var i = ancestors.Count - 2; i >= 0; i--)
					{
						var ancestor = ancestors[i];
						var modification = ancestor.Layout.FindControl(new InternedString(controlPath.Substring(ancestor.Prefix.Length)));
						if (modification.HasValue) effective = modification.Value.Merge(effective);
					}
					if (Matches(parts[index], name, effective))
					{
						if (index == parts.Length - 1) result.Add(new Control(controlPath, effective.layout.ToString()));
						else if (IsUsage(parts[index + 1]))
							Walk(device, "", new List<Ancestor>(), parts, index + 1, result, device, true);
						else Walk(Load(effective.layout.ToString()), controlPath + "/", ancestors, parts, index + 1, result, device, false);
					}
					if (searchDescendants) Walk(Load(effective.layout.ToString()), controlPath + "/", ancestors, parts, index, result, device, true);
				}
			}
			ancestors.RemoveAt(ancestors.Count - 1);
		}

		private static bool IsUsage(InputControlPath.ParsedPathComponent part) =>
			string.IsNullOrEmpty(part.name) && string.IsNullOrEmpty(part.layout) && string.IsNullOrEmpty(part.displayName) && part.usages.Any();

		private static bool Matches(InputControlPath.ParsedPathComponent part, string name, InputControlLayout.ControlItem control)
		{
			if (!string.IsNullOrEmpty(part.name) && !string.Equals(part.name, name, StringComparison.OrdinalIgnoreCase) &&
				!control.aliases.Any(alias => string.Equals(alias.ToString(), part.name, StringComparison.OrdinalIgnoreCase))) return false;
			if (!string.IsNullOrEmpty(part.layout) && !BasedOn(control.layout.ToString(), part.layout)) return false;
			if (!string.IsNullOrEmpty(part.displayName) && !string.Equals(part.displayName, control.displayName, StringComparison.OrdinalIgnoreCase)) return false;
			return part.usages.All(usage => control.usages.Any(value => string.Equals(value.ToString(), usage, StringComparison.OrdinalIgnoreCase)));
		}

		private InputControlLayout Load(string layout)
		{
			if (string.IsNullOrEmpty(layout)) return null;
			if (!_layouts.TryGetValue(layout, out var loaded)) _layouts[layout] = loaded = InputSystem.LoadLayout(layout);
			return loaded;
		}

		private static bool BasedOn(string layout, string parent) => !string.IsNullOrEmpty(layout) && !string.IsNullOrEmpty(parent) &&
			(string.Equals(layout, parent, StringComparison.OrdinalIgnoreCase) || InputSystem.IsFirstLayoutBasedOnSecond(layout, parent));

		private static bool TryParse(string path, out InputControlPath.ParsedPathComponent[] parts)
		{
			parts = null;
			if (!BindingOverrideStore.ValidPath(path)) return false;
			var parsed = InputControlPath.Parse(path).ToArray();
			if (parsed.Length < 2 || string.IsNullOrEmpty(parsed[0].layout) || !string.IsNullOrEmpty(parsed[0].name) ||
				!string.IsNullOrEmpty(parsed[0].displayName) || parsed[0].usages.Any()) return false;
			// Reject incomplete syntax which the Input System's permissive parser would otherwise repair.
			var rebuilt = parsed.Select(part =>
				(string.IsNullOrEmpty(part.layout) ? "" : "<" + part.layout + ">") +
				string.Concat(part.usages.Select(usage => "{" + usage + "}")) +
				(string.IsNullOrEmpty(part.displayName) ? "" : "#(" + part.displayName + ")") + part.name);
			if (!string.Equals(string.Join("/", rebuilt), path, StringComparison.Ordinal) ||
				parsed.Skip(1).Any(part => string.IsNullOrEmpty(part.name) && string.IsNullOrEmpty(part.layout) && string.IsNullOrEmpty(part.displayName) && !part.usages.Any())) return false;
			parts = parsed;
			return true;
		}

		private static bool ModifiersOverlap(string first, string second)
		{
			foreach (var modifier in new[] { "shift", "ctrl", "alt" })
				if (string.Equals(first, modifier, StringComparison.OrdinalIgnoreCase) && IsSide(second, modifier) ||
					string.Equals(second, modifier, StringComparison.OrdinalIgnoreCase) && IsSide(first, modifier)) return true;
			return false;
		}

		private static bool IsSide(string control, string modifier) =>
			string.Equals(control, "left" + modifier, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(control, "right" + modifier, StringComparison.OrdinalIgnoreCase);
	}
}
