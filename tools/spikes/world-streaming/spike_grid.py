"""Harbor tiles v1 grid, as the spike uses it, and each tile's local east-north-up frame.

Tile (i, j) covers north in [4000 i, 4000 (i + 1)) and east in [4000 j, 4000 (j + 1)) metres of the world
frame (docs/conventions.md), for i and j from -2 to 3. Geometry for a tile is placed in an ENU frame at the
tile centre on the ellipsoid, computed through ECEF from each point's geodetic position, so Earth curvature
is exact: about 20 m of drop across the harbor relative to the world tangent plane, under 1 m inside a tile.
"""

import math

import numpy as np

from ferrydata import geodesy

TILE_SIZE_M = 4000.0
TILE_MIN = -2
TILE_MAX = 3
MARGIN_M = 250.0


def tile_id(i, j):
	return f"tile_{i}_{j}"


def all_tiles():
	"""Every candidate tile, south to north, then west to east."""
	return [(i, j) for i in range(TILE_MIN, TILE_MAX + 1) for j in range(TILE_MIN, TILE_MAX + 1)]


def owner(north, east):
	"""The tile whose square (without the margin) contains a world point."""
	return math.floor(north / TILE_SIZE_M), math.floor(east / TILE_SIZE_M)


def in_extent(i, j):
	return TILE_MIN <= i <= TILE_MAX and TILE_MIN <= j <= TILE_MAX


def bounds(i, j):
	"""(north0, north1, east0, east1) of the tile's square, metres."""
	return (TILE_SIZE_M * i, TILE_SIZE_M * (i + 1), TILE_SIZE_M * j, TILE_SIZE_M * (j + 1))


def grown_bounds(i, j):
	north0, north1, east0, east1 = bounds(i, j)
	return (north0 - MARGIN_M, north1 + MARGIN_M, east0 - MARGIN_M, east1 + MARGIN_M)


class TileFrame:
	"""East-north-up frame at a tile's centre on the WGS84 ellipsoid (height 0)."""

	def __init__(self, i, j):
		self.i, self.j = i, j
		self.north0, self.north1, self.east0, self.east1 = bounds(i, j)
		self.centre_north = 0.5 * (self.north0 + self.north1)
		self.centre_east = 0.5 * (self.east0 + self.east1)
		lat, lon = geodesy.from_local(self.centre_north, self.centre_east)
		self.latitude, self.longitude = float(lat), float(lon)
		self.origin = np.array([float(v) for v in geodesy.to_ecef(self.latitude, self.longitude, 0.0)])
		phi, lam = math.radians(self.latitude), math.radians(self.longitude)
		self.east = np.array([-math.sin(lam), math.cos(lam), 0.0])
		self.north = np.array([-math.sin(phi) * math.cos(lam), -math.sin(phi) * math.sin(lam), math.cos(phi)])
		self.up = np.array([math.cos(phi) * math.cos(lam), math.cos(phi) * math.sin(lam), math.sin(phi)])
		self.rotation = np.stack([self.east, self.north, self.up])  # rows: ECEF -> ENU

	def enu(self, north, east, height):
		"""ENU (x east, y north, z up) in this frame of world points at an ellipsoidal height. Arrays in, (n, 3) out."""
		lat, lon = geodesy.from_local(north, east)
		ecef = np.stack(geodesy.to_ecef(lat, lon, height), axis=-1)
		return (ecef - self.origin) @ self.rotation.T

	def ecef_from_enu(self, enu):
		return np.asarray(enu, dtype=np.float64) @ self.rotation + self.origin

	def tiles_transform(self):
		"""3D Tiles `transform`: column-major 4x4 from this frame (z up) to ECEF."""
		columns = [self.east, self.north, self.up]
		values = []
		for column in columns:
			values.extend(float(v) for v in column)
			values.append(0.0)
		values.extend(float(v) for v in self.origin)
		values.append(1.0)
		return values

	def region(self, min_height, max_height, margin=0.0):
		"""3D Tiles `region` (radians, metres) around the tile's square, optionally grown by a margin."""
		north = np.array([self.north0 - margin, self.north0 - margin, self.north1 + margin, self.north1 + margin])
		east = np.array([self.east0 - margin, self.east1 + margin, self.east0 - margin, self.east1 + margin])
		lat, lon = geodesy.from_local(north, east)
		return [math.radians(float(np.min(lon))), math.radians(float(np.min(lat))),
			math.radians(float(np.max(lon))), math.radians(float(np.max(lat))), float(min_height), float(max_height)]
