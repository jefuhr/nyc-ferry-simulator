using System;
using System.Collections.Generic;
using FerrySim.World;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Slice
{
	/// <summary>
	/// Puts the world importer's prefab (terrain, chart structures, aids, landing strips) on the world's water. The
	/// importer maps heights flat, Unity y = NAVD88 + the slice-centre geoid offset (its import-manifest.json), while
	/// the world's water follows the slice's geoid grid and Earth curvature (World/README.md): 4 km out the flat terrain
	/// stands 1.2 m proud of the drawn water, so shoals show through it. Each vertex of a mesh drawn in frame
	/// coordinates, and each object placed by position, is moved up or down by the difference between the two NAVD88
	/// zeros at its position (<see cref="Correction"/>), so NAVD88 zero on the terrain is NAVD88 zero on the water.
	/// Meshes are corrected on copies; the imported assets are untouched. An instance is corrected once: the
	/// <see cref="TerrainCorrection"/> stays on it, and a second <see cref="Apply"/> changes nothing.
	/// </summary>
	public static class ImportedTerrain
	{
		/// <summary>The importer's flat mapping, as its manifest states it; anything else is placed as imported.</summary>
		public const string FlatMapping = "Unity y = NAVD88 elevation + slice centre geoid offset; horizontal coordinates use core ellipsoid-surface geodesy";

		/// <summary>
		/// The Unity height of NAVD88 zero the importer used (<c>navd88_zero_y_m</c>), or NaN with a reason when the
		/// manifest is missing, malformed or describes another mapping.
		/// </summary>
		public static double FlatNavd88ZeroHeight(string manifestJson, out string problem)
		{
			problem = null;
			try
			{
				var manifest = JObject.Parse(manifestJson ?? string.Empty);
				var mapping = (string)manifest["vertical_mapping"];
				if (mapping != FlatMapping)
				{
					problem = $"the import manifest's vertical_mapping is '{mapping}', not the flat mapping this correction is for";
					return double.NaN;
				}

				var zero = manifest["navd88_zero_y_m"];
				if (zero == null || (zero.Type != JTokenType.Float && zero.Type != JTokenType.Integer))
				{
					problem = "the import manifest has no navd88_zero_y_m";
					return double.NaN;
				}

				return (double)zero;
			}
			catch (Exception exception) when (exception is Newtonsoft.Json.JsonException || exception is InvalidCastException)
			{
				problem = "the import manifest is unreadable: " + exception.Message;
				return double.NaN;
			}
		}

		/// <summary>
		/// How far to move a point the importer placed at world (north, east) so its NAVD88 height lands on the water's
		/// NAVD88 frame, metres up.
		/// </summary>
		public static double Correction(SliceDatum datum, double north, double east, double flatNavd88ZeroHeight) =>
			WaterSurface.SurfaceUpAt(datum, north, east, 0.0) - flatNavd88ZeroHeight;

		/// <summary>
		/// Moves an instantiated import prefab (at identity under the world frame) onto the water's NAVD88 frame, once.
		/// The returned <see cref="TerrainCorrection"/> is added to the instance: calling again with the same datum and
		/// height changes nothing, copies nothing and returns it, while another datum or height throws, since the
		/// instance is no longer as imported. The mesh copies it makes are added to <paramref name="owned"/> for the
		/// caller to destroy.
		/// </summary>
		public static TerrainCorrection Apply(GameObject instance, SliceDatum datum, double flatNavd88ZeroHeight, List<Object> owned)
		{
			if (instance.TryGetComponent<TerrainCorrection>(out var applied))
			{
				// Already on the water's datum: correcting again would add the offset twice.
				if (applied.Datum != datum || !applied.FlatNavd88ZeroHeight.Equals(flatNavd88ZeroHeight))
				{
					throw new InvalidOperationException(
						$"{instance.name} is already on {(applied.Datum != null ? applied.Datum.name : "another datum")} from NAVD88 zero at y = {applied.FlatNavd88ZeroHeight} m; " +
						"instantiate the import prefab again to correct it another way.");
				}

				return applied;
			}

			var result = instance.AddComponent<TerrainCorrection>();
			result.Begin(datum, flatNavd88ZeroHeight);
			Visit(instance.transform, datum, flatNavd88ZeroHeight, owned, result);
			return result;
		}

		private static void Visit(Transform node, SliceDatum datum, double zero, List<Object> owned, TerrainCorrection result)
		{
			foreach (Transform child in node)
			{
				var identity = child.localPosition == Vector3.zero && child.localRotation == Quaternion.identity && child.localScale == Vector3.one;
				if (!identity)
				{
					// Placed by position (an aid, a landing strip): it moves as a whole.
					var position = child.localPosition;
					var correction = Correction(datum, position.z, position.x, zero);
					child.localPosition = new Vector3(position.x, (float)(position.y + correction), position.z);
					result.Add(correction);
					result.AddObject();
					continue;
				}

				if (child.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
				{
					// Drawn in frame coordinates (terrain tiles, chart structures): each vertex moves.
					var mesh = Object.Instantiate(filter.sharedMesh);
					mesh.name = filter.sharedMesh.name + " (on the water's datum)";
					owned.Add(mesh);
					var vertices = mesh.vertices;
					for (var i = 0; i < vertices.Length; i++)
					{
						var correction = Correction(datum, vertices[i].z, vertices[i].x, zero);
						vertices[i].y = (float)(vertices[i].y + correction);
						result.Add(correction);
					}

					mesh.vertices = vertices;
					mesh.RecalculateBounds();
					filter.sharedMesh = mesh;
					if (child.TryGetComponent<MeshCollider>(out var collider))
					{
						collider.sharedMesh = mesh;
					}

					result.AddMesh();
				}

				Visit(child, datum, zero, owned, result);
			}
		}
	}
}
