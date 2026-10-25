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
	assert [r['verification'] for r in south] == ['needs-crew-verification', 'crew-reported']
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
