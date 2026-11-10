"""Survey failure modes: inert empty input, provenance/unit/frame mistakes, wrong
physical outline, face/identity drift, disabled faces, and changes leaking to peers.
The byte-parity fixture is a compact two-stop chart, not the full harbor.
"""
import json
import math
from types import SimpleNamespace

import pytest
from shapely.geometry import Point, box, shape

from ferrydata import geojsonio, geodesy, landings, paths
from ferrydata.export_unity import contact_records
from ferrydata.harbor_runtime.build import landings as runtime_landings


def value(number, unit='m'):
	return dict(value=number, unit=unit, source='synthetic measurement', method='published', confidence='high')


def position(north, east):
	return value(list(map(float, geodesy.from_local(north, east))), 'deg')


def barge(berth_id='derived-barge:s1:land', **extras):
	return dict(berth_id=berth_id, centre=position(2, 38), heading=value(25, 'deg'),
		length=value(32), width=value(12), berths=[dict(name='Review slip', face_bearing=value(115, 'deg'),
		landed_heading=value(295, 'deg'))], **extras)


def document(*barges):
	return dict(schema_version=1, purpose='Synthetic fixture only', stops={'s1': dict(barges=list(barges))} if barges else {})


@pytest.fixture
def chart(tmp_path, monkeypatch):
	root = tmp_path / 'processed'; out = root / 'fixture'; out.mkdir(parents=True)
	manifests = tmp_path / 'manifests'; manifests.mkdir()
	(manifests / 'crew-landing-notes.json').write_text('{"notes":[]}\n')
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', root)
	monkeypatch.setattr(paths, 'MANIFEST_DIR', manifests)
	monkeypatch.setattr(landings, '_slow_reports', lambda: None)
	monkeypatch.setattr(landings, '_unsurveyed', lambda _: [])
	def write(name, geometries):
		geojsonio.write_collection(out / name, [geojsonio.make_feature(landings.to_wgs84(g), p) for g, p in geometries], name)
	write('landings.geojson', [(Point(30, n), dict(stop_id=s, stop_name=s, route_ids=[])) for s, n in [('s1', 0), ('s2', 700)]])
	write('land_areas.geojson', [(box(-400, -200, 0, 200), dict(id='land')), (box(-400, 500, 0, 900), dict(id='land2'))])
	write('depth_areas.geojson', [(box(0, -400, 400, 1100), dict(id='water', min_depth_m=10))])
	write('shoreline_constructions.geojson', [])
	write('routes.geojson', [])
	return SimpleNamespace(slice=SimpleNamespace(name='fixture', polygon=None), out=out, manifests=manifests, write=write)


def run(chart, payload=None):
	file = chart.manifests / 'landing-survey.json'
	if payload is not None:
		file.write_text(json.dumps(payload))
	landings.build(chart.slice, log=lambda _: None)
	faces = geojsonio.read_collection(chart.out / 'landing_faces.geojson')['features']
	structures = geojsonio.read_collection(chart.out / 'landing_structures.geojson')
	contacts = contact_records(structures)
	index = dict(stops=[dict(stop_id='s1'), dict(stop_id='s2')])
	runtime = runtime_landings(index, structures['features'], contacts)
	return faces, structures, contacts, runtime


def by_stop(records, stop='s1'):
	return [r for r in records if r.get('properties', r)['stop_id'] == stop]


def test_empty_survey_is_byte_identical_on_compact_two_stop_chart(chart):
	one = run(chart)
	before = {p.name: p.read_bytes() for p in chart.out.iterdir()}
	two = run(chart, document())
	assert before == {p.name: p.read_bytes() for p in chart.out.iterdir()}
	assert json.dumps(one, sort_keys=True) == json.dumps(two, sort_keys=True)
	assert json.loads((chart.out / 'landing-survey-changes.json').read_text())['stops'] == []


def test_surveyed_derived_pose_face_alias_and_runtime_fields(chart):
	measurement = barge(gangways=[dict(shore_hinge=position(0, 0), barge_hinge=position(1, 33), width=value(2), shore_height=value(3))],
		piles=[dict(centre=position(5, 42), diameter=value(.8), top_height=value(6))],
		canopy=dict(centre=position(2, 38), length=value(12), width=value(5), eave_height=value(3)))
	payload = document(measurement)
	payload['stops']['s1']['shore'] = [dict(kind='platform', outline=dict(points=[v['value'] for v in [position(-5, -4), position(5, -4), position(5, 0), position(-5, 0), position(-5, -4)]],
		source='synthetic measurement', method='published', confidence='high'), deck_height=value(3))]
	faces, structures, contacts, runtime = run(chart, payload)
	f = by_stop(faces)[0]; p = f['properties']; polygon = landings.to_local(shape(f['geometry']))
	assert [polygon.centroid.y, polygon.centroid.x] == pytest.approx([2, 38], abs=.02)
	assert polygon.area == pytest.approx(32 * 12, abs=.4)
	edges = landings._straight_edges(polygon)
	assert sorted(round(e.length, 1) for e in edges) == [12, 12, 32, 32]
	assert p['face_heading_deg_true'] == pytest.approx(115)
	assert p['outline_id'] == measurement['berth_id']
	assert p['source'] == 'surveyed' and p['outline_source'] == 'surveyed'
	assert p['confidence'] == 'high'
	assert p['survey']['centre']['source'] == 'synthetic measurement'
	for c in by_stop(contacts):
		if c['source'] == 'surveyed':
			assert c['solid_side_evidence']['method'] == 'surveyed-outline'
			assert c['solid_side_evidence']['outline_survey']['centre']['source'] == 'synthetic measurement'
	assert p['berths'][0]['alias'] == 'Review slip'
	b = next(b for b in runtime['barges'] if b['id'] == measurement['berth_id'])
	assert b['gangways'][0]['barge_hinge_ne_m'] == pytest.approx([1, 33], abs=.001)
	assert b['piles'][0]['centre_ne_m'] == pytest.approx([5, 42], abs=.001)
	assert b['canopy']['heading_rad'] == pytest.approx(math.radians(25))
	assert b['shore'][0]['deck_height_m'] == 3
	assert runtime['stops'][0]['berths'][0]['landed_heading'] == pytest.approx(math.radians(295))
	assert runtime['stops'][0]['berths'][0]['alias'] == 'Review slip'
	assert 'gangways' not in runtime['barges'][1]


def test_surveyed_chart_pose_replaces_only_the_named_pontoon(chart):
	chart.write('shoreline_constructions.geojson', [(box(25, -12, 35, 12), dict(id='one', feature_class='PONTON')),
		(box(25, 28, 35, 52), dict(id='two', feature_class='PONTON'))])
	old = run(chart)
	new = run(chart, document(barge('enc:one')))
	old_barges = {b['id']: b for b in old[3]['barges']}; new_barges = {b['id']: b for b in new[3]['barges']}
	assert old_barges['enc:one']['geometry_ne_m'] != new_barges['enc:one']['geometry_ne_m']
	assert old_barges['enc:two'] == new_barges['enc:two']
	assert new[0][0]['properties']['outline_id'] == 'enc:one'


def test_disabled_face_keeps_structure_and_no_selectable_berths(chart):
	old = run(chart)
	measurement = dict(berth_id='derived-barge:s1:land', berths=[dict(face_bearing=value(90, 'deg'), is_ferry_berth=False)])
	new = run(chart, document(measurement))
	assert new[0][0]['geometry'] == old[0][0]['geometry']
	assert new[0][0]['properties']['berths'] == []
	assert not any(c.get('berth_face') or c.get('primary_face') for c in by_stop(new[2]))
	assert [(c['id'], c['start_ne_m'], c['end_ne_m']) for c in by_stop(new[2])] == [(c['id'], c['start_ne_m'], c['end_ne_m']) for c in by_stop(old[2])]
	assert new[3]['stops'][0]['berths'] == []
	assert [s['stop_id'] for s in json.loads((chart.out / 'landing-survey-changes.json').read_text())['stops']] == ['s1']


def test_one_surveyed_stop_does_not_change_peer_records_and_reports_geometry(chart):
	old = run(chart)
	new = run(chart, document(barge()))
	for a, b in zip(old[:3], new[:3]):
		left = a['features'] if isinstance(a, dict) else a
		right = b['features'] if isinstance(b, dict) else b
		assert by_stop(left, 's2') == by_stop(right, 's2')
	assert old[3]['stops'][1] == new[3]['stops'][1]
	assert old[3]['barges'][1] == new[3]['barges'][1]
	changes = json.loads((chart.out / 'landing-survey-changes.json').read_text())['stops']
	assert [c['stop_id'] for c in changes] == ['s1']
	assert changes[0]['barges'][0]['after']['centre_ne_m'] == pytest.approx([2, 38], abs=.001)
	assert changes[0]['barges'][0]['after']['heading_rad'] == pytest.approx(math.radians(25), abs=1e-6)
	assert changes[0]['barges'][0]['after']['face_bearings_rad'] == pytest.approx([math.radians(115)])


def test_multiple_faces_keep_identity_and_existing_orders(chart):
	old = run(chart)
	measurement = barge()
	measurement['berths'].append(dict(name='Second slip', face_bearing=value(25, 'deg')))
	new = run(chart, document(measurement))
	berths = new[0][0]['properties']['berths']
	assert [b['id'] for b in berths] == ['derived-barge:s1:land'] * 2
	assert [b['order'] for b in berths] == [old[0][0]['properties']['berths'][0]['order'], 1]
	assert [b['berth_order'] for b in new[3]['stops'][0]['berths']] == [0, 1]
	assert all(len(b['berth_face_segment_ids']) for b in new[3]['stops'][0]['berths'])


@pytest.mark.parametrize('mutation,match', [
	(lambda b: b['centre'].pop('source'), 'source'),
	(lambda b: b['length'].pop('method'), 'method'),
	(lambda b: b['width'].pop('confidence'), 'confidence'),
	(lambda b: b['heading'].update(value=180), 'heading'),
	(lambda b: b['centre'].update(value=[-74, 40.7]), 'harbor'),
	(lambda b: b['length'].update(value=-1), 'length'),
	(lambda b: b['width'].update(unit='deg'), 'unit'),
	(lambda b: b.pop('width'), 'pose'),
])
def test_invalid_files_are_refused(tmp_path, mutation, match):
	from ferrydata import survey
	measurement = barge(); mutation(measurement)
	file = tmp_path / 'survey.json'; file.write_text(json.dumps(document(measurement)))
	with pytest.raises(ValueError, match=match):
		survey.load(file)


def test_unsurveyed_runtime_fields_are_absent(chart):
	runtime = run(chart)[3]
	for b in runtime['barges']:
		assert not {'gangways', 'piles', 'canopy', 'shore'} & b.keys()
	for s in runtime['stops']:
		assert all('landed_heading' not in b for b in s['berths'])


def test_existing_single_face_order_helper_stays_identical():
	polylines = [dict(stop_id='s', segments=[dict(id=2, berth_face=True, berth_id='b', berth_order=0, _berth_start=[1, 0], _berth_end=[2, 0]),
		dict(id=1, berth_face=True, berth_id='b', berth_order=0, _berth_start=[0, 0], _berth_end=[1, 0])])]
	assert [s['id'] for s in landings.ordered_berth_segments(polylines, 's', 'b')] == [1, 2]
	assert [s['id'] for s in landings.ordered_berth_segments(polylines, 's', 'b', 0)] == [1, 2]


def test_overlay_from_compact_fixture_has_scale_and_both_outlines(chart, tmp_path):
	from ferrydata import survey
	run(chart, document(barge(piles=[dict(centre=position(5, 42))], gangways=[dict(shore_hinge=position(0, 0), barge_hinge=position(2, 33))])))
	out = tmp_path / 'overlay.png'
	assert survey.main(['overlay', '--stop', 's1', '--processed', str(chart.out), '--out', str(out)]) == 0
	assert out.read_bytes().startswith(b'\x89PNG\r\n\x1a\n')
	assert out.stat().st_size > 1000


def test_disabled_survey_has_named_passing_warning_and_empty_survey_has_none(chart):
	from ferrydata import validate
	run(chart)
	assert validate.survey_warnings(chart.slice.name) == []
	run(chart, document(dict(berth_id='derived-barge:s1:land', berths=[dict(face_bearing=value(90, 'deg'), is_ferry_berth=False)])))
	warnings = validate.survey_warnings(chart.slice.name)
	assert len(warnings) == 1
	assert warnings[0].passed
	assert 'WARNING' in warnings[0].name and 's1' in warnings[0].summary
	faces = geojsonio.read_collection(chart.out / 'landing_faces.geojson')
	structures = geojsonio.read_collection(chart.out / 'landing_structures.geojson')
	assert validate.structure_errors(structures, {f['properties']['stop_id']: f['properties'] for f in faces['features']}) == []


def test_unknown_survey_identity_is_refused(chart):
	with pytest.raises(ValueError, match='unknown.*berth'):
		run(chart, document(barge('enc:missing')))


def test_metadata_only_survey_does_not_report_contact_geometry_change(chart):
	old = run(chart)
	new = run(chart, document(dict(berth_id='derived-barge:s1:land', piles=[dict(centre=position(5, 42))])))
	assert old[0][0]['geometry'] == new[0][0]['geometry']
	assert old[2] == new[2]
	assert old[0][0]['properties']['water_side_evidence'] == new[0][0]['properties']['water_side_evidence']
	assert json.loads((chart.out / 'landing-survey-changes.json').read_text())['stops'] == []
	assert [(r['id'], r['start_ne_m'], r['end_ne_m']) for r in old[2]] == [(r['id'], r['start_ne_m'], r['end_ne_m']) for r in new[2]]


def test_secondary_charted_pose_keeps_primary_outline_and_assigns_next_order(chart):
	chart.write('shoreline_constructions.geojson', [(box(25, -12, 35, 12), dict(id='one', feature_class='PONTON')),
		(box(25, 28, 35, 52), dict(id='two', feature_class='PONTON'))])
	old = run(chart)
	measurement = barge('enc:two'); measurement['centre'] = position(40, 55)
	new = run(chart, document(measurement))
	assert old[0][0]['geometry'] == new[0][0]['geometry']
	assert new[0][0]['properties']['outline_id'] == 'enc:one'
	assert [(b['id'], b['order']) for b in new[0][0]['properties']['berths']] == [('enc:one', 0), ('enc:two', 1)]
	b = next(b for b in new[3]['barges'] if b['id'] == 'enc:two')
	assert all(p['source'] == 'surveyed' for p in b['provenance'])
	assert b['provenance'][0]['survey']['centre']['source'] == 'synthetic measurement'
	assert [c['stop_id'] for c in json.loads((chart.out / 'landing-survey-changes.json').read_text())['stops']] == ['s1']


def test_partial_face_removal_cannot_mark_the_whole_stop_disabled(chart):
	chart.write('shoreline_constructions.geojson', [(box(25, -12, 35, 12), dict(id='one', feature_class='PONTON')),
		(box(25, 28, 35, 52), dict(id='two', feature_class='PONTON'))])
	measurement = dict(berth_id='enc:two', berths=[dict(face_bearing=value(90, 'deg'), is_ferry_berth=False)])
	new = run(chart, document(measurement))
	assert len(new[3]['stops'][0]['berths']) == 1
	assert not any(f['properties'].get('survey_no_berths') for f in by_stop(new[1]['features']))


def test_changes_report_is_repeatable_and_clears_only_rebuilt_tile_stops(tmp_path):
	from ferrydata import survey
	a = tmp_path / 'harbor/tiles/a'; b = a.parent / 'b'
	a.mkdir(parents=True); b.mkdir()
	ca = dict(stop_id='a', barges=[]); cb = dict(stop_id='b', barges=[])
	survey.write_changes(a, [ca], ['a']); survey.write_changes(b, [cb], ['b'])
	file = a.parent.parent / 'landing-survey-changes.json'
	before = file.read_bytes()
	survey.write_changes(a, [ca], ['a'])
	assert file.read_bytes() == before
	survey.write_changes(a, [], ['a'])
	assert json.loads(file.read_text())['stops'] == [cb]


def test_moved_neighbour_cannot_leave_stale_primary_water_side_proof(chart):
	chart.write('shoreline_constructions.geojson', [(box(25, -12, 35, 12), dict(id='one', feature_class='PONTON')),
		(box(25, 28, 35, 52), dict(id='two', feature_class='PONTON'))])
	old = run(chart)
	measurement = barge('enc:two'); measurement['centre'] = position(0, 48); measurement['heading'] = value(0, 'deg')
	measurement['berths'][0]['face_bearing'] = value(90, 'deg')
	new = run(chart, document(measurement))
	assert old[0][0]['properties']['water_side_verified_by_pipeline']
	assert old[0][0]['geometry'] == new[0][0]['geometry']
	assert not new[0][0]['properties']['water_side_verified_by_pipeline']
	assert all(not c['water_side_verified_by_pipeline'] for c in by_stop(new[2]) if c.get('primary_face'))
