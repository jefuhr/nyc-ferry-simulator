using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// A fixed bridge's navigation lights by 33 CFR 118.65, on its greybox (<see cref="BridgeGreybox"/>). The ENC charts no
	/// lights on the slice's bridges (checked in the raw cells, 2026-10-25), so this is the regulation's nominal set,
	/// labelled as such in every light's name and in <see cref="Source"/>:
	/// <list type="bullet">
	/// <item>(a) the centre of the navigable channel "marked by a range of two green lights", each showing 360°, "just below
	/// the outermost edge of the bridge span structure": one at each face of the span, half a metre under the deck, at the
	/// channel's middle, midway between the greybox's towers;</item>
	/// <item>(b) the channel runs from pier to pier, so "each end of such piers shall be lighted with a red light", showing
	/// 180° ("90° on either side of a line parallel to the axis of the channel"), "as low as practicable but not lower than
	/// 2 feet above navigable high water": at both ends of each tower, on its channel side, 2 ft above mean high water.</item>
	/// </list>
	/// The lights are fixed (118.60) and show from sunset to sunrise and when the visibility is under a mile (118.55(a),
	/// <see cref="Daylight"/>). Their range is 118.60's "2,000 yards", with Annex I's intensity for it. The towers'
	/// positions are the greybox's estimates, so the pier lights' are too.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class BridgeLights : MonoBehaviour
	{
		/// <summary>118.60: visible at 2,000 yards, metres.</summary>
		public const double RangeMetres = 2000.0 * 0.9144;

		/// <summary>118.65(b): not lower than 2 feet above navigable high water, metres.</summary>
		public const double PierLightAboveHighWater = 0.6096;

		/// <summary>The green lights hang this far under the deck's edge, metres (estimate).</summary>
		public const float GreenBelowDeck = 0.5f;

		public const string Source = "nominal 33 CFR 118.65 set on the greybox; no bridge lights are charted in the ENC";

		/// <summary>No 84.15 profile is given for bridge lights; a 1° cut-off.</summary>
		private static readonly LightSectorEdge BridgeEdge = new LightSectorEdge(0.0, 1.0, 1.0);

		private readonly List<SignalLight> _lights = new List<SignalLight>(6);
		private bool _on = true;

		public IReadOnlyList<SignalLight> Lights => _lights;

		public BridgeGreybox Bridge { get; private set; }

		public bool On
		{
			get => _on;
			set
			{
				if (_on == value)
				{
					return;
				}

				_on = value;
				for (var i = 0; i < _lights.Count; i++)
				{
					_lights[i].On = value;
				}
			}
		}

		/// <summary>Adds the lights to <paramref name="bridge"/>; false, adding none, when its greybox has no deck or two towers.</summary>
		public static bool TryAdd(BridgeGreybox bridge, out BridgeLights lights)
		{
			lights = null;
			if (bridge == null)
			{
				return false;
			}

			var root = bridge.transform;
			var deck = root.Find("Deck");
			var towerA = root.Find("Tower A");
			var towerB = root.Find("Tower B");
			if (deck == null || towerA == null || towerB == null)
			{
				return false;
			}

			lights = bridge.GetComponent<BridgeLights>();
			if (lights == null)
			{
				lights = bridge.gameObject.AddComponent<BridgeLights>();
			}

			lights.Build(bridge, deck, towerA, towerB);
			return true;
		}

		private void Build(BridgeGreybox bridge, Transform deck, Transform towerA, Transform towerB)
		{
			Bridge = bridge;
			foreach (var light in _lights)
			{
				if (light != null)
				{
					Destroy(light.gameObject);
				}
			}

			_lights.Clear();
			var intensity = SignalLightOptics.IntensityForRange(RangeMetres);

			// The bridge's frame: z along the span, x across the deck, that is along the channel.
			var deckUnderside = deck.localPosition.y - 0.5f * deck.localScale.y;
			var deckEdge = 0.5f * deck.localScale.x;
			var channelMiddle = 0.5f * (towerA.localPosition.z + towerB.localPosition.z);
			foreach (var face in new[] { 1f, -1f })
			{
				var position = new Vector3(face * deckEdge, deckUnderside - GreenBelowDeck, channelMiddle);
				Add($"Bridge light, centre of channel, green, {Facing(face)} face ({Source})", position, Quaternion.identity, SignalColour.Green, LightSector.AllRound, intensity);
			}

			// Heights: the deck's underside is at its NAVD88 height, so mean high water is that much lower (the curvature
			// across one bridge is millimetres).
			var highWater = deckUnderside - (float)(bridge.DeckUndersideNavd88 - bridge.MeanHighWaterNavd88 - PierLightAboveHighWater);
			var pierSector = new LightSector(270.0, 90.0, BridgeEdge, BridgeEdge);
			foreach (var tower in new[] { towerA, towerB })
			{
				var channelSide = tower.localPosition.z + Math.Sign(channelMiddle - tower.localPosition.z) * 0.5f * tower.localScale.z;
				var end = 0.5f * tower.localScale.x;
				foreach (var face in new[] { 1f, -1f })
				{
					var position = new Vector3(face * end, highWater, channelSide);
					var facing = Quaternion.LookRotation(new Vector3(face, 0f, 0f), Vector3.up);
					Add($"Bridge light, {tower.name} pier end, red, {Facing(face)} face ({Source})", position, facing, SignalColour.Red, pierSector, intensity);
				}
			}

			for (var i = 0; i < _lights.Count; i++)
			{
				_lights[i].On = _on;
			}
		}

		private void Add(string name, Vector3 position, Quaternion rotation, SignalColour colour, LightSector sector, double intensity) =>
			_lights.Add(SignalLight.Create(transform, name, position, rotation, colour, sector, intensity));

		/// <summary>The compass point a face of the span looks toward, for the light names.</summary>
		private string Facing(float face)
		{
			var direction = transform.TransformDirection(new Vector3(face, 0f, 0f));
			var bearing = LightSector.Wrap(Math.Atan2(direction.x, direction.z) * 180.0 / Math.PI);
			var points = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
			return points[(int)Math.Round(bearing / 45.0) % 8];
		}
	}
}
