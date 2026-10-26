"""Tile frames, the height datum and edge ownership for the harbor's 3D Tiles (ferrydata.world3d.frames, heights).

Failure modes guarded:
- a wrong ENU/ECEF frame or axis order: the frame's origin, its axes, the 3D Tiles transform's column order, and
  glTF's y-up axes against 3D Tiles' z-up;
- curvature ignored (geometry placed flat in a tangent plane instead of through ECEF);
- heights in the wrong datum: NAVD88 written as ellipsoidal, the geoid's sign, or the geoid not interpolated from the
  owning tile's corners;
- an edge owned by both neighbours, or by neither, so their shared vertices come from different rasters (a crack);
- a raster read with the wrong cell convention, or with nodata turned into a height.
"""

import math

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin

from ferrydata import geodesy
from ferrydata.world3d import frames, heights


def test_frame_origin_is_the_tile_centre_on_the_ellipsoid():
	frame = frames.TileFrame(0, 0)
	lat, lon = geodesy.from_local(2000.0, 2000.0)
	assert frame.latitude == pytest.approx(float(lat), abs=1e-12)
	assert frame.longitude == pytest.approx(float(lon), abs=1e-12)
	assert np.allclose(frame.origin, geodesy.to_ecef(lat, lon, 0.0), atol=1e-9, rtol=0)


def test_frame_axes_are_east_north_up():
	frame = frames.TileFrame(1, 2)
	assert np.allclose(frame.rotation @ frame.rotation.T, np.eye(3), atol=1e-15)
	assert np.linalg.det(frame.rotation) == pytest.approx(1.0, abs=1e-15)
	# 10 m east, north and up of the origin, through geodesy: each lands on its own axis.
	lat, lon = frame.latitude, frame.longitude
	metre_lat = 1.0 / 111_000.0
	metre_lon = metre_lat / math.cos(math.radians(lat))
	east = frame.enu(np.array(geodesy.to_ecef(lat, lon + 10 * metre_lon, 0.0)))
	north = frame.enu(np.array(geodesy.to_ecef(lat + 10 * metre_lat, lon, 0.0)))
	up = frame.enu(np.array(geodesy.to_ecef(lat, lon, 10.0)))
	assert east[0] > 9.0 and abs(east[1]) < 0.05 and abs(east[2]) < 0.01
	assert north[1] > 9.0 and abs(north[0]) < 0.05 and abs(north[2]) < 0.01
	assert np.allclose(up, [0.0, 0.0, 10.0], atol=1e-9)


def test_tiles_transform_is_column_major_enu_to_ecef():
	frame = frames.TileFrame(3, -1)
	matrix = np.array(frame.transform()).reshape(4, 4).T  # 3D Tiles stores the 4x4 column-major
	assert np.allclose(matrix[3], [0, 0, 0, 1])
	assert np.allclose(matrix[:3, 0], frame.east) and np.allclose(matrix[:3, 1], frame.north)
	assert np.allclose(matrix[:3, 2], frame.up) and np.allclose(matrix[:3, 3], frame.origin)
	lat, lon = geodesy.from_local(13000.0, -3500.0)
	ecef = np.array(geodesy.to_ecef(lat, lon, -20.0))
	enu = frame.enu(ecef)
	assert np.abs((matrix @ np.append(enu, 1.0))[:3] - ecef).max() < 1e-6


def test_gltf_axes_are_y_up_and_round_trip():
	enu = np.array([[1.0, 2.0, 3.0], [-4.0, 5.0, -6.0]])
	gltf = frames.gltf_from_enu(enu)
	assert np.array_equal(gltf, [[1.0, 3.0, -2.0], [-4.0, -6.0, -5.0]])  # x east, y up, z south
	assert np.array_equal(frames.enu_from_gltf(gltf), enu)
	# The 3D Tiles 1.1 y-up to z-up rotation takes glTF content back to the tile frame.
	assert np.allclose(gltf @ frames.Y_UP_TO_Z_UP.T, enu)
	assert np.linalg.det(frames.Y_UP_TO_Z_UP) == pytest.approx(1.0)  # a rotation keeps the triangles' winding


def test_curvature_is_in_the_frame():
	# The ellipsoid falls away from the tile's tangent plane: about 0.6 m at a 4 km tile's corner.
	frame = frames.TileFrame(3, 3)
	lat, lon = geodesy.from_local(frame.north0, frame.east0)
	corner = frame.enu(np.array(geodesy.to_ecef(lat, lon, 0.0)))
	assert -0.8 < corner[2] < -0.4
	# The tile's north is not the world frame's: meridians converge by about 0.1 degree 14 km east of the origin.
	assert corner[0] == pytest.approx(-2000.0, abs=5.0) and corner[1] == pytest.approx(-2000.0, abs=5.0)
	assert np.hypot(corner[0], corner[1]) == pytest.approx(2000.0 * math.sqrt(2.0), abs=0.5)


def test_region_covers_its_points_with_padding():
	lat = np.array([40.70, 40.71, 40.72])
	lon = np.array([-74.02, -74.00, -74.01])
	h = np.array([-60.0, 5.0, -10.0])
	west, south, east, north, low, high = frames.region(lat, lon, h)
	assert west < math.radians(-74.02) and east > math.radians(-74.00)
	assert south < math.radians(40.70) and north > math.radians(40.72)
	assert low < -60.0 and high > 5.0
	assert east - math.radians(-74.00) < 1e-6 and low > -62.0


def test_lattice_coordinates_land_exactly_on_tile_edges():
	assert heights.L0_CELLS_PER_TILE == 336
	for i in range(-2, 5):
		assert heights.coordinate(heights.L0_CELLS_PER_TILE * i) == 4000.0 * i
	assert heights.coordinate(21) == 250.0  # the harbor tiles' margin is a whole number of cells
	assert heights.coordinate(1) == pytest.approx(4000.0 / 336.0)


ACTIVE = {(0, 0), (0, 1), (1, 0)}  # (1, 1) inactive


@pytest.mark.parametrize("north,east,expected", [
	(10.0, 10.0, (0, 0)),
	(0.0, 0.0, (0, 0)),            # the south-west corner belongs to the tile
	(4000.0, 10.0, (1, 0)),        # a tile owns its south edge
	(10.0, 4000.0, (0, 1)),        # and its west edge
	(4000.0, 4000.0, (0, 0)),      # (1, 1) is inactive: three active tiles touch the corner; lowest (i, j)
	(4100.0, 4100.0, (0, 1)),      # inside the inactive tile, 100 m from (1, 0) and (0, 1): lowest (i, j)
	(4300.0, 4100.0, (1, 0)),      # 100 m east of (1, 0), 300 m north of (0, 1): the nearer
	(8000.0, 2000.0, (1, 0)),      # on the outer edge of the active area
	(8200.0, 2000.0, (1, 0)),      # within (1, 0)'s 250 m margin
	(8300.0, 2000.0, None),        # beyond every margin
	(-0.5, 10.0, (0, 0)),          # inside (-1, 0), which is inactive: the margin of (0, 0)
])
def test_owner_rule(north, east, expected):
	owners = heights.Owners(ACTIVE)
	i, j = owners.owner(np.array([north]), np.array([east]))
	assert (None if i[0] == heights.NO_TILE else (int(i[0]), int(j[0]))) == expected


def test_owner_rule_gives_a_shared_edge_one_owner_per_point():
	# Along north = 4000 m: (1, 0) owns its south edge; at the corner and beyond, (1, 1) is inactive and the touching
	# active tiles decide, the same for every tile that asks.
	owners = heights.Owners(ACTIVE)
	g = np.arange(0, 2 * 336 + 1)
	i, j = owners.owner(heights.coordinate(np.full(g.shape, 336)), heights.coordinate(g))
	assert (i[:336] == 1).all() and (j[:336] == 0).all()
	assert (i[336], j[336]) == (0, 0)
	assert (i[337:] == 0).all() and (j[337:] == 1).all()


def write_raster(path, values, west, north, cell, crs="EPSG:4269", nodata=-99999.0):
	with rasterio.open(path, "w", driver="GTiff", width=values.shape[1], height=values.shape[0], count=1, dtype="float32",
			crs=crs, transform=from_origin(west, north, cell, cell), nodata=nodata) as ds:
		ds.write(values.astype("float32"), 1)
		ds.update_tags(vertical_datum="NAVD88")


def test_raster_sampling_is_strict_bilinear_at_cell_centres(tmp_path):
	cell = 1.0 / 9.0 / 3600.0
	values = np.arange(20, dtype=float).reshape(4, 5) * 2.0 - 5.0  # row r, column c holds 10 r + 2 c - 5
	values[3, 4] = -99999.0
	path = tmp_path / "r.tif"
	write_raster(path, values, -74.0, 40.7, cell)
	raster = heights.Raster.read(path)

	def at(row, col):
		return 40.7 - (row + 0.5) * cell, -74.0 + (col + 0.5) * cell

	# A cell centre gives the cell's value; halfway between centres the mean; a nodata neighbour or a point outside
	# the cell centres gives nodata, never an invented height.
	points = [at(1, 1), at(1, 1.5), at(1.5, 1), at(1.25, 2.75), at(2.5, 3.5), at(-0.2, 2), at(1, 4.2)]
	sample = raster.sample(np.array([p[0] for p in points]), np.array([p[1] for p in points]))
	assert sample[0] == pytest.approx(7.0, abs=1e-6)
	assert sample[1] == pytest.approx(8.0, abs=1e-6)
	assert sample[2] == pytest.approx(12.0, abs=1e-6)
	assert sample[3] == pytest.approx(10 * 1.25 + 2 * 2.75 - 5, abs=1e-6)
	assert np.isnan(sample[4]) and np.isnan(sample[5]) and np.isnan(sample[6])


def test_raster_sampling_matches_the_environment_packs_depth_sampling():
	# The terrain samples the merged raster exactly as ferrydata.environment samples the core's depth grid.
	from ferrydata.environment import depth as environment_depth
	rng = np.random.default_rng(7)
	values = rng.normal(-8.0, 4.0, (40, 50))
	values[3, 4] = np.nan
	transform = from_origin(-74.01, 40.71, 1.0 / 32400.0, 1.0 / 32400.0)
	x = -74.01 + rng.uniform(-2, 52, 500) / 32400.0
	y = 40.71 - rng.uniform(-2, 42, 500) / 32400.0
	ours = heights.bilinear(values, transform, x, y)
	theirs = environment_depth.bilinear(values, transform, x, y)
	assert np.array_equal(np.isnan(ours), np.isnan(theirs))
	assert np.array_equal(ours[~np.isnan(ours)], theirs[~np.isnan(theirs)])


def test_ellipsoidal_height_is_navd88_plus_the_owners_geoid():
	# Geoid corners at the grown square's corners (250 m margin), interpolated bilinearly in the world frame.
	corners = {"sw": -32.0, "se": -31.0, "nw": -30.0, "ne": -29.0}
	geoid = heights.Geoid.from_corners(0, 0, corners)
	assert geoid(np.array([-250.0]), np.array([-250.0]))[0] == pytest.approx(-32.0)
	assert geoid(np.array([4250.0]), np.array([4250.0]))[0] == pytest.approx(-29.0)
	assert geoid(np.array([2000.0]), np.array([2000.0]))[0] == pytest.approx(-30.5)
	assert geoid(np.array([-250.0]), np.array([4250.0]))[0] == pytest.approx(-31.0)
	# NAVD88 zero sits below the ellipsoid here: a -10 m seabed is at -40.5 m ellipsoidal at the centre.
	assert heights.ellipsoidal(np.array([-10.0]), geoid(np.array([2000.0]), np.array([2000.0])))[0] == pytest.approx(-40.5)


def test_geoid_reads_the_tile_file_and_refuses_corners_off_the_grown_square(tmp_path):
	document = {"points": {}}
	for label, (n, e) in {"sw": (-250, -250), "se": (-250, 4250), "nw": (4250, -250), "ne": (4250, 4250),
			"centre": (2000, 2000)}.items():
		lat, lon = geodesy.from_local(float(n), float(e))
		document["points"][label] = {"lat": float(lat), "lon": float(lon), "north_m": float(n), "east_m": float(e),
			"navd88_zero_ellipsoidal_height_m": -31.8}
	import json
	path = tmp_path / "geoid_offset.json"
	path.write_text(json.dumps(document))
	assert heights.Geoid.read(path, 0, 0)(np.array([1.0]), np.array([1.0]))[0] == pytest.approx(-31.8)
	document["points"]["ne"]["north_m"] = 4000.0
	path.write_text(json.dumps(document))
	with pytest.raises(ValueError, match="corner"):
		heights.Geoid.read(path, 0, 0)
