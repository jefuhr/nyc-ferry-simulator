"""Generated workload tiles: geometry, bounding volumes, density, variety and reproducibility.

Failure modes guarded:
- terrain or walls wound the wrong way, so back-face culling hides them (normals must face up and outward);
- neighbouring tiles that do not meet (cracks or steps at the shared edge);
- content outside its 3D Tiles bounding box (Cesium would cull it while it is on screen);
- the wrong vertex density (the brief asks for ~12 m and a 48 m LOD);
- tiles that repeat each other, which would flatter caching;
- a second generation that differs from the first;
- the camera path running over land or through a stand-in building.
"""

import hashlib

import numpy as np
import pytest

import camera_path
import glb
import spike_grid as grid
import tiles


@pytest.fixture(scope="module")
def sources():
	return tiles.HeightSources.synthetic_only()


@pytest.fixture(scope="module")
def tile_far(sources):
	return tiles.build_tile(3, -2, sources)


@pytest.fixture(scope="module")
def tile_east(sources):
	return tiles.build_tile(3, -1, sources)


def faces(chunk):
	p = chunk.positions_enu.astype(np.float64)
	tri = chunk.indices.reshape(-1, 3)
	return p, tri, np.cross(p[tri[:, 1]] - p[tri[:, 0]], p[tri[:, 2]] - p[tri[:, 0]])


def test_terrain_faces_point_up(tile_far):
	chunk = tile_far.chunks[(1, 2)][0]
	p, tri, normal = faces(chunk)
	terrain = tri[: chunk.terrain_triangle_count]
	assert (normal[: len(terrain), 2] > 0).all()
	# Stored vertex normals agree with the faces around them.
	vertex_normals = chunk.normals_enu[terrain[:, 0]]
	unit = normal[: len(terrain)] / np.linalg.norm(normal[: len(terrain)], axis=1, keepdims=True)
	assert (np.einsum("ij,ij->i", vertex_normals, unit) > 0.5).mean() > 0.99


def test_building_walls_face_outward(tile_far):
	chunk = next(c for c in (tile_far.chunks[key][0] for key in sorted(tile_far.chunks)) if c.building_count > 0)
	p, tri, normal = faces(chunk)
	walls = slice(chunk.terrain_triangle_count, len(tri))
	centroid = p[tri[walls]].mean(axis=1)
	box_ids = chunk.triangle_building[chunk.terrain_triangle_count:]
	box_centre = chunk.building_centres_enu[box_ids]
	outward = np.einsum("ij,ij->i", normal[walls], centroid - box_centre)
	assert (outward > 0).all()


def test_vertex_density(tile_far):
	l0, l1 = tile_far.chunks[(0, 0)]
	assert l0.grid_shape == (85, 85) and l1.grid_shape == (22, 22)
	grid0 = l0.positions_enu[: 85 * 85].reshape(85, 85, 3)
	spacing = np.linalg.norm(grid0[0, 1, :2] - grid0[0, 0, :2])
	assert spacing == pytest.approx(1000.0 / 84.0, abs=0.05)
	grid1 = l1.positions_enu[: 22 * 22].reshape(22, 22, 3)
	assert np.linalg.norm(grid1[0, 1, :2] - grid1[0, 0, :2]) == pytest.approx(1000.0 / 21.0, abs=0.05)


def test_content_inside_bounding_boxes(tile_far):
	for key in sorted(tile_far.chunks):
		for chunk in tile_far.chunks[key]:
			centre, half = chunk.box_centre, chunk.box_half
			assert (np.abs(chunk.positions_enu - centre) <= half + 1e-3).all()


def test_neighbours_meet_at_the_shared_edge(tile_far, tile_east):
	# tile_3_-2's east edge is tile_3_-1's west edge: compare their L0 edge vertices in ECEF.
	left = tile_far.chunks[(2, 3)][0]
	right = tile_east.chunks[(2, 0)][0]
	a = left.positions_enu[: 85 * 85].reshape(85, 85, 3)[:, -1]
	b = right.positions_enu[: 85 * 85].reshape(85, 85, 3)[:, 0]
	ecef_a = grid.TileFrame(3, -2).ecef_from_enu(a.astype(np.float64))
	ecef_b = grid.TileFrame(3, -1).ecef_from_enu(b.astype(np.float64))
	assert np.abs(ecef_a - ecef_b).max() < 0.01


def test_buildings_present_and_counted(tile_far):
	assert 2000 <= tile_far.building_count <= 6000
	l1_buildings = sum(tile_far.chunks[key][1].building_count for key in tile_far.chunks)
	assert 0 < l1_buildings < tile_far.building_count


def test_tiles_differ(tile_far, tile_east):
	a = glb.encode(*tile_far.chunks[(1, 1)][0].arrays_gltf())
	b = glb.encode(*tile_east.chunks[(1, 1)][0].arrays_gltf())
	assert hashlib.sha256(a).digest() != hashlib.sha256(b).digest()


def test_generation_is_reproducible(sources, tile_far):
	again = tiles.build_tile(3, -2, sources)
	for key in sorted(tile_far.chunks):
		for first, second in zip(tile_far.chunks[key], again.chunks[key]):
			assert glb.encode(*first.arrays_gltf()) == glb.encode(*second.arrays_gltf())


def test_camera_path_stays_on_water_and_clear_of_buildings(sources):
	path = camera_path.build()
	samples = camera_path.sample(path, spacing=25.0)
	heights = sources.navd88(samples[:, 0], samples[:, 1])
	synthetic = ~sources.real_mask(samples[:, 0], samples[:, 1])
	assert (heights[synthetic] < -2.0).all()
	for i, j in sorted({grid.owner(n, e) for n, e in samples}):
		footprints = tiles.building_footprints(i, j, sources)
		if len(footprints) == 0:
			continue
		# Distance from every path sample to every footprint centre in this tile.
		d = np.hypot(samples[:, None, 0] - footprints[None, :, 0], samples[:, None, 1] - footprints[None, :, 1])
		assert d.min() > 120.0


def test_camera_path_runs_on_real_water():
	"""With the real rasters, every 10 m of the path is deeper than 3 m (skipped when the slice is not built)."""
	from pathlib import Path

	from ferrydata import paths

	repo = Path(__file__).resolve().parents[4]
	if not (repo / "data" / "processed" / "lower-east-river" / "depth_merged_navd88.tif").exists():
		pytest.skip("slice outputs not built: ferrydata build --slice lower-east-river")
	sources = tiles.HeightSources.load(repo, paths.cache_root())
	samples = camera_path.sample(camera_path.build(), spacing=10.0)
	heights, codes = sources.evaluate(samples[:, 0], samples[:, 1])
	assert (codes != tiles.SYNTHETIC).all(), "real terrain should cover the whole path"
	assert heights.max() < -3.0
