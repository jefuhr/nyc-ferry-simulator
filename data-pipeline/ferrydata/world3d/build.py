"""Writes data/processed/harbor/3dtiles/ from a harbor tiles v1 build: local 3D Tiles 1.1, deterministically.

Layout:
- tileset.json: the root; one child per active tile, a `region` around its content, its content an external tileset;
- tiles/<id>/tileset.json: the tile's `transform` (its ENU frame to ECEF) and its tree: the whole tile at level 2
  (l2.glb), refining to 16 chunks of 1 km at level 1 (c<row>_<column>_l1.glb), each refining to level 0 (_l0.glb);
- index.json: every other file's path, size and SHA-256, the sources' hashes, and what each tile holds.

Nothing in it carries a time, so two builds from the same inputs are byte-identical. The build writes into a sibling
folder and swaps it in, so a failed build leaves the previous output as it was.
"""

import hashlib
import json
import math
import resource
import shutil
import sys
import time
from pathlib import Path

import numpy as np

from ferrydata import geodesy, manifest, paths
from ferrydata.world3d import frames, glb, heights, mesh

SCHEMA = "harbor-3dtiles-v1"
# One LOD policy, as the world-streaming spike measured it: level 1 refines to level 0 within about 2 km of the camera
# at the reference view (1080 rows, a 60 degree vertical field of view, Cesium's default maximum screen-space error
# of 16 pixels). Level 2 refines to level 1 four times as far out, as its lattice is four times coarser.
LOD_SWITCH_DISTANCE_M = 2000.0
REFERENCE_SCREEN_HEIGHT = 1080
REFERENCE_FOV_DEG = 60.0
MAXIMUM_SCREEN_SPACE_ERROR = 16.0
GEOMETRIC_ERROR_M = {0: 0.0}
GEOMETRIC_ERROR_M[1] = MAXIMUM_SCREEN_SPACE_ERROR * LOD_SWITCH_DISTANCE_M * 2.0 * math.tan(math.radians(REFERENCE_FOV_DEG / 2)) / REFERENCE_SCREEN_HEIGHT
GEOMETRIC_ERROR_M[2] = 4.0 * GEOMETRIC_ERROR_M[1]
# Nodes without content must always refine to reach their content: these refine within 500 km and 5,000 km.
TILE_REFERENCE_ERROR_M = 1.0e4
ROOT_ERROR_M = 1.0e5
BOX_PAD_M = 0.01
BARGE_KINDS = ("berth-face", "barge-side", "barge-end")
FLOATING_LAYERS = ("PONTON", "FLODOC", "HULKES")
FLOATING_WATLEV = 7
STAGING_SUFFIX = ".building"


def default_output():
	return paths.processed_dir("harbor") / "3dtiles"


def read_json(path):
	return json.loads(Path(path).read_text(encoding="utf-8"))


def write_json(path, document):
	Path(path).write_text(json.dumps(document, indent="\t", ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")


def active_tiles(harbor_dir):
	"""(i, j) of every active tile in the harbor index, south to north, then west to east."""
	index = read_json(Path(harbor_dir) / "index.json")
	if index.get("schema") != "harbor-tiles-v1":
		raise ValueError(f"{harbor_dir}/index.json is not a harbor tiles v1 index")
	return sorted((t["i"], t["j"]) for t in index["tiles"] if t["active"]), index


SOURCE_FILES = ("depth_merged_navd88.tif", "geoid_offset.json", "shoreline_constructions.geojson", "landing_structures.geojson")


def source_records(harbor_dir, index, active):
	"""Each input file this build reads, with its SHA-256, checked against the harbor index; a mismatch fails the build."""
	harbor_dir = Path(harbor_dir)
	recorded = {o["file"]: o["sha256"] for t in index["tiles"] for o in t.get("outputs", [])}
	records = []
	for i, j in active:
		for name in SOURCE_FILES:
			relative = f"tiles/{frames.tile_id(i, j)}/{name}"
			path = harbor_dir / relative
			if not path.is_file():
				raise FileNotFoundError(f"{path} is missing; run ferrydata build --harbor")
			digest = manifest.sha256_file(path)
			if relative in recorded and recorded[relative] != digest:
				raise ValueError(f"{relative} differs from the harbor index's hash; rebuild the harbor tiles")
			records.append({"tile": frames.tile_id(i, j), "file": relative, "sha256": digest})
	return records


def barge_lnams(sources):
	"""LNAMs of floating landing barges: every structure a landing file gives for a barge-kind segment, in any tile."""
	lnams = set()
	for i, j in sources.active:
		document = read_json(sources.tile_dir(i, j) / "landing_structures.geojson")
		for feature in document["features"]:
			for segment in feature["properties"].get("segments", []):
				if segment.get("kind") in BARGE_KINDS and segment.get("lnam"):
					lnams.add(segment["lnam"])
	return lnams


def _lines(geometry):
	kind = geometry["type"] if geometry else None
	coords = geometry.get("coordinates") if geometry else None
	if kind == "LineString":
		yield coords
	elif kind in ("Polygon", "MultiLineString"):
		yield from coords
	elif kind == "MultiPolygon":
		for polygon in coords:
			yield from polygon
	elif kind == "GeometryCollection":
		for part in geometry["geometries"]:
			yield from _lines(part)


def tile_structures(sources, frame, barges):
	"""The tile's wall segments ((k, 2, 2) world [north, east]) and what it drew and left out.

	Every chart structure in the tile's shoreline_constructions.geojson except point objects (no extent) and landing
	barges. Each segment is clipped to the tile's square and kept where the tile owns its midpoint, so a structure
	across a seam is drawn once, in parts.
	"""
	document = read_json(sources.tile_dir(frame.i, frame.j) / "shoreline_constructions.geojson")
	features = sorted(document["features"], key=lambda f: str(f["properties"].get("id")))
	segments, owners = [], []
	drawn, floating, left_out_barges, left_out_points = [], [], [], []
	for feature in features:
		p = feature["properties"]
		lnam = p.get("id")
		lines = [np.asarray(line, dtype=np.float64)[:, :2] for line in _lines(feature.get("geometry")) if len(line) >= 2]
		if lnam in barges:
			left_out_barges.append(lnam)
			continue
		if not lines:
			left_out_points.append(lnam)
			continue
		pieces = []
		for line in lines:
			north, east = geodesy.to_local(line[:, 1], line[:, 0])
			# Remove only the nanometre-scale round trip through ECEF, so points on a tile edge sit exactly on it.
			ne = np.round(np.stack([north, east], axis=1), 8)
			a, b = mesh.clip(ne[:-1], ne[1:], frame.north0, frame.north1, frame.east0, frame.east1)
			if len(a):
				pieces.append(np.stack([a, b], axis=1))
		if not pieces:
			continue
		pieces = np.concatenate(pieces)
		middle = 0.5 * (pieces[:, 0] + pieces[:, 1])
		oi, oj = sources.owners.owner(middle[:, 0], middle[:, 1])
		mine = pieces[(oi == frame.i) & (oj == frame.j)]
		if not len(mine):
			continue
		segments.append(mine)
		drawn.append(lnam)
		if p.get("feature_class") in FLOATING_LAYERS or p.get("water_level_effect_code") == FLOATING_WATLEV:
			floating.append({"lnam": lnam, "layer": p.get("feature_class"), "water_level_effect_code": p.get("water_level_effect_code"),
				"segments": int(len(mine))})
	segments = np.concatenate(segments) if segments else np.zeros((0, 2, 2))
	return segments, {"drawn": sorted(set(drawn)), "floating_drawn": floating, "left_out_barges": sorted(set(left_out_barges)),
		"left_out_points": sorted(set(left_out_points))}


def chunk_segments(segments, frame, row, column):
	"""The parts of a tile's wall segments in chunk (row, column): clipped to it, kept where it holds the midpoint."""
	if not len(segments):
		return segments
	size = frame.north1 - frame.north0
	step = size / mesh.CHUNKS_PER_SIDE
	n0, e0 = frame.north0 + row * step, frame.east0 + column * step
	a, b = mesh.clip(segments[:, 0], segments[:, 1], n0, n0 + step, e0, e0 + step)
	if not len(a):
		return np.zeros((0, 2, 2))
	middle = 0.5 * (a + b)
	r = np.minimum(np.floor((middle[:, 0] - frame.north0) / step), mesh.CHUNKS_PER_SIDE - 1)
	c = np.minimum(np.floor((middle[:, 1] - frame.east0) / step), mesh.CHUNKS_PER_SIDE - 1)
	keep = (r == row) & (c == column)
	return np.stack([a[keep], b[keep]], axis=1)


def box(positions_gltf):
	"""3D Tiles `box` in the tile frame (z up) around content positions, padded."""
	enu = frames.enu_from_gltf(np.asarray(positions_gltf, dtype=np.float64))
	low, high = enu.min(axis=0), enu.max(axis=0)
	centre, half = 0.5 * (low + high), 0.5 * (high - low) + BOX_PAD_M
	return [float(centre[0]), float(centre[1]), float(centre[2]), float(half[0]), 0.0, 0.0, 0.0, float(half[1]), 0.0, 0.0, 0.0, float(half[2])]


def content_region(frame, positions_gltf):
	ecef = frame.ecef(frames.enu_from_gltf(np.asarray(positions_gltf, dtype=np.float64)))
	lat, lon, h = geodesy.from_ecef(ecef[:, 0], ecef[:, 1], ecef[:, 2])
	return frames.region(lat, lon, h)


def build_tile(lattice, sources, frame, barges, folder):
	"""Writes one tile's GLBs and tileset.json into `folder`; returns its index record and its root region."""
	folder.mkdir(parents=True, exist_ok=True)
	segments, structures = tile_structures(sources, frame, barges)
	grids = {level: mesh.TileGrid(lattice, frame, level) for level in mesh.LEVELS}
	stats = {f"L{level}": {"files": 0, "bytes": 0, "vertices": 0, "triangles": 0} for level in mesh.LEVELS}
	every_position = []

	def write(name, parts, level):
		positions, normals, colours, indices = parts.arrays()
		data = glb.encode(positions, normals, colours, indices)
		(folder / name).write_bytes(data)
		record = stats[f"L{level}"]
		record["files"] += 1
		record["bytes"] += len(data)
		record["vertices"] += len(positions)
		record["triangles"] += len(indices) // 3
		every_position.append(positions)
		return positions

	children = []
	walls_per_chunk = {}
	for row in range(mesh.CHUNKS_PER_SIDE):
		for column in range(mesh.CHUNKS_PER_SIDE):
			chunk_walls = chunk_segments(segments, frame, row, column)
			walls_per_chunk[f"c{row}_{column}"] = int(len(chunk_walls))
			contents = {}
			for level in (0, 1):
				cells = mesh.LEVELS[level]["cells"]
				parts = mesh.terrain(grids[level], row * cells, (row + 1) * cells, column * cells, (column + 1) * cells)
				mesh.walls(parts, frame, sources, chunk_walls)
				contents[level] = write(f"c{row}_{column}_l{level}.glb", parts, level)
			volume = box(np.concatenate([contents[0], contents[1]]))
			children.append({"boundingVolume": {"box": volume}, "geometricError": GEOMETRIC_ERROR_M[1],
				"content": {"uri": f"c{row}_{column}_l1.glb"},
				"children": [{"boundingVolume": {"box": volume}, "geometricError": GEOMETRIC_ERROR_M[0],
					"content": {"uri": f"c{row}_{column}_l0.glb"}}]})
	cells = mesh.LEVELS[2]["cells"]
	write("l2.glb", mesh.terrain(grids[2], 0, cells, 0, cells), 2)
	everything = np.concatenate(every_position)
	write_json(folder / "tileset.json", {
		"asset": {"version": "1.1", "generator": glb.GENERATOR},
		"geometricError": TILE_REFERENCE_ERROR_M,
		"root": {"transform": frame.transform(), "boundingVolume": {"box": box(everything)}, "geometricError": GEOMETRIC_ERROR_M[2],
			"refine": "REPLACE", "content": {"uri": "l2.glb"}, "children": children},
	})
	tile_region = content_region(frame, everything)
	nodata = int((~grids[0].valid).sum())
	record = {"id": frame.id, "i": frame.i, "j": frame.j,
		"frame": {"latitude": frame.latitude, "longitude": frame.longitude, "origin_ecef": [float(v) for v in frame.origin],
			"transform": frame.transform()},
		"levels": stats, "bytes": sum(s["bytes"] for s in stats.values()), "nodata_l0_vertices": nodata,
		"walls": {"segments": int(len(segments)), "per_chunk": walls_per_chunk}, "structures": structures, "region": tile_region}
	return record, tile_region


def file_records(root):
	files = []
	for path in sorted(p for p in root.rglob("*") if p.is_file() and p.name != "index.json"):
		files.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size, "sha256": manifest.sha256_file(path)})
	return files


def _replace(staging, out):
	if out.exists():
		marker = out / "index.json"
		if any(out.iterdir()) and not (marker.is_file() and read_json(marker).get("schema") == SCHEMA):
			raise ValueError(f"refusing to replace {out}: it is not a harbor 3D Tiles output (no {SCHEMA} index.json)")
		shutil.rmtree(out)
	staging.rename(out)


def build(harbor_dir=None, out_dir=None, log=print):
	"""Builds the harbor's 3D Tiles from `harbor_dir` (data/processed/harbor) into `out_dir` (its 3dtiles/)."""
	started = time.perf_counter()
	harbor_dir = Path(harbor_dir) if harbor_dir else paths.processed_dir("harbor")
	out = Path(out_dir) if out_dir else default_output()
	active, index = active_tiles(harbor_dir)
	if not active:
		raise ValueError(f"{harbor_dir}/index.json has no active tile")
	sources_record = source_records(harbor_dir, index, active)
	sources = heights.Sources(harbor_dir, active)
	log(f"[world3d] {len(active)} active tiles; heights on one lattice")
	lattice = heights.Lattice(sources, ring=mesh.LEVELS[2]["stride"], log=log)
	barges = barge_lnams(sources)
	log(f"[world3d] {len(barges)} landing barges left out: {', '.join(sorted(barges))}")
	staging = out.with_name(out.name + STAGING_SUFFIX)
	if staging.exists():
		shutil.rmtree(staging)
	(staging / "tiles").mkdir(parents=True)
	records, children = [], []
	for i, j in active:
		frame = frames.TileFrame(i, j)
		t0 = time.perf_counter()
		record, region = build_tile(lattice, sources, frame, barges, staging / "tiles" / frame.id)
		records.append(record)
		children.append({"boundingVolume": {"region": region}, "geometricError": TILE_REFERENCE_ERROR_M,
			"content": {"uri": f"tiles/{frame.id}/tileset.json"}})
		log(f"  {frame.id}: {record['bytes'] / 1e6:.1f} MB, {record['walls']['segments']} wall segments, "
			f"{len(record['structures']['floating_drawn'])} floating structures drawn fixed, {time.perf_counter() - t0:.1f} s")
	write_json(staging / "tileset.json", {
		"asset": {"version": "1.1", "generator": glb.GENERATOR},
		"geometricError": ROOT_ERROR_M,
		"root": {"boundingVolume": {"region": frames.union_region([c["boundingVolume"]["region"] for c in children])},
			"geometricError": ROOT_ERROR_M, "refine": "REPLACE", "children": children},
	})
	files = file_records(staging)
	levels = {name: {key: sum(r["levels"][name][key] for r in records) for key in ("files", "bytes", "vertices", "triangles")}
		for name in records[0]["levels"]}
	write_json(staging / "index.json", {
		"schema": SCHEMA,
		"description": "The harbor's terrain and fixed chart structures as local 3D Tiles 1.1, written by ferrydata.world3d from harbor tiles v1.",
		"tileset": "tileset.json",
		"frames": "Each tile's content is in its own east-north-up frame at the tile's centre on the WGS84 ellipsoid, through ECEF; "
			"glTF content is y-up (x east, y up, z south). The tile's transform pins the frame to the globe.",
		"heights": "WGS84 ellipsoidal metres: NAVD88 from the owning tile's merged raster plus its geoid offset, bilinear between "
			"the corners of its grown square (geoid_offset.json).",
		"owner_rule": "A lattice point's height comes from the tile whose half-open square holds it (a tile owns its south and west "
			"edges); if that tile is inactive, from the nearest active tile whose 250 m grown square holds it, lowest (i, j) on a tie.",
		"levels": [{"level": level, "spacing_m": heights.SIZE_M / heights.L0_CELLS_PER_TILE * spec["stride"],
			"vertices_per_side": spec["cells"] + 1, "node": spec["node"], "geometric_error_m": GEOMETRIC_ERROR_M[level]}
			for level, spec in mesh.LEVELS.items()],
		"lod": {"switch_distance_m": LOD_SWITCH_DISTANCE_M, "reference_screen_height": REFERENCE_SCREEN_HEIGHT,
			"reference_fov_deg": REFERENCE_FOV_DEG, "maximum_screen_space_error": MAXIMUM_SCREEN_SPACE_ERROR,
			"tile_reference_error_m": TILE_REFERENCE_ERROR_M, "root_error_m": ROOT_ERROR_M},
		"parameters": {"skirt_depth_m": mesh.SKIRT_DEPTH_M, "wall_navd88_m": [mesh.WALL_BOTTOM_NAVD88_M, mesh.WALL_TOP_NAVD88_M],
			"structure_colour_rgba": [int(v) for v in mesh.STRUCTURE_COLOUR], "box_pad_m": BOX_PAD_M,
			"region_pad_rad": frames.REGION_PAD_RAD, "region_pad_m": frames.REGION_PAD_M},
		"left_out": "Floating landing barges (every structure a landing file gives for a barge-kind segment), point objects, "
			"aids, bridges and buildings. Other floating chart structures (PONTON, FLODOC, HULKES, WATLEV 7) are drawn fixed.",
		"sources": sources_record,
		"tiles": records,
		"totals": {"tiles": len(records), "files": len(files) + 1, "bytes": sum(f["bytes"] for f in files), "levels": levels},
		"files": files,
	})
	_replace(staging, out)
	peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
	metrics = {"elapsed_s": round(time.perf_counter() - started, 3), "peak_rss_bytes": int(peak if sys.platform == "darwin" else peak * 1024),
		"tiles": len(records), "bytes": sum(f["bytes"] for f in files), "files": len(files) + 1, "output": str(out)}
	log(f"[world3d] wrote {metrics['files']} files, {metrics['bytes'] / 1e6:.1f} MB, in {metrics['elapsed_s']:.1f} s; "
		f"peak RSS {metrics['peak_rss_bytes'] / 2 ** 20:.0f} MiB")
	return metrics


def digest(out_dir):
	"""One SHA-256 over every file's path and bytes, for comparing two builds."""
	h = hashlib.sha256()
	out = Path(out_dir)
	for path in sorted(p for p in out.rglob("*") if p.is_file()):
		h.update(path.relative_to(out).as_posix().encode() + b"\0")
		h.update(path.read_bytes())
	return h.hexdigest()
