using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FerrySim.World
{
	/// <summary>
	/// The colours signal lights are drawn in, linear sRGB, against the chromaticity regions of Annex I (33 CFR 84.13(b),
	/// CIE 1931 x, y). White and green lie inside their regions. The regions for red and yellow lie outside what an sRGB
	/// display can show, so red is the display's own red primary (x 0.640, y 0.330; the region starts at x 0.660) and
	/// yellow the nearest point on the gamut's edge.
	/// </summary>
	public static class SignalColours
	{
		/// <summary>84.13(b)(i), white: corner x, y.</summary>
		public static readonly Vector2[] WhiteRegion = Region(new[] { 0.525f, 0.525f, 0.452f, 0.310f, 0.310f, 0.443f }, new[] { 0.382f, 0.440f, 0.440f, 0.348f, 0.283f, 0.382f });

		/// <summary>84.13(b)(ii), green.</summary>
		public static readonly Vector2[] GreenRegion = Region(new[] { 0.028f, 0.009f, 0.300f, 0.203f }, new[] { 0.385f, 0.723f, 0.511f, 0.356f });

		/// <summary>84.13(b)(iii), red.</summary>
		public static readonly Vector2[] RedRegion = Region(new[] { 0.680f, 0.660f, 0.735f, 0.721f }, new[] { 0.320f, 0.320f, 0.265f, 0.259f });

		/// <summary>84.13(b)(iv), yellow.</summary>
		public static readonly Vector2[] YellowRegion = Region(new[] { 0.612f, 0.618f, 0.575f, 0.575f }, new[] { 0.382f, 0.382f, 0.425f, 0.406f });

		/// <summary>The drawn colour, linear sRGB, brightest channel 1.</summary>
		public static Color Linear(SignalColour colour)
		{
			switch (colour)
			{
				case SignalColour.Red:
					return new Color(1f, 0f, 0f);
				case SignalColour.Green:
					return new Color(0f, 1f, 0.35f);
				case SignalColour.Yellow:
					return new Color(1f, 0.3f, 0f);
				default:
					return new Color(1f, 0.85f, 0.65f);
			}
		}

		/// <summary>CIE 1931 chromaticity of a linear sRGB colour (IEC 61966-2-1's matrix, D65 white).</summary>
		public static Vector2 Chromaticity(Color linear)
		{
			var x = 0.4124564f * linear.r + 0.3575761f * linear.g + 0.1804375f * linear.b;
			var y = 0.2126729f * linear.r + 0.7151522f * linear.g + 0.0721750f * linear.b;
			var z = 0.0193339f * linear.r + 0.1191920f * linear.g + 0.9503041f * linear.b;
			var sum = x + y + z;
			return sum > 0f ? new Vector2(x / sum, y / sum) : Vector2.zero;
		}

		/// <summary>True when <paramref name="point"/> lies inside the polygon <paramref name="region"/>.</summary>
		public static bool Inside(Vector2 point, Vector2[] region)
		{
			var inside = false;
			for (int i = 0, j = region.Length - 1; i < region.Length; j = i++)
			{
				var a = region[i];
				var b = region[j];
				if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
				{
					inside = !inside;
				}
			}

			return inside;
		}

		private static Vector2[] Region(float[] x, float[] y)
		{
			var corners = new Vector2[x.Length];
			for (var i = 0; i < x.Length; i++)
			{
				corners[i] = new Vector2(x[i], y[i]);
			}

			return corners;
		}
	}

	/// <summary>
	/// One signal light drawn as a point (Resources/FerrySimAtmosphere/SignalLight.shader): its colour, intensity and
	/// sector, in this object's frame (sector bearings clockwise from its forward, +z, seen from above). Whether the eye
	/// sees it, and how bright, is Allard's law against the global optics <see cref="SetOptics"/> sets: the shader works it
	/// out per vertex, and <see cref="RatioToThreshold"/> is the same sum for code and tests. <see cref="On"/> switches it.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class SignalLight : MonoBehaviour
	{
		public const string ShaderResource = "FerrySimAtmosphere/SignalLight";

		private static readonly int ColorId = Shader.PropertyToID("_Color");
		private static readonly int IntensityId = Shader.PropertyToID("_Intensity");
		private static readonly int SectorId = Shader.PropertyToID("_Sector");
		private static readonly int StartEdgeId = Shader.PropertyToID("_StartEdge");
		private static readonly int EndEdgeId = Shader.PropertyToID("_EndEdge");
		private static readonly int OpticsId = Shader.PropertyToID("_FerrySimLightOptics");

		private static Mesh _quad;
		private static Material _material;

		private MeshRenderer _renderer;

		public SignalColour Colour { get; private set; }

		public double IntensityCandela { get; private set; }

		public LightSector Sector { get; private set; }

		/// <summary>The renderer that draws it.</summary>
		public MeshRenderer Renderer => _renderer;

		/// <summary>Whether the light is lit: an off light draws nothing.</summary>
		public bool On
		{
			get => _renderer != null && _renderer.enabled;
			set
			{
				if (_renderer != null && _renderer.enabled != value)
				{
					_renderer.enabled = value;
				}
			}
		}

		/// <summary>
		/// The air every signal light is seen through: the extinction coefficient per metre and the eye's threshold of
		/// illuminance, lux (<see cref="SignalLightOptics"/>). One global for all lights.
		/// </summary>
		public static void SetOptics(double extinction, double thresholdLux) =>
			Shader.SetGlobalVector(OpticsId, new Vector4((float)extinction, (float)thresholdLux, 0f, 0f));

		/// <summary>Adds a light under <paramref name="parent"/>, lit.</summary>
		public static SignalLight Create(Transform parent, string name, Vector3 localPosition, Quaternion localRotation, SignalColour colour, LightSector sector, double intensityCandela)
		{
			var host = new GameObject(name);
			host.layer = parent != null ? parent.gameObject.layer : 0;
			host.transform.SetParent(parent, false);
			host.transform.localPosition = localPosition;
			host.transform.localRotation = localRotation;
			host.AddComponent<MeshFilter>().sharedMesh = Quad;
			var renderer = host.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = SharedMaterial;
			renderer.shadowCastingMode = ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			renderer.lightProbeUsage = LightProbeUsage.Off;
			renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
			var light = host.AddComponent<SignalLight>();
			light._renderer = renderer;
			light.Set(colour, sector, intensityCandela);
			return light;
		}

		/// <summary>Sets what the light shows; once, not per frame (a property block per light).</summary>
		public void Set(SignalColour colour, LightSector sector, double intensityCandela)
		{
			if (!(intensityCandela >= 0.0) || double.IsInfinity(intensityCandela))
			{
				throw new ArgumentOutOfRangeException(nameof(intensityCandela), intensityCandela, "A light's intensity is a finite number of candelas.");
			}

			Colour = colour;
			Sector = sector;
			IntensityCandela = intensityCandela;
			var block = new MaterialPropertyBlock();
			// SetVector, not SetColor: SetColor takes an sRGB colour and converts it, and these are already linear.
			block.SetVector(ColorId, (Vector4)SignalColours.Linear(colour));
			block.SetFloat(IntensityId, (float)intensityCandela);
			block.SetVector(SectorId, new Vector4((float)sector.StartDegrees, (float)sector.EndDegrees, sector.IsAllRound ? 1f : 0f, 0f));
			block.SetVector(StartEdgeId, Edge(sector.StartEdge));
			block.SetVector(EndEdgeId, Edge(sector.EndEdge));
			_renderer.SetPropertyBlock(block);
		}

		/// <summary>
		/// Allard's illuminance at <paramref name="eye"/> (world position) over the threshold: at least 1 means seen. Off,
		/// or outside its sector, it is 0. The shader draws the same.
		/// </summary>
		public double RatioToThreshold(Vector3 eye, double extinction, double thresholdLux)
		{
			if (!On)
			{
				return 0.0;
			}

			var toEye = eye - transform.position;
			var local = transform.InverseTransformDirection(toEye);
			var factor = Sector.Factor(LightSector.RelativeBearingDegrees(local));
			return SignalLightOptics.Illuminance(IntensityCandela * factor, Math.Max(toEye.magnitude, 0.01), extinction) / thresholdLux;
		}

		private static Vector4 Edge(LightSectorEdge edge) =>
			new Vector4((float)edge.FullInsideDegrees, (float)edge.AtLimit, (float)edge.CutOffOutsideDegrees, 0f);

		private static Mesh Quad
		{
			get
			{
				if (_quad != null)
				{
					return _quad;
				}

				// The shader places the corners in screen space from the uv; the positions only give the culling bounds.
				_quad = new Mesh { name = "Signal light quad", hideFlags = HideFlags.DontSave };
				_quad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
				_quad.uv = new[] { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f) };
				_quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
				_quad.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));
				return _quad;
			}
		}

		private static Material SharedMaterial
		{
			get
			{
				if (_material != null)
				{
					return _material;
				}

				var shader = Resources.Load<Shader>(ShaderResource);
				if (shader == null)
				{
					throw new InvalidOperationException($"The signal light shader Resources/{ShaderResource}.shader is missing.");
				}

				_material = new Material(shader) { name = "Signal light", hideFlags = HideFlags.DontSave };
				return _material;
			}
		}
	}
}
