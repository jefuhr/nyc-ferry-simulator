using System;
using FerrySim.Controls;
using FerrySim.UI;
using FerrySim.World;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FerrySim.Prompts
{
	/// <summary>
	/// Drives a <see cref="KeybindPopupLayer"/>: owns it, adds it to the UIDocument root it is given, and updates it
	/// after the cameras have moved each frame. It reads <c>Interface/KeybindHints</c> from its own copy of the
	/// actions asset, registered with the binding service (Input/README.md, Binding service), and follows the
	/// scheme of the device last used (a button press on a keyboard or mouse, a gamepad or a joystick).
	/// <para>
	/// Shown only in the wheelhouse view (the lean-in view included): the camera's <see cref="CameraRig"/> mode
	/// when it has one, else whether its <see cref="WheelhouseCameraController"/> is enabled; a camera with neither
	/// counts as a wheelhouse camera (tools and tests). No singletons: the composition code creates it, calls
	/// <see cref="Bind(Camera, WheelhouseControlsView, IBindingService)"/> after every class switch, and sets
	/// <see cref="Mode"/>, <see cref="Suppressed"/> and <see cref="Palette"/>.
	/// </para>
	/// </summary>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(100)]
	public sealed class KeybindPopups : MonoBehaviour
	{
		/// <summary>The profiler marker around the per-frame update.</summary>
		public const string MarkerName = "FerrySim.KeybindPopups.Update";

		private const string DefaultsResource = "FerrySimInput/BindingDefaults";
		private static readonly ProfilerMarker Marker = new ProfilerMarker(ProfilerCategory.Scripts, MarkerName);

		private KeybindPopupLayer _layer;
		private Camera _camera;
		private CameraRig _rig;
		private WheelhouseCameraController _wheelhouse;
		private IBindingService _bindings;
		private InputBindingService _registeredWith;
		private InputActionAsset _actions;
		private InputAction _hold;
		private IDisposable _anyButton;
		private Action<InputControl> _onButton;

		/// <summary>The layer this driver owns.</summary>
		public KeybindPopupLayer Layer => _layer ??= new KeybindPopupLayer();

		/// <summary>The C5 setting <c>hud.keybindHints</c>.</summary>
		public KeybindHintMode Mode
		{
			get => Layer.Mode;
			set => Layer.Mode = value;
		}

		/// <summary>True while the options menu or the scenario picker is open.</summary>
		public bool Suppressed
		{
			get => Layer.Suppressed;
			set => Layer.Suppressed = value;
		}

		/// <summary>Follow <c>WheelhouseView.Palette</c>; setting the same value again costs nothing.</summary>
		public PaletteKind Palette
		{
			get => Layer.Palette;
			set => Layer.Palette = value;
		}

		/// <summary>"Keyboard", "Gamepad" or "Joystick": the last device used, or what was set here since.</summary>
		public string ActiveScheme
		{
			get => Layer.Scheme;
			set => Layer.Scheme = value;
		}

		/// <summary>The camera the popups are projected through.</summary>
		public Camera Camera => _camera;

		/// <summary>Adds the layer to a document's root (moving it if it was elsewhere).</summary>
		public void AttachTo(UIDocument document)
		{
			if (document == null)
			{
				throw new ArgumentNullException(nameof(document));
			}

			AttachTo(document.rootVisualElement);
		}

		/// <summary>Adds the layer as the last child of <paramref name="root"/>, so it draws over its siblings; null detaches it.</summary>
		public void AttachTo(VisualElement root)
		{
			Layer.RemoveFromHierarchy();
			root?.Add(Layer);
		}

		/// <summary>
		/// Shows popups for one interior instance through one camera. Call again after a class switch: the control
		/// map's entries belong to one instance. A null map or root shows nothing.
		/// </summary>
		public void Bind(Camera camera, Transform interiorRoot, ControlMapFile controlMap, IBindingService bindings)
		{
			_camera = camera;
			_rig = camera != null ? camera.GetComponent<CameraRig>() : null;
			_wheelhouse = camera != null ? camera.GetComponent<WheelhouseCameraController>() : null;
			if (!ReferenceEquals(bindings, _bindings))
			{
				ReleaseHold();
				_bindings = bindings;
				Layer.SetBindings(bindings);
			}

			AcquireHold();
			ListenForDevices();
			Layer.SetControls(interiorRoot, controlMap);
		}

		/// <summary>The same, from the interior's <see cref="WheelhouseControlsView"/> and its <see cref="WheelhouseControlsView.ControlMap"/>.</summary>
		public void Bind(Camera camera, WheelhouseControlsView view, IBindingService bindings) =>
			Bind(camera, view != null ? view.transform : null, view != null ? view.ControlMap : null, bindings);

		/// <summary>Lets go of the camera, interior, binding service and the registered actions copy, and hides everything.</summary>
		public void Unbind()
		{
			StopListening();
			ReleaseHold();
			_bindings = null;
			_camera = null;
			_rig = null;
			_wheelhouse = null;
			if (_layer != null)
			{
				_layer.SetBindings(null);
				_layer.SetControls(null, null);
				_layer.HoldPressed = false;
				_layer.Refresh(null);
			}
		}

		private void OnEnable()
		{
			if (_bindings != null)
			{
				AcquireHold();
				ListenForDevices();
			}
		}

		private void OnDisable()
		{
			StopListening();
			ReleaseHold();
			if (_layer != null)
			{
				_layer.HoldPressed = false;
				_layer.Refresh(null);
			}
		}

		private void OnDestroy()
		{
			Unbind();
			_layer?.RemoveFromHierarchy();
		}

		private void LateUpdate() => Tick();

		/// <summary>The per-frame update (LateUpdate, after the cameras). Allocation-free.</summary>
		internal void Tick()
		{
			using (Marker.Auto())
			{
				var layer = Layer;
				layer.HoldPressed = _hold != null && _hold.IsPressed();
				layer.InWheelhouseView = InWheelhouseView();
				layer.Refresh(_camera);
			}
		}

		private bool InWheelhouseView()
		{
			if (_camera == null)
			{
				return false;
			}

			if (_rig != null)
			{
				return _rig.CurrentMode == CameraRig.Mode.Wheelhouse;
			}

			if (_wheelhouse != null)
			{
				return _wheelhouse.isActiveAndEnabled;
			}

			return true;
		}

		private void AcquireHold()
		{
			if (_bindings == null || _actions != null)
			{
				return;
			}

			if (_bindings is InputBindingService service)
			{
				_actions = service.CreateInstance();
				_registeredWith = service;
			}
			else
			{
				var json = Resources.Load<TextAsset>(DefaultsResource);
				if (json == null)
				{
					Debug.LogWarning("Keybind popups: missing " + DefaultsResource + "; the hold key will not work.", this);
					return;
				}

				_actions = InputActionAsset.FromJson(json.text);
				_bindings.Register(_actions);
			}

			_hold = _actions.FindAction(ActionIds.KeybindHints, false);
			_hold?.Enable();
		}

		private void ReleaseHold()
		{
			if (_actions == null)
			{
				return;
			}

			_registeredWith?.Unregister(_actions);
			_actions.Disable();
			if (Application.isPlaying)
			{
				Object.Destroy(_actions);
			}
			else
			{
				Object.DestroyImmediate(_actions);
			}

			_actions = null;
			_hold = null;
			_registeredWith = null;
		}

		private void ListenForDevices()
		{
			if (_anyButton != null)
			{
				return;
			}

			_onButton ??= OnButton;
			_anyButton = InputSystem.onAnyButtonPress.Call(_onButton);
		}

		private void StopListening()
		{
			_anyButton?.Dispose();
			_anyButton = null;
		}

		private void OnButton(InputControl control)
		{
			var scheme = KeybindSchemes.ForDevice(control?.device);
			if (scheme != null)
			{
				Layer.Scheme = scheme;
			}
		}
	}
}
