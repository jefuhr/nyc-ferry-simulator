using System;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.Harbor
{
	/// <summary>One synchronous text-focus gate for the harbor's helm, camera, alarms and display keys.</summary>
	[DisallowMultipleComponent]
	public sealed class HarborTextEntryGate : MonoBehaviour
	{
		private WheelhouseView _view;
		public bool Held { get; private set; }
		public event Action<bool> Changed;

		public static HarborTextEntryGate For(WheelhouseUI ui)
		{
			if (ui == null) return null;
			var gate = ui.GetComponent<HarborTextEntryGate>();
			return gate != null ? gate : ui.gameObject.AddComponent<HarborTextEntryGate>();
		}
		public void Follow(WheelhouseView view)
		{
			if (ReferenceEquals(_view, view)) return;
			if (_view != null) _view.TextEntryChanged -= SetHeld;
			_view = view;
			SetHeld(false);
			if (_view != null) _view.TextEntryChanged += SetHeld;
		}
		private void SetHeld(bool held)
		{
			if (Held == held) return;
			Held = held;
			Changed?.Invoke(held);
		}
		private void OnDestroy() { Follow(null); Changed = null; }
	}
}
