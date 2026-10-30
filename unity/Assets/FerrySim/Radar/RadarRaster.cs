using System;
using FerrySim.Core.Radar;
using FerrySim.UI;
using UnityEngine;
using Unity.Collections;

namespace FerrySim.Radar
{
	public enum RadarPalette { Day, Dusk, Night }
	/// <summary>Each Cartesian pixel belongs to one spoke. Only newly swept pixels are rewritten.</summary>
	public sealed class RadarRaster : IDisposable
	{
		/// <summary>Estimated display resolution selected by the measured radar-perf follow-up.</summary>
		public const int DefaultSize = 384;
		private readonly RadarEngine _engine;
		private NativeArray<Color32> _pixels;
		private readonly Color32[] _levels = new Color32[256];
		private readonly int[] _spokes, _radial, _offsets, _indices;
		public RadarRaster(RadarEngine engine, int size = DefaultSize)
		{
			_engine = engine; Size = size;
			Texture = new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = "Semantic radar PPI", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
			_pixels = Texture.GetRawTextureData<Color32>(); _spokes = new int[_pixels.Length]; _radial = new int[_pixels.Length];
			_offsets = new int[engine.Configuration.Spokes + 1];
			for (var p = 0; p < _pixels.Length; p++)
			{
				var n = p / size + .5 - size / 2.0; var e = p % size + .5 - size / 2.0;
				var range = RadarMath.Range(n, e) / (size / 2.0); _spokes[p] = -1;
				if (range > 1) continue;
				var spoke = (int)Math.Round(RadarMath.Bearing(n, e) / RadarMath.Tau * engine.Configuration.Spokes) % engine.Configuration.Spokes;
				_spokes[p] = spoke; _radial[p] = Math.Min(1023, (int)(range * 1024)); _offsets[spoke + 1]++;
			}
			for (var s = 1; s < _offsets.Length; s++) _offsets[s] += _offsets[s - 1];
			_indices = new int[_offsets[_offsets.Length - 1]]; var cursors = (int[])_offsets.Clone();
			for (var p = 0; p < _pixels.Length; p++) if (_spokes[p] >= 0) _indices[cursors[_spokes[p]]++] = p;
			SetPalette(RadarPalette.Day);
		}
		public int Size { get; }
		public Texture2D Texture { get; }
		public int SpokeAt(int x, int y) => _spokes[y * Size + x];
		public int BinAt(int x, int y) => _radial[y * Size + x] * _engine.Scale.Bins / 1024;
		public void SetPalette(RadarPalette kind)
		{
			var background = kind == RadarPalette.Day ? InstrumentPalette.Day.Console : InstrumentPalette.Night.Console;
			var echo = kind == RadarPalette.Day ? InstrumentPalette.Day.Starboard : kind == RadarPalette.Night ? InstrumentPalette.Night.Text : InstrumentPalette.Night.Starboard;
			for (var i = 0; i < 256; i++) _levels[i] = Color.Lerp(background, echo, i / 255f);
			for (var p = 0; p < _pixels.Length; p++) _pixels[p] = background;
			RefreshAll();
		}
		public void Refresh()
		{
			if (_engine.Cleared) { RefreshAll(); return; }
			if (_engine.LastSpokeCount == 0) return;
			for (var s = 0; s < _engine.LastSpokeCount; s++) Paint((_engine.LastFirstSpoke + s) % _engine.Configuration.Spokes);
			Upload();
		}
		public void RefreshAll() { for (var s = 0; s < _engine.Configuration.Spokes; s++) Paint(s); Upload(); }
		private void Paint(int spoke)
		{
			for (var i = _offsets[spoke]; i < _offsets[spoke + 1]; i++)
			{
				var p = _indices[i]; var bin = _radial[p] * _engine.Scale.Bins / 1024;
				_pixels[p] = _levels[Math.Min(255, (int)(_engine.Echo(spoke, bin) * 255))];
			}
		}
		/// <summary>How many times the texture was uploaded; evidence that a measured window really drew.</summary>
		public int Uploads { get; private set; }
		private void Upload() { Texture.Apply(false, false); Uploads++; }
		public void Dispose() { if (Application.isPlaying) UnityEngine.Object.Destroy(Texture); else UnityEngine.Object.DestroyImmediate(Texture); }
	}
}
