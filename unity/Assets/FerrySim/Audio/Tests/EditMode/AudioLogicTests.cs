using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Controls;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Audio.Tests
{
	public class AudioLogicTests
	{
		private static EngineMix Mix(double speed, double throttle = 1, GearState gear = GearState.Ahead, double shaft = 50) =>
			EngineSoundMapping.Evaluate(speed, 60, 220, shaft, 3, throttle, gear);

		[Test]
		public void EngineLayersHaveBoundedContinuousWeightsAndMonotonicPitch()
		{
			var previous = Mix(0);
			for (var speed = 0.1; speed <= 300; speed += .1)
			{
				var mix = Mix(speed);
				Assert.That(mix.IdleGain + mix.MidGain + mix.HighGain, Is.EqualTo(1).Within(1e-12));
				Assert.That(mix.IdleGain, Is.InRange(0.0, 1.0));
				Assert.That(mix.MidGain, Is.InRange(0.0, 1.0));
				Assert.That(mix.HighGain, Is.InRange(0.0, 1.0));
				Assert.That(mix.IdlePitch, Is.GreaterThanOrEqualTo(previous.IdlePitch));
				Assert.That(mix.MidPitch, Is.GreaterThanOrEqualTo(previous.MidPitch));
				Assert.That(mix.HighPitch, Is.GreaterThanOrEqualTo(previous.HighPitch));
				previous = mix;
			}
			foreach (var boundary in new[] { 60.0, 140.0, 220.0 })
			{
				var before = Mix(boundary - 1e-6); var after = Mix(boundary + 1e-6);
				Assert.That(after.IdleGain, Is.EqualTo(before.IdleGain).Within(1e-6));
				Assert.That(after.MidGain, Is.EqualTo(before.MidGain).Within(1e-6));
				Assert.That(after.HighGain, Is.EqualTo(before.HighGain).Within(1e-6));
			}
		}

		[Test]
		public void LoadUsesGearThrottleAndShaftAndRejectsNonfiniteReadings()
		{
			Assert.That(Mix(140).LowGain, Is.GreaterThan(Mix(140, gear: GearState.Neutral).LowGain));
			Assert.That(Mix(140).Gain, Is.GreaterThan(Mix(140, 0, shaft: 0).Gain));
			Assert.That(Mix(140, shaft: -50).PropGain, Is.EqualTo(Mix(140).PropGain));
			Assert.That(Mix(double.NaN).Gain, Is.Zero);
			Assert.That(Mix(double.PositiveInfinity).Gain, Is.Zero);
		}

		[Test]
		public void EveryGearTransitionQueuesOnceIncludingChangesBetweenFrames()
		{
			var clutch = new ClutchEvents(); clutch.Reset(GearState.Neutral);
			clutch.Observe(GearState.Shifting); clutch.Observe(GearState.Ahead); clutch.Observe(GearState.Ahead);
			clutch.Observe(GearState.Shifting); clutch.Observe(GearState.Neutral);
			foreach (var expected in new[] { ClutchSound.Disengage, ClutchSound.Engage, ClutchSound.Disengage, ClutchSound.Disengage })
			{
				Assert.That(clutch.TryTake(out var sound), Is.True); Assert.That(sound, Is.EqualTo(expected));
			}
			Assert.That(clutch.TryTake(out _), Is.False);
			clutch.Observe(GearState.Astern); clutch.Reset(GearState.Astern);
			Assert.That(clutch.TryTake(out _), Is.False, "a new run clears pending sounds");
		}

		[Test]
		public void HeldHornAttacksSustainsReleasesAndRetriggersContinuously()
		{
			var horn = new HornEnvelope(); horn.Hold(true);
			horn.Advance(.04); Assert.That(horn.Gain, Is.EqualTo(.5).Within(1e-9));
			horn.Advance(.04); Assert.That(horn.State, Is.EqualTo(HornState.Sustain));
			horn.Advance(10); Assert.That(horn.Gain, Is.EqualTo(1));
			horn.Hold(false); horn.Advance(.075); Assert.That(horn.Gain, Is.EqualTo(.5).Within(1e-9));
			horn.Hold(true); Assert.That(horn.Gain, Is.EqualTo(.5).Within(1e-9));
			horn.Advance(.04); Assert.That(horn.State, Is.EqualTo(HornState.Sustain));
			horn.Hold(false); horn.Advance(.15); Assert.That(horn.State, Is.EqualTo(HornState.Off));
		}

		[Test]
		public void TrafficPhaseIsStableAndReusesTheOwnShipPattern()
		{
			const int mmsi = 367123456;
			var phase = TrafficSignalTiming.Phase(mmsi);
			Assert.That(phase, Is.EqualTo(TrafficSignalTiming.Phase(mmsi)));
			Assert.That(phase, Is.InRange(0.0, 119.999999));
			Assert.That(phase, Is.Not.EqualTo(TrafficSignalTiming.Phase(mmsi + 1)));
			for (var time = 0.0; time < 240; time += .031)
			{
				var since = TrafficSignalTiming.SecondsInCycle(time, mmsi);
				Assert.That(TrafficSignalTiming.IsBlasting(time, mmsi, FogSignalKind.TwoProlonged),
					Is.EqualTo(FogSignalSchedule.IsBlasting(FogSignalKind.TwoProlonged, since)));
			}
			Assert.That(AudioVoiceBudget.Total, Is.LessThanOrEqualTo(32));
			Assert.That(AudioVoiceBudget.Traffic, Is.EqualTo(8));
		}

		[Test]
		public void AudibleSelectionKeepsTheNearestEightWithStableTiesAndNoOutOfRangeTargets()
		{
			var targets = new NearestAudibleTargets(); targets.Begin();
			for (var i = 128; i >= 0; i--) targets.Consider(i, 100000000 + i, i * i);
			Assert.That(targets.Count, Is.EqualTo(8));
			for (var i = 0; i < 8; i++) Assert.That(targets.IndexAt(i), Is.EqualTo(i));
			targets.Begin();
			targets.Consider(1, 400, 1); targets.Consider(2, 300, 1);
			targets.Consider(3, 200, 2001 * 2001); targets.Consider(4, 100, double.NaN);
			Assert.That(targets.Count, Is.EqualTo(2)); Assert.That(targets.IndexAt(0), Is.EqualTo(2));
		}

		[Test]
		public void WheelhouseMufflesEnginesAndWindButOutsideOpensThemUp()
		{
			var inside = ListeningPosition.ForWheelhouse(true); var outside = ListeningPosition.ForWheelhouse(false);
			Assert.That(inside.EngineGain, Is.LessThan(outside.EngineGain));
			Assert.That(inside.WindGain, Is.LessThan(outside.WindGain));
			Assert.That(inside.EngineCutoffHertz, Is.LessThan(outside.EngineCutoffHertz));
		}

		[Test]
		public void ZeroVolumeAndMuteKeepIndependentGroupSettings()
		{
			var bus = new AudioBus { Master = .7f, Engines = .4f };
			Assert.That(bus.Gain(AudioGroup.Engines), Is.EqualTo(.28f).Within(1e-6));
			bus.Muted = true; Assert.That(bus.Gain(AudioGroup.Engines), Is.Zero);
			bus.Muted = false; Assert.That(bus.Gain(AudioGroup.Engines), Is.EqualTo(.28f).Within(1e-6));
			bus.Master = 0; Assert.That(bus.Engines, Is.EqualTo(.4f));
		}

		[TestCase("0", 0f)] [TestCase("1", 1f)] [TestCase("0.25", .25f)]
		public void PlayerVolumeIsParsedInvariantly(string text, float expected) =>
			Assert.That(AudioLaunchOptions.MasterVolume(new[] { "player", "-ferrysim-audio-volume", text }), Is.EqualTo(expected));

		[TestCase("-1")] [TestCase("1.1")] [TestCase("NaN")] [TestCase("Infinity")] [TestCase("0,5")]
		public void BadPlayerVolumeFailsClearly(string value) => Assert.Throws<ArgumentException>(() =>
			AudioLaunchOptions.MasterVolume(new[] { "-ferrysim-audio-volume", value }));

		[Test]
		public void ExistingAutomatedSmokeAndTestsDefaultToSilenceWithExplicitOverride()
		{
			Assert.That(AudioLaunchOptions.MasterVolume(new[] { "-runTests" }), Is.Zero);
			Assert.That(AudioLaunchOptions.MasterVolume(new[] { "-ferrysim-smoke", "output" }), Is.Zero);
			Assert.That(AudioLaunchOptions.MasterVolume(new[] { "-ferrysim-smoke", "output", "-ferrysim-audio-volume", "1" }), Is.EqualTo(1));
			Assert.That(AudioLaunchOptions.MasterVolume(Array.Empty<string>()), Is.EqualTo(1));
		}

		[Test]
		public void ContactSoundsLatchBriefTouchesAndRateLimitContinuousContact()
		{
			var events = new ContactSounds();
			Assert.That(events.Update(new ContactLatch(1, 0, 10000, 10000, 0), .02), Is.EqualTo(ContactSound.Both));
			Assert.That(events.Update(new ContactLatch(1, 0, 10000, 10000, 0), 10), Is.EqualTo(ContactSound.None));
			Assert.That(events.Update(new ContactLatch(2, 10, 10000, 10000, 0), .1), Is.EqualTo(ContactSound.None));
			Assert.That(events.Update(new ContactLatch(3, 100, 10000, 10000, 0), 1), Is.EqualTo(ContactSound.Fender));
			Assert.That(events.Update(new ContactLatch(4, 400, 10000, 10000, 0), 4), Is.EqualTo(ContactSound.Both));
		}

		[Test]
		public void SeededGullsRepeatAndNeedALanding()
		{
			var a = new GullTiming(123); var b = new GullTiming(123);
			for (var i = 0; i < 2000; i++) Assert.That(a.Due(i * .1, true), Is.EqualTo(b.Due(i * .1, true)));
			var offshore = new GullTiming(123);
			for (var i = 0; i < 200; i++) Assert.That(offshore.Due(i, false), Is.False);
		}

		[Test]
		public void AudioMuteHasOneFreeKeyAcrossEveryMap()
		{
			var asset = InputActionAsset.FromJson(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Input/FerrySimControls.inputactions")));
			try
			{
				var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var map in asset.actionMaps) foreach (var binding in map.bindings)
					if (!binding.isComposite && binding.path.StartsWith("<Keyboard>/")) Assert.That(owners.Add(binding.path), Is.True, binding.path);
				var mute = asset.FindAction("Audio/Mute", true);
				Assert.That(mute.bindings[0].path, Is.EqualTo("<Keyboard>/v"));
				using (var mapper = new AudioInputMapper(asset)) { mapper.Enable(); Assert.That(mapper.Enabled, Is.True); }
			}
			finally { UnityEngine.Object.DestroyImmediate(asset); }
		}
	}
}
