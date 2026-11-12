using UnityEngine;
using FerrySim.Controls;

namespace FerrySim.World
{
	/// <summary>
	/// Switches the main camera between the free-fly debug view, the external chase view and the
	/// wheelhouse view by enabling exactly one controller. F1, F2 and F3 select them when keyboard
	/// shortcuts are on. The camera also carries the CesiumGlobeAnchor and CesiumOriginShift that keep
	/// the georeference origin near it.
	/// </summary>
	[DisallowMultipleComponent]
	[RequireComponent(typeof(Camera))]
	public sealed class CameraRig : MonoBehaviour
	{
		private CameraActionReader _input;

		public enum Mode
		{
			FreeFly,
			Chase,
			Wheelhouse,
		}

		[SerializeField] private Mode mode = Mode.FreeFly;
		[SerializeField] private FreeFlyCameraController freeFly;
		[SerializeField] private ChaseCameraController chase;
		[SerializeField] private WheelhouseCameraController wheelhouse;
		[Tooltip("F1 free-fly, F2 chase, F3 wheelhouse.")]
		[SerializeField] private bool keyboardShortcuts = true;
		[Tooltip("Field of view outside the wheelhouse, degrees.")]
		[Range(20f, 110f)]
		[SerializeField] private float defaultFieldOfView = 60f;

		public Mode CurrentMode
		{
			get => mode;
			set
			{
				mode = value;
				ApplyMode();
			}
		}

		public FreeFlyCameraController FreeFly => freeFly;

		public ChaseCameraController Chase => chase;

		public WheelhouseCameraController Wheelhouse => wheelhouse;

		/// <summary>What the chase view follows (the vessel, once the bridge task assigns it).</summary>
		public Transform ChaseTarget
		{
			get => chase != null ? chase.Target : null;
			set
			{
				if (chase != null)
				{
					chase.Target = value;
				}
			}
		}

		/// <summary>Where the wheelhouse view sits (the bridge task parents the mount to the vessel).</summary>
		public WheelhouseCameraMount WheelhouseMount
		{
			get => wheelhouse != null ? wheelhouse.Mount : null;
			set
			{
				if (wheelhouse != null)
				{
					wheelhouse.Mount = value;
				}
			}
		}

		/// <summary>Sets the controllers; the world builder calls this.</summary>
		public void Configure(FreeFlyCameraController freeFlyController, ChaseCameraController chaseController, WheelhouseCameraController wheelhouseController)
		{
			freeFly = freeFlyController;
			chase = chaseController;
			wheelhouse = wheelhouseController;
			ApplyHold();
			ApplyMode();
		}

		/// <summary>True while camera input is held because the player is typing.</summary>
		public bool TextEntryHeld { get; private set; }

		/// <summary>
		/// Holds (true) or releases (false) every camera key, button and axis this rig and its three controllers read,
		/// rebound ones included: view selection, bow and focus keys, recentre, look, orbit, scroll and free-fly movement.
		/// The cameras keep following, easing and presenting; nothing about their current view is reset. On release a
		/// button needs a fresh press. Controllers assigned or enabled while held start held.
		/// </summary>
		public void SetTextEntryHeld(bool held)
		{
			TextEntryHeld = held;
			_input?.SetHeld(held);
			ApplyHold();
		}

		private void ApplyHold()
		{
			if (freeFly != null) freeFly.SetInputHeld(TextEntryHeld);
			if (chase != null) chase.SetInputHeld(TextEntryHeld);
			if (wheelhouse != null) wheelhouse.SetInputHeld(TextEntryHeld);
		}

		private void OnEnable() { _input = new CameraActionReader(); _input.SetHeld(TextEntryHeld); ApplyMode(); }
		private void OnDisable() { _input?.Dispose(); _input = null; InputBindingService.Instance.SetFreeFly(false); }

		private void Update()
		{
			if (!keyboardShortcuts || _input == null)
			{
				return;
			}

			if (_input[ActionIds.ViewFreeFly].WasPressedThisFrame())
			{
				CurrentMode = Mode.FreeFly;
			}
			else if (_input[ActionIds.ViewChase].WasPressedThisFrame())
			{
				CurrentMode = Mode.Chase;
			}
			else if (_input[ActionIds.ViewWheelhouse].WasPressedThisFrame())
			{
				CurrentMode = Mode.Wheelhouse;
			}
		}

		private void ApplyMode()
		{
			InputBindingService.Instance.SetFreeFly(mode == Mode.FreeFly);
			if (freeFly != null)
			{
				freeFly.enabled = mode == Mode.FreeFly;
			}

			if (chase != null)
			{
				chase.enabled = mode == Mode.Chase;
			}

			if (wheelhouse != null)
			{
				wheelhouse.enabled = mode == Mode.Wheelhouse;
			}

			if (mode != Mode.Wheelhouse)
			{
				GetComponent<Camera>().fieldOfView = defaultFieldOfView;
			}
		}
	}
}
