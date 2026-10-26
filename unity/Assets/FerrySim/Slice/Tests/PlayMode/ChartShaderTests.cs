using System.Collections;
using System.Collections.Generic;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// The world import's chart shaders (World/Import/Runtime DepthColour and SolidColour) with the night and fog of
	/// <see cref="Atmosphere.ChartLightingKeyword"/>. Failure modes guarded: the shaders drawing anything but exactly what
	/// they drew before when the keyword is off (compared byte for byte with the import's shaders as they were, kept verbatim
	/// as fixtures); the keyword not dimming the colours, not fogging them, or dimming a light's lens. Draws its own
	/// geometry with its own camera, so it needs no scene; needs graphics.
	/// </summary>
	public class ChartShaderTests
	{
		private const int Size = 128;
		private static readonly int ChartLightId = Shader.PropertyToID("_FerrySimChartLight");
		private static readonly int ChartFogId = Shader.PropertyToID("_FerrySimChartFog");
		private static readonly int ChartFogColourId = Shader.PropertyToID("_FerrySimChartFogColour");
		private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
		private static readonly int EmissiveId = Shader.PropertyToID("_FerrySimEmissive");

		private readonly List<Object> _owned = new List<Object>();
		private Camera _camera;
		private RenderTexture _target;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			Shader.DisableKeyword(Atmosphere.ChartLightingKeyword);
			var host = Own(new GameObject("chart shader camera"));
			_camera = host.AddComponent<Camera>();
			_camera.orthographic = true;
			_camera.orthographicSize = 1f;
			_camera.nearClipPlane = 0.1f;
			_camera.farClipPlane = 100f;
			_camera.clearFlags = CameraClearFlags.SolidColor;
			_camera.backgroundColor = Color.black;
			_camera.transform.position = new Vector3(0f, 0f, -10f);
			_camera.enabled = false;
			_target = Own(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB));
			yield return null;
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			Shader.DisableKeyword(Atmosphere.ChartLightingKeyword);
			Shader.SetGlobalVector(ChartLightId, new Vector4(1f, 0f, 0f, 0f));
			Shader.SetGlobalVector(ChartFogId, Vector4.zero);
			foreach (var item in _owned)
			{
				if (item is RenderTexture texture)
				{
					texture.Release();
				}

				if (item != null)
				{
					Object.Destroy(item);
				}
			}

			_owned.Clear();
			yield return null;
		}

		[UnityTest]
		public IEnumerator WithTheKeywordOff_DepthColourDrawsExactlyAsImported_AndOnItDimsAndFogs()
		{
			var mesh = ColouredGrid();
			var before = Render(mesh, Material("FerrySim/Tests/DepthColourAsImported"));
			var after = Render(mesh, Material("FerrySim/DepthColour"));
			AssertSame(before, after, "DepthColour, keyword off");

			Shader.EnableKeyword(Atmosphere.ChartLightingKeyword);
			Shader.SetGlobalVector(ChartLightId, new Vector4(0.25f, 0f, 0f, 0f));
			Shader.SetGlobalVector(ChartFogId, Vector4.zero);
			var dimmed = Render(mesh, Material("FerrySim/DepthColour"));
			AssertDimmed(before, dimmed, 0.25f, "DepthColour at night");

			Shader.SetGlobalVector(ChartLightId, new Vector4(1f, 0f, 0f, 0f));
			Shader.SetGlobalVector(ChartFogId, new Vector4(2f, 0f, 0f, 0f));
			Shader.SetGlobalColor(ChartFogColourId, new Color(0.2f, 0.3f, 0.4f));
			var fogged = Render(mesh, Material("FerrySim/DepthColour"));
			AssertAll(fogged, new Color(0.2f, 0.3f, 0.4f), "DepthColour 10 m into fog of 1.5 m: the fog's colour");
			yield return null;
		}

		[UnityTest]
		public IEnumerator WithTheKeywordOff_SolidColourDrawsExactlyAsImported_AndOnItDimsAllButALightsLens()
		{
			var quad = Quad();
			foreach (var colour in new[] { new Color(0.36f, 0.39f, 0.42f), new Color(1f, 0.58f, 0.05f), new Color(0.65f, 0.68f, 0.71f), Color.green, Color.red, Color.white })
			{
				var asImported = Material("FerrySim/Tests/SolidColourAsImported");
				asImported.SetColor(BaseColorId, colour);
				var now = Material("FerrySim/SolidColour");
				now.SetColor(BaseColorId, colour);
				AssertSame(Render(quad, asImported), Render(quad, now), $"SolidColour {colour}, keyword off");
			}

			var structure = Material("FerrySim/SolidColour");
			structure.SetColor(BaseColorId, new Color(0.65f, 0.68f, 0.71f));
			var unlit = Render(quad, structure);
			Shader.EnableKeyword(Atmosphere.ChartLightingKeyword);
			Shader.SetGlobalVector(ChartLightId, new Vector4(0.25f, 0f, 0f, 0f));
			Shader.SetGlobalVector(ChartFogId, Vector4.zero);
			AssertDimmed(unlit, Render(quad, structure), 0.25f, "a structure at night");

			var lens = Material("FerrySim/SolidColour");
			lens.SetColor(BaseColorId, Color.green);
			Shader.DisableKeyword(Atmosphere.ChartLightingKeyword);
			var lensByDay = Render(quad, lens);
			Shader.EnableKeyword(Atmosphere.ChartLightingKeyword);
			var block = new MaterialPropertyBlock();
			block.SetFloat(EmissiveId, 1f);
			AssertSame(lensByDay, Render(quad, lens, block), "a lens (_FerrySimEmissive 1) is not dimmed");
			yield return null;
		}

		private T Own<T>(T item) where T : Object
		{
			_owned.Add(item);
			return item;
		}

		private Material Material(string shaderName)
		{
			var shader = Shader.Find(shaderName);
			Assert.That(shader, Is.Not.Null, shaderName);
			return Own(new Material(shader));
		}

		/// <summary>Eight by eight squares filling the view, each its own colour (dark, mid, bright and saturated).</summary>
		private Mesh ColouredGrid()
		{
			var vertices = new List<Vector3>();
			var colours = new List<Color32>();
			var triangles = new List<int>();
			for (var row = 0; row < 8; row++)
			{
				for (var column = 0; column < 8; column++)
				{
					var x = -1f + column * 0.25f;
					var y = -1f + row * 0.25f;
					var start = vertices.Count;
					vertices.Add(new Vector3(x, y, 0f));
					vertices.Add(new Vector3(x, y + 0.25f, 0f));
					vertices.Add(new Vector3(x + 0.25f, y + 0.25f, 0f));
					vertices.Add(new Vector3(x + 0.25f, y, 0f));
					var colour = new Color32((byte)(row * 36 + 3), (byte)(column * 36 + 1), (byte)((row * 8 + column) * 4), 255);
					for (var i = 0; i < 4; i++)
					{
						colours.Add(colour);
					}

					triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
				}
			}

			var mesh = Own(new Mesh { name = "chart shader grid" });
			mesh.SetVertices(vertices);
			mesh.SetColors(colours);
			mesh.SetTriangles(triangles, 0);
			mesh.RecalculateBounds();
			return mesh;
		}

		/// <summary>One square filling the view, facing the camera.</summary>
		private Mesh Quad()
		{
			var mesh = Own(new Mesh { name = "chart shader quad" });
			mesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
			mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
			mesh.RecalculateBounds();
			return mesh;
		}

		private Color32[] Render(Mesh mesh, Material material, MaterialPropertyBlock block = null)
		{
			var host = new GameObject("chart shader subject");
			try
			{
				host.AddComponent<MeshFilter>().sharedMesh = mesh;
				var renderer = host.AddComponent<MeshRenderer>();
				renderer.sharedMaterial = material;
				if (block != null)
				{
					renderer.SetPropertyBlock(block);
				}

				_camera.targetTexture = _target;
				_camera.Render();
				var previous = RenderTexture.active;
				var texture = Own(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
				RenderTexture.active = _target;
				texture.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
				texture.Apply();
				RenderTexture.active = previous;
				return texture.GetPixels32();
			}
			finally
			{
				_camera.targetTexture = null;
				Object.DestroyImmediate(host);
			}
		}

		private static void AssertSame(Color32[] expected, Color32[] actual, string what)
		{
			Assert.That(actual.Length, Is.EqualTo(expected.Length));
			var lit = 0;
			for (var i = 0; i < expected.Length; i++)
			{
				var a = expected[i];
				var b = actual[i];
				Assert.That(b.r == a.r && b.g == a.g && b.b == a.b && b.a == a.a, Is.True, $"{what}: pixel {i % Size},{i / Size} drew {b}, the import's shader {a}");
				lit += a.r + a.g + a.b > 0 ? 1 : 0;
			}

			Assert.That(lit, Is.GreaterThan(expected.Length / 2), what + ": the subject fills the view");
		}

		/// <summary>Every pixel darker by about <paramref name="light"/> in linear terms (within a few sRGB steps).</summary>
		private static void AssertDimmed(Color32[] unlit, Color32[] dimmed, float light, string what)
		{
			var checkedPixels = 0;
			for (var i = 0; i < unlit.Length; i++)
			{
				var full = (Color)unlit[i];
				if (full.maxColorComponent < 0.2f)
				{
					continue;
				}

				var expected = (full.linear * light).gamma;
				var actual = (Color)dimmed[i];
				Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.02f), $"{what}: pixel {i % Size},{i / Size} red");
				Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.02f), $"{what}: pixel {i % Size},{i / Size} green");
				Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.02f), $"{what}: pixel {i % Size},{i / Size} blue");
				checkedPixels++;
			}

			Assert.That(checkedPixels, Is.GreaterThan(unlit.Length / 4), what + ": enough bright pixels to compare");
		}

		/// <summary>Every pixel the colour <paramref name="linear"/> (as the sky's globals take it, unconverted), within a step or two.</summary>
		private static void AssertAll(Color32[] pixels, Color linear, string what)
		{
			var expected = linear.gamma;
			foreach (var pixel in pixels)
			{
				var actual = (Color)pixel;
				Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.01f), what);
				Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.01f), what);
				Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.01f), what);
			}
		}
	}
}
