using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FerrySim.Slice.PlayModeTests
{
	/// <summary>
	/// (e) The hull sits on the water that is drawn: at positions across the slice the placed hull's WGS84 ellipsoidal
	/// height equals the drawn water mesh's under it, within 1 cm, in calm water and on a pack day's tide. Failure modes
	/// guarded: the placement left on its constant fallback (the slice-centre geoid offset from geoid_offset.json,
	/// centimetres off away from the centre), the adapter not wired, heights compared in the wrong frame (the mesh is
	/// measured through Cesium, the same way the hull is placed), and the hull placed at another level than the drawn
	/// tide (the drawn water moved after the placement read it).
	/// </summary>
	public class WaterHeightTests
	{
		private const double Tolerance = 0.01;

		private SliceScene _scene;

		[UnitySetUp]
		public IEnumerator SetUp()
		{
			_scene = new SliceScene();
			yield return _scene.Load();
		}

		[UnityTearDown]
		public IEnumerator TearDown()
		{
			yield return SliceScene.Unload();
		}

		[UnityTest]
		public IEnumerator ThePlacedHull_SitsOnTheDrawnWater_WithinACentimetre_AcrossTheSlice()
		{
			Assert.That(_scene.Placement.WaterSurface, Is.InstanceOf<WorldWaterHeight>(), "the placement reads the world's water");
			Assert.That(ConstantWaterSurface.TryReadGeoidOffset(SimPaths.Resolve(ConstantWaterSurface.DefaultGeoidOffsetFile), out var centreOffset, out var problem), Is.True, problem);
			_scene.StepsPerFrame(2);

			var positions = new List<(string Name, double Latitude, double Longitude)>
			{
				("off Wall St/Pier 11", 40.7030, -74.0040),
				("off South Williamsburg", 40.7092, -73.9722),
				("off Hunters Point South", 40.7400, -73.9660),
			};
			var worstFallbackError = 0.0;
			foreach (var (name, latitude, longitude) in positions)
			{
				var scenario = new ScenarioSettings
				{
					name = "water height " + name,
					vesselFile = _scene.Runner.Scenario.vesselFile,
					startLatitude = latitude,
					startLongitude = longitude,
					startHeadingDegrees = 30.0,
					scenarioStartUtc = "2026-06-15T16:00:00Z",
					environment = new ScenarioEnvironment { calm = true },
				};
				_scene.Restart(scenario, new ConstantControls(ControlInputs.Neutral));
				// Let the cameras follow and Cesium shift its origin to the new place.
				yield return SliceScene.Frames(6);
				Assert.That(_scene.Water.WaterLevelNavd88, Is.EqualTo(0.0), "calm water keeps the world scene's constant NAVD88 0.0 m level");

				var pose = _scene.Runner.RenderPose;
				var hull = _scene.Placement.Anchor.longitudeLatitudeHeight;
				var drawn = DrawnSurfaceHeight(_scene.Runner.transform.position, _scene.Runner.transform.up);
				var expected = _scene.Placement.WaterSurface.EllipsoidalHeight(pose.North, pose.East, _scene.Runner.Driver.SimulationTime);
				var geodetic = LocalTangentPlane.World.ToGeodetic(pose.North, pose.East);
				Debug.Log($"Water height {name}: hull {hull.z:F4} m, drawn water {drawn:F4} m, difference {(hull.z - drawn) * 100.0:F2} cm; slice-centre constant would be {(centreOffset - drawn) * 100.0:F2} cm off.");

				Assert.That(Math.Abs(pose.Down), Is.LessThan(1e-6), $"{name}: at rest, no heave");
				Assert.That(hull.x, Is.EqualTo(geodetic.LongitudeDegrees).Within(1e-9), $"{name}: longitude");
				Assert.That(hull.y, Is.EqualTo(geodetic.LatitudeDegrees).Within(1e-9), $"{name}: latitude");
				Assert.That(hull.z, Is.EqualTo(expected - pose.Down).Within(1e-6), $"{name}: placed at the adapter's height");
				Assert.That(hull.z, Is.EqualTo(drawn).Within(Tolerance), $"{name}: the hull sits on the drawn water");
				worstFallbackError = Math.Max(worstFallbackError, Math.Abs(centreOffset - drawn));
			}

			Assert.That(worstFallbackError, Is.GreaterThan(Tolerance), "control: the constant fallback would miss the drawn water by more than the tolerance somewhere");
		}

		/// <summary>
		/// On a pack day the drawn water stands at the tide, and the hull sits on it: near high water at Pier 11 on 2025-07-14,
		/// the drawn level is the pack's level at the vessel and the placed hull is on the mesh within 1 cm.
		/// </summary>
		[UnityTest]
		public IEnumerator OnAPackDay_TheHullSitsOnTheDrawnTide()
		{
			Assert.That(_scene.Bootstrap.ScenarioCatalog.TryCreate(new ScenarioChoice("87", "2025-07-14", 700, "t-class", berthId: "enc:0226015B0512B198"), out var scenario, out var problem), Is.True, problem);
			_scene.Restart(scenario, new ConstantControls(ControlInputs.Neutral));
			_scene.StepsPerFrame(2);
			yield return SliceScene.Frames(6);

			var pose = _scene.Runner.RenderPose;
			var level = _scene.Water.WaterLevelNavd88;
			var hull = _scene.Placement.Anchor.longitudeLatitudeHeight;
			var drawn = DrawnSurfaceHeight(_scene.Runner.transform.position, _scene.Runner.transform.up);
			Debug.Log($"Water height on the tide at Pier 11: level {level:F4} m NAVD88, hull {hull.z:F4} m, drawn water {drawn:F4} m, difference {(hull.z - drawn) * 100.0:F2} cm.");

			Assert.That(level, Is.EqualTo(_scene.Runner.Environment.WaterLevelNavd88), "the drawn level is the pack's at the vessel");
			Assert.That(level, Is.GreaterThan(0.7), "11:40 EDT is high water at The Battery that day");
			Assert.That(hull.z, Is.EqualTo(drawn).Within(Tolerance), "the hull sits on the drawn tide");
			Assert.That(hull.z, Is.EqualTo(_scene.Water.EllipsoidalHeight(pose.North, pose.East) - pose.Down).Within(1e-6));
		}

		/// <summary>
		/// The imported terrain is on the drawn water's NAVD88 frame: a terrain vertex at NAVD88 height h sits where the
		/// water would at level h, within 1 cm, across the slice. Control: as imported, the far tiles stand more than half
		/// a metre off.
		/// </summary>
		[UnityTest]
		public IEnumerator TheImportedTerrain_SitsOnTheWatersNavd88Frame()
		{
			var correction = _scene.Bootstrap.TerrainCorrection;
			Assert.That(correction, Is.Not.Null, "the terrain was moved onto the water's datum");
			Assert.That(correction.MeshesCorrected, Is.GreaterThan(500), "terrain tiles and chart structures");
			Assert.That(correction.ObjectsMoved, Is.GreaterThan(0), "aids and landing strips");
			var datum = _scene.Water.Datum;
			var zero = datum.GeoidCentre;
			var originals = _scene.Bootstrap.TerrainPrefab.transform.Find("Terrain");
			var placed = _scene.Bootstrap.Terrain.transform.Find("Terrain");
			Assert.That(placed.childCount, Is.EqualTo(originals.childCount));
			var worstPlaced = 0.0;
			var worstImported = 0.0;
			var checkedVertices = 0;
			for (var tile = 0; tile < placed.childCount; tile += 5)
			{
				var before = originals.GetChild(tile).GetComponent<MeshFilter>().sharedMesh.vertices;
				var after = placed.GetChild(tile).GetComponent<MeshFilter>().sharedMesh.vertices;
				Assert.That(after.Length, Is.EqualTo(before.Length));
				for (var i = 0; i < before.Length; i += 97)
				{
					var navd88 = before[i].y - zero;
					var expected = WaterSurface.SurfaceUpAt(datum, before[i].z, before[i].x, navd88);
					worstPlaced = Math.Max(worstPlaced, Math.Abs(after[i].y - expected));
					worstImported = Math.Max(worstImported, Math.Abs(before[i].y - expected));
					checkedVertices++;
				}
			}

			Debug.Log($"Imported terrain: {correction}; {checkedVertices} vertices checked, worst {worstPlaced * 100.0:F2} cm from the water's frame (as imported: {worstImported:F2} m).");
			Assert.That(checkedVertices, Is.GreaterThan(100));
			Assert.That(worstPlaced, Is.LessThan(Tolerance));
			Assert.That(worstImported, Is.GreaterThan(0.5), "control: as imported the terrain is off the water's frame");
			yield return null;
		}

		/// <summary>
		/// At the packs' lowest water nothing drawn for a barge stands above a floating greybox deck. The imported terrain
		/// draws no contact strip on a barge (berth face, barge side or end) but one on every fixed segment, and no chart
		/// structure for a barge or berth whose sides contacts.json gives (the greybox floats those); the greybox's every
		/// floating barge, berth and pontoon rides the drawn water, its fenders on its deck. No contact strip, floating chart
		/// structure (PONTON, HULKES) or greybox wall reaches over a deck, more than <see cref="DeckInset"/> m inside its
		/// edge, higher than the deck. Other fixed chart structures found over a deck are written to
		/// artifacts/unity/slice/low-water/ and the log, not checked. Failure modes guarded: the importer's fixed strips at
		/// NAVD88 +0.18 m, 0.35 m tall, drawn on barge sides (they show through a deck below about -0.85 m); a barge's chart
		/// outline extruded fixed to NAVD88 +1 m (through a deck below -0.2 m); a barge left fixed; fenders that do not ride
		/// their barge. Heights are compared in the world frame, where both prefabs are built.
		/// </summary>
		[UnityTest]
		public IEnumerator AtLowWater_NoBargeVisualStandsAboveAFloatingDeck()
		{
			// 2025-01-14 has the packs' lowest water; the run starts at Pier 11 at that day's lowest minute there.
			const string date = "2025-01-14";
			var pack = EnvironmentPackJson.Load(Path.Combine(SliceScene.RepoRoot, EnvironmentPacks.RegionFolder("lower-east-river"), "days", date));
			Assert.That(_scene.Bootstrap.ScenarioCatalog.TryCreate(new ScenarioChoice("87", date, 0, "t-class", berthId: "enc:02261FA117EFB198"), out var probe, out var problem), Is.True, problem);
			var at = probe.StartPosition;
			var day = new DataEnvironment(pack, pack.EpochUtc);
			var lowest = Enumerable.Range(0, 24 * 60).Where(m => m * 60.0 >= pack.CoverageStart && m * 60.0 <= pack.CoverageEnd).OrderBy(m => day.SampleWaterLevel(at.North, at.East, m * 60.0)).First();
			Assert.That(_scene.Bootstrap.ScenarioCatalog.TryCreate(new ScenarioChoice("87", date, lowest, "t-class", berthId: "enc:02261FA117EFB198"), out var scenario, out problem), Is.True, problem);
			_scene.Restart(scenario, new ConstantControls(ControlInputs.Neutral));
			_scene.StepsPerFrame(2);
			yield return SliceScene.Frames(6);
			var level = _scene.Water.WaterLevelNavd88;
			Assert.That(level, Is.EqualTo(_scene.Runner.Environment.WaterLevelNavd88), "the drawn level is the pack's at the vessel");
			Assert.That(level, Is.LessThan(-0.9), "low enough that a fixed strip at NAVD88 +0.18 m, 0.35 m tall, would show through a 1.2 m deck");

			var frame = Object.FindFirstObjectByType<WorldFrame>().transform;
			Vector3[] InFrame(MeshFilter filter) => filter.sharedMesh.vertices.Select(v => frame.InverseTransformPoint(filter.transform.TransformPoint(v))).ToArray();

			// The greybox's floating decks: the top of every hull that rides the water, as triangles in the frame.
			var decks = new List<Deck>();
			var floatingRenderers = new HashSet<Renderer>();
			var worstFender = float.NegativeInfinity;
			foreach (var root in Object.FindObjectsByType<FloatOnWater>(FindObjectsSortMode.None))
			{
				Assert.That(root.transform.localPosition.y, Is.EqualTo((float)level).Within(1e-4f), $"{root.name} rides the drawn water");
				floatingRenderers.UnionWith(root.GetComponentsInChildren<Renderer>(true));
				foreach (var hull in root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name == "Hull"))
				{
					var deck = new Deck(hull.transform.parent.name, InFrame(hull), hull.sharedMesh.triangles);
					Assert.That(deck.Cap, Is.Not.Empty, $"{deck.Name}: a deck");
					decks.Add(deck);
					var fenders = hull.transform.parent.Find("Fenders");
					if (fenders != null)
					{
						var above = InFrame(fenders.GetComponent<MeshFilter>()).Max(v => v.y) - deck.Top;
						worstFender = Math.Max(worstFender, above);
						Assert.That(above, Is.LessThanOrEqualTo(FenderAboveDeck + 1e-3f), $"{deck.Name}: its fenders ride its deck");
					}
				}
			}

			Assert.That(decks.Count, Is.GreaterThanOrEqualTo(11), "a deck for every landing at least");

			// What contacts.json says floats: the barge kinds, their segments by importer strip name, and their charted structures.
			var path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", LandingGeometry.DefaultContactsAssetPath);
			var bargeKinds = new[] { "berth-face", "barge-side", "barge-end" };
			var kinds = new Dictionary<string, string>();
			var afloat = new HashSet<string>(StringComparer.Ordinal);
			foreach (var stop in JArray.Parse(File.ReadAllText(path)).GroupBy(c => (string)c["stop_id"]))
			{
				var index = 0;
				foreach (var contact in stop)
				{
					var kind = (string)contact[LandingGeometry.KindField];
					kinds[$"{stop.Key}-Contact-{index++}"] = kind;
					if (bargeKinds.Contains(kind) && contact["lnam"]?.Type == JTokenType.String)
					{
						afloat.Add("structure-" + (string)contact["lnam"]);
					}
				}
			}

			var terrain = _scene.Bootstrap.Terrain.transform;
			var strips = terrain.Find("Landings").GetComponentsInChildren<MeshFilter>(true);
			Assert.That(strips.Select(s => s.name).Where(name => !kinds.ContainsKey(name)), Is.Empty, "every strip is a contact segment's");
			Assert.That(strips.Where(s => bargeKinds.Contains(kinds[s.name])).Select(s => s.name), Is.Empty, "no contact strip drawn on a barge: barges float");
			Assert.That(strips.Select(s => s.name).OrderBy(n => n, StringComparer.Ordinal),
				Is.EqualTo(kinds.Where(k => !bargeKinds.Contains(k.Value)).Select(k => k.Key).OrderBy(n => n, StringComparer.Ordinal)), "a strip on every fixed segment");
			var structures = terrain.Find("Structures").GetComponentsInChildren<MeshFilter>(true);
			Assert.That(afloat, Is.Not.Empty);
			Assert.That(structures.Select(s => s.name).Where(afloat.Contains), Is.Empty, "no chart structure drawn for a barge or berth the greybox floats");

			// Anything fixed reaching over a deck's interior: the terrain's strips and chart structures, and the greybox's walls.
			// The chart classes come from the pipeline's shoreline_constructions.geojson, which the importer extrudes.
			var classes = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var feature in JObject.Parse(File.ReadAllText(Path.Combine(SliceScene.RepoRoot, "data/processed/lower-east-river/shoreline_constructions.geojson")))["features"])
			{
				classes["structure-" + (string)feature["properties"]["id"]] = (string)feature["properties"]["feature_class"];
			}

			var floatingClasses = new[] { "PONTON", "HULKES" };
			var greyboxWalls = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None)
				.Where(f => f.name == "Walls" && !f.transform.IsChildOf(terrain) && f.TryGetComponent<Renderer>(out var r) && !floatingRenderers.Contains(r)).ToArray();
			Assert.That(greyboxWalls, Is.Not.Empty, "the greybox's fixed walls");
			var checkedFixed = strips.Concat(structures.Where(s => classes.TryGetValue(s.name, out var c) && floatingClasses.Contains(c))).Concat(greyboxWalls).ToArray();
			var otherFixed = structures.Where(s => !checkedFixed.Contains(s)).ToArray();
			var overDecks = new List<string>();
			var otherOverDecks = new List<string>();
			var lowestDeck = decks.Min(d => d.Top);
			var samples = 0;
			foreach (var filter in checkedFixed.Concat(otherFixed))
			{
				var found = checkedFixed.Contains(filter) ? overDecks : otherOverDecks;
				var worst = new Dictionary<string, float>();
				foreach (var point in EdgeSamples(InFrame(filter), filter.sharedMesh.triangles, lowestDeck))
				{
					samples++;
					foreach (var deck in decks)
					{
						if (point.y > deck.Top + 1e-3f && deck.Inside(point, DeckInset))
						{
							worst[deck.Name] = Math.Max(worst.TryGetValue(deck.Name, out var w) ? w : 0f, point.y - deck.Top);
						}
					}
				}

				found.AddRange(worst.Select(w => $"{filter.name} ({(classes.TryGetValue(filter.name, out var c) ? c : kinds.TryGetValue(filter.name, out var k) ? k : "greybox")}) up to {w.Value:F3} m above {w.Key}'s deck"));
			}

			var folder = SliceScene.FreshFolder("low-water");
			_scene.Bootstrap.Scenarios.TryGetStartDepth(new ScenarioChoice("87", date, lowest, "t-class", berthId: "enc:0226015B0512B198"), out var formerDepth);
			_scene.Bootstrap.Scenarios.TryGetStartDepth(new ScenarioChoice("87", date, lowest, "t-class", berthId: "enc:02261FA117EFB198"), out var movedDepth);
			File.WriteAllLines(Path.Combine(folder, "over-decks.txt"), new[]
			{
				$"Pier 11, {date} {lowest / 60:D2}:{lowest % 60:D2} local, drawn level {level:F3} m NAVD88; {decks.Count} floating decks; inset {DeckInset} m.",
				$"Same low-water time: former south under-keel {formerDepth.UnderKeel:F6} m; east under-keel {movedDepth.UnderKeel:F6} m. All floating decks remain checked.",
				$"Checked (contact strips, floating chart structures, greybox walls): {checkedFixed.Length} meshes, {overDecks.Count} over a deck.",
			}.Concat(overDecks).Concat(new[] { $"Other fixed chart structures, not checked: {otherFixed.Length} meshes, {otherOverDecks.Count} over a deck." }).Concat(otherOverDecks));

			Debug.Log($"Low water at Pier 11 on {date} {lowest / 60:D2}:{lowest % 60:D2} local: drawn level {level:F3} m NAVD88; {decks.Count} floating decks; " +
				$"{strips.Length} contact strips and {structures.Length} chart structures in the imported terrain ({afloat.Count} barge or berth structures left to the greybox), " +
				$"{samples} fixed points above the lowest deck; fenders at most {worstFender:F3} m above their deck. Other fixed structures over a deck (not checked): " +
				(otherOverDecks.Count == 0 ? "none" : string.Join("; ", otherOverDecks)));
			Assert.That(overDecks, Is.Empty, string.Join("\n", overDecks.Take(40)));
		}

		/// <summary>How far the greybox's fender strips rise above their deck, m (GreyboxBuilder.FenderOffset).</summary>
		private const float FenderAboveDeck = 0.05f;

		/// <summary>How far inside a deck's edge a fixed structure counts as over the deck, m: a wall along a barge's edge, such as the bulkhead behind it, does not.</summary>
		private const float DeckInset = 0.4f;

		/// <summary>Points every 0.5 m or less along every triangle edge of a mesh (frame vertices), those higher than <paramref name="above"/>.</summary>
		private static IEnumerable<Vector3> EdgeSamples(Vector3[] vertices, int[] triangles, float above)
		{
			for (var i = 0; i < triangles.Length; i += 3)
			{
				for (var e = 0; e < 3; e++)
				{
					var a = vertices[triangles[i + e]];
					var b = vertices[triangles[i + (e + 1) % 3]];
					if (a.y <= above && b.y <= above)
					{
						continue;
					}

					var steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(a, b) / 0.5f));
					for (var k = 0; k <= steps; k++)
					{
						var point = Vector3.Lerp(a, b, k / (float)steps);
						if (point.y > above)
						{
							yield return point;
						}
					}
				}
			}
		}

		/// <summary>A floating hull's deck in the frame: its top triangles, its edge and its height.</summary>
		private sealed class Deck
		{
			private readonly Vector2 _min;
			private readonly Vector2 _max;

			public Deck(string name, Vector3[] vertices, int[] triangles)
			{
				Name = name;
				Top = vertices.Max(v => v.y);
				var cap = new List<Vector2>();
				var edges = new Dictionary<(int, int), int>();
				for (var i = 0; i < triangles.Length; i += 3)
				{
					var t = new[] { triangles[i], triangles[i + 1], triangles[i + 2] };
					if (t.All(k => vertices[k].y > Top - 1e-3f))
					{
						cap.AddRange(t.Select(k => new Vector2(vertices[k].x, vertices[k].z)));
						for (var e = 0; e < 3; e++)
						{
							var key = (Math.Min(t[e], t[(e + 1) % 3]), Math.Max(t[e], t[(e + 1) % 3]));
							edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
						}
					}
				}

				Cap = cap.ToArray();
				_min = new Vector2(Cap.Min(c => c.x), Cap.Min(c => c.y));
				_max = new Vector2(Cap.Max(c => c.x), Cap.Max(c => c.y));
				Edge = edges.Where(e => e.Value == 1).Select(e => (new Vector2(vertices[e.Key.Item1].x, vertices[e.Key.Item1].z), new Vector2(vertices[e.Key.Item2].x, vertices[e.Key.Item2].z))).ToArray();
			}

			public string Name { get; }

			public float Top { get; }

			public Vector2[] Cap { get; }

			public (Vector2 A, Vector2 B)[] Edge { get; }

			/// <summary>Whether a frame point lies, seen from above, on the deck at least <paramref name="inset"/> m inside its edge.</summary>
			public bool Inside(Vector3 point, float inset)
			{
				var p = new Vector2(point.x, point.z);
				if (p.x < _min.x || p.y < _min.y || p.x > _max.x || p.y > _max.y)
				{
					return false;
				}

				var on = false;
				for (var i = 0; i < Cap.Length && !on; i += 3)
				{
					var d1 = Side(p, Cap[i], Cap[i + 1]);
					var d2 = Side(p, Cap[i + 1], Cap[i + 2]);
					var d3 = Side(p, Cap[i + 2], Cap[i]);
					on = !((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f));
				}

				return on && Edge.All(e => Distance(p, e.A, e.B) >= inset);
			}

			private static float Side(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

			private static float Distance(Vector2 p, Vector2 a, Vector2 b)
			{
				var ab = b - a;
				var t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
				return Vector2.Distance(p, a + ab * t);
			}
		}

		/// <summary>
		/// The drawn water mesh's height under a Unity point: a ray down the local vertical against every triangle of
		/// the mesh, the hit taken through the Cesium georeference to ECEF and then to a WGS84 ellipsoidal height.
		/// </summary>
		private double DrawnSurfaceHeight(Vector3 point, Vector3 up)
		{
			var water = _scene.Water;
			var mesh = water.Mesh;
			Assert.That(mesh, Is.Not.Null, "the water mesh is built");
			var vertices = mesh.vertices;
			var triangles = mesh.triangles;
			var frame = water.transform;
			var origin = frame.InverseTransformPoint(point + up * 50f);
			var direction = frame.InverseTransformDirection(-up).normalized;
			for (var i = 0; i < triangles.Length; i += 3)
			{
				if (Intersect(origin, direction, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]], out var distance))
				{
					var hit = frame.TransformPoint(origin + direction * distance);
					var ecef = _scene.Georeference.TransformUnityPositionToEarthCenteredEarthFixed(new double3(hit.x, hit.y, hit.z));
					return CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(ecef).z;
				}
			}

			Assert.Fail("the vertical through the hull does not meet the water mesh");
			return double.NaN;
		}

		/// <summary>Möller–Trumbore ray-triangle intersection, both faces.</summary>
		private static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
		{
			distance = 0f;
			var ab = b - a;
			var ac = c - a;
			var p = Vector3.Cross(direction, ac);
			var determinant = Vector3.Dot(ab, p);
			if (Mathf.Abs(determinant) < 1e-9f)
			{
				return false;
			}

			var inverse = 1f / determinant;
			var s = origin - a;
			var u = Vector3.Dot(s, p) * inverse;
			if (u < 0f || u > 1f)
			{
				return false;
			}

			var q = Vector3.Cross(s, ab);
			var v = Vector3.Dot(direction, q) * inverse;
			if (v < 0f || u + v > 1f)
			{
				return false;
			}

			distance = Vector3.Dot(ac, q) * inverse;
			return distance > 0f;
		}
	}
}
