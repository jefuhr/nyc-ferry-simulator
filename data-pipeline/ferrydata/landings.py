"""Charted or explicitly estimated landing outlines and outward berthing faces.

Shapely coordinates here are (east, north) metres. Vectors are (north, east).
A GTFS stop identifies a passenger landing, not an assigned berth or surveyed face.
"""
import itertools
import math

import numpy as np
import pandas as pd
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.polygon import orient
from shapely.ops import nearest_points, unary_union
from shapely import STRtree, transform

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
# Whole landing structures: everything charted within this distance of the GTFS stop, the same
# radius as the AIS slow-report search. It covers the final approach and departure (about six
# T-class lengths), every berth of a multi-berth stop and the piers beside it.
STRUCTURE_RADIUS_M = 150.
SOLID_PROBE_M = .5  # charted water and no land or structure this far out on the water (left) side
WINDING_PROBE_M = .05
FLOATING_CLASSES = ('PONTON', 'FLODOC', 'HULKES')
PIER_CATEGORIES = {4, 5, 14, 16}  # pier (jetty), promenade pier, fender, open face wharf
BULKHEAD_CATEGORIES = {6, 7, 10, 15, 18}  # wharf (quay), training wall, sea wall, solid face wharf, lock wall
KIND_PRIORITY = ('pier', 'bulkhead', 'shoreline')


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


# ---- whole landing structures (contacts.json version 2) ---------------------------------------------------


def contact_id(key):
	"""World/Import's StableId: FNV-1a over the key's UTF-16 code units, masked to a positive Int32."""
	value = 2166136261
	for char in key:
		code = ord(char)
		if code > 0xFFFF:
			raise ValueError(f'contact identity outside the UTF-16 basic plane: {key!r}')
		value = ((value ^ code) * 16777619) & 0xFFFFFFFF
	return value & 0x7FFFFFFF


def legacy_edge_key(stop_id, outline_id, ring, a, b):
	"""World/Import's physical edge identity: the outline as written, clockwise from its smallest
	[lon, lat] vertex, and the edge's ordinal. Primary/end role and direction never enter it."""
	points = [[float(v) for v in p[:2]] for p in ring]
	vertices = [points[0]] + list(itertools.takewhile(lambda p: p != points[0], points[1:]))
	if len(vertices) < 3:
		raise ValueError(f'landing source outline has fewer than three vertices: {stop_id}')
	x0, y0 = vertices[0]
	area = 0.
	for i, (x, y) in enumerate(vertices):
		nx, ny = vertices[(i + 1) % len(vertices)]
		area += (x - x0) * (ny - y0) - (nx - x0) * (y - y0)
	if area > 0:
		vertices.reverse()
	first = min(range(len(vertices)), key=lambda i: (vertices[i][0], vertices[i][1]))
	a, b = [float(v) for v in a[:2]], [float(v) for v in b[:2]]
	for ordinal in range(len(vertices)):
		start, end = vertices[(first + ordinal) % len(vertices)], vertices[(first + ordinal + 1) % len(vertices)]
		if (start == a and end == b) or (start == b and end == a):
			return f'{stop_id}:{outline_id}:edge:{ordinal}'
	raise ValueError(f'contact segment is not an edge of the landing source outline: {stop_id}')


def fixed_edge_key(layer, lnam, a, b):
	"""Endpoint identity of a fixed charted boundary edge, independent of direction, ring start and clipping."""
	first, second = sorted([tuple(float(v) for v in a[:2]), tuple(float(v) for v in b[:2])])
	return f'{layer}:{lnam}:{first[0]!r},{first[1]!r}:{second[0]!r},{second[1]!r}'


def segment_verification(record, note):
	"""Coordinator-approved label rule: crew-reported on primary-face subsegments whose water side a
	face-orientation crew note reports; every other segment still needs crew verification."""
	if record.get('on_primary_face') and record.get('water_side_crew_reported') and note and note.get('kind') == 'face-orientation':
		return 'crew-reported'
	return 'needs-crew-verification'


def structure_verification(note, berth_id, primary_outline_id):
	"""Name only the crew evidence applying to this structure, never verification of its outline or depth."""
	if note and berth_id:
		if note.get('kind') == 'face-orientation' and note.get('water_side_crew_reported') and berth_id == primary_outline_id:
			return 'crew-reported landing side'
		if note.get('kind') == 'berth-use-policy' and any(b.get('id') == berth_id for b in note.get('berths', [])):
			return 'crew-reported berth'
	return 'needs-crew-verification'


def structure_kind(props):
	"""pier, bulkhead or shoreline from a charted construction's category; uncategorised is shoreline."""
	if props is None or props.get('feature_class') != 'SLCONS':
		return 'shoreline'
	category = props.get('category_code')
	return 'pier' if category in PIER_CATEGORIES else 'bulkhead' if category in BULKHEAD_CATEGORIES else 'shoreline'


def outline_side_kinds(outline):
	"""barge-side for each source edge running along the outline's long axis, barge-end across it.

	The axis is the long side of the minimum rotated rectangle, so a charted long side with a slight
	bend (South Williamsburg's SE side turns 2.6 degrees) stays a side.
	"""
	rectangle = list(outline.minimum_rotated_rectangle.exterior.coords)
	a, b, c = rectangle[0], rectangle[1], rectangle[2]
	axis = (b[0] - a[0], b[1] - a[1]) if math.dist(a, b) >= math.dist(b, c) else (c[0] - b[0], c[1] - b[1])
	axis = math.atan2(axis[1], axis[0])
	kinds = {}
	for group in _straight_edges(orient(outline, sign=-1)):
		start, end = group.coords[0], group.coords[-1]
		turn = abs((math.atan2(end[1] - start[1], end[0] - start[0]) - axis + math.pi / 2) % math.pi - math.pi / 2)
		kind = 'barge-side' if turn <= math.pi / 4 else 'barge-end'
		for p, q in zip(group.coords, list(group.coords)[1:]):
			kinds[frozenset((p, q))] = kind
	return kinds


def _clockwise_ring(outline):
	"""Unique outline vertices, clockwise, as the straight-side groups keep them (sub-centimetre edges merged)."""
	groups = _straight_edges(orient(outline, sign=-1))
	ring = list(groups[0].coords)
	for group in groups[1:]:
		if ring[-1] != group.coords[0]:
			raise ValueError('ENC outline has disconnected edges')
		ring.extend(list(group.coords)[1:])
	if ring[-1] != ring[0]:
		raise ValueError('ENC outline does not close')
	return ring[:-1]


def outline_closure(barge, exposed):
	"""The rest of the clockwise outline after the exposed contact run: together they close the barge."""
	ring = _clockwise_ring(barge)
	start, end = tuple(exposed.coords[-1]), tuple(exposed.coords[0])
	index = ring.index(start)
	points = [ring[index]]
	while points[-1] != end or len(points) == 1:
		index = (index + 1) % len(ring)
		points.append(ring[index])
	return LineString(points)


def closed_outline(outline):
	"""The whole clockwise outline as one closed polyline, from its smallest (east, north) vertex."""
	ring = _clockwise_ring(outline)
	first = min(range(len(ring)), key=lambda i: ring[i])
	ring = ring[first:] + ring[:first]
	return LineString(ring + [ring[0]])


class ChartedWater:
	"""Where the solid-side probe may stand: a charted depth area (DEPARE or DRGARE with a non-negative lower
	bound) or, coordinator-approved for this probe only, ENC UNSARE, water of unknown depth. It never gives
	a depth: approach depth and the merged depth ignore UNSARE."""

	ORDER = ('DEPARE', 'DRGARE', 'UNSARE')

	def __init__(self, areas, unsurveyed=()):
		self._items = [(g, p.get('feature_class') or 'DEPARE', p.get('id')) for g, p in areas]
		self._items += [(g, 'UNSARE', p.get('id')) for g, p in unsurveyed]
		self._tree = STRtree([g for g, _, _ in self._items])

	def at(self, point):
		"""(layer, LNAM) of the charted water covering the point, depth areas before UNSARE; (None, None) if none."""
		hits = sorted((self.ORDER.index(layer) if layer in self.ORDER else len(self.ORDER), lnam or '', layer)
			for g, layer, lnam in (self._items[i] for i in self._tree.query(point)) if g.covers(point))
		return (hits[0][2], hits[0][1] or None) if hits else (None, None)


def start_face_eligible(segment):
	"""A start needs a basis on every subdivision; solid-side collision retention alone is insufficient."""
	return bool(segment.get('water_side_verified_by_pipeline') or segment.get('water_side_crew_reported')
		or berth_solid_contact(segment))


def runtime_contact(segment):
	"""The Slice loader's gate: a primary face on the chart test or crew report; any other segment also on its solid side."""
	if segment['water_side_verified_by_pipeline'] or segment.get('water_side_crew_reported'):
		return True
	return berth_solid_contact(segment) or (not segment.get('primary_face') and bool(segment['solid_side_evidence']['verified']))


def solid_side_evidence(a, b, interior, layer, lnam, water, land, others, method='enc-closed-outline'):
	"""Coordinator-approved collision eligibility for non-primary segments.

	The edge of a closed outline is solid on its right by winding (checked just inside and outside the
	edge), and the point 0.5 m out on its left is charted water (a depth area, or UNSARE: see
	ChartedWater) with no land or other structure. This proves which side is solid, not depth,
	approach room or a berth.
	"""
	length = math.dist(a, b)
	left = ((a[1] - b[1]) / length, (b[0] - a[0]) / length)
	mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
	# A charted chamfer can be centimetres long: keep the winding probes off the neighbouring edges.
	offset = min(WINDING_PROBE_M, length / 4)
	inside = Point(mid[0] - left[0] * offset, mid[1] - left[1] * offset)
	outside = Point(mid[0] + left[0] * offset, mid[1] + left[1] * offset)
	probe = Point(mid[0] + left[0] * SOLID_PROBE_M, mid[1] + left[1] * SOLID_PROBE_M)
	winding = bool(interior.covers(inside) and not interior.covers(outside))
	if not isinstance(water, ChartedWater):
		water = ChartedWater([(water, {'feature_class': 'DEPARE', 'id': None})])
	water_layer, water_lnam = water.at(probe)
	charted = water_layer is not None
	in_land = bool(land.covers(probe))
	other = bool(others is not None and not others.is_empty and others.covers(probe))
	lat, lon = geodesy.from_local(probe.y, probe.x)
	return {'verified': winding and charted and not in_land and not other, 'method': method, 'layer': layer, 'lnam': lnam,
		'winding': 'clockwise: outline interior on the right, start to end', 'winding_check': winding,
		'probe_offset_m': SOLID_PROBE_M, 'probe_ne_m': [round(probe.y, 3), round(probe.x, 3)],
		'probe_lonlat': [round(float(lon), geojsonio.LONLAT_DIGITS), round(float(lat), geojsonio.LONLAT_DIGITS)],
		'probe_in_charted_water': charted, 'probe_water_layer': water_layer, 'probe_water_lnam': water_lnam,
		'probe_in_lndare': in_land, 'probe_in_other_structure': other}


def _polygons(geometry):
	if geometry.is_empty:
		return []
	return [g for g in getattr(geometry, 'geoms', [geometry]) if g.geom_type == 'Polygon']


def _on_clip(a, b, clip):
	"""Both ends and the middle of the edge on the output clip boundary (WGS84 within 2e-7 degrees)."""
	if clip is None:
		return False
	edge = to_wgs84(LineString([a, b]))
	return all(clip.exterior.distance(Point(p)) < 2e-7 for p in (edge.coords[0], edge.coords[-1], edge.interpolate(.5, normalized=True).coords[0]))


def _left_out(stop_id, stop_point, geometry, props, reason):
	_, nearest = nearest_points(stop_point, geometry)
	lat, lon = geodesy.from_local(nearest.y, nearest.x)
	return {'stop_id': stop_id, 'lnam': props['id'], 'layer': props['feature_class'], 'category_code': props.get('category_code'),
		'water_level_effect_code': props.get('water_level_effect_code'), 'reason': reason,
		'position_ne_m': [round(nearest.y, 3), round(nearest.x, 3)],
		'position_lonlat': [round(float(lon), geojsonio.LONLAT_DIGITS), round(float(lat), geojsonio.LONLAT_DIGITS)],
		'distance_m': round(float(stop_point.distance(geometry)), 3)}


def fixed_structure_runs(stop_id, stop_point, sources, lines, listed, water, land, floating, radius=STRUCTURE_RADIUS_M, clip=None):
	"""Charted fixed boundaries within the radius of a stop, as land-right contact polylines.

	sources are the closed solid outlines [(polygon, props)]: ENC LNDARE and dry SLCONS areas, merged
	so a pier drawn on land has no internal walls. lines [(line, props)] classify the boundary by the
	charted construction lying on it; a line off the boundary is an open line in water and is only
	listed, as are listed [(geometry, props)] points and always-submerged structures. floating are the
	floating outlines, which a fixed edge's water probe must not fall inside. Edges on the output clip
	boundary are cuts, not shoreline. Returns (runs, left_out).
	"""
	solid = unary_union([g for g, _ in sources]) if sources else Polygon()
	floating_union = unary_union(floating) if floating else None
	near = stop_point.buffer(radius + 1.)
	carriers = [(g.boundary.buffer(.01), p) for g, p in sources if g.intersects(near)]
	classifiers = [(g.buffer(.05), p) for g, p in lines if p.get('water_level_effect_code') != 3 and g.intersects(near)]
	runs = []
	for polygon in _polygons(solid):
		polygon = orient(polygon, sign=-1)
		for ring in [polygon.exterior, *polygon.interiors]:
			coords = list(ring.coords)[:-1]
			count = len(coords)
			keep = [math.dist(coords[i], coords[(i + 1) % count]) >= .001
				and LineString([coords[i], coords[(i + 1) % count]]).distance(stop_point) <= radius
				and not _on_clip(coords[i], coords[(i + 1) % count], clip) for i in range(count)]
			if not any(keep):
				continue
			if all(keep):
				first = min(range(count), key=lambda i: coords[i])
				pieces = [[(first + k) % count for k in range(count)]]
			else:
				first = next(i for i in range(count) if not keep[i]) + 1
				pieces, current = [], []
				for k in range(count):
					i = (first + k) % count
					if keep[i]:
						current.append(i)
					elif current:
						pieces.append(current)
						current = []
				if current:
					pieces.append(current)
			for piece in pieces:
				points = [coords[i] for i in piece] + [coords[(piece[-1] + 1) % count]]
				segments = []
				for a, b in zip(points, points[1:]):
					edge = LineString([a, b])
					carrier = next((p for g, p in carriers if g.covers(edge)), {'id': None, 'feature_class': 'LNDARE'})
					found = {structure_kind(p) for g, p in classifiers if g.covers(edge)}
					kind = next((k for k in KIND_PRIORITY if k in found), 'shoreline')
					record = {'kind': kind, 'layer': carrier['feature_class'], 'lnam': carrier['id'],
						'classified_by': sorted(p['id'] for g, p in classifiers if g.covers(edge)),
						'solid_side_evidence': solid_side_evidence(a, b, solid, carrier['feature_class'], carrier['id'], water, land, floating_union)}
					segments.append(record)
				runs.append({'stop_id': stop_id, 'coords': points, 'closed': points[0] == points[-1], 'segments': segments})
	runs.sort(key=lambda run: run['coords'][0])
	boundary = solid.boundary.buffer(.05) if not solid.is_empty else Polygon()
	left_out = []
	for g, p in lines:
		if g.is_empty or p.get('water_level_effect_code') == 3 or g.distance(stop_point) > radius:
			continue
		off = g.difference(boundary).intersection(near)
		if not off.is_empty and off.length > .01:
			left_out.append(_left_out(stop_id, stop_point, off, p, 'open line in water: no charted solid side, not contact geometry'))
	for g, p in listed:
		if g.is_empty or g.distance(stop_point) > radius:
			continue
		if p.get('water_level_effect_code') == 3:
			left_out.append(_left_out(stop_id, stop_point, g, p, 'always submerged (WATLEV 3): a depth hazard, not a wall'))
		elif g.geom_type in ('Point', 'MultiPoint'):
			left_out.append(_left_out(stop_id, stop_point, g, p, 'point object: no charted extent, not contact geometry'))
	left_out.sort(key=lambda item: (item['distance_m'], item['lnam']))
	return runs, left_out


def _unsurveyed(slice_):
	"""ENC UNSARE (unsurveyed water) in local metres, read from the cached cells as landing_audit reads SLCONS."""
	if not getattr(slice_, 'enc_cells', None):
		return []
	from ferrydata import enc
	layers, _ = enc.read_layers(slice_, ('UNSARE',))
	if 'UNSARE' not in layers:
		return []
	frame = layers['UNSARE']
	return [(to_local(g), {'id': lnam, 'feature_class': 'UNSARE'}) for g, lnam in sorted(zip(frame.geometry, frame.LNAM), key=lambda x: (str(x[1]), x[0].wkb))
		if g is not None and not g.is_empty and g.geom_type in ('Polygon', 'MultiPolygon')]


def _slow_reports():
	"""Cached candidate-fleet AIS reports under 0.5 kn, or None when the AIS extract is not built."""
	ais_file = paths.ais_processed_dir() / 'nycferry_tracks.parquet'
	if not ais_file.exists():
		return None
	slow = pd.read_parquet(ais_file)
	return slow[slow.sog_kn < .5]


def _shore_anchor(polygon, bulkheads):
	"""The fixed-shore anchor the candidate evaluation uses: nearest LNDARE boundary not under the outline."""
	fixed = [(line, props) for line, props in bulkheads if not line.intersects(polygon.buffer(-.2))]
	line, props = min(fixed, key=lambda x: x[0].distance(polygon.centroid))
	anchor, tangent = approach.face_geometry((polygon.centroid.x, polygon.centroid.y), [line])
	return anchor, tangent, props


def _chart_side(a, b, anchor, water, land, structures, barge):
	"""The existing per-side chart test (direct 12 m, or the corridor for a charted floating outline)."""
	length = math.dist(a, b)
	normal = ((b[0] - a[0]) / length, (a[1] - b[1]) / length)
	return water_side_evidence(LineString([a, b]), normal, anchor, water, land, structures, barge)


def _lonlat(points):
	return geojsonio._round_coords(list(to_wgs84(LineString(points)).coords))


def _floating_outlines(shore):
	return [(g, p) for g, p in shore if g.geom_type == 'Polygon' and (p['feature_class'] in FLOATING_CLASSES or p.get('water_level_effect_code') == 7)]


def stop_structures(context, shore, lands, bulkheads, water, land, clip=None, radius=STRUCTURE_RADIUS_M, charted=None):
	"""Every contact polyline of one stop: the landing barge whole, every berth, floating and fixed structures.

	Returns (polylines, left_out). Polylines keep the stop's existing exposed contact run first and
	unchanged, so its ids, indices and primary flags survive; the rest of the barge closes it.
	"""
	stop_id, point = context['stop_id'], context['point']
	barge, derived, outline_id = context['barge'], context['derived'], context['outline_id']
	charted = charted if charted is not None else ChartedWater([(water, {'feature_class': 'DEPARE', 'id': None})])
	floating = _floating_outlines(shore)
	floating_geometries = [g for g, _ in floating]
	solid_sources = list(lands)
	solid_sources += [(g, p) for g, p in shore if p['feature_class'] == 'SLCONS' and g.geom_type == 'Polygon' and p.get('water_level_effect_code') not in (3, 7)]
	solid = unary_union([g for g, _ in solid_sources]) if solid_sources else Polygon()

	def others(outline):
		rest = [g for g in floating_geometries if not g.equals(outline)]
		return unary_union(rest + [solid])

	note = context['note']
	layer = 'derived-from-gtfs' if derived else context['match']['feature_class']
	lnam = None if derived else context['match']['id']
	kinds = outline_side_kinds(barge)
	landing_others = others(barge)
	polylines = []
	# The exposed run, exactly as landing_faces.geojson's contact_geometry and its per-side evidence.
	# Identities hash the coordinates as written there, the outline's ring included.
	contact = list(context['contact'].coords)
	points = context['contact_lonlat']
	ring = context['ring_lonlat']
	written = dict(zip(barge.exterior.coords, ring))
	segments = []
	for record, a, b, la, lb in zip(context['records'], contact, contact[1:], points, points[1:]):
		on_primary = record['on_primary_face']
		segment = {'index': record['index'], 'kind': 'berth-face' if on_primary else kinds[frozenset((a, b))],
			'id_key': legacy_edge_key(stop_id, outline_id, ring, la, lb),
			'source': context['source'], 'confidence': context['confidence'] if on_primary else context['contact_confidence'],
			'verification': record['verification'], 'berth_id': outline_id, 'layer': layer, 'lnam': lnam,
			'water_side_verified_by_pipeline': record['water_side_verified_by_pipeline'], 'water_side_evidence': record['water_side_evidence'],
			'solid_side_evidence': solid_side_evidence(a, b, barge, layer, lnam, charted, land, landing_others, 'derived-outline' if derived else 'enc-closed-outline')}
		if on_primary:
			segment['primary_face'] = True
		if record.get('water_side_crew_reported'):
			segment['water_side_crew_reported'] = True
		if note and (on_primary or note.get('kind') == 'berth-use-policy'):
			segment['crew_evidence'] = {'note_id': note['id'], 'kind': note['kind']}
		if derived and not on_primary:
			segment['geometry_estimated'] = True
		segments.append(segment)
	base = {'stop_id': stop_id, 'stop_name': context['stop_name'], 'structure_id': outline_id, 'berth_id': outline_id, 'source': context['source']}
	polylines.append({**base, 'polyline_id': f'landing-{stop_id}', 'role': 'landing', 'coords': contact, 'lonlat': points, 'segments': segments})
	back = list(outline_closure(barge, context['contact']).coords)
	back_points = [written[p] for p in back]
	segments = []
	for index, (a, b, la, lb) in enumerate(zip(back, back[1:], back_points, back_points[1:])):
		proof = _chart_side(a, b, context['anchor'], water, land, context['structures'], None if derived else barge)
		segment = {'index': index, 'kind': kinds[frozenset((a, b))], 'id_key': legacy_edge_key(stop_id, outline_id, ring, la, lb),
			'source': context['source'], 'confidence': context['contact_confidence'], 'verification': 'needs-crew-verification',
			'berth_id': outline_id, 'layer': layer, 'lnam': lnam,
			'water_side_verified_by_pipeline': proof.get('chart_verified', proof['verified']), 'water_side_evidence': proof,
			'solid_side_evidence': solid_side_evidence(a, b, barge, layer, lnam, charted, land, landing_others, 'derived-outline' if derived else 'enc-closed-outline')}
		if note and note.get('kind') == 'berth-use-policy':
			segment['crew_evidence'] = {'note_id': note['id'], 'kind': note['kind']}
		if derived:
			segment['geometry_estimated'] = True
		segments.append(segment)
	polylines.append({**base, 'polyline_id': f'landing-{stop_id}-back', 'role': 'landing', 'coords': back, 'lonlat': back_points, 'segments': segments})
	# Other berths: every other charted floating outline the stop's candidate evaluation considered.
	used = {lnam} if lnam else set()
	for berth in context['berths']:
		polygon, props = berth['polygon'], berth['props']
		used.add(props['id'])
		berth_id = f"enc:{props['id']}"
		coords = list(closed_outline(polygon).coords)
		ring_points = _lonlat(coords)
		face_edges = {frozenset(e) for e in zip(berth['face'].coords, list(berth['face'].coords)[1:])}
		berth_kinds = outline_side_kinds(polygon)
		berth_others = others(polygon)
		segments = []
		for index, (a, b, la, lb) in enumerate(zip(coords, coords[1:], ring_points, ring_points[1:])):
			on_face = frozenset((a, b)) in face_edges
			proof = berth['proof'] if on_face else _chart_side(a, b, berth['anchor'], water, land, berth['obstacles'], polygon)
			segments.append({'index': index, 'kind': 'berth-face' if on_face else berth_kinds[frozenset((a, b))],
				'id_key': legacy_edge_key(stop_id, berth_id, ring_points, la, lb),
				'source': 'enc-feature', 'confidence': berth['confidence'], 'verification': 'needs-crew-verification',
				'berth_id': berth_id, 'layer': props['feature_class'], 'lnam': props['id'],
				'water_side_verified_by_pipeline': proof.get('chart_verified', proof['verified']), 'water_side_evidence': proof,
				'solid_side_evidence': solid_side_evidence(a, b, polygon, props['feature_class'], props['id'], charted, land, berth_others)})
		polylines.append({'stop_id': stop_id, 'stop_name': context['stop_name'], 'structure_id': berth_id, 'berth_id': berth_id,
			'source': 'enc-feature', 'polyline_id': f"berth-{stop_id}-{props['id']}", 'role': 'berth', 'coords': coords, 'lonlat': ring_points, 'segments': segments})
	# Other floating outlines within the radius: pontoons and hulks that are not this stop's berths.
	pieces = {}
	for g, props in sorted(floating, key=lambda item: (item[1]['id'], item[0].bounds)):
		if props['id'] in used or g.distance(point) > radius:
			continue
		pieces.setdefault(props['id'], []).append(g)
	for identifier, outlines in sorted(pieces.items()):
		for piece, polygon in enumerate(outlines):
			structure_id = f'enc:{identifier}' + (f'#{piece}' if len(outlines) > 1 else '')
			anchor, _, _ = _shore_anchor(polygon, bulkheads)
			props = next(p for g, p in floating if p['id'] == identifier)
			obstacles = surface_structures(shore, identifier)
			coords = list(closed_outline(polygon).coords)
			ring_points = _lonlat(coords)
			outline_kinds = outline_side_kinds(polygon)
			outline_others = others(polygon)
			segments = []
			for index, (a, b, la, lb) in enumerate(zip(coords, coords[1:], ring_points, ring_points[1:])):
				proof = _chart_side(a, b, anchor, water, land, obstacles, polygon)
				segments.append({'index': index, 'kind': outline_kinds[frozenset((a, b))], 'id_key': legacy_edge_key(stop_id, structure_id, ring_points, la, lb),
					'source': 'enc-feature', 'confidence': 'medium', 'verification': 'needs-crew-verification',
					'layer': props['feature_class'], 'lnam': identifier,
					'water_side_verified_by_pipeline': proof.get('chart_verified', proof['verified']), 'water_side_evidence': proof,
					'solid_side_evidence': solid_side_evidence(a, b, polygon, props['feature_class'], identifier, charted, land, outline_others)})
			polylines.append({'stop_id': stop_id, 'stop_name': context['stop_name'], 'structure_id': structure_id, 'berth_id': None,
				'source': 'enc-feature', 'polyline_id': f'floating-{stop_id}-{identifier}' + (f'-{piece}' if len(outlines) > 1 else ''),
				'role': 'floating-structure', 'coords': coords, 'lonlat': ring_points, 'segments': segments})
	# Fixed charted boundaries: land, piers drawn on land, and dry construction areas, solid on the right.
	lines = [(g, p) for g, p in shore if g.geom_type in ('LineString', 'MultiLineString')]
	lines += [(g.boundary, p) for g, p in solid_sources if p['feature_class'] == 'SLCONS']
	listed = [(g, p) for g, p in shore if g.geom_type in ('Point', 'MultiPoint') or p.get('water_level_effect_code') == 3]
	runs, left_out = fixed_structure_runs(stop_id, point, solid_sources, lines, listed, charted, land, floating_geometries, radius, clip)
	blockers = surface_structures(shore)
	for number, run in enumerate(runs):
		coords = run['coords']
		run_points = _lonlat(coords)
		segments = []
		for index, (a, b, la, lb, record) in enumerate(zip(coords, coords[1:], run_points, run_points[1:], run['segments'])):
			length = math.dist(a, b)
			left = ((a[1] - b[1]) / length, (b[0] - a[0]) / length)
			inside = ((a[0] + b[0]) / 2 - left[0] * WINDING_PROBE_M, (a[1] + b[1]) / 2 - left[1] * WINDING_PROBE_M)
			proof = _chart_side(a, b, inside, water, land, blockers, None)
			segments.append({'index': index, 'kind': record['kind'], 'id_key': fixed_edge_key(record['layer'], record['lnam'], la, lb),
				'source': 'enc-feature', 'confidence': 'medium', 'verification': 'needs-crew-verification',
				'layer': record['layer'], 'lnam': record['lnam'], 'classified_by': record['classified_by'],
				'water_side_verified_by_pipeline': proof['verified'], 'water_side_evidence': proof,
				'solid_side_evidence': record['solid_side_evidence']})
		polylines.append({'stop_id': stop_id, 'stop_name': context['stop_name'], 'structure_id': None, 'berth_id': None,
			'source': 'enc-feature', 'polyline_id': f'fixed-{stop_id}-{number}', 'role': 'fixed-structure', 'coords': coords, 'lonlat': run_points, 'segments': segments})
	for polyline in polylines:
		polyline['closed'] = polyline['coords'][0] == polyline['coords'][-1]
	return polylines, left_out


def assign_contact_ids(polylines):
	"""Hash every segment's physical identity; the existing exposed runs must keep their ids exactly.

	A new identity that collides with an earlier one (or hashes to zero) is rehashed with '+'
	suffixes, in file order, as World/EditorTools.WorldFileIds does for object ids.
	"""
	used = {}
	ordered = sorted(((polyline['role'] != 'landing' or polyline['polyline_id'].endswith('-back'), n, segment)
		for n, polyline in enumerate(polylines) for segment in polyline['segments']), key=lambda item: (item[0], item[1]))
	for legacy_last, _, segment in ordered:
		key = segment['id_key']
		identifier = contact_id(key)
		if not legacy_last and (identifier == 0 or identifier in used):
			raise ValueError(f'existing contact id {identifier} collides ({key} and {used.get(identifier)})')
		while identifier == 0 or identifier in used:
			key += '+'
			identifier = contact_id(key)
		used[identifier] = key
		segment['id_key'], segment['id'] = key, identifier


def build(slice_, log=print, reference_stops=None):
	out = paths.processed_dir(slice_.name)
	stops = _features(out, 'landings.geojson')
	shore = [(to_local(shape(f['geometry'])), f['properties']) for f in _features(out, 'shoreline_constructions.geojson')]
	candidates = [(g, p) for g, p in shore if p['feature_class'] in ('PONTON', 'FLODOC') or p.get('water_level_effect_code') == 7]
	# LNDARE gives the fixed land/water boundary; detailed SLCONS can include
	# gangways/fenders at a barge and falsely rotate its normal along the berth.
	lands = [(to_local(shape(f['geometry'])), {**f['properties'], 'feature_class': 'LNDARE'}) for f in _features(out, 'land_areas.geojson')]
	# Harbor cells also chart islets and narrow land as LNDARE points and lines: they have no
	# shoreline to anchor a barge to and no solid side, so they are only listed beside a landing.
	land_marks = [(g, p) for g, p in lands if g.geom_type not in ('Polygon', 'MultiPolygon')]
	lands = [(g, p) for g, p in lands if g.geom_type in ('Polygon', 'MultiPolygon')]
	bulkheads = [(g.boundary, props) for g, props in lands]
	water, land = chart_masks(out)
	routes = _features(out, 'routes.geojson')
	notes, notes_sha256 = crew_notes.load()
	slow = _slow_reports()
	features, contexts_by_stop = [], []
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
		candidate_records, contexts, berth_faces = [], {}, {}
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
			berth_faces[cp['id']] = {'polygon': g, 'props': cp, 'face': cf, 'normal': normal, 'proof': proof, 'anchor': ca, 'obstacles': obstacles}
		if candidate_records:
			chosen = choose_candidate_for_stop(p['stop_id'], candidate_records, match['id'])
			default_berth = notes.get(p['stop_id'], {}).get('default_berth_id')
			if default_berth:
				chosen = next(r for r in candidate_records if f"enc:{r['enc_id']}" == default_berth)
			elif notes.get(p['stop_id'], {}).get('modelled_enc_id'):
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
			reference_stop = next((f['properties'] for f in stops if f['properties']['stop_id'] == reference_id), None) or (reference_stops or {}).get(reference_id)
			if reference_stop is None:
				raise ValueError(f"{p['stop_name']}: crew note reference stop {reference_id} is not available")
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
		records = contact_segment_evidence(contact, face, proof, anchor, water, land, structures,
			None if derived else barge, bool(note and note.get('water_side_crew_reported')))
		for record in records:
			record['verification'] = segment_verification(record, note)
		props = {
			'stop_id': p['stop_id'], 'stop_name': p['stop_name'], 'route_ids': p.get('route_ids', []),
			'source': 'derived-from-gtfs' if derived else 'enc-feature',
			'confidence': face_confidence(derived, ambiguous, proof, note),
			'verification': structure_verification(note, f"derived-barge:{p['stop_id']}:{bulk['id']}" if derived else f"enc:{match['id']}", f"derived-barge:{p['stop_id']}:{bulk['id']}" if derived else f"enc:{match['id']}"),
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
			'contact_segment_evidence': records,
			'contact_note': 'Connected clockwise outline, excluding landward long side; water left, structure right. Derived barge ends use published ASCE dimensions and estimated placement. The whole structure, including the landward side, is in landing_structures.geojson.',
			'water_side_evidence': proof,
			'face_midpoint': {'lat': float(face_lat), 'lon': float(face_lon), 'north_m': mid.y, 'east_m': mid.x},
			'gtfs_to_face_m': face.distance(point),
			'water_side': proof['verified'],
			'water_side_verified_by_pipeline': proof.get('chart_verified', proof['verified']),
			'water_side_crew_reported': bool(note and note.get('water_side_crew_reported')),
			'derivation': {'length_m': BARGE_LENGTH_M, 'width_m': BARGE_WIDTH_M, 'nominal_gangway_m': GANGWAY_M,
				'note': 'Estimated horizontal 85 ft gangway (published range 70–100 ft), barge parallel to nearest ENC LNDARE shoreline boundary. GTFS offshore offset retained if larger; not a survey.'} if derived else None,
		}
		choices = note.get('berths') if note else None
		if choices:
			default_id = note['default_berth_id']
			choices = sorted(enumerate(choices), key=lambda item: (item[1]['id'] != default_id, item[0]))
		else:
			choices = [(0, {'id': props['outline_id'], 'name': 'Main berth'})]
		selectable = []
		for order, (_, choice) in enumerate(choices):
			if choice['id'] == props['outline_id']:
				selected_face, selected_normal = face, normal
			else:
				selected = berth_faces[choice['id'].removeprefix('enc:')]
				selected_face, selected_normal = selected['face'], selected['normal']
			selectable.append({**choice, 'order': order,
				'face_geometry': geojsonio.make_feature(to_wgs84(selected_face), {})['geometry'],
				'outward_normal_ne': list(selected_normal),
				'crew_note': note['id'] if note and note.get('berths') else None})
			if note and note.get('berths'):
				selectable[-1]['use_evidence'] = {'note': note, 'crew_notes_sha256': notes_sha256}
				selectable[-1]['side_evidence'] = berth_side_support(selected_face, selected_normal, near)
				selectable[-1]['corridor_structures'] = berth_corridor_structures(selected_face, selected_normal, shore, choice['id'].removeprefix('enc:'))
		props['berths'] = selectable
		features.append(geojsonio.make_feature(to_wgs84(barge), props))
		# Every other candidate is its own berth, ranked by the same historical AIS usage.
		ring_lonlat = features[-1]['geometry']['coordinates'][0]
		ranking = sorted(candidate_records, key=lambda r: (-r['ais_slow_reports_within_10m'], r['distance_to_gtfs_m'], r['enc_id']))
		berths = []
		for rank, record in enumerate(ranking, 1):
			if not derived and record['enc_id'] == match['id']:
				continue
			berth = dict(berth_faces[record['enc_id']], rank=rank, ais_slow_reports_within_10m=record['ais_slow_reports_within_10m'],
				distance_to_gtfs_m=record['distance_to_gtfs_m'])
			berth['confidence'] = face_confidence(False, False, berth['proof'], None)
			berths.append(berth)
		own_rank = next((rank for rank, record in enumerate(ranking, 1) if not derived and record['enc_id'] == match['id']), 1)
		contexts_by_stop.append({'stop_id': p['stop_id'], 'stop_name': p['stop_name'], 'point': point, 'barge': barge, 'derived': derived,
			'outline_id': props['outline_id'], 'match': match, 'face': face, 'contact': contact, 'records': records, 'note': note,
			'ring_lonlat': ring_lonlat, 'contact_lonlat': contact_geo['geometry']['coordinates'],
			'source': props['source'], 'confidence': props['confidence'], 'contact_confidence': props['contact_confidence'],
			'anchor': anchor, 'structures': structures, 'berths': berths, 'selectable_berths': selectable, 'rank': own_rank})
	geojsonio.write_collection(out / 'landing_faces.geojson', features, 'landing_faces', {
		'note': 'Geometry is barge outline; properties.face_geometry is the berthing line in WGS84, face_local_coordinates is [north,east]. Outward normal heading is clockwise from true north. All assigned faces require crew verification; ENC outlines do not identify the operational berth.',
		'derived_size_source': ASCE_URL})
	log(f'  landing_faces.geojson: {len(features)} landings ({sum(f["properties"]["source"] == "enc-feature" for f in features)} ENC outlines)')
	areas = [(to_local(shape(f['geometry'])), f['properties']) for f in _features(out, 'depth_areas.geojson')
		if f['properties'].get('min_depth_m') is not None and f['properties']['min_depth_m'] >= 0]
	charted = ChartedWater(areas, _unsurveyed(slice_))
	write_structures(out, contexts_by_stop, shore + land_marks, lands, bulkheads, water, land, getattr(slice_, 'polygon', None), log, charted)


def write_structures(out, contexts, shore, lands, bulkheads, water, land, clip, log=print, charted=None):
	polylines, left_out = [], []
	for context in contexts:
		stop_polylines, stop_left_out = stop_structures(context, shore, lands, bulkheads, water, land, clip, charted=charted)
		for polyline in stop_polylines:
			polyline['verification'] = structure_verification(context['note'], polyline['berth_id'], context['outline_id'])
			for segment in polyline['segments']:
				segment['verification'] = polyline['verification']
			polyline['berth_rank'] = (context['rank'] if polyline['polyline_id'].startswith('landing-') else
				next((b['rank'] for b in context['berths'] if f"enc:{b['props']['id']}" == polyline['berth_id']), None))
		mark_berth_faces(stop_polylines, context.get('selectable_berths', []))
		polylines += stop_polylines
		left_out += stop_left_out
	keys = [segment['id_key'] for polyline in polylines for segment in polyline['segments']]
	if len(keys) != len(set(keys)):
		raise ValueError('a physical structure edge is within the radius of two stops; landing structures need an ownership rule')
	assign_contact_ids(polylines)
	# IDs are assigned after every structure is known. Repeat the ordered face IDs on each face segment.
	for polyline in polylines:
		for segment in polyline['segments']:
			if segment.get('berth_face'):
				segment['berth_face_segment_ids'] = [s['id'] for s in ordered_berth_segments(polylines, polyline['stop_id'], segment['berth_id'])]
	features = []
	for polyline in polylines:
		props = {key: polyline[key] for key in ('stop_id', 'stop_name', 'polyline_id', 'role', 'structure_id', 'berth_id', 'berth_rank', 'source', 'closed', 'verification')}
		props['segments'] = [{'index': s['index'], 'id': s['id'], **{k: v for k, v in s.items() if k not in ('index', 'id') and not k.startswith('_')}} for s in polyline['segments']]
		features.append(geojsonio.make_feature(LineString(polyline['lonlat']), props))
	kinds = sorted({s['kind'] for polyline in polylines for s in polyline['segments']})
	geojsonio.write_collection(out / 'landing_structures.geojson', features, 'landing_structures', {
		'radius_m': STRUCTURE_RADIUS_M, 'reference': 'GTFS stop point; an edge is included when any part of it lies within the radius',
		'kinds': kinds, 'left_out': left_out,
		'eligibility': 'Runtime contact: water_side_verified_by_pipeline or water_side_crew_reported; a selectable crew-use berth also on verified solid-side geometry with a clear 0.5 m UNSARE probe and outward slow T/K reports at least as numerous as inward (and nonzero); any non-primary segment also when solid_side_evidence.verified (closed charted outline solid on the right by winding; 0.5 m out on the left charted water, a depth area or ENC UNSARE, with no land or other structure). UNSARE proves the water side only, never depth. Open lines in water, points and always-submerged structures are never contact geometry; they are listed in left_out.',
		'note': 'One feature per contact polyline, solid on the right and water on the left. landing-<stop> is the existing exposed run with unchanged ids; landing-<stop>-back closes the barge. Confidence is ordinal geometry confidence. Verification labels name only the applicable crew evidence (landing side or berth use); neither verifies the outline, depth or clearance.'})
	log(f'  landing_structures.geojson: {len(features)} polylines, {sum(len(f["properties"]["segments"]) for f in features)} segments, {len(left_out)} charted objects left out of contact')


def build_tile(tile, log=print):
	"""Harbor tiles (wave 2): landing faces and whole structures for the GTFS stops this tile owns."""
	from ferrydata import tiles
	stops, _ = tiles._stop_inventory(tiles._feed())
	build(tile, log, reference_stops={s['stop_id']: s for s in stops})


def ordered_berth_segments(polylines, stop_id, berth_id):
	"""Face subdivisions in start-to-end order, including a face crossing a closed ring's first vertex."""
	segments = [s for p in polylines if p['stop_id'] == stop_id for s in p['segments']
		if s.get('berth_face') and s.get('berth_id') == berth_id]
	by_start = {tuple(s['_berth_start']): s for s in segments}
	ends = {tuple(s['_berth_end']) for s in segments}
	first = [s for s in segments if tuple(s['_berth_start']) not in ends]
	if len(first) != 1:
		raise ValueError(f'stop {stop_id} berth {berth_id}: face must be one open joined line')
	ordered = [first[0]]
	while tuple(ordered[-1]['_berth_end']) in by_start:
		following = by_start[tuple(ordered[-1]['_berth_end'])]
		if following in ordered:
			raise ValueError(f'stop {stop_id} berth {berth_id}: cyclic face')
		ordered.append(following)
	if len(ordered) != len(segments):
		raise ValueError(f'stop {stop_id} berth {berth_id}: disconnected face')
	return ordered


def mark_berth_faces(polylines, berths):
	for berth in berths:
		found = []
		for polyline in polylines:
			if polyline.get('berth_id') != berth['id']:
				continue
			for segment in polyline['segments']:
				if segment['kind'] != 'berth-face':
					continue
				i = segment['index']
				segment.update(berth_face=True, berth_name=berth['name'], berth_order=berth['order'],
					_berth_start=polyline['lonlat'][i], _berth_end=polyline['lonlat'][i + 1])
				if berth.get('crew_note'):
					segment['berth_use_crew_note'] = berth['crew_note']
					for source, target in (('use_evidence', 'berth_use_evidence'), ('side_evidence', 'berth_side_evidence'), ('corridor_structures', 'berth_corridor_structures')):
						if source in berth: segment[target] = berth[source]
				found.append(segment)
		if not found:
			raise ValueError(f"berth {berth['id']}: no selected face segments")


def berth_side_support(face, outward_ne, reports, classes=None):
	"""Slow T/K reports within the face span and 15 m either side; evidence, not a ramp-side survey."""
	if classes is None:
		from ferrydata.operational_depth import identity_classes
		classes = identity_classes()
	outward = inward = 0
	if reports is not None and len(reports):
		fleet = reports[reports.mmsi.isin(classes) & (reports.sog_kn < .5)]
		a = np.array(face.coords[0]); b = np.array(face.coords[-1])
		length = np.linalg.norm(b - a); tangent = (b - a) / length
		delta = np.stack([fleet.east_m - a[0], fleet.north_m - a[1]], axis=1)
		along = delta @ tangent; side = delta @ np.array(outward_ne[::-1])
		span = (along >= 0) & (along <= length)
		outward = int(np.sum(span & (side > 0) & (side <= 15)))
		inward = int(np.sum(span & (side < 0) & (side >= -15)))
	return {'verified': outward > 0 and outward >= inward, 'method': 'slow-TK-face-span', 'distance_m': 15.,
		'sog_below_kn': .5, 'outward_reports': outward, 'inward_reports': inward,
		'note': 'T/K fleet identity audit; positions project onto the whole face span. AIS antenna positions do not establish ramp side, depth or clearance.'}


BERTH_SOLID_BASIS = 'solid-side + crew berth use (water side unsurveyed: UNSARE)'


def berth_solid_contact(segment, current_note=None, notes_sha256=None):
	"""The crew-use exception, separate from water-side proof. Validation supplies the live note and digest."""
	use = segment.get('berth_use_evidence') or {}
	note = use.get('note') or {}
	solid = segment.get('solid_side_evidence') or {}
	side = segment.get('berth_side_evidence') or {}
	if current_note is not None and (note != current_note or use.get('crew_notes_sha256') != notes_sha256):
		return False
	outward, inward = side.get('outward_reports', 0), side.get('inward_reports', 0)
	return bool(segment.get('berth_face') is True and note.get('kind') == 'berth-use-policy'
		and segment.get('berth_use_crew_note') == note.get('id') and note.get('id')
		and any(b.get('id') == segment.get('berth_id') for b in note.get('berths', []))
		and isinstance(use.get('crew_notes_sha256'), str) and len(use['crew_notes_sha256']) == 64
		and solid.get('verified') is True and solid.get('winding_check') is True
		and solid.get('probe_offset_m') == .5 and solid.get('probe_in_charted_water') is True
		and solid.get('probe_water_layer') == 'UNSARE' and solid.get('probe_in_lndare') is False
		and solid.get('probe_in_other_structure') is False and side.get('verified') is True
		and type(outward) is int and type(inward) is int and outward > 0 and 0 <= inward <= outward)


def berth_corridor_structures(face, normal, shore, own_id):
	"""Disclose every unchanged 8 x 30 m corridor blocker; points keep unknown role and clearance."""
	mid = face.interpolate(.5, normalized=True); n, e = normal
	corridor = Polygon([(mid.x + e*d + n*w, mid.y + n*d - e*w) for d,w in ((0,-4),(0,4),(30,4),(30,-4))])
	blockers = surface_structures(shore, own_id)
	along = np.array(face.coords[-1]) - np.array(face.coords[0]); along /= np.linalg.norm(along)
	rows = []
	for geometry, props in shore:
		if not geometry.intersects(corridor) or not any(geometry.equals(g) for g in blockers): continue
		point = geometry if geometry.geom_type == 'Point' else nearest_points(face, geometry)[1]
		delta = np.array([point.x - mid.x, point.y - mid.y])
		rows.append({'lnam': props['id'], 'object_class': props['feature_class'], 'geometry_type': geometry.geom_type,
			'water_level_effect_code': props.get('water_level_effect_code'), 'distance_from_face_m': float(face.distance(geometry)),
			'signed_outward_m': float(delta @ np.array([e,n])), 'along_from_midpoint_m': float(delta @ along),
			'position_lonlat': list(to_wgs84(point).coords[0]),
			'note': 'Charted object; role and clearance unverified. Point geometry is not runtime contact.'})
	return sorted(rows, key=lambda r: (r['lnam'], r['object_class']))
