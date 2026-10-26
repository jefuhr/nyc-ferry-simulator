"""The harbor region's packs, offline: three synthetic active tiles with their own rasters, a fake raw cache, no network.
Failure modes guarded: tile grids that leave a gap or do not share their edge rows and columns; shared samples taken from
each tile's own raster, so the grids disagree and the depth-set rule refuses them (the owner's raster gives them: the
north neighbour's on a north edge, the first active tile where the owner is not active); a step at a seam; current
stations duplicated across overlapping tiles, weak and variable ones kept, or harmonic ones fetched per day instead of per
week; gauges not the slice's; a harbor day naming a current field; two builds that differ in any byte; and a download.
"""
import hashlib
import json
from datetime import timedelta

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin

from ferrydata import fetcher, geodesy, manifest, paths, slices, tiles
from ferrydata.environment import harbor, pack, series
from ferrydata.environment.__main__ import main

DAY = '2025-07-14'
ACTIVE = [(0, 0), (0, 1), (1, 0)]  # (1, 1) is not active, so the corner at (4000, 4000) falls back to tile_0_0


def bed(lon, lat):
	return -10. - 200. * (lat - 40.7) + 100. * (lon + 74.015)


def write_raster(path, i, j, offset=0.):
	"""A NAD83 raster over tile (i, j)'s square grown by 400 m, 0.0005 degree pixels, a smooth bed (plus an offset)."""
	n0, e0, n1, e1 = tiles.SIZE_M * i - 400., tiles.SIZE_M * j - 400., tiles.SIZE_M * (i + 1) + 400., tiles.SIZE_M * (j + 1) + 400.
	lat, lon = geodesy.from_local(np.array([n0, n1, n0, n1]), np.array([e0, e0, e1, e1]))
	west, north = float(lon.min()), float(lat.max())
	width, height = int((lon.max() - west) / .0005) + 2, int((north - lat.min()) / .0005) + 2
	transform = from_origin(west, north, .0005, .0005)
	cols, rows = np.meshgrid(np.arange(width), np.arange(height))
	x, y = rasterio.transform.xy(transform, rows.ravel(), cols.ravel())
	values = (bed(np.array(x), np.array(y)) + offset).reshape(height, width).astype('float32')
	path.parent.mkdir(parents=True, exist_ok=True)
	with rasterio.open(path, 'w', driver='GTiff', width=width, height=height, count=1, dtype='float32', crs='EPSG:4269',
		transform=transform, nodata=-99999.) as ds:
		ds.write(values, 1)
		ds.update_tags(vertical_datum='NAVD88')
	path.with_suffix('.json').write_text('{}')


@pytest.fixture
def harbor_fixture(tmp_path, monkeypatch):
	monkeypatch.setattr(paths, 'REPO_ROOT', tmp_path)
	monkeypatch.setattr(paths, 'MANIFEST_DIR', tmp_path / 'manifests')
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', tmp_path / 'processed')
	monkeypatch.setenv('FERRYSIM_CACHE', str(tmp_path / 'cache'))
	monkeypatch.setattr(fetcher, '_download', lambda *a, **kw: pytest.fail('fixture must never use network'))
	monkeypatch.setattr(harbor, 'CELL_M', 100.)
	root = paths.processed_dir('harbor')
	entries = []
	for i in range(0, 2):
		for j in range(0, 2):
			active = (i, j) in ACTIVE
			entries.append(dict(id=f'tile_{i}_{j}', i=i, j=j, active=active))
			if active:
				# tile_1_0's raster reads 4 cm deeper than the others: within the 0.05 m tiles v1 allows in the shared bands.
				write_raster(root / 'tiles' / f'tile_{i}_{j}' / 'depth_merged_navd88.tif', i, j, offset=-.04 if (i, j) == (1, 0) else 0.)
	(root / 'index.json').write_text(json.dumps(dict(tiles=entries)))
	# The listings overlap as the real tiles' do: NYH1920 and the gauge appear in two tiles.
	def listing(*stations):
		return json.dumps(dict(stations=list(stations)))
	nyh1920 = dict(id='NYH1920', name='Brooklyn Bridge', lat=40.706, lon=-73.9977, type='H', kind='current_prediction', owner_tile='tile_0_0')
	new = dict(id='NYHNEW', name='A new harmonic station', lat=40.735, lon=-73.99, type='H', kind='current_prediction', owner_tile='tile_0_0')
	sub = dict(id='ACTX', name='fixture subordinate', lat=40.7, lon=-73.9138, type='S', kind='current_prediction', owner_tile='tile_0_1')
	weak = dict(id='ACTW', name='weak and variable', lat=40.74, lon=-73.92, type='W', kind='current_prediction', owner_tile='tile_1_0')
	battery = dict(id='8518750', name='The Battery', lat=40.700554, lon=-74.01417, kind='water_level', owner_tile='tile_0_0')
	(root / 'tiles' / 'tile_0_0' / 'coops_stations.json').write_text(listing(nyh1920, new, battery))
	(root / 'tiles' / 'tile_0_1' / 'coops_stations.json').write_text(listing(nyh1920, sub))
	(root / 'tiles' / 'tile_1_0' / 'coops_stations.json').write_text(listing(weak, battery))

	env = manifest.Manifest.load('coops-environment')
	cal = manifest.Manifest.load('coops-calibration')
	listing = manifest.Manifest.load('coops')
	def raw(m, name, body):
		p = paths.raw_dir(m.source) / name
		p.parent.mkdir(parents=True, exist_ok=True)
		p.write_bytes(body.encode())
		m.record(name, 'https://example.invalid/fixture', hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_size,
			retrieved_utc='2026-01-01T00:00:00Z')
	def payload(name, data):
		raw(env, name, json.dumps(data))
	for station in ('8518750', '8519483', '8516945', '8516990'):
		payload(f'station_{station}.json', {'stations': [dict(id=station, name=station, lat=40.7, lng=-74.015)]})
		payload(f'datums_{station}.json', {'datums': [] if station == '8519483' else [{'name': 'NAVD88'}]})
	times = [(series.epoch(DAY) + timedelta(seconds=t)).strftime('%Y-%m-%d %H:%M') for t in range(-3600, 90001, 360)]
	water = [dict(t=t, v='.5') for t in times]
	for station in ('8518750', '8516945'):
		payload(f'water_level_{station}_{DAY}.json', {'data': water})
	payload(f'water_level_8516990_{DAY}.json', {'error': {'message': 'No data'}})
	payload(f'predictions_8516990_{DAY}.json', {'predictions': water})
	payload(f'wind_8518750_{DAY}.json', {'error': {'message': 'No data'}})
	payload(f'wind_8516945_{DAY}.json', {'data': [dict(t=t, s='2', d='0', g='3') for t in times]})
	header = 'Time, Depth, Velocity_Major, meanFloodDir, meanEbbDir, Bin\n'
	cal.header['stations'] = [dict(id='NYH1920', name='Brooklyn Bridge', lat=40.706, lon=-73.9977)]
	raw(cal, f'NYH1920_{DAY}.csv', header + ''.join(f'{t},2.4,100,45,225,12\n' for t in times))
	# The new harmonic station's week, in the calibration window: the week's first UTC date to the date a week later.
	week = [(series.epoch(DAY).replace(hour=0) + timedelta(minutes=6 * k)).strftime('%Y-%m-%d %H:%M') for k in range(8 * 240)]
	raw(env, f'currents_NYHNEW_week_{DAY}.csv', header + ''.join(f'{t},1.5,-50,30,210,3\n' for t in week))
	payload('offsets_ACTX.json', {'stations': [dict(id='ACTX', name='fixture subordinate', lat=40.7, lng=-73.9138, type='S', currbin=1,
		currentpredictionoffsets=dict(refStationId='REF', refStationBin=6, meanFloodDir=30., meanEbbDir=210., mfcTimeAdjMin=-6,
			sbeTimeAdjMin=-2, mecTimeAdjMin=8, sbfTimeAdjMin=1, mfcAmpAdj=.6, mecAmpAdj=1.12))]})
	period = 44712.
	week0 = series.epoch(DAY) - timedelta(hours=13)
	stamps = [week0 + timedelta(seconds=360 * k) for k in range(int((7 * 86400 + 26 * 3600 + 13 * 3600) / 360) + 1)]
	seconds = [(t - series.epoch(DAY)).total_seconds() for t in stamps]
	raw(env, f'currents_REF_bin6_{DAY}.csv', header + ''.join(
		f'{t.strftime("%Y-%m-%d %H:%M")},4.6,{150 * np.sin(2 * np.pi * x / period):.1f},45.0,225.0,6\n' for t, x in zip(stamps, seconds)))
	events = []
	for k in range(int(seconds[0] // (period / 4)) + 1, int(seconds[-1] // (period / 4))):
		kind, v = [('slack', 0.), ('flood', 150.), ('slack', 0.), ('ebb', -150.)][k % 4]
		events.append(((series.epoch(DAY) + timedelta(seconds=k * period / 4)).strftime('%Y-%m-%d %H:%M'), kind, v))
	event_header = 'Time, Depth, Type, Velocity_Major, meanFloodDir, meanEbbDir, Bin\n'
	raw(env, f'events_REF_bin6_{DAY}.csv', event_header + ''.join(f'{t},4.6,{k},{v},45.0,225.0,6\n' for t, k, v in events))
	raw(env, f'events_ACTX_bin1_{DAY}.csv', event_header + ''.join(f'{t},null,{k},{v * .6 if v > 0 else v * 1.12},30.0,210.0,1\n' for t, k, v in events))
	# The CO-OPS listing tiles v1 read: NYHNEW's bins are heights above the seabed.
	raw(listing, 'stations_currentpredictions.json', json.dumps(dict(stations=[dict(id='NYH1920', depthType='B'), dict(id='NYHNEW', depthType='S')])))
	env.save()
	cal.save()
	listing.save()
	return root


def test_tile_grids_share_their_edges_on_one_lattice():
	a, b, c = harbor.lattice(0, 0, 10.), harbor.lattice(0, 1, 10.), harbor.lattice(1, 0, 10.)
	assert (a['rows'], a['cols']) == (401, 401)
	assert a['origin_east_m'] + (a['cols'] - 1) * a['cell_m'] == b['origin_east_m'], 'east neighbour starts on the last column'
	assert a['origin_north_m'] + (a['rows'] - 1) * a['cell_m'] == c['origin_north_m'], 'north neighbour starts on the last row'
	with pytest.raises(ValueError, match='does not divide'):
		harbor.lattice(0, 0, 7.)


def test_a_centre_takes_its_owners_raster_or_the_first_active_tile_holding_it():
	active = set(ACTIVE)
	assert harbor.source(100., 100., active) == (0, 0)
	assert harbor.source(4000., 100., active) == (1, 0), 'a north edge belongs to the north neighbour'
	assert harbor.source(100., 4000., active) == (0, 1), 'an east edge to the east neighbour'
	assert harbor.source(4000., 4000., active) == (0, 0), 'the corner owner (1, 1) is not active: the first active tile holding it'
	assert harbor.source(8000., 100., active) == (1, 0), 'the owner (2, 0) is outside the grid: the tile below'
	assert harbor.source(-1., 100., active) is None


def test_the_harbor_build_twice_is_byte_identical_and_verifies_without_a_download(harbor_fixture):
	root = harbor_fixture
	assert main(['build', '--region', 'harbor', '--days', DAY]) == 0
	env = root / 'env'
	before = {str(p.relative_to(env)): p.read_bytes() for p in env.rglob('*') if p.is_file()}
	assert main(['build', '--region', 'harbor', '--days', DAY]) == 0
	assert before == {str(p.relative_to(env)): p.read_bytes() for p in env.rglob('*') if p.is_file()}
	assert main(['verify', '--region', 'harbor']) == 0
	assert (paths.REPO_ROOT / 'artifacts' / 'environment-pack' / 'harbor' / 'report.md').is_file()
	summary = json.loads((paths.REPO_ROOT / 'artifacts' / 'environment-pack' / 'harbor' / 'build-summary.json').read_text())
	assert summary['new_fetch_count'] == 0
	assert not (env / 'field').exists()


def test_the_harbor_day_lists_the_tiles_stations_once_without_the_weak_ones_and_names_no_field(harbor_fixture):
	assert main(['build', '--region', 'harbor', '--days', DAY]) == 0
	metadata = json.loads((harbor_fixture / 'env' / 'days' / DAY / 'manifest.json').read_text())
	assert 'field_digest' not in metadata
	assert [s['id'] for s in metadata['current_stations']] == ['ACTX', 'NYH1920', 'NYHNEW']
	assert next(s for s in metadata['current_stations'] if s['id'] == 'NYHNEW')['source'] == f'currents_NYHNEW_week_{DAY}.csv'
	assert next(s for s in metadata['current_stations'] if s['id'] == 'NYH1920')['source'] == f'NYH1920_{DAY}.csv'
	assert next(s for s in metadata['current_stations'] if s['id'] == 'ACTX')['derivation']['reference_station'] == 'REF'
	assert [s['id'] for s in metadata['water_level_stations']] == ['8518750', '8516945', '8516990']
	assert [s['id'] for s in metadata['wind_stations']] == ['8516945']
	assert any('ACTW (weak and variable)' in n and 'no predictions' in n for n in metadata['notes'])
	assert any('no current field' in n and 'StationBlendCurrentField' in n for n in metadata['notes'])
	assert any('8519483' in n and 'no NAVD88' in n for n in metadata['notes'])
	assert any(n.startswith('NYHNEW also reference their bins to the seabed') for n in metadata['notes'])


def test_the_harbor_depth_grids_share_identical_edges_from_the_owners_raster_with_no_seam_step(harbor_fixture):
	assert main(['build', '--region', 'harbor', '--days', DAY]) == 0
	depth = pack.load_depth(harbor_fixture / 'env' / 'depth')
	grids = {g['id']: g for g in depth.grids}
	assert list(grids) == ['tile_0_0', 'tile_0_1', 'tile_1_0']
	south, north, east = grids['tile_0_0']['values'], grids['tile_1_0']['values'], grids['tile_0_1']['values']
	assert np.array_equal(south[-1], north[0]), 'the shared row'
	assert np.array_equal(south[:, -1], east[:, 0]), 'the shared column'
	# The shared row is the north tile's raster (4 cm deeper), the row below it the south tile's own; the corner, whose
	# owner tile_1_1 is not active, is tile_0_0's.
	raster = harbor.Raster(harbor_fixture / 'tiles' / 'tile_1_0' / 'depth_merged_navd88.tif')
	own = harbor.Raster(harbor_fixture / 'tiles' / 'tile_0_0' / 'depth_merged_navd88.tif')
	n = np.full(41, 4000.)
	e = np.arange(41) * 100.
	assert np.array_equal(north[0][:-1], raster.sample(n, e).astype('<f4')[:-1])
	assert north[0][-1] == own.sample(4000., 4000.).astype('<f4')
	assert np.array_equal(south[-2][:-1], own.sample(n - 100., e).astype('<f4')[:-1])
	# Across the seam in 1 mm steps: no step beyond the bed's slope either side (the bed's own 1.8e-6 m per mm, and the
	# 4 cm between the rasters spread over the last 100 m cell, 4e-7 m per mm).
	beds = np.array([depth.sample(4000. + k * 1e-3, 1234.5).bed for k in range(-500, 501)])
	steps = np.abs(np.diff(beds))
	assert steps[499:501].max() <= max(steps[:499].max(), steps[501:].max()) + 1e-12
	assert steps.max() < 3e-6


def test_shared_samples_from_each_tiles_own_raster_are_refused(harbor_fixture):
	assert main(['build', '--region', 'harbor', '--days', DAY]) == 0
	directory = harbor_fixture / 'env' / 'depth'
	m = json.loads((directory / 'manifest.json').read_text())
	# What a builder that sampled each tile's own raster would write: tile_0_0's north row from its own raster.
	values = np.fromfile(directory / 'tile_0_0.f32', dtype='<f4').reshape(41, 41)
	values[-1] = harbor.Raster(harbor_fixture / 'tiles' / 'tile_0_0' / 'depth_merged_navd88.tif').sample(np.full(41, 4000.), np.arange(41) * 100.)
	(directory / 'tile_0_0.f32').write_bytes(values.astype('<f4').tobytes())
	m['grids'][0]['sha256'] = hashlib.sha256((directory / 'tile_0_0.f32').read_bytes()).hexdigest()
	pack.write_json(directory / 'manifest.json', m)
	pack.write_digest(directory, pack.depth_digest(directory))
	with pytest.raises(ValueError, match='tile_0_0 and tile_1_0 share the sample at north 4000'):
		pack.verify_depth(directory)


def test_the_gauges_are_the_regions_named_ones_then_any_other_a_tile_lists():
	assert harbor.gauge_ids([dict(id='8518750'), dict(id='9999999')]) == ['8518750', '8519483', '8516945', '8516990', '9999999']
	assert tiles.extent().water_level_stations == slices.LOWER_EAST_RIVER.water_level_stations


def test_the_validations_lattice_rule_is_what_the_build_wrote(harbor_fixture):
	# validate --region harbor compares lattices of each cell size built by this rule; at the built size it must be the set.
	from ferrydata.environment import harbor_validation
	assert main(['build', '--region', 'harbor', '--days', DAY]) == 0
	depth = pack.load_depth(harbor_fixture / 'env' / 'depth')
	active = set(harbor.active_tiles(harbor_fixture))
	rasters = harbor.Rasters(harbor_fixture, active)
	rng = np.random.default_rng(1)
	n, e = rng.uniform(0., 8000., 400), rng.uniform(0., 8000., 400)
	keep = ~((n > 4000.) & (e > 4000.))
	n, e = n[keep], e[keep]
	built = np.array([depth.sample(a, b).bed for a, b in zip(n, e)])
	assert np.max(np.abs(harbor_validation.lattice_bed(n, e, 100., active, rasters) - built)) < 1e-9
	i, j = harbor_validation.sources(np.array([100., 4000., 4000.]), np.array([100., 100., 4000.]), active)
	assert list(zip(i.tolist(), j.tolist())) == [(0, 0), (1, 0), (0, 0)]
