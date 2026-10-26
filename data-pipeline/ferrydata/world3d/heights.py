"""Terrain heights for the harbor's 3D Tiles: one lattice, one owner per point, ellipsoidal heights.

Every level of detail samples one lattice in the world frame: 336 cells per 4 km tile side (about 11.9 m), so a tile
edge is a lattice line and neighbouring tiles share their edge points exactly. Coarser levels take every 4th and every
16th point of it.

A point's height comes from exactly one tile, its owner, so two tiles never disagree about a shared vertex:
- the tile whose half-open square contains it (a tile owns its south and west edges and its south-west corner);
- if that tile is inactive (not built), the active tile nearest the point whose 250 m grown square contains it, the
  lowest (i, j) on a tie;
- otherwise nothing: the point has no height and no triangle uses it.

The owner's merged NAVD88 raster is sampled strictly bilinearly at cell centres (a nodata neighbour gives nodata), as
ferrydata.environment samples the depth the core uses. The raster's CRS is read from the file; WGS84 points go to it
through pyproj (for NAD83 that is the null "NAD83 to WGS 84 (1)"). The geoid offset, the ellipsoidal height of NAVD88
zero, is interpolated bilinearly in the world frame from the owner's geoid_offset.json corners, which sit on the
corners of its grown square. Ellipsoidal height = NAVD88 + that offset.

The lattice is computed once for the whole harbor, each point's height in one batch with its owner's other points, so
shared vertices are bit-identical inputs to every tile that draws them.
"""

import json
from pathlib import Path

import numpy as np
import rasterio
from pyproj import Transformer

from ferrydata import geodesy, tiles

SIZE_M = float(tiles.SIZE_M)
MARGIN_M = float(tiles.MARGIN_M)
L0_CELLS_PER_TILE = 336
NO_TILE = -9999


def coordinate(index):
	"""World north or east (m) of lattice index `index`: exact on tile edges (multiplied before it is divided)."""
	return np.asarray(index, dtype=np.float64) * SIZE_M / L0_CELLS_PER_TILE


class Owners:
	"""The owner rule above, for points in the world frame."""

	def __init__(self, active):
		self.active = frozenset((int(i), int(j)) for i, j in active)
		self._lo = min(min(i for i, _ in self.active), min(j for _, j in self.active)) - 2 if self.active else 0
		size = (max(max(i for i, _ in self.active), max(j for _, j in self.active)) + 3 - self._lo) if self.active else 1
		self._grid = np.zeros((size, size), dtype=bool)
		for i, j in self.active:
			self._grid[i - self._lo, j - self._lo] = True

	def is_active(self, i, j):
		a, b = np.asarray(i) - self._lo, np.asarray(j) - self._lo
		inside = (a >= 0) & (b >= 0) & (a < self._grid.shape[0]) & (b < self._grid.shape[1])
		return inside & self._grid[np.where(inside, a, 0), np.where(inside, b, 0)]

	def owner(self, north, east):
		"""(i, j) arrays of each point's owning tile, NO_TILE where none."""
		north, east = np.asarray(north, dtype=np.float64), np.asarray(east, dtype=np.float64)
		i0 = np.floor(north / SIZE_M).astype(np.int64)
		j0 = np.floor(east / SIZE_M).astype(np.int64)
		home = self.is_active(i0, j0)
		best_i = np.where(home, i0, NO_TILE)
		best_j = np.where(home, j0, NO_TILE)
		best_d = np.where(home, -1.0, np.inf)
		for di in (-1, 0, 1):
			for dj in (-1, 0, 1):
				ci, cj = i0 + di, j0 + dj
				dn = np.maximum(0.0, np.maximum(SIZE_M * ci - north, north - SIZE_M * (ci + 1)))
				de = np.maximum(0.0, np.maximum(SIZE_M * cj - east, east - SIZE_M * (cj + 1)))
				d = np.hypot(dn, de)
				ok = self.is_active(ci, cj) & (d <= MARGIN_M) & ~home
				better = ok & ((d < best_d) | ((d == best_d) & ((ci < best_i) | ((ci == best_i) & (cj < best_j)))))
				best_i = np.where(better, ci, best_i)
				best_j = np.where(better, cj, best_j)
				best_d = np.where(better, d, best_d)
		return best_i, best_j


def bilinear(array, transform, x, y):
	"""Strict bilinear at cell centres: nodata (NaN) anywhere in the 2x2 neighbourhood, or outside the centres, is NaN.

	The same arithmetic as ferrydata.environment.depth.bilinear (a test keeps them equal).
	"""
	inverse = ~transform
	x, y = np.asarray(x, dtype=np.float64), np.asarray(y, dtype=np.float64)
	col = inverse.a * x + inverse.b * y + inverse.c - .5
	row = inverse.d * x + inverse.e * y + inverse.f - .5
	valid = (row >= 0) & (col >= 0) & (row <= array.shape[0] - 1) & (col <= array.shape[1] - 1)
	r0 = np.floor(np.clip(row, 0, array.shape[0] - 1)).astype(int)
	c0 = np.floor(np.clip(col, 0, array.shape[1] - 1)).astype(int)
	r1 = np.minimum(r0 + 1, array.shape[0] - 1)
	c1 = np.minimum(c0 + 1, array.shape[1] - 1)
	dy, dx = row - r0, col - c0
	a, b, c, d = array[r0, c0], array[r0, c1], array[r1, c0], array[r1, c1]
	result = a * (1 - dy) * (1 - dx) + b * (1 - dy) * dx + c * dy * (1 - dx) + d * dy * dx
	return np.where(valid, result, np.nan)


class Raster:
	"""A merged NAVD88 raster, nodata as NaN, sampled at WGS84 points."""

	def __init__(self, values, transform, crs, name):
		self.values, self.transform, self.crs, self.name = values, transform, crs, name
		self._project = Transformer.from_crs("EPSG:4326", crs, always_xy=True)

	@classmethod
	def read(cls, path):
		path = Path(path)
		with rasterio.open(path) as ds:
			if ds.transform.b != 0 or ds.transform.d != 0 or not ds.crs.is_geographic:
				raise ValueError(f"{path}: expected an unrotated geographic raster")
			values = ds.read(1).astype(np.float64)
			if ds.nodata is not None:
				values[values == ds.nodata] = np.nan
			values[~np.isfinite(values)] = np.nan
			return cls(values, ds.transform, ds.crs, path.name)

	def sample(self, lat, lon):
		x, y = self._project.transform(np.asarray(lon, dtype=np.float64), np.asarray(lat, dtype=np.float64))
		return bilinear(self.values, self.transform, x, y)


class Geoid:
	"""The ellipsoidal height of NAVD88 zero, bilinear in the world frame between a tile's grown-square corners."""

	def __init__(self, i, j, corners):
		self.i, self.j = i, j
		self.corners = {key: float(corners[key]) for key in ("sw", "se", "nw", "ne")}
		self.north0, self.north1 = SIZE_M * i - MARGIN_M, SIZE_M * (i + 1) + MARGIN_M
		self.east0, self.east1 = SIZE_M * j - MARGIN_M, SIZE_M * (j + 1) + MARGIN_M

	@classmethod
	def from_corners(cls, i, j, corners):
		return cls(i, j, corners)

	@classmethod
	def read(cls, path, i, j):
		points = json.loads(Path(path).read_text(encoding="utf-8"))["points"]
		expected = {"sw": (SIZE_M * i - MARGIN_M, SIZE_M * j - MARGIN_M), "se": (SIZE_M * i - MARGIN_M, SIZE_M * (j + 1) + MARGIN_M),
			"nw": (SIZE_M * (i + 1) + MARGIN_M, SIZE_M * j - MARGIN_M), "ne": (SIZE_M * (i + 1) + MARGIN_M, SIZE_M * (j + 1) + MARGIN_M)}
		corners = {}
		for key, (north, east) in expected.items():
			point = points[key]
			if abs(point["north_m"] - north) > 0.01 or abs(point["east_m"] - east) > 0.01:
				raise ValueError(f"{path}: the {key} corner is at ({point['north_m']}, {point['east_m']}), not on the grown square's "
					f"({north}, {east})")
			corners[key] = point["navd88_zero_ellipsoidal_height_m"]
		return cls(i, j, corners)

	def __call__(self, north, east):
		u = (np.asarray(east, dtype=np.float64) - self.east0) / (self.east1 - self.east0)
		v = (np.asarray(north, dtype=np.float64) - self.north0) / (self.north1 - self.north0)
		c = self.corners
		return (c["sw"] * (1 - u) + c["se"] * u) * (1 - v) + (c["nw"] * (1 - u) + c["ne"] * u) * v


def ellipsoidal(navd88, geoid):
	"""WGS84 ellipsoidal height of a NAVD88 elevation, given the ellipsoidal height of NAVD88 zero there."""
	return np.asarray(navd88, dtype=np.float64) + np.asarray(geoid, dtype=np.float64)


class Sources:
	"""The active tiles of a harbor tiles v1 build: where each one's raster and geoid corners are."""

	def __init__(self, harbor_dir, active):
		self.harbor_dir = Path(harbor_dir)
		self.active = sorted((int(i), int(j)) for i, j in active)
		self.owners = Owners(self.active)
		self._geoids = {}

	def tile_dir(self, i, j):
		return self.harbor_dir / "tiles" / f"tile_{i}_{j}"

	def raster(self, i, j):
		return Raster.read(self.tile_dir(i, j) / "depth_merged_navd88.tif")

	def geoid(self, i, j):
		if (i, j) not in self._geoids:
			self._geoids[(i, j)] = Geoid.read(self.tile_dir(i, j) / "geoid_offset.json", i, j)
		return self._geoids[(i, j)]

	def geoid_at(self, north, east):
		"""Geoid offset at world points from each point's owner; NaN where no tile owns it."""
		north, east = np.asarray(north, dtype=np.float64), np.asarray(east, dtype=np.float64)
		oi, oj = self.owners.owner(north, east)
		result = np.full(north.shape, np.nan)
		for i, j in self.active:
			mask = (oi == i) & (oj == j)
			if mask.any():
				result[mask] = self.geoid(i, j)(north[mask], east[mask])
		return result


class Lattice:
	"""The L0 lattice over the active tiles plus a ring of `ring` points, with every point's height computed once.

	Arrays are indexed [row, column] = [north index - gn0, east index - ge0]. `navd88` is NaN where the owner's raster
	has no value (or no tile owns the point); `height` (ellipsoidal) and `ecef` are finite everywhere, using NAVD88 0
	and a zero geoid offset where there is nothing, so unused vertices still have a place.
	"""

	def __init__(self, sources, ring=16, log=None):
		self.sources = sources
		i_values = [i for i, _ in sources.active]
		j_values = [j for _, j in sources.active]
		self.gn0 = L0_CELLS_PER_TILE * min(i_values) - ring
		self.ge0 = L0_CELLS_PER_TILE * min(j_values) - ring
		gn1 = L0_CELLS_PER_TILE * (max(i_values) + 1) + ring
		ge1 = L0_CELLS_PER_TILE * (max(j_values) + 1) + ring
		self.north_axis = coordinate(np.arange(self.gn0, gn1 + 1))
		self.east_axis = coordinate(np.arange(self.ge0, ge1 + 1))
		north, east = np.meshgrid(self.north_axis, self.east_axis, indexing="ij")
		self.lat, self.lon = geodesy.from_local(north, east)
		oi, oj = sources.owners.owner(north, east)
		self.owner_i, self.owner_j = oi.astype(np.int16), oj.astype(np.int16)
		self.navd88 = np.full(north.shape, np.nan)
		self.geoid = np.full(north.shape, np.nan)
		for i, j in sources.active:
			mask = (oi == i) & (oj == j)
			if not mask.any():
				continue
			raster = sources.raster(i, j)
			self.navd88[mask] = raster.sample(self.lat[mask], self.lon[mask])
			self.geoid[mask] = sources.geoid(i, j)(north[mask], east[mask])
			del raster
			if log:
				log(f"  heights: tile_{i}_{j} owns {int(mask.sum())} lattice points")
		self.valid = np.isfinite(self.navd88)
		self.height = ellipsoidal(np.where(self.valid, self.navd88, 0.0), np.where(np.isfinite(self.geoid), self.geoid, 0.0))
		self.ecef = np.stack(geodesy.to_ecef(self.lat, self.lon, self.height), axis=-1)

	def rows(self, gn):
		return np.asarray(gn) - self.gn0

	def cols(self, ge):
		return np.asarray(ge) - self.ge0

	def window(self, gn_start, gn_stop, ge_start, ge_stop, stride):
		"""Row and column index arrays (into the lattice) of the points gn_start..gn_stop, ge_start..ge_stop by stride, inclusive."""
		rows = self.rows(np.arange(gn_start, gn_stop + 1, stride))
		cols = self.cols(np.arange(ge_start, ge_stop + 1, stride))
		if rows.min() < 0 or cols.min() < 0 or rows.max() >= len(self.north_axis) or cols.max() >= len(self.east_axis):
			raise ValueError("lattice window outside the computed lattice")
		return rows, cols

	def normals(self, rows, cols, stride):
		"""Unit ECEF normals at the window's points: central differences of ECEF positions `stride` points apart.

		One-sided where a neighbour has no height, and the ellipsoid normal where neither side has one.
		"""
		R, C = np.meshgrid(rows, cols, indexing="ij")
		centre = self.ecef[R, C]

		def difference(dr, dc):
			ahead_r, ahead_c = R + dr * stride, C + dc * stride
			back_r, back_c = R - dr * stride, C - dc * stride
			ahead_ok = self.valid[ahead_r, ahead_c]
			back_ok = self.valid[back_r, back_c]
			ahead = np.where(ahead_ok[..., None], self.ecef[ahead_r, ahead_c], centre)
			back = np.where(back_ok[..., None], self.ecef[back_r, back_c], centre)
			return ahead - back, ahead_ok | back_ok

		d_east, east_ok = difference(0, 1)
		d_north, north_ok = difference(1, 0)
		normal = np.cross(d_east, d_north)
		length = np.linalg.norm(normal, axis=-1, keepdims=True)
		lat, lon = np.radians(self.lat[R, C]), np.radians(self.lon[R, C])
		up = np.stack([np.cos(lat) * np.cos(lon), np.cos(lat) * np.sin(lon), np.sin(lat)], axis=-1)
		usable = (east_ok & north_ok & self.valid[R, C])[..., None] & (length > 0)
		return np.where(usable, normal / np.where(length > 0, length, 1.0), up)


def surface_points(lat, lon, height):
	"""ECEF (n, 3) of geodetic points at ellipsoidal heights."""
	return np.stack(geodesy.to_ecef(lat, lon, height), axis=-1)
