using System;
using FerrySim.Controls;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.UI
{
	/// <summary>
	/// Reads the Interface map's <c>Options</c> (F10) and <c>HudEdit</c> (F8) actions from a private copy of the actions
	/// asset registered with the binding service, so a rebind reaches it and an interactive capture disables it like every
	/// other map. The Interface map is live in every view (contracts C4). Presses are counted as the actions perform, so one
	/// that lands between two frames is not lost; the host takes them once per frame.
	/// </summary>
	public sealed class InterfaceActionReader : IDisposable
	{
		private readonly InputBindingService _service;
		private readonly InputAction _options;
		private readonly InputAction _hudEdit;
		private int _optionsPresses;
		private int _hudEditPresses;

		public InterfaceActionReader(InputBindingService service)
		{
			_service = service ?? throw new ArgumentNullException(nameof(service));
			Actions = _service.CreateInstance();
			var map = Actions.FindActionMap("Interface", true);
			_options = Actions.FindAction(ActionIds.Options, true);
			_hudEdit = Actions.FindAction(ActionIds.HudEdit, true);
			_options.performed += OnOptions;
			_hudEdit.performed += OnHudEdit;
			map.Enable();
		}

		public InputActionAsset Actions { get; }

		/// <summary>True once per press of Interface/Options since the last call.</summary>
		public bool TakeOptionsPress() => Take(ref _optionsPresses);

		/// <summary>True once per press of Interface/HudEdit since the last call.</summary>
		public bool TakeHudEditPress() => Take(ref _hudEditPresses);

		private static bool Take(ref int presses)
		{
			if (presses <= 0)
			{
				return false;
			}

			presses--;
			return true;
		}

		private void OnOptions(InputAction.CallbackContext context) => _optionsPresses++;

		private void OnHudEdit(InputAction.CallbackContext context) => _hudEditPresses++;

		public void Dispose()
		{
			_options.performed -= OnOptions;
			_hudEdit.performed -= OnHudEdit;
			_service.Unregister(Actions);
			Actions.Disable();
			if (Application.isPlaying)
			{
				UnityEngine.Object.Destroy(Actions);
			}
			else
			{
				UnityEngine.Object.DestroyImmediate(Actions);
			}
		}
	}
}
