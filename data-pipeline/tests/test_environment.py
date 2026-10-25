"""Environment boundary failures: frames, missing data, provenance and integrity."""
import hashlib
import json
from pathlib import Path

import numpy as np
import pytest
from rasterio.transform import from_origin

from ferrydata.environment import depth, pack, series, sources
from ferrydata.fetcher import ChecksumError
from ferrydata.manifest import Manifest


@pytest.mark.parametrize('day,hour', [('2024-10-07', 4), ('2025-01-13', 5), ('2025-04-14', 4), ('2025-07-14', 4)])
def test_epoch_is_new_york_midnight(day, hour):
	assert series.epoch_utc(day) == f'{day}T{hour:02d}:00:00Z'


def test_current_positive_flood_and_negative_ebb_use_their_own_directions():
	n, e = series.current_components(100, 45, 270)
	assert (n, e) == pytest.approx((2 ** -.5, 2 ** -.5))
	assert series.current_components(-100, 45, 270) == pytest.approx((0, -1))


def test_north_wind_blows_south_and_calm_can_lack_direction():
	assert series.wind_components(2, 0) == pytest.approx((-2, 0))
	assert series.wind_components(0, None) == (0, 0)
	assert series.wind_components(2, None) is None


def test_bilinear_is_not_nearest_and_never_blends_a_nodata_sentinel():
	a = np.array([[0., 2.], [4., 6.]])
	transform = from_origin(0, 2, 1, 1)
	assert depth.bilinear(a, transform, np.array([1.]), np.array([1.]))[0] == 3
	a[0, 0] = np.nan
	assert np.isnan(depth.bilinear(a, transform, np.array([1.]), np.array([1.]))[0])
	assert np.isnan(depth.bilinear(a, transform, np.array([-1.]), np.array([1.]))[0])


def test_grid_rows_grow_north_and_file_is_little_endian_row_major(tmp_path):
	grid = {'id': 'slice', 'origin_north_m': 0., 'origin_east_m': 0., 'cell_m': 5., 'rows': 3, 'cols': 2}
	values = depth.sample_lattice(grid, lambda north, east: north + 2 * east)
	assert values.tolist() == [[0, 10], [5, 15], [10, 20]]
	(tmp_path / 'slice.f32').write_bytes(values.astype('<f4').tobytes(order='C'))
	assert np.fromfile(tmp_path / 'slice.f32', dtype='<f4').tolist() == [0, 10, 5, 15, 10, 20]


def test_observations_win_and_prediction_fills_are_labelled():
	rows, notes = series.fill_water_levels({0: 1., 720: 3.}, {0: 99., 360: 2., 720: 99.}, [0, 360, 720])
	assert rows == [(0, 1., 'observed'), (360, 2., 'predicted'), (720, 3., 'observed')]
	assert any('1' in note and 'predicted' in note for note in notes)


def test_missing_rows_remain_missing_and_long_gaps_are_noted():
	rows, notes = series.fill_water_levels({0: 1., 2160: 3.}, {}, range(0, 2520, 360))
	assert rows == [(0, 1., 'observed'), (2160, 3., 'observed')]
	assert any('2160' in note and '1800' in note for note in notes)


def test_raw_checksum_failure_never_refetches(tmp_path, monkeypatch):
	m = Manifest.load('fixture', tmp_path)
	p = tmp_path / 'raw.json'
	p.write_text('original')
	m.record(p.name, 'https://example.invalid', hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_size)
	p.write_text('changed')
	monkeypatch.setattr(sources.fetcher, 'fetch_file', lambda *a, **kw: pytest.fail('corruption must not trigger network'))
	with pytest.raises(ChecksumError):
		sources.checked_bytes(m, p.name, p)


@pytest.fixture
def tiny_pack(tmp_path):
	root = tmp_path / 'env'
	(root / 'depth').mkdir(parents=True)
	grid = {'id': 'slice', 'file': 'slice.f32', 'origin_north_m': 0., 'origin_east_m': 0., 'cell_m': 5., 'rows': 2, 'cols': 2, 'nodata': 'NaN'}
	data = np.array([[-10, -11], [-12, np.nan]], dtype='<f4').tobytes()
	(root / 'depth' / 'slice.f32').write_bytes(data)
	grid['sha256'] = hashlib.sha256(data).hexdigest()
	dm = {'format': 'ferrysim-env-depth/1', 'region': 'lower-east-river', 'datum': 'NAVD88', 'grids': [grid], 'outside_depth_m': 20., 'sources': [], 'value': 'bed elevation, metres, positive up (water is negative)'}
	pack.write_json(root / 'depth' / 'manifest.json', dm)
	depth_hash = pack.depth_digest(root / 'depth')
	(root / 'depth' / 'DIGEST').write_bytes((depth_hash + '\n').encode('ascii'))
	stations = [dict(id='a', name='fixture', lat=40.7, lon=-74.015, north_m=0., east_m=0., source='fixture.json', source_sha256='0'*64)]
	args = dict(root=root, region='lower-east-river', day='2025-07-14', depth_digest=depth_hash,
		current_stations=[dict(stations[0], kind='harmonic-prediction', bin_depth_m=1.5)],
		water_level_stations=stations, wind_stations=[dict(stations[0], sensor_height_m=None)],
		currents=[('a', -3600., 1., 0.), ('a', 90000., 0., 1.)],
		water_levels=[('a', -3600., 0., 'observed'), ('a', 90000., 1., 'predicted')],
		wind=[('a', -3600., -1., 0., None), ('a', 90000., 0., 0., 2.)], notes=['fixture gap over 1800 s'])
	pack.write_day(**args)
	assert pack.verify(root) == [(root / 'days/2025-07-14/DIGEST').read_text().strip()]
	return root, args


@pytest.mark.parametrize('empty_wind', [False, True])
def test_pack_bytes_and_both_digests_reproduce(tiny_pack, empty_wind):
	root, args = tiny_pack
	if empty_wind:
		args.update(wind=[], wind_stations=[], notes=['No wind available; core reports still air.'])
	first = pack.write_day(**args)
	before = {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}
	assert pack.write_day(**args) == first
	assert before == {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}
	assert 'digest' not in first
	assert 'digest' not in json.loads((root / 'depth/manifest.json').read_bytes())
	assert first['depth_digest'] == (root / 'depth/DIGEST').read_text().strip()
	assert pack.verify(root) == [(root / 'days/2025-07-14/DIGEST').read_text().strip()]


PACK_FILES = ['depth/manifest.json', 'depth/slice.f32', 'depth/DIGEST', *[
	f'days/2025-07-14/{name}' for name in ('manifest.json', 'currents.csv', 'water_levels.csv', 'wind.csv', 'DIGEST')]]


@pytest.mark.parametrize('name', PACK_FILES)
def test_single_byte_change_in_every_pack_file_is_refused(tiny_pack, name):
	root, _ = tiny_pack
	p = root / name
	body = bytearray(p.read_bytes())
	# A whitespace change leaves JSON semantically identical: hash raw bytes.
	index = body.index(b'\t') if p.suffix == '.json' else 0
	body[index] = ord(' ') if p.suffix == '.json' else (ord('1') if body[index] == ord('0') else ord('0'))
	p.write_bytes(body)
	with pytest.raises(ValueError, match=p.name):
		pack.verify(root)


@pytest.mark.parametrize('name,old,new', [
	('depth/manifest.json', b'"outside_depth_m": 20.0', b'"outside_depth_m": 21.0'),
	('days/2025-07-14/manifest.json', b'90000', b'90360'),
	('days/2025-07-14/manifest.json', b'"name": "fixture"', b'"name": "changed"'),
	('days/2025-07-14/manifest.json', b'"depth_digest": "', b'"depth_digest": "0'),
])
def test_manifest_field_changes_are_refused(tiny_pack, name, old, new):
	root, _ = tiny_pack
	p = root / name
	body = p.read_bytes()
	assert old in body
	p.write_bytes(body.replace(old, new, 1))
	with pytest.raises(ValueError, match='manifest.json'):
		pack.verify(root)


@pytest.mark.parametrize('directory', ['depth', 'days/2025-07-14'])
@pytest.mark.parametrize('body', [b'0' * 64, b'A' * 64 + b'\n', b'g' * 64 + b'\n', b'0' * 63 + b'\n', b'0' * 64 + b'\r\n'])
def test_digest_requires_exact_lowercase_hex_and_lf(tiny_pack, directory, body):
	root, _ = tiny_pack
	(root / directory / 'DIGEST').write_bytes(body)
	with pytest.raises(ValueError, match='DIGEST'):
		pack.verify(root)


def test_largest_interior_rectangle_removes_padding_without_extending_depth():
	mask = np.array([[0, 0, 0, 0, 0], [0, 1, 1, 1, 0], [0, 1, 1, 1, 1], [0, 1, 1, 1, 0]], dtype=bool)
	r0, c0, r1, c1 = depth.largest_rectangle(mask)
	assert (r0, c0, r1, c1) == (1, 1, 4, 4)
	assert mask[r0:r1, c0:c1].all()


def test_missing_water_observations_all_use_navd88_predictions():
	rows, notes = series.fill_water_levels({}, {-3600: .2, 90000: -.3}, [-3600, 90000])
	assert all(r[2] == 'predicted' for r in rows)
	assert any('2 missing' in n for n in notes)


def test_absent_tidal_phase_is_unknown_not_a_nonfinite_report_value():
	assert series.phase_peak([.2, .4]) == .4
	assert series.phase_peak([]) is None


def test_untracked_raw_file_is_refused_before_fetch(tmp_path, monkeypatch):
	inputs = sources.Inputs()
	inputs.cache = tmp_path
	inputs.manifest = Manifest.load('fixture', tmp_path / 'manifests')
	(tmp_path / 'untracked.json').write_text('{}')
	monkeypatch.setattr(sources.fetcher, 'fetch_file', lambda *a, **kw: pytest.fail('untracked input must not be adopted'))
	with pytest.raises(ChecksumError):
		inputs.get('untracked.json', 'https://example.invalid')


def test_v1_digest_wire_golden_values(tmp_path):
	# Literal bytes and fixed hashes, independent of writer JSON formatting.
	(tmp_path / 'test.f32').write_bytes(bytes.fromhex('000080bf000000c0000040c0000080c0'))
	(tmp_path / 'manifest.json').write_bytes(b'{"grids":[{"file":"test.f32"}],"outside_depth_m":20.0}\n')
	depth_hash = pack.depth_digest(tmp_path)
	assert depth_hash == 'e992d5bc8b7d9fd05a66cfb843c611f2702c5e653c05ce29f114617b9f7f642c'
	(tmp_path / 'currents.csv').write_bytes(b'station_id,t_s,north_mps,east_mps\na,0.0,1.0,0.0\n')
	(tmp_path / 'water_levels.csv').write_bytes(b'station_id,t_s,level_navd88_m,source\na,0.0,0.0,observed\n')
	(tmp_path / 'wind.csv').write_bytes(b'station_id,t_s,north_mps,east_mps,gust_mps\na,0.0,-1.0,0.0,\n')
	(tmp_path / 'manifest.json').write_bytes(b'{"depth_digest":"' + depth_hash.encode('ascii') + b'","time_coverage_s":[-3600,90000]}\n')
	assert pack.day_digest(tmp_path, depth_hash) == '9769ec908fd1a10868b85e2c1ea4a7c4866c543454267e326c838b70d3dc39e6'
