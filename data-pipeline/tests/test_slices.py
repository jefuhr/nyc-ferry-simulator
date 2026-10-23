import pytest
from shapely.geometry import LineString, Point, box

from ferrydata import slices

LER = slices.LOWER_EAST_RIVER


def test_slice_bbox_matches_brief():
	assert LER.bbox == (-74.020, 40.695, -73.960, 40.750)


def test_contains_edges_and_outside():
	assert LER.contains(-74.0, 40.72)
	assert LER.contains(-74.020, 40.695)  # inclusive edge
	assert not LER.contains(-74.0, 40.69)
	assert not LER.contains(-73.9, 40.72)  # lon/lat order matters


def test_clip_point_inside_and_outside():
	assert slices.clip_geometry(Point(-74.0, 40.72), LER) is not None
	assert slices.clip_geometry(Point(-74.0, 40.80), LER) is None


def test_clip_line_crossing_boundary_is_cut_at_edge():
	line = LineString([(-74.05, 40.72), (-73.99, 40.72)])
	clipped = slices.clip_geometry(line, LER)
	assert clipped.bounds[0] == pytest.approx(-74.020)
	assert clipped.bounds[2] == pytest.approx(-73.99)


def test_clip_line_entirely_outside_is_none():
	assert slices.clip_geometry(LineString([(-73.9, 40.6), (-73.8, 40.6)]), LER) is None


def test_clip_polygon_partially_overlapping():
	poly = box(-74.1, 40.7, -74.0, 40.9)
	clipped = slices.clip_geometry(poly, LER)
	assert clipped.bounds == pytest.approx((-74.020, 40.7, -74.0, 40.75))


def test_cudem_tiles_for_slice():
	assert slices.cudem_tile_ids(LER) == ["ncei19_n40x75_w074x00", "ncei19_n40x75_w074x25"]


def test_cudem_tiles_slice_inside_single_tile():
	s = slices.Slice("t", 40.55, 40.60, -73.90, -73.85, (), 0.0, (), "", "")
	assert slices.cudem_tile_ids(s) == ["ncei19_n40x75_w074x00"]
