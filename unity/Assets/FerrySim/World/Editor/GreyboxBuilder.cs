using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FerrySim.Core.Contracts.Geodesy;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FerrySim.World.EditorTools
{
	/// <summary>
	/// Builds the water-level greybox from the pipeline's outputs: the slice datum asset, one floating
	/// barge per landing face (outline, berthing line, outward normal and a label with the confidence
	/// flag), the whole landing structures of landing_structures.geojson (fender strips on every barge
	/// side and end, the stop's other berths and pontoons, and fixed pier, bulkhead and shoreline walls)
	/// and the Brooklyn, Manhattan and Williamsburg bridges (deck at the published clearance, estimated
	/// towers). Writes assets under World/Greybox/ and World/Data/; overwrites in place so GUIDs stay
	/// stable. Positions go through the core's LocalTangentPlane, never the pipeline's rounded local
	/// coordinates, which are only used as a cross-check.
	/// </summary>
	public static class GreyboxBuilder
	{
		public const string WorldFolder = "Assets/FerrySim/World";
		public const string GreyboxFolder = WorldFolder + "/Greybox";
		public const string MeshFolder = GreyboxFolder + "/Meshes";
		public const string MaterialFolder = GreyboxFolder + "/Materials";
		public const string PrefabPath = GreyboxFolder + "/LowerEastRiverGreybox.prefab";
		public const string DataFolder = WorldFolder + "/Data";
		public const string DatumPath = DataFolder + "/LowerEastRiverDatum.asset";

		// Greybox sizes: estimates chosen for visibility, not survey data.
		public const float BargeFreeboard = 1.2f;
		public const float BargeDraft = 1.5f;
		public const float DeckDepth = 4f;
		public const float TowerAlongSpan = 12f;
		public const float TowerExtraWidth = 6f;
		public const float TowerTopAboveClearance = 45f;
		public const float TowerBaseBelowWater = 8f;

		// Whole-structure greybox sizes: estimates chosen for visibility, not survey data.
		public const float WallTopNavd88 = 2.5f;
		public const float WallBottomNavd88 = -1.5f;
		public const float WallCapWidth = 0.6f;
		public const float FenderBottom = -0.3f;
		public const float FenderOffset = 0.05f;

		/// <summary>Strip groups, in submesh order: the contact kinds, then segments the runtime does not use for contact.</summary>
		public static readonly string[] StripGroups = { "berth-face", "barge-side", "barge-end", "pier", "bulkhead", "shoreline", "not-contact" };

		/// <summary>Largest allowed gap between our tangent-plane coordinates and the pipeline's (it rounds to 1 mm).</summary>
		public const double LocalCoordinateTolerance = 0.01;

		public static readonly string[] BridgeNames = { "Brooklyn Bridge", "Manhattan Bridge", "Williamsburg Bridge" };

		/// <summary>Builds every greybox asset; throws if the data cannot produce a complete greybox.</summary>
		public static GreyboxReport Build(PipelineData data, string worldFolder = WorldFolder)
		{
			var meshFolder = WorldBuildPaths.At(worldFolder, MeshFolder);
			var materialFolder = WorldBuildPaths.At(worldFolder, MaterialFolder);
			var prefabPath = WorldBuildPaths.At(worldFolder, PrefabPath);
			var previous = WorldFileIds.Read(prefabPath);
			EnsureFolder(WorldBuildPaths.At(worldFolder, GreyboxFolder));
			EnsureFolder(meshFolder);
			EnsureFolder(materialFolder);
			EnsureFolder(WorldBuildPaths.At(worldFolder, DataFolder));

			var report = new GreyboxReport();
			var datum = WriteDatum(data, WorldBuildPaths.At(worldFolder, DatumPath));
			var materials = new GreyboxMaterials(materialFolder);
			var root = new GameObject("LowerEastRiverGreybox");
			try
			{
				var landings = Child(root.transform, "Landings", WorldLayers.Structure);
				landings.AddComponent<FloatOnWater>();
				var barges = new Dictionary<string, (GameObject Barge, PlanePoint Centroid)>(StringComparer.Ordinal);
				foreach (var face in data.Faces.OrderBy(f => f.StopId, StringComparer.Ordinal))
				{
					var built = BuildBarge(face, data, datum, materials, landings.transform, report, meshFolder);
					if (built.Barge != null)
					{
						barges[face.StopId] = built;
					}
				}

				// Whole landing structures: fenders on every side and end of the stop's barge, the stop's other
				// berths and pontoons floating with it, and its piers, bulkheads and shoreline standing fixed.
				var structures = Child(root.transform, "Structures", WorldLayers.Structure);
				var written = new HashSet<string>(StringComparer.Ordinal);
				foreach (var stop in data.Structures.Select(p => p.StopId).Distinct().OrderBy(id => id, StringComparer.Ordinal))
				{
					BuildStructures(stop, data.Structures.Where(p => p.StopId == stop).ToList(), barges, datum, materials, landings.transform, structures.transform, report, meshFolder, written);
				}

				foreach (var path in AssetDatabase.FindAssets("t:Mesh", new[] { meshFolder }).Select(AssetDatabase.GUIDToAssetPath).Where(p => !p.Contains("/Barge_") && !written.Contains(p)).ToList())
				{
					AssetDatabase.DeleteAsset(path);
				}

				var bridges = Child(root.transform, "Bridges", WorldLayers.Structure);
				foreach (var name in BridgeNames)
				{
					BuildBridge(name, data, datum, materials, bridges.transform, report);
				}

				if (report.Errors.Count > 0)
				{
					throw new InvalidOperationException("Greybox build failed:\n" + string.Join("\n", report.Errors));
				}

				PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var saved);
				if (!saved)
				{
					throw new InvalidOperationException("Could not save " + prefabPath);
				}
			}
			finally
			{
				Object.DestroyImmediate(root);
			}

			AssetDatabase.SaveAssets();
			WorldFileIds.Stabilize(prefabPath, previous);
			return report;
		}

		/// <summary>Writes (or updates) the slice datum asset from geoid_offset.json and the datum station.</summary>
		public static SliceDatum WriteDatum(PipelineData data, string path = DatumPath)
		{
			var geoid = data.Geoid;
			var datums = data.Datums;
			var provenance = new StringBuilder()
				.AppendLine($"Geoid: data/processed/{geoid.Slice}/geoid_offset.json, {geoid.Model}; {geoid.Transformation}.")
				.AppendLine($"Tidal datums: NOAA CO-OPS {datums.StationId} {datums.StationName}, epoch {datums.Epoch}, {datums.SourceUrl} (cached {datums.SourceFile}, sha256 {datums.SourceSha256}). Datum station chosen by the pipeline (depth_merged_navd88.json).")
				.Append("NAVD88 height of a datum = its value minus the NAVD88 value, both relative to station datum (STND).")
				.ToString();

			var values = new SliceDatumValues
			{
				SliceName = geoid.Slice,
				SouthLatitude = geoid.South,
				NorthLatitude = geoid.North,
				WestLongitude = geoid.West,
				EastLongitude = geoid.East,
				GeoidModel = geoid.Model,
				GeoidSouthWest = geoid.SouthWest,
				GeoidSouthEast = geoid.SouthEast,
				GeoidNorthWest = geoid.NorthWest,
				GeoidNorthEast = geoid.NorthEast,
				GeoidCentre = geoid.Centre,
				DatumStation = $"{datums.StationId} {datums.StationName}",
				TidalDatumEpoch = datums.Epoch,
				MeanHighWaterNavd88 = datums.MeanHighWaterNavd88,
				MeanLowerLowWaterNavd88 = datums.MeanLowerLowWaterNavd88,
				Provenance = provenance,
			};

			var datum = AssetDatabase.LoadAssetAtPath<SliceDatum>(path);
			if (datum == null)
			{
				datum = ScriptableObject.CreateInstance<SliceDatum>();
				datum.Assign(values);
				AssetDatabase.CreateAsset(datum, path);
			}
			else
			{
				datum.Assign(values);
				EditorUtility.SetDirty(datum);
			}

			return datum;
		}

		private static (GameObject Barge, PlanePoint Centroid) BuildBarge(LandingFace face, PipelineData data, SliceDatum datum, GreyboxMaterials materials, Transform parent, GreyboxReport report, string meshFolder)
		{
			var plane = LocalTangentPlane.World;
			var outline = new List<PlanePoint>();
			for (var i = 0; i < face.OutlineLonLat.Count; i++)
			{
				var point = ToPlane(face.OutlineLonLat[i]);
				outline.Add(point);
				report.Check(face.StopId + " outline", point, face.OutlineNorthEast[i]);
			}

			var ring = PolygonGeometry.CounterClockwise(PolygonGeometry.CleanRing(outline));
			if (ring.Count < 3)
			{
				report.Errors.Add($"Landing {face.StopId}: outline has {ring.Count} distinct points.");
				return (null, default);
			}

			var centroid = PolygonGeometry.Centroid(ring);
			var waterline = WaterSurface.SurfaceUpAt(datum, centroid.North, centroid.East, 0.0);
			var gtfs = data.Landings.FirstOrDefault(l => l.StopId == face.StopId);
			if (gtfs == null)
			{
				report.Errors.Add($"Landing face {face.StopId} has no GTFS stop in landings.geojson.");
				return (null, default);
			}

			var gtfsLocal = plane.ToLocal(new GeodeticPosition(gtfs.Latitude, gtfs.Longitude));
			report.Check(face.StopId + " GTFS stop", new PlanePoint(gtfsLocal.East, gtfsLocal.North), new[] { gtfs.North, gtfs.East });

			var barge = Child(parent, $"{face.StopId} {face.StopName}".Replace('/', '-'), WorldLayers.Structure);
			barge.transform.localPosition = new Vector3((float)centroid.East, (float)waterline, (float)centroid.North);

			// Hull: the outline extruded from the draft to the freeboard, in coordinates relative to the centroid.
			var relative = ring.Select(p => p - centroid).ToList();
			var mesh = UpsertMesh($"{meshFolder}/Barge_{face.StopId}.asset", ExtrudedPrism(relative, BargeFreeboard, -BargeDraft, $"Barge {face.StopId}"));
			var hull = Child(barge.transform, "Hull", WorldLayers.Structure);
			hull.AddComponent<MeshFilter>().sharedMesh = mesh;
			hull.AddComponent<MeshRenderer>().sharedMaterial = materials.Barge(face.Confidence);

			// Berthing face: a bar along the face line, just outside the hull, and an arrow along the outward normal.
			var start = ToPlane(face.FaceLonLat[0]);
			var end = ToPlane(face.FaceLonLat[face.FaceLonLat.Count - 1]);
			report.Check(face.StopId + " face start", start, face.FaceNorthEast[0]);
			report.Check(face.StopId + " face end", end, face.FaceNorthEast[face.FaceNorthEast.Count - 1]);
			var along = end - start;
			var length = along.Length;
			var normal = new PlanePoint(face.OutwardNormalEast, face.OutwardNormalNorth);
			var middle = (start + end) * 0.5 - centroid;
			Box(barge.transform, "BerthingFace", middle + normal * 0.3, BargeFreeboard - 0.3f, new Vector3(0.6f, 1.2f, (float)length), Bearing(along), materials.Face);
			Box(barge.transform, "FaceNormal", middle + normal * 3.0, BargeFreeboard + 0.25f, new Vector3(0.4f, 0.4f, 5f), Bearing(normal), materials.Face);

			var component = barge.AddComponent<LandingBarge>();
			component.Assign(new LandingBargeValues
			{
				StopId = face.StopId,
				StopName = face.StopName,
				RouteIds = face.RouteIds,
				Source = face.Source,
				Confidence = face.Confidence,
				Verification = face.Verification,
				EncId = face.EncId,
				GtfsLatitude = gtfs.Latitude,
				GtfsLongitude = gtfs.Longitude,
				GtfsNorth = gtfsLocal.North,
				GtfsEast = gtfsLocal.East,
				FaceStartNorth = start.North,
				FaceStartEast = start.East,
				FaceEndNorth = end.North,
				FaceEndEast = end.East,
				FaceHeadingDegrees = face.FaceHeadingDegrees,
				OutwardNormalNorth = face.OutwardNormalNorth,
				OutwardNormalEast = face.OutwardNormalEast,
				Freeboard = BargeFreeboard,
				Draft = BargeDraft,
			});

			Label(barge.transform, new Vector3(0f, BargeFreeboard + 2.5f, 0f), component.LabelText, materials.LabelColor(face.Confidence), materials.LabelBacking);
			report.Barges.Add($"{face.StopId} {face.StopName}: {face.Source}, {face.Confidence}, waterline {waterline:F3} m");
			return (barge, centroid);
		}

		private static void BuildStructures(string stop, List<StructurePolyline> polylines, Dictionary<string, (GameObject Barge, PlanePoint Centroid)> barges, SliceDatum datum,
			GreyboxMaterials materials, Transform landings, Transform structures, GreyboxReport report, string meshFolder, HashSet<string> written)
		{
			var name = $"{stop} {polylines[0].StopName}".Replace('/', '-');
			if (!barges.TryGetValue(stop, out var landing))
			{
				report.Errors.Add($"Landing structures for stop {stop} have no landing barge.");
				return;
			}

			// The stop's own barge: a fender strip on every side and end, in its kind's colour.
			var own = polylines.Where(p => p.Role == "landing").ToList();
			var fenders = Strips(own, landing.Centroid, (point, navd88) => navd88, FenderBottom, BargeFreeboard + FenderOffset, FenderOffset, 0f, report);
			MeshChild(landing.Barge.transform, "Fenders", UpsertMesh($"{meshFolder}/Fenders_{stop}.asset", fenders), materials, written, $"{meshFolder}/Fenders_{stop}.asset");
			report.Structures.Add($"{stop}: {own.Sum(p => p.Segments.Count)} barge segments");

			// The stop's other berths and nearby pontoons float with the landings, each its own prism.
			foreach (var polyline in polylines.Where(p => p.Role == "berth" || p.Role == "floating-structure"))
			{
				var outline = polyline.LonLat.Select(ToPlane).ToList();
				for (var i = 0; i < outline.Count; i++)
				{
					report.Check(polyline.PolylineId, outline[i], polyline.NorthEast[i]);
				}

				var ring = PolygonGeometry.CounterClockwise(PolygonGeometry.CleanRing(outline));
				if (!polyline.Closed || ring.Count < 3)
				{
					report.Errors.Add($"{polyline.PolylineId}: a floating outline must be a closed ring.");
					continue;
				}

				var centroid = PolygonGeometry.Centroid(ring);
				var identity = (polyline.StructureId ?? polyline.PolylineId).Replace("enc:", string.Empty).Replace('#', '-');
				var floating = Child(landings, $"{name} {(polyline.Role == "berth" ? "berth" : "pontoon")} {identity}", WorldLayers.Structure);
				floating.transform.localPosition = new Vector3((float)centroid.East, (float)WaterSurface.SurfaceUpAt(datum, centroid.North, centroid.East, 0.0), (float)centroid.North);
				var hullPath = $"{meshFolder}/Berth_{stop}_{identity}.asset";
				var hull = Child(floating.transform, "Hull", WorldLayers.Structure);
				hull.AddComponent<MeshFilter>().sharedMesh = UpsertMesh(hullPath, ExtrudedPrism(ring.Select(p => p - centroid).ToList(), BargeFreeboard, -BargeDraft, "Berth " + identity));
				hull.AddComponent<MeshRenderer>().sharedMaterial = materials.Barge(polyline.Segments.Select(s => s.Confidence).FirstOrDefault());
				written.Add(hullPath);
				var strips = Strips(new[] { polyline }, centroid, (point, navd88) => navd88, FenderBottom, BargeFreeboard + FenderOffset, FenderOffset, 0f, report);
				MeshChild(floating.transform, "Fenders", UpsertMesh($"{meshFolder}/Fenders_{stop}_{identity}.asset", strips), materials, written, $"{meshFolder}/Fenders_{stop}_{identity}.asset");
				report.Structures.Add($"{stop}: {polyline.Role} {polyline.StructureId}, {polyline.Segments.Count} segments");
			}

			// Fixed piers, bulkheads and shoreline: a water-facing wall with a cap on its solid side, on the
			// datum's surface so the walls follow the Earth's curvature like the water.
			var fixedPolylines = polylines.Where(p => p.Role == "fixed-structure").ToList();
			if (fixedPolylines.Count == 0)
			{
				return;
			}

			var origin = ToPlane(fixedPolylines[0].LonLat[0]);
			var baseUp = WaterSurface.SurfaceUpAt(datum, origin.North, origin.East, 0.0);
			var group = Child(structures, name, WorldLayers.Structure);
			group.transform.localPosition = new Vector3((float)origin.East, (float)baseUp, (float)origin.North);
			Func<PlanePoint, double, double> up = (point, navd88) => WaterSurface.SurfaceUpAt(datum, point.North + origin.North, point.East + origin.East, navd88) - baseUp;
			var walls = Strips(fixedPolylines, origin, up, WallBottomNavd88, WallTopNavd88, 0f, WallCapWidth, report);
			MeshChild(group.transform, "Walls", UpsertMesh($"{meshFolder}/Walls_{stop}.asset", walls), materials, written, $"{meshFolder}/Walls_{stop}.asset");
			report.Structures.Add($"{stop}: {fixedPolylines.Sum(p => p.Segments.Count)} fixed segments in {fixedPolylines.Count} runs");
		}

		/// <summary>
		/// A water-facing vertical strip along every segment of the polylines (solid on the right, so the strip faces
		/// left), raised <paramref name="offset"/> into the water, with an optional cap <paramref name="cap"/> wide
		/// on its solid side. One submesh per <see cref="StripGroups"/> entry: the segment's kind, or not-contact.
		/// Heights are NAVD88 metres mapped through <paramref name="up"/>; points are relative to <paramref name="origin"/>.
		/// </summary>
		public static Mesh Strips(IEnumerable<StructurePolyline> polylines, PlanePoint origin, Func<PlanePoint, double, double> up, float bottom, float top, float offset, float cap, GreyboxReport report)
		{
			var vertices = new List<Vector3>();
			var groups = StripGroups.Select(_ => new List<int>()).ToArray();
			foreach (var polyline in polylines)
			{
				for (var i = 0; i < polyline.Segments.Count; i++)
				{
					var segment = polyline.Segments[i];
					var a = ToPlane(polyline.LonLat[i]) - origin;
					var b = ToPlane(polyline.LonLat[i + 1]) - origin;
					report.Check(polyline.PolylineId + " segment " + segment.Index, a + origin, polyline.NorthEast[i]);
					var along = b - a;
					if (along.Length < 1e-3)
					{
						continue;
					}

					var left = new PlanePoint(-along.North, along.East) * (1.0 / along.Length);
					var group = segment.Contact ? Array.IndexOf(StripGroups, segment.Kind) : StripGroups.Length - 1;
					if (group < 0)
					{
						report.Errors.Add($"{polyline.PolylineId} segment {segment.Index}: unknown kind {segment.Kind}.");
						continue;
					}

					var wa = a + left * offset;
					var wb = b + left * offset;
					var start = vertices.Count;
					vertices.Add(Point(wa, up(wa, bottom)));
					vertices.Add(Point(wb, up(wb, bottom)));
					vertices.Add(Point(wb, up(wb, top)));
					vertices.Add(Point(wa, up(wa, top)));
					// Clockwise seen from the water (left) side: Unity's front face.
					groups[group].AddRange(new[] { start + 2, start + 3, start, start + 2, start, start + 1 });
					if (cap > 0f)
					{
						var ca = wa - left * cap;
						var cb = wb - left * cap;
						var capStart = vertices.Count;
						vertices.Add(Point(wa, up(wa, top)));
						vertices.Add(Point(wb, up(wb, top)));
						vertices.Add(Point(cb, up(cb, top)));
						vertices.Add(Point(ca, up(ca, top)));
						// Facing up: clockwise seen from above.
						groups[group].AddRange(new[] { capStart, capStart + 1, capStart + 2, capStart, capStart + 2, capStart + 3 });
					}
				}
			}

			var mesh = new Mesh { name = "Strips", subMeshCount = StripGroups.Length };
			mesh.SetVertices(vertices);
			for (var g = 0; g < groups.Length; g++)
			{
				mesh.SetTriangles(groups[g], g);
			}

			mesh.RecalculateNormals();
			mesh.RecalculateBounds();
			return mesh;
		}

		private static Vector3 Point(PlanePoint point, double up) => new Vector3((float)point.East, (float)up, (float)point.North);

		private static void MeshChild(Transform parent, string name, Mesh mesh, GreyboxMaterials materials, HashSet<string> written, string path)
		{
			var child = Child(parent, name, WorldLayers.Structure);
			child.AddComponent<MeshFilter>().sharedMesh = mesh;
			var renderer = child.AddComponent<MeshRenderer>();
			renderer.sharedMaterials = StripGroups.Select(materials.Strip).ToArray();
			written.Add(path);
		}

		private static void BuildBridge(string name, PipelineData data, SliceDatum datum, GreyboxMaterials materials, Transform parent, GreyboxReport report)
		{
			var features = data.Bridges.Where(b => b.Name == name).ToList();
			var main = features.Where(f => f.HasClearance && f.VerticalClearance.HasValue).ToList();
			if (main.Count != 1)
			{
				report.Errors.Add($"{name}: expected one span with a published clearance, found {main.Count}.");
				return;
			}

			var span = main[0];
			var spanPoints = span.RingsLonLat.SelectMany(r => r).Select(ToPlane).ToList();
			var rectangle = PolygonGeometry.MinimumAreaRectangle(spanPoints);
			var axis = rectangle.Axis;

			// The deck covers every ENC piece of this bridge along the main span's axis.
			var minAlong = double.MaxValue;
			var maxAlong = double.MinValue;
			foreach (var point in features.SelectMany(f => f.RingsLonLat).SelectMany(r => r).Select(ToPlane))
			{
				var s = (point - rectangle.Centre).Dot(axis);
				minAlong = Math.Min(minAlong, s);
				maxAlong = Math.Max(maxAlong, s);
			}

			// Tower positions are not in the ENC data. Put their inner faces just outside both the main-span
			// polygon and the published navigable width, centred on the span, so a greybox tower never
			// narrows the channel; the deck reaches at least to the towers' outer faces.
			var halfGap = 0.5 * Math.Max(rectangle.Length, span.HorizontalClearance ?? 0.0);
			minAlong = Math.Min(minAlong, -halfGap - TowerAlongSpan);
			maxAlong = Math.Max(maxAlong, halfGap + TowerAlongSpan);

			var deckCentre = rectangle.Centre + axis * (0.5 * (minAlong + maxAlong));
			var deckLength = maxAlong - minAlong;
			var clearance = span.VerticalClearance.Value;
			var undersideNavd88 = datum.MeanHighWaterNavd88 + clearance;
			var towerTopNavd88 = undersideNavd88 + TowerTopAboveClearance;
			var undersideUp = WaterSurface.SurfaceUpAt(datum, deckCentre.North, deckCentre.East, undersideNavd88);
			var deckGeodetic = LocalTangentPlane.World.ToGeodetic(deckCentre.North, deckCentre.East);
			var undersideEllipsoidal = undersideNavd88 + datum.GeoidHeight(deckGeodetic.LatitudeDegrees, deckGeodetic.LongitudeDegrees);

			// Bridge origin at the deck centre on the tangent plane, z along the span; children use absolute heights.
			var bridge = Child(parent, name, WorldLayers.Structure);
			bridge.transform.localPosition = new Vector3((float)deckCentre.East, 0f, (float)deckCentre.North);
			bridge.transform.localRotation = Quaternion.Euler(0f, (float)rectangle.AxisBearingDegrees, 0f);

			var deck = Box(bridge.transform, "Deck", default, 0f, new Vector3((float)rectangle.Width, DeckDepth, (float)deckLength), 0.0, materials.Deck);
			deck.transform.localPosition = new Vector3(0f, (float)(undersideUp + 0.5 * DeckDepth), 0f);
			deck.transform.localRotation = Quaternion.identity;

			var offset = (rectangle.Centre - deckCentre).Dot(axis);
			foreach (var side in new[] { -1.0, 1.0 })
			{
				var along = offset + side * (halfGap + 0.5 * TowerAlongSpan);
				var position = deckCentre + axis * along;
				var top = WaterSurface.SurfaceUpAt(datum, position.North, position.East, towerTopNavd88);
				var bottom = WaterSurface.SurfaceUpAt(datum, position.North, position.East, 0.0) - TowerBaseBelowWater;
				var tower = Box(bridge.transform, side < 0 ? "Tower A" : "Tower B", default, 0f, new Vector3((float)rectangle.Width + TowerExtraWidth, (float)(top - bottom), TowerAlongSpan), 0.0, materials.Tower);
				tower.transform.localPosition = new Vector3(0f, (float)(0.5 * (top + bottom)), (float)along);
				tower.transform.localRotation = Quaternion.identity;
			}

			var component = bridge.AddComponent<BridgeGreybox>();
			component.Assign(new BridgeGreyboxValues
			{
				BridgeName = name,
				EncIds = features.Select(f => f.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
				VerticalClearance = clearance,
				ClearanceDatum = span.ClearanceDatum,
				HorizontalClearance = span.HorizontalClearance ?? double.NaN,
				Information = span.Information,
				MeanHighWaterNavd88 = datum.MeanHighWaterNavd88,
				DeckUndersideNavd88 = undersideNavd88,
				DeckUndersideEllipsoidal = undersideEllipsoidal,
				TowerTopNavd88 = towerTopNavd88,
				TowerHeightIsEstimate = true,
			});

			var text = string.Format(CultureInfo.InvariantCulture, "{0}\nclearance {1:F1} m above {2} (NOAA ENC)\ntowers: greybox estimate", name, clearance, span.ClearanceDatum);
			// Above the towers, so the label never hides the deck it describes.
			var towerTopUp = WaterSurface.SurfaceUpAt(datum, deckCentre.North, deckCentre.East, towerTopNavd88);
			Label(bridge.transform, new Vector3(0f, (float)(towerTopUp + 6.0), 0f), text, materials.BridgeLabel, materials.LabelBacking);
			report.Bridges.Add(string.Format(CultureInfo.InvariantCulture, "{0}: clearance {1} m above {2}, deck underside {3:F2} m NAVD88 ({4:F2} m ellipsoidal), span {5:F0} m x {6:F0} m, deck {7:F0} m", name, clearance, span.ClearanceDatum, undersideNavd88, undersideEllipsoidal, rectangle.Length, rectangle.Width, deckLength));
		}

		/// <summary>The outline extruded between two heights: top, bottom and outward walls, flat-shaded.</summary>
		public static Mesh ExtrudedPrism(IReadOnlyList<PlanePoint> counterClockwiseRing, float top, float bottom, string name)
		{
			var vertices = new List<Vector3>();
			var triangles = new List<int>();
			var cap = PolygonGeometry.Triangulate(counterClockwiseRing);

			// Top faces up: Unity's front faces are clockwise seen from the front, so reverse each
			// counter-clockwise (seen from above) triangle.
			var topStart = vertices.Count;
			foreach (var point in counterClockwiseRing)
			{
				vertices.Add(new Vector3((float)point.East, top, (float)point.North));
			}

			for (var i = 0; i < cap.Count; i += 3)
			{
				triangles.Add(topStart + cap[i]);
				triangles.Add(topStart + cap[i + 2]);
				triangles.Add(topStart + cap[i + 1]);
			}

			// Bottom faces down: the same triangles keep their order (clockwise seen from below).
			var bottomStart = vertices.Count;
			foreach (var point in counterClockwiseRing)
			{
				vertices.Add(new Vector3((float)point.East, bottom, (float)point.North));
			}

			for (var i = 0; i < cap.Count; i += 3)
			{
				triangles.Add(bottomStart + cap[i]);
				triangles.Add(bottomStart + cap[i + 1]);
				triangles.Add(bottomStart + cap[i + 2]);
			}

			// Walls: walking a counter-clockwise ring the outside is on the right.
			for (var i = 0; i < counterClockwiseRing.Count; i++)
			{
				var a = counterClockwiseRing[i];
				var b = counterClockwiseRing[(i + 1) % counterClockwiseRing.Count];
				var start = vertices.Count;
				vertices.Add(new Vector3((float)a.East, top, (float)a.North));
				vertices.Add(new Vector3((float)b.East, top, (float)b.North));
				vertices.Add(new Vector3((float)b.East, bottom, (float)b.North));
				vertices.Add(new Vector3((float)a.East, bottom, (float)a.North));
				triangles.Add(start);
				triangles.Add(start + 1);
				triangles.Add(start + 2);
				triangles.Add(start);
				triangles.Add(start + 2);
				triangles.Add(start + 3);
			}

			var mesh = new Mesh { name = name };
			mesh.SetVertices(vertices);
			mesh.SetTriangles(triangles, 0);
			mesh.RecalculateNormals();
			mesh.RecalculateBounds();
			return mesh;
		}

		private static PlanePoint ToPlane(double[] lonLat)
		{
			var local = LocalTangentPlane.World.ToLocal(new GeodeticPosition(lonLat[1], lonLat[0]));
			return new PlanePoint(local.East, local.North);
		}

		private static double Bearing(PlanePoint direction) => Math.Atan2(direction.East, direction.North) * 180.0 / Math.PI;

		private static GameObject Child(Transform parent, string name, int layer)
		{
			var child = new GameObject(name) { layer = layer };
			child.transform.SetParent(parent, false);
			return child;
		}

		private static GameObject Box(Transform parent, string name, PlanePoint centre, float height, Vector3 size, double bearingDegrees, Material material)
		{
			var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
			Object.DestroyImmediate(box.GetComponent<Collider>());
			box.name = name;
			box.layer = WorldLayers.Structure;
			box.transform.SetParent(parent, false);
			box.transform.localPosition = new Vector3((float)centre.East, height, (float)centre.North);
			box.transform.localRotation = Quaternion.Euler(0f, (float)bearingDegrees, 0f);
			box.transform.localScale = size;
			box.GetComponent<MeshRenderer>().sharedMaterial = material;
			return box;
		}

		private static void Label(Transform parent, Vector3 localPosition, string text, Color color, Material backing)
		{
			var label = new GameObject("Label");
			label.transform.SetParent(parent, false);
			label.transform.localPosition = localPosition;
			var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			var mesh = label.AddComponent<TextMesh>();
			mesh.text = text;
			mesh.font = font;
			mesh.fontSize = 64;
			mesh.characterSize = 0.08f;
			mesh.anchor = TextAnchor.LowerCenter;
			mesh.alignment = TextAlignment.Center;
			mesh.color = color;
			var renderer = label.GetComponent<MeshRenderer>();
			renderer.sharedMaterial = font.material;
			renderer.shadowCastingMode = ShadowCastingMode.Off;
			renderer.receiveShadows = false;

			var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
			Object.DestroyImmediate(plate.GetComponent<Collider>());
			plate.name = "Backing";
			plate.transform.SetParent(label.transform, false);
			var plateRenderer = plate.GetComponent<MeshRenderer>();
			plateRenderer.sharedMaterial = backing;
			plateRenderer.shadowCastingMode = ShadowCastingMode.Off;
			plateRenderer.receiveShadows = false;
			label.AddComponent<Billboard>().SetBacking(plate.transform);
		}

		private static Mesh UpsertMesh(string path, Mesh mesh)
		{
			// Unity normalizes new main-object names to their filenames on import. Match it on updates too.
			mesh.name = System.IO.Path.GetFileNameWithoutExtension(path);
			var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
			if (existing == null)
			{
				AssetDatabase.CreateAsset(mesh, path);
				return mesh;
			}

			EditorUtility.CopySerialized(mesh, existing);
			EditorUtility.SetDirty(existing);
			Object.DestroyImmediate(mesh);
			return existing;
		}

		internal static void EnsureFolder(string path)
		{
			if (AssetDatabase.IsValidFolder(path))
			{
				return;
			}

			var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
			EnsureFolder(parent);
			AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
		}

		/// <summary>Greybox materials (URP Lit), written once and updated in place.</summary>
		private sealed class GreyboxMaterials
		{
			private readonly string _folder;

			public GreyboxMaterials(string folder)
			{
				_folder = folder;
				Low = Upsert("Barge_LowConfidence", new Color(0.93f, 0.45f, 0.20f), 0f);
				Medium = Upsert("Barge_MediumConfidence", new Color(0.95f, 0.76f, 0.22f), 0f);
				High = Upsert("Barge_HighConfidence", new Color(0.30f, 0.72f, 0.36f), 0f);
				Face = Upsert("BerthingFace", new Color(0.10f, 0.85f, 0.95f), 0.8f);
				Deck = Upsert("BridgeDeck", new Color(0.56f, 0.56f, 0.60f), 0f);
				Tower = Upsert("BridgeTower", new Color(0.62f, 0.55f, 0.47f), 0f);
				LabelBacking = UpsertLabelBacking();
				_strips = new Dictionary<string, Material>(StringComparer.Ordinal)
				{
					["berth-face"] = Face,
					["barge-side"] = Upsert("Fender_BargeSide", new Color(0.16f, 0.16f, 0.18f), 0f),
					["barge-end"] = Upsert("Fender_BargeEnd", new Color(0.32f, 0.30f, 0.36f), 0f),
					["pier"] = Upsert("Wall_Pier", new Color(0.50f, 0.36f, 0.22f), 0f),
					["bulkhead"] = Upsert("Wall_Bulkhead", new Color(0.70f, 0.70f, 0.68f), 0f),
					["shoreline"] = Upsert("Wall_Shoreline", new Color(0.33f, 0.38f, 0.28f), 0f),
					["not-contact"] = Upsert("Structure_NotContact", new Color(0.85f, 0.22f, 0.40f), 0.3f),
				};
			}

			private readonly Dictionary<string, Material> _strips;

			/// <summary>The material of a <see cref="StripGroups"/> entry.</summary>
			public Material Strip(string group) => _strips[group];

			/// <summary>Translucent black plate behind label text (URP Unlit, transparent, drawn just before the text).</summary>
			public Material LabelBacking { get; }

			public Material Low { get; }

			public Material Medium { get; }

			public Material High { get; }

			public Material Face { get; }

			public Material Deck { get; }

			public Material Tower { get; }

			public Color BridgeLabel => new Color(1f, 1f, 1f);

			public Material Barge(string confidence) =>
				confidence == "high" ? High : confidence == "medium" ? Medium : Low;

			public Color LabelColor(string confidence) =>
				confidence == "high" ? new Color(0.55f, 1f, 0.6f) : confidence == "medium" ? new Color(1f, 0.9f, 0.45f) : new Color(1f, 0.65f, 0.45f);

			private Material UpsertLabelBacking()
			{
				var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? throw new InvalidOperationException("URP Unlit shader not found.");
				var material = new Material(shader) { name = "LabelBacking" };
				material.SetFloat("_Surface", 1f);
				material.SetFloat("_Blend", 0f);
				material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
				material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
				material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
				material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
				material.SetFloat("_ZWrite", 0f);
				material.SetColor("_BaseColor", new Color(0f, 0f, 0f, 0.55f));
				material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
				material.SetOverrideTag("RenderType", "Transparent");

				// GUI/Text Shader draws at 3000; the plate must come first.
				material.renderQueue = 2999;
				return Store(material);
			}

			private Material Store(Material material)
			{
				Validate(material);
				var path = $"{_folder}/{material.name}.mat";
				var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
				if (existing == null)
				{
					AssetDatabase.CreateAsset(material, path);
					return material;
				}

				existing.shader = material.shader;
				existing.CopyPropertiesFromMaterial(material);
				existing.shaderKeywords = material.shaderKeywords;
				existing.globalIlluminationFlags = material.globalIlluminationFlags;
				existing.SetOverrideTag("RenderType", material.GetTag("RenderType", false));
				existing.renderQueue = material.rawRenderQueue;
				Validate(existing);
				EditorUtility.SetDirty(existing);
				Object.DestroyImmediate(material);
				return existing;
			}


			/// <summary>Run the same shader validation on new and updated assets, including URP's derived
			/// tags, pass flags and legacy color aliases. The import postprocessor only initializes new assets.</summary>
			private static void Validate(Material material)
			{
				var editor = UnityEditor.Editor.CreateEditor(material) as MaterialEditor;
				try
				{
					editor?.customShaderGUI?.ValidateMaterial(material);
				}
				finally
				{
					Object.DestroyImmediate(editor);
				}
			}

			private Material Upsert(string name, Color color, float emission)
			{
				var shader = Shader.Find("Universal Render Pipeline/Lit");
				if (shader == null)
				{
					throw new InvalidOperationException("URP Lit shader not found.");
				}

				var material = new Material(shader) { name = name };
				material.SetColor("_BaseColor", color);
				material.SetFloat("_Smoothness", 0.15f);
				material.SetFloat("_Metallic", 0f);
				if (emission > 0f)
				{
					material.EnableKeyword("_EMISSION");
					material.SetColor("_EmissionColor", color * emission);
					material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
				}

				return Store(material);
			}
		}
	}

	/// <summary>What a greybox build produced, and the cross-check against the pipeline's local coordinates.</summary>
	public sealed class GreyboxReport
	{
		public List<string> Barges { get; } = new List<string>();

		public List<string> Bridges { get; } = new List<string>();

		public List<string> Structures { get; } = new List<string>();

		public List<string> Errors { get; } = new List<string>();

		/// <summary>Largest gap between our (north, east) and the pipeline's local_coordinates, metres.</summary>
		public double MaxLocalCoordinateGap { get; private set; }

		internal void Check(string what, PlanePoint ours, double[] pipelineNorthEast)
		{
			var gap = Math.Sqrt(Math.Pow(ours.North - pipelineNorthEast[0], 2) + Math.Pow(ours.East - pipelineNorthEast[1], 2));
			MaxLocalCoordinateGap = Math.Max(MaxLocalCoordinateGap, gap);
			if (gap > GreyboxBuilder.LocalCoordinateTolerance)
			{
				Errors.Add($"{what}: tangent plane gives {ours}, pipeline local_coordinates (N {pipelineNorthEast[0]}, E {pipelineNorthEast[1]}), {gap:F3} m apart.");
			}
		}

		public override string ToString()
		{
			var text = new StringBuilder();
			text.AppendLine($"Greybox: {Barges.Count} landing barges, {Bridges.Count} bridges, {Structures.Count} landing structure groups; largest gap to pipeline local coordinates {MaxLocalCoordinateGap * 1000.0:F2} mm.");
			foreach (var line in Barges.Concat(Structures).Concat(Bridges))
			{
				text.AppendLine("  " + line);
			}

			foreach (var error in Errors)
			{
				text.AppendLine("  ERROR " + error);
			}

			return text.ToString();
		}
	}
}
