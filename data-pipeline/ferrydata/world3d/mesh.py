"""Meshes for one tile: terrain grids per level of detail with skirts, and the fixed chart structures as walls.

A chunk's vertices come in a fixed order, which the validator relies on: the terrain grid (rows south to north, columns
west to east), then its skirt (the four edges counter-clockwise seen from above: south west to east, east south to
north, north east to west, west north to south), then its walls (eight vertices each: the face on the left of the
segment's direction, bottom start, bottom end, top start, top end, then the same four for the face on the right).

Triangles wind counter-clockwise seen from their front: terrain faces up, skirts face out of the chunk, and each wall
has a face to either side, so back-face culling hides none of them.
"""

import numpy as np

from ferrydata import geodesy
from ferrydata.world3d import frames, heights

CHUNKS_PER_SIDE = 4
# Level 0 and 1 are 1 km chunks; level 2 is the whole 4 km tile. Strides are in L0 lattice points.
LEVELS = {0: {"stride": 1, "cells": 84, "node": "chunk"}, 1: {"stride": 4, "cells": 21, "node": "chunk"},
	2: {"stride": 16, "cells": 21, "node": "tile"}}
# Skirts hang this far below every chunk edge along the ellipsoid normal, so a refined chunk beside an unrefined one
# shows no gap. It covers the largest difference measured between neighbouring levels' edges (README.md).
SKIRT_DEPTH_M = 30.0
# Chart structures extruded as the slice importer does: a greybox wall from NAVD88 -60 m to +1 m, not surveyed heights.
WALL_BOTTOM_NAVD88_M = -60.0
WALL_TOP_NAVD88_M = 1.0
# The slice's structure material, (0.36, 0.39, 0.42), as vertex colour.
STRUCTURE_COLOUR = np.array([92, 99, 107, 255], dtype=np.uint8)
MINIMUM_WALL_M = 1e-3

# The slice importer's terrain colours: shallow cyan to deep blue below NAVD88 0, green to brown above it.
_WATER_SHALLOW, _WATER_DEEP = np.array([0.13, 0.65, 0.69]), np.array([0.015, 0.07, 0.24])
_LAND_LOW, _LAND_HIGH = np.array([0.38, 0.48, 0.28]), np.array([0.65, 0.60, 0.42])


def terrain_colours(navd88):
	"""RGBA uint8 per point, as World/Import's SliceImporter colours its terrain (Color.Lerp clamps to [0, 1])."""
	e = np.nan_to_num(np.asarray(navd88, dtype=np.float64), nan=0.0)[..., None]
	land = _LAND_LOW + (_LAND_HIGH - _LAND_LOW) * np.clip(e / 30.0, 0.0, 1.0)
	water = _WATER_SHALLOW + (_WATER_DEEP - _WATER_SHALLOW) * np.clip(-e / 45.0, 0.0, 1.0)
	rgb = np.where(e > 0, land, water)
	alpha = np.full(rgb.shape[:-1] + (1,), 255, dtype=np.uint8)
	return np.concatenate([np.round(rgb * 255.0).astype(np.uint8), alpha], axis=-1)


class TileGrid:
	"""One tile's lattice points at one level, in the tile's frame: computed once, so chunks share their edges bit for bit."""

	def __init__(self, lattice, frame, level):
		self.level = level
		self.stride = LEVELS[level]["stride"]
		size = heights.L0_CELLS_PER_TILE
		rows, cols = lattice.window(size * frame.i, size * (frame.i + 1), size * frame.j, size * (frame.j + 1), self.stride)
		R, C = np.meshgrid(rows, cols, indexing="ij")
		self.lat, self.lon = lattice.lat[R, C], lattice.lon[R, C]
		self.height = lattice.height[R, C]
		self.navd88 = lattice.navd88[R, C]
		self.valid = lattice.valid[R, C]
		self.positions = frames.gltf_from_enu(frame.enu(lattice.ecef[R, C])).astype(np.float32)
		self.normals = frames.gltf_from_enu(frame.enu_vectors(lattice.normals(rows, cols, self.stride))).astype(np.float32)
		self.colours = terrain_colours(self.navd88)
		self.frame = frame


class Parts:
	"""Vertex and index arrays, appended in order."""

	def __init__(self):
		self.positions, self.normals, self.colours, self.indices = [], [], [], []
		self.count = 0

	def add(self, positions, normals, colours, triangles, absolute=False):
		"""Appends vertices and triangles; triangle indices count from these vertices, or from the first part if `absolute`."""
		self.positions.append(np.asarray(positions, dtype=np.float32).reshape(-1, 3))
		self.normals.append(np.asarray(normals, dtype=np.float32).reshape(-1, 3))
		self.colours.append(np.asarray(colours, dtype=np.uint8).reshape(-1, 4))
		self.indices.append(np.asarray(triangles, dtype=np.int64).reshape(-1) + (0 if absolute else self.count))
		self.count += len(self.positions[-1])

	def arrays(self):
		return (np.concatenate(self.positions), np.concatenate(self.normals), np.concatenate(self.colours),
			np.concatenate(self.indices).astype(np.uint32))


def grid_triangles(valid):
	"""Two counter-clockwise (seen from above) triangles per cell, each only where its three corners have heights."""
	h, w = valid.shape
	r, c = np.meshgrid(np.arange(h - 1), np.arange(w - 1), indexing="ij")
	a = (r * w + c).ravel()
	b, d, e = a + 1, a + w + 1, a + w
	flat = valid.ravel()
	first = np.stack([a, b, d], axis=1)[flat[a] & flat[b] & flat[d]]
	second = np.stack([a, d, e], axis=1)[flat[a] & flat[d] & flat[e]]
	# Interleave by cell so the order is stable and readable: cell by cell, first then second.
	order = np.argsort(np.concatenate([np.flatnonzero(flat[a] & flat[b] & flat[d]) * 2,
		np.flatnonzero(flat[a] & flat[d] & flat[e]) * 2 + 1]), kind="stable")
	return np.concatenate([first, second])[order]


def edge_loop(h, w):
	"""Grid indices of the four edges, counter-clockwise from the south-west corner, one list per edge."""
	return [np.arange(w), np.arange(h) * w + (w - 1), (h - 1) * w + np.arange(w)[::-1], (np.arange(h) * w)[::-1]]


def terrain(grid, row0, row1, col0, col1):
	"""Parts for the window [row0, row1] x [col0, col1] of a TileGrid (inclusive): the grid, then its skirt."""
	window = (slice(row0, row1 + 1), slice(col0, col1 + 1))
	valid = grid.valid[window]
	h, w = valid.shape
	parts = Parts()
	parts.add(grid.positions[window], grid.normals[window], grid.colours[window], grid_triangles(valid))
	# The skirt: each edge vertex again, straight down along the ellipsoid normal.
	lat, lon, height = grid.lat[window].ravel(), grid.lon[window].ravel(), grid.height[window].ravel()
	normals, colours, flat = grid.normals[window].reshape(-1, 3), grid.colours[window].reshape(-1, 4), valid.ravel()
	edges = edge_loop(h, w)
	top = np.concatenate(edges)
	below = heights.surface_points(lat[top], lon[top], height[top] - SKIRT_DEPTH_M)
	skirt = frames.gltf_from_enu(grid.frame.enu(below)).astype(np.float32)
	# Facing out of the chunk: (top k, skirt k, top k+1) and (top k+1, skirt k, skirt k+1) along each edge.
	triangles, start = [], h * w
	for edge in edges:
		k = np.arange(len(edge) - 1)
		ok = flat[edge[k]] & flat[edge[k + 1]]
		t0, t1 = edge[k][ok], edge[k + 1][ok]
		s0, s1 = start + k[ok], start + k[ok] + 1
		triangles.append(np.stack([t0, s0, t1, t1, s0, s1], axis=1).reshape(-1))
		start += len(edge)
	parts.add(skirt, normals[top], colours[top], np.concatenate(triangles), absolute=True)
	return parts


def walls(parts, frame, sources, segments):
	"""Appends a wall per segment ((k, 2, 2) world [north, east] start and end) to `parts`."""
	if len(segments) == 0:
		return
	a, b = segments[:, 0], segments[:, 1]
	ends = np.concatenate([a, b])
	lat, lon = geodesy.from_local(ends[:, 0], ends[:, 1])
	geoid = sources.geoid_at(ends[:, 0], ends[:, 1])
	if not np.isfinite(geoid).all():
		raise ValueError(f"{frame.id}: a structure's end has no owning tile for its geoid offset")
	bottom = heights.surface_points(lat, lon, heights.ellipsoidal(np.full(len(ends), WALL_BOTTOM_NAVD88_M), geoid))
	top = heights.surface_points(lat, lon, heights.ellipsoidal(np.full(len(ends), WALL_TOP_NAVD88_M), geoid))
	k = len(segments)
	a_bottom, b_bottom = frame.enu(bottom[:k]), frame.enu(bottom[k:])
	a_top, b_top = frame.enu(top[:k]), frame.enu(top[k:])
	left = np.cross(a_top - a_bottom, b_bottom - a_bottom)
	left /= np.linalg.norm(left, axis=1, keepdims=True)
	face = np.stack([a_bottom, b_bottom, a_top, b_top], axis=1)
	positions = np.concatenate([face, face], axis=1)  # (k, 8, 3)
	normals = np.concatenate([np.repeat(left[:, None], 4, axis=1), np.repeat(-left[:, None], 4, axis=1)], axis=1)
	quad = np.array([0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6])
	triangles = (np.arange(k)[:, None] * 8 + quad[None, :]).reshape(-1)
	colours = np.broadcast_to(STRUCTURE_COLOUR, (k * 8, 4))
	parts.add(frames.gltf_from_enu(positions.reshape(-1, 3)), frames.gltf_from_enu(normals.reshape(-1, 3)), colours, triangles)


def clip(a, b, north0, north1, east0, east1):
	"""Liang-Barsky: the part of each segment a -> b ((k, 2) [north, east]) inside the closed box, and which survive.

	An end that lies inside is returned unchanged, so consecutive segments keep sharing their exact vertices.
	"""
	d = b - a
	t0, t1 = np.zeros(len(a)), np.ones(len(a))
	keep = np.ones(len(a), dtype=bool)
	for p, q in ((-d[:, 0], a[:, 0] - north0), (d[:, 0], north1 - a[:, 0]), (-d[:, 1], a[:, 1] - east0), (d[:, 1], east1 - a[:, 1])):
		parallel = p == 0
		keep &= ~(parallel & (q < 0))
		with np.errstate(divide="ignore", invalid="ignore"):
			r = np.where(parallel, 0.0, q / np.where(parallel, 1.0, p))
		t0 = np.where(~parallel & (p < 0), np.maximum(t0, r), t0)
		t1 = np.where(~parallel & (p > 0), np.minimum(t1, r), t1)
	keep &= t0 <= t1
	start = np.where((t0 == 0)[:, None], a, a + t0[:, None] * d)
	end = np.where((t1 == 1)[:, None], b, a + t1[:, None] * d)
	keep &= np.linalg.norm(end - start, axis=1) >= MINIMUM_WALL_M
	return start[keep], end[keep]
