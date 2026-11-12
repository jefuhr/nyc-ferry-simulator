using System;
using FerrySim.UI;
using FerrySim.UI.Displays;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.Harbor
{
	/// <summary>Forwards pointer input from the shared display picture to its existing skin softkeys. No second view or render.</summary>
	internal sealed class HarborHudDisplayInput : IDisposable
	{
		private Image _image;
		private PickingMode _originalPicking;
		private SkinnedDisplayView _display;
		private bool _allowed;
		private int _pressed = -1, _pointer;

		public void Follow(WheelhouseView view, SkinnedDisplayView display, string widget)
		{
			var image = (view?.HudLayer.Widget(widget) as ScreenWidget)?.Picture;
			if (!ReferenceEquals(image, _image))
			{
				Detach(); _image = image;
				if (image != null)
				{
					_originalPicking = image.pickingMode; image.pickingMode = PickingMode.Position;
					image.RegisterCallback<PointerDownEvent>(Down);
					image.RegisterCallback<PointerUpEvent>(Up);
					image.RegisterCallback<PointerCancelEvent>(Cancel);
					image.RegisterCallback<PointerCaptureOutEvent>(CaptureLost);
				}
			}
			if (!ReferenceEquals(_display, display))
			{
				CancelHeld();
				if (_display != null) _display.PageChanged -= PageChanged;
				_display = display;
				if (_display != null) _display.PageChanged += PageChanged;
			}
			_allowed = view != null && view.PanelVisible && !view.OptionsVisible && !view.PickerVisible && !view.DebriefVisible &&
				view.HudLayout.IsShown(widget) && display != null;
			if (!_allowed) CancelHeld();
		}

		private int Hit(Vector2 local)
		{
			if (_image == null || _display == null) return -1;
			var rect = _image.contentRect;
			var reference = _display.Skin.Reference;
			var scale = Mathf.Min(rect.width / reference.x, rect.height / reference.y);
			if (!(scale > 0)) return -1;
			var point = (local - rect.center) / scale + reference * 0.5f;
			for (var i = _display.Elements.Count - 1; i >= 0; i--)
				if (_display.Elements[i] is SoftkeyView && _display.Elements[i].OnPage && _display.Elements[i].Spec.Rect.Contains(point)) return i;
			return -1;
		}
		private void Down(PointerDownEvent evt)
		{
			if (!_allowed || evt.button != 0 || _pressed >= 0) return;
			_pressed = Hit(evt.localPosition);
			if (_pressed < 0) return;
			_pointer = evt.pointerId; _display.PressSoftkey(_pressed); _image.CapturePointer(_pointer); evt.StopPropagation();
		}
		private void Up(PointerUpEvent evt)
		{
			if (_pressed < 0 || evt.pointerId != _pointer) return;
			var pressed = _pressed; _pressed = -1;
			if (_allowed && Hit(evt.localPosition) == pressed) _display.ReleaseSoftkey(pressed);
			else _display.CancelSoftkey(pressed);
			_image.ReleasePointer(_pointer); evt.StopPropagation();
		}
		private void PageChanged(string page)
		{
			if (_pressed >= 0 && !_display.Elements[_pressed].OnPage) CancelHeld();
		}
		private void Cancel(PointerCancelEvent evt) => CancelHeld();
		private void CaptureLost(PointerCaptureOutEvent evt) => CancelHeld();
		private void CancelHeld()
		{
			if (_pressed < 0) return;
			var pressed = _pressed; _pressed = -1; _display?.CancelSoftkey(pressed);
			if (_image != null && _image.HasPointerCapture(_pointer)) _image.ReleasePointer(_pointer);
		}
		private void Detach()
		{
			CancelHeld();
			if (_image == null) return;
			_image.UnregisterCallback<PointerDownEvent>(Down); _image.UnregisterCallback<PointerUpEvent>(Up);
			_image.UnregisterCallback<PointerCancelEvent>(Cancel); _image.UnregisterCallback<PointerCaptureOutEvent>(CaptureLost);
			_image.pickingMode = _originalPicking; _image = null;
		}
		public void Dispose()
		{
			Detach();
			if (_display != null) _display.PageChanged -= PageChanged;
			_display = null;
		}
	}
}
