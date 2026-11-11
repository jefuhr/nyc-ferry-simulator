using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace FerrySim.Controls
{
	/// <summary>Session binding authority. All consumers register their private asset with Instance.</summary>
	public sealed class InputBindingService : IBindingService, IDisposable
	{
		private static InputBindingService _instance;
		public static InputBindingService Instance => _instance ??= new InputBindingService(LoadDefaults(), new BindingOverrideStore(BindingOverrideStore.DefaultPath), true);
		private readonly InputActionAsset _defaults;
		private readonly InputActionAsset _current;
		private readonly BindingOverrideStore _store;
		private readonly List<BindingRow> _rows = new List<BindingRow>();
		private readonly List<InputActionAsset> _instances = new List<InputActionAsset>();
		private readonly Dictionary<InputActionMap, bool> _suspendedHelm = new Dictionary<InputActionMap, bool>();
		private readonly Dictionary<InputActionAsset, HashSet<InputControl>> _heldOnReturn = new Dictionary<InputActionAsset, HashSet<InputControl>>();
		private readonly List<InputActionMap> _captureMaps = new List<InputActionMap>();
		private List<BindingOverride> _overrides = new List<BindingOverride>();
		private InputActionRebindingExtensions.RebindingOperation _operation;
		private Action<RebindOutcome> _completed;
		private ButtonControl _releaseBeforeResume;
		private readonly List<ButtonControl> _heldBeforeCapture = new List<ButtonControl>();
		private bool _freeFly;
		private bool _disposed;
		private bool _suppressInput;
		private static readonly Dictionary<InputActionAsset, InputBindingService> Owners = new Dictionary<InputActionAsset, InputBindingService>();

		public InputBindingService(InputActionAsset defaults, BindingOverrideStore store = null) : this(defaults, store, false) { }
		private InputBindingService(InputActionAsset defaults, BindingOverrideStore store, bool ownsDefaults)
		{
			if (defaults == null) throw new ArgumentNullException(nameof(defaults));
			NeutralDeadzoneProcessor.Register();
			_defaults = UnityEngine.Object.Instantiate(defaults);
			_current = UnityEngine.Object.Instantiate(defaults);
			_defaults.Disable(); _current.Disable();
			if (ownsDefaults) DestroyAsset(defaults);
			_store = store;
			foreach (var map in _defaults.actionMaps)
				foreach (var action in map.actions)
				{
					var counts = new Dictionary<string, int>();
					foreach (var binding in action.bindings)
					{
						if (!BindingSlots.Rebindable(action, binding)) continue;
						foreach (var scheme in (binding.groups ?? "").Split(';'))
						{
							if (string.IsNullOrEmpty(scheme)) continue;
							counts.TryGetValue(scheme, out var slot);
							_rows.Add(new BindingRow(map.name + "/" + action.name, slot, scheme,
								BindingLabels.TryGetRowLabel(map.name + "/" + action.name, slot, out var label) ? label : LabelFor(action, binding)));
							counts[scheme] = slot + 1;
						}
					}
				}
			Rows = _rows.AsReadOnly();
			Load();
		}

		private static string LabelFor(InputAction action, InputBinding binding)
		{
			var direction = binding.name == "negative" ? "port" : "starboard";
			if (action.name == "NfuJog") return "Jog lever to " + direction;
			if (action.name == "HelmStep") return "Helm to " + direction;
			if (action.name.EndsWith("LeverStep", StringComparison.Ordinal) || action.name == "BothLeversStep")
				return action.name.Replace("Step", "") + (binding.name == "negative" ? " astern" : " ahead");
			var text = new System.Text.StringBuilder();
			foreach (var character in action.name)
			{
				if (text.Length > 0 && char.IsUpper(character)) text.Append(' ');
				text.Append(character);
			}
			if (binding.isPartOfComposite) text.Append(" ").Append(binding.name);
			return text.ToString();
		}

		public event Action BindingsChanged;
		public IReadOnlyList<BindingRow> Rows { get; }
		public bool IsRebinding => _operation != null;
		public string LastError { get; private set; } = "";
		public bool IsFreeFly => _freeFly;

		/// <summary>Reloads all-or-nothing and publishes once, including a missing or rejected file.</summary>
		public bool Load()
		{
			CancelRebind();
			var loaded = new List<BindingOverride>();
			var accepted = _store == null || _store.Load(_defaults, out loaded);
			LastError = _store?.LastError ?? "";
			if (_store != null) foreach (var warning in _store.Warnings) Debug.LogWarning("[FerrySim] " + warning);
			// Check the final document, rather than application order: exchanging two keys is valid.
			ApplyTo(_current, loaded);
			if (accepted && FindAnyConflict(out var conflict))
			{
				accepted = false;
				LastError = "Input bindings ignored: conflicting binding " + conflict + ".";
			}
			if (!accepted) loaded.Clear();
			_overrides = loaded;
			Publish();
			return accepted;
		}

		public void Register(InputActionAsset instance)
		{
			if (instance == null) throw new ArgumentNullException(nameof(instance));
			if (_instances.Contains(instance)) return;
			if (Owners.TryGetValue(instance, out var owner) && owner != this) throw new InvalidOperationException("Asset already belongs to another binding service.");
			// This service outlives input-system resets (test fixtures, domain state restores), which drop
			// custom processors. Bindings name ferryNeutralDeadzone, so it must exist before any is read.
			NeutralDeadzoneProcessor.Register();
			_instances.Add(instance); Owners[instance] = this;
			ApplyTo(instance, _overrides);
			if (_freeFly) SuspendHelm(instance);
			BindingsChanged?.Invoke();
		}

		public void Unregister(InputActionAsset instance)
		{
			if (instance == null) return;
			_instances.Remove(instance); Owners.Remove(instance); _heldOnReturn.Remove(instance);
			var map = instance.FindActionMap("Helm");
			if (map != null) _suspendedHelm.Remove(map);
			_captureMaps.RemoveAll(item => item.asset == instance);
		}

		public InputActionAsset CreateInstance()
		{
			var asset = UnityEngine.Object.Instantiate(_defaults);
			Register(asset);
			return asset;
		}

		public string DisplayString(string actionId, string scheme)
		{
			var action = _current.FindAction(actionId);
			if (action == null) return "";
			NeutralDeadzoneProcessor.Register();
			var labels = new List<string>();
			for (var i = 0; i < action.bindings.Count; i++)
			{
				var binding = action.bindings[i];
				if (binding.isComposite || !BindingSlots.InScheme(binding, scheme) || string.IsNullOrEmpty(binding.effectivePath)) continue;
				labels.Add(action.GetBindingDisplayString(i));
			}
			return string.Join(" / ", labels);
		}

		public string DisplayString(string actionId, int slot, string scheme)
		{
			var action = _current.FindAction(actionId);
			if (action == null) return "";
			var index = BindingSlots.Index(action, slot, scheme);
			if (index < 0 || string.IsNullOrEmpty(action.bindings[index].effectivePath)) return "";
			NeutralDeadzoneProcessor.Register();
			return action.GetBindingDisplayString(index);
		}

		public bool TryApply(string actionId, int slot, string scheme, string path, out string conflictActionId)
		{
			var action = _current.FindAction(actionId, true);
			var index = BindingSlots.Index(action, slot, scheme);
			if (index < 0) throw new ArgumentOutOfRangeException(nameof(slot));
			var paths = new BindingControlPaths();
			if (!paths.ValidButton(path, scheme, out var reason)) throw new ArgumentException(reason, nameof(path));

			conflictActionId = FindConflict(actionId, index, path, paths);
			if (conflictActionId != null) return false;
			var next = CopyOverrides();
			next.RemoveAll(item => item.action == actionId && item.slot == slot && item.scheme == scheme);
			if (!string.Equals(_defaults.FindAction(actionId).bindings[index].path, path, StringComparison.OrdinalIgnoreCase))
				next.Add(new BindingOverride { action = actionId, slot = slot, scheme = scheme, path = path });
			Persist(next);
			return true;
		}

		public void BeginRebind(string actionId, int slot, string scheme, Action<RebindOutcome> completed)
		{
			if (completed == null) throw new ArgumentNullException(nameof(completed));
			CancelRebind();
			ResumeCaptureMaps();
			var action = _current.FindAction(actionId, true);
			var index = BindingSlots.Index(action, slot, scheme);
			if (index < 0) throw new ArgumentOutOfRangeException(nameof(slot));
			_completed = completed;
			_suppressInput = true;
			foreach (var asset in _instances)
				if (asset != null) foreach (var map in asset.actionMaps) if (map.enabled)
					{
						_captureMaps.Add(map);
						foreach (var candidate in map.actions)
							foreach (var control in candidate.controls)
								if (control is ButtonControl button && button.isPressed && !_heldBeforeCapture.Contains(button)) _heldBeforeCapture.Add(button);
						map.Disable();
					}
			var outcome = new RebindOutcome(RebindResult.Cancelled, null);
			_operation = action.PerformInteractiveRebinding(index).WithExpectedControlType("Button")
				.WithCancelingThrough("<Keyboard>/escape").OnMatchWaitForAnother(0f)
				.WithMatchingEventsBeingSuppressed(false) // All registered maps are disabled; retain physical state until release.
				.WithControlsExcluding("<Pointer>/position").WithControlsExcluding("<Pointer>/delta")
				.OnApplyBinding((operation, path) =>
				{
					_releaseBeforeResume = operation.selectedControl as ButtonControl;
					try
					{
						var applied = TryApply(actionId, slot, scheme, path, out var conflict);
						outcome = new RebindOutcome(applied ? RebindResult.Applied : RebindResult.Conflict, conflict);
					}
					catch (System.IO.IOException) { outcome = new RebindOutcome(RebindResult.Cancelled, null); }
				})
				.OnComplete(_ => Finish(outcome))
				.OnCancel(_ => Finish(new RebindOutcome(RebindResult.Cancelled, null)));
			if (scheme == "Keyboard") _operation.WithControlsHavingToMatchPath("<Keyboard>").WithControlsHavingToMatchPath("<Mouse>");
			else _operation.WithControlsHavingToMatchPath("<" + scheme + ">");
			_operation.Start();
		}

		public void CancelRebind() => _operation?.Cancel();
		private void Finish(RebindOutcome outcome)
		{
			var operation = _operation; _operation = null;
			var completed = _completed; _completed = null;
			operation?.Dispose();
			// The capture press must be released before actions resume, or it could become a lever order.
			if (_releaseBeforeResume != null || _heldBeforeCapture.Count > 0) InputSystem.onAfterUpdate += ResumeAfterRelease;
			else ResumeCaptureMaps();
			completed?.Invoke(outcome);
		}
		private void ResumeAfterRelease()
		{
			if (_releaseBeforeResume != null && _releaseBeforeResume.device.added && _releaseBeforeResume.isPressed) return;
			foreach (var button in _heldBeforeCapture) if (button.device.added && button.isPressed) return;
			ResumeCaptureMaps();
		}
		private void ResumeCaptureMaps()
		{
			InputSystem.onAfterUpdate -= ResumeAfterRelease;
			_releaseBeforeResume = null; _heldBeforeCapture.Clear();
			foreach (var map in _captureMaps)
				if (map.asset != null && !(map.name == "Helm" && _freeFly) && !(map.name == "FreeFly" && !_freeFly)) map.Enable();
			_captureMaps.Clear(); _suppressInput = false;
		}

		public void Reset(string actionId)
		{
			if (!TryReset(actionId, out var conflict))
				throw new InvalidOperationException("Reset would conflict with " + conflict + "; reset that action or reset all bindings.");
		}
		public void ResetAll() { CancelRebind(); Persist(new List<BindingOverride>()); }
		public bool TryReset(string actionId, out string conflictActionId)
		{
			var action = _current.FindAction(actionId, true);
			var next = CopyOverrides(); next.RemoveAll(item => item.action == actionId);
			conflictActionId = null;
			ApplyTo(_current, next);
			try
			{
				for (var index = 0; index < action.bindings.Count; index++)
				{
					if (!BindingSlots.Rebindable(action, action.bindings[index])) continue;
					conflictActionId = FindConflict(actionId, index, action.bindings[index].effectivePath);
					if (conflictActionId != null) return false;
				}
			}
			finally { ApplyTo(_current, _overrides); }
			Persist(next);
			return true;
		}
		private void Persist(List<BindingOverride> next)
		{
			if (_store != null && !_store.Save(next)) { LastError = _store.LastError; throw new System.IO.IOException(LastError); }
			LastError = ""; _overrides = next; Publish();
		}
		private List<BindingOverride> CopyOverrides()
		{
			var copy = new List<BindingOverride>(); foreach (var item in _overrides) copy.Add(item.Copy()); return copy;
		}
		private void Publish()
		{
			_suppressInput = true;
			try
			{
				ApplyTo(_current, _overrides);
				for (var i = _instances.Count - 1; i >= 0; i--)
					if (_instances[i] == null) { Owners.Remove(_instances[i]); _instances.RemoveAt(i); } else ApplyTo(_instances[i], _overrides);
			}
			finally { _suppressInput = IsRebinding || _captureMaps.Count > 0; }
			BindingsChanged?.Invoke();
		}
		private static void ApplyTo(InputActionAsset asset, List<BindingOverride> overrides)
		{
			// Change only managed button paths. Unchanged bindings and analog profile assignments survive.
			foreach (var map in asset.actionMaps)
				foreach (var action in map.actions)
					for (var index = 0; index < action.bindings.Count; index++)
					{
						var binding = action.bindings[index];
						if (!BindingSlots.Rebindable(action, binding)) continue;
						string desired = null;
						foreach (var item in overrides)
							if (item.action == map.name + "/" + action.name && BindingSlots.Index(action, item.slot, item.scheme) == index) { desired = item.path; break; }
						if (binding.overridePath == desired) continue;
						if (desired == null) action.RemoveBindingOverride(index);
						else action.ApplyBindingOverride(index, desired);
					}
		}

		private bool FindAnyConflict(out string holder)
		{
			var paths = new BindingControlPaths();
			foreach (var row in _rows)
			{
				var action = _current.FindAction(row.ActionId);
				var index = BindingSlots.Index(action, row.Slot, row.Scheme);
				holder = FindConflict(row.ActionId, index, action.bindings[index].effectivePath, paths);
				if (holder != null) return true;
			}
			holder = null; return false;
		}
		private string FindConflict(string actionId, int index, string path, BindingControlPaths paths = null)
		{
			paths ??= new BindingControlPaths();
			foreach (var reserved in BindingConflicts.ReservedKeys) if (paths.Overlap(path, reserved)) return "Interface/Navigation";
			foreach (var map in _current.actionMaps)
				foreach (var action in map.actions)
					for (var i = 0; i < action.bindings.Count; i++)
					{
						if (map.name + "/" + action.name == actionId && i == index || action.bindings[i].isComposite) continue;
						if (BindingConflicts.CanBeLiveTogether(actionId, map.name + "/" + action.name) && paths.Overlap(path, action.bindings[i].effectivePath))
							return map.name + "/" + action.name;
					}
			return null;
		}

		public void SetFreeFly(bool freeFly)
		{
			_freeFly = freeFly;
			foreach (var asset in _instances)
			{
				if (asset == null) continue;
				if (freeFly) SuspendHelm(asset);
				else
				{
					var helm = asset.FindActionMap("Helm");
					if (helm != null && _suspendedHelm.TryGetValue(helm, out var enabled))
					{ _suspendedHelm.Remove(helm); if (enabled && !_suppressInput) { RememberHeldButtons(helm); helm.Enable(); } }
				}
			}
		}
		private void RememberHeldButtons(InputActionMap map)
		{
			var held = new HashSet<InputControl>();
			foreach (var action in map.actions)
			{
				var rebindable = false;
				foreach (var binding in action.bindings) if (BindingSlots.Rebindable(action, binding)) { rebindable = true; break; }
				if (!rebindable) continue;
				foreach (var control in action.controls) if (control is ButtonControl button && button.isPressed) held.Add(control);
			}
			if (held.Count > 0) _heldOnReturn[map.asset] = held;
			else _heldOnReturn.Remove(map.asset);
		}
		internal static bool HeldWhileFlying(InputAction action)
		{
			var asset = action.actionMap.asset;
			if (asset == null || !Owners.TryGetValue(asset, out var service) || !service._heldOnReturn.TryGetValue(asset, out var held)) return false;
			foreach (var control in action.controls)
			{
				if (!held.Contains(control)) continue;
				if (control is ButtonControl button && button.isPressed) return true;
				held.Remove(control);
			}
			if (held.Count == 0) service._heldOnReturn.Remove(asset);
			return false;
		}

		private void SuspendHelm(InputActionAsset asset)
		{
			var helm = asset.FindActionMap("Helm");
			if (helm == null) return;
			if (!_suspendedHelm.ContainsKey(helm)) _suspendedHelm.Add(helm, helm.enabled);
			helm.Disable();
		}
		internal static bool HelmSuppressed(InputActionAsset asset) => asset != null && Owners.TryGetValue(asset, out var service) && service._freeFly;
		internal static bool InputSuppressed(InputActionAsset asset) => asset != null && Owners.TryGetValue(asset, out var service) && (service._freeFly || service._suppressInput);
		internal static bool RequestHelmEnabled(InputActionAsset asset)
		{
			if (asset == null || !Owners.TryGetValue(asset, out var service)) return true;
			var map = asset.FindActionMap("Helm");
			if (service._freeFly) { service._suspendedHelm[map] = true; return false; }
			if (service._suppressInput)
			{
				if (!service._captureMaps.Contains(map)) service._captureMaps.Add(map);
				return false;
			}
			return true;
		}
		internal static void RequestHelmDisabled(InputActionAsset asset)
		{
			if (asset == null || !Owners.TryGetValue(asset, out var service)) return;
			var map = asset.FindActionMap("Helm");
			if (service._freeFly) service._suspendedHelm[map] = false;
			service._captureMaps.Remove(map);
		}

		private static void DestroyAsset(InputActionAsset asset)
		{
			if (Application.isPlaying) UnityEngine.Object.Destroy(asset); else UnityEngine.Object.DestroyImmediate(asset);
		}

		private static InputActionAsset LoadDefaults()
		{
			var source = Resources.Load<TextAsset>("FerrySimInput/BindingDefaults");
			if (source == null) throw new InvalidOperationException("Missing input binding defaults resource.");
			return InputActionAsset.FromJson(source.text);
		}
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void OnSubsystemRegistration() => ResetSession();
		public static void SetSession(InputBindingService service) { ResetSession(); _instance = service ?? throw new ArgumentNullException(nameof(service)); }
		public static void ResetSession() { _instance?.Dispose(); _instance = null; }
		public void Dispose()
		{
			if (_disposed) return;
			CancelRebind(); ResumeCaptureMaps(); _disposed = true;
			foreach (var asset in _instances) Owners.Remove(asset);
			_instances.Clear(); _suspendedHelm.Clear(); _heldOnReturn.Clear();
			DestroyAsset(_defaults); DestroyAsset(_current);
		}
	}

	/// <summary>Private camera action instance with shared overrides and explicit map lifetime.</summary>
	public sealed class CameraActionReader : IDisposable
	{
		private readonly InputBindingService _service;
		public InputActionAsset Actions { get; }
		public CameraActionReader(bool freeFly = false)
		{
			_service = InputBindingService.Instance;
			Actions = _service.CreateInstance();
			Actions.FindActionMap("Camera", true).Enable();
			if (freeFly) Actions.FindActionMap("FreeFly", true).Enable();
		}
		public InputAction this[string actionId] => Actions.FindAction(actionId, true);
		public void Dispose()
		{
			_service.Unregister(Actions); Actions.Disable();
			if (Application.isPlaying) UnityEngine.Object.Destroy(Actions); else UnityEngine.Object.DestroyImmediate(Actions);
		}
	}
}
