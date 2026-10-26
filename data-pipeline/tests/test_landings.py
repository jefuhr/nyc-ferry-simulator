"""Failure modes: reversed normals, invented ENC outlines, landward faces and ambiguous matches."""
import math

import pytest
from shapely.geometry import LineString, Point, Polygon

from ferrydata import landings


def test_synthetic_barge_face_is_outer_long_edge_with_true_heading():
	barge = Polygon([(-14, 20), (14, 20), (14, 30), (-14, 30)])  # east, north
	face, outward = landings.berthing_face(barge, (1, 0))  # north, east
	assert face.equals(LineString([(-14, 30), (14, 30)]))
	assert face.length == pytest.approx(28)
	assert outward == pytest.approx((1, 0))
	assert landings.heading_deg(outward) == pytest.approx(0)
	assert landings.heading_deg((0, -1)) == pytest.approx(270)


def test_rotated_barge_normal_remains_perpendicular_to_face():
	from shapely.affinity import rotate
	barge = rotate(Polygon([(-14, 20), (14, 20), (14, 30), (-14, 30)]), -35, origin=(0, 0))
	face, outward = landings.berthing_face(barge, (math.cos(math.radians(35)), math.sin(math.radians(35))))
	assert landings.heading_deg(outward) == pytest.approx(35)
	a, b = face.coords
	assert (b[0] - a[0]) * outward[1] + (b[1] - a[1]) * outward[0] == pytest.approx(0, abs=1e-8)


def test_derived_barge_dimensions_and_gangway_are_explicit():
	barge = landings.derive_barge((0, 0), (1, 0), (1, 0), Point(0, 0))
	assert barge.area == pytest.approx(35 * 90 * .3048 ** 2)
	assert barge.bounds[1] == pytest.approx(85 * .3048)
	assert barge.bounds[3] == pytest.approx(120 * .3048)


def test_water_side_uses_charted_water_and_rejects_land_and_inward_normal():
	water = Polygon([(-100, 0), (100, 0), (100, 100), (-100, 100)])
	land = Polygon([(-100, -100), (100, -100), (100, 0), (-100, 0)])
	face = LineString([(-14, 30), (14, 30)])
	assert landings.face_on_water_side(face, (1, 0), (0, 0), water, land)
	assert not landings.face_on_water_side(face, (-1, 0), (0, 0), water, land)
	assert not landings.face_on_water_side(LineString([(-14, -30), (14, -30)]), (1, 0), (0, 0), water, land)


def test_ambiguous_enc_polygon_match_is_marked_for_review():
	stop = Point(0, 0)
	candidates = [(Polygon([(-15, 20), (-5, 20), (-5, 45), (-15, 45)]), {'id': 'a'}),
		(Polygon([(5, 20), (15, 20), (15, 45), (5, 45)]), {'id': 'b'})]
	_, _, ambiguous = landings.match_barge(stop, candidates)
	assert ambiguous


def test_face_uses_the_water_immediately_outside_an_enc_barge_hole():
	from shapely.geometry import box
	barge = box(-14, 20, 14, 30)
	water = box(-100, 0, 100, 100).difference(barge.buffer(.01))
	land = box(-100, -100, 100, 0)
	assert landings.face_on_water_side(LineString([(-14, 30), (14, 30)]), (1, 0), (0, 0), water, land)


def test_subdivided_enc_long_face_is_kept_whole_instead_of_short_end():
	barge = Polygon([(-5, 20), (5, 20), (5, 30), (5, 40), (5, 50), (-5, 50), (-5, 40), (-5, 30)])
	face, normal = landings.berthing_face(barge, (.9, .1))
	assert face.length == pytest.approx(30)
	assert normal == pytest.approx((0, 1))

# New failure modes: a nearer pontoon in an uncharted/landward pier pocket beats
# a directly charted water-facing berth; outline ordering reverses contact normals;
# separate sides lose their shared joints; a landward back edge closes an open berth.
def test_barge_selection_prefers_directly_charted_water_over_a_nearer_pier_pocket():
	from shapely.geometry import box
	near = box(-5, 5, 5, 30)
	outer = box(20, -10, 30, 18)
	water = box(30.1, -100, 200, 100)
	chosen, source, ambiguous = landings.match_barge(Point(0, 0), [(near, {'id': 'near'}), (outer, {'id': 'outer'})], water, box(-200, -200, -20, 200))
	assert chosen.equals(outer)
	assert source['id'] == 'outer'


@pytest.mark.parametrize('reverse', [False, True])
def test_water_facing_outline_is_one_open_connected_water_left_polyline(reverse):
	coords = [(-14, 20), (14, 20), (14, 30), (-14, 30)]
	barge = Polygon(coords[::-1] if reverse else coords)
	face, normal = landings.berthing_face(barge, (1, 0))
	line = landings.water_facing_outline(barge, face, normal)
	assert line.length == pytest.approx(48)
	assert not line.is_closed
	assert line.distance(face) < 1e-8
	assert landings.polyline_errors(line, barge) == []
	for a, b in zip(line.coords, list(line.coords)[1:]):
		mid = Point((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
		length = math.dist(a, b)
		left = ((a[1] - b[1]) / length, (b[0] - a[0]) / length)
		assert not barge.contains(Point(mid.x + left[0] * .1, mid.y + left[1] * .1))
	assert list(line.coords)[0][1] == pytest.approx(20)
	assert list(line.coords)[-1][1] == pytest.approx(20)


def test_polyline_validation_rejects_solid_on_left_zero_segments_and_off_outline():
	from shapely.geometry import box
	barge = box(-14, 20, 14, 30)
	assert landings.polyline_errors(LineString([(14, 30), (-14, 30)]), barge)
	assert landings.polyline_errors(LineString([(14, 30), (14, 30), (-14, 30)]), barge)
	assert landings.polyline_errors(LineString([(14, 35), (-14, 35)]), barge)


def test_subdivided_outline_joints_share_exact_source_vertices():
	barge = Polygon([(-5, 20), (5, 20), (5, 30), (5, 40), (5, 50), (-5, 50), (-5, 40), (-5, 30)])
	face, normal = landings.berthing_face(barge, (0, 1))
	line = landings.water_facing_outline(barge, face, normal)
	assert line.length == pytest.approx(50)
	assert landings.polyline_errors(line, barge) == []
	assert (5., 30.) in list(line.coords)
	assert (5., 40.) in list(line.coords)


def test_topology_fallback_accepts_only_a_clear_vessel_width_chart_gap():
	from shapely.geometry import box
	barge = box(-14, 20, 14, 30)
	face = LineString([(-14, 30), (14, 30)])
	water = box(-100, 48, 100, 100)
	proof = landings.water_side_evidence(face, (1, 0), (0, 0), water, box(-100, -100, 100, 0), [], barge)
	assert proof['verified']
	assert proof['method'] == 'topology-corridor-8x30m'
	assert proof['near_face_coverage_gap_m'] == pytest.approx(18, abs=.1)
	assert proof['corridor_width_m'] == 8


@pytest.mark.parametrize('obstacle', ['land', 'pier', 'pontoon', 'mooring'])
def test_topology_fallback_rejects_land_and_every_structure_even_off_centre(obstacle):
	from shapely.geometry import box
	barge = box(-14, 20, 14, 30)
	face = LineString([(-14, 30), (14, 30)])
	water = box(-100, 48, 100, 100)
	obstruction = box(2, 39, 3, 42)  # misses the centre ray, intersects the 8 m corridor
	land = box(-100, -100, 100, 0)
	structures = []
	if obstacle == 'land':
		land = land.union(obstruction)
	else:
		structures = [obstruction]
	proof = landings.water_side_evidence(face, (1, 0), (0, 0), water, land, structures, barge)
	assert not proof['verified']


def test_topology_fallback_requires_the_entire_far_end_in_charted_water():
	from shapely.geometry import box
	proof = landings.water_side_evidence(LineString([(-14, 30), (14, 30)]), (1, 0), (0, 0), box(-2, 48, 2, 100), box(-100, -100, 100, 0), [], box(-14, 20, 14, 30))
	assert not proof['verified']


def test_candidate_choice_uses_ais_support_before_chart_convenience_but_requires_valid_water():
	middle = {'enc_id': 'middle', 'ais_slow_reports_within_10m': 4019, 'distance_to_gtfs_m': 22.4, 'water_side_evidence': {'verified': True, 'method': 'topology-corridor-8x30m'}}
	north = {'enc_id': 'north', 'ais_slow_reports_within_10m': 3594, 'distance_to_gtfs_m': 20.8, 'water_side_evidence': {'verified': True, 'method': 'direct-12m'}}
	south = {'enc_id': 'south', 'ais_slow_reports_within_10m': 1234, 'distance_to_gtfs_m': 21.1, 'water_side_evidence': {'verified': True, 'method': 'direct-12m'}}
	assert landings.choose_supported_candidate([south, north, middle])['enc_id'] == 'middle'
	middle['water_side_evidence']['verified'] = False
	assert landings.choose_supported_candidate([south, north, middle])['enc_id'] == 'north'
	north['water_side_evidence']['verified'] = False
	assert landings.choose_supported_candidate([south, north, middle])['enc_id'] == 'south'


def test_ais_assignment_priority_is_scoped_to_reviewed_terminal():
	middle = {'enc_id': 'middle', 'ais_slow_reports_within_10m': 4019, 'distance_to_gtfs_m': 22.4, 'water_side_evidence': {'verified': True}}
	south = {'enc_id': 'south', 'ais_slow_reports_within_10m': 1234, 'distance_to_gtfs_m': 21.1, 'water_side_evidence': {'verified': True}}
	assert landings.choose_candidate_for_stop('87', [south, middle], 'south')['enc_id'] == 'middle'
	assert landings.choose_candidate_for_stop('17', [south, middle], 'south')['enc_id'] == 'south'


def test_crew_orientation_selects_manhattan_side_and_preserves_chart_failure():
	from shapely.geometry import box
	note = {'stop_id': '19', 'water_side_crew_reported': True, 'statement': 'Ramps face Manhattan', 'date': '2026-10-24', 'source': 'crew-reported: project crew reviewer'}
	barge = box(-14, 20, 14, 30)
	face, normal, proof = landings.crew_reported_face(barge, (1, 0), (0, 0), box(-100, 80, 100, 100), box(-100, -100, 100, 0), [box(-10, 31, 10, 60)], note)
	assert face.centroid.y == pytest.approx(30)
	assert normal == pytest.approx((1, 0))
	assert proof['method'] == 'crew-reported'
	assert proof['verified']
	assert not proof['chart_verified']
	assert proof['crew_note']['statement'] == note['statement']


def test_crew_notes_are_loaded_from_tracked_manifest_with_provenance():
	from ferrydata import crew_notes
	notes, checksum = crew_notes.load()
	assert notes['19']['source'] == 'crew-reported: project crew reviewer'
	assert notes['19']['date'] == '2026-10-24'
	assert notes['19']['water_side_crew_reported']
	assert notes['87']['default_berth_id'] == 'enc:02261FA117EFB198'
	assert len(checksum) == 64


@pytest.mark.parametrize('watlev, expected', [(3, True), (2, False), (None, False)])
def test_submerged_pier_is_a_depth_hazard_but_dry_or_unknown_structure_blocks(watlev, expected):
	from shapely.geometry import box
	face = LineString([(-14, 30), (14, 30)])
	shore = [(box(2, 39, 3, 42), {'id': 'pier', 'feature_class': 'SLCONS', 'category_code': 4, 'water_level_effect_code': watlev, 'source_date': '20220302', 'cell': 'fixture'})]
	blockers = landings.surface_structures(shore)
	proof = landings.water_side_evidence(face, (1, 0), (0, 0), box(-100, 48, 100, 100), box(-100, -100, 100, 0), blockers, box(-14, 20, 14, 30))
	assert proof['verified'] is expected
	hazards = landings.submerged_hazards(face, (1, 0), shore)
	assert len(hazards) == (1 if watlev == 3 else 0)
	if hazards:
		assert hazards[0]['enc_id'] == 'pier'
		assert hazards[0]['CATSLC'] == 4
		assert hazards[0]['SORDAT'] == '20220302'
		assert hazards[0]['corridor_overlap_m2'] == pytest.approx(3)


# Review failure modes: primary proof leaking to a perpendicular ENC end; crew
# policy upgrading geometry confidence; a short initial face or a charted 12 m
# point bypassing fallback eligibility; dry/unknown walls between direct probes.
def test_north_williamsburg_supported_primary_cannot_verify_end_787614137():
	import json
	from pathlib import Path
	from shapely.geometry import shape
	fixture = json.loads((Path(__file__).parent / 'fixtures/north-williamsburg-end.json').read_text())
	shore = [(shape(s['geometry']), s['properties']) for s in fixture['structures']]
	proofs = landings.contact_segment_evidence(shape(fixture['contact']), shape(fixture['face']),
		fixture['primary_proof'], fixture['anchor'], shape(fixture['water']), shape(fixture['land']),
		landings.surface_structures(shore, fixture['own_id']), shape(fixture['barge']), True)
	assert len(proofs) == 5
	assert [p['index'] for p in proofs if p['on_primary_face']] == [1, 2, 3]
	assert all(p['water_side_verified_by_pipeline'] and p['water_side_crew_reported'] for p in proofs[1:4])
	assert not proofs[4]['water_side_verified_by_pipeline']
	assert not proofs[4].get('water_side_crew_reported', False)
	assert not proofs[4]['water_side_evidence']['verified']


@pytest.mark.parametrize('note, expected', [
	({'id': 'north-orientation', 'kind': 'face-orientation'}, 'medium'),
	({'id': 'pier11-use', 'kind': 'berth-use-policy'}, 'low'),
])
def test_crew_provenance_stays_separate_from_ordinal_geometry_confidence(note, expected):
	proof = {'verified': True, 'method': 'crew-reported', 'chart_verified': True,
		'chart_method': 'topology-corridor-8x30m'} if note['kind'] == 'face-orientation' else {'verified': True, 'method': 'direct-12m'}
	assert landings.face_confidence(False, True, proof, note) == expected
	assert landings.face_confidence(False, True, proof, None) == 'low'


@pytest.mark.parametrize('path', ['initial', 'alternate'])
def test_four_metre_face_cannot_claim_eight_metre_fallback(path):
	from shapely.geometry import box
	barge = box(-2, 24, 2, 30)
	water, land = box(-100, 48, 100, 100), box(-100, -100, 100, 0)
	if path == 'initial':
		proof = landings.select_face(barge, (1, 0), (0, 0), water, land)[2]
	else:
		proof = landings.water_side_evidence(LineString([(-2, 30), (2, 30)]), (1, 0), (0, 0), water, land, [], barge)
	assert not proof['verified']


def test_charted_twelve_metre_point_with_missing_half_metre_line_cannot_use_fallback():
	from shapely.geometry import box
	proof = landings.water_side_evidence(LineString([(-14, 30), (14, 30)]), (1, 0), (0, 0),
		box(-100, 40, 100, 100), box(-100, -100, 100, 0), [], box(-14, 20, 14, 30))
	assert not proof['verified']


@pytest.mark.parametrize('watlev', [2, None, 3])
def test_direct_across_wall_uses_the_same_watlev_blockers_as_corridor(watlev):
	from shapely.geometry import box
	wall = (box(-5, 35, 5, 37), {'id': 'wall', 'water_level_effect_code': watlev})
	proof = landings.water_side_evidence(LineString([(-14, 30), (14, 30)]), (1, 0), (0, 0),
		box(-100, 30.1, 100, 100), box(-100, -100, 100, 0), landings.surface_structures([wall]), box(-14, 20, 14, 30))
	assert proof['verified'] is (watlev == 3)


def test_direct_across_land_between_half_and_twelve_metres_is_rejected():
	from shapely.geometry import box
	proof = landings.water_side_evidence(LineString([(-14, 30), (14, 30)]), (1, 0), (0, 0),
		box(-100, 30.1, 100, 100), box(-100, -100, 100, 0).union(box(2, 35, 3, 37)), [], box(-14, 20, 14, 30))
	assert not proof['verified']


def test_south_williamsburg_diagnosis_does_not_call_new_end_the_old_long_face():
	from ferrydata import landing_audit
	props = {'stop_id': '8', 'face_heading_deg_true': 215.01797236630065}
	evidence = {'sampled_cells': 100, 'shallow_cells': 100, 'min_depth_m': -.076}
	text = landing_audit.explanation(props, evidence, 2.1)
	assert 'outer long side' not in text
	assert '215.018' in text
	assert 'crew verification' in text


# Crew override failure modes: missing note, short-end selection, inherited crew
# support on an end, a chart failure laundered into a pass, or confidence drift.
def test_south_williamsburg_crew_note_selects_nw_without_clearing_morfac(monkeypatch, tmp_path):
	import json
	from pathlib import Path
	from types import SimpleNamespace
	from shapely.geometry import shape
	from ferrydata import crew_notes, geojsonio, validate
	fixture = json.loads((Path(__file__).parent / 'fixtures/south-williamsburg.json').read_text())
	notes, checksum = crew_notes.load()
	note = notes['8']
	assert note['id'] == 'south-williamsburg-face-orientation-2026-10-24'
	assert note['date'] == '2026-10-24'
	assert note['source'] == 'crew-reported: project crew reviewer'
	assert note['kind'] == 'face-orientation'
	monkeypatch.setattr(landings, '_features', lambda out, name: fixture[name])
	monkeypatch.setattr(landings.paths, 'processed_dir', lambda name: tmp_path)
	monkeypatch.setattr(landings.paths, 'ais_processed_dir', lambda: tmp_path)
	landings.build(SimpleNamespace(name='fixture'), log=lambda message: None)
	feature = geojsonio.read_collection(tmp_path / 'landing_faces.geojson')['features'][0]
	p = feature['properties']
	assert p['crew_notes_sha256'] == checksum
	assert p['crew_evidence'] == {'note_id': note['id'], 'kind': 'face-orientation'}
	assert p['confidence'] == 'medium'
	assert p['face_heading_deg_true'] == pytest.approx(305.042, abs=.001)
	assert landings.to_local(shape(p['face_geometry'])).length == pytest.approx(25.590, abs=.001)
	assert p['water_side_crew_reported']
	assert not p['water_side_verified_by_pipeline']
	assert not p['water_side_evidence']['chart_verified']
	records = p['contact_segment_evidence']
	assert len(records) == 2
	assert [r['index'] for r in records if r['on_primary_face']] == [1]
	assert records[1]['water_side_crew_reported']
	assert not records[1]['water_side_verified_by_pipeline']
	assert records[0]['water_side_verified_by_pipeline']
	assert records[0]['water_side_evidence']['method'] == 'direct-12m'
	assert not records[0].get('water_side_crew_reported', False)
	assert validate.filtered_contact_topology(landings.to_local(shape(p['contact_geometry'])), records)['primary_retained']
	# Independently re-run validation against the fixture, including current note hash.
	monkeypatch.setattr(validate, '_load_json', lambda name, file: {'features': [feature]} if file == 'landing_faces.geojson' else {'features': fixture[file]})
	(tmp_path / 'shoreline_constructions.geojson').write_text('{}')
	result = validate.check_shoreline(SimpleNamespace(name='fixture'))
	assert result.passed
	assert 'Independent chart: FAIL' in '\n'.join(result.details)
	assert 'outranks the chart check for orientation' in '\n'.join(result.details)


def test_morfac_conflict_discloses_raw_attributes_and_lateral_position():
	from ferrydata import landing_audit
	face = LineString([(-14, 30), (14, 30)])
	attrs = [{'LNAM': '022646E95F81764B', 'CATMOR': 1, 'WATLEV': 2, 'SORDAT': '20220722', 'SORIND': 'US,US,graph,GC-11912', 'cell': 'US5NYCDG', 'INFORM': None}]
	props = {'id': attrs[0]['LNAM'], 'feature_class': 'MORFAC', 'water_level_effect_code': 2}
	records = landing_audit.surface_conflicts(face, (1, 0), [(Point(10, 41.16), props)], {'MORFAC': attrs})
	assert len(records) == 1
	r = records[0]
	assert r['attributes'] == attrs
	assert r['distance_to_face_m'] == pytest.approx(11.16)
	assert r['along_face_from_midpoint_m'] == pytest.approx(10.)
	assert r['outward_from_face_midpoint_m'] == pytest.approx(11.16)
	assert not r['on_centre_line']
	assert r['geometry_wgs84']['type'] == 'Point'
	assert 'role remains unverified' in r['interpretation']
	# Outside the full face sweep, submerged, and own outline do not become conflicts.
	assert not landing_audit.surface_conflicts(face, (1, 0), [(Point(15, 41), props)], {'MORFAC': attrs})
	assert not landing_audit.surface_conflicts(face, (1, 0), [(Point(10, 41), {**props, 'water_level_effect_code': 3})], {'MORFAC': attrs})


# History is retained, but a superseded modelling decision must never remain active.
def test_crew_notes_supersedes_replaces_old_fields_and_rejects_unrelated_duplicates(tmp_path, monkeypatch):
	import json
	from ferrydata import crew_notes
	monkeypatch.setattr(crew_notes.paths, 'MANIFEST_DIR', tmp_path)
	old = dict(stop_id='87', id='old', statement='old', source='crew', date='2026-10-24', modelled_enc_id='old-pontoon')
	new = dict(stop_id='87', id='new', statement='new', source='crew', date='2026-10-25', supersedes='old', default_berth_id='enc:new')
	file = tmp_path / 'crew-landing-notes.json'
	file.write_text(json.dumps({'notes':[old, new]}))
	active, _ = crew_notes.load()
	assert active['87'] == new and 'modelled_enc_id' not in active['87']
	new['supersedes'] = 'someone-else'
	file.write_text(json.dumps({'notes':[old, new]}))
	with pytest.raises(ValueError, match='Duplicate'):
		crew_notes.load()
