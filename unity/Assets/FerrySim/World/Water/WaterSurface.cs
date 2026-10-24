using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts.Geodesy;
using UnityEngine;
using UnityEngine.Rendering;

namespace FerrySim.World
{
	/// <summary>
	/// The harbor's water surface. It sits at a constant NAVD88 height, which is a curved surface in the
	/// world frame: its WGS84 ellipsoidal height is the geoid offset (NAVD88 zero, about -31.8 m here,
	/// from <see cref="SliceDatum"/>) plus the water level, and Earth curvature drops it below the world
	/// tangent plane away from the origin (about 4.1 m at the slice's far corner, 7.2 km out). The mesh is generated
	/// at NAVD88 zero with that geometry, dense over the slice and coarser out to the horizon, and the
	/// water level raises it along the frame's up axis (the tilt error is under a centimetre across the
	/// slice). Must be a direct child of the <see cref="WorldFrame"/>.
	/// </summary>
	[ExecuteAlways]
	[DisallowMultipleComponent]
	[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
	public sealed class WaterSurface : MonoBehaviour
	{
		private static readonly int NormalMapId = Shader.PropertyToID("_FerrySimWaterNormals");

		[SerializeField] private SliceDatum datum;

		[Tooltip("Water level, metres above NAVD88. Set from tide data at runtime.")]
		[SerializeField] private double waterLevelNavd88;

		[Header("Mesh")]
		[Tooltip("Grid spacing over the slice and its margin, metres.")]
		[Min(10f)]
		[SerializeField] private float spacing = 100f;

		[Tooltip("Margin of dense grid around the slice, metres.")]
		[Min(0f)]
		[SerializeField] private float margin = 1000f;

		[Tooltip("How far the water reaches beyond the slice, metres. Fog hides the edge.")]
		[Min(100f)]
		[SerializeField] private float reach = 20000f;

		[Tooltip("Growth of the cell size per step outside the dense grid.")]
		[Range(1.05f, 2f)]
		[SerializeField] private float outerGrowth = 1.4f;

		private Mesh _mesh;
		private bool _rebuildRequested;

		/// <summary>Raised after the water level changes.</summary>
		public event Action<WaterSurface> LevelChanged;

		public SliceDatum Datum => datum;

		/// <summary>Water level, metres above NAVD88 (the tide). Moves the surface and anything floating on it.</summary>
		public double WaterLevelNavd88
		{
			get => waterLevelNavd88;
			set
			{
				if (double.IsNaN(value) || double.IsInfinity(value))
				{
					throw new ArgumentOutOfRangeException(nameof(value), "Water level must be finite.");
				}

				waterLevelNavd88 = value;
				ApplyLevel();
				LevelChanged?.Invoke(this);
			}
		}

		/// <summary>The generated mesh (null before the first build).</summary>
		public Mesh Mesh => _mesh;

		/// <summary>Sets the datum (editor builder and tests) and rebuilds the mesh.</summary>
		public void Configure(SliceDatum sliceDatum)
		{
			datum = sliceDatum;
			Rebuild();
		}

		/// <summary>Ellipsoidal height of NAVD88 zero (metres above WGS84) under a world (north, east) point.</summary>
		public double GeoidHeight(double north, double east)
		{
			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			return RequireDatum().GeoidHeight(position.LatitudeDegrees, position.LongitudeDegrees);
		}

		/// <summary>
		/// Ellipsoidal height of the water surface, metres above WGS84, under a world (north, east) point:
		/// the geoid offset there plus the current NAVD88 water level. This is the height a globe-anchored
		/// vessel floats at.
		/// </summary>
		public double EllipsoidalHeight(double north, double east) => GeoidHeight(north, east) + waterLevelNavd88;

		/// <summary>
		/// Height of the water surface in the world frame (metres up from the tangent plane, so including
		/// Earth curvature) under a world (north, east) point at the current level.
		/// </summary>
		public double SurfaceUp(double north, double east) => SurfaceUpAt(RequireDatum(), north, east, waterLevelNavd88);

		/// <summary>
		/// Height in the world frame (metres up from the tangent plane) of the surface at a NAVD88 level
		/// under a world (north, east) point: the point on the ellipsoid below (north, east), raised along
		/// its normal to the geoid offset plus the level. Greybox builders use it for waterlines.
		/// </summary>
		public static double SurfaceUpAt(SliceDatum datum, double north, double east, double levelNavd88)
		{
			var position = LocalTangentPlane.World.ToGeodetic(north, east);
			var height = datum.GeoidHeight(position.LatitudeDegrees, position.LongitudeDegrees) + levelNavd88;
			LocalTangentPlane.World.ToNed(position, height, out _, out _, out var down);
			return -down;
		}

		/// <summary>Regenerates the mesh from the datum (it does not depend on the water level).</summary>
		public void Rebuild()
		{
			if (datum == null || !datum.IsPopulated)
			{
				return;
			}

			if (_mesh == null)
			{
				_mesh = new Mesh { name = "Water surface (generated)", hideFlags = HideFlags.DontSave };
			}

			BuildMesh(_mesh, datum, spacing, margin, reach, outerGrowth);
			GetComponent<MeshFilter>().sharedMesh = _mesh;
		}

		/// <summary>
		/// Grid positions along one axis: every <paramref name="spacing"/> metres from min - margin to
		/// max + margin, then cells growing by <paramref name="growth"/> out to <paramref name="reach"/>.
		/// </summary>
		public static List<double> AxisNodes(double min, double max, double spacing, double margin, double reach, double growth)
		{
			var nodes = new List<double>();
			var start = min - margin;
			var end = max + margin;
			var count = Math.Max(1, (int)Math.Ceiling((end - start) / spacing));
			var step = (end - start) / count;

			var outerBelow = new List<double>();
			var cell = step;
			var position = start;
			while (position > min - reach)
			{
				cell *= growth;
				position = Math.Max(position - cell, min - reach);
				outerBelow.Add(position);
			}

			for (var i = outerBelow.Count - 1; i >= 0; i--)
			{
				nodes.Add(outerBelow[i]);
			}

			for (var i = 0; i <= count; i++)
			{
				nodes.Add(start + i * step);
			}

			cell = step;
			position = end;
			while (position < max + reach)
			{
				cell *= growth;
				position = Math.Min(position + cell, max + reach);
				nodes.Add(position);
			}

			return nodes;
		}

		/// <summary>World (north, east) bounds of the datum's bounding box.</summary>
		public static void SliceBounds(SliceDatum datum, out double minNorth, out double maxNorth, out double minEast, out double maxEast)
		{
			minNorth = double.MaxValue;
			maxNorth = double.MinValue;
			minEast = double.MaxValue;
			maxEast = double.MinValue;
			foreach (var latitude in new[] { datum.SouthLatitude, datum.NorthLatitude })
			{
				foreach (var longitude in new[] { datum.WestLongitude, datum.EastLongitude })
				{
					var local = LocalTangentPlane.World.ToLocal(new GeodeticPosition(latitude, longitude));
					minNorth = Math.Min(minNorth, local.North);
					maxNorth = Math.Max(maxNorth, local.North);
					minEast = Math.Min(minEast, local.East);
					maxEast = Math.Max(maxEast, local.East);
				}
			}
		}

		private static void BuildMesh(Mesh mesh, SliceDatum datum, double spacing, double margin, double reach, double growth)
		{
			SliceBounds(datum, out var minNorth, out var maxNorth, out var minEast, out var maxEast);
			var easts = AxisNodes(minEast, maxEast, spacing, margin, reach, growth);
			var norths = AxisNodes(minNorth, maxNorth, spacing, margin, reach, growth);
			var columns = easts.Count;
			var rows = norths.Count;

			var vertices = new Vector3[columns * rows];
			var normals = new Vector3[vertices.Length];
			var uvs = new Vector2[vertices.Length];
			var plane = LocalTangentPlane.World;
			for (var row = 0; row < rows; row++)
			{
				for (var column = 0; column < columns; column++)
				{
					var position = plane.ToGeodetic(norths[row], easts[column]);
					var height = datum.GeoidHeight(position.LatitudeDegrees, position.LongitudeDegrees);
					plane.ToNed(position, height, out var north, out var east, out var down);
					plane.ToNed(position, height + 1.0, out var upNorth, out var upEast, out var upDown);

					var index = row * columns + column;
					vertices[index] = WorldFrame.ToLocal(north, east, down);
					normals[index] = WorldFrame.ToLocal(upNorth - north, upEast - east, upDown - down).normalized;
					uvs[index] = new Vector2((float)east, (float)north);
				}
			}

			var indices = new int[(columns - 1) * (rows - 1) * 6];
			var next = 0;
			for (var row = 0; row < rows - 1; row++)
			{
				for (var column = 0; column < columns - 1; column++)
				{
					// Clockwise seen from above (Unity's front face): south-west, north-west, north-east, south-east.
					var southWest = row * columns + column;
					var northWest = southWest + columns;
					indices[next++] = southWest;
					indices[next++] = northWest;
					indices[next++] = northWest + 1;
					indices[next++] = southWest;
					indices[next++] = northWest + 1;
					indices[next++] = southWest + 1;
				}
			}

			mesh.Clear();
			mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			mesh.vertices = vertices;
			mesh.normals = normals;
			mesh.uv = uvs;
			mesh.triangles = indices;
			mesh.RecalculateBounds();

			// Leave room for the tide so a raised surface is never culled.
			var bounds = mesh.bounds;
			bounds.Expand(new Vector3(0f, 20f, 0f));
			mesh.bounds = bounds;
		}

		private SliceDatum RequireDatum()
		{
			if (datum == null || !datum.IsPopulated)
			{
				throw new InvalidOperationException("WaterSurface has no populated SliceDatum; run FerrySim > World > Rebuild Greybox From Pipeline Data.");
			}

			return datum;
		}

		private void ApplyLevel()
		{
			transform.localPosition = new Vector3(0f, (float)waterLevelNavd88, 0f);
			transform.localRotation = Quaternion.identity;
			transform.localScale = Vector3.one;
		}

		private void OnEnable()
		{
			Shader.SetGlobalTexture(NormalMapId, WaterNormalMap.Get());
			Rebuild();
			ApplyLevel();
		}

		private void OnValidate()
		{
			// Mesh rebuilds are deferred: OnValidate may not touch other objects safely.
			_rebuildRequested = true;
		}

		private void Update()
		{
			if (_rebuildRequested)
			{
				_rebuildRequested = false;
				Rebuild();
			}

			if (transform.localPosition.y != (float)waterLevelNavd88)
			{
				ApplyLevel();
			}
		}

		private void OnDestroy()
		{
			if (_mesh != null)
			{
				if (Application.isPlaying)
				{
					Destroy(_mesh);
				}
				else
				{
					DestroyImmediate(_mesh);
				}
			}
		}
	}
}
