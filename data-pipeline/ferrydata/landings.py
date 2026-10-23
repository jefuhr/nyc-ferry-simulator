"""Charted or explicitly estimated landing outlines and outward berthing faces.

Shapely coordinates here are (east, north) metres. Vectors are (north, east).
A GTFS stop identifies a passenger landing, not an assigned berth or surveyed face.
"""
import math

import numpy as np
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.ops import unary_union
from shapely import transform

from ferrydata import approach, geodesy, geojsonio, paths

ASCE_URL = 'https://www.asce.org/publications-and-news/civil-engineering-source/civil-engineering-magazine/article/2021/06/floating-ferry-landings-help-solve-nyc-transit-travails'
BARGE_LENGTH_M = 90 * .3048
BARGE_WIDTH_M = 35 * .3048
GANGWAY_M = 85 * .3048  # midpoint of published 70–100 ft range; horizontal estimate
MATCH_MAX_M = 50.


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


def match_barge(stop, candidates):
	"""Only polygon objects can supply an outline. Nearby lines/points are evidence, not invented polygons."""
	hits = sorted(((g.distance(stop), p['id'], g, p) for g, p in candidates if g.geom_type == 'Polygon' and g.distance(stop) <= MATCH_MAX_M), key=lambda x: (x[0], x[1]))
	if not hits:
		return None, None, False
	ambiguous = len(hits) > 1 and hits[1][0] - hits[0][0] < 10.
	return hits[0][2], hits[0][3], ambiguous


def face_on_water_side(face, outward_ne, shore_anchor_en, water, land):
	"""Independent chart test just outside the floating outline, away from the bulkhead.

	ENC depth areas commonly exclude the pontoon footprint itself. Probe a parallel
	line 0.5 m outside the face (also avoids GeoJSON rounding at shared edges),
	and 12 m outside its midpoint. Neither probe may intersect ENC land.
	"""
	mid = face.interpolate(.5, normalized=True)
	n, e = outward_ne
	distance_out = (mid.x - shore_anchor_en[0]) * e + (mid.y - shore_anchor_en[1]) * n
	probe = Point(mid.x + e * 12, mid.y + n * 12)
	outside = LineString([(x + e * .5, y + n * .5) for x, y in face.coords])
	return bool(distance_out > 0 and water.covers(outside) and water.covers(probe)
		and not land.intersects(outside) and not land.covers(probe))


def _features(out, name):
	return geojsonio.read_collection(out / name)['features']


def chart_masks(out):
	water = unary_union([to_local(shape(f['geometry'])) for f in _features(out, 'depth_areas.geojson')
		if f['properties'].get('min_depth_m') is not None and f['properties']['min_depth_m'] >= 0])
	land = unary_union([to_local(shape(f['geometry'])) for f in _features(out, 'land_areas.geojson')])
	return water, land


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
	features = []
	for stop in stops:
		p = stop['properties']
		point = Point(p['east_m'], p['north_m'])
		barge, match, ambiguous = match_barge(point, candidates)
		# Use the nearest fixed construction, excluding outlines overlapping the floating barge.
		fixed = [(g, props) for g, props in bulkheads if barge is None or not g.intersects(barge.buffer(-.2))]
		reference = barge.centroid if barge is not None else point
		nearest, bulk = min(fixed, key=lambda x: x[0].distance(reference))
		anchor, tangent = approach.face_geometry((reference.x, reference.y), [nearest])
		parts = [to_local(shape(f['geometry'])) for f in routes if set(f['properties']['route_ids']) & set(p.get('route_ids', []))]
		route = unary_union(parts) if parts else None
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
		face, normal = berthing_face(barge, outward)
		face_geo = geojsonio.make_feature(to_wgs84(face), {})
		mid = face.interpolate(.5, normalized=True)
		face_lat, face_lon = geodesy.from_local(mid.y, mid.x)
		props = {
			'stop_id': p['stop_id'], 'stop_name': p['stop_name'], 'route_ids': p.get('route_ids', []),
			'source': 'derived-from-gtfs' if derived else 'enc-feature',
			'confidence': 'low' if derived or ambiguous else 'medium',
			'verification': 'needs-crew-verification',
			'outline_source': ASCE_URL if derived else 'NOAA ENC',
			'enc_id': match['id'] if match else None, 'enc_cell': match.get('cell') if match else None,
			'enc_source_date': match.get('source_date') if match else None,
			'ambiguous_enc_match': ambiguous, 'side_chosen_by': how,
			'bulkhead_enc_id': bulk['id'], 'bulkhead_feature_class': 'LNDARE boundary', 'bulkhead_anchor_en_m': list(anchor),
			'face_heading_deg_true': heading_deg(normal), 'outward_normal_ne': list(normal),
			'face_geometry': face_geo['geometry'], 'face_local_coordinates': face_geo['local_coordinates'],
			'face_midpoint': {'lat': float(face_lat), 'lon': float(face_lon), 'north_m': mid.y, 'east_m': mid.x},
			'gtfs_to_face_m': face.distance(point),
			'water_side': face_on_water_side(face, normal, anchor, water, land),
			'derivation': {'length_m': BARGE_LENGTH_M, 'width_m': BARGE_WIDTH_M, 'nominal_gangway_m': GANGWAY_M,
				'note': 'Estimated horizontal 85 ft gangway (published range 70–100 ft), barge parallel to nearest ENC LNDARE shoreline boundary. GTFS offshore offset retained if larger; not a survey.'} if derived else None,
		}
		features.append(geojsonio.make_feature(to_wgs84(barge), props))
	geojsonio.write_collection(out / 'landing_faces.geojson', features, 'landing_faces', {
		'note': 'Geometry is barge outline; properties.face_geometry is the berthing line in WGS84, face_local_coordinates is [north,east]. Outward normal heading is clockwise from true north. All assigned faces require crew verification; ENC outlines do not identify the operational berth.',
		'derived_size_source': ASCE_URL})
	log(f'  landing_faces.geojson: {len(features)} landings ({sum(f["properties"]["source"] == "enc-feature" for f in features)} ENC outlines)')
