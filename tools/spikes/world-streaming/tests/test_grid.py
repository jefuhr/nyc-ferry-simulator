"""Harbor tiles v1 grid and the per-tile frames the spike places geometry in.

Failure modes guarded:
- a point lands in the wrong tile (off-by-one at negative indices, north/east swapped);
- the grid does not span the brief's extent (36 tiles, St. George, Bay Ridge and Ferry Point Park inside, Rockaway outside);
- a tile's ENU frame or its 3D Tiles transform does not map local metres back to the right place on the globe;
- curvature is ignored (tiles placed flat in the world tangent plane).
"""

import numpy as np
import pytest

from ferrydata import geodesy
import spike_grid as grid


def test_extent_is_36_tiles_with_signed_ids():
	tiles = grid.all_tiles()
	assert len(tiles) == 36
	assert tiles[0] == (-2, -2) and tiles[-1] == (3, 3)
	assert grid.tile_id(-2, 3) == "tile_-2_3"
	assert grid.tile_id(0, 0) == "tile_0_0"


@pytest.mark.parametrize("lat,lon,expected", [
	(40.70316, -74.00614, "tile_0_0"),     # Wall St/Pier 11
	(40.64607, -74.07423, "tile_-2_-2"),   # St. George
	(40.63986, -74.03813, "tile_-2_-1"),   # Bay Ridge
	(40.80546, -73.83731, "tile_2_3"),     # Ferry Point Park
	(40.77172, -73.93571, "tile_1_1"),     # Astoria, 33 m short of the tile_2_1 edge
	(40.76032, -74.00408, "tile_1_0"),     # Midtown West / Pier 79
])
def test_owner_of_landings(lat, lon, expected):
	north, east = geodesy.to_local(lat, lon)
	assert grid.tile_id(*grid.owner(float(north), float(east))) == expected


def test_owner_edges_are_half_open():
	assert grid.owner(0.0, 0.0) == (0, 0)
	assert grid.owner(-0.001, -0.001) == (-1, -1)
	assert grid.owner(3999.999, 4000.0) == (0, 1)


def test_rockaway_is_outside():
	north, east = geodesy.to_local(40.58423, -73.83160)
	assert not grid.in_extent(*grid.owner(float(north), float(east)))


def test_bounds_and_margin():
	assert grid.bounds(-2, 3) == (-8000.0, -4000.0, 12000.0, 16000.0)
	assert grid.grown_bounds(0, 0) == (-250.0, 4250.0, -250.0, 4250.0)


def test_frame_round_trips_geodetic_points():
	frame = grid.TileFrame(1, 2)
	north = np.array([4000.0, 6000.0, 7999.0])
	east = np.array([8000.0, 10000.0, 11999.0])
	height = np.array([-40.0, 0.0, 250.0])
	enu = frame.enu(north, east, height)
	ecef = frame.ecef_from_enu(enu)
	lat, lon = geodesy.from_local(north, east)
	expected = np.stack(geodesy.to_ecef(lat, lon, height), axis=-1)
	assert np.abs(ecef - expected).max() < 1e-3


def test_frame_centre_is_its_origin():
	frame = grid.TileFrame(-2, -2)
	enu = frame.enu(np.array([frame.centre_north]), np.array([frame.centre_east]), np.array([0.0]))
	assert np.abs(enu).max() < 1e-6


def test_tiles_transform_matches_frame():
	frame = grid.TileFrame(3, -1)
	m = np.array(frame.tiles_transform()).reshape(4, 4).T  # 3D Tiles stores column-major
	enu = frame.enu(np.array([13000.0]), np.array([-3500.0]), np.array([12.0]))
	ecef = (m @ np.array([*enu[0], 1.0]))[:3]
	assert np.abs(ecef - frame.ecef_from_enu(enu)[0]).max() < 1e-3


def test_curvature_is_not_flat():
	# 16 km out the ellipsoid is ~20 m below a plane through the origin, and a tile's own frame keeps
	# the drop to well under a metre inside the tile.
	far = grid.TileFrame(3, 3)
	_, _, down = geodesy.to_ned(*geodesy.from_local(far.centre_north, far.centre_east))
	assert float(down) > 15.0
	corner = far.enu(np.array([far.north0]), np.array([far.east0]), np.array([0.0]))
	assert -1.0 < corner[0, 2] < 0.0
