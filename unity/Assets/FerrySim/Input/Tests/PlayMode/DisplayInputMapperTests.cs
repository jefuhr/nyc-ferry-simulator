using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Tests.PlayMode
{
	/// <summary>
	/// The Displays map driven through the Input System's test fixture with a simulated keyboard. Failure modes: a key
	/// raising the wrong event or the wrong sign; a press raising an event while the map is disabled; a display key
	/// reaching the Helm map.
	/// </summary>
	public class DisplayInputMapperTests : InputTestFixture
	{
		private InputActionAsset _actions;
		private DisplayInputMapper _mapper;
		private HelmInputMapper _helm;
		private Keyboard _keyboard;
		private readonly List<string> _events = new List<string>();

		private static string ActionsPath => Path.Combine(Application.dataPath, "FerrySim", "Input", "FerrySimControls.inputactions");

		public override void Setup()
		{
			base.Setup();
			_keyboard = InputSystem.AddDevice<Keyboard>();
			_actions = InputActionAsset.FromJson(File.ReadAllText(ActionsPath));
			_mapper = new DisplayInputMapper(_actions);
			_helm = new HelmInputMapper(_actions);
			_events.Clear();
			_mapper.ChartplotterToggleRequested += () => _events.Add("chart-toggle");
			_mapper.RadarToggleRequested += () => _events.Add("radar-toggle");
			_mapper.ChartZoomRequested += d => _events.Add("chart-zoom " + d);
			_mapper.ChartOrientationRequested += () => _events.Add("chart-orientation");
			_mapper.ChartCentreRequested += () => _events.Add("chart-centre");
			_mapper.ChartPanRequested += (x, y) => _events.Add("chart-pan " + x + " " + y);
			_mapper.ChartPaletteRequested += () => _events.Add("chart-palette");
			_mapper.ChartNextTargetRequested += () => _events.Add("chart-next-target");
			_mapper.ChartClearSelectionRequested += () => _events.Add("chart-clear-selection");
			_mapper.RadarRangeRequested += d => _events.Add("radar-range " + d);
			_mapper.RadarOrientationRequested += () => _events.Add("radar-orientation");
			_mapper.RadarPaletteRequested += () => _events.Add("radar-palette");
			_mapper.RadarEblRequested += d => _events.Add("radar-ebl " + d);
			_mapper.RadarVrmRequested += d => _events.Add("radar-vrm " + d);
			_mapper.RadarGainRequested += d => _events.Add("radar-gain " + d);
			_mapper.RadarSeaRequested += d => _events.Add("radar-sea " + d);
			_helm.PauseRequested += () => _events.Add("helm-pause");
			_helm.NewRunRequested += () => _events.Add("helm-new-run");
			_helm.ResetRequested += () => _events.Add("helm-reset");
			_helm.CameraCycleRequested += () => _events.Add("helm-camera");
			_helm.Enable();
			_mapper.Enable();
		}

		public override void TearDown()
		{
			_mapper.Dispose();
			_helm.Dispose();
			Object.DestroyImmediate(_actions);
			base.TearDown();
		}

		[TestCase(Key.Digit1, "chart-toggle")]
		[TestCase(Key.Digit2, "radar-toggle")]
		[TestCase(Key.Equals, "chart-zoom 1")]
		[TestCase(Key.Minus, "chart-zoom -1")]
		[TestCase(Key.O, "chart-orientation")]
		[TestCase(Key.U, "chart-centre")]
		[TestCase(Key.I, "chart-pan 0 -1")]
		[TestCase(Key.J, "chart-pan -1 0")]
		[TestCase(Key.K, "chart-pan 0 1")]
		[TestCase(Key.L, "chart-pan 1 0")]
		[TestCase(Key.F, "chart-palette")]
		[TestCase(Key.T, "chart-next-target")]
		[TestCase(Key.Y, "chart-clear-selection")]
		[TestCase(Key.RightBracket, "radar-range 1")]
		[TestCase(Key.LeftBracket, "radar-range -1")]
		[TestCase(Key.Z, "radar-orientation")]
		[TestCase(Key.X, "radar-palette")]
		[TestCase(Key.Comma, "radar-ebl -1")]
		[TestCase(Key.Period, "radar-ebl 1")]
		[TestCase(Key.Semicolon, "radar-vrm -1")]
		[TestCase(Key.Quote, "radar-vrm 1")]
		[TestCase(Key.Digit3, "radar-gain -1")]
		[TestCase(Key.Digit4, "radar-gain 1")]
		[TestCase(Key.Digit5, "radar-sea -1")]
		[TestCase(Key.Digit6, "radar-sea 1")]
		public void EachDisplayKeyRaisesExactlyItsEventAndNothingOnTheHelm(Key key, string expected)
		{
			Press(_keyboard[key]);
			Release(_keyboard[key]);
			Assert.That(_events, Is.EqualTo(new[] { expected }));
		}

		[Test]
		public void ADisabledDisplaysMapRaisesNothing()
		{
			_mapper.Disable();
			Press(_keyboard.digit1Key);
			Release(_keyboard.digit1Key);
			Press(_keyboard.rightBracketKey);
			Release(_keyboard.rightBracketKey);
			Assert.That(_events, Is.Empty);
			_mapper.Enable();
			Press(_keyboard.digit2Key);
			Release(_keyboard.digit2Key);
			Assert.That(_events, Is.EqualTo(new[] { "radar-toggle" }));
		}

		[Test]
		public void HelmKeysStillReachOnlyTheHelm()
		{
			Press(_keyboard.pKey);
			Release(_keyboard.pKey);
			Press(_keyboard.nKey);
			Release(_keyboard.nKey);
			Press(_keyboard.cKey);
			Release(_keyboard.cKey);
			Assert.That(_events, Is.EqualTo(new[] { "helm-pause", "helm-new-run", "helm-camera" }));
		}
	}
}
