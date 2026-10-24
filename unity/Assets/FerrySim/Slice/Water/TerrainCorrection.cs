using System;
using FerrySim.World;
using UnityEngine;

namespace FerrySim.Slice
{
	/// <summary>
	/// What <see cref="ImportedTerrain.Apply"/> moved, and by how much. It stays on the corrected instance and marks it
	/// as already on the water's datum, so a second Apply changes nothing.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class TerrainCorrection : MonoBehaviour
	{
		[SerializeField]
		private SliceDatum datum;

		[SerializeField]
		private double flatNavd88ZeroHeight;

		[SerializeField]
		private int meshesCorrected;

		[SerializeField]
		private int objectsMoved;

		[SerializeField]
		private double lowest = double.PositiveInfinity;

		[SerializeField]
		private double highest = double.NegativeInfinity;

		/// <summary>The water's datum the instance was moved onto.</summary>
		public SliceDatum Datum => datum;

		/// <summary>The importer's Unity height of NAVD88 zero that the correction started from, m.</summary>
		public double FlatNavd88ZeroHeight => flatNavd88ZeroHeight;

		public int MeshesCorrected => meshesCorrected;

		public int ObjectsMoved => objectsMoved;

		/// <summary>Largest move down (most negative), m.</summary>
		public double Lowest => lowest;

		/// <summary>Largest move up, m.</summary>
		public double Highest => highest;

		internal void Begin(SliceDatum onto, double fromZero)
		{
			datum = onto;
			flatNavd88ZeroHeight = fromZero;
		}

		internal void AddMesh() => meshesCorrected++;

		internal void AddObject() => objectsMoved++;

		internal void Add(double correction)
		{
			lowest = Math.Min(lowest, correction);
			highest = Math.Max(highest, correction);
		}

		public override string ToString() =>
			$"{MeshesCorrected} meshes and {ObjectsMoved} placed objects moved by {Lowest:F3} to {Highest:F3} m onto the water's NAVD88 frame";
	}
}
