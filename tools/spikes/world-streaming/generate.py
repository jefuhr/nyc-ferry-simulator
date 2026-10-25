"""Generates the world streaming spike's workload from the slice outputs and the raw cache.

	uv run --project data-pipeline python tools/spikes/world-streaming/generate.py [--tiles tile_0_0,...] [--synthetic-only]

Writes, under artifacts/world-streaming-spike/data/ (gitignored):
- camera_path.json: the measured path, world (north, east) metres;
- tiles3d/tileset.json and tiles3d/tiles/<tile id>/{tileset.json, c<row>_<column>_l0.glb, ..._l1.glb}: local 3D Tiles
  1.1 for approach B, which approach A's Unity importer also reads, so both stream identical geometry;
- tiles3d/manifest.json: every file with its SHA-256, each tile's frame, chunk boxes and counts, the sources and timings.

The output is deterministic: rerunning it gives byte-identical files (the manifest's timings aside).
"""

import argparse
import hashlib
import json
import math
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path.insert(0, str(REPO / "data-pipeline"))
sys.path.insert(0, str(HERE))

import numpy as np  # noqa: E402

import camera_path  # noqa: E402
import glb  # noqa: E402
import spike_grid as grid  # noqa: E402
import tiles  # noqa: E402
from ferrydata import paths  # noqa: E402

# One LOD policy for both approaches: a chunk switches from L1 to L0 when the camera is within this distance of
# it. Cesium refines on screen-space error, so L1's geometric error is the one that gives that distance at the
# reference view (1080 rows, 60 degree vertical field of view, Cesium's default maximum screen-space error).
LOD_SWITCH_DISTANCE_M = 2000.0
REFERENCE_SCREEN_HEIGHT = 1080
REFERENCE_FOV_DEG = 60.0
MAXIMUM_SCREEN_SPACE_ERROR = 16.0
L1_GEOMETRIC_ERROR_M = MAXIMUM_SCREEN_SPACE_ERROR * LOD_SWITCH_DISTANCE_M * 2.0 * math.tan(math.radians(REFERENCE_FOV_DEG / 2)) / REFERENCE_SCREEN_HEIGHT
# Empty tile and root nodes always refine; their content is in the chunks.
ALWAYS_REFINE = 1.0e5
REGION_HEIGHTS = (-150.0, 500.0)


def sha256(path):
	digest = hashlib.sha256()
	with open(path, "rb") as stream:
		for block in iter(lambda: stream.read(1 << 20), b""):
			digest.update(block)
	return digest.hexdigest()


def box(chunk):
	c, h = chunk.box_centre, chunk.box_half
	return [float(c[0]), float(c[1]), float(c[2]), float(h[0]), 0.0, 0.0, 0.0, float(h[1]), 0.0, 0.0, 0.0, float(h[2])]


def union_box(chunks):
	low = np.min([c.box_centre - c.box_half for c in chunks], axis=0)
	high = np.max([c.box_centre + c.box_half for c in chunks], axis=0)
	centre, half = 0.5 * (low + high), 0.5 * (high - low)
	return [float(centre[0]), float(centre[1]), float(centre[2]), float(half[0]), 0.0, 0.0, 0.0, float(half[1]), 0.0, 0.0, 0.0, float(half[2])]


def write_json(path, document):
	path.write_text(json.dumps(document, indent="\t", sort_keys=False) + "\n", encoding="utf-8")


def write_tile(tile, folder):
	folder.mkdir(parents=True, exist_ok=True)
	children, chunk_records = [], []
	for row, column in sorted(tile.chunks):
		l0, l1 = tile.chunks[(row, column)]
		names = (f"c{row}_{column}_l0.glb", f"c{row}_{column}_l1.glb")
		for chunk, name in zip((l0, l1), names):
			(folder / name).write_bytes(glb.encode(*chunk.arrays_gltf()))
		children.append({
			"boundingVolume": {"box": box(l1)},
			"geometricError": L1_GEOMETRIC_ERROR_M,
			"content": {"uri": names[1]},
			"children": [{"boundingVolume": {"box": box(l0)}, "geometricError": 0.0, "content": {"uri": names[0]}}],
		})
		chunk_records.append({"row": row, "column": column, "l0": names[0], "l1": names[1],
			"box_centre_enu": [float(v) for v in l0.box_centre], "box_half_enu": [float(v) for v in l0.box_half],
			"l0_vertices": l0.vertex_count, "l0_triangles": l0.triangle_count, "l0_buildings": l0.building_count,
			"l1_vertices": l1.vertex_count, "l1_triangles": l1.triangle_count, "l1_buildings": l1.building_count})
	all_chunks = [chunk for pair in tile.chunks.values() for chunk in pair]
	write_json(folder / "tileset.json", {
		"asset": {"version": "1.1", "generator": glb.GENERATOR},
		"geometricError": ALWAYS_REFINE,
		"root": {"transform": tile.frame.tiles_transform(), "boundingVolume": {"box": union_box(all_chunks)},
			"geometricError": ALWAYS_REFINE, "refine": "REPLACE", "children": children},
	})
	return chunk_records


def main():
	parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
	parser.add_argument("--out", default=str(REPO / "artifacts" / "world-streaming-spike" / "data"))
	parser.add_argument("--tiles", help="comma-separated tile ids; default every candidate tile")
	parser.add_argument("--synthetic-only", action="store_true", help="ignore real rasters (tests and quick checks)")
	args = parser.parse_args()

	started = time.perf_counter()
	out = Path(args.out)
	tiles_root = out / "tiles3d"
	(tiles_root / "tiles").mkdir(parents=True, exist_ok=True)
	path = camera_path.build()
	camera_path.write(path, out / "camera_path.json")

	selected = grid.all_tiles()
	if args.tiles:
		wanted = set(args.tiles.split(","))
		selected = [t for t in selected if grid.tile_id(*t) in wanted]
	sources = tiles.HeightSources.synthetic_only() if args.synthetic_only else tiles.HeightSources.load(REPO, paths.cache_root())
	loaded = time.perf_counter()

	records, children = [], []
	for i, j in selected:
		t0 = time.perf_counter()
		tile = tiles.build_tile(i, j, sources)
		t1 = time.perf_counter()
		folder = tiles_root / "tiles" / tile.id
		chunk_records = write_tile(tile, folder)
		t2 = time.perf_counter()
		frame = tile.frame
		files = sorted(p for p in folder.iterdir() if p.is_file())
		records.append({
			"id": tile.id, "i": i, "j": j, "bounds_ne_m": list(grid.bounds(i, j)),
			"frame": {"latitude": frame.latitude, "longitude": frame.longitude, "origin_ecef": [float(v) for v in frame.origin],
				"east_ecef": [float(v) for v in frame.east], "north_ecef": [float(v) for v in frame.north], "up_ecef": [float(v) for v in frame.up]},
			"buildings": tile.building_count, "provenance": {k: round(v, 4) for k, v in tile.provenance.items()},
			"chunks": chunk_records,
			"bytes": sum(p.stat().st_size for p in files),
			"build_s": round(t1 - t0, 3), "write_s": round(t2 - t1, 3),
		})
		children.append({"boundingVolume": {"region": frame.region(*REGION_HEIGHTS)}, "geometricError": ALWAYS_REFINE,
			"content": {"uri": f"tiles/{tile.id}/tileset.json"}})
		print(f"{tile.id}: {tile.building_count} buildings, {records[-1]['bytes'] / 1e6:.1f} MB, "
			f"build {t1 - t0:.2f} s, write {t2 - t1:.2f} s, provenance {records[-1]['provenance']}", flush=True)

	south_west = grid.TileFrame(grid.TILE_MIN, grid.TILE_MIN)
	north_east = grid.TileFrame(grid.TILE_MAX, grid.TILE_MAX)
	sw, ne = south_west.region(*REGION_HEIGHTS), north_east.region(*REGION_HEIGHTS)
	write_json(tiles_root / "tileset.json", {
		"asset": {"version": "1.1", "generator": glb.GENERATOR},
		"geometricError": ALWAYS_REFINE,
		"root": {"boundingVolume": {"region": [sw[0], sw[1], ne[2], ne[3], REGION_HEIGHTS[0], REGION_HEIGHTS[1]]},
			"geometricError": ALWAYS_REFINE, "refine": "REPLACE", "children": children},
	})
	finished = time.perf_counter()

	inventory = []
	for file in sorted(p for p in tiles_root.rglob("*") if p.is_file() and p.name != "manifest.json"):
		inventory.append({"path": file.relative_to(tiles_root).as_posix(), "bytes": file.stat().st_size, "sha256": sha256(file)})
	raw = [{"name": raster.name, "role": {tiles.SLICE: "slice merged raster", tiles.CUDEM: "CUDEM 1/9 arc-second, 4x overview"}[code]}
		for raster, code in sources.rasters]
	write_json(tiles_root / "manifest.json", {
		"description": "World streaming spike workload: harbor tiles v1 grid, 4 x 4 chunks of 1 km per tile, L0 and L1 per chunk.",
		"note": "Benchmark geometry only. Buildings are synthetic stand-ins; terrain is real where the sources have data.",
		"lod": {"switch_distance_m": LOD_SWITCH_DISTANCE_M, "l1_geometric_error_m": L1_GEOMETRIC_ERROR_M,
			"reference_screen_height": REFERENCE_SCREEN_HEIGHT, "reference_fov_deg": REFERENCE_FOV_DEG,
			"maximum_screen_space_error": MAXIMUM_SCREEN_SPACE_ERROR},
		"grid": {"tile_size_m": grid.TILE_SIZE_M, "chunk_size_m": tiles.CHUNK_SIZE_M, "l0_cells": tiles.L0_CELLS, "l1_cells": tiles.L1_CELLS},
		"sources": raw, "geoid": sources.geoid_note,
		"timings_s": {"load_sources": round(loaded - started, 3), "generate_and_write": round(finished - loaded, 3), "total": round(finished - started, 3)},
		"tiles": records,
		"files": inventory,
	})
	total = sum(r["bytes"] for r in records)
	print(f"{len(records)} tiles, {total / 1e6:.1f} MB, sources {loaded - started:.1f} s, tiles {finished - loaded:.1f} s")


if __name__ == "__main__":
	main()
