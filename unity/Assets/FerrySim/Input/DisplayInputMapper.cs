using System;
using UnityEngine.InputSystem;

namespace FerrySim.Controls
{
	/// <summary>
	/// Turns the "Displays" action map into button events for the wheelhouse chartplotter and radar, and nothing
	/// else: no lever, helm or run command lives here. The composition root decides when the map is enabled (only
	/// in the wheelhouse view with the scenario picker closed) and which events apply (only to a visible display).
	/// Keyboard only; the key map is documented in Input/README.md and must not reuse a Helm key, the bow view's B,
	/// the camera's F1–F3 or the picker's keys (Input/Tests guards that).
	/// </summary>
	public sealed class DisplayInputMapper : IDisposable
	{
		public const string MapName = "Displays";

		private readonly InputActionMap _map;
		private readonly InputAction[] _actions;
		private readonly Action<InputAction.CallbackContext>[] _handlers;
		private bool _disposed;

		public DisplayInputMapper(InputActionAsset actions)
		{
			if (actions == null)
			{
				throw new ArgumentNullException(nameof(actions));
			}

			_map = actions.FindActionMap(MapName, throwIfNotFound: true);
			var names = new[]
			{
				"ChartplotterToggle", "RadarToggle",
				"ChartZoomIn", "ChartZoomOut", "ChartOrientation", "ChartCentre", "ChartPanUp", "ChartPanLeft", "ChartPanDown", "ChartPanRight",
				"ChartPalette", "ChartNextTarget", "ChartClearSelection",
				"RadarRangeUp", "RadarRangeDown", "RadarOrientation", "RadarPalette", "RadarEblLeft", "RadarEblRight", "RadarVrmIn", "RadarVrmOut",
				"RadarGainDown", "RadarGainUp", "RadarSeaDown", "RadarSeaUp",
			};
			_actions = new InputAction[names.Length];
			_handlers = new Action<InputAction.CallbackContext>[names.Length];
			for (var i = 0; i < names.Length; i++)
			{
				var index = i;
				_actions[i] = _map.FindAction(names[i], throwIfNotFound: true);
				_handlers[i] = _ => Raise(index);
				_actions[i].performed += _handlers[i];
			}
		}

		public event Action ChartplotterToggleRequested;
		public event Action RadarToggleRequested;

		/// <summary>+1 zooms in (a smaller span), −1 zooms out.</summary>
		public event Action<int> ChartZoomRequested;
		public event Action ChartOrientationRequested;
		public event Action ChartCentreRequested;

		/// <summary>Screen direction: dx +1 is right, dy +1 is down, one step each.</summary>
		public event Action<int, int> ChartPanRequested;
		public event Action ChartPaletteRequested;
		public event Action ChartNextTargetRequested;
		public event Action ChartClearSelectionRequested;

		/// <summary>+1 is the next longer range scale, −1 the next shorter.</summary>
		public event Action<int> RadarRangeRequested;
		public event Action RadarOrientationRequested;
		public event Action RadarPaletteRequested;

		/// <summary>+1 turns the EBL clockwise one step, −1 anticlockwise.</summary>
		public event Action<int> RadarEblRequested;

		/// <summary>+1 moves the VRM out one step, −1 in.</summary>
		public event Action<int> RadarVrmRequested;
		public event Action<int> RadarGainRequested;
		public event Action<int> RadarSeaRequested;

		public InputActionMap Map => _map;

		public bool Enabled => _map.enabled;

		public void Enable() => _map.Enable();

		public void Disable() => _map.Disable();

		private void Raise(int index)
		{
			switch (index)
			{
				case 0: ChartplotterToggleRequested?.Invoke(); break;
				case 1: RadarToggleRequested?.Invoke(); break;
				case 2: ChartZoomRequested?.Invoke(1); break;
				case 3: ChartZoomRequested?.Invoke(-1); break;
				case 4: ChartOrientationRequested?.Invoke(); break;
				case 5: ChartCentreRequested?.Invoke(); break;
				case 6: ChartPanRequested?.Invoke(0, -1); break;
				case 7: ChartPanRequested?.Invoke(-1, 0); break;
				case 8: ChartPanRequested?.Invoke(0, 1); break;
				case 9: ChartPanRequested?.Invoke(1, 0); break;
				case 10: ChartPaletteRequested?.Invoke(); break;
				case 11: ChartNextTargetRequested?.Invoke(); break;
				case 12: ChartClearSelectionRequested?.Invoke(); break;
				case 13: RadarRangeRequested?.Invoke(1); break;
				case 14: RadarRangeRequested?.Invoke(-1); break;
				case 15: RadarOrientationRequested?.Invoke(); break;
				case 16: RadarPaletteRequested?.Invoke(); break;
				case 17: RadarEblRequested?.Invoke(-1); break;
				case 18: RadarEblRequested?.Invoke(1); break;
				case 19: RadarVrmRequested?.Invoke(-1); break;
				case 20: RadarVrmRequested?.Invoke(1); break;
				case 21: RadarGainRequested?.Invoke(-1); break;
				case 22: RadarGainRequested?.Invoke(1); break;
				case 23: RadarSeaRequested?.Invoke(-1); break;
				case 24: RadarSeaRequested?.Invoke(1); break;
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			for (var i = 0; i < _actions.Length; i++)
			{
				_actions[i].performed -= _handlers[i];
			}

			_map.Disable();
		}
	}
}
