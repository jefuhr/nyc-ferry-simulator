using System;
using System.Reflection;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class LeanInCameraTests
	{
		private static void Call(WheelhouseCameraController controller, string name, params object[] arguments)
		{
			var method = typeof(WheelhouseCameraController).GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
			Assert.That(method, Is.Not.Null, name + " API"); method.Invoke(controller, arguments);
		}
		private static string Focus(WheelhouseCameraController c)
		{
			var property = typeof(WheelhouseCameraController).GetProperty("FocusedScreen");
			Assert.That(property, Is.Not.Null); return (string)property.GetValue(c);
		}

		[TestCase("k-class")]
		[TestCase("t-class")]
		public void CycleOrder_EasedPose_HeadCone_Recentre_BowAndReset(string id)
		{
			using var f = new InstrumentFixture(id);
			var owner = new GameObject("Focus vessel"); var camera = new GameObject("Focus camera");
			try
			{
				f.Root.name = WheelhouseControlsView.RootName; f.Root.transform.SetParent(owner.transform, false);
				var mount = new GameObject("Mount").AddComponent<WheelhouseCameraMount>(); mount.transform.SetParent(owner.transform, false);
				mount.transform.position = f.Root.transform.Find("Eye").position;
				mount.transform.localRotation = Quaternion.Euler(8, 0, 0);
				var c = camera.AddComponent<WheelhouseCameraController>(); c.Mount = mount; c.Snap();
				Assert.That(Focus(c), Is.Null);
				foreach (var screen in new[] { "RadarScreen", "PlotterScreen", "PortEngineScreen", "StarboardEngineScreen", null })
				{
					Call(c, "CycleFocus"); Assert.That(Focus(c), Is.EqualTo(screen));
					for (var frame = 0; frame < 150; frame++) Call(c, "AdvancePresentation", 1f / 60);
					var target = screen == null ? mount.transform : f.Root.transform.Find(screen + "FocusEye");
					Assert.That(Vector3.Distance(c.transform.position, target.position), Is.LessThan(.001));
					var direction = screen == null ? mount.transform.forward : (f.Root.transform.Find(screen).position - target.position).normalized;
					Assert.That(Vector3.Angle(c.transform.forward, direction), Is.LessThan(1));
				}
				Call(c, "CycleFocus"); c.HeadLook = new Vector2(100, 100);
				Assert.That(c.HeadLook.magnitude, Is.LessThanOrEqualTo(15.01f));
				for (var frame = 0; frame < 150; frame++) Call(c, "AdvancePresentation", 1f / 60);
				var focusEye = f.Root.transform.Find("RadarScreenFocusEye");
				var screenDirection = f.Root.transform.Find("RadarScreen").position - focusEye.position;
				var focusedRotation = Quaternion.LookRotation(screenDirection, owner.transform.up);
				var lookedRotation = focusedRotation * Quaternion.Euler(c.HeadLook.y, c.HeadLook.x, 0);
				Assert.That(Quaternion.Angle(c.transform.rotation, lookedRotation), Is.LessThan(.05));
				Assert.That(Vector3.Angle(c.transform.forward, screenDirection), Is.LessThanOrEqualTo(15.01f));
				Assert.That(Vector3.Distance(c.transform.position, focusEye.position), Is.LessThan(.001), "head look rotates about the focus eye");
				Call(c, "Recentre"); Assert.That(Focus(c), Is.Null); Assert.That(c.HeadLook, Is.EqualTo(Vector2.zero));
				Call(c, "CycleFocus"); c.ToggleBowView(); Assert.That(Focus(c), Is.Null); Assert.That(c.ShowingBow, Is.True);
				Call(c, "CycleFocus"); Assert.That(c.ShowingBow, Is.False);
				typeof(WheelhouseCameraController).GetMethod("ResetBow", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(c, null);
				Assert.That(Focus(c), Is.Null);
			}
			finally { UnityEngine.Object.DestroyImmediate(camera); UnityEngine.Object.DestroyImmediate(owner); }
		}

		[Test]
		public void NoInteriorDoesNothing()
		{
			var camera = new GameObject("No interior"); var mount = new GameObject("Mount").AddComponent<WheelhouseCameraMount>();
			try { var c = camera.AddComponent<WheelhouseCameraController>(); c.Mount = mount; Call(c, "CycleFocus"); Assert.That(Focus(c), Is.Null); }
			finally { UnityEngine.Object.DestroyImmediate(camera); UnityEngine.Object.DestroyImmediate(mount.gameObject); }
		}
	}
}
