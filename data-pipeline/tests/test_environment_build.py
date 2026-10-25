"""Offline workflow: actual raster/cache/manifest inputs through CLI and verification."""
import hashlib
import json
from dataclasses import replace
from datetime import timedelta

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin

from ferrydata import fetcher, manifest, paths, slices
from ferrydata.environment import pack, series
from ferrydata.environment.__main__ import main


@pytest.fixture
def environment_fixture(tmp_path, monkeypatch):
	monkeypatch.setattr(paths, 'REPO_ROOT', tmp_path)
	monkeypatch.setattr(paths, 'MANIFEST_DIR', tmp_path / 'manifests')
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', tmp_path / 'processed')
	monkeypatch.setenv('FERRYSIM_CACHE', str(tmp_path / 'cache'))
	monkeypatch.setattr(fetcher, '_download', lambda *a, **kw: pytest.fail('fixture must never use network'))
	day = '2025-07-14'
	slice_ = replace(slices.LOWER_EAST_RIVER, south=40.6996, north=40.7004, west=-74.0154, east=-74.0146,
		water_level_stations=('8518750', '8519483', '8516990'))
	monkeypatch.setattr(slices, 'get_slice', lambda _: slice_)
	out = paths.processed_dir(slice_.name)
	out.mkdir(parents=True)
	for name, elevation in [('depth_merged_navd88', -10.), ('depth_chart_navd88', -9.)]:
		with rasterio.open(out / f'{name}.tif', 'w', driver='GTiff', width=100, height=100,
			count=1, dtype='float32', crs='EPSG:4326', transform=from_origin(-74.016, 40.701, .00002, .00002), nodata=-99999.) as ds:
			ds.write(np.full((100, 100), elevation, dtype='float32'), 1)
			ds.update_tags(vertical_datum='NAVD88')
	(out / 'depth_merged_navd88.json').write_text('{}')
	(out / 'land_areas.geojson').write_text('{"type":"FeatureCollection","features":[]}')
	env = manifest.Manifest.load('coops-environment')
	cal = manifest.Manifest.load('coops-calibration')
	def raw(m, name, body):
		p = paths.raw_dir(m.source) / name
		p.parent.mkdir(parents=True, exist_ok=True)
		p.write_bytes(body.encode())
		m.record(name, 'https://example.invalid/fixture', hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_size,
			retrieved_utc='2026-01-01T00:00:00Z')
	def payload(name, data):
		raw(env, name, json.dumps(data))
	for station in ('8518750', '8519483', '8516990', '8516945'):
		payload(f'station_{station}.json', {'stations': [dict(id=station, name=station, lat=40.7, lng=-74.015)]})
		payload(f'datums_{station}.json', {'datums': [] if station == '8519483' else [{'name': 'NAVD88'}]})
	times = [(series.epoch(day)+timedelta(seconds=t)).strftime('%Y-%m-%d %H:%M') for t in range(-3600, 90001, 360)]
	water = [dict(t=t, v='.5') for t in times]
	payload(f'water_level_8518750_{day}.json', {'data': [r for i,r in enumerate(water) if i != 10]})
	payload(f'predictions_8518750_{day}.json', {'predictions': [dict(t=t, v='.4') for t in times]})
	payload(f'water_level_8516990_{day}.json', {'error': {'message': 'No data'}})
	payload(f'predictions_8516990_{day}.json', {'predictions': water})
	payload(f'wind_8518750_{day}.json', {'error': {'message': 'No data'}})
	payload(f'wind_8516945_{day}.json', {'data': [dict(t=t, s='2', d='0', g='3') for t in times]})
	# Five harmonic stations 100 m apart (their field disks may not overlap) and one subordinate to a reference.
	cal.header['stations'] = [dict(id=s, name=s, lat=float(lat), lon=-74.015) for s, lat in zip(('NYH1919','NYH1920','NYH1921','NYH1922','NYH1927'),
		40.7 + (np.arange(5) - 2) * 100. / 111034.)]
	for station in cal.header['stations']:
		body = 'Time, Depth, Velocity_Major, meanFloodDir, meanEbbDir, Bin\n' + ''.join(f'{t},9.4,100,45,225,9\n' for t in times)
		raw(cal, f'{station["id"]}_{day}.csv', body)
	(out / 'coops_stations.json').write_text(json.dumps({'stations': [
		dict(id='ACTX', kind='current_prediction', station_type='subordinate'), dict(id='NYH1920', kind='current_prediction', station_type='harmonic')]}))
	payload('offsets_ACTX.json', {'stations': [dict(id='ACTX', name='fixture subordinate', lat=40.7, lng=-74.0138, type='S', currbin=1,
		currentpredictionoffsets=dict(refStationId='REF', refStationBin=6, meanFloodDir=30., meanEbbDir=210., mfcTimeAdjMin=-6,
			sbeTimeAdjMin=-2, mecTimeAdjMin=8, sbfTimeAdjMin=1, mfcAmpAdj=.6, mecAmpAdj=1.12))]})
	# The reference: a pure semidiurnal sine over the week's window and 12 hours either side; its events at its slacks and peaks.
	period = 44712.
	week0 = series.epoch(day) - timedelta(hours=13)
	stamps = [week0 + timedelta(seconds=360 * k) for k in range(int((7 * 86400 + 26 * 3600 + 13 * 3600) / 360) + 1)]
	seconds = [(t - series.epoch(day)).total_seconds() for t in stamps]
	raw(env, f'currents_REF_bin6_{day}.csv', 'Time, Depth, Velocity_Major, meanFloodDir, meanEbbDir, Bin\n' + ''.join(
		f'{t.strftime("%Y-%m-%d %H:%M")},4.6,{150 * np.sin(2 * np.pi * x / period):.1f},45.0,225.0,6\n' for t, x in zip(stamps, seconds)))
	events = []
	for k in range(int(seconds[0] // (period / 4)) + 1, int(seconds[-1] // (period / 4))):
		x = k * period / 4
		kind, v = [('slack', 0.), ('flood', 150.), ('slack', 0.), ('ebb', -150.)][k % 4]
		events.append(((series.epoch(day) + timedelta(seconds=x)).strftime('%Y-%m-%d %H:%M'), kind, v))
	header = 'Time, Depth, Type, Velocity_Major, meanFloodDir, meanEbbDir, Bin\n'
	raw(env, f'events_REF_bin6_{day}.csv', header + ''.join(f'{t},4.6,{k},{v},45.0,225.0,6\n' for t, k, v in events))
	raw(env, f'events_ACTX_bin1_{day}.csv', header + ''.join(f'{t},null,{k},{v * .6 if v > 0 else v * 1.12},30.0,210.0,1\n' for t, k, v in events))
	env.save()
	cal.save()
	# No ENC or CUDEM offline: open water, 10 m deep, in a small box around the fixture.
	from ferrydata.environment import region_field
	monkeypatch.setitem(region_field.REGIONS, 'lower-east-river', dict(north=(-400., 400.), east=(-400., 400.), cell=20., inflow='south', outflow='north'))
	monkeypatch.setattr(region_field, 'land', lambda lat: (np.zeros((lat.rows, lat.cols), bool), []))
	monkeypatch.setattr(region_field, 'bed', lambda lat, depth_dir: (np.full((lat.rows, lat.cols), -10.),
		[dict(name='depth/DIGEST', sha256=pack.read_digest(depth_dir), note='fixture')]))
	return out, day


def test_cli_build_twice_verify_provenance_fill_and_fallback(environment_fixture):
	out, day = environment_fixture
	args = ['build', '--region', 'lower-east-river', '--days', day]
	assert main(args) == 0
	root = out / 'env'
	before = {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}
	assert main(args) == 0
	assert before == {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}
	assert main(['verify', '--region', 'lower-east-river']) == 0
	metadata = json.loads((root / 'days' / day / 'manifest.json').read_text())
	assert [s['id'] for s in metadata['wind_stations']] == ['8516945']
	assert metadata['water_level_stations'][0]['id'] == '8518750'
	assert [s['id'] for s in metadata['water_level_stations']] == ['8518750', '8516990']
	assert any('1 missing observed' in n for n in metadata['notes'])
	assert any('261 missing observed' in n for n in metadata['notes'])
	assert any('no NAVD88' in n for n in metadata['notes'])
	assert any('Long Island Sound' in n for n in metadata['notes'])
	assert metadata['field_digest'] == pack.read_digest(root / 'field')
	subordinate = next(s for s in metadata['current_stations'] if s['id'] == 'ACTX')
	assert subordinate['kind'] == 'subordinate-prediction' and subordinate['derivation']['reference_station'] == 'REF'
	assert [s['id'] for s in metadata['current_stations']] == sorted(s['id'] for s in metadata['current_stations'])
	field_manifest = json.loads((root / 'field' / 'manifest.json').read_text())
	assert [s['id'] for s in field_manifest['stations']] == sorted(s['id'] for s in metadata['current_stations'])
	assert (paths.REPO_ROOT / 'artifacts/environment-pack/report.md').is_file()
	assert (paths.REPO_ROOT / 'artifacts/environment-pack/currents-tide.png').is_file()
	p = paths.raw_dir('coops-calibration') / f'NYH1919_{day}.csv'
	p.write_text('corrupted')
	with pytest.raises(fetcher.ChecksumError):
		main(args)


def test_cli_rejects_non_scenario_day_before_any_download(environment_fixture):
	with pytest.raises(SystemExit) as error:
		main(['build', '--region', 'lower-east-river', '--days', '2026-01-01'])
	assert error.value.code == 2


def test_missing_calibration_cache_fetches_only_into_environment_cache(environment_fixture):
	from ferrydata.environment.sources import Inputs
	out, day = environment_fixture
	p = paths.raw_dir('coops-calibration') / f'NYH1919_{day}.csv'
	body = p.read_bytes()
	p.unlink()
	m = manifest.Manifest.load('coops-environment')
	name = f'currents_NYH1919_{day}.csv'
	cache = paths.raw_dir('coops-environment') / name
	cache.write_bytes(body)
	m.record(name, 'https://example.invalid/fixture', hashlib.sha256(body).hexdigest(), len(body))
	m.save()
	inputs = Inputs()
	station = inputs.calibration.header['stations'][0]
	metadata, rows, notes, stats = inputs.current(station, day)
	assert metadata['source'] == name
	assert len(rows) == 261
	assert not p.exists()


def test_unknown_water_is_filled_only_with_charted_depth(environment_fixture):
	from ferrydata.environment import depth
	out, day = environment_fixture
	with rasterio.open(out / 'depth_merged_navd88.tif', 'r+') as ds:
		values = ds.read(1)
		values[40:60,40:60] = ds.nodata
		ds.write(values, 1)
	metadata, stats = depth.build(slices.get_slice('lower-east-river'), out, out / 'env/depth')
	assert stats['water_nan_before'] > 0
	assert stats['chart_filled_cells'] == stats['water_nan_before']
	assert stats['water_nan_after'] == 0
	assert any(s['name'] == 'depth_chart_navd88.tif' for s in metadata['sources'])
