using System;
using System.Collections.Generic;
using System.Text;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.World;
using FerrySim.Sim;
using FerrySim.World.Import;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	/// <summary>Resident navigation visuals. Build once from the verified pack, then bind to the run's render clock.</summary>
	[DefaultExecutionOrder(110)]
	public sealed class HarborNavigationWorld : MonoBehaviour
	{
		private readonly List<HarborBargeView> _barges = new List<HarborBargeView>();
		private readonly List<HarborLabelGroup> _labels = new List<HarborLabelGroup>();
		private readonly List<HarborAidView> _aids = new List<HarborAidView>();
		private readonly List<HarborSpanView> _spans = new List<HarborSpanView>();
		private readonly List<SignalLight> _bridgeLights = new List<SignalLight>();
		private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
		private readonly List<GameObject> _roots = new List<GameObject>();
		private IWaterSurface _water;
		private Atmosphere _atmosphere;
		private VesselSimulationRunner _runner;
		private Camera _camera;
		private Material _structure, _aidMaterial, _labelMaterial;
		private Font _font;
		private Mesh _cube;
		public IReadOnlyList<HarborBargeView> Barges => _barges;
		public IReadOnlyList<HarborLabelGroup> Labels => _labels;
		public IReadOnlyList<HarborAidView> Aids => _aids;
		public IReadOnlyList<HarborSpanView> Spans => _spans;
		public IReadOnlyList<SignalLight> BridgeSignals => _bridgeLights;
		public AidLights Lights { get; private set; }
		public HarborQueryIndex Queries { get; private set; }
		public string SelectedStopId { get; set; } = "";
		public int BoundaryAidsOmitted { get; private set; }
		public int EstimatedCharacters { get; private set; }
		private readonly List<string> _omittedSpanIds = new List<string>();
		private readonly List<string> _edgeAnchoredSpanIds = new List<string>();
		public IReadOnlyList<string> OmittedSpanIds => _omittedSpanIds;
		public IReadOnlyList<string> EdgeAnchoredSpanIds => _edgeAnchoredSpanIds;

		public void Build(HarborRuntimePack pack, CesiumGeoreference georeference, IWaterSurface water, Atmosphere atmosphere)
		{
			if (Queries != null) throw new InvalidOperationException("Build a new navigation batch for a replacement pack.");
			if (pack == null || georeference == null || water == null) throw new ArgumentNullException("Navigation needs a pack, georeference and water surface.");
			_water = water; _atmosphere = atmosphere; Queries = pack.Queries; Lights = new AidLights();
			_structure = Material("FerrySim/SolidColour", new Color(.34f, .39f, .42f));
			_aidMaterial = Material("FerrySim/SolidColour", Color.white);
			_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			_labelMaterial = Material("FerrySim/HarborLabel", Color.white); _labelMaterial.mainTexture = _font.material.mainTexture;
			_cube = Cube(); _owned.Add(_cube);
			var identities = new HashSet<string>(StringComparer.Ordinal);
			foreach (var barge in pack.Barges)
			{
				if (!identities.Add(barge.Id)) throw new ArgumentException("Duplicate barge structure: " + barge.Id);
				var n = .5 * (barge.Geometry.MinimumNorth + barge.Geometry.MaximumNorth); var e = .5 * (barge.Geometry.MinimumEast + barge.Geometry.MaximumEast);
				var host = Root(barge.Id + " (freeboard/depth estimated)", georeference.transform);
				var anchor = new NavigationAnchor(host, georeference, n, e, water.EllipsoidalHeight(n, e, 0));
				var view = host.AddComponent<HarborBargeView>(); view.Source = barge; view.Anchor = anchor;
				view.Details = BargeDetails(barge);
				Mesh(host, NavigationMesh.Extrude(barge.Geometry, anchor, anchor.Height - view.Draft, anchor.Height + view.Freeboard), _structure);
				_barges.Add(view);
			}
			foreach (var stop in pack.Stops)
			{
				var host = Root(stop.Name, georeference.transform);
				new NavigationAnchor(host, georeference, stop.Position.North, stop.Position.East, water.EllipsoidalHeight(stop.Position.North, stop.Position.East, 0) + 7);
				var label = host.AddComponent<HarborLabelGroup>(); label.Source = stop;
				var names = new StringBuilder(stop.Name); var details = new StringBuilder();
				foreach (var berth in stop.Berths)
				{
					names.Append('\n').Append(berth.Name);
					details.Append(berth.Name).Append(": ").Append(berth.Record["berth_use_crew_note"].Text).Append(' ').Append(berth.Record["berth_note"].Text).Append('\n');
				}
				foreach (var barge in _barges) foreach (var id in barge.Source.StopIds) if (id.Text == stop.Id) details.Append(barge.Details).Append('\n');
				label.Details = details.ToString(); label.Initialize(_font, _labelMaterial, names.ToString()); _labels.Add(label);
			}
			foreach (var aid in pack.Aids)
			{
				if (aid.SourceId == "0226022294660032" || aid.SourceId == "02260222946E0032") { BoundaryAidsOmitted++; continue; }
				if (!pack.Geoid.TrySample(aid.Position.North, aid.Position.East, out var datum)) throw new ArgumentException("Aid outside canonical datum: " + aid.Id);
				var host = Root(aid.Properties["name"].Text ?? aid.Id, georeference.transform);
				new NavigationAnchor(host, georeference, aid.Position.North, aid.Position.East, datum);
				var view = host.AddComponent<HarborAidView>(); view.Source = aid;
				var body = Box(host.transform, "Aid body (dimensions estimated)", new Vector3(0, 1.5f, 0), new Vector3(1.2f, 3, 1.2f), _aidMaterial);
				view.Body = body.GetComponent<Renderer>();
				var colour = aid.Properties["colours"].Items;
				var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", SignalColours.Linear(AidLights.ColourOf(colour != null && colour.Count > 0 ? colour[0].Text : "W"))); view.Body.SetPropertyBlock(block);
				var details = new StringBuilder(aid.Id).Append("; height and body dimensions estimated; ");
				view.FirstLight = Lights.Count;
				var records = aid.Properties["lights"].Items;
				if (records != null) foreach (var record in records) AddAidCharacter(host.transform, view, record, details);
				view.LightCount = Lights.Count - view.FirstLight;
				view.Details = details.ToString(); _aids.Add(view);
			}
			foreach (var bridge in pack.Bridges)
			{
				var group = Root(bridge.Id, georeference.transform);
				foreach (var span in bridge.Spans) BuildSpan(span, bridge.Id, group.transform, georeference, pack);
			}
			atmosphere?.RegisterNavigationBatch(Lights, _bridgeLights);
			Tick(0, null, SelectedStopId, atmosphere != null && atmosphere.Sun != null && atmosphere.Sun.IsNight, 0);
		}

		public void Follow(VesselSimulationRunner runner, Camera camera) { _runner = runner; _camera = camera; }
		private void LateUpdate()
		{
			if (_runner == null || !_runner.IsInitialized) return;
			var driver = _runner.Driver;
			var renderTime = Math.Max(0, driver.SimulationTime - SimulationClock.FixedTimeStep + driver.InterpolationFactor * SimulationClock.FixedTimeStep);
			var sun = _atmosphere != null ? _atmosphere.Sun : null;
			Tick(renderTime, _camera, SelectedStopId, sun != null && sun.IsNight, sun != null ? sun.VisibilityMetres : 0);
		}

		/// <summary>No geodesy, parsing, allocation, or mesh/collider construction in the run loop.</summary>
		public void Tick(double renderTime, Camera camera, string selectedStopId, bool night, double visibilityMetres)
		{
			for (var i = 0; i < _barges.Count; i++) { var a = _barges[i].Anchor; a.SetHeight(_water.EllipsoidalHeight(a.North, a.East, renderTime)); }
			for (var i = 0; i < _labels.Count; i++) _labels[i].Refresh(camera, selectedStopId, night);
			var nearDistance = visibilityMetres > 0 ? Math.Min(2000, visibilityMetres) : 2000;
			for (var i = 0; i < _aids.Count; i++)
			{
				var aid = _aids[i];
				var visible = camera == null || (aid.transform.position - camera.transform.position).sqrMagnitude <= nearDistance * nearDistance;
				aid.Body.enabled = visible;
				for (var j = 0; j < aid.LightCount; j++) Lights.SetNearVisible(aid.FirstLight + j, visible);
			}
			// Far signal points use the shared Allard optics shader; never hide them with the body/terrain cull.
			_atmosphere?.RefreshNavigation(renderTime);
			if (_atmosphere == null) Lights.Show(renderTime, night);
		}

		private void AddAidCharacter(Transform parent, HarborAidView view, HarborValue record, StringBuilder details)
		{
			var notation = record["notation"].Text ?? record["full_notation"].Text ?? "";
			details.Append(notation).Append("; ");
			if (notation == "AlFl W G")
			{
				view.PeriodEstimated = true; EstimatedCharacters++; details.Append("period estimated, chart gives none; 4 s alternating W/G; ");
				var white = new LightCharacteristic { Rhythm = "F", Colour = "W", PeriodSeconds = 4, Groups = new[] { 1 } };
				white.SetSequence(new[] { 2.0, 2.0 }, new[] { true, false });
				var green = new LightCharacteristic { Rhythm = "F", Colour = "G", PeriodSeconds = 4, Groups = new[] { 1 } };
				green.SetSequence(new[] { 2.0, 2.0 }, new[] { false, true });
				AddLamp(parent, white, record, true); AddLamp(parent, green, record, true); return;
			}
			if (notation == "Fl W") { notation = "Fl W 4s"; view.PeriodEstimated = true; EstimatedCharacters++; details.Append("period estimated, chart gives none; 4 s; "); }
			if (!LightCharacteristic.TryParse(notation, out var character)) throw new ArgumentException("Unsupported aid character: " + notation);
			var sequence = record["sequence"].Items;
			if (sequence != null && sequence.Count > 0)
			{
				var durations = new double[sequence.Count]; var states = new bool[sequence.Count];
				for (var i = 0; i < sequence.Count; i++) { durations[i] = sequence[i]["seconds"].Number.Value; states[i] = sequence[i]["state"].Text == "light"; }
				character.SetSequence(durations, states);
			}
			AddLamp(parent, character, record);
		}

		private void AddLamp(Transform parent, LightCharacteristic character, HarborValue record, bool sequenceEstimated = false)
		{
			// Chart HEIGHT is MHW; use the same explicitly approximate display datum as bridges.
			var aid = parent.GetComponent<HarborAidView>().Source;
			var mhw = HarborDisplayDatum.NearestMhw(aid.Position.North, aid.Position.East, out _);
			var height = record["height_m"].Number.HasValue ? record["height_m"].Number.Value + mhw : 3.0;
			var lamp = Box(parent, "Lamp " + character.Colour, new Vector3(0, (float)height, 0), Vector3.one * .25f, _aidMaterial);
			var flasher = lamp.AddComponent<BuoyFlasher>(); flasher.Characteristic = character; flasher.Lens = lamp.GetComponent<Renderer>();
			var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", SignalColours.Linear(AidLights.ColourOf(character.Colour))); flasher.Lens.SetPropertyBlock(block);
			flasher.Lamp = lamp.AddComponent<Light>(); flasher.Lamp.intensity = 0; // State only; far-point shader owns attenuation.
			LightSector? sector = null;
			if (record["sector_start_deg"].Number.HasValue && record["sector_end_deg"].Number.HasValue)
				sector = new LightSector(record["sector_start_deg"].Number.Value, record["sector_end_deg"].Number.Value, new LightSectorEdge(0, 1, 1), new LightSectorEdge(0, 1, 1));
			Lights.Add(flasher, record["range_m"].Number ?? record["range_nm"].Number * NavigationLightRules.MetresPerNauticalMile, sector, sequenceEstimated);
		}

		private void BuildSpan(HarborSpan span, string bridgeId, Transform group, CesiumGeoreference geo, HarborRuntimePack pack)
		{
			var n = .5 * (span.Geometry.MinimumNorth + span.Geometry.MaximumNorth); var e = .5 * (span.Geometry.MinimumEast + span.Geometry.MaximumEast);
			var covered = pack.Geoid.TrySample(n, e, out var datum);
			if (!covered)
			{
				foreach (var polygon in span.Geometry.Polygons)
				{
					foreach (var ring in polygon.Rings)
					{
						foreach (var point in ring) if (pack.Geoid.TrySample(point.North, point.East, out datum)) { n = point.North; e = point.East; covered = true; break; }
						if (covered) break;
					}
					if (covered) break;
				}
				if (!covered) foreach (var line in span.Geometry.Lines)
				{
					foreach (var point in line) if (pack.Geoid.TrySample(point.North, point.East, out datum)) { n = point.North; e = point.East; covered = true; break; }
					if (covered) break;
				}
				if (covered) _edgeAnchoredSpanIds.Add(span.Id);
			}
			var host = new GameObject(span.Id + (covered ? " (deck/towers display estimates)" : " (outside coverage; not drawn)")); host.transform.SetParent(group, false);
			var view = host.AddComponent<HarborSpanView>(); view.Source = span; view.BridgeId = bridgeId;
			_spans.Add(view);
			if (!covered) { _omittedSpanIds.Add(span.Id); view.DisplayHeightBasis = "outside canonical datum coverage; no visual"; return; }
			view.Drawn = true;
			var mhw = HarborDisplayDatum.NearestMhw(n, e, out var basis);
			double? clearance = null;
			if (span.Clearances != null) foreach (var item in span.Clearances) if (item["clearance_datum"].Text == "MHW") { clearance = item["vertical_clearance_m"].Number ?? item["vertical_clearance_closed_m"].Number; if (clearance.HasValue) break; }
			var underside = span.VerticalBounds.Minimum ?? (clearance.HasValue ? mhw + clearance.Value : 15.0);
			var anchor = new NavigationAnchor(host, geo, n, e, datum);
			view.DisplayUndersideNavd88 = underside;
			view.DisplayHeightBasis = clearance.HasValue ? basis + "; per-span MHW clearance; deck thickness 3 m estimated" : "no charted clearance; display underside NAVD88 +15 m estimated";
			Mesh(host, NavigationMesh.Extrude(span.Geometry, anchor, datum + underside, datum + underside + 3), _structure);
			// A source-edge orientation gives a recognizable span, while extents/towers remain explicitly estimated.
			var along = Vector3.forward; var longest = 0f;
			var spanRing = span.Geometry.Polygons.Count > 0 ? span.Geometry.Polygons[0].Rings[0] : span.Geometry.Lines[0];
			for (var i = 1; i < spanRing.Count; i++)
			{
				var a = anchor.Local(spanRing[i - 1].North, spanRing[i - 1].East, datum); var b = anchor.Local(spanRing[i].North, spanRing[i].East, datum);
				var edge = b - a; edge.y = 0;
				if (edge.sqrMagnitude > longest) { longest = edge.sqrMagnitude; along = edge.normalized; }
			}
			var minAlong = float.PositiveInfinity; var maxAlong = float.NegativeInfinity;
			foreach (var point in spanRing) { var local = anchor.Local(point.North, point.East, datum); var projection = Vector3.Dot(local, along); minAlong = Mathf.Min(minAlong, projection); maxAlong = Mathf.Max(maxAlong, projection); }
			var middle = along * ((minAlong + maxAlong) * .5f); var half = (maxAlong - minAlong) * .4f;
			var across = Vector3.Cross(along, Vector3.up);
			var towerHeight = (float)(span.VerticalBounds.Maximum ?? (underside + 12));
			for (var side = -1; side <= 1; side += 2)
			{
				Box(host.transform, "Tower extent estimated", middle + along * (side * half) + Vector3.up * (towerHeight / 2), new Vector3(3, towerHeight, 3), _structure);
				for (var face = -1; face <= 1; face += 2)
				{
					var sector = new LightSector(270, 90, new LightSectorEdge(0, 1, 1), new LightSectorEdge(0, 1, 1));
					_bridgeLights.Add(SignalLight.Create(host.transform, "Nominal bridge pier signal (placement estimated)", middle + along * (side * half) + across * (face * 1.5f) + Vector3.up * (float)(mhw + BridgeLights.PierLightAboveHighWater), Quaternion.LookRotation(across * face), SignalColour.Red, sector, SignalLightOptics.IntensityForRange(BridgeLights.RangeMetres)));
				}
				_bridgeLights.Add(SignalLight.Create(host.transform, "Nominal bridge centre signal (placement estimated)", middle + across * (side * 4) + Vector3.up * (float)(underside - .5), Quaternion.identity, SignalColour.Green, LightSector.AllRound, SignalLightOptics.IntensityForRange(BridgeLights.RangeMetres)));
			}
		}

		private static string BargeDetails(HarborBarge barge)
		{
			var text = new StringBuilder(barge.Id).Append("; freeboard 1.2 m and draft 1.5 m estimated; ");
			var sources = barge.Record["provenance"].Items;
			if (sources != null) foreach (var source in sources)
			{
				text.Append(source["source"].Text == "derived-from-gtfs" ? "estimated from GTFS, not surveyed" : source["source"].Text).Append("; confidence ");
				var confidence = source["confidence"].Items;
				if (confidence != null) foreach (var item in confidence) text.Append(item.Text).Append(' ');
			}
			return text.ToString();
		}
		private GameObject Root(string name, Transform parent) { var host = new GameObject(name); host.transform.SetParent(parent, false); _roots.Add(host); return host; }
		private Material Material(string shader, Color color)
		{
			var found = Shader.Find(shader);
			if (found == null) throw new InvalidOperationException("Missing navigation shader: " + shader);
			var material = new Material(found); material.SetColor(material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", color); _owned.Add(material); return material;
		}
		private void Mesh(GameObject host, Mesh mesh, Material material) { _owned.Add(mesh); host.AddComponent<MeshFilter>().sharedMesh = mesh; host.AddComponent<MeshRenderer>().sharedMaterial = material; }
		private GameObject Box(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
		{
			var host = new GameObject(name); host.transform.SetParent(parent, false); host.transform.localPosition = position; host.transform.localScale = scale;
			host.AddComponent<MeshFilter>().sharedMesh = _cube; host.AddComponent<MeshRenderer>().sharedMaterial = material; return host;
		}
		private static Mesh Cube()
		{
			var mesh = new Mesh { name = "Navigation box" };
			mesh.vertices = new[] { new Vector3(-.5f,-.5f,-.5f),new Vector3(.5f,-.5f,-.5f),new Vector3(.5f,.5f,-.5f),new Vector3(-.5f,.5f,-.5f),new Vector3(-.5f,-.5f,.5f),new Vector3(.5f,-.5f,.5f),new Vector3(.5f,.5f,.5f),new Vector3(-.5f,.5f,.5f) };
			mesh.triangles = new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 }; mesh.RecalculateNormals(); return mesh;
		}
		private void OnDestroy()
		{
			if (_atmosphere != null) _atmosphere.UnregisterNavigationBatch(Lights);
			foreach (var root in _roots) if (root != null) { if (Application.isPlaying) Destroy(root); else DestroyImmediate(root); }
			foreach (var item in _owned) if (item != null) { if (Application.isPlaying) Destroy(item); else DestroyImmediate(item); }
		}
	}
}
