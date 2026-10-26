using System.Collections.Generic;
using FerrySim.World.Import;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// The imported aids' lights on the run's clock. The world import gives each charted light a <c>BuoyFlasher</c> with its
	/// parsed character (World/Import/README.md); this drives every one with <c>BuoyFlasher.SetSimulationTime</c> from the
	/// run's simulated time each frame, so a light flashes its real character in step with the run (paused with it, the
	/// same in a replay), and adds a <see cref="SignalLight"/> at each lamp so the light is seen at its range, by Allard's
	/// law, through the run's air. That point shows while the lamp is lit and the aid's daylight control would have it on
	/// (from sunset to sunrise, <see cref="Daylight.ShowsAidLights"/>).
	/// <para>
	/// Ranges: the chart's nominal range is not carried by the import (the aids' prefabs keep the character, colour and
	/// height, not VALNMR), and the chart gives one for 2 of the slice's 14 lights. Every aid light here takes
	/// <see cref="DefaultNominalRangeNauticalMiles"/> by colour, an estimate, until the import carries the chart's.
	/// </para>
	/// <para>
	/// The lights are indexed in the scene's order; the import's types stay out of this class's public members, so code
	/// that does not reference the import's assembly can use it.
	/// </para>
	/// </summary>
	public sealed class AidLights
	{
		private static readonly int EmissiveId = Shader.PropertyToID("_FerrySimEmissive");

		private readonly List<BuoyFlasher> _flashers = new List<BuoyFlasher>(32);
		private readonly List<SignalLight> _points = new List<SignalLight>(32);

		/// <summary>The number of aid lights driven.</summary>
		public int Count => _flashers.Count;

		/// <summary>The long-range point of each light, in order.</summary>
		public IReadOnlyList<SignalLight> Points => _points;

		/// <summary>The simulation time last given to the lights, seconds.</summary>
		public double Time { get; private set; } = double.NaN;

		/// <summary>An estimate, nautical miles, for a light the chart gives no range (USCG lighted buoys and minor lights are typically 3 to 5).</summary>
		public static double DefaultNominalRangeNauticalMiles(string colour) =>
			colour == "W" ? 4.0 : colour == "R" || colour == "G" ? 3.0 : 2.0;

		/// <summary>The drawn colour of a chart colour code (S-57 COLOUR: W, R, G, Y, Am, Bu).</summary>
		public static SignalColour ColourOf(string colour) =>
			colour == "R" ? SignalColour.Red : colour == "G" ? SignalColour.Green : colour == "Y" || colour == "Am" ? SignalColour.Yellow : SignalColour.White;

		/// <summary>Takes over every aid light under <paramref name="root"/>, or in the loaded scenes when null.</summary>
		public static AidLights Find(Transform root)
		{
			var lights = new AidLights();
			var flashers = root != null
				? root.GetComponentsInChildren<BuoyFlasher>(true)
				: Object.FindObjectsByType<BuoyFlasher>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID);
			foreach (var flasher in flashers)
			{
				lights.Add(flasher);
			}

			return lights;
		}

		/// <summary>The lamp of light <paramref name="index"/>: the imported light's own object.</summary>
		public Transform Lamp(int index) => _flashers[index].transform;

		/// <summary>The chart's notation colour of light <paramref name="index"/> (W, R, G ...).</summary>
		public string Colour(int index) => _flashers[index].Characteristic.Colour;

		/// <summary>Whether the import's lamp of light <paramref name="index"/> is lit now.</summary>
		public bool LampLit(int index) => _flashers[index].Lamp != null && _flashers[index].Lamp.enabled;

		/// <summary>Whether light <paramref name="index"/>'s character is lit at <paramref name="simulationSeconds"/>.</summary>
		public bool CharacterLitAt(int index, double simulationSeconds) => _flashers[index].Characteristic.IsLit(simulationSeconds);

		/// <summary>Whether light <paramref name="index"/>'s character is the chart's sequence (SIGSEQ), not nominal pulse widths.</summary>
		public bool UsesChartSequence(int index) => _flashers[index].Characteristic.UsesChartSequence;

		/// <summary>Whether light <paramref name="index"/>'s lens is marked as a light, which the night does not dim.</summary>
		public bool LensMarked(int index)
		{
			var lens = _flashers[index].Lens;
			if (lens == null || !lens.HasPropertyBlock())
			{
				return false;
			}

			var block = new MaterialPropertyBlock();
			lens.GetPropertyBlock(block);
			return block.GetFloat(EmissiveId) == 1f;
		}

		private void Add(BuoyFlasher flasher)
		{
			if (flasher == null || flasher.Characteristic == null || _flashers.Contains(flasher))
			{
				return;
			}

			var colour = flasher.Characteristic.Colour;
			var range = DefaultNominalRangeNauticalMiles(colour) * NavigationLightRules.MetresPerNauticalMile;
			var point = SignalLight.Create(flasher.transform, $"Aid light point ({colour}, {range / NavigationLightRules.MetresPerNauticalMile:0.#} nm estimated range)",
				Vector3.zero, Quaternion.identity, ColourOf(colour), LightSector.AllRound, SignalLightOptics.IntensityForRange(range));
			point.On = false;
			_flashers.Add(flasher);
			_points.Add(point);

			// The lens is the light itself: the import's shader leaves it undimmed at night (World/Import/Runtime/SolidColour.shader).
			if (flasher.Lens != null)
			{
				var block = new MaterialPropertyBlock();
				flasher.Lens.GetPropertyBlock(block);
				block.SetFloat(EmissiveId, 1f);
				flasher.Lens.SetPropertyBlock(block);
			}
		}

		/// <summary>
		/// Puts every light at <paramref name="simulationSeconds"/> and shows each light's point while its lamp is lit and
		/// <paramref name="daylightControlOn"/>. Allocation-free on lights with a chart sequence.
		/// </summary>
		public void Show(double simulationSeconds, bool daylightControlOn)
		{
			Time = simulationSeconds;
			for (var i = 0; i < _flashers.Count; i++)
			{
				var flasher = _flashers[i];
				if (flasher == null)
				{
					continue;
				}

				flasher.SetSimulationTime(simulationSeconds);
				_points[i].On = daylightControlOn && flasher.Lamp != null && flasher.Lamp.enabled;
			}
		}
	}
}
