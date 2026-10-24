using UnityEngine;
using UnityEngine.InputSystem;

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
			ApplyMode();
		}

		private void OnEnable() => ApplyMode();

		private void Update()
		{
			var keyboard = Keyboard.current;
			if (!keyboardShortcuts || keyboard == null)
			{
				return;
			}

			if (keyboard.f1Key.wasPressedThisFrame)
			{
				CurrentMode = Mode.FreeFly;
			}
			else if (keyboard.f2Key.wasPressedThisFrame)
			{
				CurrentMode = Mode.Chase;
			}
			else if (keyboard.f3Key.wasPressedThisFrame)
			{
				CurrentMode = Mode.Wheelhouse;
			}
		}

		private void ApplyMode()
		{
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
