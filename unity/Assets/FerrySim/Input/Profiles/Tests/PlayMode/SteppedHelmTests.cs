using System.IO;
using FerrySim.Controls;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Profiles.Tests
{
	/// <summary>Guards lost held orders, duplicate presses, runaway repeats, reset leakage and axes quantized into detents.</summary>
	public class SteppedHelmTests : InputTestFixture
	{
		private InputActionAsset _actions;
		private HelmInputMapper _mapper;

		public override void Setup()
		{
			base.Setup();
			_actions = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_mapper = new HelmInputMapper(_actions);
			_mapper.Enable();
		}

		public override void TearDown()
		{
			_mapper.Dispose();
			Object.DestroyImmediate(_actions);
			base.TearDown();
		}

		[Test]
		public void Detents_HoldOnRelease_Clamp_AndReturnToExactMidships()
		{
			var keys = InputSystem.AddDevice<Keyboard>();
			for (var i = 1; i <= 6; i++)
			{
				Press(keys.rightArrowKey); Release(keys.rightArrowKey);
				Assert.That(_mapper.Helm, Is.EqualTo(System.Math.Min(i, 4) * .25));
			}
			for (var i = 1; i <= 10; i++)
			{
				Press(keys.leftArrowKey); Release(keys.leftArrowKey);
				Assert.That(_mapper.Helm, Is.EqualTo(System.Math.Max(4 - i, -4) * .25));
			}
			Press(keys.mKey); Release(keys.mKey);
			Assert.That(_mapper.Helm, Is.Zero);
		}

		[Test]
		public void HeldKey_RepeatsAfterDelay_StopsOnRelease_AndResetCancelsRepeat()
		{
			var keys = InputSystem.AddDevice<Keyboard>();
			Press(keys.rightArrowKey);
			_mapper.AdvanceHelmRepeat(.39);
			Assert.That(_mapper.Helm, Is.EqualTo(.25));
			_mapper.AdvanceHelmRepeat(.02);
			Assert.That(_mapper.Helm, Is.EqualTo(.5));
			_mapper.AdvanceHelmRepeat(.2);
			Assert.That(_mapper.Helm, Is.EqualTo(.75));
			Release(keys.rightArrowKey);
			_mapper.AdvanceHelmRepeat(1);
			Assert.That(_mapper.Helm, Is.EqualTo(.75));
			Press(keys.leftArrowKey);
			_mapper.ResyncWithDevices();
			_mapper.AdvanceHelmRepeat(1);
			Assert.That(_mapper.Helm, Is.Zero);
		}

		[Test]
		public void ReleasingAnOpposingKey_DoesNotIssueANewOrder()
		{
			var keys = InputSystem.AddDevice<Keyboard>();
			Press(keys.rightArrowKey);
			Press(keys.leftArrowKey);
			var order = _mapper.Helm;
			Release(keys.leftArrowKey);
			Assert.That(_mapper.Helm, Is.EqualTo(order));
			Release(keys.rightArrowKey);
			Assert.That(_mapper.Helm, Is.EqualTo(order));
		}

		[Test]
		public void Shoulders_StepAndHold_WhileAbsoluteAxesRemainContinuous()
		{
			var pad = InputSystem.AddDevice<Gamepad>();
			Press(pad.rightShoulder); Release(pad.rightShoulder);
			Assert.That(_mapper.Helm, Is.EqualTo(.25));
			var stick = InputSystem.AddDevice<Joystick>();
			Set(stick.stick, new Vector2(.6f, 0));
			var expected = stick.stick.x.ReadValue();
			Assert.That(_mapper.Helm, Is.EqualTo(expected).Within(1e-6));
			_mapper.ResyncWithDevices();
			Assert.That(_mapper.Helm, Is.EqualTo(expected).Within(1e-6));
			Set(stick.stick, Vector2.zero);
			Assert.That(_mapper.Helm, Is.Zero);
		}
	}
}
