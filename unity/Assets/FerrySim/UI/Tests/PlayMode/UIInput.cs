using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI.Tests.PlayMode
{
	/// <summary>
	/// Sends the events a mouse and a keyboard send, through the rendered panel. A click is a pointer down and up at
	/// the element's centre, picked by position as a real click is, so an element that ignores picking, or one under
	/// another, does not receive it. Keys go to the focused element and bubble up as real ones do.
	/// </summary>
	internal static class UIInput
	{
		public static void Click(VisualElement element)
		{
			var panel = element.panel;
			Assert(panel != null, element.name + " is not in a panel");
			var position = element.worldBound.center;
			var down = new Event { type = EventType.MouseDown, mousePosition = position, button = 0, clickCount = 1 };
			using (var evt = PointerDownEvent.GetPooled(down))
			{
				panel.visualTree.SendEvent(evt);
			}

			var up = new Event { type = EventType.MouseUp, mousePosition = position, button = 0, clickCount = 1 };
			using (var evt = PointerUpEvent.GetPooled(up))
			{
				panel.visualTree.SendEvent(evt);
			}
		}

		/// <summary>A key press, to whatever has the focus in <paramref name="anyElement"/>'s panel.</summary>
		public static void Key(VisualElement anyElement, KeyCode key)
		{
			var target = Focused(anyElement);
			using (var evt = KeyDownEvent.GetPooled('\0', key, EventModifiers.None))
			{
				target.SendEvent(evt);
			}

			using (var evt = KeyUpEvent.GetPooled('\0', key, EventModifiers.None))
			{
				target.SendEvent(evt);
			}
		}

		/// <summary>A navigation move, as the Input System's UI actions send for arrows, W, A, S, D and Tab.</summary>
		public static void Navigate(VisualElement anyElement, NavigationMoveEvent.Direction direction)
		{
			var target = Focused(anyElement);
			using (var evt = NavigationMoveEvent.GetPooled(direction))
			{
				target.SendEvent(evt);
			}
		}

		public static VisualElement Focused(VisualElement anyElement)
		{
			var focused = anyElement.panel?.focusController?.focusedElement as VisualElement;
			return focused ?? anyElement;
		}

		private static void Assert(bool condition, string message)
		{
			if (!condition)
			{
				throw new System.InvalidOperationException(message);
			}
		}
	}
}
