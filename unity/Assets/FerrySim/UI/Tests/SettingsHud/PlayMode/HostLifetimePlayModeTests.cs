using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.UI.Tests.SettingsHud.PlayMode
{
	/// <summary>Scene unload and enable/disable/enable must release the old view before a session store notifies it.</summary>
	public class HostLifetimePlayModeTests
	{
		private readonly List<GameObject> _hosts = new List<GameObject>();

		[TearDown]
		public void DestroyHosts()
		{
			// Unity can stop an iterator on an unexpected log before its finally block is reached.
			foreach (var host in _hosts)
			{
				if (host != null) Object.DestroyImmediate(host);
			}

			_hosts.Clear();
		}

		[UnityTest]
		public IEnumerator ReplacedHost_SharedStoresReachOnlyTheLiveView_WithoutMirrorSettingsThrowing()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var first = CreateHost(settings, layouts);
			GameObject second = null;
			try
			{
				yield return null;
				var discarded = first.GetComponent<WheelhouseUI>().View;
				Assert.That(discarded, Is.Not.Null);
				var discardedCalls = 0;
				discarded.SettingsChanged += () => discardedCalls++;
				first.SetActive(false);
				Assert.That(first.GetComponent<WheelhouseUI>().View, Is.Null);
				var mirror = typeof(WheelhouseUI).GetMethod("MirrorSettings", BindingFlags.Instance | BindingFlags.NonPublic);
				Assert.That(mirror, Is.Not.Null);
				Assert.That(() => mirror.Invoke(first.GetComponent<WheelhouseUI>(), null), Throws.Nothing, "the defensive mirror guard also tolerates an absent view");
				second = CreateHost(settings, layouts);
				yield return null;
				var live = second.GetComponent<WheelhouseUI>().View;
				var liveCalls = 0;
				live.SettingsChanged += () => liveCalls++;
				Assert.That(() => settings.DistanceUnit = DistanceUnit.Metres, Throws.Nothing, "discarded host's MirrorSettings must not interrupt the multicast");
				Assert.That(liveCalls, Is.EqualTo(1));
				Assert.That(discardedCalls, Is.Zero);
				Assert.That(() => settings.DepthAlarmMetres = 3.0, Throws.Nothing);
				Assert.That(liveCalls, Is.EqualTo(2));
				Assert.That(discardedCalls, Is.Zero);
				Assert.That(live.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
				Assert.That(live.DepthAlarmThresholdMetres, Is.EqualTo(3.0));
				Object.DestroyImmediate(first);
				Assert.That(() => settings.DepthAlarmMetres = 4.0, Throws.Nothing);
				Assert.That(liveCalls, Is.EqualTo(3));
			}
			finally
			{
				if (first != null) Object.DestroyImmediate(first);
				if (second != null) Object.DestroyImmediate(second);
			}
		}

		[UnityTest]
		public IEnumerator EnableDisableEnable_BuildsAWorkingView_WithoutRevivingTheOldOne()
		{
			var settings = SettingsStore.InMemory();
			var layouts = HudLayoutStore.InMemory();
			var host = CreateHost(settings, layouts);
			try
			{
				yield return null;
				var ui = host.GetComponent<WheelhouseUI>();
				var old = ui.View;
				var oldCalls = 0;
				old.SettingsChanged += () => oldCalls++;
				host.SetActive(false);
				host.SetActive(true);
				yield return null;
				Assert.That(ui.View, Is.Not.Null.And.Not.SameAs(old));
				var liveCalls = 0;
				ui.View.SettingsChanged += () => liveCalls++;
				settings.DistanceUnit = DistanceUnit.Metres;
				settings.DepthAlarmMetres = 2.0;
				Assert.That(liveCalls, Is.EqualTo(2));
				Assert.That(oldCalls, Is.Zero);
				ui.View.OptionsVisible = true;
				ui.View.Options.DistanceUnit.Adjust(-1);
				Assert.That(ui.View.DistanceUnit, Is.EqualTo(DistanceUnit.Feet));
				ui.View.OptionsVisible = false;
				ui.View.HudEditing = true;
				Assert.That(ui.View.HudLayout.Editing, Is.True);
				settings.HudLayout = HudPresets.Minimal;
				Assert.That(ui.View.HudLayout.Current.Name, Is.EqualTo(HudPresets.Minimal));
			}
			finally
			{
				Object.DestroyImmediate(host);
			}
		}

		private GameObject CreateHost(SettingsStore settings, HudLayoutStore layouts)
		{
			var host = new GameObject("View lifetime test");
			_hosts.Add(host);
			host.SetActive(false);
			var ui = host.AddComponent<WheelhouseUI>();
			ui.Configure(null, null);
			ui.ConfigureStores(settings, layouts);
			host.SetActive(true);
			return host;
		}
	}
}
