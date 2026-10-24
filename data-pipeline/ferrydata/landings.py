"""Charted or explicitly estimated landing outlines and outward berthing faces.

Shapely coordinates here are (east, north) metres. Vectors are (north, east).
A GTFS stop identifies a passenger landing, not an assigned berth or surveyed face.
"""
import math

import numpy as np
import pandas as pd
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.polygon import orient
from shapely.ops import nearest_points, unary_union
from shapely import transform

from ferrydata import approach, crew_notes, geodesy, geojsonio, paths

ASCE_URL = 'https://www.asce.org/publications-and-news/civil-engineering-source/civil-engineering-magazine/article/2021/06/floating-ferry-landings-help-solve-nyc-transit-travails'
BARGE_LENGTH_M = 90 * .3048
BARGE_WIDTH_M = 35 * .3048
GANGWAY_M = 85 * .3048  # midpoint of published 70–100 ft range; horizontal estimate
MATCH_MAX_M = 50.
# Coordinator-approved multi-berth decision: use historical usage to order Pier 11
# candidates, then require a supported water side. Other assignments stay put
# until their separate candidate evidence has had crew review.
AIS_PRIORITY_STOP_IDS = {'87'}


def to_local(geometry):
	def convert(x, y):
		n, e = geodesy.to_local(y, x)
		return e, n
	return transform(geometry, convert, interleaved=False, include_z=False)


def to_wgs84(geometry):
	def convert(e, n):
		lat, lon = geodesy.from_local(n, e)
		return lon, lat
	return transform(geometry, convert, interleaved=False)


def heading_deg(outward_ne):
	return math.degrees(math.atan2(outward_ne[1], outward_ne[0])) % 360


def _straight_edges(barge):
	"""Join adjacent ENC segments turning by < 1 degree, preserving every source vertex."""
	groups = []
	for a, b in zip(barge.exterior.coords[:-1], barge.exterior.coords[1:]):
		vector = np.array(b) - a
		length = np.linalg.norm(vector)
		if length < .01:
			continue
		if groups:
			prior = np.array(groups[-1][-1]) - groups[-1][0]
			alignment = np.dot(prior, vector) / (np.linalg.norm(prior) * length)
			if alignment > math.cos(math.radians(1)):
				groups[-1].append(b)
				continue
		groups.append([a, b])
	if len(groups) > 1:
		first = np.array(groups[0][-1]) - groups[0][0]
		last = np.array(groups[-1][-1]) - groups[-1][0]
		if np.dot(first, last) / (np.linalg.norm(first) * np.linalg.norm(last)) > math.cos(math.radians(1)):
			groups[0] = groups.pop()[:-1] + groups[0]
	return [LineString(points) for points in groups]


def berthing_face(barge, outward_ne):
	"""Choose the outward long exterior side, keeping ENC edge vertices intact.

	The fixed shoreline supplies a direction, never a berth-depth target. Grouped
	segments avoid mistaking a tessellated 90 ft side for a series of short edges.
	"""
	edges = _straight_edges(barge)
	longest = max(edge.length for edge in edges)
	candidates = []
	for edge in edges:
		if edge.length < longest * .6:
			continue
		a, b = edge.coords[0], edge.coords[-1]
		norm = math.hypot(b[0] - a[0], b[1] - a[1])
		te, tn = (b[0] - a[0]) / norm, (b[1] - a[1]) / norm
		mid = edge.interpolate(.5, normalized=True)
		normal = (te, -tn)
		if barge.contains(Point(mid.x + normal[1] * .2, mid.y + normal[0] * .2)):
			normal = (-normal[0], -normal[1])
		alignment = float(np.dot(normal, outward_ne))
		candidates.append((alignment, edge, normal))
	_, edge, normal = max(candidates, key=lambda c: c[0])
	return edge, normal


def derive_barge(anchor_en, tangent_en, outward_ne, stop):
	"""85 ft nominal gangway to the landward edge, unless GTFS is already farther offshore."""
	out_en = np.array([outward_ne[1], outward_ne[0]])
	anchor = np.array(anchor_en)
	stop_offset = np.dot(np.array([stop.x, stop.y]) - anchor, out_en)
	centre_offset = max(GANGWAY_M + BARGE_WIDTH_M / 2, stop_offset)
	centre = anchor + centre_offset * out_en
	tangent = np.array(tangent_en)
	return Polygon([centre + a * tangent + b * out_en for a, b in (
		(-BARGE_LENGTH_M / 2, -BARGE_WIDTH_M / 2), (BARGE_LENGTH_M / 2, -BARGE_WIDTH_M / 2),
		(BARGE_LENGTH_M / 2, BARGE_WIDTH_M / 2), (-BARGE_LENGTH_M / 2, BARGE_WIDTH_M / 2))])


def match_barge(stop, candidates, water=None, land=None):
	"""Only polygon objects can supply an outline. Nearby lines/points are evidence, not invented polygons."""
	hits = sorted(((g.distance(stop), p['id'], g, p) for g, p in candidates if g.geom_type == 'Polygon' and g.distance(stop) <= MATCH_MAX_M), key=lambda x: (x[0], x[1]))
	if not hits:
		return None, None, False
	ambiguous = len(hits) > 1 and hits[1][0] - hits[0][0] < 10.
	if water is not None and land is not None:
		# Passenger stops may lie on a shared pier between several pontoons. Prefer
		# an outline with a directly charted exterior long side, never a deeper box.
		def charted_side(hit):
			barge = hit[2]
			edges = _straight_edges(orient(barge, sign=-1))
			longest = max(e.length for e in edges)
			for edge in edges:
				if edge.length < longest * .6:
					continue
				a, b = edge.coords[0], edge.coords[-1]
				length = math.dist(a, b)
				normal = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
				mid = edge.interpolate(.5, normalized=True)
				anchor = (mid.x - normal[1], mid.y - normal[0])
				if face_on_water_side(edge, normal, anchor, water, land):
					return True
			return False
		hits.sort(key=lambda hit: (not charted_side(hit), hit[0], hit[1]))
	return hits[0][2], hits[0][3], ambiguous


def water_facing_outline(barge, face, outward_ne, land=None, structures=()):
	"""Clockwise outline with its landward long side removed, preserving source vertices.

	A single LineString shares joints exactly, including the ends of an estimated
	ASCE barge. The omitted back edge leaves two open ends for contact release.
	"""
	edges = _straight_edges(orient(barge, sign=-1))
	def keep(edge):
		a, b = edge.coords[0], edge.coords[-1]
		length = math.dist(a, b)
		normal = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
		if np.dot(normal, outward_ne) < -.2:
			return False
		outside = LineString([(x + normal[1] * .5, y + normal[0] * .5) for x, y in edge.coords])
		return not ((land is not None and land.intersects(outside)) or any(g.intersects(outside) for g in structures))
	flags = [keep(edge) for edge in edges]
	# The connected exposed run containing the selected face. Do not bridge an
	# occluded side by inventing an edge through a pier or close the back of a barge.
	main = min(range(len(edges)), key=lambda i: edges[i].hausdorff_distance(face))
	flags[main] = True
	indices = [main]
	i = (main - 1) % len(edges)
	while flags[i] and i not in indices:
		indices.insert(0, i)
		i = (i - 1) % len(edges)
	i = (main + 1) % len(edges)
	while flags[i] and i not in indices:
		indices.append(i)
		i = (i + 1) % len(edges)
	kept = [edges[i] for i in indices]
	points = list(kept[0].coords)
	for edge in kept[1:]:
		if points[-1] != edge.coords[0]:
			raise ValueError('ENC outline has disconnected edges')
		points.extend(list(edge.coords)[1:])
	return LineString(points)


def polyline_errors(line, barge):
	"""Validate each joint, outline support, and the solid-right/water-left contract."""
	errors = []
	if line.geom_type != 'LineString' or line.is_closed or not line.is_simple:
		return ['contact outline must be a simple open connected LineString']
	for index, (a, b) in enumerate(zip(line.coords, list(line.coords)[1:])):
		length = math.dist(a, b)
		if length < .001:
			errors.append(f'segment {index}: shorter than 1 mm')
			continue
		edge = LineString([a, b])
		if not barge.boundary.buffer(.001).covers(edge):
			errors.append(f'segment {index}: not on outline within 1 mm')
		mid = edge.interpolate(.5, normalized=True)
		left = ((a[1] - b[1]) / length, (b[0] - a[0]) / length)
		if barge.contains(Point(mid.x + left[0] * .05, mid.y + left[1] * .05)) or not barge.contains(Point(mid.x - left[0] * .05, mid.y - left[1] * .05)):
			errors.append(f'segment {index}: structure is not solid on right')
	return errors


def face_on_water_side(face, outward_ne, shore_anchor_en, water, land, structures=()):
	"""Independent chart test just outside the floating outline, away from the bulkhead.

	ENC depth areas commonly exclude the pontoon footprint itself. Probe a parallel
	line 0.5 m outside the face (also avoids GeoJSON rounding at shared edges),
	and 12 m outside its midpoint. Clear the full swept face between 0.5 m
	and 12 m against LNDARE and the same surface/unknown blockers as fallback.
	"""
	mid = face.interpolate(.5, normalized=True)
	n, e = outward_ne
	distance_out = (mid.x - shore_anchor_en[0]) * e + (mid.y - shore_anchor_en[1]) * n
	probe = Point(mid.x + e * 12, mid.y + n * 12)
	outside = LineString([(x + e * .5, y + n * .5) for x, y in face.coords])
	sweep = _face_sweep(face, outward_ne, .5, 12.)
	return bool(distance_out > 0 and water.covers(outside) and water.covers(probe)
		and not land.intersects(outside) and not land.covers(probe)
		and not land.intersects(sweep)
		and not any(g.intersects(sweep) for g in structures))


def _face_sweep(face, outward_ne, near, far):
	"""Full face width, including intermediate ENC vertices, swept outward in metres."""
	n, e = outward_ne
	return Polygon([(x + e * near, y + n * near) for x, y in face.coords] +
		[(x + e * far, y + n * far) for x, y in reversed(face.coords)])


def topology_fallback_eligible(face, outward_ne, anchor, water, land, barge):
	"""One gate for initial/alternate/end sides; never claim 8 m on a shorter face."""
	if barge is None or face.length < 8.:
		return False
	n, e = outward_ne
	mid = face.interpolate(.5, normalized=True)
	probe = Point(mid.x + e * 12, mid.y + n * 12)
	outside = LineString([(x + e * .5, y + n * .5) for x, y in face.coords])
	return bool((mid.x - anchor[0]) * e + (mid.y - anchor[1]) * n > 0
		and not water.covers(probe) and not land.intersects(outside) and not land.covers(probe)
		and not barge.contains(Point(mid.x + e * .05, mid.y + n * .05)))


def water_side_evidence(face, outward_ne, anchor, water, land, structures=(), barge=None):
	"""Primary 12 m check, or separately labelled vessel-width DEPARE-gap proof.

	The fallback is only for a charted floating outline. It cannot establish depth
	or an assigned berth, and may not cross ANY other charted structure or land.
	"""
	if face_on_water_side(face, outward_ne, anchor, water, land, structures):
		return {'verified': True, 'method': 'direct-12m', 'near_face_coverage_gap_m': 0.}
	result = {'verified': False, 'method': 'unverified'}
	if not topology_fallback_eligible(face, outward_ne, anchor, water, land, barge):
		return result
	n, e = outward_ne
	mid = face.interpolate(.5, normalized=True)
	def transect(distance):
		return LineString([(mid.x + e * distance + n * offset, mid.y + n * distance - e * offset) for offset in (-4., 4.)])
	corridor = Polygon([*transect(0).coords, *list(transect(30).coords)[::-1]])
	result.update(corridor_land_overlap_m2=corridor.intersection(land).area,
		corridor_other_structures=sum(g.intersects(corridor) for g in structures), far_end_charted=bool(water.covers(transect(30))))
	gap = next((float(d) for d in np.linspace(0, 30, 301) if water.covers(transect(d))), 30.)
	result['near_face_coverage_gap_m'] = gap
	if land.intersects(corridor) or result['corridor_other_structures'] or not result['far_end_charted']:
		return result
	return {'verified': True, 'method': 'topology-corridor-8x30m', 'corridor_width_m': 8.,
		'corridor_length_m': 30., 'near_face_coverage_gap_m': gap,
		'note': 'Near-face DEPARE coverage is missing. Clear 8 m by 30 m corridor to charted water proves topology only; depth and operational berth remain unverified.'}


def select_face(barge, outward, anchor, water, land, structures=()):
	"""Keep the shoreline-selected long face when valid; otherwise evaluate actual ENC sides."""
	face, normal = berthing_face(barge, outward)
	proof = water_side_evidence(face, normal, anchor, water, land, structures, barge)
	if proof['verified']:
		return face, normal, proof
	choices = []
	for edge in _straight_edges(orient(barge, sign=-1)):
		a, b = edge.coords[0], edge.coords[-1]
		length = math.dist(a, b)
		n = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
		evidence = water_side_evidence(edge, n, anchor, water, land, structures, barge)
		if evidence['verified']:
			choices.append((evidence['method'] == 'direct-12m', float(np.dot(n, outward)), edge.length, edge, n, evidence))
	if choices:
		_, _, _, face, normal, proof = max(choices, key=lambda c: c[:3])
	return face, normal, proof


def contact_segment_evidence(contact, face, primary_proof, anchor, water, land, structures=(), barge=None, crew_reported=False):
	"""Only primary subdivisions share primary evidence; every other side proves itself."""
	primary_edges = {frozenset((a, b)) for a, b in zip(face.coords, list(face.coords)[1:])}
	results = []
	for index, (a, b) in enumerate(zip(contact.coords, list(contact.coords)[1:])):
		on_primary = frozenset((a, b)) in primary_edges
		length = math.dist(a, b)
		normal = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
		proof = primary_proof if on_primary else water_side_evidence(LineString([a, b]), normal, anchor, water, land, structures, barge)
		record = {'index': index, 'on_primary_face': on_primary, 'water_side_evidence': proof,
			'water_side_verified_by_pipeline': proof.get('chart_verified', proof['verified'])}
		if on_primary and crew_reported:
			record['water_side_crew_reported'] = True
		results.append(record)
	return results


def face_confidence(derived, ambiguous, proof, note):
	"""Ordinal geometry confidence; operational policy is provenance, not a survey."""
	if note and note.get('kind') == 'face-orientation' and (proof.get('chart_verified', False) or (not derived and not ambiguous)):
		return 'medium'
	return 'low' if derived or ambiguous or proof['method'] != 'direct-12m' else 'medium'


def crew_compass_direction(note):
	"""Crew names a compass side, never hand-digitised face coordinates."""
	bearings = {'N': 0., 'NE': 45., 'E': 90., 'SE': 135., 'S': 180., 'SW': 225., 'W': 270., 'NW': 315.}
	bearing = math.radians(bearings[note['face_outward_compass']])
	return math.cos(bearing), math.sin(bearing)


def crew_reported_face(barge, toward_ne, anchor, water, land, structures, note):
	"""Apply crew orientation while preserving the independent chart result."""
	face, normal = berthing_face(barge, toward_ne)
	chart = water_side_evidence(face, normal, anchor, water, land, structures, barge)
	proof = {**chart, 'verified': True, 'method': 'crew-reported',
		'chart_verified': chart['verified'], 'chart_method': chart['method'], 'crew_note': note}
	return face, normal, proof


def surface_structures(shore, own_id=None):
	"""WATLEV=3 is always submerged: disclose it as a depth hazard, not a wall.

	Unknown water-level effect remains blocking. This does not erase submerged
	geometry from any source layer or imply sufficient under-keel clearance.
	"""
	return [g for g, p in shore if p['id'] != own_id and p.get('water_level_effect_code') != 3]


def submerged_hazards(face, outward_ne, shore):
	mid = face.interpolate(.5, normalized=True); n, e = outward_ne
	corridor = LineString([(mid.x, mid.y), (mid.x + e * 30, mid.y + n * 30)]).buffer(4, cap_style=2)
	return [{'enc_id': p['id'], 'feature_class': p['feature_class'], 'CATSLC': p.get('category_code'),
		'WATLEV': 3, 'SORDAT': p.get('source_date'), 'source': 'NOAA ENC', 'cell': p.get('cell'),
		'corridor_overlap_m2': g.intersection(corridor).area, 'distance_to_face_m': g.distance(face)}
		for g, p in shore if p.get('water_level_effect_code') == 3 and g.intersects(corridor)]


def _features(out, name):
	return geojsonio.read_collection(out / name)['features']


def chart_masks(out):
	water = unary_union([to_local(shape(f['geometry'])) for f in _features(out, 'depth_areas.geojson')
		if f['properties'].get('min_depth_m') is not None and f['properties']['min_depth_m'] >= 0])
	land = unary_union([to_local(shape(f['geometry'])) for f in _features(out, 'land_areas.geojson')])
	return water, land


def choose_supported_candidate(records):
	"""AIS usage orders plausible berths; a valid water side is still mandatory.

	A topology-supported, well-used berth wins over a less-used directly charted
	one. If none passes, preserve the nearest candidate as explicitly unverified.
	"""
	valid = [r for r in records if r['water_side_evidence']['verified']]
	if valid:
		return min(valid, key=lambda r: (-r['ais_slow_reports_within_10m'], r['distance_to_gtfs_m'], r['enc_id']))
	return min(records, key=lambda r: (r['distance_to_gtfs_m'], r['enc_id']))


def choose_candidate_for_stop(stop_id, records, initial_enc_id):
	if stop_id in AIS_PRIORITY_STOP_IDS:
		return choose_supported_candidate(records)
	return next(r for r in records if r['enc_id'] == initial_enc_id)


def build(slice_, log=print):
	out = paths.processed_dir(slice_.name)
	stops = _features(out, 'landings.geojson')
	shore = [(to_local(shape(f['geometry'])), f['properties']) for f in _features(out, 'shoreline_constructions.geojson')]
	candidates = [(g, p) for g, p in shore if p['feature_class'] in ('PONTON', 'FLODOC') or p.get('water_level_effect_code') == 7]
	# LNDARE gives the fixed land/water boundary; detailed SLCONS can include
	# gangways/fenders at a barge and falsely rotate its normal along the berth.
	bulkheads = [(to_local(shape(f['geometry'])).boundary, f['properties'])
		for f in _features(out, 'land_areas.geojson')]
	water, land = chart_masks(out)
	routes = _features(out, 'routes.geojson')
	notes, notes_sha256 = crew_notes.load()
	ais_file = paths.ais_processed_dir() / 'nycferry_tracks.parquet'
	slow = pd.read_parquet(ais_file) if ais_file.exists() else None
	if slow is not None:
		slow = slow[slow.sog_kn < .5]
	features = []
	for stop in stops:
		p = stop['properties']
		point = Point(p['east_m'], p['north_m'])
		barge, match, ambiguous = match_barge(point, candidates, water, land)
		# Use the nearest fixed construction, excluding outlines overlapping the floating barge.
		fixed = [(g, props) for g, props in bulkheads if barge is None or not g.intersects(barge.buffer(-.2))]
		reference = barge.centroid if barge is not None else point
		nearest, bulk = min(fixed, key=lambda x: x[0].distance(reference))
		anchor, tangent = approach.face_geometry((reference.x, reference.y), [nearest])
		parts = [to_local(shape(f['geometry'])) for f in routes if set(f['properties']['route_ids']) & set(p.get('route_ids', []))]
		route = unary_union(parts) if parts else None
		candidate_records, contexts = [], {}
		near = slow[((slow.east_m - point.x) ** 2 + (slow.north_m - point.y) ** 2) < 150 ** 2] if slow is not None else None
		for g, cp in candidates:
			if g.geom_type != 'Polygon' or g.distance(point) > MATCH_MAX_M:
				continue
			fixed_candidate = [(line, bp) for line, bp in bulkheads if not line.intersects(g.buffer(-.2))]
			line, bp = min(fixed_candidate, key=lambda x: x[0].distance(g.centroid))
			ca, ct = approach.face_geometry((g.centroid.x, g.centroid.y), [line])
			vector = (g.centroid.y - ca[1], g.centroid.x - ca[0]); length = math.hypot(*vector)
			if length < .01:
				continue
			cn = (vector[0] / length, vector[1] / length)
			obstacles = surface_structures(shore, cp['id'])
			cf, normal, proof = select_face(g, cn, ca, water, land, obstacles)
			mid = cf.interpolate(.5, normalized=True)
			count = sum(g.distance(Point(row.east_m, row.north_m)) < 10. for row in near.itertuples()) if near is not None else 0
			record = {'enc_id': cp['id'], 'distance_to_gtfs_m': g.distance(point), 'ais_slow_reports_within_10m': count,
				'ais_method': 'Candidate-fleet reports with SOG < 0.5 kn, within 150 m of GTFS stop, then within 10 m of ENC polygon; reports, not independent visits; 2024–2025 cached sample; antenna offsets not corrected.',
				'ais_available': slow is not None, 'water_side_evidence': proof,
				'submerged_hazards': submerged_hazards(cf, normal, shore),
				'route_distance_60m_outward_m': route.distance(Point(mid.x + normal[1] * 60, mid.y + normal[0] * 60)) if route is not None else None,
				'route_distance_60m_inward_m': route.distance(Point(mid.x - normal[1] * 60, mid.y - normal[0] * 60)) if route is not None else None,
				'sides': []}
			for edge in _straight_edges(orient(g, sign=-1)):
				a, b = edge.coords[0], edge.coords[-1]; length = math.dist(a, b)
				normal_edge = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
				record['sides'].append({'length_m': edge.length, 'outward_normal_ne': normal_edge,
					'water_side_evidence': water_side_evidence(edge, normal_edge, ca, water, land, obstacles, g)})
			candidate_records.append(record); contexts[cp['id']] = (g, cp, ca, ct, bp)
		if candidate_records:
			chosen = choose_candidate_for_stop(p['stop_id'], candidate_records, match['id'])
			if notes.get(p['stop_id'], {}).get('modelled_enc_id'):
				chosen = next(r for r in candidate_records if r['enc_id'] == notes[p['stop_id']]['modelled_enc_id'])
			barge, match, anchor, tangent, bulk = contexts[chosen['enc_id']]
		def is_water(n, e):
			q = Point(e, n)
			return water.covers(q) and not land.covers(q)
		if barge is not None:
			# Orient the existing floating outline relative to its own fixed shore,
			# not a passenger point on another pier at a multiple-berth terminal.
			vector = (barge.centroid.y - anchor[1], barge.centroid.x - anchor[0])
			norm = math.hypot(*vector)
			if norm < .01:
				raise ValueError(f"{p['stop_name']}: charted barge lies on fixed shoreline")
			outward, how = (vector[0] / norm, vector[1] / norm), 'away-from-fixed-shoreline'
		else:
			outward, how = approach.choose_outward((anchor[1], anchor[0]), tangent, is_water, route)
		if outward is None:
			raise ValueError(f"{p['stop_name']}: cannot resolve water side from ENC or GTFS route")
		derived = barge is None
		if derived:
			barge = derive_barge(anchor, tangent, outward, point)
		structures = surface_structures(shore, match['id'] if match else None)
		if derived:
			face, normal = berthing_face(barge, outward)
			proof = water_side_evidence(face, normal, anchor, water, land, structures)
		else:
			face, normal, proof = select_face(barge, outward, anchor, water, land, structures)
		note = notes.get(p['stop_id'])
		crew_orientation = None
		if note and note.get('face_toward_fixed_land_near_stop_id'):
			reference_id = note['face_toward_fixed_land_near_stop_id']
			reference_stop = next(f['properties'] for f in stops if f['properties']['stop_id'] == reference_id)
			reference_point = Point(reference_stop['east_m'], reference_stop['north_m'])
			manhattan = min(bulkheads, key=lambda x: x[0].distance(reference_point))
			_, opposite_shore = nearest_points(barge.centroid, manhattan[0])
			vector = (opposite_shore.y - barge.centroid.y, opposite_shore.x - barge.centroid.x)
			length = math.hypot(*vector); toward = (vector[0] / length, vector[1] / length)
			face, normal, proof = crew_reported_face(barge, toward, anchor, water, land, structures, note)
			crew_orientation = {'opposite_shore_enc_id': manhattan[1]['id'], 'opposite_shore_reference_stop_id': reference_id,
				'opposite_shore_ne_m': [opposite_shore.y, opposite_shore.x], 'bearing_to_manhattan_deg_true': heading_deg(toward), 'side_bearings': []}
			for edge in _straight_edges(orient(barge, sign=-1)):
				a, b = edge.coords[0], edge.coords[-1]; length = math.dist(a, b)
				en = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
				crew_orientation['side_bearings'].append({'length_m': edge.length, 'outward_bearing_deg_true': heading_deg(en)})
		# A compass crew note changes the selected berth, not the existing chart-selected
		# exposed contact run. Preserve physical segments and IDs across this role change.
		contact_outward = normal
		if note and note.get('face_outward_compass'):
			face, normal, proof = crew_reported_face(barge, crew_compass_direction(note), anchor, water, land, structures, note)
			how = 'crew-reported-compass-orientation'
		face_geo = geojsonio.make_feature(to_wgs84(face), {})
		contact = water_facing_outline(barge, face, contact_outward, land, structures)
		contact_geo = geojsonio.make_feature(to_wgs84(contact), {})
		mid = face.interpolate(.5, normalized=True)
		face_lat, face_lon = geodesy.from_local(mid.y, mid.x)
		props = {
			'stop_id': p['stop_id'], 'stop_name': p['stop_name'], 'route_ids': p.get('route_ids', []),
			'source': 'derived-from-gtfs' if derived else 'enc-feature',
			'confidence': face_confidence(derived, ambiguous, proof, note),
			'verification': 'needs-crew-verification',
			'outline_source': ASCE_URL if derived else 'NOAA ENC',
			'outline_id': f"derived-barge:{p['stop_id']}:{bulk['id']}" if derived else f"enc:{match['id']}",
			'enc_id': match['id'] if match else None, 'enc_cell': match.get('cell') if match else None,
			'enc_source_date': match.get('source_date') if match else None,
			'candidate_evidence': candidate_records,
			'crew_evidence': {'note_id': note['id'], 'kind': note['kind']} if note else None,
			'crew_note': note, 'crew_notes_sha256': notes_sha256 if note else None, 'crew_orientation': crew_orientation,
			'submerged_hazards': submerged_hazards(face, normal, shore),
			'ambiguous_enc_match': ambiguous, 'side_chosen_by': how,
			'bulkhead_enc_id': bulk['id'], 'bulkhead_feature_class': 'LNDARE boundary', 'bulkhead_anchor_en_m': list(anchor),
			'face_heading_deg_true': heading_deg(normal), 'outward_normal_ne': list(normal),
			'face_geometry': face_geo['geometry'], 'face_local_coordinates': face_geo['local_coordinates'],
			'contact_geometry': contact_geo['geometry'], 'contact_local_coordinates': contact_geo['local_coordinates'],
			'contact_confidence': 'low' if derived or ambiguous else 'medium',
			'contact_geometry_estimated': derived,
			'contact_segment_evidence': contact_segment_evidence(contact, face, proof, anchor, water, land, structures,
				None if derived else barge, bool(note and note.get('water_side_crew_reported'))),
			'contact_note': 'Connected clockwise outline, excluding landward long side; water left, structure right. Derived barge ends use published ASCE dimensions and estimated placement.',
			'water_side_evidence': proof,
			'face_midpoint': {'lat': float(face_lat), 'lon': float(face_lon), 'north_m': mid.y, 'east_m': mid.x},
			'gtfs_to_face_m': face.distance(point),
			'water_side': proof['verified'],
			'water_side_verified_by_pipeline': proof.get('chart_verified', proof['verified']),
			'water_side_crew_reported': bool(note and note.get('water_side_crew_reported')),
			'derivation': {'length_m': BARGE_LENGTH_M, 'width_m': BARGE_WIDTH_M, 'nominal_gangway_m': GANGWAY_M,
				'note': 'Estimated horizontal 85 ft gangway (published range 70–100 ft), barge parallel to nearest ENC LNDARE shoreline boundary. GTFS offshore offset retained if larger; not a survey.'} if derived else None,
		}
		features.append(geojsonio.make_feature(to_wgs84(barge), props))
	geojsonio.write_collection(out / 'landing_faces.geojson', features, 'landing_faces', {
		'note': 'Geometry is barge outline; properties.face_geometry is the berthing line in WGS84, face_local_coordinates is [north,east]. Outward normal heading is clockwise from true north. All assigned faces require crew verification; ENC outlines do not identify the operational berth.',
		'derived_size_source': ASCE_URL})
	log(f'  landing_faces.geojson: {len(features)} landings ({sum(f["properties"]["source"] == "enc-feature" for f in features)} ENC outlines)')
