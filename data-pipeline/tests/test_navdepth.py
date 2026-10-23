"""Failure modes: datum sign/units, unknown datum, deeper-than-chart edits, nodata and buffer leakage."""
import numpy as np
import pytest
from rasterio.transform import from_origin
from shapely.geometry import box

from ferrydata import navdepth


def test_mllw_to_navd88_uses_station_datum_difference_not_tide_height():
	payload = {'units': 'meters', 'datums': [{'name': 'MLLW', 'value': 1.002}, {'name': 'NAVD88', 'value': 1.848}]}
	offset = navdepth.datum_offset(payload)
	assert offset == pytest.approx(-.846)
	assert navdepth.chart_elevation(2.0, offset) == pytest.approx(-2.846)
	assert -.8 - navdepth.chart_elevation(2.0, offset) == pytest.approx(2.046)


@pytest.mark.parametrize('payload', [{'units': 'feet', 'datums': []}, {'units': 'meters', 'datums': [{'name': 'MLLW', 'value': 1}]}])
def test_missing_or_wrong_units_fail_closed(payload):
	with pytest.raises(ValueError):
		navdepth.datum_offset(payload)


def test_merge_changes_only_deeper_chart_values_inside_buffer_and_keeps_nodata():
	cudem = np.array([[-1., -5., -1., -99999., -1., -1.]])
	chart = np.array([[-3., -3., -3., -3., np.nan, -3.]])
	allowed = np.array([[True, True, False, True, True, True]])
	merged, changed, flagged = navdepth.merge_elevations(cudem, chart, allowed, -99999.)
	assert merged.tolist() == [[-3., -5., -1., -99999., -1., -3.]]
	assert changed.tolist() == [[True, False, False, False, False, True]]
	assert flagged.tolist() == [[True, True, False, False, False, True]]
	assert np.all(merged[changed] >= chart[changed])  # never deeper than the chart
	assert np.all(merged[changed] <= cudem[changed])  # never shallower than CUDEM


def test_overlapping_depth_areas_use_shallowest_minimum_regardless_of_order():
	transform = from_origin(0, 2, 1, 1)
	areas = [(box(0, 0, 2, 2), 5.), (box(0, 0, 1, 2), 2.)]
	a = navdepth.rasterize_chart(areas, [], (2, 2), transform, -.846)
	b = navdepth.rasterize_chart(list(reversed(areas)), [], (2, 2), transform, -.846)
	np.testing.assert_array_equal(a, b)
	np.testing.assert_allclose(a, [[-2.846, -5.846], [-2.846, -5.846]])


def test_sounding_is_only_supported_in_its_own_cell_and_shoal_wins():
	from shapely.geometry import Point
	transform = from_origin(0, 2, 1, 1)
	chart = navdepth.rasterize_chart([(box(0, 0, 2, 2), 5.)], [(Point(.5, 1.5), 3.), (Point(.5, 1.5), 8.)], (2, 3), transform, -.846)
	assert chart[0, 0] == pytest.approx(-3.846)
	assert chart[0, 1] == pytest.approx(-5.846)
	assert np.isnan(chart[0, 2])


@pytest.mark.parametrize("bound", [-2.846, -3.846, -4.846, -9.846])
def test_float32_rounding_never_deepens_past_double_precision_chart_bound(bound):
	cudem = np.array([[-1.]], dtype='float32')
	chart = np.array([[bound]])
	merged, _, _ = navdepth.merge_elevations(cudem, chart, np.array([[True]]), -99999.)
	assert float(merged[0, 0]) >= chart[0, 0]
	assert merged[0, 0] <= cudem[0, 0]


def test_measured_sounding_replaces_area_lower_bound_only_in_its_sample_cell():
	from shapely.geometry import Point
	chart = navdepth.rasterize_chart([(box(0, 0, 2, 2), 0.)], [(Point(.5, 1.5), 5.)], (2, 2), from_origin(0, 2, 1, 1), -.846)
	assert chart[0, 0] == pytest.approx(-5.846)
	assert chart[0, 1] == pytest.approx(-.846)
