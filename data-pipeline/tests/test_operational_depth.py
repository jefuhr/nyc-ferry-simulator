"""Operational depth floor from the fleet's AIS (operational_depth).

Failure modes: a bed raised; land, a fixed structure or nodata touched; a report outside the radius, over the SOG limit,
of another class, off the depth grid or off the scenario days used; the wrong level model (one gauge, wrong
interpolation across gaps, wrong blend); footprints not combined by their deepest requirement; a footprint of the wrong
size; float32 rounding leaving a bed above its floor; nondeterminism; provenance missing from the outputs.
"""
import json
from datetime import timedelta

import numpy as np
import pandas as pd
import pytest
import rasterio
from rasterio.transform import from_origin

from ferrydata import geodesy, operational_depth as od
from ferrydata.environment import series

CELL = 1 / 32400  # 1/9 arc-second, the CUDEM lattice
DAY = '2025-07-14'


def _grid(rows=60, cols=60):
	"""A small EPSG:4326 lattice centred on the world origin."""
	transform = from_origin(geodesy.ORIGIN_LON - cols / 2 * CELL, geodesy.ORIGIN_LAT + rows / 2 * CELL, CELL, CELL)
	return transform, (rows, cols)


def _centres(transform, shape):
	rows, cols = np.indices(shape)
	lon = transform.c + (cols + .5) * transform.a
	lat = transform.f + (rows + .5) * transform.e
	return geodesy.to_local(lat, lon)


def _report(north, east, required, radius, **extra):
	return dict(north_m=north, east_m=east, required_bed_navd88_m=required, footprint_radius_m=radius, **extra)


# ---- the classes and identities ------------------------------------------------------------------------------


def test_classes_come_from_the_vessel_files():
	classes = od.vessel_classes()
	assert classes['T'].max_draft_m == 1.9 and classes['K'].max_draft_m == 1.6
	assert classes['T'].footprint_radius_m == pytest.approx(26.0096 / 2)
	assert classes['K'].footprint_radius_m == pytest.approx(29.6 / 2)
	assert od.draft_m(classes, 'unknown') == od.UNKNOWN_CLASS_DRAFT_M == 1.9


def test_only_t_and_k_identities_are_ferries(tmp_path):
	audit = tmp_path / 'audit.csv'
	audit.write_text('mmsi,name,class\n1,A,T\n2,B,K-IC18130\n3,C,unclassified\n4,D,\n')
	assert od.identity_classes(audit) == {1: 'T', 2: 'K'}


# ---- the level model: the core's DataEnvironment, gauge by gauge ----------------------------------------------


def test_a_series_interpolates_up_to_1800_s_and_holds_the_nearer_sample_across_longer_gaps():
	times, values = [0., 360., 720., 2600., 4400.], [1., 2., 4., 8., 6.]
	assert od.series_value(times, values, 180.) == 1.5
	assert od.series_value(times, values, 360.) == 2.
	assert od.series_value(times, values, -50.) == 1., 'before the first sample: that sample'
	assert od.series_value(times, values, 9e9) == 6., 'after the last sample: that sample'
	assert od.series_value(times, values, 1660.) == 4., '1880 s gap: the earlier sample up to and at the midpoint'
	assert od.series_value(times, values, 1661.) == 8.
	assert od.series_value(times, values, 3500.) == 7., 'a gap of exactly 1800 s still interpolates'


def test_the_gauges_blend_by_inverse_distance_squared_not_the_first_gauge():
	assert od.blend(0., 5., [0., 0.], [0., 10.], [0., 1.]) == .5
	assert od.blend(0., 2.5, [0., 0.], [0., 10.], [0., 1.]) == pytest.approx(.1)
	assert od.blend(0., 1e-7, [0., 0.], [0., 10.], [3., 1.]) == 3., 'at a station: its value'
	assert od.blend(500., 500., [0.], [0.], [2.]) == 2., 'one gauge: its value everywhere'


def test_a_day_holds_times_outside_its_coverage_and_samples_every_gauge():
	levels = od.DayLevels([od.Gauge('A', 0., 0., [0., 360.], [0., 1.]), od.Gauge('B', 0., 10., [0., 360.], [2., 3.])], (0., 360.))
	assert levels.level(0., 5., 180.) == pytest.approx(1.5)
	assert levels.level(0., 5., 1e6) == levels.level(0., 5., 360.)
	assert levels.level(0., 5., -1e6) == levels.level(0., 5., 0.)


class _FakeInputs:
	"""The environment build's sources: two gauges with NAVD88, one without, one with no data that day."""
	def __init__(self):
		self.calls = []

	def station(self, station_id):
		return dict(id=station_id, name=station_id, lat=40.7 + int(station_id[-1]) * .01, lng=-74.0)

	def datums(self, station_id):
		return {'datums': [] if station_id == 'G3' else [{'name': 'NAVD88'}]}

	def water_levels(self, station, day, week):
		self.calls.append((station['id'], day, week))
		if station['id'] == 'G4':
			return None, [], ['G4 unavailable'], {}
		value = {'G1': 1., 'G2': 3.}[station['id']]
		north, east = geodesy.to_local(station['lat'], station['lng'])
		return (dict(id=station['id'], north_m=float(north), east_m=float(east)),
			[(station['id'], float(t), value, 'observed') for t in range(-3600, 90001, 360)], [], {})


def test_a_days_gauges_are_the_packs_gauges_in_the_packs_order():
	inputs = _FakeInputs()
	levels = od.day_levels(DAY, ('G1', 'G3', 'G2', 'G4'), inputs)
	assert [g.id for g in levels.gauges] == ['G1', 'G2'], 'no NAVD88 and no data are left out, the order kept'
	assert [c[0] for c in inputs.calls] == ['G1', 'G2', 'G4'], 'a gauge without NAVD88 is never asked for its levels'
	assert inputs.calls[0][2] == '2025-07-14'
	assert levels.coverage == tuple(float(t) for t in series.COVERAGE)
	g1, g2 = levels.gauges
	mid_north, mid_east = (g1.north_m + g2.north_m) / 2, (g1.east_m + g2.east_m) / 2
	assert levels.level(mid_north, mid_east, 0.) == pytest.approx(2.), 'halfway between the gauges: their mean'
	assert levels.level(g1.north_m, g1.east_m, 0.) == 1.


# ---- which reports count ----------------------------------------------------------------------------------


def _tracks(rows):
	"""AIS reports: (mmsi, local metres north, east, SOG m/s, seconds after the day's local midnight)."""
	frame = pd.DataFrame(rows, columns=['mmsi', 'north_m', 'east_m', 'sog_mps', 't_s'])
	epoch = series.epoch(DAY)
	frame['time_utc'] = [pd.Timestamp(epoch + timedelta(seconds=float(t))) for t in frame.t_s]
	frame['lat'], frame['lon'] = geodesy.from_local(frame.north_m.to_numpy(), frame.east_m.to_numpy())
	return frame.drop(columns='t_s')


FACES = [dict(stop_id='8', stop_name='Face A', north_m=0., east_m=0.), dict(stop_id='87', stop_name='Face B', north_m=0., east_m=1000.)]


def test_reports_count_within_200_m_at_most_2_5_m_s_for_t_and_k_on_the_scenario_days_on_the_grid():
	tracks = _tracks([
		(1, 0., 199.99, 2.5, 3600),  # in: on the radius and the SOG limit
		(1, 0., 200.01, 0., 3600),  # out: past the radius
		(1, 0., 50., 2.51, 3600),  # out: too fast (squat)
		(1, 0., 50., np.nan, 3600),  # out: no SOG
		(3, 0., 50., 0., 3600),  # out: not a T or K
		(2, 0., 990., 0., 3600),  # in: the second face, a K
		(1, 0., 50., 0., -7200),  # out: the evening before, not a scenario day
		(1, 500., 1000., 0., 3600),  # out: beyond both faces
		(1, 0., 1100., 0., 3600),  # out: off the depth grid
	])
	inside = lambda lat, lon: geodesy.to_local(lat, lon)[1] < 1050.
	reports, dropped = od.select_reports(tracks, {1: 'T', 2: 'K'}, FACES, inside)
	assert reports[['mmsi', 'vessel_class', 'stop_id']].values.tolist() == [[1, 'T', '8'], [2, 'K', '87']]
	assert reports.day.tolist() == [DAY, DAY]
	assert reports.pack_time_s.tolist() == [3600., 3600.]
	assert dropped == {'not_t_or_k': 1, 'no_position': 0, 'no_sog': 1, 'over_sog_limit': 1, 'beyond_radius': 2, 'off_depth_grid': 1, 'off_scenario_days': 1}


def test_a_report_counts_for_its_nearest_face_once():
	tracks = _tracks([(1, 0., 499., 0., 60), (1, 0., 501., 0., 60)])
	faces = [dict(stop_id='a', stop_name='A', north_m=0., east_m=300.), dict(stop_id='b', stop_name='B', north_m=0., east_m=700.)]
	reports, _ = od.select_reports(tracks, {1: 'T'}, faces, lambda lat, lon: np.ones(len(lat), bool))
	assert reports.stop_id.tolist() == ['a', 'b']


def test_required_bed_is_the_level_less_the_class_draft_and_the_keel_margin():
	tracks = _tracks([(1, 0., 10., 0., 3600), (2, 0., 10., 0., 3600)])
	reports, _ = od.select_reports(tracks, {1: 'T', 2: 'K'}, FACES, lambda lat, lon: np.ones(len(lat), bool))
	levels = {DAY: od.DayLevels([od.Gauge('A', 0., 0., [0., 1800., 3600., 5400.], [-1., -.5, 0., .5])], (-3600., 90000.))}
	beds = od.required_beds(reports, levels, od.vessel_classes())
	assert beds.level_navd88_m.tolist() == [0., 0.], 'the level at the report time, not the day low'
	assert beds.required_bed_navd88_m.tolist() == pytest.approx([-1.9 - .3, -1.6 - .3])
	assert beds.footprint_radius_m.tolist() == pytest.approx([13.0048, 14.8])
	assert beds.keel_margin_m.tolist() == [od.KEEL_MARGIN_M] * 2 == [.3, .3]


# ---- the floor on the grid ----------------------------------------------------------------------------------


def test_a_footprint_covers_water_cells_within_its_radius_only():
	transform, shape = _grid()
	water = np.ones(shape, bool)
	floor = od.floor_grid(pd.DataFrame([_report(0., 0., -3., 13.)]), transform, shape, water)
	north, east = _centres(transform, shape)
	distance = np.hypot(north, east)
	assert np.all(floor[distance <= 12.9] == -3.)
	assert np.all(np.isnan(floor[distance > 13.1])), 'NaN where no report applies'


def test_overlapping_footprints_keep_the_deepest_requirement_in_any_order():
	transform, shape = _grid()
	water = np.ones(shape, bool)
	reports = pd.DataFrame([_report(0., 0., -2.5, 13.), _report(0., 10., -3.5, 13.), _report(0., 5., -2.9, 13.)])
	floor = od.floor_grid(reports, transform, shape, water)
	again = od.floor_grid(reports.iloc[::-1].reset_index(drop=True), transform, shape, water)
	assert floor.tobytes() == again.tobytes()
	north, east = _centres(transform, shape)
	both = (np.hypot(north, east) <= 12.9) & (np.hypot(north, east - 10.) <= 12.9)
	only_first = (np.hypot(north, east) <= 12.9) & (np.hypot(north, east - 10.) > 13.1) & (np.hypot(north, east - 5.) > 13.1)
	assert both.any() and np.all(floor[both] == -3.5)
	assert only_first.any() and np.all(floor[only_first] == -2.5)


def test_land_fixed_structures_and_nodata_are_never_touched_and_no_bed_is_raised():
	transform, shape = _grid()
	bed = np.full(shape, -1., dtype='float32')
	bed[:, :10] = 2.  # a pier the footprint reaches
	bed[0, 20] = -99999.  # nodata in the water
	bed[30, 30] = -5.  # already deeper than the floor
	water = np.ones(shape, bool)
	water[:, :10] = False
	floor = np.full(shape, -3.)
	new, lowered, mask = od.apply_floor(bed, floor, water, -99999.)
	assert new.dtype == np.float32
	assert np.all(new[:, :10] == bed[:, :10]), 'land and fixed structures keep their bed'
	assert new[0, 20] == -99999. and np.isnan(lowered[0, 20]), 'nodata is never filled'
	assert new[30, 30] == -5. and lowered[30, 30] == 0., 'a deeper bed is never raised to the floor'
	assert np.all(new <= bed)
	assert np.all(new[mask] == np.float32(-3.)) and mask.sum() == 60 * 50 - 2


def test_the_float32_bed_is_at_or_below_its_floor_and_the_lowering_is_exact():
	bed = np.full((1, 4), -1., dtype='float32')
	floor = np.array([[-2.846, -3.0000001, -2.2 - 1e-9, -2.95]])
	new, lowered, mask = od.apply_floor(bed, floor, np.ones((1, 4), bool), -99999.)
	assert mask.all()
	assert np.all(new.astype('float64') <= floor), 'the keel margin is kept even after float32 rounding'
	assert np.all(new.astype('float64') + lowered == bed.astype('float64')), 'before = after + lowering, exactly'


# ---- provenance and determinism on a small slice ------------------------------------------------------------


@pytest.fixture
def small_build(tmp_path):
	transform, shape = _grid()
	bed = np.full(shape, -1., dtype='float32')
	bed[:, :8] = 2.
	land = np.zeros(shape, bool)
	land[:, :8] = True
	profile = dict(driver='GTiff', width=shape[1], height=shape[0], count=1, dtype='float32', crs='EPSG:4326',
		transform=transform, nodata=-99999., compress='deflate', tiled=True, blockxsize=256, blockysize=256)
	reports = pd.DataFrame([
		dict(mmsi=1, vessel_class='T', day=DAY, stop_id='8', stop_name='Face A', pack_time_s=3600., level_navd88_m=-.5,
			max_draft_m=1.9, keel_margin_m=.3, **_report(0., 0., -2.7, 13.0048)),
		dict(mmsi=2, vessel_class='K', day='2025-07-15', stop_id='8', stop_name='Face A', pack_time_s=7200., level_navd88_m=1.,
			max_draft_m=1.6, keel_margin_m=.3, **_report(0., -40., -.9, 14.8)),
	])
	return tmp_path, bed, land, profile, reports


def test_outputs_carry_the_method_parameters_counts_and_rasters(small_build):
	out, bed, land, profile, reports = small_build
	new, provenance = od.write_outputs(out, bed, ~land, profile, reports, od.vessel_classes(), sources={'tracks': 'a' * 64})
	with rasterio.open(out / od.FLOOR_FILE) as ds:
		floor = ds.read(1)
		assert ds.tags()['method'] == od.METHOD
		assert np.isnan(ds.nodata)
	with rasterio.open(out / od.LOWERED_FILE) as ds:
		lowered = ds.read(1)
		assert ds.dtypes[0] == 'float64'
	assert np.isnan(floor[land]).all(), 'no floor on land'
	assert np.isnan(floor).any() and np.isfinite(floor).any()
	assert np.array_equal(new.astype('float64') + np.nan_to_num(lowered), bed.astype('float64'))
	assert provenance['method'] == 'operational-ais' and provenance['applied'] is True
	assert provenance['parameters'] == {'radius_m': 200., 'max_sog_mps': 2.5, 'keel_margin_m': .3, 'unknown_class_draft_m': 1.9,
		'max_draft_m': {'K': 1.6, 'T': 1.9}, 'footprint_radius_m': {'K': 14.8, 'T': 13.0048},
		'level_model': provenance['parameters']['level_model']}
	assert 'inverse distance squared' in provenance['parameters']['level_model']
	assert provenance['cells_lowered'] == int((lowered > 0).sum()) > 0
	assert provenance['max_lowering_m'] == pytest.approx(1.7, abs=1e-6), 'from -1.0 to -2.7'
	assert provenance['reports'] == {'total': 2, 'by_class': {'K': 1, 'T': 1}}
	assert provenance['floor_file'] == od.FLOOR_FILE and provenance['lowered_file'] == od.LOWERED_FILE
	assert provenance['sources'] == {'tracks': 'a' * 64}
	assert len(provenance['floor_sha256']) == 64 and len(provenance['lowered_sha256']) == 64
	assert not (bed == new).all(), 'the K report at high water is shallower than the bed and lowers nothing'


def test_two_builds_write_the_same_bytes(small_build, tmp_path):
	out, bed, land, profile, reports = small_build
	od.write_outputs(out, bed, ~land, profile, reports, od.vessel_classes(), sources={})
	first = {name: (out / name).read_bytes() for name in (od.FLOOR_FILE, od.LOWERED_FILE)}
	od.write_outputs(out, bed, ~land, profile, reports.sample(frac=1., random_state=3), od.vessel_classes(), sources={})
	assert first == {name: (out / name).read_bytes() for name in (od.FLOOR_FILE, od.LOWERED_FILE)}


def test_the_landing_report_counts_reports_by_class_and_day_and_the_box_before_and_after(small_build):
	out, bed, land, profile, reports = small_build
	new, provenance = od.write_outputs(out, bed, ~land, profile, reports, od.vessel_classes(), sources={})
	with rasterio.open(out / od.FLOOR_FILE) as ds:
		floor = ds.read(1).astype('float64')
	face = dict(stop_id='8', stop_name='Face A', anchor_ne=(0., 0.), outward_normal_ne=(0., 1.))
	rows = od.landing_rows(reports, bed, new, floor, ~land, profile['transform'], -99999., [face], low=-.9, required=2.1)
	row = rows[0]
	assert row['reports'] == {'total': 2, 'by_class': {'K': 1, 'T': 1}, 'by_day': {DAY: {'T': 1}, '2025-07-15': {'K': 1}}}
	assert row['lowest_level_navd88_m'] == -.5
	assert row['box']['lowest_covering_level_navd88_m'] == -.5
	assert row['cells_lowered'] > 0 and row['max_lowering_m'] == pytest.approx(1.7, abs=1e-6)
	assert 0 < row['mean_lowering_m'] <= row['max_lowering_m']
	assert row['box']['before']['min_depth_m'] == pytest.approx(-.9 + 1.)
	assert row['box']['after']['min_depth_m'] == pytest.approx(-.9 + 1.), 'part of the box is beyond every footprint'
	assert row['box']['uncovered_cells'] > 0
	worst = row['box']['worst_cell']
	assert worst['depth_before_m'] == worst['depth_after_m'] == pytest.approx(.1), 'float64 depths: an unchanged cell reads the same'


# ---- validate: the basis that passes each approach box ----------------------------------------------------------


OFFSET, LOW, REQUIRED = -.846, -.922, 2.1


def _box_raster(path, elevation):
	"""40 x 40 cells around the world origin at one elevation (NAVD88 m), like the landing checks' rasters."""
	transform = from_origin(geodesy.ORIGIN_LON - 20 * CELL, geodesy.ORIGIN_LAT + 20 * CELL, CELL, CELL)
	with rasterio.open(path, 'w', driver='GTiff', width=40, height=40, count=1, dtype='float32', crs='EPSG:4326',
		transform=transform, nodata=-99999.) as dataset:
		dataset.write(np.full((40, 40), elevation, dtype='float32'), 1)
	return path


def _approach(tmp_path, before, chart, after, operational=None):
	from ferrydata import depth, validate
	from shapely.geometry import box
	box_polygon = depth.landing_box_polygon((0., 0.), (1., 0.))
	areas = [(box(-100, -5, 100, 60), {'id': 'DEPARE0', 'feature_class': 'DEPARE', 'min_depth_m': 0., 'max_depth_m': 2.})]
	paths_ = [_box_raster(tmp_path / f'{name}.tif', value) for name, value in (('before', before), ('chart', chart), ('after', after))]
	with rasterio.open(paths_[0]) as b, rasterio.open(paths_[1]) as c, rasterio.open(paths_[2]) as a:
		return validate.approach_depth(a, c, box_polygon, LOW, REQUIRED, OFFSET, areas, [], before_floor=b, operational=operational)


def test_a_box_only_the_floor_deepens_enough_passes_on_the_operational_ais_basis(tmp_path):
	result = _approach(tmp_path, before=-1., chart=OFFSET, after=-3.2)
	assert result['before_floor']['verdict'] == 'FAIL' and result['charted']['verdict'] == 'FAIL'
	assert result['merged']['verdict'] == 'PASS'
	assert result['verdict'] == 'PASS' and result['decided_by'] == 'operational-ais'
	assert 'operational-ais' in result['explanation'] and '0.078 m' in result['explanation']


def test_the_strongest_evidence_names_the_basis(tmp_path):
	assert _approach(tmp_path, before=-3.2, chart=OFFSET, after=-3.2)['decided_by'] == 'merged', 'the chart and CUDEM merge alone'
	charted = _approach(tmp_path, before=-1., chart=OFFSET - 5., after=-3.2)
	assert charted['decided_by'] == 'charted' and charted['verdict'] == 'PASS'


def test_a_box_the_floor_cannot_settle_fails_and_says_why(tmp_path):
	operational = dict(cells=104, covered_cells=101, uncovered_cells=3, failing_cells=3, failing_uncovered_cells=0,
		lowest_covering_level_navd88_m=-.59)
	result = _approach(tmp_path, before=-1., chart=OFFSET, after=-2.84, operational=operational)
	assert result['verdict'] == 'FAIL' and result['decided_by'] == 'neither'
	assert result['before_floor']['min_depth_m'] == pytest.approx(.078, abs=1e-4)
	assert result['merged']['min_depth_m'] == pytest.approx(1.918, abs=1e-4)
	text = result['explanation']
	assert 'operational-ais floor' in text and '0.078 m' in text and '1.918 m' in text
	assert '101 of 104' in text and '-0.59 m' in text and '3 uncovered' in text
	assert 'The 2.10 m requirement is unchanged' in text


def test_without_a_floor_the_basis_is_the_one_it_always_was(tmp_path):
	from ferrydata import depth, validate
	path = _box_raster(tmp_path / 'deep.tif', -9.)
	with rasterio.open(path) as m:
		result = validate.approach_depth(m, m, depth.landing_box_polygon((0., 0.), (1., 0.)), LOW, REQUIRED, OFFSET, [], [])
	assert result['decided_by'] == 'merged' and 'before_floor' not in result


# ---- the environment pack's depth set carries the same provenance -------------------------------------------------


def _source_dir(tmp_path, floor_applied=True):
	from dataclasses import replace
	from ferrydata import slices
	slice_ = replace(slices.LOWER_EAST_RIVER, south=40.6996, north=40.7004, west=-74.0154, east=-74.0146)
	out = tmp_path / 'processed'
	out.mkdir()
	profile = dict(driver='GTiff', width=100, height=100, count=1, crs='EPSG:4326', transform=from_origin(-74.016, 40.701, .00002, .00002))
	for name, value, dtype, nodata in (('depth_merged_navd88.tif', -10., 'float32', -99999.), ('depth_chart_navd88.tif', -9., 'float32', -99999.),
		(od.FLOOR_FILE, -10., 'float32', float('nan')), (od.LOWERED_FILE, 0., 'float64', float('nan'))):
		with rasterio.open(out / name, 'w', dtype=dtype, nodata=nodata, **profile) as ds:
			ds.write(np.full((100, 100), value, dtype=dtype), 1)
			ds.update_tags(vertical_datum='NAVD88')
	from ferrydata import manifest
	operational = dict(method=od.METHOD, applied=True, parameters=dict(radius_m=200., max_sog_mps=2.5, keel_margin_m=.3,
		unknown_class_draft_m=1.9, max_draft_m=dict(K=1.6, T=1.9), footprint_radius_m=dict(K=14.8, T=13.0048), level_model=od.LEVEL_MODEL),
		reports=dict(total=57000, by_class=dict(K=7000, T=50000)), cells_lowered=5891, max_lowering_m=4.13, mean_lowering_m=1.2,
		floor_file=od.FLOOR_FILE, floor_sha256=manifest.sha256_file(out / od.FLOOR_FILE),
		lowered_file=od.LOWERED_FILE, lowered_sha256=manifest.sha256_file(out / od.LOWERED_FILE))
	(out / 'depth_merged_navd88.json').write_text(json.dumps({'operational_ais': operational} if floor_applied else {}))
	(out / 'land_areas.geojson').write_text('{"type":"FeatureCollection","features":[]}')
	return slice_, out


def test_the_depth_set_names_the_floor_its_parameters_and_its_rasters(tmp_path):
	from ferrydata.environment import depth as env_depth
	slice_, out = _source_dir(tmp_path)
	metadata, _ = env_depth.build(slice_, out, tmp_path / 'env' / 'depth')
	sources = {s['name']: s for s in metadata['sources']}
	assert 'operational-ais' in sources['depth_merged_navd88.tif']['note']
	from ferrydata import manifest
	floor = sources[od.FLOOR_FILE]
	assert floor['sha256'] == manifest.sha256_file(out / od.FLOOR_FILE)
	for text in ('operational-ais', '57000 T/K AIS reports', '200 m', '2.5 m/s', 'T 1.9 m', 'K 1.6 m', '0.3 m keel margin', 'an estimate',
		'13.00 m', '14.80 m', '5891 cells lowered', 'largest 4.13 m'):
		assert text in floor['note'], text
	assert sources[od.LOWERED_FILE]['sha256'] == manifest.sha256_file(out / od.LOWERED_FILE)


def test_a_depth_set_refuses_floor_rasters_that_are_not_the_ones_the_merge_recorded(tmp_path):
	from ferrydata.environment import depth as env_depth
	slice_, out = _source_dir(tmp_path)
	with rasterio.open(out / od.FLOOR_FILE, 'r+') as ds:
		ds.write(np.full((100, 100), -11., dtype='float32'), 1)
	with pytest.raises(ValueError, match=od.FLOOR_FILE):
		env_depth.build(slice_, out, tmp_path / 'env' / 'depth')


def test_a_merge_without_the_floor_keeps_the_depth_sets_old_sources(tmp_path):
	from ferrydata.environment import depth as env_depth
	slice_, out = _source_dir(tmp_path, floor_applied=False)
	metadata, _ = env_depth.build(slice_, out, tmp_path / 'env' / 'depth')
	assert [s['name'] for s in metadata['sources']] == ['depth_merged_navd88.tif', 'depth_merged_navd88.json', 'depth_chart_navd88.tif']


def test_the_landing_report_samples_a_box_exactly_as_validate_does(tmp_path):
	from ferrydata import depth, validate
	transform = from_origin(geodesy.ORIGIN_LON - 20 * CELL, geodesy.ORIGIN_LAT + 20 * CELL, CELL, CELL)
	rng = np.random.default_rng(7)
	before = rng.uniform(-4., 0., (40, 40)).astype('float32')
	after = np.minimum(before, -2.5).astype('float32')
	for direction in ((1., 0.), (.6, .8), (-.28, .96)):
		polygon = depth.landing_box_polygon((0., 0.), direction)
		paths_ = []
		for name, grid in (('before', before), ('after', after)):
			with rasterio.open(tmp_path / f'{name}.tif', 'w', driver='GTiff', width=40, height=40, count=1, dtype='float32', crs='EPSG:4326',
				transform=transform, nodata=-99999.) as ds:
				ds.write(grid, 1)
			paths_.append(tmp_path / f'{name}.tif')
		with rasterio.open(paths_[0]) as b, rasterio.open(paths_[1]) as a:
			checked = validate.approach_depth(a, a, polygon, LOW, REQUIRED, OFFSET, [], [], before_floor=b)
		face = dict(stop_id='x', stop_name='X', anchor_ne=(0., 0.), outward_normal_ne=direction)
		box = od.box_result(before, after, np.full((40, 40), np.nan), np.ones((40, 40), bool), transform, -99999., face,
			pd.DataFrame(columns=['north_m', 'east_m', 'footprint_radius_m', 'level_navd88_m']), LOW, REQUIRED)
		assert box['before']['min_depth_m'] == checked['before_floor']['min_depth_m']
		assert box['after']['min_depth_m'] == checked['merged']['min_depth_m']
		assert box['after']['valid_fraction'] == checked['merged']['valid_fraction']
		assert box['cells'] == checked['merged']['cell_count']


@pytest.mark.parametrize('case', ['raised', 'nodata-filled', 'nodata-lost'])
def test_validate_refuses_a_lowering_that_raised_a_bed_or_moved_nodata(tmp_path, case):
	from ferrydata import validate
	merged = np.full((40, 40), -2., dtype='float32')
	merged[0, 0] = -99999.
	lowered = np.zeros((40, 40))
	lowered[0, 0] = np.nan
	if case == 'raised':
		lowered[5, 5] = -.1
	elif case == 'nodata-filled':
		lowered[0, 0] = 0.
	else:
		lowered[6, 6] = np.nan
	transform = from_origin(geodesy.ORIGIN_LON - 20 * CELL, geodesy.ORIGIN_LAT + 20 * CELL, CELL, CELL)
	profile = dict(driver='GTiff', width=40, height=40, count=1, crs='EPSG:4326', transform=transform)
	with rasterio.open(tmp_path / 'm.tif', 'w', dtype='float32', nodata=-99999., **profile) as ds:
		ds.write(merged, 1)
	with rasterio.open(tmp_path / 'l.tif', 'w', dtype='float64', nodata=float('nan'), **profile) as ds:
		ds.write(lowered, 1)
	with rasterio.open(tmp_path / 'm.tif') as m, rasterio.open(tmp_path / 'l.tif') as l:
		with pytest.raises(ValueError):
			with validate.before_floor_dataset(m, l):
				pass


def test_validate_restores_the_bed_before_the_floor_exactly(tmp_path):
	from ferrydata import validate
	before = np.random.default_rng(1).uniform(-5., 1., (40, 40)).astype('float32')
	before[3, 3] = -99999.
	after, lowered, _ = od.apply_floor(before, np.full((40, 40), -2.95), np.ones((40, 40), bool), -99999.)
	transform = from_origin(geodesy.ORIGIN_LON - 20 * CELL, geodesy.ORIGIN_LAT + 20 * CELL, CELL, CELL)
	profile = dict(driver='GTiff', width=40, height=40, count=1, crs='EPSG:4326', transform=transform)
	with rasterio.open(tmp_path / 'm.tif', 'w', dtype='float32', nodata=-99999., **profile) as ds:
		ds.write(after, 1)
	with rasterio.open(tmp_path / 'l.tif', 'w', dtype='float64', nodata=float('nan'), **profile) as ds:
		ds.write(lowered, 1)
	with rasterio.open(tmp_path / 'm.tif') as m, rasterio.open(tmp_path / 'l.tif') as l:
		with validate.before_floor_dataset(m, l) as restored:
			assert restored.read(1).tobytes() == before.tobytes()


# ---- charted hazards keep their bed (coordinator, 2026-10-25) -------------------------------------------------------


def _hazard_dir(tmp_path, transform):
	"""shoreline_constructions.geojson with a submerged pier (WATLEV 3), a submerged line, a dry pier and a dolphin."""
	def lonlat(north, east):
		lat, lon = geodesy.from_local(north, east)
		return [float(lon), float(lat)]
	def square(n0, e0, size):
		return [[lonlat(n0, e0), lonlat(n0, e0 + size), lonlat(n0 + size, e0 + size), lonlat(n0 + size, e0), lonlat(n0, e0)]]
	features = [
		dict(type='Feature', geometry=dict(type='Polygon', coordinates=square(-6., -6., 4.)),
			properties=dict(id='SUBMERGED', feature_class='SLCONS', water_level_effect_code=3, category='pier (jetty)')),
		dict(type='Feature', geometry=dict(type='LineString', coordinates=[lonlat(8., -10.), lonlat(8., 10.)]),
			properties=dict(id='SUBLINE', feature_class='SLCONS', water_level_effect_code=3, category=None)),
		dict(type='Feature', geometry=dict(type='Polygon', coordinates=square(20., 20., 4.)),
			properties=dict(id='DRY', feature_class='SLCONS', water_level_effect_code=2, category='pier (jetty)')),
		dict(type='Feature', geometry=dict(type='Point', coordinates=lonlat(0., 30.)),
			properties=dict(id='DOLPHIN', feature_class='MORFAC', water_level_effect_code=2, category=None)),
	]
	(tmp_path / 'shoreline_constructions.geojson').write_text(json.dumps(dict(type='FeatureCollection', features=features)))
	(tmp_path / 'land_areas.geojson').write_text(json.dumps(dict(type='FeatureCollection', features=[])))
	return tmp_path


def _enc_hazards(*rows):
	import geopandas as gpd
	from shapely.geometry import Point, Polygon
	def poly(n0, e0, size):
		corners = [geodesy.from_local(n, e) for n, e in ((n0, e0), (n0, e0 + size), (n0 + size, e0 + size), (n0 + size, e0))]
		return Polygon([(float(lon), float(lat)) for lat, lon in corners])
	layers = {}
	for layer, lnam, geometry in rows:
		shape_ = poly(*geometry) if isinstance(geometry, tuple) else Point(*[float(v) for v in geodesy.from_local(*geometry)[::-1]])
		layers.setdefault(layer, []).append(dict(LNAM=lnam, WATLEV=3, VALSOU=None, geometry=shape_))
	return {name: gpd.GeoDataFrame(rows_, crs='EPSG:4326') for name, rows_ in layers.items()}


def test_hazards_are_the_submerged_structures_and_the_obstruction_and_wreck_areas(tmp_path, monkeypatch):
	from ferrydata import enc
	transform, shape_ = _grid()
	out = _hazard_dir(tmp_path, transform)
	monkeypatch.setattr(enc, 'read_layers', lambda slice_, names, m=None: (_enc_hazards(
		('OBSTRN', 'OBSAREA', (-20., 10., 4.)), ('OBSTRN', 'OBSPOINT', [-20., -20.]), ('WRECKS', 'WRKAREA', (12., 12., 3.))), {}))
	items = od.hazards(out, slice_=None)
	assert [(h['layer'], h['id']) for h in items] == [('OBSTRN', 'OBSAREA'), ('SLCONS', 'SUBLINE'), ('SLCONS', 'SUBMERGED'), ('WRECKS', 'WRKAREA')], \
		'always-submerged structures and obstruction and wreck areas; not a dry pier, a dolphin or a point obstruction'


def test_a_charted_hazard_keeps_its_bed_and_the_report_says_what_it_would_have_lost(tmp_path, monkeypatch):
	from ferrydata import enc
	transform, shape_ = _grid()
	out = _hazard_dir(tmp_path, transform)
	monkeypatch.setattr(enc, 'read_layers', lambda slice_, names, m=None: (_enc_hazards(('WRECKS', 'WRKAREA', (12., 12., 3.))), {}))
	items = od.hazards(out, slice_=None)
	hazard = od.hazard_mask(items, shape_, transform)
	bed = np.full(shape_, -1., dtype='float32')
	water = ~hazard
	reports = pd.DataFrame([_report(0., 0., -3., 25.)])
	new, lowered, mask = od.apply_floor(bed, od.floor_grid(reports, transform, shape_, water), water, -99999.)
	assert hazard.any() and np.all(new[hazard] == bed[hazard]) and not mask[hazard].any(), 'no hazard cell is lowered'
	records = od.left_out(items, bed, reports, transform, shape_, ~np.zeros(shape_, bool), -99999., [dict(stop_id='8', stop_name='Face A', anchor_ne=(0., 0.))])
	by_id = {r['id']: r for r in records}
	assert set(by_id) == {'SUBMERGED', 'SUBLINE', 'WRKAREA'}
	for record in by_id.values():
		assert record['cells'] > 0 and record['cells_reached'] == record['cells_would_lower'] > 0
		assert record['max_would_lower_m'] == pytest.approx(2., abs=1e-6), 'from -1 m to the -3 m floor'
		assert record['nearest_landing'] == 'Face A' and record['distance_to_face_m'] < 25.


def test_the_depth_set_note_names_the_hazards_the_floor_left_alone(tmp_path):
	from ferrydata.environment import depth as env_depth
	slice_, out = _source_dir(tmp_path)
	metadata = json.loads((out / 'depth_merged_navd88.json').read_text())
	metadata['operational_ais']['left_out'] = {'always-submerged structure': dict(features=101, cells=900, cells_reached=700, cells_would_lower=612, max_would_lower_m=4.13),
		'wreck area': dict(features=2, cells=40, cells_reached=0, cells_would_lower=0, max_would_lower_m=0.)}
	(out / 'depth_merged_navd88.json').write_text(json.dumps(metadata))
	manifest_, _ = env_depth.build(slice_, out, tmp_path / 'env' / 'depth')
	note = next(s['note'] for s in manifest_['sources'] if s['name'] == od.FLOOR_FILE)
	assert 'charted hazards keep their bed (101 always-submerged structures, 2 wreck areas)' in note
