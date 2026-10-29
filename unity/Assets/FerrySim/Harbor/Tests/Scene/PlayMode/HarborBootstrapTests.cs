using System;
using System.Collections;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FerrySim.Harbor.Tests.PlayMode
{
	public class HarborBootstrapTests
	{
		[UnityTest]
		public IEnumerator UnboundSceneHasOneVisibleReasonAndNoSimulation()
		{
			using (var rig = new HarborTestRig(false))
			{
				yield return null;
				Assert.That(rig.Bootstrap.IsHeld, Is.True);
				Assert.That(rig.Runner.IsRunActive, Is.False);
				Assert.That(rig.Bootstrap.Problem, Does.Contain("data/processed/harbor/runtime"));
				var label = rig.Bootstrap.WheelhouseUI.Document.rootVisualElement.Q<Label>("harbor-status");
				Assert.That(label, Is.Not.Null);
				Assert.That(label.text, Does.Contain("vessel held"));
				Assert.That(label.resolvedStyle.display, Is.Not.EqualTo(DisplayStyle.None));
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator InitialViewHoldsWithoutChangingPoseOrStep_ThenStreamingCannotPauseIt()
		{
			using (var rig = new HarborTestRig())
			{
				Assert.That(rig.Bootstrap.TryStart(rig.Scenario(), out var problem), Is.True, problem);
				var position = rig.Runner.State;
				for (var i = 0; i < 15; i++) yield return null;
				Assert.That(rig.Runner.Driver.StepCount, Is.Zero);
				Assert.That(rig.Runner.State.North, Is.EqualTo(position.North));
				Assert.That(rig.Runner.State.East, Is.EqualTo(position.East));
				Assert.That(rig.Runner.IsRunActive, Is.True, "the hold must not disable/end the runner");
				rig.Layer.LoadProgress = 100;
				for (var i = 0; i < 12; i++) yield return null;
				Assert.That(rig.Bootstrap.ViewState, Is.EqualTo(HarborViewState.Ready));
				yield return new WaitForSecondsRealtime(0.04f);
				Assert.That(rig.Runner.Driver.StepCount, Is.GreaterThan(0));
				rig.Layer.LoadProgress = 0;
				var before = rig.Runner.Driver.StepCount;
				yield return new WaitForSecondsRealtime(0.04f);
				Assert.That(rig.Runner.IsPaused, Is.False);
				Assert.That(rig.Runner.Driver.StepCount, Is.GreaterThan(before));
				Assert.That(SimulationClock.FixedTimeStep, Is.EqualTo(0.01));
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator PickerClosingBeforeOrAfterReadinessPreservesTheRightPauseOwner()
		{
			using (var rig = new HarborTestRig())
			{
				yield return null;
				Assert.That(rig.Bootstrap.TryStart(rig.Scenario(), out _), Is.True);
				var view = rig.Bootstrap.WheelhouseUI.View;
				view.PickerVisible = true;
				view.PickerVisible = false;
				Assert.That(rig.Runner.IsPaused, Is.True, "closing a picker cannot release a loading hold");
				view.PickerVisible = true;
				rig.Layer.LoadProgress = 100;
				for (var i = 0; i < 12; i++) yield return null;
				Assert.That(rig.Bootstrap.ViewState, Is.EqualTo(HarborViewState.Ready));
				Assert.That(rig.Runner.IsPaused, Is.True);
				view.PickerVisible = false;
				Assert.That(rig.Runner.IsPaused, Is.False, "ready start resumes when the picker finally closes");
				rig.Runner.Pause();
				view.PickerVisible = true;
				view.PickerVisible = false;
				Assert.That(rig.Runner.IsPaused, Is.True, "the user's earlier pause remains theirs");
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator TileFailureNamesMissingContentAndNeverSteps()
		{
			using (var rig = new HarborTestRig())
			{
				rig.Layer.Failure = "data/processed/harbor/3dtiles/tiles/tile_0_0/c0_0.glb missing";
				Assert.That(rig.Bootstrap.TryStart(rig.Scenario(), out _), Is.True);
				yield return null;
				Assert.That(rig.Bootstrap.ViewState, Is.EqualTo(HarborViewState.Failed));
				Assert.That(rig.Bootstrap.Problem, Does.Contain(rig.Layer.Failure));
				Assert.That(rig.Runner.Driver.StepCount, Is.Zero);
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator MissingRuntimePackIsRefusedBeforeMutatingTheRunner()
		{
			using (var rig = new HarborTestRig(false))
			{
				rig.Services.RuntimePack = null;
				Assert.That(rig.Bootstrap.Connect(rig.Services, out var problem), Is.False);
				Assert.That(problem, Does.Contain("data/processed/harbor/runtime"));
				Assert.That(rig.Runner.IsInitialized, Is.False);
				yield return null;
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator ConnectingAfterTheFirstFrameOpensThePicker_AndARepairedViewCanRetry()
		{
			Shader.SetGlobalTexture("_FerrySimWaterNormals", null);
			using (var rig = new HarborTestRig(false))
			{
				yield return null;
				Assert.That(rig.Bootstrap.Connect(rig.Services, out var problem), Is.True, problem);
				Assert.That(Shader.GetGlobalTexture("_FerrySimWaterNormals"), Is.SameAs(WaterNormalMap.Get()), "a fresh harbor needs no prior slice to bind water shading");
				yield return null;
				Assert.That(rig.Bootstrap.WheelhouseUI.View.PickerVisible, Is.True);
				rig.Layer.Failure = "fixture missing tile";
				Assert.That(rig.Bootstrap.TryStart(rig.Scenario(), out problem), Is.True, problem);
				yield return null;
				Assert.That(rig.Bootstrap.ViewState, Is.EqualTo(HarborViewState.Failed));
				rig.Layer.Failure = null;
				rig.Layer.LoadProgress = 100;
				Assert.That(rig.Bootstrap.TryStart(rig.Scenario(), out problem), Is.True, problem);
				rig.Bootstrap.WheelhouseUI.View.PickerVisible = false;
				for (var i = 0; i < 12; i++) yield return null;
				Assert.That(rig.Bootstrap.ViewState, Is.EqualTo(HarborViewState.Ready));
				Assert.That(rig.Runner.IsPaused, Is.False);
			}
			yield return null;
		}

		[UnityTest]
		public IEnumerator WarmRefreshAllocatesNothing_WithPositiveControl()
		{
			using (var rig = new HarborTestRig())
			{
				Assert.That(rig.Bootstrap.TryStart(rig.Scenario(), out _), Is.True);
				for (var i = 0; i < 12; i++) yield return null;
				var recorder = Recorder.Get("GC.Alloc");
				recorder.enabled = false;
				recorder.FilterToCurrentThread();
				var chars = new char[8];
				recorder.enabled = true;
				var allocated = new string(chars);
				recorder.enabled = false;
				GC.KeepAlive(allocated);
				Assert.That(recorder.sampleBlockCount, Is.GreaterThan(0), "positive control");
				long total = 0;
				for (var i = 0; i < 120; i++)
				{
					recorder.enabled = true;
					rig.Bootstrap.RefreshControls();
					rig.Bootstrap.RefreshView(Time.realtimeSinceStartupAsDouble);
					recorder.enabled = false;
					total += recorder.sampleBlockCount;
					yield return null;
				}
				recorder.CollectFromAllThreads();
				Assert.That(total, Is.Zero, "bootstrap refresh during the initial view hold");
			}
			yield return null;
		}
	}
}
