using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// A power-driven vessel's navigation lights (<see cref="NavigationLightRules"/>) as <see cref="SignalLight"/>s under this
	/// object, which stands for the vessel: its origin at the reference point (midships, centreline, waterline) and its
	/// forward (+z) the bow, as <c>VesselGreybox</c> and the runner place it. Reusable on any hull: give it plain particulars
	/// (<see cref="ConfigureGeneric"/>) or a layout (<see cref="Configure"/>) and a status. <see cref="On"/> lights them or
	/// puts them out (sunset to sunrise and restricted visibility are the caller's rule, <see cref="Daylight"/>).
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class NavigationLights : MonoBehaviour
	{
		private readonly List<NavigationLightSpec> _specs = new List<NavigationLightSpec>(8);
		private readonly List<SignalLight> _lights = new List<SignalLight>(8);
		private bool _on = true;

		/// <summary>The status the lights show for.</summary>
		public VesselLightStatus Status { get; private set; }

		/// <summary>The layout the lights were placed from.</summary>
		public VesselLightLayout Layout { get; private set; }

		/// <summary>The lights shown, in <see cref="NavigationLightRules.Lights"/>'s order.</summary>
		public IReadOnlyList<SignalLight> Lights => _lights;

		/// <summary>What each of <see cref="Lights"/> is.</summary>
		public IReadOnlyList<NavigationLightSpec> Specs => _specs;

		/// <summary>Lights every light, or puts them all out.</summary>
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

		/// <summary>The light of a kind, or null when the status does not show it.</summary>
		public SignalLight Find(NavigationLightKind kind)
		{
			for (var i = 0; i < _specs.Count; i++)
			{
				if (_specs[i].Kind == kind)
				{
					return _lights[i];
				}
			}

			return null;
		}

		/// <summary>Places the lights for plain particulars, metres (<see cref="VesselLightLayout.Generic"/>): for traffic hulls.</summary>
		public void ConfigureGeneric(double lengthOverall, double beam, double mastheadHeight, double freeboard, VesselLightStatus status) =>
			Configure(VesselLightLayout.Generic(lengthOverall, beam, mastheadHeight, freeboard), status);

		/// <summary>Replaces the lights with those <paramref name="status"/> shows at <paramref name="layout"/>.</summary>
		public void Configure(in VesselLightLayout layout, VesselLightStatus status)
		{
			Layout = layout;
			Status = status;
			Clear();
			NavigationLightRules.Lights(layout, status, _specs);
			foreach (var spec in _specs)
			{
				var light = SignalLight.Create(transform, Name(spec), spec.Position, Quaternion.identity, spec.Colour, spec.Sector, spec.IntensityCandela);
				light.On = _on;
				_lights.Add(light);
			}
		}

		private void Clear()
		{
			foreach (var light in _lights)
			{
				if (light == null)
				{
					continue;
				}

				// Destroy is deferred while playing; hide the old light so it is never drawn again.
				light.gameObject.SetActive(false);
				if (Application.isPlaying)
				{
					Destroy(light.gameObject);
				}
				else
				{
					DestroyImmediate(light.gameObject);
				}
			}

			_lights.Clear();
			_specs.Clear();
		}

		private static string Name(in NavigationLightSpec spec) =>
			$"{spec.Kind} ({spec.Colour}, {spec.Sector.ArcDegrees:0.#} deg, {spec.NominalRangeMetres / NavigationLightRules.MetresPerNauticalMile:0.#} nm, position a greybox estimate)";

		private void OnDestroy() => _lights.Clear();
	}
}
