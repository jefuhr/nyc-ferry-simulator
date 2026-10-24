using System;
using CesiumForUnity;
using FerrySim.Core.Contracts.Geodesy;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Scene-level self-check that Cesium and the sim core agree on where things are. It converts a known
	/// point (Wall St/Pier 11 by default, from the GTFS stop in the pipeline output) two ways and compares
	/// them in the georeference's Unity space:
	/// <list type="number">
	/// <item>Cesium: WGS84 to ECEF on Cesium's ellipsoid, then the georeference's ECEF-to-Unity transform.</item>
	/// <item>Core: <see cref="LocalTangentPlane.World"/> to north-east-down, the conventions' mapping
	/// (x = east, y = -down, z = north), then the <see cref="WorldFrame"/> transform.</item>
	/// </list>
	/// It also checks the georeference itself: origin at <see cref="WorldOrigin"/>, height 0, scale 1.
	/// Runs on Start and whenever <see cref="Run"/> is called; logs an error when out of tolerance.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class GeoreferenceCheck : MonoBehaviour
	{
		/// <summary>Largest disagreement allowed between Cesium and the core, metres.</summary>
		public const double ToleranceMetres = 0.05;

		[SerializeField] private CesiumGeoreference georeference;
		[SerializeField] private WorldFrame worldFrame;

		[Header("Reference point")]
		[SerializeField] private string referenceName = "Wall St/Pier 11";
		[SerializeField] private double referenceLatitude = 40.703161;
		[SerializeField] private double referenceLongitude = -74.006144;
		[SerializeField] private double referenceEllipsoidalHeight;
		[SerializeField] private string referenceSource = "NYC Ferry GTFS stop 87 (data/processed/lower-east-river/landings.geojson)";

		public string ReferenceName => referenceName;

		public double ReferenceLatitude => referenceLatitude;

		public double ReferenceLongitude => referenceLongitude;

		public string ReferenceSource => referenceSource;

		/// <summary>Result of the last <see cref="Run"/>; null before the first run.</summary>
		public GeoreferenceCheckResult LastResult { get; private set; }

		/// <summary>Sets the scene references and the reference point. The world builder calls this.</summary>
		public void Configure(CesiumGeoreference georeference, WorldFrame worldFrame, string name, double latitude, double longitude, string source)
		{
			this.georeference = georeference;
			this.worldFrame = worldFrame;
			referenceName = name;
			referenceLatitude = latitude;
			referenceLongitude = longitude;
			referenceEllipsoidalHeight = 0.0;
			referenceSource = source;
		}

		/// <summary>Runs the check now, logs the outcome and returns it.</summary>
		public GeoreferenceCheckResult Run()
		{
			var result = Measure(georeference, worldFrame, referenceLatitude, referenceLongitude, referenceEllipsoidalHeight);
			LastResult = result;
			var message = $"Georeference check at {referenceName} ({referenceLatitude:F6}, {referenceLongitude:F6}): {result}";
			if (result.Passed)
			{
				Debug.Log(message, this);
			}
			else
			{
				Debug.LogError(message, this);
			}

			return result;
		}

		/// <summary>
		/// Compares Cesium's and the core's Unity position for a WGS84 point, in the georeference's local
		/// space, and checks the georeference and world-frame setup.
		/// </summary>
		public static GeoreferenceCheckResult Measure(CesiumGeoreference georeference, WorldFrame worldFrame, double latitude, double longitude, double ellipsoidalHeight)
		{
			if (georeference == null || worldFrame == null)
			{
				return GeoreferenceCheckResult.Missing(georeference == null ? "no CesiumGeoreference" : "no WorldFrame");
			}

			// Cesium path.
			var ecef = georeference.ellipsoid.LongitudeLatitudeHeightToCenteredFixed(new double3(longitude, latitude, ellipsoidalHeight));
			var cesium = georeference.TransformEarthCenteredEarthFixedPositionToUnity(ecef);

			// Core path: north-east-down in the world plane, mapped to Unity, through the world frame's
			// transform into the georeference's local space (identity until the origin shifts).
			LocalTangentPlane.World.ToNed(new GeodeticPosition(latitude, longitude), ellipsoidalHeight, out var north, out var east, out var down);
			var mapped = new double3(east, -down, north);
			// Matrix4x4 * Matrix4x4 is a matrix product (float4x4 * float4x4 would be per component).
			float4x4 worldFrameToGeoreference = georeference.transform.worldToLocalMatrix * worldFrame.transform.localToWorldMatrix;
			var core = math.mul((double4x4)worldFrameToGeoreference, new double4(mapped, 1.0)).xyz;

			worldFrame.MeasurePinError(out var pinPosition, out var pinRotation);
			return new GeoreferenceCheckResult(
				math.length(cesium - core),
				new double3(north, east, down),
				georeference.latitude,
				georeference.longitude,
				georeference.height,
				georeference.scale,
				georeference.originAuthority,
				pinPosition,
				pinRotation);
		}

		private void Start() => Run();
	}

	/// <summary>Outcome of a <see cref="GeoreferenceCheck"/>.</summary>
	public sealed class GeoreferenceCheckResult
	{
		/// <summary>The world-frame pin may drift by float rounding only, metres.</summary>
		public const double PinTolerance = 0.001;

		private readonly string _missing;

		public GeoreferenceCheckResult(double errorMetres, double3 nedOfReference, double originLatitude, double originLongitude, double originHeight, double scale, CesiumGeoreferenceOriginAuthority authority, double pinPositionErrorMetres, double pinRotationErrorDegrees)
		{
			ErrorMetres = errorMetres;
			NorthEastDown = nedOfReference;
			OriginLatitude = originLatitude;
			OriginLongitude = originLongitude;
			OriginHeight = originHeight;
			Scale = scale;
			Authority = authority;
			PinPositionErrorMetres = pinPositionErrorMetres;
			PinRotationErrorDegrees = pinRotationErrorDegrees;
		}

		private GeoreferenceCheckResult(string missing)
		{
			_missing = missing;
			ErrorMetres = double.NaN;
		}

		/// <summary>Distance between the Cesium and core positions, metres.</summary>
		public double ErrorMetres { get; }

		/// <summary>The reference point in the world frame, as the core computes it.</summary>
		public double3 NorthEastDown { get; }

		public double OriginLatitude { get; }

		public double OriginLongitude { get; }

		public double OriginHeight { get; }

		public double Scale { get; }

		public CesiumGeoreferenceOriginAuthority Authority { get; }

		public double PinPositionErrorMetres { get; }

		public double PinRotationErrorDegrees { get; }

		public bool PositionsAgree => !double.IsNaN(ErrorMetres) && ErrorMetres <= GeoreferenceCheck.ToleranceMetres;

		public bool ScaleIsOne => Math.Abs(Scale - 1.0) < 1e-12;

		public bool WorldFramePinned => PinPositionErrorMetres <= PinTolerance && PinRotationErrorDegrees <= 1e-6;

		public bool Passed => _missing == null && PositionsAgree && ScaleIsOne && WorldFramePinned;

		internal static GeoreferenceCheckResult Missing(string what) => new GeoreferenceCheckResult(what);

		public override string ToString()
		{
			if (_missing != null)
			{
				return "FAILED: " + _missing;
			}

			return $"{(Passed ? "passed" : "FAILED")}: Cesium vs core {ErrorMetres * 1000.0:F3} mm (limit {GeoreferenceCheck.ToleranceMetres * 1000.0:F0} mm); " +
				$"world N {NorthEastDown.x:F3} E {NorthEastDown.y:F3} D {NorthEastDown.z:F3} m; " +
				$"georeference origin ({OriginLatitude:F6}, {OriginLongitude:F6}, h {OriginHeight:F3}) scale {Scale}; " +
				$"world frame pin {PinPositionErrorMetres * 1000.0:F3} mm, {PinRotationErrorDegrees:E1} deg";
		}
	}
}
