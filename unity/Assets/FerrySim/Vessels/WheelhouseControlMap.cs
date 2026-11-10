using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.Vessels
{
	/// <summary>
	/// Authored presentation mounts, populated from the provenance-bearing layout by the importer.
	/// Positions use vessel metres. Axes/normals are unit vectors in the layout frame; travel
	/// is the degree-valued authoring boundary. Rest rotations are identity. This map never drives physics.
	/// Lamp mounts are metadata pivots: all lamp geometry lives in one shared LampsSurface mesh,
	/// with LampIndex in UV1.x (Mesh.uv2 in Unity), and must not be animated as separate renderers.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class WheelhouseControlMap : MonoBehaviour
	{
		[Serializable]
		public struct ControlMount
		{
			public string Id;
			public string Kind;
			public Transform Mount;
			public Vector3 Axis;
			public Vector2 Travel;
			public Vector3 Normal;
			public int LampIndex;
		}

		public List<ControlMount> Controls = new List<ControlMount>();
	}
}
