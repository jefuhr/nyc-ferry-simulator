"""Checks a harbor 3D Tiles build and fails loudly, naming the tile and the file.

Checks, each reported in validation.md and validation.json:
- index: index.json lists exactly the files on disk, with their sizes and SHA-256, and its sources are unchanged;
- structure: the root has one child per active harbor tile, each tile's tree is the expected one, every content file
  exists and every file is used;
- glb: every GLB parses back with the strict reader (finite vertices, valid indices, the declared counts);
- bounds: every bounding volume contains its own and its descendants' content, and the root's regions their tiles';
- geometric-error: it drops from parent to child, to 0 at the leaves;
- frame: each tile's transform is its ENU frame, and puts the tile's corner and centre vertices where
  ferrydata.geodesy puts them at their expected ellipsoidal height (ECEF within 1 mm);
- cracks: chunk edges inside a tile are bit-identical, and neighbouring tiles' shared edges meet in ECEF within 1 mm,
  at every level;
- floating: no landing barge is drawn as a fixed wall;
- slice: where the tiles overlap the slice, terrain heights agree with the slice's imported terrain;
- size: the total and per-level sizes, with any tile over 25 MB a failure.
"""

import json
import math
from pathlib import Path

import numpy as np
import rasterio
from rasterio.transform import Affine

from ferrydata import geodesy, manifest, paths
from ferrydata.world3d import build, frames, glb, heights, mesh

FRAME_TOLERANCE_M = 1e-3
CRACK_TOLERANCE_M = 1e-3
BOUNDS_TOLERANCE_M = 1e-4
# Slice agreement: the harbor tiles' terrain against the grid the slice's terrain was imported from (unity/depth.f32).
# Both come from the same merged lattice, so nearly every vertex agrees to float rounding; the harbor merge differs
# from the slice's in a few cells (a chart correction, the slice's own edge), so a small share may differ by more.
SLICE_TOLERANCE_M = 0.01
SLICE_FRACTION = 0.999
SLICE_MAXIMUM_M = 1.0
TILE_BUDGET_BYTES = 25_000_000
# A barge drawn as a wall covers all of its outline. Fixed piers charted along a barge's sides cover part of it
# (up to 44% at St. George in the current data), so the limit sits well above that and well below a whole outline.
BARGE_COVERAGE_LIMIT = 0.9
BARGE_SAMPLE_M = 0.5
BARGE_DISTANCE_M = 0.02
CHECKS = ("index", "structure", "glb", "bounds", "geometric-error", "frame", "cracks", "floating", "slice", "size")


def default_slice_dir():
	return paths.processed_dir("lower-east-river")


def default_import_manifest():
	return paths.REPO_ROOT / "unity" / "Assets" / "FerrySim" / "World" / "Generated" / "LowerEastRiver" / "import-manifest.json"


def default_report_dir():
	return paths.REPO_ROOT / "artifacts" / "world3d"


class Results:
	def __init__(self, log):
		self.items = []
		self.log = log

	def add(self, check, ok, message, tile="", file=""):
		self.items.append({"check": check, "ok": bool(ok), "tile": tile, "file": file, "message": message})
		if not ok:
			self.log(f"FAIL [{check}] {tile} {file}: {message}")

	def ok(self, check, message, tile="", file=""):
		self.add(check, True, message, tile, file)

	def fail(self, check, message, tile="", file=""):
		self.add(check, False, message, tile, file)

	def failed(self, check):
		return any(not r["ok"] and r["check"] == check for r in self.items)


def tile_of(path):
	parts = Path(path).parts
	return parts[1] if len(parts) > 2 and parts[0] == "tiles" else ""


def check_index(out, index, harbor_dir, results):
	listed = {}
	for entry in index.get("files", []):
		if entry["path"] in listed:
			results.fail("index", "listed twice", tile_of(entry["path"]), entry["path"])
		listed[entry["path"]] = entry
	on_disk = sorted(p.relative_to(out).as_posix() for p in out.rglob("*") if p.is_file() and p.name != "index.json")
	for path in on_disk:
		if path not in listed:
			results.fail("index", "on disk but not in index.json", tile_of(path), path)
	bad = 0
	for path, entry in sorted(listed.items()):
		target = out / path
		if not target.is_file():
			results.fail("index", "in index.json but missing on disk", tile_of(path), path)
			bad += 1
			continue
		size = target.stat().st_size
		digest = manifest.sha256_file(target)
		if size != entry["bytes"] or digest != entry["sha256"]:
			results.fail("index", f"size or SHA-256 differs from index.json ({size} bytes, {digest[:12]}, listed {entry['bytes']}, "
				f"{entry['sha256'][:12]})", tile_of(path), path)
			bad += 1
	for source in index.get("sources", []):
		path = Path(harbor_dir) / source["file"]
		if not path.is_file() or manifest.sha256_file(path) != source["sha256"]:
			results.fail("index", f"source {source['file']} changed since the build: rebuild the 3D Tiles", source["tile"], source["file"])
			bad += 1
	if not bad and not results.failed("index"):
		results.ok("index", f"{len(listed)} files match index.json; {len(index.get('sources', []))} sources unchanged")


def expected_tree(tile):
	"""(uri, level, children uris) of a tile's tree: l2, 16 chunks at l1, each with its l0."""
	chunks = [f"c{r}_{c}" for r in range(mesh.CHUNKS_PER_SIDE) for c in range(mesh.CHUNKS_PER_SIDE)]
	return "l2.glb", [(f"{chunk}_l1.glb", f"{chunk}_l0.glb") for chunk in chunks]


def check_structure(out, active, results):
	"""Parses the root and every tile tileset; returns {tile id: tileset document} for the tiles that parse."""
	documents = {}
	try:
		root = build.read_json(out / "tileset.json")
	except (OSError, ValueError) as error:
		results.fail("structure", f"the root tileset does not parse: {error}", "", "tileset.json")
		return documents, None
	if root.get("asset", {}).get("version") != "1.1":
		results.fail("structure", "asset.version is not 1.1", "", "tileset.json")
	children = root.get("root", {}).get("children", [])
	seen = {}
	for child in children:
		uri = child.get("content", {}).get("uri", "")
		tile = tile_of(uri)
		if uri != f"tiles/{tile}/tileset.json":
			results.fail("structure", f"unexpected root child content {uri!r}", tile, "tileset.json")
			continue
		seen[tile] = child
	wanted = {frames.tile_id(i, j) for i, j in active}
	for tile in sorted(wanted - set(seen)):
		results.fail("structure", "an active harbor tile is missing from the root tileset", tile, "tileset.json")
	for tile in sorted(set(seen) - wanted):
		results.fail("structure", "the root tileset has a tile that is not active in the harbor index", tile, "tileset.json")
	used = {"tileset.json", "index.json"}
	for tile in sorted(seen):
		path = f"tiles/{tile}/tileset.json"
		used.add(path)
		try:
			document = build.read_json(out / path)
		except (OSError, ValueError) as error:
			results.fail("structure", f"the tile's tileset does not parse: {error}", tile, path)
			continue
		node = document.get("root", {})
		problems = []
		if document.get("asset", {}).get("version") != "1.1":
			problems.append("asset.version is not 1.1")
		if len(node.get("transform", [])) != 16:
			problems.append("the root has no 16-number transform")
		l2, chunks = expected_tree(tile)
		if node.get("content", {}).get("uri") != l2:
			problems.append(f"the root's content is not {l2}")
		kids = node.get("children", [])
		if [k.get("content", {}).get("uri") for k in kids] != [c[0] for c in chunks]:
			problems.append("the root's children are not the 16 level-1 chunks in row order")
		else:
			for kid, (_, l0) in zip(kids, chunks):
				grand = kid.get("children", [])
				if len(grand) != 1 or grand[0].get("content", {}).get("uri") != l0 or grand[0].get("children"):
					problems.append(f"{kid['content']['uri']} does not refine to {l0} alone")
		for problem in problems:
			results.fail("structure", problem, tile, path)
		for uri in [l2] + [u for pair in chunks for u in pair]:
			used.add(f"tiles/{tile}/{uri}")
			if not (out / "tiles" / tile / uri).is_file():
				results.fail("structure", "content file is missing", tile, f"tiles/{tile}/{uri}")
		documents[tile] = document
	for path in sorted(p.relative_to(out).as_posix() for p in out.rglob("*") if p.is_file()):
		if path not in used:
			results.fail("structure", "file is not part of the tileset", tile_of(path), path)
	if not results.failed("structure"):
		results.ok("structure", f"root has the {len(wanted)} active tiles; every tile has l2, 16 l1 and 16 l0 contents")
	return documents, root


def nodes(document):
	"""(node, parent) pairs of a tileset, depth first."""
	stack = [(document["root"], None)]
	while stack:
		node, parent = stack.pop(0)
		yield node, parent
		stack[0:0] = [(child, node) for child in node.get("children", [])]


def check_glbs(out, documents, results):
	meshes = {}
	count = 0
	for tile, document in sorted(documents.items()):
		for node, _ in nodes(document):
			uri = node.get("content", {}).get("uri")
			if not uri:
				continue
			path = f"tiles/{tile}/{uri}"
			try:
				_, mesh_arrays = glb.decode((out / path).read_bytes())
			except (OSError, glb.GlbError) as error:
				results.fail("glb", str(error), tile, path)
				continue
			meshes[(tile, uri)] = mesh_arrays
			count += 1
	if not results.failed("glb"):
		results.ok("glb", f"{count} GLBs parse back with finite vertices, valid indices and their declared counts")
	return meshes


def enu_of(mesh_arrays):
	return frames.enu_from_gltf(mesh_arrays["POSITION"].astype(np.float64))


def inside_box(points, volume):
	"""Largest excess (m) of points beyond a 3D Tiles box, 0 when all are inside."""
	box = np.asarray(volume, dtype=np.float64)
	centre, axes = box[:3], box[3:].reshape(3, 3)
	lengths = np.linalg.norm(axes, axis=1)
	local = np.abs((points - centre) @ (axes / lengths[:, None]).T)
	return float(np.max(local - lengths)) if len(points) else 0.0


def ecef_of(mesh_arrays, transform):
	return frames.ecef_from_content(mesh_arrays["POSITION"], transform)


def check_bounds(documents, root, meshes, results):
	children = {tile_of(c["content"]["uri"]): c for c in (root or {}).get("root", {}).get("children", [])}
	regions = []
	for tile, document in sorted(documents.items()):
		content = {}
		for node, _ in nodes(document):
			uri = node.get("content", {}).get("uri")
			if (tile, uri) in meshes:
				content[uri] = enu_of(meshes[(tile, uri)])

		def descendants(node):
			points = [content[node["content"]["uri"]]] if node.get("content", {}).get("uri") in content else []
			for child in node.get("children", []):
				points.extend(descendants(child))
			return points

		for node, _ in nodes(document):
			uri = node.get("content", {}).get("uri", "")
			volume = node.get("boundingVolume", {}).get("box")
			if volume is None:
				results.fail("bounds", "the node has no box", tile, f"tiles/{tile}/{uri}")
				continue
			points = descendants(node)
			excess = inside_box(np.concatenate(points), volume) if points else 0.0
			if excess > BOUNDS_TOLERANCE_M:
				results.fail("bounds", f"content lies {excess:.4f} m outside the node's box", tile, f"tiles/{tile}/{uri}")
		child = children.get(tile)
		if child is None or "region" not in child.get("boundingVolume", {}):
			continue
		region = child["boundingVolume"]["region"]
		regions.append(region)
		transform = document["root"]["transform"]
		points = np.concatenate([ecef_of(meshes[(tile, uri)], transform) for (t, uri) in meshes if t == tile])
		lat, lon, h = geodesy.from_ecef(points[:, 0], points[:, 1], points[:, 2])
		lat, lon = np.radians(lat), np.radians(lon)
		outside = (lon < region[0]) | (lat < region[1]) | (lon > region[2]) | (lat > region[3]) | (h < region[4]) | (h > region[5])
		if outside.any():
			results.fail("bounds", f"{int(outside.sum())} vertices lie outside the tile's region in the root tileset", tile, "tileset.json")
	if root is not None and regions:
		outer = root["root"]["boundingVolume"].get("region")
		union = frames.union_region(regions)
		if outer is None or outer[0] > union[0] or outer[1] > union[1] or outer[2] < union[2] or outer[3] < union[3] \
				or outer[4] > union[4] or outer[5] < union[5]:
			results.fail("bounds", "the root's region does not contain its children's regions", "", "tileset.json")
	if not results.failed("bounds"):
		results.ok("bounds", "every box contains its own and its descendants' content; every region contains its tile")


def check_geometric_error(documents, root, results):
	if root is not None:
		top = root.get("geometricError", -1)
		node = root["root"]
		if top < node.get("geometricError", math.inf):
			results.fail("geometric-error", "the tileset's geometricError is below its root's", "", "tileset.json")
		for child in node.get("children", []):
			if not child.get("geometricError", math.inf) < node["geometricError"]:
				results.fail("geometric-error", "a tile's reference does not have a smaller error than the root",
					tile_of(child["content"]["uri"]), "tileset.json")
	references = {tile_of(c["content"]["uri"]): c.get("geometricError", 0) for c in (root or {}).get("root", {}).get("children", [])}
	for tile, document in sorted(documents.items()):
		node = document["root"]
		if tile in references and not node.get("geometricError", math.inf) < references[tile]:
			results.fail("geometric-error", "the tile's root error is not below its reference's", tile, f"tiles/{tile}/tileset.json")
		for node, parent in nodes(document):
			uri = node.get("content", {}).get("uri", "")
			error = node.get("geometricError")
			if not isinstance(error, (int, float)) or error < 0:
				results.fail("geometric-error", "missing or negative geometricError", tile, f"tiles/{tile}/{uri}")
				continue
			if parent is not None and not error < parent["geometricError"]:
				results.fail("geometric-error", f"{error} is not below its parent's {parent['geometricError']}", tile, f"tiles/{tile}/{uri}")
			if not node.get("children") and error != 0:
				results.fail("geometric-error", f"a leaf with error {error}, not 0", tile, f"tiles/{tile}/{uri}")
	if not results.failed("geometric-error"):
		results.ok("geometric-error", "errors drop from the root through every tile's levels to 0 at the leaves")


L0_SIDE = mesh.LEVELS[0]["cells"] + 1


def frame_points(i, j):
	"""(name, chunk file, vertex, lattice gn, ge) of a tile's corner and centre vertices at level 0."""
	n0, e0, size, half = heights.L0_CELLS_PER_TILE * i, heights.L0_CELLS_PER_TILE * j, heights.L0_CELLS_PER_TILE, heights.L0_CELLS_PER_TILE // 2
	last = L0_SIDE - 1
	return [("south-west corner", "c0_0_l0.glb", 0, n0, e0), ("south-east corner", "c0_3_l0.glb", last, n0, e0 + size),
		("north-west corner", "c3_0_l0.glb", last * L0_SIDE, n0 + size, e0), ("north-east corner", "c3_3_l0.glb", L0_SIDE * L0_SIDE - 1, n0 + size, e0 + size),
		("centre", "c2_2_l0.glb", 0, n0 + half, e0 + half)]


def expected_heights(sources, north, east):
	"""Ellipsoidal heights the owner rule gives at world points: NAVD88 (0 where nodata) plus the owner's geoid offset."""
	north, east = np.asarray(north, dtype=np.float64), np.asarray(east, dtype=np.float64)
	lat, lon = geodesy.from_local(north, east)
	oi, oj = sources.owners.owner(north, east)
	result = np.full(north.shape, np.nan)
	for i, j in sources.active:
		mask = (oi == i) & (oj == j)
		if mask.any():
			navd88 = sources.raster(i, j).sample(lat[mask], lon[mask])
			result[mask] = np.where(np.isfinite(navd88), navd88, 0.0) + sources.geoid(i, j)(north[mask], east[mask])
	return lat, lon, result


def check_frames(documents, meshes, sources, results):
	worst = 0.0
	for tile, document in sorted(documents.items()):
		frame = frames.TileFrame(*map(int, tile.split("_")[1:]))
		transform = document["root"].get("transform")
		m = frames.matrix(transform)
		rotation = m[:3, :3]
		if np.abs(rotation.T @ rotation - np.eye(3)).max() > 1e-12 or abs(np.linalg.det(rotation) - 1) > 1e-12:
			results.fail("frame", "the transform's rotation is not a proper rotation", tile, f"tiles/{tile}/tileset.json")
		lat, lon = geodesy.from_local(frame.centre_north, frame.centre_east)
		origin = np.array(geodesy.to_ecef(lat, lon, 0.0))
		if np.linalg.norm(m[:3, 3] - origin) > FRAME_TOLERANCE_M:
			results.fail("frame", f"the transform's origin is {np.linalg.norm(m[:3, 3] - origin):.4f} m from the tile centre on the ellipsoid",
				tile, f"tiles/{tile}/tileset.json")
		for name, axis, index in (("east", frame.east, 0), ("north", frame.north, 1), ("up", frame.up, 2)):
			if np.abs(rotation[:, index] - axis).max() > 1e-9:
				results.fail("frame", f"the transform's {name} axis is not the tile centre's {name}", tile, f"tiles/{tile}/tileset.json")
		points = frame_points(frame.i, frame.j)
		north = heights.coordinate([p[3] for p in points])
		east = heights.coordinate([p[4] for p in points])
		plat, plon, h = expected_heights(sources, north, east)
		expected = np.stack(geodesy.to_ecef(plat, plon, h), axis=-1)
		for (name, uri, vertex, _, _), want, height in zip(points, expected, h):
			if (tile, uri) not in meshes:
				continue
			got = frames.ecef_from_content(meshes[(tile, uri)]["POSITION"][vertex:vertex + 1], transform)[0]
			gap = float(np.linalg.norm(got - want))
			worst = max(worst, gap)
			if gap > FRAME_TOLERANCE_M:
				results.fail("frame", f"the {name} vertex is {gap:.4f} m from where ferrydata.geodesy puts it at its expected "
					f"ellipsoidal height of {height:.3f} m", tile, f"tiles/{tile}/{uri}")
	if not results.failed("frame"):
		results.ok("frame", f"every tile's transform is its ENU frame; corner and centre vertices within {worst * 1000:.3f} mm in ECEF")
	return worst


def grid_of(mesh_arrays, side):
	return mesh_arrays["POSITION"][: side * side].reshape(side, side, 3)


EDGES = {"south": lambda g: g[0], "north": lambda g: g[-1], "west": lambda g: g[:, 0], "east": lambda g: g[:, -1]}


def check_cracks(documents, meshes, results):
	"""Returns the largest gap between neighbouring tiles' shared vertices, metres."""
	worst = 0.0
	for tile in sorted(documents):
		for level in (0, 1):
			side = mesh.LEVELS[level]["cells"] + 1
			for r in range(mesh.CHUNKS_PER_SIDE):
				for c in range(mesh.CHUNKS_PER_SIDE):
					for dr, dc, mine, theirs in ((0, 1, "east", "west"), (1, 0, "north", "south")):
						if r + dr >= mesh.CHUNKS_PER_SIDE or c + dc >= mesh.CHUNKS_PER_SIDE:
							continue
						a_uri, b_uri = f"c{r}_{c}_l{level}.glb", f"c{r + dr}_{c + dc}_l{level}.glb"
						if (tile, a_uri) not in meshes or (tile, b_uri) not in meshes:
							continue
						a, b = meshes[(tile, a_uri)], meshes[(tile, b_uri)]
						same = all(EDGES[mine](a[name][: side * side].reshape(side, side, -1)).tobytes()
							== EDGES[theirs](b[name][: side * side].reshape(side, side, -1)).tobytes() for name in ("POSITION", "NORMAL", "COLOR_0"))
						if not same:
							results.fail("cracks", f"chunks {a_uri} and {b_uri} do not share their edge bit for bit", tile,
								f"tiles/{tile}/{a_uri}, tiles/{tile}/{b_uri}")
	transforms = {tile: d["root"]["transform"] for tile, d in documents.items()}
	for tile in sorted(documents):
		i, j = map(int, tile.split("_")[1:])
		for di, dj, mine, theirs in ((0, 1, "east", "west"), (1, 0, "north", "south")):
			other = frames.tile_id(i + di, j + dj)
			if other not in documents:
				continue
			pairs = [(f"c{r}_{mesh.CHUNKS_PER_SIDE - 1}_l{level}.glb", f"c{r}_0_l{level}.glb", level) if dj else
				(f"c{mesh.CHUNKS_PER_SIDE - 1}_{r}_l{level}.glb", f"c0_{r}_l{level}.glb", level)
				for level in (0, 1) for r in range(mesh.CHUNKS_PER_SIDE)] + [("l2.glb", "l2.glb", 2)]
			for a_uri, b_uri, level in pairs:
				if (tile, a_uri) not in meshes or (other, b_uri) not in meshes:
					continue
				side = mesh.LEVELS[level]["cells"] + 1
				a = EDGES[mine](frames.ecef_from_content(grid_of(meshes[(tile, a_uri)], side).reshape(-1, 3), transforms[tile]).reshape(side, side, 3))
				b = EDGES[theirs](frames.ecef_from_content(grid_of(meshes[(other, b_uri)], side).reshape(-1, 3), transforms[other]).reshape(side, side, 3))
				gap = float(np.linalg.norm(a - b, axis=1).max())
				worst = max(worst, gap)
				if gap > CRACK_TOLERANCE_M:
					results.fail("cracks", f"{gap:.4f} m between this tile's {mine} edge and {other}'s {theirs} edge at level {level}",
						tile, f"tiles/{tile}/{a_uri}, tiles/{other}/{b_uri}")
		for dj in (1, -1):
			other = frames.tile_id(i + 1, j + dj)
			if other not in documents:
				continue
			corner = mesh.CHUNKS_PER_SIDE - 1 if dj == 1 else 0
			a_uri, b_uri = f"c{mesh.CHUNKS_PER_SIDE - 1}_{corner}_l0.glb", f"c0_{mesh.CHUNKS_PER_SIDE - 1 - corner}_l0.glb"
			if (tile, a_uri) not in meshes or (other, b_uri) not in meshes:
				continue
			a = frames.ecef_from_content(grid_of(meshes[(tile, a_uri)], L0_SIDE)[-1, -1 if dj == 1 else 0][None], transforms[tile])[0]
			b = frames.ecef_from_content(grid_of(meshes[(other, b_uri)], L0_SIDE)[0, 0 if dj == 1 else -1][None], transforms[other])[0]
			gap = float(np.linalg.norm(a - b))
			worst = max(worst, gap)
			if gap > CRACK_TOLERANCE_M:
				results.fail("cracks", f"{gap:.4f} m between this tile's corner and {other}'s", tile, f"tiles/{tile}/{a_uri}, tiles/{other}/{b_uri}")
	steps = level_steps(documents, meshes)
	for (tile, uri), step in sorted(steps.items()):
		if step > mesh.SKIRT_DEPTH_M:
			results.fail("cracks", f"a {step:.2f} m step between this content's edge and the next level's is deeper than the "
				f"{mesh.SKIRT_DEPTH_M} m skirts", tile, f"tiles/{tile}/{uri}")
	largest = max(steps.values(), default=0.0)
	if not results.failed("cracks"):
		results.ok("cracks", f"chunk edges bit-identical inside every tile; tile edges within {worst * 1000:.3f} mm in ECEF at levels 0 to 2; "
			f"the largest step between neighbouring levels at an edge, {largest:.2f} m, is under the {mesh.SKIRT_DEPTH_M:g} m skirts")
	return worst, largest


def level_steps(documents, meshes):
	"""The largest vertical step at an edge where a refined node can meet an unrefined neighbour, per content file.

	A level-0 chunk can border a level-1 chunk at any chunk edge, and a level-1 chunk a level-2 tile at a tile edge. The
	coarser edge's vertices are every fourth of the finer's (one lattice), so the step is the finer edge's height above
	or below the coarser edge's straight segments; the skirts must reach deeper than that.
	"""
	steps = {}
	side = {level: mesh.LEVELS[level]["cells"] + 1 for level in mesh.LEVELS}
	last = mesh.CHUNKS_PER_SIDE - 1

	def grid(key, level):
		return enu_of(meshes[key])[: side[level] * side[level]].reshape(side[level], side[level], 3)

	def step(fine, coarse):
		k = np.arange(len(fine))
		return float(np.abs(fine[:, 2] - np.interp(k, k[::4], coarse[:, 2])).max())

	for tile in sorted(documents):
		for r in range(mesh.CHUNKS_PER_SIDE):
			for c in range(mesh.CHUNKS_PER_SIDE):
				l0, l1 = (tile, f"c{r}_{c}_l0.glb"), (tile, f"c{r}_{c}_l1.glb")
				if l0 in meshes and l1 in meshes:
					fine, coarse = grid(l0, 0), grid(l1, 1)
					steps[l0] = max(step(EDGES[e](fine), EDGES[e](coarse)) for e in EDGES)
		if (tile, "l2.glb") not in meshes:
			continue
		l2 = grid((tile, "l2.glb"), 2)
		for e, along in (("south", [(0, c) for c in range(4)]), ("north", [(last, c) for c in range(4)]),
				("west", [(r, 0) for r in range(4)]), ("east", [(r, last) for r in range(4)])):
			keys = [(tile, f"c{r}_{c}_l1.glb") for r, c in along]
			if any(key not in meshes for key in keys):
				continue
			parts = [EDGES[e](grid(key, 1)) for key in keys]
			fine = np.concatenate([parts[0]] + [part[1:] for part in parts[1:]])
			steps[(tile, "l2.glb")] = max(steps.get((tile, "l2.glb"), 0.0), step(fine, EDGES[e](l2)))
	return steps


def wall_tops(tile, meshes, transform):
	"""World [north, east] start and end of every wall's top edge in a tile's level-0 chunks, with the chunk file."""
	starts, ends, files = [], [], []
	first = L0_SIDE * L0_SIDE + 4 * L0_SIDE
	for (t, uri), arrays in sorted(meshes.items()):
		if t != tile or not uri.endswith("_l0.glb") or len(arrays["POSITION"]) <= first:
			continue
		quads = arrays["POSITION"][first:].reshape(-1, 8, 3)
		top = frames.ecef_from_content(quads[:, 2:4].reshape(-1, 3), transform)
		lat, lon, _ = geodesy.from_ecef(top[:, 0], top[:, 1], top[:, 2])
		north, east = geodesy.to_local(lat, lon)
		ne = np.stack([north, east], axis=1).reshape(-1, 2, 2)
		starts.append(ne[:, 0])
		ends.append(ne[:, 1])
		files.extend([uri] * len(ne))
	if not starts:
		return np.zeros((0, 2)), np.zeros((0, 2)), []
	return np.concatenate(starts), np.concatenate(ends), files


def check_floating(documents, meshes, sources, index, results):
	"""Landing barges: none in any tile's drawn list, and no barge outline covered by fixed walls."""
	barges = build.barge_lnams(sources)
	records = {t["id"]: t for t in index.get("tiles", [])}
	for tile, record in sorted(records.items()):
		for lnam in sorted(set(record.get("structures", {}).get("drawn", [])) & barges):
			results.fail("floating", f"landing barge {lnam} is listed as drawn", tile, "index.json")
	outlines = {}
	for i, j in sources.active:
		document = build.read_json(sources.tile_dir(i, j) / "shoreline_constructions.geojson")
		for feature in document["features"]:
			lnam = feature["properties"].get("id")
			if lnam in barges and lnam not in outlines:
				outlines[lnam] = [np.asarray(line, dtype=np.float64)[:, :2] for line in build._lines(feature.get("geometry")) if len(line) >= 2]
	walls = {tile: wall_tops(tile, meshes, documents[tile]["root"]["transform"]) for tile in sorted(documents)}
	coverage = {}
	for lnam, lines in sorted(outlines.items()):
		samples = []
		for line in lines:
			north, east = geodesy.to_local(line[:, 1], line[:, 0])
			ne = np.stack([north, east], axis=1)
			for a, b in zip(ne[:-1], ne[1:]):
				count = max(2, int(np.ceil(np.linalg.norm(b - a) / BARGE_SAMPLE_M)) + 1)
				t = np.linspace(0.0, 1.0, count)[:, None]
				samples.append(a + t * (b - a))
		if not samples:
			continue
		samples = np.concatenate(samples)
		low, high = samples.min(axis=0) - 1.0, samples.max(axis=0) + 1.0
		covered = np.zeros(len(samples), dtype=bool)
		by_tile = {}
		for tile, (starts, ends, files) in walls.items():
			near = ((np.minimum(starts, ends) <= high) & (np.maximum(starts, ends) >= low)).all(axis=1)
			if not near.any():
				continue
			a, b = starts[near], ends[near]
			d = b - a
			length2 = np.maximum((d * d).sum(axis=1), 1e-12)
			t = np.clip(((samples[:, None, :] - a[None]) * d[None]).sum(axis=2) / length2[None], 0.0, 1.0)
			distance = np.linalg.norm(samples[:, None, :] - (a[None] + t[..., None] * d[None]), axis=2)
			hit = (distance <= BARGE_DISTANCE_M).any(axis=1)
			if hit.any():
				by_tile[tile] = (int(hit.sum()), sorted({files[k] for k in np.flatnonzero(near)[np.unique(np.argmin(distance, axis=1)[hit])]}))
			covered |= hit
		share = float(covered.mean())
		coverage[lnam] = share
		if share > BARGE_COVERAGE_LIMIT:
			tile = max(by_tile, key=lambda k: by_tile[k][0])
			results.fail("floating", f"landing barge {lnam} is drawn as a fixed wall along {share:.0%} of its outline", tile,
				", ".join(f"tiles/{tile}/{name}" for name in by_tile[tile][1]))
	if not results.failed("floating"):
		results.ok("floating", f"none of the {len(barges)} landing barges is drawn; the most of any outline along a fixed wall "
			f"is {max(coverage.values(), default=0.0):.0%}")
	return coverage


def read_slice_grid(slice_dir):
	header = build.read_json(Path(slice_dir) / "unity" / "depth.json")
	values = np.fromfile(Path(slice_dir) / "unity" / "depth.f32", dtype="<f4").astype(np.float64)
	values = values.reshape(header["height"], header["width"])
	if header.get("nodata") is not None:
		values[values == header["nodata"]] = np.nan
	if header.get("crs", "EPSG:4326") != "EPSG:4326":
		raise ValueError(f"{slice_dir}/unity/depth.json: expected EPSG:4326, not {header.get('crs')}")
	transform = Affine(header["cell_size"][0], 0.0, header["origin"][0], 0.0, header["cell_size"][1], header["origin"][1])
	return values, transform, header


def check_slice(documents, meshes, sources, slice_dir, import_manifest, results):
	"""Terrain heights against the slice's imported terrain, the grid World/Import read (unity/depth.f32)."""
	slice_dir = Path(slice_dir)
	grid_file = slice_dir / "unity" / "depth.f32"
	if not grid_file.is_file():
		results.fail("slice", f"no slice terrain at {grid_file}: run ferrydata build --slice lower-east-river, then export-unity")
		return None
	try:
		imported = build.read_json(import_manifest)["source_sha256"]["unity/depth.f32"]
	except (OSError, ValueError, KeyError) as error:
		results.fail("slice", f"no import manifest with the imported grid's hash at {import_manifest}: {error}")
		return None
	digest = manifest.sha256_file(grid_file)
	if digest != imported:
		results.fail("slice", f"{grid_file} ({digest[:12]}) is not the grid the slice's terrain was imported from ({imported[:12]}, "
			f"{import_manifest}): rebuild the slice outputs")
		return None
	values, transform, header = read_slice_grid(slice_dir)
	# The slice deepens its berths by the operational-ais floor (ferrydata.operational_depth) and the harbor tiles don't
	# yet, so compare with the slice's terrain before the floor: its recorded lowering, on the same grid, added back.
	floor_cells = 0
	lowered_file = slice_dir / "depth_operational_lowered_m.tif"
	if lowered_file.is_file():
		with rasterio.open(lowered_file) as ds:
			lowered = ds.read(1).astype(np.float64)
		if lowered.shape != values.shape:
			results.fail("slice", f"{lowered_file} is {lowered.shape[1]} x {lowered.shape[0]} cells, not the slice grid's {values.shape[1]} x {values.shape[0]}")
			return None
		lowered = np.where(np.isfinite(lowered), lowered, 0.0)
		floor_cells = int((lowered > 0.0).sum())
		values = values + lowered
	west, north_edge = transform.c, transform.f
	east_edge, south = west + transform.a * values.shape[1], north_edge + transform.e * values.shape[0]
	stats = {"tolerance_m": SLICE_TOLERANCE_M, "fraction_required": SLICE_FRACTION, "maximum_m": SLICE_MAXIMUM_M, "tiles": {},
		"operational_floor_cells": floor_cells}
	all_differences = []
	worst = []
	for tile, document in sorted(documents.items()):
		i, j = map(int, tile.split("_")[1:])
		transform_tile = document["root"]["transform"]
		differences, where = [], []
		for r in range(mesh.CHUNKS_PER_SIDE):
			for c in range(mesh.CHUNKS_PER_SIDE):
				uri = f"c{r}_{c}_l0.glb"
				if (tile, uri) not in meshes:
					continue
				gn = heights.L0_CELLS_PER_TILE * i + mesh.LEVELS[0]["cells"] * r + np.arange(L0_SIDE)
				ge = heights.L0_CELLS_PER_TILE * j + mesh.LEVELS[0]["cells"] * c + np.arange(L0_SIDE)
				north, east = np.meshgrid(heights.coordinate(gn), heights.coordinate(ge), indexing="ij")
				lat, lon = geodesy.from_local(north, east)
				inside = (lon > west) & (lon < east_edge) & (lat < north_edge) & (lat > south)
				if not inside.any():
					continue
				theirs = heights.bilinear(values, transform, lon[inside], lat[inside])
				ecef = frames.ecef_from_content(grid_of(meshes[(tile, uri)], L0_SIDE)[inside], transform_tile)
				_, _, h = geodesy.from_ecef(ecef[:, 0], ecef[:, 1], ecef[:, 2])
				mine = h - sources.geoid_at(north[inside], east[inside])
				ok = np.isfinite(theirs)
				differences.append((mine - theirs)[ok])
				where.append(np.stack([lat[inside][ok], lon[inside][ok], north[inside][ok], east[inside][ok]], axis=1))
		if not differences:
			continue
		d = np.concatenate(differences)
		if not len(d):
			continue
		w = np.concatenate(where)
		within = float(np.mean(np.abs(d) <= SLICE_TOLERANCE_M))
		record = {"vertices": int(len(d)), "within_tolerance": within, "median_abs_m": float(np.median(np.abs(d))),
			"max_abs_m": float(np.abs(d).max()), "beyond_tolerance": int((np.abs(d) > SLICE_TOLERANCE_M).sum())}
		stats["tiles"][tile] = record
		all_differences.append(d)
		for k in np.argsort(-np.abs(d))[:5]:
			if abs(d[k]) > SLICE_TOLERANCE_M:
				worst.append({"tile": tile, "lat": float(w[k, 0]), "lon": float(w[k, 1]), "north_m": float(w[k, 2]), "east_m": float(w[k, 3]),
					"difference_m": float(d[k])})
		if within < SLICE_FRACTION or record["max_abs_m"] > SLICE_MAXIMUM_M:
			results.fail("slice", f"{record['beyond_tolerance']} of {record['vertices']} vertices differ from the slice's imported terrain by "
				f"more than {SLICE_TOLERANCE_M} m ({within:.4%} within; at most {1 - SLICE_FRACTION:.1%} may), worst {record['max_abs_m']:.3f} m "
				f"(at most {SLICE_MAXIMUM_M} m)", tile, f"tiles/{tile}/c*_l0.glb")
	if not all_differences:
		results.fail("slice", "no harbor tile overlaps the slice's imported terrain")
		return stats
	d = np.concatenate(all_differences)
	stats.update({"vertices": int(len(d)), "within_tolerance": float(np.mean(np.abs(d) <= SLICE_TOLERANCE_M)),
		"median_abs_m": float(np.median(np.abs(d))), "p99_abs_m": float(np.percentile(np.abs(d), 99)), "max_abs_m": float(np.abs(d).max()),
		"worst": sorted(worst, key=lambda x: -abs(x["difference_m"]))[:10], "grid_sha256": digest})
	if not results.failed("slice"):
		results.ok("slice", f"{stats['vertices']} level-0 vertices over the slice: {stats['within_tolerance']:.4%} within "
			f"{SLICE_TOLERANCE_M} m of the imported terrain, median {stats['median_abs_m'] * 1000:.3f} mm, worst {stats['max_abs_m']:.3f} m"
			+ (f"; compared before the slice's operational-ais floor ({floor_cells} cells lowered)" if floor_cells else ""))
	return stats


def check_size(out, documents, results):
	sizes = {"tiles": {}, "levels": {}, "total_bytes": 0}
	for path in sorted(p for p in out.rglob("*") if p.is_file()):
		sizes["total_bytes"] += path.stat().st_size
	for tile in sorted(documents):
		record = {"L0": 0, "L1": 0, "L2": 0, "json": 0}
		for path in sorted((out / "tiles" / tile).iterdir()):
			level = {"l0": "L0", "l1": "L1", "l2": "L2"}.get(path.stem[-2:], "json")
			record[level] += path.stat().st_size
		record["total"] = sum(record.values())
		sizes["tiles"][tile] = record
		for level in ("L0", "L1", "L2"):
			sizes["levels"][level] = sizes["levels"].get(level, 0) + record[level]
		if record["total"] > TILE_BUDGET_BYTES:
			results.fail("size", f"{record['total'] / 1e6:.1f} MB, over the {TILE_BUDGET_BYTES / 1e6:.0f} MB budget", tile, f"tiles/{tile}/")
	if not results.failed("size"):
		largest = max(sizes["tiles"].items(), key=lambda kv: kv[1]["total"], default=("", {"total": 0}))
		results.ok("size", f"{sizes['total_bytes'] / 1e6:.1f} MB in all; the largest tile, {largest[0]}, {largest[1]['total'] / 1e6:.1f} MB "
			f"(budget {TILE_BUDGET_BYTES / 1e6:.0f} MB)")
	return sizes


def report_markdown(summary):
	lines = ["# Harbor 3D Tiles validation", "", f"Result: **{'PASS' if summary['passed'] else 'FAIL'}**", "", "| Check | Result | Tile | File | Detail |",
		"|---|---|---|---|---|"]
	for r in summary["results"]:
		lines.append(f"| {r['check']} | {'ok' if r['ok'] else 'FAIL'} | {r['tile']} | {r['file']} | {r['message']} |")
	sizes = summary.get("sizes") or {}
	if sizes:
		lines += ["", "## Sizes", "", f"Total {sizes['total_bytes'] / 1e6:.2f} MB. Per level: "
			+ ", ".join(f"{level} {value / 1e6:.2f} MB" for level, value in sorted(sizes["levels"].items())) + ".", "",
			"| Tile | L0 MB | L1 MB | L2 MB | JSON kB | Total MB |", "|---|---|---|---|---|---|"]
		for tile, record in sizes["tiles"].items():
			lines.append(f"| {tile} | {record['L0'] / 1e6:.2f} | {record['L1'] / 1e6:.3f} | {record['L2'] / 1e6:.3f} | {record['json'] / 1e3:.1f} | "
				f"{record['total'] / 1e6:.2f} |")
	slice_stats = summary.get("slice")
	if slice_stats and "vertices" in slice_stats:
		lines += ["", "## Agreement with the slice's imported terrain", "",
			f"Tolerance: at least {SLICE_FRACTION:.1%} of level-0 vertices within {SLICE_TOLERANCE_M} m, none beyond {SLICE_MAXIMUM_M} m.", "",
			"| Tile | Vertices | Within | Beyond | Median mm | Worst m |", "|---|---|---|---|---|---|"]
		for tile, record in slice_stats["tiles"].items():
			lines.append(f"| {tile} | {record['vertices']} | {record['within_tolerance']:.4%} | {record['beyond_tolerance']} | "
				f"{record['median_abs_m'] * 1000:.3f} | {record['max_abs_m']:.3f} |")
		if slice_stats.get("worst"):
			lines += ["", "Largest differences (harbor minus slice):", "", "| Tile | Latitude | Longitude | North m | East m | Difference m |",
				"|---|---|---|---|---|---|"]
			for w in slice_stats["worst"]:
				lines.append(f"| {w['tile']} | {w['lat']:.6f} | {w['lon']:.6f} | {w['north_m']:.1f} | {w['east_m']:.1f} | {w['difference_m']:.3f} |")
	coverage = {k: v for k, v in (summary.get("barge_coverage") or {}).items() if v > 0.05}
	if coverage:
		lines += ["", "## Landing barges beside fixed walls", "", f"Every landing barge is left out. These have fixed chart structures along part of "
			f"their outline (a barge drawn as a wall would cover all of it; the check fails above {BARGE_COVERAGE_LIMIT:.0%}).", "",
			"| Barge LNAM | Outline along fixed walls |", "|---|---|"]
		for lnam, share in sorted(coverage.items(), key=lambda kv: -kv[1]):
			lines.append(f"| {lnam} | {share:.0%} |")
	floating = summary.get("floating_drawn") or []
	if floating:
		lines += ["", "## Floating chart structures drawn fixed", "", "Landing barges are left out; these other floating structures are drawn "
			"as fixed walls for now.", "", "| LNAM | Layer | WATLEV | Tiles |", "|---|---|---|---|"]
		for f in floating:
			lines.append(f"| {f['lnam']} | {f['layer']} | {f['water_level_effect_code'] if f['water_level_effect_code'] is not None else ''} | "
				f"{', '.join(f['tiles'])} |")
	return "\n".join(lines) + "\n"


def floating_summary(index):
	merged = {}
	for record in index.get("tiles", []):
		for f in record.get("structures", {}).get("floating_drawn", []):
			entry = merged.setdefault(f["lnam"], {"lnam": f["lnam"], "layer": f["layer"], "water_level_effect_code": f["water_level_effect_code"], "tiles": []})
			entry["tiles"].append(record["id"])
	return [merged[k] for k in sorted(merged)]


def validate(out_dir=None, harbor_dir=None, slice_dir=None, import_manifest=None, report_dir=None, log=print):
	out = Path(out_dir) if out_dir else build.default_output()
	harbor_dir = Path(harbor_dir) if harbor_dir else paths.processed_dir("harbor")
	slice_dir = Path(slice_dir) if slice_dir else default_slice_dir()
	import_manifest = Path(import_manifest) if import_manifest else default_import_manifest()
	report_dir = Path(report_dir) if report_dir else default_report_dir()
	results = Results(log)
	summary = {"output": str(out), "results": results.items}
	try:
		index = build.read_json(out / "index.json")
		if index.get("schema") != build.SCHEMA:
			raise ValueError(f"schema {index.get('schema')!r}, not {build.SCHEMA}")
	except (OSError, ValueError) as error:
		results.fail("index", f"index.json is missing or unreadable: {error}", "", "index.json")
		index = None
	active, _ = build.active_tiles(harbor_dir)
	sources = heights.Sources(harbor_dir, active)
	if index is not None:
		check_index(out, index, harbor_dir, results)
	documents, root = check_structure(out, active, results)
	meshes = check_glbs(out, documents, results)
	check_bounds(documents, root, meshes, results)
	check_geometric_error(documents, root, results)
	summary["frame_worst_m"] = check_frames(documents, meshes, sources, results)
	summary["seam_worst_m"], summary["level_step_worst_m"] = check_cracks(documents, meshes, results)
	summary["barge_coverage"] = check_floating(documents, meshes, sources, index or {}, results)
	summary["slice"] = check_slice(documents, meshes, sources, slice_dir, import_manifest, results)
	summary["sizes"] = check_size(out, documents, results)
	summary["floating_drawn"] = floating_summary(index or {})
	for check in CHECKS:
		if not any(r["check"] == check for r in results.items):
			results.fail(check, "the check did not run")
	summary["passed"] = all(r["ok"] for r in results.items)
	report_dir.mkdir(parents=True, exist_ok=True)
	(report_dir / "validation.json").write_text(json.dumps(summary, indent="\t", allow_nan=False) + "\n", encoding="utf-8")
	(report_dir / "validation.md").write_text(report_markdown(summary), encoding="utf-8")
	failed = [r for r in results.items if not r["ok"]]
	log(f"[world3d] validation {'passed' if summary['passed'] else 'FAILED'}: {len(results.items) - len(failed)} ok, {len(failed)} failed; "
		f"report in {report_dir}")
	return summary
