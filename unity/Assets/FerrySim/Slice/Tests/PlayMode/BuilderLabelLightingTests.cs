using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>Builder labels must dim at night, retain their daytime appearance, and respect light depth on both sides.</summary>
	public class BuilderLabelLightingTests
	{
		private readonly List<Object> _owned = new List<Object>();
		private bool _chartLighting;
		private Vector4 _chartLight, _chartFog, _optics;

		[SetUp]
		public void SetUp()
		{
			_chartLighting = Shader.IsKeywordEnabled(Atmosphere.ChartLightingKeyword);
			_chartLight = Shader.GetGlobalVector("_FerrySimChartLight");
			_chartFog = Shader.GetGlobalVector("_FerrySimChartFog");
			_optics = Shader.GetGlobalVector("_FerrySimLightOptics");
		}

		private string Folder => Path.Combine(SliceScene.RepoRoot, "artifacts/pier11-and-picker");

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			if (_chartLighting) Shader.EnableKeyword(Atmosphere.ChartLightingKeyword);
			else Shader.DisableKeyword(Atmosphere.ChartLightingKeyword);
			Shader.SetGlobalVector("_FerrySimChartLight", _chartLight);
			Shader.SetGlobalVector("_FerrySimChartFog", _chartFog);
			Shader.SetGlobalVector("_FerrySimLightOptics", _optics);
			foreach (var item in _owned)
			{
				if (item is RenderTexture texture) texture.Release();
				if (item != null) Object.Destroy(item);
			}
			_owned.Clear();
			yield return null;
		}

		[UnityTest]
		public IEnumerator BuilderLabels_DimOnlyAtNight_AndLightsRespectTheirDepth()
		{
#if UNITY_EDITOR
			Directory.CreateDirectory(Folder);
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/FerrySim/World/Greybox/LowerEastRiverGreybox.prefab");
			var original = prefab.GetComponentsInChildren<TextMesh>(true).Single(l => l.text.Contains("North pontoon"));
			var label = Object.Instantiate(original.gameObject); _owned.Add(label);
			label.GetComponent<Billboard>().enabled = false;
			label.transform.SetPositionAndRotation(new Vector3(0, 0, 10), Quaternion.identity);
			label.transform.localScale = Vector3.one;
			var text = label.GetComponent<TextMesh>(); text.anchor = TextAnchor.MiddleCenter;
			var renderer = label.GetComponent<Renderer>();
			var backing = label.transform.Find("Backing");
			backing.localPosition = new Vector3(0, 0, 0.05f);
			backing.localScale = new Vector3(8, 2, 1);
			var camera = NightFogScene.TestCamera(_owned, Vector3.zero, Vector3.forward, 30);
			camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
			camera.cullingMask = 1 << 31;
			foreach (var child in label.GetComponentsInChildren<Transform>()) child.gameObject.layer = 31;
			var generated = renderer.sharedMaterial;
			Shader.DisableKeyword(Atmosphere.ChartLightingKeyword);
			renderer.sharedMaterial = text.font.material;
			var before = Capture(camera, "label-day-before");
			renderer.sharedMaterial = generated;
			var day = Capture(camera, "label-day-after");
			Assert.That(day.GetPixels32(), Is.EqualTo(before.GetPixels32()), "day colour, font atlas and glyph coverage are unchanged");
			Shader.SetGlobalVector("_FerrySimChartLight", new Vector4(1, 0, 0, 0));
			Shader.SetGlobalVector("_FerrySimChartFog", new Vector4(0.01f, 0, 0, 0));
			Shader.EnableKeyword(Atmosphere.ChartLightingKeyword);
			Assert.That(Capture(camera, "label-day-fog").GetPixels32(), Is.EqualTo(day.GetPixels32()), "daytime fog does not dim or fog the debug annotation");
			Shader.SetGlobalVector("_FerrySimChartLight", new Vector4(0.05f, 0, 0, 0));
			var night = Capture(camera, "label-night");
			Assert.That(Peak(night), Is.LessThan(Peak(day) * 0.5f), "night labels read the existing chart multiplier");

			Shader.SetGlobalVector("_FerrySimLightOptics", new Vector4((float)SignalLightOptics.LightExtinction(0), (float)SignalLightOptics.NightThresholdLux, 0, 0));
			var light = SignalLight.Create(null, "label depth probe", new Vector3(0, 0, 5), Quaternion.identity, SignalColour.Green, LightSector.AllRound, 10);
			_owned.Add(light.gameObject); light.gameObject.layer = 31; light.On = true;
			var near = Capture(camera, "label-behind-light-night");
			Assert.That(NightFogScene.Brightest(near, camera, light.transform.position).g, Is.GreaterThan(0.5f), "a nearer light remains visible");
			light.transform.position = new Vector3(0, 0, 15);
			var far = Capture(camera, "label-in-front-of-light-night");
			Assert.That(far.GetPixels32(), Is.EqualTo(night.GetPixels32()), "the foreground label backing occludes the farther signal");
			yield return null;
#else
			Assert.Ignore("Builder label materials are tested through the editor asset database.");
			yield break;
#endif
		}

		private Texture2D Capture(Camera camera, string name)
		{
			var image = NightFogScene.Render(_owned, camera, 960, 540);
			File.WriteAllBytes(Path.Combine(Folder, name + ".png"), image.EncodeToPNG());
			return image;
		}

		private static float Peak(Texture2D image) => image.GetPixels().Max(p => Mathf.Max(p.r, p.g, p.b));
	}
}
