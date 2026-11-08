using System;
using UnityEngine.InputSystem;

namespace FerrySim.Controls
{
	/// <summary>Session-only audio settings on an independent map, so holding the helm never traps a muted player.</summary>
	public sealed class AudioInputMapper : IDisposable
	{
		private readonly InputActionMap _map;
		private readonly InputAction _mute;
		public AudioInputMapper(InputActionAsset actions)
		{
			_map = actions.FindActionMap("Audio", true);
			_mute = _map.FindAction("Mute", true);
			_mute.performed += OnMute;
		}
		public event Action MuteRequested;
		public bool Enabled => _map.enabled;
		public void Enable() => _map.Enable();
		public void Disable() => _map.Disable();
		private void OnMute(InputAction.CallbackContext context) => MuteRequested?.Invoke();
		public void Dispose() { _mute.performed -= OnMute; _map.Disable(); }
	}
}
