import hashlib
import json
import math

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin

from ferrydata.export_unity import export_grid


def test_export_is_little_endian_lossless_and_deterministic(tmp_path):
	source = tmp_path / 'depth_merged_navd88.tif'
	values = np.array([[1.25, -9999], [-12.5, 0]], dtype=np.float32)
	with rasterio.open(source, 'w', driver='GTiff', width=2, height=2, count=1,
		dtype='float32', crs='EPSG:4326', transform=from_origin(-74, 41, .001, .002), nodata=-9999) as ds:
		ds.write(values, 1)
	dest = tmp_path / 'unity'
	export_grid(source, dest)
	first = {p.name: p.read_bytes() for p in dest.iterdir()}
	export_grid(source, dest)
	assert first == {p.name: p.read_bytes() for p in dest.iterdir()}
	assert np.array_equal(np.frombuffer(first['depth.f32'], dtype='<f4').reshape(2, 2), values)
	header = json.loads(first['depth.json'])
	assert header['source_sha256'] == hashlib.sha256(source.read_bytes()).hexdigest()
	assert header['datum'] == 'NAVD88'
	assert header['crs'] == 'EPSG:4326'
	assert header['origin'] == [-74, 41]
	assert header['cell_size'] == [.001, -.002]
	assert header['nodata'] == -9999


def test_projected_input_rejected_instead_of_misplaced(tmp_path):
	source = tmp_path / 'projected.tif'
	with rasterio.open(source, 'w', driver='GTiff', width=2, height=2, count=1,
		dtype='float32', crs='EPSG:32618', transform=from_origin(500000, 4500000, 3, 3)) as ds:
		ds.write(np.ones((2, 2), dtype=np.float32), 1)
	with pytest.raises(ValueError, match='EPSG:4326'):
		export_grid(source, tmp_path / 'unity')


def test_nad83_cudem_is_converted_to_wgs84(tmp_path):
	source = tmp_path / 'nad83.tif'
	with rasterio.open(source, 'w', driver='GTiff', width=2, height=2, count=1,
		dtype='float32', crs='EPSG:4269', transform=from_origin(-74, 41, .001, .002), nodata=-9999) as ds:
		ds.write(np.ones((2, 2), dtype=np.float32), 1)
	header = export_grid(source, tmp_path / 'unity')
	assert header['crs'] == 'EPSG:4326'
	assert header['source_crs'] == 'EPSG:4269'
	assert np.all(np.fromfile(tmp_path / 'unity/depth.f32', dtype='<f4') == 1)


# contacts.json version 2. Failure modes: a root that is no longer a bare array (a version 1 reader refuses
# it), a version 1 field missing or retyped on a new segment, the existing exposed runs reordered or
# re-identified, a water normal on the solid side, broken joints inside a polyline, duplicate ids, and a
# primary flag on anything but the selected face.
def _contacts(monkeypatch, tmp_path, fixture='south-williamsburg.json'):
	from types import SimpleNamespace
	from ferrydata import export_unity, landings
	data = json.loads((__import__('pathlib').Path(__file__).parent / 'fixtures' / fixture).read_text())
	monkeypatch.setattr(landings, '_features', lambda out, name: data[name])
	monkeypatch.setattr(landings.paths, 'processed_dir', lambda name: tmp_path)
	monkeypatch.setattr(landings.paths, 'ais_processed_dir', lambda: tmp_path)
	landings.build(SimpleNamespace(name='fixture', polygon=None), log=lambda message: None)
	count = export_unity.export_contacts(tmp_path / 'landing_structures.geojson', tmp_path / 'unity' / 'contacts.json')
	records = json.loads((tmp_path / 'unity' / 'contacts.json').read_text())
	assert count == len(records)
	return records


def _version1_reader(records):
	"""The Slice loader's version 1 contract, independently: required fields and the chart-or-crew gate."""
	required = ('id', 'stop_id', 'stop_name', 'start_ne_m', 'end_ne_m', 'water_normal_ne', 'source', 'confidence', 'verification')
	assert isinstance(records, list)
	kept = []
	for record in records:
		assert all(field in record for field in required), record
		assert isinstance(record['id'], int) and 1 <= record['id'] <= 2 ** 31 - 1
		for field in ('stop_id', 'source', 'confidence', 'verification'):
			assert isinstance(record[field], str) and record[field]
		for flag in ('water_side_verified_by_pipeline', 'water_side_crew_reported', 'primary_face'):
			assert record.get(flag) in (None, True, False)
		if record.get('water_side_verified_by_pipeline') or record.get('water_side_crew_reported'):
			kept.append(record['id'])
	return kept


def test_contacts_v2_is_a_bare_array_a_version_1_reader_loads(monkeypatch, tmp_path):
	records = _contacts(monkeypatch, tmp_path)
	kept = _version1_reader(records)
	assert 149111046 in kept and 216221522 in kept
	assert len({r['id'] for r in records}) == len(records)
	for record in records:
		(sn, se), (en, ee) = record['start_ne_m'], record['end_ne_m']
		length = math.hypot(en - sn, ee - se)
		assert record['water_normal_ne'] == pytest.approx([(ee - se) / length, -(en - sn) / length]), 'water on the left of start to end'
		assert record['kind'] in ('berth-face', 'barge-side', 'barge-end', 'pier', 'bulkhead', 'shoreline')
		assert len(record['start_lonlat']) == 2 and len(record['end_lonlat']) == 2


def test_contacts_v2_keeps_the_existing_exposed_run_first_and_unchanged(monkeypatch, tmp_path):
	records = _contacts(monkeypatch, tmp_path)
	south = [r for r in records if r['polyline_id'] == 'landing-8']
	assert records[:2] == south
	assert [r['id'] for r in south] == [216221522, 149111046]
	assert [r['index'] for r in south] == [0, 1]
	assert [r.get('primary_face') for r in south] == [None, True]
	assert [r['verification'] for r in south] == ['crew-reported landing side', 'crew-reported landing side']
	assert south[1]['water_side_crew_reported'] and not south[1]['water_side_verified_by_pipeline']
	assert south[1]['crew_evidence'] == {'note_id': 'south-williamsburg-face-orientation-2026-10-24', 'kind': 'face-orientation'}
	assert all(r.get('primary_face') is None for r in records[2:])
	assert {r['berth_id'] for r in records if r['polyline_id'].startswith('landing-')} == {'enc:022679C2F5BA764B'}


def test_contacts_v2_polylines_join_exactly_and_rings_close(monkeypatch, tmp_path):
	records = _contacts(monkeypatch, tmp_path)
	by_polyline = {}
	for record in records:
		by_polyline.setdefault(record['polyline_id'], []).append(record)
	for polyline, segments in by_polyline.items():
		assert [s['index'] for s in segments] == list(range(len(segments))), polyline
		for previous, current in zip(segments, segments[1:]):
			assert previous['end_lonlat'] == current['start_lonlat'], polyline
			assert previous['end_ne_m'] == current['start_ne_m'], polyline
	landing = by_polyline['landing-8'] + by_polyline['landing-8-back']
	assert landing[0]['start_lonlat'] == landing[-1]['end_lonlat'], 'the whole barge closes'


def test_contacts_export_is_deterministic(monkeypatch, tmp_path):
	first, second = tmp_path / 'a', tmp_path / 'b'
	first.mkdir(); second.mkdir()
	_contacts(monkeypatch, first)
	_contacts(monkeypatch, second)
	assert (first / 'unity' / 'contacts.json').read_bytes() == (second / 'unity' / 'contacts.json').read_bytes()


# Selectable-berth failure modes: AIS reorders the default, a fourth pontoon is offered,
# crew use is misrepresented as face orientation, or a face loses a subdivision.
def test_pier11_exports_three_named_berths_default_first_without_crew_water_side(monkeypatch, tmp_path):
	records = _contacts(monkeypatch, tmp_path, 'pier-11.json')
	faces = [r for r in records if r.get('berth_face')]
	by_id = {}
	for record in faces:
		by_id.setdefault(record['berth_id'], []).append(record)
	assert sorted(by_id, key=lambda key: by_id[key][0]['berth_order']) == [
		'enc:02261FA117EFB198', 'enc:0226111D16AAB198', 'enc:0226015B0512B198']
	assert [by_id[key][0]['berth_name'] for key in sorted(by_id, key=lambda key: by_id[key][0]['berth_order'])] == [
		'Outer pontoon (east)', 'North pontoon', 'South pontoon']
	for berth, segments in by_id.items():
		ids = segments[0]['berth_face_segment_ids']
		assert set(ids) == {s['id'] for s in segments}
		ordered = [next(s for s in segments if s['id'] == identifier) for identifier in ids]
		assert all(a['end_lonlat'] == b['start_lonlat'] for a, b in zip(ordered, ordered[1:]))
		assert all(s['berth_face_segment_ids'] == ids for s in segments)
		assert all(s['berth_use_crew_note'] == 'pier11-ramped-berths-2026-10-25' for s in segments)
		assert all(not s.get('water_side_crew_reported') for s in segments)
		assert all(bool(s.get('primary_face')) == (berth == 'enc:02261FA117EFB198') for s in segments)
	assert any(r.get('lnam') == '02260848030BB198' for r in records)
	assert not any(r.get('berth_face') for r in records if r.get('lnam') == '02260848030BB198')


def test_single_berth_exports_the_primary_face_as_main_berth(monkeypatch, tmp_path):
	records = _contacts(monkeypatch, tmp_path)
	faces = [r for r in records if r.get('berth_face')]
	assert faces and all(r.get('primary_face') for r in faces)
	assert all(r['berth_name'] == 'Main berth' and r['berth_order'] == 0 for r in faces)
	assert {tuple(r['berth_face_segment_ids']) for r in faces} == {tuple(r['id'] for r in faces)}


# Start eligibility is stricter than collision retention: every subdivision needs a basis.
# Guard zero outward reports, removal from the current crew note, and one bad subdivision.
@pytest.mark.parametrize('change', ['no-outward', 'unlisted', 'subdivision', None])
def test_nondefault_start_berths_require_a_basis_on_every_subdivision(monkeypatch, tmp_path, change):
	from ferrydata import export_unity, landings, validate
	monkeypatch.setattr(landings, 'berth_side_support', lambda *args, **kwargs:
		{'verified': True, 'outward_reports': 3, 'inward_reports': 0})
	_contacts(monkeypatch, tmp_path, 'pier-11.json')
	structures = json.loads((tmp_path / 'landing_structures.geojson').read_text())
	faces = {f['properties']['stop_id']: f['properties'] for f in
		json.loads((tmp_path / 'landing_faces.geojson').read_text())['features']}
	# This small chart fixture omits UNSARE. Supply a complete synthetic exception basis,
	# then break one condition at a time; the Unity cases also cover the shipped data.
	for feature in structures['features']:
		for segment in feature['properties']['segments']:
			if not segment.get('berth_face'): continue
			segment['water_side_verified_by_pipeline'] = False
			segment['solid_side_evidence'].update(verified=True, winding_check=True, probe_offset_m=.5,
				probe_in_charted_water=True, probe_water_layer='UNSARE', probe_in_lndare=False, probe_in_other_structure=False)
	north = [s for f in structures['features'] for s in f['properties']['segments']
		if s.get('berth_face') and s.get('berth_id') == 'enc:0226111D16AAB198']
	assert len(north) > 1
	if change == 'no-outward':
		for s in north: s['berth_side_evidence'].update(verified=False, outward_reports=0)
	elif change == 'unlisted':
		for s in north:
			note = s['berth_use_evidence']['note']
			note['berths'] = [b for b in note['berths'] if b['id'] != s['berth_id']]
	elif change == 'subdivision':
		north[1]['solid_side_evidence']['probe_in_other_structure'] = True
	assert all(landings.runtime_contact(s) for s in north), 'solid-side collision fallback remains'
	errors = validate.structure_errors(structures, faces)
	basis_errors = [e for e in errors if 'start-face basis' in e]
	assert bool(basis_errors) == (change is not None), errors
	if change is not None:
		assert all('87' in e and 'enc:0226111D16AAB198' in e for e in basis_errors)
	# Exercise the actual validate result as well; isolate this stored-data gate from the separate
	# chart/AIS source recomputation tests and figure rendering.
	from types import SimpleNamespace
	from ferrydata import landing_audit
	(tmp_path / 'landing_structures.geojson').write_text(json.dumps(structures))
	monkeypatch.setattr(validate, 'fixed_solid', lambda name: None)
	monkeypatch.setattr(validate, 'independent_berth_gates', lambda name: {})
	monkeypatch.setattr(landing_audit, 'structure_figures', lambda slice_: [])
	result = validate.check_landing_structures(SimpleNamespace(name='fixture'))
	assert result.passed == (change is None), result.summary
	# The exported subdivisions must make the same start decision as validation, while keeping contact.
	records = export_unity.contact_records(structures)
	for berth in ('enc:0226111D16AAB198', 'enc:0226015B0512B198'):
		face = [r for r in records if r.get('berth_face') and r.get('berth_id') == berth]
		assert face
		assert all(export_unity.landing_gate(r)[0] == 'kept' for r in face), 'export retains collision geometry'
		eligible = all(landings.start_face_eligible(r) for r in face)
		assert eligible == (change is None or berth.endswith('0226015B0512B198'))
