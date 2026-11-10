using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests.PlayMode
{
	// Missing actions, wrong signs, opposing keys and a held jog after map disable are safety failures.
	public class JogInputTests : InputTestFixture
	{
		private InputActionAsset _asset;
		private HelmInputMapper _mapper;
		public override void Setup()
		{
			base.Setup();
			_asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			_mapper = new HelmInputMapper(_asset);
			_mapper.Enable();
		}
		public override void TearDown()
		{
			_mapper.Dispose(); Object.DestroyImmediate(_asset); base.TearDown();
		}
		[Test]
		public void KeyboardJogHasThreeStatesAndReleasesImmediately()
		{
			var keyboard = InputSystem.AddDevice<Keyboard>();
			Assert.That(_asset.FindAction("Helm/NfuJog"), Is.Not.Null);
			Press(keyboard.f6Key); Assert.That(_mapper.Current.Jog, Is.EqualTo(-1));
			Press(keyboard.f7Key); Assert.That(_mapper.Current.Jog, Is.Zero);
			Release(keyboard.f6Key); Assert.That(_mapper.Current.Jog, Is.EqualTo(1));
			Release(keyboard.f7Key); Assert.That(_mapper.Current.Jog, Is.Zero);
			Press(keyboard.numpad4Key); Assert.That(_mapper.Current.Jog, Is.EqualTo(-1));
			_mapper.Disable(); Assert.That(_mapper.Current.Jog, Is.Zero);
		}
		[Test]
		public void DpadJogIsIndependentOfTheExistingLeverSteps()
		{
			var gamepad = InputSystem.AddDevice<Gamepad>();
			Assert.That(_asset.FindAction("Helm/NfuJog"), Is.Not.Null);
			Press(gamepad.dpad.right); Assert.That(_mapper.Current.Jog, Is.EqualTo(1));
			Assert.That(_mapper.Current.PortLever, Is.Zero);
			Release(gamepad.dpad.right); Assert.That(_mapper.Current.Jog, Is.Zero);
		}
	}
}
