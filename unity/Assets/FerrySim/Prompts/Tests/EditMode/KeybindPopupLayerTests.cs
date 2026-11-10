using System.Collections.Generic;
using FerrySim.Controls;
using FerrySim.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using UnityEngine.UIElements;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace FerrySim.Prompts.Tests.EditMode
{
	/// <summary>
	/// The popup layer against a fake binding service and a stand-in interior of plain transforms, with no panel
	/// (sizes fall back to the 1280×720 reference and estimated plate sizes). Failure modes guarded: the wrong
	/// action's key shown, an unbound control showing an empty plate, stale text after a rebind or a scheme or
	/// interior change, text rebuilt every frame, popups showing in the wrong mode or context, labels for controls
	/// behind the camera, off screen or facing away, plates overlapping, more than 12 shown, labels jumping
	/// between frames with a still camera, and per-frame garbage.
	/// </summary>
	public class KeybindPopupLayerTests
	{
		private StandInInterior _interior;
		private FakeBindings _bindings;
		private KeybindPopupLayer _layer;

		[SetUp]
		public void SetUp()
		{
			_interior = new StandInInterior();
			_bindings = new FakeBindings();
			_layer = new KeybindPopupLayer { Mode = KeybindHintMode.Always };
		}

		[TearDown]
		public void TearDown()
		{
			_layer.SetBindings(null);
			_interior.Dispose();
		}

		private void Bind()
		{
			_layer.SetBindings(_bindings);
			_layer.SetControls(_interior.Root.transform, _interior.Map());
		}

		private KeybindPopupState State(string mount)
		{
			Assert.That(_layer.TryGetState(mount, out var state), Is.True, mount + " has a popup");
			return state;
		}

		[Test]
		public void EachControl_ShowsTheFirstActionBoundInTheScheme_AndItsLabel()
		{
			_bindings.Set(ActionIds.PortLeverStep, "Keyboard", "Q / A").Set(ActionIds.PortLever, "Keyboard", "X")
				.Set(ActionIds.Horn, "Keyboard", "H").Set(ActionIds.NfuJog, "Keyboard", "");
			_interior.Add("PortLever", new Vector3(-0.3f, 0f, 1f), "Port engine", ActionIds.PortLeverStep, ActionIds.PortLever);
			_interior.Add("PortNFU", new Vector3(0f, -0.2f, 1f), "Jog lever", ActionIds.NfuJog, ActionIds.Horn);
			_interior.Add("HornButton", new Vector3(0.3f, 0f, 1f), "Horn", ActionIds.Horn);
			Bind();
			_layer.Refresh(_interior.Camera);

			Assert.That(State("PortLever").KeyText, Is.EqualTo("Q / A"), "the first action wins when it is bound");
			Assert.That(State("PortLever").Caption, Is.EqualTo("Port engine"));
			Assert.That(State("PortNFU").KeyText, Is.EqualTo("H"), "an unbound first action is skipped for the next bound one");
			Assert.That(State("HornButton").KeyText, Is.EqualTo("H"));
			Assert.That(State("HornButton").Caption, Is.EqualTo("Horn"));
			Assert.That(_layer.VisibleCount, Is.EqualTo(3));
		}

		[Test]
		public void AControlWithoutActions_HasNoPopup_AndOneWithNoBoundAction_ShowsNothing()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			_interior.Add("HornButton", new Vector3(0.3f, 0f, 1f), "Horn", ActionIds.Horn);
			_interior.Add("AlarmSilence", new Vector3(-0.3f, 0f, 1f), "Silence alarms", ActionIds.AlarmSilence);
			_interior.Add("SteeringAlarmLamp2", new Vector3(0f, 0.3f, 1f), null);
			Bind();
			_layer.Refresh(_interior.Camera);

			Assert.That(_layer.ControlCount, Is.EqualTo(2), "only controls with actions get a popup");
			Assert.That(_layer.TryGetState("SteeringAlarmLamp2", out _), Is.False);
			Assert.That(State("AlarmSilence").Visible, Is.False, "no bound action: nothing at all, not an empty plate");
			Assert.That(State("AlarmSilence").KeyText, Is.Empty);
			Assert.That(State("HornButton").Visible, Is.True);
		}

		[Test]
		public void ARebind_ChangesTheTextAtOnce_AndUnbindingHidesIt()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			_interior.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn", ActionIds.Horn);
			Bind();
			_layer.Refresh(_interior.Camera);
			Assert.That(State("HornButton").KeyText, Is.EqualTo("H"));

			_bindings.Set(ActionIds.Horn, "Keyboard", "F11", raise: true);
			Assert.That(State("HornButton").KeyText, Is.EqualTo("F11"), "on BindingsChanged, before the next frame");
			_layer.Refresh(_interior.Camera);
			Assert.That(State("HornButton").Visible, Is.True);

			_bindings.Set(ActionIds.Horn, "Keyboard", "", raise: true);
			_layer.Refresh(_interior.Camera);
			Assert.That(State("HornButton").Visible, Is.False);
		}

		[Test]
		public void TextIsRebuilt_OnlyOnBindingsSchemeOrInteriorChanges_NeverPerFrame()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H").Set(ActionIds.Horn, "Gamepad", "Button South");
			_interior.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn", ActionIds.Horn);
			Bind();
			var rebuilds = _layer.RebuildCount;
			var lookups = _bindings.DisplayStringCalls;
			for (var frame = 0; frame < 50; frame++)
			{
				_interior.Camera.transform.rotation = Quaternion.Euler(0f, frame * 0.2f, 0f);
				_layer.Refresh(_interior.Camera);
			}

			Assert.That(_layer.RebuildCount, Is.EqualTo(rebuilds), "frames do not rebuild text");
			Assert.That(_bindings.DisplayStringCalls, Is.EqualTo(lookups), "frames do not look bindings up");

			_layer.Scheme = KeybindSchemes.Keyboard;
			Assert.That(_layer.RebuildCount, Is.EqualTo(rebuilds), "setting the same scheme again is not a change");
			_layer.Scheme = KeybindSchemes.Gamepad;
			Assert.That(_layer.RebuildCount, Is.EqualTo(rebuilds + 1));
			Assert.That(State("HornButton").KeyText, Is.EqualTo("Button South"), "the gamepad's binding");

			_bindings.Raise();
			Assert.That(_layer.RebuildCount, Is.EqualTo(rebuilds + 2));

			using (var other = new StandInInterior())
			{
				other.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn (other class)", ActionIds.Horn);
				_layer.SetControls(other.Root.transform, other.Map());
				Assert.That(_layer.RebuildCount, Is.EqualTo(rebuilds + 3));
				Assert.That(State("HornButton").Caption, Is.EqualTo("Horn (other class)"), "the new interior's entries");
				_layer.SetControls(null, null);
			}
		}

		[Test]
		public void Modes_HoldAndSuppression_DecideWhetherAnythingShows()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			_interior.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn", ActionIds.Horn);
			Bind();

			void Expect(bool showing, string because)
			{
				_layer.Refresh(_interior.Camera);
				Assert.That(_layer.IsShowing, Is.EqualTo(showing), because);
				Assert.That(_layer.VisibleCount, Is.EqualTo(showing ? 1 : 0), because);
				Assert.That(State("HornButton").Visible, Is.EqualTo(showing), because);
				Assert.That(_layer.resolvedStyle.display == DisplayStyle.None || _layer.style.display == DisplayStyle.None, Is.EqualTo(!showing), because + " (layer display)");
			}

			_layer.Mode = KeybindHintMode.Always;
			Expect(true, "always");
			_layer.Mode = KeybindHintMode.Off;
			Expect(false, "off");
			_layer.Mode = KeybindHintMode.Hold;
			Expect(false, "hold, not held");
			_layer.HoldPressed = true;
			Expect(true, "hold, held");
			_layer.HoldPressed = false;
			Expect(false, "hold, released");

			_layer.Mode = KeybindHintMode.Always;
			_layer.Suppressed = true;
			Expect(false, "suppressed by the options menu or the picker");
			_layer.Suppressed = false;
			_layer.InWheelhouseView = false;
			Expect(false, "chase or free-fly view");
			_layer.InWheelhouseView = true;
			Expect(true, "back in the wheelhouse");
		}

		[Test]
		public void NoInterior_ShowsNothing()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			_interior.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn", ActionIds.Horn);
			_layer.SetBindings(_bindings);
			_layer.SetControls(null, null);
			_layer.Refresh(_interior.Camera);
			Assert.That(_layer.IsShowing, Is.False, "no control map");

			Bind();
			_layer.Refresh(_interior.Camera);
			Assert.That(_layer.IsShowing, Is.True);
			Object.DestroyImmediate(_interior.Root);
			_layer.Refresh(_interior.Camera);
			Assert.That(_layer.IsShowing, Is.False, "the interior was destroyed (a class switch)");
			Assert.That(_layer.VisibleCount, Is.Zero);

			_layer.Refresh(null);
			Assert.That(_layer.VisibleCount, Is.Zero, "no camera");
		}

		[Test]
		public void ControlsBehindTheCamera_OffScreen_OrFacingAway_AreHidden()
		{
			foreach (var action in new[] { ActionIds.Horn, ActionIds.AlarmAcknowledge, ActionIds.AlarmSilence, ActionIds.AlarmLampTest, ActionIds.NfuJog })
			{
				_bindings.Set(action, "Keyboard", "K");
			}

			_interior.Add("InView", new Vector3(0f, 0f, 1f), "In view", ActionIds.Horn);
			_interior.Add("Behind", new Vector3(0f, 0f, -1f), Vector3.forward, "Behind", ActionIds.AlarmAcknowledge);
			_interior.Add("OffRight", new Vector3(3f, 0f, 1f), "Off right", ActionIds.AlarmSilence);
			_interior.Add("OffBelow", new Vector3(0f, -2f, 1f), "Off below", ActionIds.AlarmLampTest);
			_interior.Add("FacingAway", new Vector3(0.4f, 0f, 1f), Vector3.forward, "Facing away", ActionIds.NfuJog);
			Bind();
			_layer.Refresh(_interior.Camera);

			Assert.That(State("InView").Visible, Is.True);
			Assert.That(State("Behind").Visible, Is.False, "behind the camera");
			Assert.That(State("OffRight").Visible, Is.False, "off the right edge");
			Assert.That(State("OffBelow").Visible, Is.False, "below the bottom edge");
			Assert.That(State("FacingAway").Visible, Is.False, "its face points away from the eye");

			_interior.Camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
			_layer.Refresh(_interior.Camera);
			Assert.That(State("InView").Visible, Is.False, "turned round, the control is behind");
			Assert.That(State("Behind").Visible, Is.True, "and the one behind is in front, facing the eye");
		}

		[Test]
		public void TheLabel_SitsJustAboveItsControl()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			_interior.Add("HornButton", new Vector3(0.2f, -0.1f, 1f), "Horn", ActionIds.Horn);
			Bind();
			_layer.Refresh(_interior.Camera);

			var state = State("HornButton");
			var expected = Project(_interior.Camera, new Vector3(0.2f, -0.1f, 1f));
			Assert.That(Vector2.Distance(state.Anchor, expected), Is.LessThan(0.5f), "anchor is the control's projection");
			Assert.That(state.Plate.center.x, Is.EqualTo(expected.x).Within(0.5f), "centred over the control");
			Assert.That(state.Plate.yMax, Is.LessThan(expected.y), "above it");
			Assert.That(expected.y - state.Plate.yMax, Is.LessThan(40f), "just above it");
		}

		[Test]
		public void NearTheTopEdge_TheLabelGoesBelowRatherThanCoveringTheControl()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			// 0.6 m up at 1 m is about 30 px below the top edge: no room above for a plate.
			_interior.Add("HornButton", new Vector3(0f, 0.6f, 1f), "Horn", ActionIds.Horn);
			Bind();
			_layer.Refresh(_interior.Camera);
			var state = State("HornButton");
			Assert.That(state.Visible, Is.True);
			Assert.That(state.Plate.yMin, Is.GreaterThan(state.Anchor.y), "below the control");
			Assert.That(state.Plate.yMin, Is.GreaterThanOrEqualTo(0f));
		}

		[Test]
		public void VisibleLabels_NeverOverlap_AndTheOneNearestTheCentreKeepsItsPlace()
		{
			// A tight cluster: 5 x 4 controls 6 cm apart at 1 m, centred on the view.
			var actions = new[] { ActionIds.Horn, ActionIds.AlarmAcknowledge, ActionIds.AlarmSilence, ActionIds.AlarmLampTest, ActionIds.NfuJog };
			foreach (var action in actions)
			{
				_bindings.Set(action, "Keyboard", "Key");
			}

			for (var row = 0; row < 4; row++)
			{
				for (var column = 0; column < 5; column++)
				{
					_interior.Add("Mount" + row + column, new Vector3((column - 2) * 0.06f, (row - 1.5f) * 0.06f, 1f), "Control " + row + column, actions[column]);
				}
			}

			_interior.Add("Centre", new Vector3(0f, 0f, 1f), "Centre", ActionIds.Horn);
			Bind();
			_layer.Refresh(_interior.Camera);

			var visible = Visible();
			Assert.That(visible.Count, Is.GreaterThan(1));
			Assert.That(visible.Count, Is.LessThanOrEqualTo(KeybindPopupLayer.MaxShown));
			AssertNoOverlap(visible);
			var centre = State("Centre");
			Assert.That(centre.Visible, Is.True, "the control at the view centre is always shown");
			Assert.That(centre.Plate.center.x, Is.EqualTo(centre.Anchor.x).Within(0.5f), "and keeps its own spot");
			Assert.That(centre.Plate.yMax, Is.LessThan(centre.Anchor.y));
		}

		[Test]
		public void AtMostTwelveAreShown_ChosenByNearnessToTheCentre()
		{
			var actions = new[] { ActionIds.Horn, ActionIds.AlarmAcknowledge, ActionIds.AlarmSilence, ActionIds.AlarmLampTest, ActionIds.NfuJog };
			foreach (var action in actions)
			{
				_bindings.Set(action, "Keyboard", "K");
			}

			// 20 controls on a 5 x 4 grid about 100 px by 85 px apart, too far apart to collide, off centre so no
			// two are the same distance from it (the 12th and 13th nearest differ by about 1.4 px).
			for (var i = 0; i < 20; i++)
			{
				var position = new Vector3((i % 5 - 2) * 0.18f + 0.037f, (i / 5 - 1.5f) * 0.15f - 0.023f, 1f);
				_interior.Add("Grid" + i, position, "G" + i, actions[i % actions.Length]);
			}

			Bind();
			_layer.Refresh(_interior.Camera);
			var visible = Visible();
			Assert.That(visible.Count, Is.EqualTo(KeybindPopupLayer.MaxShown), "the cap");
			AssertNoOverlap(visible);
			var centre = new Vector2(640f, 360f);
			var farthestShown = 0f;
			var nearestHidden = float.MaxValue;
			for (var i = 0; i < _layer.ControlCount; i++)
			{
				var state = _layer.GetState(i);
				var distance = Vector2.Distance(state.Anchor, centre);
				if (state.Visible)
				{
					farthestShown = Mathf.Max(farthestShown, distance);
				}
				else
				{
					nearestHidden = Mathf.Min(nearestHidden, distance);
				}
			}

			Assert.That(farthestShown, Is.LessThan(nearestHidden), "the twelve shown are the twelve nearest the centre");
		}

		[Test]
		public void WithAStillCamera_NothingMovesOrFlickers()
		{
			var actions = new[] { ActionIds.Horn, ActionIds.AlarmAcknowledge, ActionIds.AlarmSilence, ActionIds.AlarmLampTest, ActionIds.NfuJog };
			foreach (var action in actions)
			{
				_bindings.Set(action, "Keyboard", "Key");
			}

			for (var i = 0; i < 16; i++)
			{
				_interior.Add("M" + i, new Vector3((i % 4 - 1.5f) * 0.07f, (i / 4 - 1.5f) * 0.05f, 1f), "Control " + i, actions[i % actions.Length]);
			}

			Bind();
			_interior.Camera.transform.rotation = Quaternion.Euler(3f, -2f, 0f);
			_layer.Refresh(_interior.Camera);
			var first = Snapshot();
			for (var frame = 0; frame < 30; frame++)
			{
				_layer.Refresh(_interior.Camera);
				Assert.That(Snapshot(), Is.EqualTo(first), "frame " + frame);
			}
		}

		[Test]
		public void LabelsNearTheViewCentre_AreStrongest()
		{
			_bindings.Set(ActionIds.Horn, "Keyboard", "H").Set(ActionIds.AlarmSilence, "Keyboard", "\\");
			_interior.Add("Centre", new Vector3(0f, 0f, 1f), "Centre", ActionIds.Horn);
			_interior.Add("Edge", new Vector3(0.95f, -0.45f, 1f), "Edge", ActionIds.AlarmSilence);
			_interior.Add("Far", new Vector3(0f, -0.4f, 5f), "Far", ActionIds.Horn);
			Bind();
			_layer.Refresh(_interior.Camera);
			Assert.That(State("Centre").Scale, Is.EqualTo(1f).Within(1e-4f));
			Assert.That(State("Edge").Visible, Is.True);
			Assert.That(State("Edge").Scale, Is.LessThan(State("Centre").Scale), "weaker towards the edge of the view");
			Assert.That(State("Far").Scale, Is.LessThan(State("Centre").Scale), "weaker further away");
		}

		[Test]
		public void TheLayer_NeverTakesFocusOrPointerInput()
		{
			Assert.That(_layer.pickingMode, Is.EqualTo(PickingMode.Ignore));
			Assert.That(_layer.focusable, Is.False);
			_bindings.Set(ActionIds.Horn, "Keyboard", "H");
			_interior.Add("HornButton", new Vector3(0f, 0f, 1f), "Horn", ActionIds.Horn);
			Bind();
			_layer.Query<VisualElement>().ForEach(element =>
			{
				Assert.That(element.pickingMode, Is.EqualTo(PickingMode.Ignore), element.name);
				Assert.That(element.focusable, Is.False, element.name);
			});
			Assert.That(_layer.Query<VisualElement>().ToList().Count, Is.GreaterThan(KeybindPopupLayer.PoolSize), "a fixed pool is built up front");
		}

		[Test]
		public void SteadyState_AllocatesNothing_WithTheCameraTurning()
		{
			var actions = new[] { ActionIds.Horn, ActionIds.AlarmAcknowledge, ActionIds.AlarmSilence, ActionIds.AlarmLampTest, ActionIds.NfuJog };
			foreach (var action in actions)
			{
				_bindings.Set(action, "Keyboard", "Key");
			}

			for (var i = 0; i < 16; i++)
			{
				_interior.Add("M" + i, new Vector3((i % 4 - 1.5f) * 0.12f, (i / 4 - 1.5f) * 0.08f, 1f), "Control " + i, actions[i % actions.Length]);
			}

			Bind();
			var camera = _interior.Camera;
			var frame = 0;
			void Turn(int frames)
			{
				for (var i = 0; i < frames; i++, frame++)
				{
					camera.transform.rotation = Quaternion.Euler(Mathf.Sin(frame * 0.05f) * 12f, Mathf.Sin(frame * 0.031f) * 30f, 0f);
					_layer.Refresh(camera);
				}
			}

			Turn(400);
			Assert.That(() => Turn(400), Is.Not.AllocatingGCMemory());
		}

		private static Vector2 Project(Camera camera, Vector3 world)
		{
			var viewport = camera.WorldToViewportPoint(world);
			return new Vector2(viewport.x * 1280f, (1f - viewport.y) * 720f);
		}

		private List<KeybindPopupState> Visible()
		{
			var visible = new List<KeybindPopupState>();
			for (var i = 0; i < _layer.ControlCount; i++)
			{
				var state = _layer.GetState(i);
				if (state.Visible)
				{
					visible.Add(state);
				}
			}

			Assert.That(_layer.VisibleCount, Is.EqualTo(visible.Count));
			return visible;
		}

		private static void AssertNoOverlap(List<KeybindPopupState> visible)
		{
			for (var a = 0; a < visible.Count; a++)
			{
				for (var b = a + 1; b < visible.Count; b++)
				{
					Assert.That(visible[a].Plate.Overlaps(visible[b].Plate), Is.False,
						$"{visible[a].MountId} {visible[a].Plate} overlaps {visible[b].MountId} {visible[b].Plate}");
				}
			}
		}

		private string Snapshot()
		{
			var text = new System.Text.StringBuilder();
			for (var i = 0; i < _layer.ControlCount; i++)
			{
				var state = _layer.GetState(i);
				text.Append(state.MountId).Append(state.Visible ? " shown " : " hidden ").Append(state.Plate.ToString("F3")).Append('\n');
			}

			return text.ToString();
		}
	}
}
