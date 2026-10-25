"""Reproducible per-landing evidence, using built slice inputs only.

Charts, GTFS and AIS are evidence with different dates and accuracy, not a berth
survey. This module never edits depth or changes a validation verdict.
"""
import json
import math
import textwrap

import numpy as np
import pandas as pd
import rasterio
from rasterio.features import geometry_mask
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from ferrydata import depth, enc, geodesy, landings, paths


def _load(out, name):
	return json.loads((out / name).read_text())['features']


def _parts(g):
	return list(g.geoms) if hasattr(g, 'geoms') else [g]


def _plot_geometry(ax, geometry, **kwargs):
	for g in _parts(geometry):
		if g.geom_type == 'Polygon':
			ax.plot(*g.exterior.xy, **kwargs)
		elif g.geom_type == 'LineString':
			ax.plot(*g.xy, **kwargs)
		elif g.geom_type == 'Point':
			ax.plot(g.x, g.y, marker='.', **kwargs)


def box_evidence(dataset, cudem, chart, polygon, face, outline, low, required, areas, constructions, land):
	"""Cell-centre sampling, identical inclusion rule to the depth check."""
	array = dataset.read(1)
	inside = geometry_mask([polygon], out_shape=array.shape, transform=dataset.transform, invert=True)
	rr, cc = np.where(inside & np.isfinite(array) & (array != dataset.nodata))
	if not len(rr):
		return {'sampled_cells': 0, 'note': 'No valid depth cells; depth is unknown.'}
	values = array[rr, cc].astype('float64')
	worst = int(np.argmax(values))
	r, c = int(rr[worst]), int(cc[worst])
	lon, lat = rasterio.transform.xy(dataset.transform, r, c)
	n, e = geodesy.to_local(lat, lon)
	point = Point(float(e), float(n))
	local_box = landings.to_local(polygon)
	chart_value = float(chart.read(1, window=rasterio.windows.Window(c, r, 1, 1))[0, 0])
	cudem_value = float(cudem.read(1, window=rasterio.windows.Window(c, r, 1, 1))[0, 0])
	ranges = [f['properties'] for f in areas if shape(f['geometry']).covers(Point(lon, lat))]
	return {
		'sampled_cells': len(values), 'shallow_cells': int(np.sum(low - values < required)),
		'min_depth_m': float(low - values[worst]), 'cudem_at_worst_depth_m': float(low - cudem_value),
		'chart_at_worst_depth_m': None if chart_value == chart.nodata else float(low - chart_value),
		'worst_cell': {'row': r, 'column': c, 'lat': lat, 'lon': lon, 'north_m': float(n), 'east_m': float(e)},
		'worst_cell_distance_to_face_m': face.distance(point),
		'worst_cell_distance_to_outline_m': outline.distance(point),
		'enc_areas_at_worst': [{'id': p['id'], 'min_depth_m_mllw': p['min_depth_m'], 'max_depth_m_mllw': p['max_depth_m']} for p in ranges],
		'approach_land_overlap_m2': local_box.intersection(land).area,
		'nearby_constructions_at_worst': [{'id': p['id'], 'feature_class': p['feature_class'], 'distance_m': g.distance(point)} for g, p in constructions if g.distance(point) < 10.],
	}


def explanation(props, evidence, required):
	if props['stop_id'] == '19' and props.get('crew_orientation'):
		verdict = 'PASS' if evidence['min_depth_m'] >= required else 'FAIL'
		return f"{verdict}: crew-selected NW long-face box minimum {evidence['min_depth_m']:.3f} m against {required:.2f} m. The crew bearing is independently supported by a clear surface corridor to DEPARE; charted always-submerged pier geometry is a disclosed depth hazard, not a surface wall. The rejected SW-end box and its 6 mm miss are recorded separately below; they are not the crew-selected berth approach."
	if evidence.get('min_depth_m', -np.inf) >= required:
		return f"PASS: sampled minimum {evidence['min_depth_m']:.3f} m meets {required:.2f} m. This is cached-grid coverage, not a surveyed or crew-verified berth depth."
	count = evidence.get('shallow_cells', 0)
	total = evidence['sampled_cells']
	base = f"FAIL retained: {count}/{total} cells below {required:.2f} m; minimum {evidence.get('min_depth_m', float('nan')):.3f} m. "
	if props['stop_id'] == '120':
		return base + 'Brooklyn Navy Yard has no matching ENC floating polygon. The ASCE-size face and gangway offset are estimates; the box is wholly in the broad 0–2 m MLLW area. Neither that area lower bound nor CUDEM resolves the operating berth. Cause remains unresolved placement versus near-structure bathymetry; do not infer actual navigable depth or move the estimate to chase a passing cell.'
	if props['stop_id'] == '8':
		return base + f"South Williamsburg uses the crew-selected NW long river side at {props['face_heading_deg_true']:.3f}° true. Crew orientation outranks the failed chart check; the dry MORFAC structure role remains unverified." + ' Its broad 0–2 m MLLW DEPARE supplies a zero-depth lower bound, not a 2 m measurement; CUDEM also reports shallow water beside the structure. Evidence supports a near-structure data limitation, but cannot prove the real bed is deeper. A berth survey/crew verification is still needed; neither depths nor the threshold were edited.'
	if props['stop_id'] == '87':
		return base + 'Pier 11 geometry changed to the southern charted pontoon and its outward side. The worst cell lies within one raster cell of the face and CUDEM reports an above-water elevation there; ENC provides only a 0–2 m MLLW bound at that cell. This is unresolved near-pier bathymetry despite corrected chart geometry, not proof that a ferry cannot berth. Multiple pontoons and schematic GTFS routes leave the operational assignment provisional.'
	if props['stop_id'] == '19':
		return base + 'North Williamsburg now uses the unobstructed southwest pontoon end. The worst cell lacks chart depth support; its exact distance and deficit remain in the cell evidence below. The original long-face corridor crossed a charted pier. The 30 m square is wider than this short end and remains a conservative approach envelope; this marginal raster result is not rounded up or presented as surveyed shoaling.'
	return base + 'Existing cached sources do not establish sufficient depth. Geometry and near-structure raster uncertainty remain explicit; no depth values or thresholds were changed.'


def surface_conflicts(face, outward_ne, constructions, raw_attributes):
	"""Disclose every surface blocker intersecting the unchanged direct-check sweep."""
	sweep = landings._face_sweep(face, outward_ne, .5, 12.)
	mid = face.interpolate(.5, normalized=True)
	a, b = face.coords[0], face.coords[-1]
	te, tn = (b[0] - a[0]) / math.dist(a, b), (b[1] - a[1]) / math.dist(a, b)
	n, e = outward_ne
	records = []
	for g, props in constructions:
		if props.get('water_level_effect_code') == 3 or not g.intersects(sweep):
			continue
		position = g.centroid
		de, dn = position.x - mid.x, position.y - mid.y
		along = de * te + dn * tn
		attrs = [r for r in raw_attributes.get(props['feature_class'], []) if r['LNAM'] == props['id']]
		records.append({'enc_id': props['id'], 'feature_class': props['feature_class'], 'source': 'NOAA ENC',
			'attributes': attrs, 'geometry_wgs84': landings.to_wgs84(g).__geo_interface__,
			'distance_to_face_m': g.distance(face), 'along_face_from_midpoint_m': along,
			'outward_from_face_midpoint_m': de * e + dn * n,
			'on_centre_line': abs(along) <= .5, 'centre_line_tolerance_m': .5,
			'interpretation': 'The crew answer outranks the chart check for orientation; the structure role remains unverified. A pile or dolphin beside a berth can be worked alongside; this is not a clearance or depth survey.'})
	return records


def write(slice_, approaches, baseline, low, required, target=None):
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	from matplotlib.lines import Line2D

	out = paths.processed_dir(slice_.name)
	target = target or paths.REPO_ROOT / 'artifacts' / 'landing-faces'
	target.mkdir(parents=True, exist_ok=True)
	faces = {f['properties']['stop_id']: f for f in _load(out, 'landing_faces.geojson')}
	constructions = [(landings.to_local(shape(f['geometry'])), f['properties']) for f in _load(out, 'shoreline_constructions.geojson')]
	areas = _load(out, 'depth_areas.geojson')
	raw_structures, _ = enc.read_layers(slice_, ['SLCONS', 'MORFAC'])
	raw_attributes = {name: json.loads(frame.drop(columns='geometry').to_json(orient='records')) for name, frame in raw_structures.items()}
	_, land = landings.chart_masks(out)
	routes = _load(out, 'routes.geojson')
	ais_path = paths.ais_processed_dir() / 'nycferry_tracks.parquet'
	ais = pd.read_parquet(ais_path) if ais_path.exists() else None
	if ais is not None:
		ais = ais[ais.sog_kn < .5]
	records, notes = [], []
	with rasterio.open(out / 'depth_merged_navd88.tif') as merged, rasterio.open(out / 'depth_navd88.tif') as cudem, rasterio.open(out / 'depth_chart_navd88.tif') as chart:
		array = merged.read(1)
		for item, original in zip(approaches, baseline):
			p = faces[item.props['stop_id']]['properties']
			outline = landings.to_local(shape(faces[p['stop_id']]['geometry']))
			face = landings.to_local(shape(p['face_geometry']))
			contact = landings.to_local(shape(p['contact_geometry']))
			n, e = p['outward_normal_ne']; mid = face.interpolate(.5, normalized=True)
			polygon = depth.landing_box_polygon(item.anchor_ne, (n, e))
			evidence = box_evidence(merged, cudem, chart, polygon, face, outline, low, required, areas, constructions, land)
			conflicts = surface_conflicts(face, (n, e), constructions, raw_attributes)
			submerged = []
			for hazard in p.get('submerged_hazards', []):
				g, _ = next((g, q) for g, q in constructions if q['id'] == hazard['enc_id'])
				raw = raw_structures['SLCONS']
				matching = raw[raw.LNAM == hazard['enc_id']]
				attributes = json.loads(matching.drop(columns='geometry').to_json(orient='records')) if not matching.empty else []
				rect = g.minimum_rotated_rectangle
				sizes = sorted(math.dist(a, b) for a, b in zip(rect.exterior.coords, list(rect.exterior.coords)[1:])) if rect.geom_type == 'Polygon' else [0., g.length]
				submerged.append({**hazard, 'attributes': attributes, 'geometry_wgs84': landings.to_wgs84(g).__geo_interface__,
					'area_m2': g.area, 'bounding_size_m': [sizes[-1], sizes[0]], 'face_intersection_length_m': g.intersection(face).length})
			alternative = None
			if p.get('crew_orientation'):
				# Retain the rejected SW-end result rather than attach its shallow
				# cell to the different, crew-selected NW-face approach.
				choices = []
				for edge in landings._straight_edges(orient(outline, sign=-1)):
					a, b = edge.coords[0], edge.coords[-1]; length = math.dist(a, b)
					normal = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
					angle = abs((landings.heading_deg(normal) - (p['face_heading_deg_true'] - 90) + 180) % 360 - 180)
					choices.append((angle, edge, normal))
				_, alternate_face, alternate_normal = min(choices, key=lambda x: x[0])
				am = alternate_face.interpolate(.5, normalized=True)
				alternative = box_evidence(merged, cudem, chart, depth.landing_box_polygon((am.y, am.x), alternate_normal), alternate_face, outline, low, required, areas, constructions, land)
				alternative['outward_bearing_deg_true'] = landings.heading_deg(alternate_normal)
				alternative['reason_rejected'] = 'SW short end points downriver; crew confirms the NW long side facing Manhattan.'
				point = Point(alternative['worst_cell']['east_m'], alternative['worst_cell']['north_m'])
				alternative['worst_cell_distance_to_submerged_hazards_m'] = [{'enc_id': h['enc_id'], 'distance_m': next(g for g, q in constructions if q['id'] == h['enc_id']).distance(point)} for h in submerged]
			parts = [landings.to_local(shape(f['geometry'])) for f in routes if set(f['properties']['route_ids']) & set(p.get('route_ids', []))]
			route = unary_union(parts)
			evidence['route_distance_at_60m_outward_m'] = route.distance(Point(mid.x + e * 60, mid.y + n * 60))
			evidence['route_distance_at_60m_inward_m'] = route.distance(Point(mid.x - e * 60, mid.y - n * 60))
			near = None
			if ais is not None:
				near = ais[((ais.north_m - mid.y) ** 2 + (ais.east_m - mid.x) ** 2) < 100 ** 2]
				distances = [outline.distance(Point(row.east_m, row.north_m)) for row in near.itertuples()]
				evidence['cached_ais_reports_below_0_5kn_within_10m_outline'] = sum(d < 10 for d in distances)
				evidence['ais_note'] = 'Historical 2024–2025 candidate-fleet reports; not unique visits, vessel footprint, a surveyed face, or proof of present berth assignment.'
			change = ('Crew selects the NW long side toward Manhattan. The SW short-end candidate is rejected. Submerged structures remain depth hazards without becoming surface contact walls.' if p['stop_id'] == '19' else
				'Changed from northern pontoon 0226111D16AAB198 to southern 0226015B0512B198; its long face directly adjoins DEPARE. Multiple operational berths remain provisional.' if p['stop_id'] == '87' else
				'Changed from southern ENC pontoon 02261ACE4C291C82 to AIS-preferred northern pontoon 02260960FDFF1C82 with a valid water-facing long side; multiple berth assignment remains provisional.' if p['stop_id'] == '17' and p['enc_id'] == '02260960FDFF1C82' else
				'Crew selects the NW long river side (305.042° true, 25.590 m), outranking the chart check for orientation. Independent chart support remains FAIL because dry MORFAC 022646E95F81764B (WATLEV=2) intersects the full swept-face check. The SW end retains its independent chart proof and no crew flag. The structure role remains unverified.' if p['stop_id'] == '8' else
				'Primary face retained; added connected exposed sides and ends where supported. Estimated placements and dimensions remain labelled.' if p['source'] == 'derived-from-gtfs' else
				'Primary face retained; exported connected exposed ENC outline sides without landward closure.')
			note = explanation(p, evidence, required)
			record = {'stop_id': p['stop_id'], 'stop_name': p['stop_name'], 'source': p['source'], 'enc_id': p['enc_id'],
				'confidence': p['confidence'], 'verification': p['verification'], 'face_length_m': face.length,
				'gtfs_to_face_m': p['gtfs_to_face_m'], 'water_side_evidence': p['water_side_evidence'],
				'candidate_evidence': p.get('candidate_evidence', []),
				'crew_evidence': p.get('crew_evidence'), 'crew_note': p.get('crew_note'), 'crew_notes_sha256': p.get('crew_notes_sha256'), 'crew_orientation': p.get('crew_orientation'),
				'submerged_hazards': submerged, 'surface_chart_conflicts': conflicts, 'rejected_sw_candidate': alternative,
				'contact_segments': len(contact.coords) - 1, 'contact_segment_evidence': p.get('contact_segment_evidence'), 'change': change, 'depth_evidence': evidence, 'depth_note': note}
			records.append(record)
			notes += ['', f"### {p['stop_name']} (stop {p['stop_id']})", '', f"Face: {face.length:.1f} m, {p['source']} {p['enc_id'] or '(GTFS/ASCE estimate)'}, {p['confidence']} confidence, {p['verification']}; {p['gtfs_to_face_m']:.1f} m from GTFS.",
				f"Water side: {p['water_side_evidence']['method']}; near-face DEPARE gap {p['water_side_evidence'].get('near_face_coverage_gap_m', 0):.1f} m along the 8 m corridor. {change}", note,
				f"Worst cell: row {evidence['worst_cell']['row']}, column {evidence['worst_cell']['column']}, {evidence['worst_cell_distance_to_face_m']:.2f} m from face; CUDEM depth there {evidence['cudem_at_worst_depth_m']:.3f} m, chart-supported depth {evidence['chart_at_worst_depth_m'] if evidence['chart_at_worst_depth_m'] is not None else 'unknown'}. Approach/LNDARE overlap {evidence['approach_land_overlap_m2']:.2f} m².",
				f"GTFS route distance at 60 m outward/inward: {evidence['route_distance_at_60m_outward_m']:.1f}/{evidence['route_distance_at_60m_inward_m']:.1f} m; schematic route geometry is not a berth survey. Cached slow AIS reports within 10 m of outline: {evidence.get('cached_ais_reports_below_0_5kn_within_10m_outline', 'unavailable')}; historical candidate-fleet evidence only.",
				f"![{p['stop_name']} evidence](../../../artifacts/landing-faces/{p['stop_id']}.png)"]
			if p.get('crew_note'):
				cn = p['crew_note']
				notes += ['', f"Crew provenance: {cn['id']} ({cn['kind']}); ordinal geometry confidence remains {p['confidence']}."]
				notes += ['', f"Crew note ({cn['date']}, {cn['source']}, SHA256 {p['crew_notes_sha256']}): {cn['statement']}", cn['limitations']]
				for key in ('modelling_decision', 'follow_up'):
					if cn.get(key): notes.append(cn[key])
			if p.get('crew_orientation'):
				co = p['crew_orientation']
				notes += ['', f"Bearing to opposite Manhattan LNDARE {co['opposite_shore_enc_id']} (identified from Manhattan stop {co['opposite_shore_reference_stop_id']}): {co['bearing_to_manhattan_deg_true']:.3f}° true. Selected face normal: {p['face_heading_deg_true']:.3f}° true.",
					'All side outward bearings / lengths: ' + '; '.join(f"{s['outward_bearing_deg_true']:.3f}° / {s['length_m']:.2f} m" for s in co['side_bearings']),
					f"Independent chart support: {'PASS' if p['water_side_verified_by_pipeline'] else 'FAIL'} ({p['water_side_evidence'].get('chart_method', p['water_side_evidence']['method'])}); crew-reported support: {p['water_side_crew_reported']}."]
			for conflict in conflicts:
				position = conflict['geometry_wgs84']
				notes += ['', f"Surface chart conflict {conflict['enc_id']} ({conflict['feature_class']}), source {conflict['source']}: {conflict['distance_to_face_m']:.6f} m from face. Position WGS84: `{json.dumps(position)}`. Outward from face midpoint {conflict['outward_from_face_midpoint_m']:.6f} m; signed along-face offset {conflict['along_face_from_midpoint_m']:.6f} m toward the directed face end (negative toward start). {'On centre line' if conflict['on_centre_line'] else 'Off to one side of the face centre line'} (classification tolerance ±0.5 m).",
					f"Every readable raw ENC attribute (null means unavailable): `{json.dumps(conflict['attributes'], separators=(',', ':'))}`", conflict['interpretation']]
			for hazard in submerged:
				notes += ['', f"Submerged approach hazard {hazard['enc_id']}: NOAA ENC {hazard['cell']}, CATSLC={hazard['CATSLC']} (pier/jetty for code 4), WATLEV=3 (always under water/submerged), SORDAT={hazard['SORDAT']}. Corridor overlap {hazard['corridor_overlap_m2']:.2f} m²; distance from face {hazard['distance_to_face_m']:.3f} m; face intersection {hazard['face_intersection_length_m']:.2f} m. Polygon area {hazard['area_m2']:.2f} m²; bounding dimensions {hazard['bounding_size_m'][0]:.2f} × {hazard['bounding_size_m'][1]:.2f} m.",
					f"Raw ENC attributes: `{json.dumps(hazard['attributes'], separators=(',', ':'))}`",
					'For CATSLC=4 this is charted submerged pier geometry, not an identified ramp (12), fender (14), gangway or wave attenuator. Unpublished height/clearance and no explanatory INFORM leave its physical role unresolved. Its footprint is excluded only from surface-corridor blocking; depth checks and chart geometry are unchanged. At North Williamsburg the ENC pier source predates the PONTON source; that alone does not establish obsolescence. Full geometry is in diagnosis.json.']
			if alternative:
				notes += ['', f"Rejected SW candidate: normal {alternative['outward_bearing_deg_true']:.3f}°, minimum {alternative['min_depth_m']:.9f} m; deficit {required - alternative['min_depth_m']:.9f} m, {alternative['shallow_cells']}/{alternative['sampled_cells']} cells fail. Worst cell {alternative['worst_cell']['row']}/{alternative['worst_cell']['column']} is retained in diagnosis.json. Distances to submerged hazards: {alternative['worst_cell_distance_to_submerged_hazards_m']}. Do not attribute this cell to a submerged pier unless positions match; these are different approach boxes."]
			candidates = sorted(p.get('candidate_evidence', []), key=lambda c: (-c['ais_slow_reports_within_10m'], c['enc_id']))
			if len(candidates) > 1:
				notes += ['', ('For stop 87, candidate priority is historical AIS usage first, then water-side validity. Other stops retain their existing assignment pending separate crew review. Reports are SOG < 0.5 kn within 150 m of GTFS, then within 10 m of the ENC polygon; not unique visits and no antenna correction. GTFS route distances are schematic evidence and do not override a blocked corridor.'), '',
					'| ENC pontoon | slow reports | GTFS distance (m) | route outward / inward at 60 m (m) | water side |', '|---|---|---|---|---|']
				for candidate in candidates:
					notes.append(f"| {candidate['enc_id']} | {candidate['ais_slow_reports_within_10m']} | {candidate['distance_to_gtfs_m']:.1f} | {candidate['route_distance_60m_outward_m']:.1f} / {candidate['route_distance_60m_inward_m']:.1f} | {candidate['water_side_evidence']['method']} ({'pass' if candidate['water_side_evidence']['verified'] else 'FAIL'}) |")
					notes.append(f"Candidate side evidence: `{json.dumps(candidate['sides'], separators=(',', ':'))}`")
				if p['stop_id'] == '87':
					notes += ['', 'Pier 11 middle and northern candidates fail all evaluated sides; the approved corridor is obstructed and/or its far end lacks DEPARE. Southern is the remaining water-side-valid candidate, not the AIS usage winner. The two unselected pontoons are NOT contact segments in this task; follow-up: represent every physical structure with matching contact geometry and greybox visuals.']
			fig, ax = plt.subplots(figsize=(10, 9))
			half = 85.; center_e, center_n = item.props['east_m'], item.props['north_m']
			east, north = np.meshgrid(np.linspace(center_e - half, center_e + half, 171), np.linspace(center_n - half, center_n + half, 171))
			lat, lon = geodesy.from_local(north, east)
			rr, cc = rasterio.transform.rowcol(merged.transform, lon.ravel(), lat.ravel())
			grid = array[np.clip(rr, 0, array.shape[0] - 1), np.clip(cc, 0, array.shape[1] - 1)].reshape(east.shape)
			grid = np.where(grid == merged.nodata, np.nan, low - grid)
			image = ax.pcolormesh(east, north, grid, cmap='viridis', vmin=-1, vmax=16, shading='auto')
			ax.contour(east, north, grid, levels=[required], colors='white', linestyles='dashed', linewidths=.7)
			for g in _parts(land.intersection(Point(center_e, center_n).buffer(130))):
				if g.geom_type == 'Polygon': ax.fill(*g.exterior.xy, color='.65', alpha=.8)
			for g, props in constructions:
				if g.distance(Point(center_e, center_n)) < 120:
					_plot_geometry(ax, g, color='magenta', lw=.8)
			for hazard in submerged:
				g = next(g for g, q in constructions if q['id'] == hazard['enc_id'])
				if g.geom_type == 'Polygon': ax.fill(*g.exterior.xy, facecolor='orange', alpha=.3, hatch='///')
				ax.annotate(f"{hazard['enc_id']}\nCATSLC {hazard['CATSLC']} · WATLEV 3\nSORDAT {hazard['SORDAT']} · {hazard['corridor_overlap_m2']:.0f} m²", (g.centroid.x, g.centroid.y), xytext=(25, 45), textcoords='offset points', fontsize=7, color='white', bbox={'facecolor': '.15', 'alpha': .8}, arrowprops={'arrowstyle': '-', 'color': 'white'})
			for area in areas:
				g = landings.to_local(shape(area['geometry']))
				if g.distance(Point(center_e, center_n)) < 100:
					_plot_geometry(ax, g, color='cornflowerblue', lw=.6, alpha=.6)
			if near is not None: ax.scatter(near.east_m, near.north_m, s=2, c='white', alpha=.12)
			_plot_geometry(ax, outline, color='gold', lw=1)
			_plot_geometry(ax, contact, color='gold', lw=4)
			_plot_geometry(ax, face, color='cyan', lw=2)
			_plot_geometry(ax, landings.to_local(polygon).boundary, color='red', lw=2)
			ax.arrow(mid.x, mid.y, e * 18, n * 18, color='cyan', width=.35, head_width=3, length_includes_head=True)
			if p['water_side_evidence'].get('chart_method', p['water_side_evidence']['method']).startswith('topology'):
				corridor = LineString([(mid.x, mid.y), (mid.x + e * 30, mid.y + n * 30)]).buffer(4, cap_style=2)
				_plot_geometry(ax, corridor, color='lime', lw=2)
			if p.get('crew_orientation'):
				for edge in landings._straight_edges(orient(outline, sign=-1)):
					a, b = edge.coords[0], edge.coords[-1]; length = math.dist(a, b)
					angle = landings.heading_deg(((b[0] - a[0]) / length, (a[1] - b[1]) / length))
					ax.text(edge.centroid.x, edge.centroid.y, f"{angle:.1f}°", fontsize=7, color='white')
				ax.text(.02, .02, f"Manhattan shore {p['crew_orientation']['bearing_to_manhattan_deg_true']:.1f}°\ncrew + chart {p['water_side_evidence'].get('chart_method')}\nsubmerged pier is a depth hazard\nrejected SW box: {alternative['min_depth_m']:.3f} m", transform=ax.transAxes, fontsize=8, color='white', bbox={'facecolor': '.2', 'alpha': .8})
			if p['stop_id'] == '8' and conflicts:
				conflict = conflicts[0]
				g = next(g for g, q in constructions if q['id'] == conflict['enc_id'])
				ax.annotate(f"MORFAC {conflict['enc_id']}\n{conflict['distance_to_face_m']:.2f} m out · along-face {conflict['along_face_from_midpoint_m']:.2f} m\noff centre line · role unverified", (g.centroid.x, g.centroid.y), xytext=(-125, 55), textcoords='offset points', fontsize=7, color='white', bbox={'facecolor': '.15', 'alpha': .9}, arrowprops={'arrowstyle': '-', 'color': 'white'})
				attributes = json.dumps(conflict['attributes'], separators=(',', ':'))
				caption = f"Crew NW orientation outranks chart FAIL; structure role remains unverified. Confidence: medium.\nWGS84 {conflict['geometry_wgs84']['coordinates']}; outward {conflict['outward_from_face_midpoint_m']:.6f} m; along-face {conflict['along_face_from_midpoint_m']:.6f} m.\nAll readable NOAA ENC attributes (null = unavailable):\n" + '\n'.join(textwrap.wrap(attributes, 145))
				fig.text(.06, .01, caption, fontsize=7, family='monospace')
			ax.plot(center_e, center_n, 'r*', ms=13)
			worst = evidence['worst_cell']; ax.plot(worst['east_m'], worst['north_m'], marker='x', color='white', ms=12, mew=2)
			ax.set(xlim=(center_e - half, center_e + half), ylim=(center_n - half, center_n + half), xlabel='east (m), shared NED origin', ylabel='north (m)',
				title=f"{p['stop_name']} · stop {p['stop_id']}\n{p['source']} · {p['confidence']} · {p['water_side_evidence']['method']}\nminimum {evidence['min_depth_m']:.3f} m at low water {low:.3f} m NAVD88")
			ax.set_aspect('equal')
			fig.suptitle(ax.get_title(), fontsize=10, y=.98)
			ax.set_title('')
			if p['stop_id'] == '87':
				for candidate in candidates:
					g, _ = next((g, q) for g, q in constructions if q['id'] == candidate['enc_id'])
					ax.annotate(candidate['enc_id'][-8:], (g.centroid.x, g.centroid.y), color='white', fontsize=7)
				text = 'AIS: SOG < 0.5 kn; within 150 m GTFS then 10 m of polygon; historical reports, not visits\n'
				text += '\n'.join(f"{c['enc_id']}: {c['ais_slow_reports_within_10m']} reports · GTFS {c['distance_to_gtfs_m']:.1f} m · route out/in {c['route_distance_60m_outward_m']:.1f}/{c['route_distance_60m_inward_m']:.1f} m · {c['water_side_evidence']['method']}" for c in candidates)
				fig.text(.08, .01, text, fontsize=7)
			fig.colorbar(image, ax=ax, label='merged depth at lowest water (m); dashed white = 2.10 m')
			ax.legend(handles=[Line2D([], [], color=c, lw=2, label=l) for c, l in [('gold', 'connected contact'), ('cyan', 'primary face / water normal'), ('red', '30 m approach box / GTFS star'), ('magenta', 'ENC structures'), ('cornflowerblue', 'DEPARE boundary'), ('white', 'worst cell × / historical slow AIS dots')]], loc='upper left', fontsize=7, facecolor='.3', labelcolor='white')
			fig.tight_layout(rect=(0, .25 if p['stop_id'] == '8' else .1 if p['stop_id'] == '87' else 0, 1, .91)); fig.savefig(target / f"{p['stop_id']}.png", dpi=110, bbox_inches='tight', pad_inches=.15); plt.close(fig)
	(target / 'diagnosis.json').write_text(json.dumps(records, indent='\t') + '\n')
	(target / 'diagnosis.md').write_text('# Landing face and approach evidence\n\nSources: built cached NOAA ENC, CUDEM, GTFS, CO-OPS and historical AIS. No new source, manual digitisation, depth edit or lowered threshold. Gold lines are topological exterior contact outlines, not fender surveys.\n' + '\n'.join(notes) + '\n')
	return notes


STRUCTURE_COLOURS = {'berth-face': 'cyan', 'barge-side': 'gold', 'barge-end': 'orange', 'pier': 'saddlebrown', 'bulkhead': 'dimgray', 'shoreline': 'olivedrab'}


def structure_figures(slice_, target=None):
	"""One figure per landing of its whole structure: kinds, which segments are contact, and what was left out."""
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	from matplotlib.lines import Line2D

	out = paths.processed_dir(slice_.name)
	target = target or paths.REPO_ROOT / 'artifacts' / 'landing-faces'
	target.mkdir(parents=True, exist_ok=True)
	structures = json.loads((out / 'landing_structures.geojson').read_text())
	stops = {f['properties']['stop_id']: f['properties'] for f in _load(out, 'landings.geojson')}
	_, land = landings.chart_masks(out)
	written = []
	for stop_id, stop in sorted(stops.items()):
		centre = Point(stop['east_m'], stop['north_m'])
		half = landings.STRUCTURE_RADIUS_M + 20.
		fig, ax = plt.subplots(figsize=(9, 9))
		for g in _parts(land.intersection(centre.buffer(half * 1.5))):
			if g.geom_type == 'Polygon':
				ax.fill(*g.exterior.xy, color='.85')
		for feature in structures['features']:
			p = feature['properties']
			if p['stop_id'] != stop_id:
				continue
			points = [(e, n) for n, e in feature['local_coordinates']]
			for segment, a, b in zip(p['segments'], points, points[1:]):
				contact = landings.runtime_contact(segment)
				ax.plot([a[0], b[0]], [a[1], b[1]], color=STRUCTURE_COLOURS[segment['kind']], lw=3.5 if contact else 1.2, ls='-' if contact else ':')
		for item in structures['left_out']:
			if item['stop_id'] == stop_id:
				north, east = item['position_ne_m']
				ax.plot(east, north, 'x' if item['reason'].startswith('point') else 'o' if item['reason'].startswith('open') else 's', color='crimson', ms=6, mfc='none')
		ax.plot(centre.x, centre.y, 'r*', ms=14)
		ax.plot(*centre.buffer(landings.STRUCTURE_RADIUS_M).exterior.xy, 'k--', lw=.6)
		ax.set(xlim=(centre.x - half, centre.x + half), ylim=(centre.y - half, centre.y + half), xlabel='east (m), shared NED origin', ylabel='north (m)',
			title=f"{stop['stop_name']} · stop {stop_id}: whole structure within {landings.STRUCTURE_RADIUS_M:g} m")
		ax.set_aspect('equal')
		handles = [Line2D([], [], color=c, lw=3, label=k) for k, c in STRUCTURE_COLOURS.items()]
		handles += [Line2D([], [], color='k', lw=3, label='contact'), Line2D([], [], color='k', lw=1.2, ls=':', label='not contact'),
			Line2D([], [], color='crimson', marker='x', ls='', label='point object (left out)'), Line2D([], [], color='crimson', marker='o', mfc='none', ls='', label='open line in water (left out)'),
			Line2D([], [], color='crimson', marker='s', mfc='none', ls='', label='always submerged (left out)')]
		ax.legend(handles=handles, loc='upper left', fontsize=7)
		path = target / f'{stop_id}-structures.png'
		fig.savefig(path, dpi=90, bbox_inches='tight')
		plt.close(fig)
		written.append(path)
	return written
