using System;
using CesiumForUnity;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace FerrySim.World.HarborTiles
{
	/// <summary>Origin shifting for the frozen harbor scene, which has no Cesium subscenes.</summary>
	[DisallowMultipleComponent]
	[RequireComponent(typeof(CesiumGlobeAnchor))]
	public sealed class HarborOriginShift : MonoBehaviour
	{
		private static readonly ProfilerMarker ShiftMarker = new ProfilerMarker("FerrySim.Harbor.OriginShift");
		[SerializeField] private CesiumGeoreference _georeference;
		[SerializeField] private CesiumGlobeAnchor _anchor;
		// The harbor configuration fixes this precision threshold at 100 m in HarborSceneConfiguration.
		[SerializeField] private double _distance = 100;
		private bool _configured;
		public double Distance => _distance;

		public void Configure(CesiumGeoreference georeference, CesiumGlobeAnchor anchor, double distance)
		{
			_configured = false;
			if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0)
				throw new ArgumentOutOfRangeException(nameof(distance));
			if (georeference == null || anchor == null || anchor.gameObject != gameObject ||
				!anchor.transform.IsChildOf(georeference.transform))
				throw new ArgumentException("The local anchor must belong to the georeference.");
			if (georeference.GetComponentsInChildren<CesiumSubScene>(true).Length != 0)
				throw new InvalidOperationException("The harbor origin driver does not support Cesium subscenes.");
			_georeference = georeference;
			_anchor = anchor;
			_distance = distance;
			_configured = true;
		}

		private void OnEnable()
		{
			if (_georeference != null && _anchor != null) Configure(_georeference, _anchor, _distance);
		}

		private void OnDisable() => _configured = false;
		private void LateUpdate() => Advance();

		public bool Advance()
		{
			using (ShiftMarker.Auto())
			{
				if (!_configured || _georeference == null || _anchor == null || !_anchor.isActiveAndEnabled)
					return false;
				var position = _anchor.positionGlobeFixed;
				var origin = new double3(_georeference.ecefX, _georeference.ecefY, _georeference.ecefZ);
				if (math.length(origin - position) < _distance) return false;
				_georeference.SetOriginEarthCenteredEarthFixed(position.x, position.y, position.z);
				Physics.SyncTransforms();
				return true;
			}
		}
	}
}
