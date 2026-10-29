using System;
using System.Collections.Generic;
using CesiumForUnity;
using FerrySim.Sim;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.HarborWater
{
	/// <summary>A curved water patch on the common 100 m world lattice, anchored once and updated from the runner's render time.</summary>
	[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(CesiumGlobeAnchor))]
	public sealed class HarborWaterPatch : MonoBehaviour
	{

		/// <summary>Common world lattice spacing in metres.</summary>
		public const double Spacing = 100.0;
		private HarborWaterService _service;
		private VesselSimulationRunner _runner;
		private WaterNode[] _nodes;
		private double[] _weights;
		private double[] _levels;
		private Vector3[] _vertices;
		private Mesh _mesh;

		/// <summary>Double-precision anchor and basis used to bake the patch.</summary>
		public WaterFrame Frame { get; private set; }

		/// <summary>The component-owned dynamic mesh, or null before configuration.</summary>
		public Mesh Mesh => _mesh;

		/// <summary>Number of unique corner nodes in this patch.</summary>
		public int NodeCount => _nodes?.Length ?? 0;

		/// <summary>Builds a patch using integer common-lattice coordinates. Shared edge coordinates use the same datum nodes and tide weights.</summary>
		public void Configure(HarborWaterService service, int firstNorth, int firstEast, int rows, int columns, Material material, VesselSimulationRunner runner = null)
		{
			if (service == null || rows < 1 || columns < 1 || rows > 40 || columns > 40)
			{
				throw new ArgumentException("A water patch needs a service and between 1 and 40 cells per side.");
			}

			if (GetComponentInParent<CesiumGeoreference>() == null)
			{
				throw new InvalidOperationException("HarborWaterPatch must be parented below a CesiumGeoreference.");
			}

			_service = service;
			_runner = runner;
			var north = firstNorth * Spacing;
			var east = firstEast * Spacing;
			Frame = WaterFrame.AtNorthEast(north + rows * Spacing * .5, east + columns * Spacing * .5);
			var anchor = GetComponent<CesiumGlobeAnchor>();
			anchor.detectTransformChanges = false;
			anchor.adjustOrientationForGlobeWhenMoving = false;
			anchor.positionGlobeFixed = Frame.OriginEcef;
			anchor.rotationEastUpNorth = quaternion.identity;
			var positions = new List<(double North, double East)>();
			var indices = new Dictionary<(int Row, int Column), int>();
			var triangles = new List<int>();
			int Node(int row, int column)
			{
				if (!indices.TryGetValue((row, column), out var index))
				{
					index = positions.Count;
					indices.Add((row, column), index);
					positions.Add((north + row * Spacing, east + column * Spacing));
				}

				return index;
			}

			for (var row = 0; row < rows; row++)
			{
				for (var column = 0; column < columns; column++)
				{
					if (!service.CellCovered(north + row * Spacing, east + column * Spacing, Spacing))
					{
						continue;
					}

					var a = Node(row, column);
					var b = Node(row, column + 1);
					var c = Node(row + 1, column);
					var d = Node(row + 1, column + 1);
					triangles.Add(a);
					triangles.Add(c);
					triangles.Add(b);
					triangles.Add(b);
					triangles.Add(c);
					triangles.Add(d);
				}
			}

			_nodes = new WaterNode[positions.Count];
			_vertices = new Vector3[positions.Count];
			_weights = new double[positions.Count * service.GaugeCount];
			_levels = new double[service.GaugeCount];
			var normals = new Vector3[positions.Count];
			for (var i = 0; i < positions.Count; i++)
			{
				_nodes[i] = service.BakeNode(positions[i].North, positions[i].East, Frame, _weights, i * service.GaugeCount);
				var up = _nodes[i].Up;
				normals[i] = new Vector3((float)up.x, (float)up.y, (float)up.z);
			}

			if (_mesh != null)
			{
				ReleaseMesh();
			}

			_mesh = new Mesh
			{
				name = "Harbor water 100 m lattice"
			};
			_mesh.MarkDynamic();
			UpdateWater(0);
			_mesh.SetNormals(normals);
			_mesh.SetTriangles(triangles, 0);
			_mesh.bounds = new Bounds(Vector3.zero, new Vector3((float)(columns * Spacing + 10), 200, (float)(rows * Spacing + 10)));
			GetComponent<MeshFilter>().sharedMesh = _mesh;
			GetComponent<MeshRenderer>().sharedMaterial = material;
		}

		/// <summary>Updates baked vertices and submits them without per-frame allocations or geodesy.</summary>
		public void UpdateWater(double time)
		{
			if (_service == null || _mesh == null)
			{
				return;
			}

			_service.SampleGaugeLevels(time, _levels);
			_service.UpdateNodes(_nodes, _weights, _levels, _vertices);
			_mesh.SetVertices(_vertices);
		}

		private void LateUpdate()
		{
			if (_runner != null && _runner.IsInitialized)
			{
				UpdateWater(_runner.RenderTime);
			}
		}

		private void ReleaseMesh()
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

		private void OnDestroy()
		{
			if (_mesh != null)
			{
				ReleaseMesh();
			}
		}
	}
}
