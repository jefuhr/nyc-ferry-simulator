using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Places the vessel on the Cesium globe instead of the flat tangent plane. The world (north, east) maps to
	/// the ellipsoid point <see cref="LocalTangentPlane.ToGeodetic(double, double)"/> gives, at the ellipsoidal
	/// height of the water surface minus heave, oriented in the local east-up-north frame there. A
	/// <see cref="CesiumGlobeAnchor"/> holds that position in double-precision ECEF, so Earth curvature is
	/// right and the vessel stays put when the georeference origin moves (<c>CesiumOriginShift</c>).
	/// Put it on the runner's GameObject, under the world's <see cref="CesiumGeoreference"/>.
	/// </summary>
	[RequireComponent(typeof(CesiumGlobeAnchor))]
	[DisallowMultipleComponent]
	public sealed class CesiumVesselPlacement : MonoBehaviour, IVesselPlacement
	{
		[Tooltip("Component implementing IWaterSurface, normally the world's tide-aware water. Empty: a constant surface from the geoid offset below.")]
		[SerializeField]
		private MonoBehaviour waterSurfaceSource;

		[Tooltip("Read the NAVD88 zero ellipsoidal height from the data pipeline's geoid_offset.json when present.")]
		[SerializeField]
		private bool useGeoidOffsetFile = true;

		[Tooltip("Relative to the repository root (or StreamingAssets in a player).")]
		[SerializeField]
		private string geoidOffsetFile = ConstantWaterSurface.DefaultGeoidOffsetFile;

		[Tooltip("Ellipsoidal height of NAVD88 zero, metres; used when the geoid offset file is off or missing.")]
		[SerializeField]
		private double navd88ZeroEllipsoidalHeight;

		[Tooltip("Water level above NAVD88 for the constant surface, metres.")]
		[SerializeField]
		private double waterLevelNavd88;

		private CesiumGlobeAnchor _anchor;
		private IWaterSurface _waterSurface;
		private bool _warnedAboutTarget;

		/// <summary>The water surface used for heights; settable so the world (or a test) can supply its own.</summary>
		public IWaterSurface WaterSurface
		{
			get => _waterSurface ??= ResolveWaterSurface();
			set => _waterSurface = value;
		}

		public CesiumGlobeAnchor Anchor
		{
			get
			{
				if (_anchor == null)
				{
					_anchor = GetComponent<CesiumGlobeAnchor>();
					// This component sets the full orientation every frame, curvature included.
					_anchor.adjustOrientationForGlobeWhenMoving = false;
					_anchor.detectTransformChanges = false;
				}

				return _anchor;
			}
		}

		public void Place(Transform target, in VesselPose pose, double time)
		{
			if (target != transform && !_warnedAboutTarget)
			{
				_warnedAboutTarget = true;
				Debug.LogWarning("[FerrySim] CesiumVesselPlacement moves its own GameObject; the runner's render target is ignored.", this);
			}

			var geodetic = LocalTangentPlane.World.ToGeodetic(pose.North, pose.East);
			var height = WaterSurface.EllipsoidalHeight(pose.North, pose.East, time) - pose.Down;
			var anchor = Anchor;
			anchor.longitudeLatitudeHeight = new double3(geodetic.LongitudeDegrees, geodetic.LatitudeDegrees, height);
			anchor.rotationEastUpNorth = WorldToUnity.Rotation(pose.Heading, pose.Pitch, pose.Roll);
		}

		private IWaterSurface ResolveWaterSurface()
		{
			if (waterSurfaceSource is IWaterSurface supplied)
			{
				return supplied;
			}

			if (useGeoidOffsetFile)
			{
				if (ConstantWaterSurface.TryReadGeoidOffset(SimPaths.Resolve(geoidOffsetFile), out var offset, out var problem))
				{
					return new ConstantWaterSurface(offset, waterLevelNavd88);
				}

				Debug.LogWarning($"[FerrySim] {problem}; using the serialized NAVD88 zero ellipsoidal height ({navd88ZeroEllipsoidalHeight} m).", this);
			}

			if (navd88ZeroEllipsoidalHeight == 0.0)
			{
				Debug.LogWarning("[FerrySim] The geoid offset is unknown (0 m): the vessel sits on the ellipsoid, not on the water. Build the slice or set the offset.", this);
			}

			return new ConstantWaterSurface(navd88ZeroEllipsoidalHeight, waterLevelNavd88);
		}
	}
}
