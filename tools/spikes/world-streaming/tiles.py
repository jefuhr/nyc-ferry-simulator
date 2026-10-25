"""Workload tiles for the world streaming spike.

Each 4 km harbor tile is 4 x 4 chunks of 1 km. A chunk has two levels of detail:
- L0: an 85 x 85 vertex terrain grid (11.9 m, the importer's ~12 m density) plus every stand-in building whose
  footprint centre is in the chunk;
- L1: a 22 x 22 grid (47.6 m) plus the buildings at least 30 m tall, for distance views and the skyline.

Heights come from the slice's merged NAVD88 raster where it exists, then the cached CUDEM tiles (read at their
4x overview, about 10 x 14 m, and resampled to the chunk grids), then a smooth synthetic field where neither has
data (seamless across tiles, with a water channel carved along the camera path so the camera never meets a
synthetic hill). Synthetic land is tinted lavender so screenshots show what is invented. Buildings are extruded
boxes, a few thousand per tile on land, seeded per tile: always synthetic. None of this is simulator data: it
exists only to load the renderer and the streamer realistically.

Geometry is in each tile's ENU frame (x east, y north, z up, through ECEF, so curvature is exact). Triangles
wind counter-clockwise seen from their front in that right-handed frame.
"""

import hashlib
import math
from pathlib import Path

import numpy as np

import camera_path
import spike_grid as grid
from ferrydata import geodesy

CHUNKS_PER_SIDE = 4
CHUNK_SIZE_M = grid.TILE_SIZE_M / CHUNKS_PER_SIDE
L0_CELLS = 84
L1_CELLS = 21
L1_MIN_BUILDING_HEIGHT_M = 30.0
BLEND_M = 300.0
CHANNEL_HALF_WIDTH_M = 350.0
CHANNEL_SHORE_M = 650.0
BUILDING_LAND_ELEVATION_M = 1.5
BUILDING_PATH_CLEARANCE_M = 150.0
# Synthetic fixtures only (tests): a stand-in geoid, never used for generated harbor data.
TEST_GEOID_M = -31.8

SYNTHETIC, SLICE, CUDEM = 0, 1, 2

# Clusters of tall stand-ins, so the synthetic skyline has a shape: (latitude, longitude).
DOWNTOWNS = [(40.7080, -74.0110), (40.7540, -73.9840), (40.6930, -73.9870), (40.7160, -74.0330), (40.7470, -73.9430)]


def _seed(*parts):
	return int.from_bytes(hashlib.sha256(":".join(str(p) for p in parts).encode("utf-8")).digest()[:8], "little")


def _hash01(xi, yi, seed):
	"""Deterministic pseudo-random values in [-1, 1] at integer lattice points."""
	with np.errstate(over="ignore"):
		h = xi.astype(np.int64).astype(np.uint64) * np.uint64(0x9E3779B97F4A7C15)
		h ^= yi.astype(np.int64).astype(np.uint64) * np.uint64(0xC2B2AE3D27D4EB4F)
		h ^= np.uint64(seed & 0xFFFFFFFFFFFFFFFF) * np.uint64(0x165667B19E3779F9)
		h ^= h >> np.uint64(31)
		h *= np.uint64(0xBF58476D1CE4E5B9)
		h ^= h >> np.uint64(29)
	return (h >> np.uint64(11)).astype(np.float64) / float(1 << 53) * 2.0 - 1.0


def _value_noise(x, y, seed):
	xi, yi = np.floor(x), np.floor(y)
	fx, fy = x - xi, y - yi
	u, v = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
	a = _hash01(xi, yi, seed)
	b = _hash01(xi + 1, yi, seed)
	c = _hash01(xi, yi + 1, seed)
	d = _hash01(xi + 1, yi + 1, seed)
	return (a + (b - a) * u) + ((c + (d - c) * u) - (a + (b - a) * u)) * v


def _smoothstep(edge0, edge1, x):
	t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
	return t * t * (3 - 2 * t)


class Raster:
	"""A north-up geographic raster (NAD83, or NAD83 + NAVD88 as EPSG:5498, treated as WGS84 as the pipeline does), sampled bilinearly."""

	def __init__(self, values, west, north, cell_lon, cell_lat, nodata, name):
		self.values = values
		self.west, self.north = west, north
		self.cell_lon, self.cell_lat = cell_lon, cell_lat  # degrees per column east, per row south
		self.nodata, self.name = nodata, name
		self.east = west + cell_lon * values.shape[1]
		self.south = north - cell_lat * values.shape[0]

	@classmethod
	def read(cls, path, decimate=1):
		import rasterio
		from rasterio.enums import Resampling
		with rasterio.open(path) as ds:
			if ds.crs.to_epsg() not in (4269, 4326, 5498) or ds.transform.b != 0 or ds.transform.d != 0:
				raise ValueError(f"{path}: expected an unrotated geographic raster")
			shape = (ds.height // decimate, ds.width // decimate)
			values = ds.read(1, out_shape=shape, resampling=Resampling.average if decimate > 1 else Resampling.nearest).astype(np.float32)
			cell_lon = ds.transform.a * ds.width / shape[1]
			cell_lat = -ds.transform.e * ds.height / shape[0]
			nodata = ds.nodata
			west, north = ds.transform.c, ds.transform.f
		if nodata is not None:
			values[values == nodata] = np.nan
		values[~np.isfinite(values)] = np.nan
		return cls(values, west, north, cell_lon, cell_lat, nodata, Path(path).name)

	def sample(self, lat, lon):
		col = (lon - self.west) / self.cell_lon - 0.5
		row = (self.north - lat) / self.cell_lat - 0.5
		c0, r0 = np.floor(col).astype(np.int64), np.floor(row).astype(np.int64)
		inside = (c0 >= 0) & (r0 >= 0) & (c0 + 1 < self.values.shape[1]) & (r0 + 1 < self.values.shape[0])
		c0, r0 = np.where(inside, c0, 0), np.where(inside, r0, 0)
		fc, fr = col - c0, row - r0
		v = self.values
		top = v[r0, c0] * (1 - fc) + v[r0, c0 + 1] * fc
		bottom = v[r0 + 1, c0] * (1 - fc) + v[r0 + 1, c0 + 1] * fc
		value = top * (1 - fr) + bottom * fr
		valid = inside & np.isfinite(value)
		return np.where(valid, value, np.nan), valid

	def clamp(self, lat, lon):
		"""The nearest point inside the raster's sample centres, and the distance to it in metres (approximate)."""
		margin_lat, margin_lon = self.cell_lat, self.cell_lon
		clat = np.clip(lat, self.south + margin_lat, self.north - margin_lat)
		clon = np.clip(lon, self.west + margin_lon, self.east - margin_lon)
		distance = np.hypot((lat - clat) * 111_000.0, (lon - clon) * 111_000.0 * np.cos(np.radians(lat)))
		return clat, clon, distance


class HeightSources:
	"""NAVD88 elevation at world points: slice raster, then CUDEM, then the synthetic field."""

	def __init__(self, rasters, path, geoid_model, geoid_note):
		self.rasters = rasters  # [(Raster, code)], highest priority last
		self.path = path
		self._geoid = geoid_model
		self.geoid_note = geoid_note

	@classmethod
	def synthetic_only(cls):
		return cls([], camera_path.build(), lambda lat, lon: np.full(np.shape(lat), TEST_GEOID_M), "test constant")

	@classmethod
	def load(cls, repo_root, cache_root):
		import json
		processed = Path(repo_root) / "data" / "processed" / "lower-east-river"
		rasters = []
		for tif in sorted((Path(cache_root) / "cudem").glob("ncei19_*.tif")):
			rasters.append((Raster.read(tif, decimate=4), CUDEM))
		rasters.append((Raster.read(processed / "depth_merged_navd88.tif"), SLICE))
		points = json.loads((processed / "geoid_offset.json").read_text(encoding="utf-8"))["points"]
		south, north = points["sw"]["lat"], points["nw"]["lat"]
		west, east = points["sw"]["lon"], points["se"]["lon"]
		sw, se = points["sw"]["navd88_zero_ellipsoidal_height_m"], points["se"]["navd88_zero_ellipsoidal_height_m"]
		nw, ne = points["nw"]["navd88_zero_ellipsoidal_height_m"], points["ne"]["navd88_zero_ellipsoidal_height_m"]

		def geoid(lat, lon):
			# Bilinear between the slice corners, held constant outside the slice box.
			u = np.clip((np.asarray(lon) - west) / (east - west), 0.0, 1.0)
			v = np.clip((np.asarray(lat) - south) / (north - south), 0.0, 1.0)
			return (sw * (1 - u) + se * u) * (1 - v) + (nw * (1 - u) + ne * u) * v

		return cls(rasters, camera_path.build(), geoid, "bilinear between the slice's GEOID18 corners, constant outside the slice")

	def geoid(self, north, east):
		lat, lon = geodesy.from_local(north, east)
		return self._geoid(lat, lon)

	def synthetic(self, north, east):
		n, e = np.asarray(north, dtype=float), np.asarray(east, dtype=float)
		h = 4.0 + 18.0 * _value_noise(n / 1500.0, e / 1500.0, 11) + 8.0 * _value_noise(n / 600.0, e / 600.0, 12) \
			+ 3.0 * _value_noise(n / 250.0, e / 250.0, 13)
		channel = -9.0 - 4.0 * _value_noise(n / 700.0, e / 700.0, 14)
		shore = _smoothstep(CHANNEL_HALF_WIDTH_M, CHANNEL_SHORE_M, camera_path.distance_to_path(self.path, n, e))
		return np.where(h > channel, channel + (h - channel) * shore, h)

	def evaluate(self, north, east):
		"""(NAVD88 elevation, source code) at world points."""
		north, east = np.asarray(north, dtype=float), np.asarray(east, dtype=float)
		h = self.synthetic(north, east)
		code = np.full(north.shape, SYNTHETIC, dtype=np.int8)
		if not self.rasters:
			return h, code
		lat, lon = geodesy.from_local(north, east)
		for raster, value in self.rasters:
			sample, valid = raster.sample(lat, lon)
			h = np.where(valid, sample, h)
			code = np.where(valid, value, code)
		# Ease synthetic ground into the edge of real coverage instead of leaving a cliff.
		outside = code == SYNTHETIC
		if outside.any():
			best = np.full(north.shape, np.inf)
			edge = np.full(north.shape, np.nan)
			for raster, _ in self.rasters:
				clat, clon, distance = raster.clamp(lat, lon)
				sample, valid = raster.sample(clat, clon)
				closer = outside & valid & (distance < best) & (distance > 0)
				best = np.where(closer, distance, best)
				edge = np.where(closer, sample, edge)
			blend = outside & np.isfinite(edge) & (best < BLEND_M)
			w = _smoothstep(0.0, BLEND_M, best)
			h = np.where(blend, edge * (1 - w) + h * w, h)
		return h, code

	def navd88(self, north, east):
		return self.evaluate(north, east)[0]

	def real_mask(self, north, east):
		return self.evaluate(north, east)[1] != SYNTHETIC


def _terrain_colours(elevation, code):
	e = elevation[..., None]
	land_real = np.array([0.38, 0.48, 0.28]) + (np.array([0.65, 0.60, 0.42]) - np.array([0.38, 0.48, 0.28])) * np.clip(e / 30.0, 0, 1)
	land_synthetic = np.array([0.46, 0.42, 0.58]) + (np.array([0.70, 0.66, 0.78]) - np.array([0.46, 0.42, 0.58])) * np.clip(e / 30.0, 0, 1)
	water = np.array([0.13, 0.65, 0.69]) + (np.array([0.015, 0.07, 0.24]) - np.array([0.13, 0.65, 0.69])) * np.clip(-e / 45.0, 0, 1)
	land = np.where((code == SYNTHETIC)[..., None], land_synthetic, land_real)
	rgb = np.where(e > 0, land, water)
	return np.concatenate([np.round(rgb * 255).astype(np.uint8), np.full(elevation.shape + (1,), 255, np.uint8)], axis=-1)


class Chunk:
	"""One chunk at one level of detail, in its tile's ENU frame."""

	def __init__(self, positions, normals, colours, indices, terrain_triangle_count, triangle_building, building_centres, grid_shape):
		self.positions_enu = positions.astype(np.float32)
		self.normals_enu = normals.astype(np.float32)
		self.colours = colours
		self.indices = indices.astype(np.uint32)
		self.terrain_triangle_count = terrain_triangle_count
		self.triangle_building = triangle_building
		self.building_centres_enu = building_centres
		self.building_count = len(building_centres)
		self.grid_shape = grid_shape
		self.box_centre = None
		self.box_half = None

	@property
	def vertex_count(self):
		return len(self.positions_enu)

	@property
	def triangle_count(self):
		return len(self.indices) // 3

	def arrays_gltf(self):
		"""(positions, normals, colours, indices) in glTF axes: x east, y up, z south (a rotation of ENU, so winding holds)."""
		p, n = self.positions_enu, self.normals_enu
		return (np.stack([p[:, 0], p[:, 2], -p[:, 1]], axis=1), np.stack([n[:, 0], n[:, 2], -n[:, 1]], axis=1), self.colours, self.indices)


class Tile:
	def __init__(self, i, j, frame, chunks, building_count, provenance):
		self.i, self.j, self.frame = i, j, frame
		self.id = grid.tile_id(i, j)
		self.chunks = chunks  # (row north, column east) -> (L0, L1)
		self.building_count = building_count
		self.provenance = provenance  # fraction of L0 vertices per source


def _grid_mesh(frame, sources, north0, east0, cells):
	"""Terrain grid for one chunk: positions, normals (central differences on a one-cell-wider grid), colours."""
	step = CHUNK_SIZE_M / cells
	axis = np.arange(-1, cells + 2) * step
	north = north0 + axis[:, None] + 0.0 * axis[None, :]
	east = east0 + axis[None, :] + 0.0 * axis[:, None]
	elevation, code = sources.evaluate(north, east)
	enu = frame.enu(north.ravel(), east.ravel(), (elevation + sources.geoid(north, east)).ravel()).reshape(north.shape + (3,))
	d_east = enu[1:-1, 2:] - enu[1:-1, :-2]
	d_north = enu[2:, 1:-1] - enu[:-2, 1:-1]
	normal = np.cross(d_east, d_north)
	normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
	size = cells + 1
	positions = enu[1:-1, 1:-1].reshape(-1, 3)
	colours = _terrain_colours(elevation[1:-1, 1:-1], code[1:-1, 1:-1]).reshape(-1, 4)
	r, c = np.meshgrid(np.arange(cells), np.arange(cells), indexing="ij")
	a = (r * size + c).ravel()
	b, d, e = a + 1, a + size + 1, a + size
	indices = np.stack([a, b, d, a, d, e], axis=1).reshape(-1)
	return positions, normal.reshape(-1, 3), colours, indices, code[1:-1, 1:-1].ravel()


def building_footprints(i, j, sources):
	"""Stand-in buildings owned by tile (i, j): columns north, east, half north, half east, height, base NAVD88."""
	rng = np.random.default_rng(_seed("buildings", grid.tile_id(i, j)))
	north0, north1, east0, east1 = grid.bounds(i, j)
	probe = np.linspace(0.5, 63.5, 64) / 64.0
	land_fraction = float(np.mean(sources.navd88(north0 + probe[:, None] * grid.TILE_SIZE_M + 0 * probe[None, :],
		east0 + probe[None, :] * grid.TILE_SIZE_M + 0 * probe[:, None]) > 0.5))
	target = 0 if land_fraction < 0.02 else int(round(2000 + 4000 * land_fraction))
	count = 40000
	north = rng.uniform(north0, north1, count)
	east = rng.uniform(east0, east1, count)
	half_north = rng.uniform(6.0, 22.0, count)
	half_east = rng.uniform(6.0, 22.0, count)
	height = np.clip(np.exp(rng.normal(math.log(14.0), 0.75, count)), 4.0, 160.0)
	lat, lon = geodesy.from_local(north, east)
	for hub_lat, hub_lon in DOWNTOWNS:
		d = np.hypot((lat - hub_lat) * 111_000.0, (lon - hub_lon) * 84_400.0)
		height *= 1.0 + 5.0 * np.clip(1.0 - d / 1500.0, 0.0, 1.0) ** 2
	height = np.minimum(height, 380.0)
	elevation = sources.navd88(north, east)
	clear = camera_path.distance_to_path(sources.path, north, east) > BUILDING_PATH_CLEARANCE_M + np.maximum(half_north, half_east)
	keep = np.flatnonzero((elevation > BUILDING_LAND_ELEVATION_M) & clear)[:target]
	return np.stack([north[keep], east[keep], half_north[keep], half_east[keep], height[keep], elevation[keep] - 1.0], axis=1)


def _building_mesh(frame, sources, footprints, rng_seed):
	"""20 vertices and 10 triangles per box: four walls and a roof, flat normals, walls wound outward."""
	count = len(footprints)
	if count == 0:
		return np.zeros((0, 3)), np.zeros((0, 3)), np.zeros((0, 4), np.uint8), np.zeros(0, np.int64), np.zeros((0, 3))
	n, e, hn, he, height, base = (footprints[:, k] for k in range(6))
	corner_n = np.stack([n - hn, n - hn, n + hn, n + hn], axis=1)  # SW, SE, NE, NW: counter-clockwise from above
	corner_e = np.stack([e - he, e + he, e + he, e - he], axis=1)
	geoid = sources.geoid(n, e)
	bottom = (base + geoid)[:, None] + 0 * corner_n
	top = (base + height + geoid)[:, None] + 0 * corner_n
	low = frame.enu(corner_n.ravel(), corner_e.ravel(), bottom.ravel()).reshape(count, 4, 3)
	high = frame.enu(corner_n.ravel(), corner_e.ravel(), top.ravel()).reshape(count, 4, 3)
	walls = []
	for k in range(4):
		m = (k + 1) % 4
		walls.append(np.stack([low[:, k], low[:, m], high[:, m], high[:, k]], axis=1))
	faces = np.stack(walls + [high], axis=1)  # (count, 5 faces, 4 corners, 3)
	normal = np.cross(faces[:, :, 1] - faces[:, :, 0], faces[:, :, 2] - faces[:, :, 0])
	normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
	positions = faces.reshape(-1, 3)
	normals = np.repeat(normal[:, :, None, :], 4, axis=2).reshape(-1, 3)
	rng = np.random.default_rng(rng_seed)
	shade = rng.uniform(0.82, 1.08, count)
	wall_rgb = np.array([0.70, 0.69, 0.66])[None, :] * shade[:, None]
	roof_rgb = np.array([0.40, 0.40, 0.43])[None, :] * shade[:, None]
	rgb = np.concatenate([np.repeat(wall_rgb[:, None, :], 16, axis=1), np.repeat(roof_rgb[:, None, :], 4, axis=1)], axis=1).reshape(-1, 3)
	colours = np.concatenate([np.round(np.clip(rgb, 0, 1) * 255).astype(np.uint8), np.full((len(rgb), 1), 255, np.uint8)], axis=1)
	quad = np.array([0, 1, 2, 0, 2, 3])
	indices = (np.arange(count * 5)[:, None] * 4 + quad[None, :]).reshape(-1)
	centres = 0.5 * (low.mean(axis=1) + high.mean(axis=1))
	return positions, normals, colours, indices, centres


def _chunk(frame, sources, north0, east0, cells, footprints, seed):
	tp, tn, tc, ti, code = _grid_mesh(frame, sources, north0, east0, cells)
	bp, bn, bc, bi, centres = _building_mesh(frame, sources, footprints, seed)
	positions = np.concatenate([tp, bp])
	normals = np.concatenate([tn, bn])
	colours = np.concatenate([tc, bc])
	indices = np.concatenate([ti, bi + len(tp)])
	terrain_triangles = len(ti) // 3
	triangle_building = np.concatenate([np.full(terrain_triangles, -1), np.repeat(np.arange(len(footprints)), 10)])
	return Chunk(positions, normals, colours, indices, terrain_triangles, triangle_building, centres, (cells + 1, cells + 1)), code


def build_tile(i, j, sources):
	frame = grid.TileFrame(i, j)
	footprints = building_footprints(i, j, sources)
	chunks, codes = {}, []
	for row in range(CHUNKS_PER_SIDE):
		for column in range(CHUNKS_PER_SIDE):
			north0 = frame.north0 + row * CHUNK_SIZE_M
			east0 = frame.east0 + column * CHUNK_SIZE_M
			inside = (footprints[:, 0] >= north0) & (footprints[:, 0] < north0 + CHUNK_SIZE_M) \
				& (footprints[:, 1] >= east0) & (footprints[:, 1] < east0 + CHUNK_SIZE_M)
			mine = footprints[inside]
			seed = _seed("colours", grid.tile_id(i, j), row, column)
			l0, code = _chunk(frame, sources, north0, east0, L0_CELLS, mine, seed)
			l1, _ = _chunk(frame, sources, north0, east0, L1_CELLS, mine[mine[:, 4] >= L1_MIN_BUILDING_HEIGHT_M], seed)
			low = np.minimum(l0.positions_enu.min(axis=0), l1.positions_enu.min(axis=0)).astype(np.float64)
			high = np.maximum(l0.positions_enu.max(axis=0), l1.positions_enu.max(axis=0)).astype(np.float64)
			for chunk in (l0, l1):
				chunk.box_centre = 0.5 * (low + high)
				chunk.box_half = 0.5 * (high - low) + 0.01
			chunks[(row, column)] = (l0, l1)
			codes.append(code)
	codes = np.concatenate(codes)
	provenance = {name: float(np.mean(codes == value)) for name, value in (("slice", SLICE), ("cudem", CUDEM), ("synthetic", SYNTHETIC))}
	return Tile(i, j, frame, chunks, len(footprints), provenance)
