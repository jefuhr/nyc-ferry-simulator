"""The harbor 3D Tiles writer and its validator on a small synthetic harbor (tests/world3d_fixtures.py).

Failure modes guarded (each has a test that breaks a good build on purpose and expects the validator to name it):
- a wrong ENU/ECEF frame or axis order in a tile's transform;
- heights in the wrong datum (NAVD88 written as ellipsoidal);
- cracks: neighbouring tiles or chunks whose shared edge vertices differ, or heights taken from the wrong tile;
- a bounding volume that misses content;
- a non-deterministic file (two builds from scratch must be byte-identical);
- a structure drawn that floats: a landing barge, which the greybox draws on the tide;
- an index that disagrees with the files on disk;
- a damaged or inconsistent GLB, a tile missing from the root, a child whose geometric error exceeds its parent's,
  and terrain that disagrees with the slice's imported terrain.
"""

import hashlib
import json
import shutil
from pathlib import Path

import numpy as np
import pytest

import world3d_fixtures as fixtures
from ferrydata import geodesy
from ferrydata.world3d import build, frames, glb, heights, mesh, validate


@pytest.fixture(scope="module")
def harbor(tmp_path_factory):
	return fixtures.make_harbor(tmp_path_factory.mktemp("harbor"))


@pytest.fixture(scope="module")
def built(harbor, tmp_path_factory):
	out = tmp_path_factory.mktemp("built") / "3dtiles"
	build.build(harbor, out, log=lambda *a: None)
	return out


@pytest.fixture(scope="module")
def slice_files(harbor, tmp_path_factory):
	return fixtures.make_slice(tmp_path_factory.mktemp("slice"), harbor)


def run_validator(out, harbor, slice_files, tmp_path):
	slice_dir, manifest = slice_files
	return validate.validate(out, harbor, slice_dir=slice_dir, import_manifest=manifest, report_dir=tmp_path / "report",
		log=lambda *a: None)


def copy_of(built, tmp_path):
	target = tmp_path / "3dtiles"
	shutil.copytree(built, target)
	return target


def failures(result, check=None):
	return [r for r in result["results"] if not r["ok"] and (check is None or r["check"] == check)]


def reindex(out):
	"""Brings index.json's sizes and hashes back in line after a deliberate edit, so only the edited property fails."""
	index_path = out / "index.json"
	index = json.loads(index_path.read_text())
	for entry in index["files"]:
		data = (out / entry["path"]).read_bytes()
		entry["bytes"], entry["sha256"] = len(data), hashlib.sha256(data).hexdigest()
	index_path.write_text(json.dumps(index, indent="\t") + "\n")


def read_glb(path):
	return glb.decode(Path(path).read_bytes())[1]


def tile_transform(out, tile):
	return json.loads((out / "tiles" / tile / "tileset.json").read_text())["root"]["transform"]


def content_ecef(out, tile, name):
	return frames.ecef_from_content(read_glb(out / "tiles" / tile / name)["POSITION"], tile_transform(out, tile))


# Layout, determinism and the validator on a good build.

def test_layout(built):
	root = json.loads((built / "tileset.json").read_text())
	assert root["asset"]["version"] == "1.1"
	uris = [child["content"]["uri"] for child in root["root"]["children"]]
	assert uris == [f"tiles/tile_{i}_{j}/tileset.json" for i, j in sorted(fixtures.ACTIVE)]
	for i, j in fixtures.ACTIVE:
		names = sorted(p.name for p in (built / "tiles" / f"tile_{i}_{j}").iterdir())
		expected = sorted(["tileset.json", "l2.glb"] + [f"c{r}_{c}_l{level}.glb" for r in range(4) for c in range(4) for level in (0, 1)])
		assert names == expected
	index = json.loads((built / "index.json").read_text())
	on_disk = sorted(str(p.relative_to(built)) for p in built.rglob("*") if p.is_file() and p.name != "index.json")
	assert [f["path"] for f in index["files"]] == on_disk
	for entry in index["files"]:
		data = (built / entry["path"]).read_bytes()
		assert entry["bytes"] == len(data) and entry["sha256"] == hashlib.sha256(data).hexdigest()


def test_two_builds_from_scratch_are_byte_identical(harbor, built, tmp_path):
	again = tmp_path / "again"
	build.build(harbor, again, log=lambda *a: None)
	first = {str(p.relative_to(built)): p.read_bytes() for p in built.rglob("*") if p.is_file()}
	second = {str(p.relative_to(again)): p.read_bytes() for p in again.rglob("*") if p.is_file()}
	assert first.keys() == second.keys()
	assert [name for name in first if first[name] != second[name]] == []


def test_command_line_builds_validates_and_fails_loudly(harbor, slice_files, tmp_path, capsys):
	from ferrydata.world3d.__main__ import main
	slice_dir, manifest = slice_files
	out, report = tmp_path / "cli", tmp_path / "cli-report"
	assert main(["build", "--harbor", str(harbor), "--out", str(out), "--metrics", str(tmp_path / "metrics.json")]) == 0
	assert json.loads((tmp_path / "metrics.json").read_text())["files"] == len([p for p in out.rglob("*") if p.is_file()])
	common = ["--harbor", str(harbor), "--out", str(out), "--slice", str(slice_dir), "--import-manifest", str(manifest), "--report", str(report)]
	assert main(["validate"] + common) == 0
	(out / "tiles" / "tile_0_0" / "l2.glb").write_bytes(b"broken")
	assert main(["validate"] + common) == 1
	assert "FAIL [index] tile_0_0 tiles/tile_0_0/l2.glb" in capsys.readouterr().out


def test_build_refuses_to_replace_a_folder_it_did_not_write(harbor, tmp_path):
	out = tmp_path / "not-tiles"
	out.mkdir()
	(out / "precious.txt").write_text("keep me")
	with pytest.raises(ValueError, match="refusing to replace"):
		build.build(harbor, out, log=lambda *a: None)
	assert (out / "precious.txt").read_text() == "keep me"


def test_rebuild_replaces_stale_files(harbor, tmp_path):
	out = tmp_path / "3dtiles"
	build.build(harbor, out, log=lambda *a: None)
	(out / "tiles" / "tile_0_0" / "stale.glb").write_bytes(b"old")
	build.build(harbor, out, log=lambda *a: None)
	assert not (out / "tiles" / "tile_0_0" / "stale.glb").exists()


def test_validator_passes_a_good_build(built, harbor, slice_files, tmp_path):
	result = run_validator(built, harbor, slice_files, tmp_path)
	assert failures(result) == []
	assert result["passed"]
	checks = {r["check"] for r in result["results"]}
	assert checks >= {"index", "structure", "glb", "bounds", "geometric-error", "frame", "cracks", "floating", "slice", "size"}
	report = (tmp_path / "report" / "validation.md").read_text()
	assert "tile_0_0" in report and "L0" in report and "L2" in report
	sizes = json.loads((tmp_path / "report" / "validation.json").read_text())["sizes"]
	listed = sum(f["bytes"] for f in json.loads((built / "index.json").read_text())["files"])
	assert sizes["total_bytes"] == listed + (built / "index.json").stat().st_size


# Geometry.

def test_terrain_faces_up_and_vertex_normals_agree(built):
	tile = "tile_0_0"
	arrays = read_glb(built / "tiles" / tile / "c1_2_l0.glb")
	enu = frames.enu_from_gltf(arrays["POSITION"].astype(np.float64))
	normals = frames.enu_from_gltf(arrays["NORMAL"].astype(np.float64))
	triangles = arrays["indices"].reshape(-1, 3)[: 84 * 84 * 2]
	face = np.cross(enu[triangles[:, 1]] - enu[triangles[:, 0]], enu[triangles[:, 2]] - enu[triangles[:, 0]])
	assert (face[:, 2] > 0).all()
	unit = face / np.linalg.norm(face, axis=1, keepdims=True)
	assert (np.einsum("ij,ij->i", normals[triangles[:, 0]], unit) > 0.9).mean() > 0.99


def test_vertex_density_per_level(built):
	l0 = read_glb(built / "tiles" / "tile_1_0" / "c0_0_l0.glb")
	l1 = read_glb(built / "tiles" / "tile_1_0" / "c0_0_l1.glb")
	l2 = read_glb(built / "tiles" / "tile_1_0" / "l2.glb")
	for arrays, side, spacing in ((l0, 85, 1000 / 84), (l1, 22, 1000 / 21), (l2, 22, 4000 / 21)):
		grid = frames.enu_from_gltf(arrays["POSITION"][: side * side].astype(np.float64)).reshape(side, side, 3)
		assert np.linalg.norm(grid[3, 4, :2] - grid[3, 3, :2]) == pytest.approx(spacing, rel=2e-3)
		assert np.linalg.norm(grid[4, 3, :2] - grid[3, 3, :2]) == pytest.approx(spacing, rel=2e-3)


def test_heights_are_ellipsoidal_and_come_from_the_owner(built, harbor):
	# The L0 vertices of tile (0, 0) along its east edge belong to tile (0, 1) (a tile owns its west edge), whose raster
	# reads 2 cm higher and whose geoid corners differ: the vertex heights must be (0, 1)'s.
	sources = heights.Sources(harbor, fixtures.ACTIVE)
	ecef = content_ecef(built, "tile_0_0", "c1_3_l0.glb")[: 85 * 85].reshape(85, 85, 3)
	lat, lon, h = geodesy.from_ecef(ecef[..., 0], ecef[..., 1], ecef[..., 2])
	north = heights.coordinate(np.arange(84, 169))[:, None] + 0 * heights.coordinate(np.arange(85))[None, :]
	east = heights.coordinate(np.arange(252, 337))[None, :] + 0 * north
	own = sources.raster(0, 0).sample(lat, lon) + sources.geoid(0, 0)(north, east)
	theirs = sources.raster(0, 1).sample(lat, lon) + sources.geoid(0, 1)(north, east)
	assert np.abs(h[:, :-1] - own[:, :-1]).max() < 2e-3
	assert np.abs(h[:, -1] - theirs[:, -1]).max() < 2e-3
	assert np.abs(theirs[:, -1] - own[:, -1]).min() > 0.015
	# NAVD88 is never written as an ellipsoidal height: the seabed sits about 32 m below the ellipsoid.
	navd88 = sources.raster(0, 0).sample(lat, lon)
	assert np.abs((h - navd88)[:, :-1] - sources.geoid(0, 0)(north, east)[:, :-1]).max() < 2e-3
	assert ((h - navd88) < -31.0).all()


def edge(arrays, side, which):
	grid = arrays["POSITION"][: side * side].reshape(side, side, 3)
	return {"south": grid[0], "north": grid[-1], "west": grid[:, 0], "east": grid[:, -1]}[which]


def test_chunk_edges_inside_a_tile_are_bit_identical(built):
	folder = built / "tiles" / "tile_0_1"
	for level, side in ((0, 85), (1, 22)):
		for r in range(4):
			for c in range(3):
				a = edge(read_glb(folder / f"c{r}_{c}_l{level}.glb"), side, "east")
				b = edge(read_glb(folder / f"c{r}_{c + 1}_l{level}.glb"), side, "west")
				assert a.tobytes() == b.tobytes()
		for r in range(3):
			a = edge(read_glb(folder / f"c{r}_1_l{level}.glb"), side, "north")
			b = edge(read_glb(folder / f"c{r + 1}_1_l{level}.glb"), side, "south")
			assert a.tobytes() == b.tobytes()


def test_tile_edges_meet_in_ecef_at_every_level(built):
	pairs = [("tile_0_0", "c2_3_l0.glb", "tile_0_1", "c2_0_l0.glb", 85), ("tile_0_0", "c2_3_l1.glb", "tile_0_1", "c2_0_l1.glb", 22),
		("tile_0_0", "l2.glb", "tile_0_1", "l2.glb", 22)]
	for left_tile, left, right_tile, right, side in pairs:
		a = content_ecef(built, left_tile, left)[: side * side].reshape(side, side, 3)[:, -1]
		b = content_ecef(built, right_tile, right)[: side * side].reshape(side, side, 3)[:, 0]
		assert np.abs(a - b).max() < 1e-3
	a = content_ecef(built, "tile_0_0", "c3_1_l0.glb")[: 85 * 85].reshape(85, 85, 3)[-1]
	b = content_ecef(built, "tile_1_0", "c0_1_l0.glb")[: 85 * 85].reshape(85, 85, 3)[0]
	assert np.abs(a - b).max() < 1e-3


def test_skirts_hang_below_every_edge_and_face_outward(built):
	arrays = read_glb(built / "tiles" / "tile_0_0" / "c0_0_l1.glb")
	enu = frames.enu_from_gltf(arrays["POSITION"].astype(np.float64))
	grid = enu[: 22 * 22]
	skirts = enu[22 * 22: 22 * 22 + 4 * 22]
	assert len(skirts) == 4 * 22
	assert (skirts[:, 2] < grid[:, 2].min()).sum() > 0
	# Each skirt vertex is straight below an edge vertex, by the skirt depth.
	edge_points = np.concatenate([grid.reshape(22, 22, 3)[0], grid.reshape(22, 22, 3)[:, -1], grid.reshape(22, 22, 3)[-1][::-1],
		grid.reshape(22, 22, 3)[::-1, 0]])
	assert np.allclose(np.linalg.norm(edge_points - skirts, axis=1), mesh.SKIRT_DEPTH_M, atol=1e-3)
	triangles = arrays["indices"].reshape(-1, 3)
	skirt_triangles = triangles[(triangles >= 22 * 22).any(axis=1) & (triangles < 22 * 22 + 4 * 22).all(axis=1)]
	centre = grid.mean(axis=0)
	face = np.cross(enu[skirt_triangles[:, 1]] - enu[skirt_triangles[:, 0]], enu[skirt_triangles[:, 2]] - enu[skirt_triangles[:, 0]])
	outward = enu[skirt_triangles].mean(axis=1) - centre
	assert (np.einsum("ij,ij->i", face[:, :2], outward[:, :2]) > 0).all()


def wall_points(built, tile):
	"""Every wall vertex of a tile's L0 chunks as (lat, lon, ellipsoidal height), with the chunk file it is in."""
	points = []
	for path in sorted((built / "tiles" / tile).glob("c*_l0.glb")):
		arrays = read_glb(path)
		start = 85 * 85 + 4 * 85
		ecef = frames.ecef_from_content(arrays["POSITION"][start:], tile_transform(built, tile))
		lat, lon, h = geodesy.from_ecef(ecef[:, 0], ecef[:, 1], ecef[:, 2])
		points.append((path.name, lat, lon, h, arrays["COLOR_0"][start:]))
	return points


def test_walls_are_extruded_like_the_slice_and_barges_are_left_out(built, harbor):
	sources = heights.Sources(harbor, fixtures.ACTIVE)
	everything = []
	for tile in ("tile_0_0", "tile_0_1", "tile_1_0"):
		for name, lat, lon, h, colour in wall_points(built, tile):
			if len(lat):
				north, east = geodesy.to_local(lat, lon)
				# float32 positions put an end on a tile edge a fraction of a millimetre off it: round before asking its owner.
				navd88 = (h - sources.geoid_at(np.round(north, 3), np.round(east, 3))).reshape(-1, 8)
				# Bottom start and end, then top start and end, on both faces: NAVD88 -60 m and +1 m, to float32 rounding.
				assert np.abs(navd88[:, [0, 1, 4, 5]] + 60.0).max() < 2e-3, (tile, name)
				assert np.abs(navd88[:, [2, 3, 6, 7]] - 1.0).max() < 2e-3, (tile, name)
				assert (colour[:, :3] == mesh.STRUCTURE_COLOUR[:3]).all()
				everything.append(np.stack([north, east], axis=1))
	points = np.concatenate(everything)
	barge = np.array([(2000, 1000), (2010.668, 1027.432)])
	in_barge = (points[:, 0] > barge[0, 0] - 0.5) & (points[:, 0] < barge[1, 0] + 0.5) & (points[:, 1] > barge[0, 1] - 0.5) & (points[:, 1] < barge[1, 1] + 0.5)
	assert not in_barge.any()
	index = json.loads((built / "index.json").read_text())
	tile = {t["id"]: t for t in index["tiles"]}["tile_0_0"]
	assert fixtures.BARGE in tile["structures"]["left_out_barges"]
	assert fixtures.BARGE not in tile["structures"]["drawn"]
	assert fixtures.DOLPHIN in tile["structures"]["left_out_points"]
	floating = {t["id"]: t for t in index["tiles"]}["tile_1_0"]["structures"]["floating_drawn"]
	assert [f["lnam"] for f in floating] == [fixtures.PONTOON]


def wall_length(built, tile, lnam_box):
	"""Horizontal length of top wall edges inside a world-frame box, from the L0 GLBs (each quad counted once)."""
	total = 0.0
	n0, n1, e0, e1 = lnam_box
	for name, lat, lon, h, _ in wall_points(built, tile):
		if not len(lat):
			continue
		north, east = geodesy.to_local(lat, lon)
		quads = np.stack([north, east], axis=1).reshape(-1, 8, 2)[:, :4]  # front face: bottom a, bottom b, top a, top b
		a, b = quads[:, 2], quads[:, 3]
		inside = (a[:, 0] >= n0) & (a[:, 0] <= n1) & (a[:, 1] >= e0) & (a[:, 1] <= e1)
		total += float(np.linalg.norm(b[inside] - a[inside], axis=1).sum())
	return total


def test_a_structure_across_a_seam_is_drawn_once(built):
	# The pier's outline is 2 x 200 m + 2 x 30 m; tiles (0, 0) and (0, 1) each draw the part in their own square.
	pier = (995.0, 1035.0, 3895.0, 4105.0)
	assert wall_length(built, "tile_0_0", pier) + wall_length(built, "tile_0_1", pier) == pytest.approx(460.0, abs=0.01)
	assert wall_length(built, "tile_0_0", pier) == pytest.approx(230.0, abs=0.01)
	# A bulkhead lying exactly on the seam belongs to the tile that owns that edge, (0, 1): drawn once.
	seam = (150.0, 650.0, 3999.0, 4001.0)
	assert wall_length(built, "tile_0_0", seam) == 0.0
	assert wall_length(built, "tile_0_1", seam) == pytest.approx(400.0, abs=0.01)


def test_bounding_volumes_contain_their_content(built):
	for i, j in fixtures.ACTIVE:
		tile = f"tile_{i}_{j}"
		document = json.loads((built / "tiles" / tile / "tileset.json").read_text())
		for node in [document["root"]] + document["root"]["children"] + [c["children"][0] for c in document["root"]["children"]]:
			box = np.array(node["boundingVolume"]["box"])
			centre, axes = box[:3], box[3:].reshape(3, 3)
			enu = frames.enu_from_gltf(read_glb(built / "tiles" / tile / node["content"]["uri"])["POSITION"].astype(np.float64))
			local = (enu - centre) @ axes.T / np.sum(axes * axes, axis=1)
			assert (np.abs(local) <= 1.0 + 1e-6).all(), (tile, node["content"]["uri"])


def test_geometric_error_drops_from_parent_to_child(built):
	root = json.loads((built / "tileset.json").read_text())
	child = root["root"]["children"][0]
	tile = json.loads((built / child["content"]["uri"]).read_text())
	l2 = tile["root"]
	l1 = l2["children"][0]
	l0 = l1["children"][0]
	assert root["root"]["geometricError"] > child["geometricError"] > l2["geometricError"] > l1["geometricError"] > l0["geometricError"] == 0
	assert l1["geometricError"] == pytest.approx(16 * 2000 * 2 * np.tan(np.radians(30)) / 1080, rel=1e-12)
	assert l2["geometricError"] == pytest.approx(4 * l1["geometricError"], rel=1e-12)


# The validator fails loudly on each broken property, naming the tile and the file.

def test_validator_catches_a_damaged_file(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	target = out / "tiles" / "tile_0_1" / "c2_2_l0.glb"
	data = bytearray(target.read_bytes())
	data[-10] ^= 0xFF
	target.write_bytes(bytes(data))
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "index")
	assert any("tiles/tile_0_1/c2_2_l0.glb" in f["file"] for f in found)


def test_validator_catches_files_missing_or_unlisted(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	(out / "tiles" / "tile_1_0" / "c0_0_l1.glb").unlink()
	(out / "tiles" / "tile_1_0" / "extra.glb").write_bytes(b"x")
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "index")
	files = {f["file"] for f in found}
	assert "tiles/tile_1_0/c0_0_l1.glb" in files and "tiles/tile_1_0/extra.glb" in files


def rewrite_glb(path, change):
	_, mesh = glb.decode(path.read_bytes())
	arrays = {k: np.array(v) for k, v in mesh.items()}
	change(arrays)
	path.write_bytes(glb.encode(arrays["POSITION"], arrays["NORMAL"], arrays["COLOR_0"], arrays["indices"]))


def test_validator_catches_a_crack_between_tiles(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)

	def lift_edge(arrays):
		grid = arrays["POSITION"][: 85 * 85].reshape(85, 85, 3)
		grid[40, -1, 1] += 0.05  # 5 cm up (glTF y) on the east edge
		arrays["POSITION"][: 85 * 85] = grid.reshape(-1, 3)

	rewrite_glb(out / "tiles" / "tile_0_0" / "c2_3_l0.glb", lift_edge)
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "cracks")
	assert any(f["tile"] == "tile_0_0" and "tile_0_1" in f["message"] and "c2_3_l0.glb" in f["file"] for f in found)


def test_validator_catches_a_crack_between_chunks(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)

	def nudge(arrays):
		arrays["POSITION"][3, 1] += 0.001  # a south-edge vertex, by a millimetre: within a tile edges are bit-identical

	rewrite_glb(out / "tiles" / "tile_1_0" / "c2_1_l1.glb", nudge)
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "cracks")
	assert any(f["tile"] == "tile_1_0" and "c2_1_l1.glb" in f["file"] for f in found)


def test_validator_catches_skirts_too_shallow_for_a_level_step(built, harbor, slice_files, tmp_path, monkeypatch):
	# The fixture's steps between levels are centimetres; skirts of a tenth of a millimetre would leave them open.
	monkeypatch.setattr(validate.mesh, "SKIRT_DEPTH_M", 1e-4)
	found = failures(run_validator(built, harbor, slice_files, tmp_path), "cracks")
	assert any("skirts" in f["message"] and f["file"].endswith("_l0.glb") for f in found)
	assert any("skirts" in f["message"] and f["file"].endswith("l2.glb") for f in found)


def test_validator_catches_a_bounding_volume_that_misses_content(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	path = out / "tiles" / "tile_0_1" / "tileset.json"
	document = json.loads(path.read_text())
	node = document["root"]["children"][5]["children"][0]
	node["boundingVolume"]["box"][11] *= 0.5  # halve the vertical half-axis
	path.write_text(json.dumps(document, indent="\t") + "\n")
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "bounds")
	assert any(f["tile"] == "tile_0_1" and node["content"]["uri"] in f["file"] for f in found)


def test_validator_catches_a_wrong_frame(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	path = out / "tiles" / "tile_1_0" / "tileset.json"
	document = json.loads(path.read_text())
	t = document["root"]["transform"]
	t[0:3], t[4:7] = t[4:7], t[0:3]  # east and north columns swapped
	path.write_text(json.dumps(document, indent="\t") + "\n")
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "frame")
	assert any(f["tile"] == "tile_1_0" for f in found)


def test_validator_catches_heights_in_the_wrong_datum(harbor, slice_files, tmp_path, monkeypatch):
	# A writer that forgot the geoid offset: NAVD88 heights written as if they were ellipsoidal.
	monkeypatch.setattr(heights, "ellipsoidal", lambda navd88, geoid: np.asarray(navd88, dtype=np.float64))
	out = tmp_path / "3dtiles"
	build.build(harbor, out, log=lambda *a: None)
	monkeypatch.undo()
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "frame")
	assert {f["tile"] for f in found} == {"tile_0_0", "tile_0_1", "tile_1_0"}


def test_validator_catches_a_floating_barge_drawn_fixed(harbor, slice_files, tmp_path, monkeypatch):
	monkeypatch.setattr(build, "barge_lnams", lambda sources: set())
	out = tmp_path / "3dtiles"
	build.build(harbor, out, log=lambda *a: None)
	monkeypatch.undo()
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "floating")
	assert any(fixtures.BARGE in f["message"] and f["tile"] == "tile_0_0" for f in found)


def test_validator_catches_a_bad_glb(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	target = out / "tiles" / "tile_0_0" / "c0_1_l1.glb"
	data = target.read_bytes()
	json_length = int.from_bytes(data[12:16], "little")
	nan = bytearray(data)
	nan[28 + json_length:28 + json_length + 4] = np.float32(np.nan).tobytes()
	target.write_bytes(bytes(nan))
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "glb")
	assert any(f["tile"] == "tile_0_0" and "c0_1_l1.glb" in f["file"] and "finite" in f["message"] for f in found)


def test_validator_catches_a_tile_missing_from_the_root(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	path = out / "tileset.json"
	document = json.loads(path.read_text())
	document["root"]["children"] = [c for c in document["root"]["children"] if "tile_1_0" not in c["content"]["uri"]]
	path.write_text(json.dumps(document, indent="\t") + "\n")
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "structure")
	assert any(f["tile"] == "tile_1_0" for f in found)


def test_validator_catches_geometric_error_that_grows(built, harbor, slice_files, tmp_path):
	out = copy_of(built, tmp_path)
	path = out / "tiles" / "tile_0_0" / "tileset.json"
	document = json.loads(path.read_text())
	document["root"]["children"][3]["children"][0]["geometricError"] = 500.0
	path.write_text(json.dumps(document, indent="\t") + "\n")
	reindex(out)
	found = failures(run_validator(out, harbor, slice_files, tmp_path), "geometric-error")
	assert any(f["tile"] == "tile_0_0" and "c0_3_l0.glb" in f["file"] for f in found)


def test_validator_catches_terrain_that_disagrees_with_the_slice(built, harbor, tmp_path):
	# Half a metre deeper over a 100 m patch of the slice's imported terrain.
	lat, lon = geodesy.from_local(1500.0, 1500.0)

	def bias(LAT, LON):
		return np.where((np.abs(LAT - float(lat)) < 0.0005) & (np.abs(LON - float(lon)) < 0.0006), -0.5, 0.0)

	slice_dir, manifest = fixtures.make_slice(tmp_path / "slice", harbor, bias=bias)
	result = validate.validate(built, harbor, slice_dir=slice_dir, import_manifest=manifest, report_dir=tmp_path / "report",
		log=lambda *a: None)
	found = failures(result, "slice")
	assert any(f["tile"] == "tile_0_0" for f in found)


def test_validator_refuses_a_slice_export_that_is_not_the_imported_one(built, harbor, slice_files, tmp_path):
	slice_dir, manifest = slice_files
	stale = tmp_path / "import-manifest.json"
	stale.write_text(json.dumps({"source_sha256": {"unity/depth.f32": "0" * 64}}))
	result = validate.validate(built, harbor, slice_dir=slice_dir, import_manifest=stale, report_dir=tmp_path / "report",
		log=lambda *a: None)
	assert any("import" in f["message"] for f in failures(result, "slice"))
