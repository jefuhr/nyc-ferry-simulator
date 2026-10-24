using System;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// A tileable ripple normal map generated in code, so the water needs no texture asset (and nothing
	/// binary in git). The height field is a sum of sine waves whose wave numbers are whole cycles per
	/// tile, which makes it tile exactly; slopes come from the analytic derivative. The seed is fixed,
	/// so every machine and run gets the same texture.
	/// </summary>
	public static class WaterNormalMap
	{
		public const int Size = 256;
		private const int Seed = 20261023;
		private const int WaveCount = 28;
		private const float TargetMaxSlope = 0.45f;

		private static Texture2D _texture;

		/// <summary>The shared normal map; created on first use and after a domain reload.</summary>
		public static Texture2D Get()
		{
			if (_texture == null)
			{
				_texture = Create();
			}

			return _texture;
		}

		/// <summary>Slopes (dh/du, dh/dv) of the height field at texel (x, y), before scaling.</summary>
		public static Vector2 Slope(int x, int y, Wave[] waves)
		{
			var u = (x + 0.5) / Size;
			var v = (y + 0.5) / Size;
			double du = 0.0;
			double dv = 0.0;
			foreach (var wave in waves)
			{
				var phase = 2.0 * Math.PI * (wave.CyclesU * u + wave.CyclesV * v) + wave.Phase;
				var c = wave.Amplitude * 2.0 * Math.PI * Math.Cos(phase);
				du += c * wave.CyclesU;
				dv += c * wave.CyclesV;
			}

			return new Vector2((float)du, (float)dv);
		}

		/// <summary>The deterministic wave set behind the texture.</summary>
		public static Wave[] Waves()
		{
			var random = new System.Random(Seed);
			var waves = new Wave[WaveCount];
			for (var i = 0; i < WaveCount; i++)
			{
				int cyclesU;
				int cyclesV;
				double length;
				do
				{
					cyclesU = random.Next(-9, 10);
					cyclesV = random.Next(-9, 10);
					length = Math.Sqrt(cyclesU * cyclesU + cyclesV * cyclesV);
				}
				while (length < 2.0);

				// Amplitude falls with wave number so short waves add texture without dominating.
				var amplitude = Math.Pow(length, -1.6) * (0.6 + 0.4 * random.NextDouble());
				waves[i] = new Wave(cyclesU, cyclesV, amplitude, random.NextDouble() * 2.0 * Math.PI);
			}

			return waves;
		}

		private static Texture2D Create()
		{
			var waves = Waves();
			var slopes = new Vector2[Size * Size];
			var maxSlope = 0f;
			for (var y = 0; y < Size; y++)
			{
				for (var x = 0; x < Size; x++)
				{
					var slope = Slope(x, y, waves);
					slopes[y * Size + x] = slope;
					maxSlope = Mathf.Max(maxSlope, slope.magnitude);
				}
			}

			var scale = TargetMaxSlope / Mathf.Max(maxSlope, 1e-6f);
			var pixels = new Color32[Size * Size];
			for (var i = 0; i < pixels.Length; i++)
			{
				var normal = new Vector3(-slopes[i].x * scale, -slopes[i].y * scale, 1f).normalized;
				pixels[i] = new Color32(Encode(normal.x), Encode(normal.y), Encode(normal.z), 255);
			}

			var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true, true)
			{
				name = "Water ripple normals (generated)",
				hideFlags = HideFlags.DontSave,
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Trilinear,
				anisoLevel = 8,
			};
			texture.SetPixels32(pixels);
			texture.Apply(true, false);
			return texture;
		}

		private static byte Encode(float component) => (byte)Mathf.Clamp(Mathf.RoundToInt((component * 0.5f + 0.5f) * 255f), 0, 255);

		/// <summary>One sine component: whole cycles per tile along u and v, amplitude and phase.</summary>
		public readonly struct Wave
		{
			public Wave(int cyclesU, int cyclesV, double amplitude, double phase)
			{
				CyclesU = cyclesU;
				CyclesV = cyclesV;
				Amplitude = amplitude;
				Phase = phase;
			}

			public int CyclesU { get; }

			public int CyclesV { get; }

			public double Amplitude { get; }

			public double Phase { get; }
		}
	}
}
