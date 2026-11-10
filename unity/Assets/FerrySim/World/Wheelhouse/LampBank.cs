using System;
using FerrySim.Core.Instruments;
using FerrySim.Vessels;
using UnityEngine;
using UnityEngine.Rendering;

namespace FerrySim.World
{
	/// <summary>One shared material/renderer, with fixed-size per-instance arrays. No per-lamp objects or lights.</summary>
	internal sealed class LampBank
	{
		internal const int Capacity = 32;
		private static readonly int LevelsId = Shader.PropertyToID("_LampLevels");
		private static readonly int ColoursId = Shader.PropertyToID("_LampColours");
		private readonly float[] _levels = new float[Capacity];
		private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
		private readonly Renderer _renderer;
		private readonly int _count;
		[Serializable] private sealed class Palette { public Lamp[] lamps; }
		[Serializable] private sealed class Lamp { public string mount, colour; public int lamp; }

		internal LampBank(Transform root, WheelhouseControlMap mounts)
		{
			foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
				if (renderer.sharedMaterial != null && renderer.sharedMaterial.name == "LampsSurface")
				{
					if (_renderer != null) throw new FormatException("Interior must have only one LampsSurface renderer.");
					_renderer = renderer;
				}
			if (_renderer == null) throw new FormatException("Missing LampsSurface renderer.");
			var material = Resources.Load<Material>("FerrySimWheelhouse/LampsSurface");
			var file = Resources.Load<TextAsset>("FerrySimWheelhouse/lamp-colours-v1");
			if (material == null || file == null) throw new FormatException("Missing wheelhouse lamp material/palette.");
			var palette = JsonUtility.FromJson<Palette>(file.text);
			var colours = new Vector4[Capacity];
			foreach (var mount in mounts.Controls)
			{
				if (mount.LampIndex < 0) continue;
				if (mount.LampIndex >= Capacity) throw new FormatException("Lamp bank capacity exceeded: " + mount.Id);
				var found = false;
				foreach (var lamp in palette.lamps)
					if (lamp.mount == mount.Id && lamp.lamp == mount.LampIndex)
					{
						// These are display lens colours; their red/green identity comes from the layout.
						colours[mount.LampIndex] = lamp.colour == "red" ? new Vector4(1, .025f, .012f, 1)
							: lamp.colour == "green" ? new Vector4(.025f, 1, .08f, 1)
							: throw new FormatException("Unknown lamp colour for " + mount.Id + ": " + lamp.colour);
						found = true; break;
					}
				if (!found) throw new FormatException("Missing layout lamp colour: " + mount.Id);
				_count = Math.Max(_count, mount.LampIndex + 1);
			}
			_renderer.sharedMaterial = material;
			_renderer.shadowCastingMode = ShadowCastingMode.Off;
			_renderer.receiveShadows = false;
			_block.SetVectorArray(ColoursId, colours);
			Apply();
		}

		internal void Refresh(ControlMapFile.Entry[] entries, IInstrumentReadout readout, double time)
		{
			Array.Clear(_levels, 0, _levels.Length);
			if (readout != null)
			{
				if (readout.LampTest) { for (var i = 0; i < _count; i++) _levels[i] = 1; }
				else foreach (var entry in entries)
				{
					var index = entry.Mount.LampIndex;
					if (index < 0) continue;
					if (entry.AlarmIndex >= 0)
						_levels[index] = AlarmAnnunciator.LampLit(readout.Phase(entry.AlarmIndex), false, time) ? 1 : 0;
					else if (entry.Channel.HasValue)
					{
						var value = readout.Value(entry.Channel.Value);
						_levels[index] = ControlMapFile.Finite(value) && (entry.HasLit ? value >= entry.LitLow && value <= entry.LitHigh : value != 0) ? 1 : 0;
					}
				}
			}
			Apply();
		}

		private void Apply() { _block.SetFloatArray(LevelsId, _levels); _renderer.SetPropertyBlock(_block); }
	}
}
