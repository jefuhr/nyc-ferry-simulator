"""Landing checks in validate: approach depth on charted data, whole structures, and the harbor's landings.

Failure modes: the MLLW to NAVD88 conversion applied with the wrong sign, a 0-2 m chart band read as 2 m of
water, a box that is mostly uncharted passing on a few charted cells, a charted pass that cannot settle a
merged failure, a merged pass undone by the new column, the explanation naming the wrong governing area,
an open or reversed polyline, a segment without kind or provenance, a stale crew label, and a harbor stop
whose structures are missing, duplicated or written into another tile.
"""
import itertools
import json
from types import SimpleNamespace

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin
from shapely.geometry import box, Point

from ferrydata import depth, geodesy, landings, validate

OFFSET = -.846  # MLLW zero in NAVD88 at The Battery
LOW = -.922  # lowest observed water in the window, NAVD88
REQUIRED = 2.1
CELL = 1 / 32400  # 1/9 arc-second
IDS = itertools.count(1000)


def _raster(path, elevations):
	"""A small EPSG:4326 grid around the world origin; elevations[row][col] in NAVD88 metres, NaN for nodata."""
	values = np.where(np.isnan(elevations), -99999., elevations).astype('float32')
	transform = from_origin(geodesy.ORIGIN_LON - 20 * CELL, geodesy.ORIGIN_LAT + 20 * CELL, CELL, CELL)
	with rasterio.open(path, 'w', driver='GTiff', width=values.shape[1], height=values.shape[0], count=1, dtype='float32',
		crs='EPSG:4326', transform=transform, nodata=-99999.) as dataset:
		dataset.write(values, 1)
	return path


def _box():
	return depth.landing_box_polygon((0., 0.), (1., 0.))  # face at the origin, water to the north


def _areas(*bands):
	"""DEPARE polygons in local metres: (south, north, DRVAL1, DRVAL2) bands across the box."""
	return [(box(-100, south, 100, north), {'id': f'DEPARE{i}', 'feature_class': 'DEPARE', 'min_depth_m': d1, 'max_depth_m': d2})
		for i, (south, north, d1, d2) in enumerate(bands)]


def _chart_grid(depth_of_row):
	rows = np.array([[OFFSET - depth_of_row(r)] * 40 for r in range(40)], dtype=float)
	return rows


def test_a_zero_to_two_metre_band_fails_with_its_bound_named(tmp_path):
	chart = _raster(tmp_path / 'chart.tif', _chart_grid(lambda r: 0. if r >= 16 else 5.))  # rows 16-19: the box's first 14 m
	merged = _raster(tmp_path / 'merged.tif', _chart_grid(lambda r: 0. if r >= 16 else 5.))
	with rasterio.open(merged) as m, rasterio.open(chart) as c:
		result = validate.approach_depth(m, c, _box(), LOW, REQUIRED, OFFSET, _areas((-5, 15, 0., 2.), (15, 60, 5., 10.)), [])
	assert result['charted']['min_depth_m'] == pytest.approx(LOW - OFFSET, abs=1e-5), 'depth = water level - (MLLW offset - charted depth)'
	assert result['charted']['verdict'] == 'FAIL'
	assert result['verdict'] == 'FAIL'
	assert result['governing'][0]['id'] == 'DEPARE0'
	assert '0-2 m' in result['explanation'] and '-0.076 m' in result['explanation']
	assert '5-10 m over' in result['explanation'] and '4.924 m at the lowest water, enough' in result['explanation']
	assert result['areas'][0]['box_fraction'] == pytest.approx(.5, abs=.05)


def test_a_deep_charted_box_passes_and_reports_both_columns(tmp_path):
	chart = _raster(tmp_path / 'chart.tif', _chart_grid(lambda r: 5.))
	merged = _raster(tmp_path / 'merged.tif', _chart_grid(lambda r: 8.))
	with rasterio.open(merged) as m, rasterio.open(chart) as c:
		result = validate.approach_depth(m, c, _box(), LOW, REQUIRED, OFFSET, _areas((-5, 60, 5., 10.)), [])
	assert result['merged']['verdict'] == 'PASS' and result['charted']['verdict'] == 'PASS'
	assert result['verdict'] == 'PASS'
	assert result['charted']['min_depth_m'] == pytest.approx(LOW - OFFSET + 5., abs=1e-5)


def test_a_two_metre_lower_bound_still_misses_two_point_one_at_the_lowest_water(tmp_path):
	chart = _raster(tmp_path / 'chart.tif', _chart_grid(lambda r: 2.))
	with rasterio.open(chart) as c:
		result = validate.approach_depth(c, c, _box(), LOW, REQUIRED, OFFSET, _areas((-5, 60, 2., 5.)), [])
	assert result['charted']['min_depth_m'] == pytest.approx(1.924, abs=1e-4)
	assert result['verdict'] == 'FAIL', 'the threshold is never relaxed'


def test_a_mostly_uncharted_box_cannot_pass_on_a_few_charted_cells(tmp_path):
	grid = _chart_grid(lambda r: 6.)
	grid[:, 18:] = np.nan  # two thirds of the box uncharted
	chart = _raster(tmp_path / 'chart.tif', grid)
	merged = _raster(tmp_path / 'merged.tif', _chart_grid(lambda r: -3.))
	with rasterio.open(merged) as m, rasterio.open(chart) as c:
		result = validate.approach_depth(m, c, _box(), LOW, REQUIRED, OFFSET, _areas((-5, 60, 5., 10.)), [])
	assert result['charted']['valid_fraction'] < .5
	assert result['charted']['verdict'] == 'FAIL'
	assert result['verdict'] == 'FAIL'
	assert 'charted' in result['explanation'] and '50%' in result['explanation']


def test_charted_depth_settles_a_cudem_only_failure_and_never_undoes_a_merged_pass(tmp_path):
	chart = _raster(tmp_path / 'chart.tif', _chart_grid(lambda r: 6.))
	shallow = _raster(tmp_path / 'shallow.tif', _chart_grid(lambda r: -1.))  # CUDEM smears a pier into the box
	deep = _raster(tmp_path / 'deep.tif', _chart_grid(lambda r: 9.))
	zero = _raster(tmp_path / 'zero.tif', _chart_grid(lambda r: 0.))
	areas = _areas((-5, 60, 5., 10.))
	with rasterio.open(shallow) as m, rasterio.open(chart) as c:
		settled = validate.approach_depth(m, c, _box(), LOW, REQUIRED, OFFSET, areas, [])
	assert settled['merged']['verdict'] == 'FAIL' and settled['charted']['verdict'] == 'PASS'
	assert settled['verdict'] == 'PASS' and settled['decided_by'] == 'charted'
	with rasterio.open(deep) as m, rasterio.open(zero) as c:
		kept = validate.approach_depth(m, c, _box(), LOW, REQUIRED, OFFSET, _areas((-5, 60, 0., 2.)), [])
	assert kept['merged']['verdict'] == 'PASS' and kept['verdict'] == 'PASS' and kept['decided_by'] == 'merged'


def test_soundings_in_and_near_the_box_are_listed(tmp_path):
	chart = _raster(tmp_path / 'chart.tif', _chart_grid(lambda r: 0.))
	soundings = [(Point(0, 10), {'id': 'S1', 'depth_m': 1.2}), (Point(0, 50), {'id': 'S2', 'depth_m': 5.7}), (Point(0, 500), {'id': 'S3', 'depth_m': 9.})]
	with rasterio.open(chart) as c:
		result = validate.approach_depth(c, c, _box(), LOW, REQUIRED, OFFSET, _areas((-5, 60, 0., 2.)), soundings)
	assert [s['id'] for s in result['soundings_in_box']] == ['S1']
	assert [s['id'] for s in result['soundings_nearby']] == ['S2']
	assert result['soundings_nearby'][0]['distance_m'] == pytest.approx(20., abs=.01)


def _structures(polylines, left_out=()):
	return {'radius_m': landings.STRUCTURE_RADIUS_M, 'left_out': list(left_out),
		'features': [{'geometry': {'type': 'LineString', 'coordinates': []}, 'local_coordinates': [[n, e] for e, n in coords], 'properties': props}
			for coords, props in polylines]}


def _segment(index, kind, **extra):
	record = {'index': index, 'id': next(IDS), 'kind': kind, 'source': 'enc-feature', 'confidence': 'medium',
		'verification': 'needs-crew-verification', 'water_side_verified_by_pipeline': True, 'layer': 'PONTON', 'lnam': 'B',
		'solid_side_evidence': {'verified': True, 'method': 'enc-closed-outline', 'layer': 'PONTON', 'lnam': 'B', 'winding_check': True,
			'probe_ne_m': [0, 0], 'probe_lonlat': [0, 0], 'probe_in_charted_water': True, 'probe_water_layer': 'DEPARE', 'probe_water_lnam': 'W1', 'probe_in_lndare': False}}
	record.update(extra)
	return record


def test_structure_check_accepts_a_closed_water_left_barge_and_rejects_a_reversed_one():
	ring = [(0, 0), (0, 10), (30, 10), (30, 0), (0, 0)]  # clockwise in (east, north): solid on the right
	good = _structures([(ring[:3], {'stop_id': 'S', 'polyline_id': 'landing-S', 'role': 'landing', 'segments': [_segment(0, 'barge-end'), _segment(1, 'berth-face', primary_face=True)]}),
		(ring[2:], {'stop_id': 'S', 'polyline_id': 'landing-S-back', 'role': 'landing', 'segments': [_segment(0, 'barge-end'), _segment(1, 'barge-side')]})])
	assert validate.structure_errors(good, {'S': {'contact_segment_evidence': [{'index': 1, 'on_primary_face': True}, {'index': 0, 'on_primary_face': False}]}}) == []
	reversed_ring = list(reversed(ring))
	bad = _structures([(reversed_ring[:3], {'stop_id': 'S', 'polyline_id': 'landing-S', 'role': 'landing', 'segments': [_segment(0, 'barge-side'), _segment(1, 'barge-end')]}),
		(reversed_ring[2:], {'stop_id': 'S', 'polyline_id': 'landing-S-back', 'role': 'landing', 'segments': [_segment(0, 'barge-side'), _segment(1, 'barge-end')]})])
	errors = validate.structure_errors(bad, {})
	assert any('solid on the left' in error for error in errors)


def test_structure_check_requires_kind_provenance_and_the_label_rule():
	line = [(0, 0), (0, 10), (30, 10)]
	missing = _segment(0, 'barge-end'); missing.pop('confidence')
	stale = _segment(1, 'berth-face', primary_face=True, water_side_crew_reported=True, crew_evidence={'note_id': 'n', 'kind': 'face-orientation'})
	errors = validate.structure_errors(_structures([(line, {'stop_id': 'S', 'polyline_id': 'landing-S', 'role': 'landing', 'segments': [missing, stale]})]), {})
	assert any('confidence' in error for error in errors)
	assert any('crew-reported' in error for error in errors), 'a crew-reported primary face must say so'
	unknown = _segment(0, 'wharf')
	assert any('kind' in error for error in validate.structure_errors(_structures([(line[:2], {'stop_id': 'S', 'polyline_id': 'fixed-S-0', 'role': 'fixed-structure', 'segments': [unknown]})]), {}))


def test_structure_check_rejects_a_point_or_open_line_object_as_contact():
	line = [(0, 0), (0, 10)]
	segment = _segment(0, 'pier', layer='MORFAC', lnam='DOLPHIN')
	errors = validate.structure_errors(_structures([(line, {'stop_id': 'S', 'polyline_id': 'fixed-S-0', 'role': 'fixed-structure', 'segments': [segment]})],
		[{'stop_id': 'S', 'lnam': 'DOLPHIN', 'layer': 'MORFAC', 'reason': 'point object: no charted extent, not contact geometry'}]), {})
	assert any('DOLPHIN' in error for error in errors)


def test_harbor_ownership_rejects_a_stop_written_into_another_tile():
	owned = {'tile_0_0': ['8'], 'tile_0_1': ['19']}
	written = {'tile_0_0': {'8'}, 'tile_0_1': {'19', '8'}}
	errors = validate.landing_ownership_errors(owned, written, {'tile_0_0': [1, 2], 'tile_0_1': [3, 2]})
	assert any("stop 8" in error and 'tile_0_1' in error for error in errors)
	assert any('id 2' in error for error in errors)
	assert validate.landing_ownership_errors(owned, {'tile_0_0': {'8'}, 'tile_0_1': {'19'}}, {'tile_0_0': [1, 2], 'tile_0_1': [3]}) == []
