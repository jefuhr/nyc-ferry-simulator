"""Whole landing structures as contact geometry.

Failure modes: an open or reversed barge outline (solid on the water side), a joint that is not one shared
vertex, a berth at a multi-berth stop dropped or merged into the landing, a kind or provenance field missing
from a segment, the radius cutoff letting distant structures in or near ones out, a clip-boundary edge
becoming a wall, an eligibility path that lets an open line in water or a MORFAC point become contact, the
stale crew label, ids that change for the existing segments, and a second build that differs.
"""
import json
import math
from pathlib import Path
from types import SimpleNamespace

import pytest
from shapely.geometry import LineString, MultiPolygon, Point, Polygon, box, shape
from shapely.geometry.polygon import orient

from ferrydata import geojsonio, landings

FIXTURES = Path(__file__).parent / 'fixtures'


def _left_outside(a, b, polygon):
	length = math.dist(a, b)
	left = ((a[1] - b[1]) / length, (b[0] - a[0]) / length)
	mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
	return (not polygon.contains(Point(mid[0] + left[0] * .05, mid[1] + left[1] * .05))
		and polygon.contains(Point(mid[0] - left[0] * .05, mid[1] - left[1] * .05)))


@pytest.mark.parametrize('reverse', [False, True])
def test_whole_barge_outline_closes_clockwise_water_left_after_the_exposed_run(reverse):
	coords = [(-14, 20), (14, 20), (14, 30), (-14, 30)]
	barge = Polygon(coords[::-1] if reverse else coords)
	face, normal = landings.berthing_face(barge, (1, 0))
	exposed = landings.water_facing_outline(barge, face, normal)
	back = landings.outline_closure(barge, exposed)
	assert back.coords[0] == exposed.coords[-1], 'the back starts where the exposed run ends'
	assert back.coords[-1] == exposed.coords[0], 'and closes the ring at its start'
	ring = list(exposed.coords) + list(back.coords)[1:]
	assert Polygon(ring).equals(barge)
	assert exposed.length + back.length == pytest.approx(barge.length)
	for a, b in zip(ring, ring[1:]):
		assert _left_outside(a, b, barge)


def test_barge_kinds_follow_the_long_sides_ends_and_selected_face():
	barge = Polygon([(-5, 20), (5, 20), (5, 30), (5, 40), (5, 50), (-5, 50), (-5, 40), (-5, 30)])
	kinds = landings.outline_side_kinds(barge)
	assert kinds[frozenset({(5., 30.), (5., 40.)})] == 'barge-side', 'a tessellated long side stays one side'
	assert kinds[frozenset({(-5., 20.), (5., 20.)})] == 'barge-end'
	assert kinds[frozenset({(-5., 50.), (5., 50.)})] == 'barge-end'
	assert sum(kind == 'barge-side' for kind in kinds.values()) == 6


def test_a_long_side_with_a_slight_bend_stays_a_barge_side():
	# South Williamsburg's charted SE side turns 2.6 degrees halfway; neither half is an end.
	barge = Polygon([(0, 0), (11, 0), (11, 25.6), (0, 25.6), (-.57, 12.8)])
	kinds = landings.outline_side_kinds(barge)
	assert kinds[frozenset({(0., 25.6), (-.57, 12.8)})] == 'barge-side'
	assert kinds[frozenset({(-.57, 12.8), (0., 0.)})] == 'barge-side'
	assert kinds[frozenset({(0., 0.), (11., 0.)})] == 'barge-end'


def test_contact_ids_match_the_unity_importer_hash_and_are_positive():
	# World/Import StableId: FNV-1a over UTF-16 code units, masked to a positive Int32.
	assert landings.contact_id('8:enc:022679C2F5BA764B:edge:1') == landings.contact_id('8:enc:022679C2F5BA764B:edge:1')
	assert landings.contact_id('') == 2166136261 & 0x7fffffff
	assert landings.contact_id('a') == ((2166136261 ^ 97) * 16777619) % 2 ** 32 & 0x7fffffff
	with pytest.raises(ValueError):
		landings.contact_id('\U0001F6A2')


def test_legacy_edge_keys_ignore_winding_and_start_vertex():
	ring = [[-74.0151, 40.7], [-74.0151, 40.7001], [-74.0149, 40.7001], [-74.0149, 40.7], [-74.0151, 40.7]]
	key = landings.legacy_edge_key('fixture', 'enc:ENC-fixture', ring, ring[1], ring[2])
	assert key == 'fixture:enc:ENC-fixture:edge:1'
	assert landings.legacy_edge_key('fixture', 'enc:ENC-fixture', ring[::-1], ring[2], ring[1]) == key
	with pytest.raises(ValueError):
		landings.legacy_edge_key('fixture', 'enc:ENC-fixture', ring, ring[0], ring[2])


def test_fixed_edge_keys_are_endpoint_identities_independent_of_direction():
	a, b = [-73.98, 40.70], [-73.97, 40.71]
	assert landings.fixed_edge_key('LNDARE', 'L1', a, b) == landings.fixed_edge_key('LNDARE', 'L1', b, a)
	assert landings.fixed_edge_key('LNDARE', 'L1', a, b) != landings.fixed_edge_key('LNDARE', 'L2', a, b)


@pytest.mark.parametrize('category, kind', [(4, 'pier'), (5, 'pier'), (6, 'bulkhead'), (10, 'bulkhead'), (8, 'shoreline'), (None, 'shoreline')])
def test_structure_kind_comes_from_the_charted_category(category, kind):
	assert landings.structure_kind({'feature_class': 'SLCONS', 'category_code': category}) == kind
	assert landings.structure_kind({'feature_class': 'COALNE', 'category_code': None}) == 'shoreline'
	assert landings.structure_kind(None) == 'shoreline'


def _land_scene():
	"""A bulkhead along north = 0 with a pier 20 m wide and 60 m long; a GTFS stop 30 m off the pier."""
	land = box(-400, -400, 400, 0).union(box(-10, 0, 10, 60))
	water = box(-400, 0, 400, 400).difference(box(-10, 0, 10, 60))
	sources = [(land, {'id': 'LAND1', 'feature_class': 'LNDARE'})]
	lines = [(LineString([(-10, 0), (-10, 60), (10, 60), (10, 0)]), {'id': 'PIER1', 'feature_class': 'SLCONS', 'category_code': 4, 'water_level_effect_code': 2}),
		(LineString([(10, 0), (400, 0)]), {'id': 'WALL1', 'feature_class': 'SLCONS', 'category_code': 10, 'water_level_effect_code': 2})]
	return land, water, sources, lines


def test_fixed_structures_are_land_right_runs_with_kinds_inside_the_radius_only():
	land, water, sources, lines = _land_scene()
	stop = Point(40, 50)
	runs, left_out = landings.fixed_structure_runs('S', stop, sources, lines, [], water, land, [], radius=100.)
	edges = [(segment['kind'], segment['solid_side_evidence']['lnam']) for run in runs for segment in run['segments']]
	assert ('pier', 'LAND1') in edges
	assert ('bulkhead', 'LAND1') in edges
	assert ('shoreline', 'LAND1') in edges, 'land boundary without a charted construction is shoreline'
	for run in runs:
		coords = run['coords']
		for a, b in zip(coords, coords[1:]):
			assert _left_outside(a, b, land)
			assert LineString([a, b]).distance(stop) <= 100.
	far = Point(390, -200)
	assert all(LineString([a, b]).distance(far) > 100 for run in runs for a, b in zip(run['coords'], run['coords'][1:]))


def test_radius_cutoff_keeps_an_edge_that_touches_the_circle_and_drops_one_beyond_it():
	land = box(-400, -400, 400, 0)
	water = box(-400, 0, 400, 400)
	sources = [(land, {'id': 'LAND1', 'feature_class': 'LNDARE'})]
	inside, _ = landings.fixed_structure_runs('S', Point(0, 149.9), sources, [], [], water, land, [], radius=150.)
	outside, _ = landings.fixed_structure_runs('S', Point(0, 150.1), sources, [], [], water, land, [], radius=150.)
	assert inside and not outside


def test_clip_boundary_edges_never_become_walls():
	from ferrydata import geodesy
	# Land clipped at east = 30 m by the output square: that cut is not shoreline.
	land = box(-400, -400, 30, 0)
	water = box(-400, 0, 30, 400)
	lat, lon = geodesy.from_local([-400, -400, 400, 400], [-400, 30, 30, -400])
	clip = Polygon(zip(lon, lat))
	runs, _ = landings.fixed_structure_runs('S', Point(0, 20), [(land, {'id': 'LAND1', 'feature_class': 'LNDARE'})], [], [], water, land, [], radius=150., clip=clip)
	for run in runs:
		for a, b in zip(run['coords'], run['coords'][1:]):
			assert not (a[0] == pytest.approx(30) and b[0] == pytest.approx(30)), 'clip edge exported'
	assert sum(len(run['coords']) - 1 for run in runs) == 1


def test_solid_side_eligibility_needs_charted_water_and_no_other_structure_on_the_left():
	land, water, sources, lines = _land_scene()
	pontoon = box(-30, 20, -20, 50)  # moored 10 m off the pier's west side
	runs, _ = landings.fixed_structure_runs('S', Point(0, 30), sources, lines, [], water.difference(pontoon), land, [pontoon], radius=200.)
	by_kind = {}
	for run in runs:
		for segment in run['segments']:
			by_kind.setdefault(segment['kind'], []).append(segment['solid_side_evidence'])
	evidence = by_kind['pier'][0]
	assert evidence['verified']
	assert evidence['method'] == 'enc-closed-outline'
	assert evidence['layer'] == 'LNDARE' and evidence['lnam'] == 'LAND1'
	assert evidence['winding'] == 'clockwise: outline interior on the right, start to end'
	assert evidence['probe_offset_m'] == .5
	assert len(evidence['probe_ne_m']) == 2 and len(evidence['probe_lonlat']) == 2
	assert evidence['probe_in_charted_water'] and not evidence['probe_in_lndare']
	# A wall facing uncharted water is recorded but not eligible.
	dry, _ = landings.fixed_structure_runs('S', Point(0, 30), sources, lines, [], box(1000, 1000, 1001, 1001), land, [], radius=200.)
	assert not any(segment['solid_side_evidence']['verified'] for run in dry for segment in run['segments'])


def test_open_line_in_water_and_morfac_point_are_listed_never_contact():
	land, water, sources, lines = _land_scene()
	gangway = (LineString([(30, 30), (60, 30)]), {'id': 'GANGWAY', 'feature_class': 'PONTON', 'category_code': None, 'water_level_effect_code': None})
	dolphin = (Point(50, 50), {'id': 'DOLPHIN', 'feature_class': 'MORFAC', 'category_code': None, 'water_level_effect_code': 2})
	sunk = (box(20, 10, 30, 20), {'id': 'SUNK', 'feature_class': 'SLCONS', 'category_code': 4, 'water_level_effect_code': 3})
	runs, left_out = landings.fixed_structure_runs('S', Point(40, 40), sources, lines + [gangway], [dolphin, sunk], water, land, [], radius=150.)
	reasons = {item['lnam']: item for item in left_out}
	assert reasons['GANGWAY']['reason'].startswith('open line in water')
	assert reasons['GANGWAY']['layer'] == 'PONTON'
	assert reasons['DOLPHIN']['reason'].startswith('point')
	assert reasons['DOLPHIN']['layer'] == 'MORFAC'
	assert reasons['SUNK']['reason'].startswith('always submerged')
	for item in left_out:
		assert item['stop_id'] == 'S'
		assert len(item['position_lonlat']) == 2 and len(item['position_ne_m']) == 2
	contact = [segment['solid_side_evidence']['lnam'] for run in runs for segment in run['segments']]
	assert not {'GANGWAY', 'DOLPHIN', 'SUNK'} & set(contact)


@pytest.mark.parametrize('kind, note, primary, crew, expected', [
	('face-orientation', True, True, True, 'crew-reported'),
	('face-orientation', True, False, False, 'needs-crew-verification'),
	('face-orientation', True, True, False, 'needs-crew-verification'),
	('berth-use-policy', True, True, False, 'needs-crew-verification'),
	(None, False, True, False, 'needs-crew-verification'),
])
def test_label_is_crew_reported_only_on_face_orientation_primary_subsegments(kind, note, primary, crew, expected):
	record = {'on_primary_face': primary}
	if crew:
		record['water_side_crew_reported'] = True
	assert landings.segment_verification(record, {'kind': kind} if note else None) == expected


def _build_fixture(monkeypatch, tmp_path, name, slow=None):
	fixture = json.loads((FIXTURES / name).read_text())
	monkeypatch.setattr(landings, '_features', lambda out, file: fixture[file])
	monkeypatch.setattr(landings.paths, 'processed_dir', lambda slice_name: tmp_path)
	monkeypatch.setattr(landings.paths, 'ais_processed_dir', lambda: tmp_path)
	if slow is not None:
		monkeypatch.setattr(landings, '_slow_reports', lambda: slow)
	landings.build(SimpleNamespace(name='fixture', polygon=None), log=lambda message: None)
	faces = geojsonio.read_collection(tmp_path / 'landing_faces.geojson')['features']
	structures = geojsonio.read_collection(tmp_path / 'landing_structures.geojson')
	return fixture, faces, structures


def test_south_williamsburg_whole_structure_keeps_ids_and_flags_and_drops_the_dolphin(monkeypatch, tmp_path):
	_, faces, structures = _build_fixture(monkeypatch, tmp_path, 'south-williamsburg.json')
	features = structures['features']
	landing = next(f for f in features if f['properties']['polyline_id'] == 'landing-8')
	segments = landing['properties']['segments']
	assert [s['id'] for s in segments] == [216221522, 149111046], 'the existing contacts.json ids are unchanged'
	assert [s['kind'] for s in segments] == ['barge-end', 'berth-face']
	assert segments[1]['primary_face'] and segments[1]['water_side_crew_reported']
	assert segments[1]['verification'] == 'crew-reported'
	assert segments[0]['verification'] == 'needs-crew-verification'
	assert faces[0]['properties']['verification'] == 'needs-crew-verification', 'the landing-level label stays'
	assert faces[0]['properties']['contact_segment_evidence'][1]['verification'] == 'crew-reported'
	back = next(f for f in features if f['properties']['polyline_id'] == 'landing-8-back')
	ring = landing['local_coordinates'] + back['local_coordinates'][1:]
	assert ring[0] == ring[-1], 'the whole barge closes'
	assert {s['kind'] for s in back['properties']['segments']} <= {'barge-side', 'barge-end'}
	for feature in features:
		for segment in feature['properties']['segments']:
			for field in ('id', 'kind', 'source', 'confidence', 'verification', 'water_side_verified_by_pipeline', 'solid_side_evidence'):
				assert field in segment, (feature['properties']['polyline_id'], field)
	left_out = structures['left_out']
	dolphin = next(item for item in left_out if item['lnam'] == '022646E95F81764B')
	assert dolphin['layer'] == 'MORFAC' and dolphin['stop_id'] == '8'
	assert all(item['lnam'] != '022646E95F81764B' for f in features for item in f['properties']['segments'] if item.get('lnam'))
	assert structures['radius_m'] == landings.STRUCTURE_RADIUS_M


def test_every_charted_berth_at_pier_11_is_its_own_berth_ranked_by_ais(monkeypatch, tmp_path):
	import pandas as pd
	fixture = json.loads((FIXTURES / 'pier-11.json').read_text())
	shore = {f['properties']['id']: landings.to_local(shape(f['geometry'])) for f in fixture['shoreline_constructions.geojson']}
	# Synthetic slow reports at each pontoon's centroid: 3 at the middle, 2 at the north, 1 at the south.
	rows = []
	for lnam, count in (('02261FA117EFB198', 3), ('0226111D16AAB198', 2), ('0226015B0512B198', 1)):
		centre = shore[lnam].centroid
		rows += [{'east_m': centre.x, 'north_m': centre.y, 'sog_kn': .1}] * count
	_, faces, structures = _build_fixture(monkeypatch, tmp_path, 'pier-11.json', pd.DataFrame(rows))
	berths = [f['properties'] for f in structures['features'] if f['properties']['role'] in ('landing', 'berth') and f['properties']['polyline_id'] != 'landing-87-back']
	by_id = {p['berth_id']: p for p in berths}
	assert set(by_id) == {'enc:0226015B0512B198', 'enc:02261FA117EFB198', 'enc:0226111D16AAB198'}
	assert [by_id[k]['berth_rank'] for k in ('enc:02261FA117EFB198', 'enc:0226111D16AAB198', 'enc:0226015B0512B198')] == [1, 2, 3]
	assert faces[0]['properties']['enc_id'] == '0226015B0512B198', 'the crew-modelled primary berth is unchanged'
	for berth_id, props in by_id.items():
		kinds = [s['kind'] for s in props['segments']]
		assert 'berth-face' in kinds, berth_id
		if props['role'] == 'berth':
			assert props['closed'], berth_id
			assert not any(s.get('primary_face') for s in props['segments']), berth_id
	far = [f['properties'] for f in structures['features'] if f['properties']['role'] == 'floating-structure']
	assert [p['structure_id'] for p in far] == ['enc:02260848030BB198'], 'beyond 50 m it is a pontoon, not a berth'
	assert all(s['kind'] in ('barge-side', 'barge-end') and 'berth_id' not in s for s in far[0]['segments'])


def test_two_builds_write_identical_bytes(monkeypatch, tmp_path):
	first, second = tmp_path / 'a', tmp_path / 'b'
	first.mkdir(); second.mkdir()
	_build_fixture(monkeypatch, first, 'south-williamsburg.json')
	_build_fixture(monkeypatch, second, 'south-williamsburg.json')
	for name in ('landing_faces.geojson', 'landing_structures.geojson'):
		assert (first / name).read_bytes() == (second / name).read_bytes(), name


def test_a_harbor_tile_builds_its_own_landings_and_finds_a_crew_reference_stop_outside_the_tile(monkeypatch, tmp_path):
	# North Williamsburg's crew note names Stuyvesant Cove, which another tile owns: the tile build must take it from
	# the harbor's GTFS inventory instead of failing, and write only the stops the tile owns.
	from ferrydata import crew_notes, tiles
	fixture = json.loads((FIXTURES / 'south-williamsburg.json').read_text())
	monkeypatch.setattr(landings, '_features', lambda out, file: fixture[file])
	monkeypatch.setattr(landings.paths, 'processed_dir', lambda name: tmp_path)
	monkeypatch.setattr(landings.paths, 'ais_processed_dir', lambda: tmp_path)
	note = {'stop_id': '8', 'id': 'test-orientation', 'kind': 'face-orientation', 'statement': 'Ramps face the reference shore.', 'date': '2026-10-24',
		'source': 'crew-reported: project crew reviewer', 'face_toward_fixed_land_near_stop_id': 'REF', 'water_side_crew_reported': True}
	monkeypatch.setattr(crew_notes, 'load', lambda: ({'8': note}, '0' * 64))
	with pytest.raises(ValueError, match='reference stop REF is not available'):
		landings.build(SimpleNamespace(name='fixture', polygon=None), log=lambda message: None)
	reference = {'stop_id': 'REF', 'stop_name': 'Reference', 'north_m': 960., 'east_m': 3900.}
	monkeypatch.setattr(tiles, '_feed', lambda: None)
	monkeypatch.setattr(tiles, '_stop_inventory', lambda feed: ([reference], []))
	landings.build_tile(tiles.get_tile('tile_0_0'), log=lambda message: None)
	structures = geojsonio.read_collection(tmp_path / 'landing_structures.geojson')
	assert {f['properties']['stop_id'] for f in structures['features']} == {'8'}
	faces = geojsonio.read_collection(tmp_path / 'landing_faces.geojson')['features']
	assert faces[0]['properties']['crew_orientation']['opposite_shore_reference_stop_id'] == 'REF'


# UNSARE (coordinator-approved, solid-side probe only). Failure modes: unsurveyed water left out of the probe, the
# probe's water layer or LNAM not recorded, an UNSARE point under another structure or on land counted as water, and a
# primary face kept on the probe.
def _probe_scene(water_layer):
	land = box(-400, -400, 400, 0)
	water = box(-400, 0, 400, 400)
	props = {'id': 'WATER1', 'feature_class': water_layer}
	charted = landings.ChartedWater([] if water_layer == 'UNSARE' else [(water, props)], [(water, props)] if water_layer == 'UNSARE' else [])
	return land, charted


@pytest.mark.parametrize('water_layer', ['DEPARE', 'UNSARE'])
def test_the_solid_side_probe_is_kept_on_depare_or_unsare_and_records_the_layer(water_layer):
	land, charted = _probe_scene(water_layer)
	evidence = landings.solid_side_evidence((-10, 0), (10, 0), land, 'LNDARE', 'LAND1', charted, land, None)
	assert evidence['verified']
	assert evidence['probe_water_layer'] == water_layer and evidence['probe_water_lnam'] == 'WATER1'
	assert evidence['probe_in_charted_water']


def test_an_unsare_probe_under_another_structure_is_refused():
	land, charted = _probe_scene('UNSARE')
	pontoon = box(-20, .2, 20, 10)  # moored against the bulkhead, over unsurveyed water
	evidence = landings.solid_side_evidence((-10, 0), (10, 0), land, 'LNDARE', 'LAND1', charted, land, pontoon)
	assert not evidence['verified']
	assert evidence['probe_in_other_structure']
	assert evidence['probe_water_layer'] == 'UNSARE', 'the layer is recorded even when the probe is refused'


def test_an_unsare_probe_on_land_is_refused():
	land, charted = _probe_scene('UNSARE')
	wall = box(-50, 10, 50, 12)  # a dry structure whose south face looks onto land drawn 0.3 m away
	land = land.union(box(-30, 9.2, 30, 9.7))
	evidence = landings.solid_side_evidence((10, 10), (-10, 10), wall, 'SLCONS', 'WALL1', charted, land, None)
	assert evidence['winding_check'], 'walking west along the south face, the wall is on the right'
	assert evidence['probe_in_lndare']
	assert evidence['probe_water_layer'] == 'UNSARE', 'UNSARE drawn under the land does not make it water'
	assert not evidence['verified']


@pytest.mark.parametrize('primary, chart, crew, solid, expected', [
	(True, False, False, True, False),
	(True, False, True, False, True),
	(True, True, False, False, True),
	(False, False, False, True, True),
	(False, False, False, False, False),
])
def test_runtime_contact_never_keeps_a_primary_face_on_the_probe(primary, chart, crew, solid, expected):
	segment = {'water_side_verified_by_pipeline': chart, 'solid_side_evidence': {'verified': solid}}
	if primary:
		segment['primary_face'] = True
	if crew:
		segment['water_side_crew_reported'] = True
	assert landings.runtime_contact(segment) is expected
