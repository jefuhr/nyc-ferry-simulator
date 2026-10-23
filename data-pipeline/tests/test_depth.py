import numpy as np
import pytest
import rasterio
from rasterio.io import MemoryFile
from rasterio.transform import from_origin
from shapely.geometry import box

from ferrydata import depth, geodesy

NODATA = -99999.0
# 1e-4 degree pixels (about 11 m north-south, 8 m east-west) keep the arithmetic easy to follow.
RES = 1e-4
ORIGIN_LON, ORIGIN_LAT = -74.0, 40.72


def _raster(values):
	return np.asarray(values, dtype="float32"), from_origin(ORIGIN_LON, ORIGIN_LAT, RES, RES)


def test_landing_box_is_30_by_30_and_sits_off_the_face():
	anchor = (100.0, 200.0)  # north_m, east_m
	outward = (1.0, 0.0)  # face looks north
	corners = depth.landing_box_local(anchor, outward, size_m=30.0)
	norths = [c[0] for c in corners]
	easts = [c[1] for c in corners]
	assert min(norths) == pytest.approx(100.0) and max(norths) == pytest.approx(130.0)
	assert min(easts) == pytest.approx(185.0) and max(easts) == pytest.approx(215.0)


def test_landing_box_rotates_with_face_direction():
	corners = depth.landing_box_local((0.0, 0.0), (0.0, -1.0), size_m=30.0)  # face looks west
	easts = [c[1] for c in corners]
	norths = [c[0] for c in corners]
	assert min(easts) == pytest.approx(-30.0) and max(easts) == pytest.approx(0.0)
	assert min(norths) == pytest.approx(-15.0) and max(norths) == pytest.approx(15.0)


def test_box_polygon_lonlat_has_expected_area():
	poly = depth.landing_box_polygon((0.0, 0.0), (1.0, 0.0), 30.0)
	assert abs(poly.centroid.y - geodesy.from_local(15.0, 0.0)[0]) < 5e-5
	# round trip the corners through local metres: 30 m x 30 m
	pts = [geodesy.to_local(lat, lon) for lon, lat in poly.exterior.coords[:4]]
	side = np.hypot(pts[1][0] - pts[0][0], pts[1][1] - pts[0][1])
	assert side == pytest.approx(30.0, abs=0.05)


def test_sample_polygon_returns_cells_whose_centres_are_inside():
	values, transform = _raster(np.arange(100, dtype="float32").reshape(10, 10))
	# Cover columns 2..3 and rows 4..5 (pixel centres at +0.5).
	poly = box(ORIGIN_LON + 2 * RES, ORIGIN_LAT - 6 * RES, ORIGIN_LON + 4 * RES, ORIGIN_LAT - 4 * RES)
	got = depth.sample_polygon(values, transform, poly, NODATA)
	assert sorted(got.values.tolist()) == [42.0, 43.0, 52.0, 53.0]
	assert got.nodata_count == 0


def test_nodata_is_counted_and_excluded():
	values, transform = _raster(np.full((10, 10), -5.0))
	values[4, 2] = NODATA
	poly = box(ORIGIN_LON + 2 * RES, ORIGIN_LAT - 6 * RES, ORIGIN_LON + 4 * RES, ORIGIN_LAT - 4 * RES)
	got = depth.sample_polygon(values, transform, poly, NODATA)
	assert got.nodata_count == 1
	assert len(got.values) == 3


def test_polygon_outside_raster_is_empty_not_a_crash():
	values, transform = _raster(np.zeros((10, 10)))
	far = box(-73.0, 40.0, -72.99, 40.01)
	got = depth.sample_polygon(values, transform, far, NODATA)
	assert len(got.values) == 0 and got.cell_count == 0


def test_tiny_polygon_still_samples_the_cell_it_touches():
	values, transform = _raster(np.full((10, 10), -3.0))
	tiny = box(ORIGIN_LON + 1.4 * RES, ORIGIN_LAT - 3.6 * RES, ORIGIN_LON + 1.6 * RES, ORIGIN_LAT - 3.4 * RES)
	got = depth.sample_polygon(values, transform, tiny, NODATA)
	assert got.values.tolist() == [-3.0]


def test_depth_is_water_level_minus_elevation():
	stats = depth.depth_stats(np.array([-4.0, -3.0, -2.5, 0.2]), water_level_m=-0.8)
	# depths: 3.2, 2.2, 1.7, -1.0 (the last cell is dry at this water level)
	assert stats["min_depth_m"] == pytest.approx(-1.0)
	assert stats["max_depth_m"] == pytest.approx(3.2)
	assert stats["dry_cells"] == 1
	assert stats["cells"] == 4


def test_depth_stats_empty():
	stats = depth.depth_stats(np.array([]), water_level_m=0.0)
	assert stats["min_depth_m"] is None and stats["cells"] == 0


def test_read_polygon_from_dataset_window():
	data = np.full((20, 20), -6.0, dtype="float32")
	data[10, 10] = -2.0
	with MemoryFile() as mem:
		with mem.open(driver="GTiff", height=20, width=20, count=1, dtype="float32", crs="EPSG:4269", transform=from_origin(ORIGIN_LON, ORIGIN_LAT, RES, RES), nodata=NODATA) as dst:
			dst.write(data, 1)
		with mem.open() as ds:
			poly = box(ORIGIN_LON + 9 * RES, ORIGIN_LAT - 12 * RES, ORIGIN_LON + 12 * RES, ORIGIN_LAT - 9 * RES)
			got = depth.read_polygon(ds, poly)
	assert len(got.values) == 9
	assert got.values.max() == pytest.approx(-2.0)
