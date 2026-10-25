"""Harbor grid failure modes: negative indices, boundary duplication, clipping and seams."""

import numpy as np
import pytest
from shapely.geometry import LineString, Point, box

from ferrydata import cli, geodesy, gtfs, slices, tiles


def point(north, east):
	lat, lon = geodesy.from_local(north, east)
	return Point(float(lon), float(lat))


def test_grid_ids_bounds_and_extent():
	grid = tiles.grid()
	assert len(grid) == 36
	assert grid[0].id == "tile_-2_-2"
	assert grid[-1].id == "tile_3_3"
	assert tiles.get_tile("tile_-2_3").local_bounds == (-8000, 12000, -4000, 16000)
	for invalid in ("tile_4_0", "tile_0_-3", "tile_01_0", "tile_x_0", "../tile_0_0"):
		with pytest.raises(ValueError):
			tiles.get_tile(invalid)


@pytest.mark.parametrize("tile", tiles.grid())
def test_grown_corners_round_trip(tile):
	n0, e0, n1, e1 = tile.local_bounds
	expected = {"sw": (n0 - 250, e0 - 250), "se": (n0 - 250, e1 + 250),
		"nw": (n1 + 250, e0 - 250), "ne": (n1 + 250, e1 + 250)}
	for label, (lat, lon) in tile.corners().items():
		assert geodesy.to_local(lat, lon) == pytest.approx(expected[label], abs=1e-5)


def test_ownership_is_half_open_and_does_not_use_margin():
	for north, east in ((-7999, -7999), (-4000, 0), (0, 0), (4000, 8000), (15999, 15999)):
		p = point(north, east)
		owners = [t.id for t in tiles.grid() if t.owns(p.x, p.y)]
		assert owners == [tiles.owner(north, east).id]
	assert tiles.owner(16000, 0) is None
	assert tiles.owner(0, -8000.01) is None
	assert tiles.owner(-0.01, -0.01).id == "tile_-1_-1"
	assert tiles.owner(np.nextafter(4000., -np.inf), 1).id == "tile_0_0"


def test_activity_requires_water_or_owned_landing():
	tile = tiles.get_tile("tile_0_0")
	assert tiles.activity(tile, [], []) == []
	assert tiles.activity(tile, [tile.core_polygon], []) == ["water"]
	p = point(500, 500)
	assert tiles.activity(tile, [], [p]) == ["landing"]
	assert tiles.activity(tile, [tile.core_polygon], [p]) == ["water", "landing"]
	assert tiles.activity(tile, [], [point(-100, 500)]) == []


def test_margin_clips_geometry_at_250_metres():
	tile = tiles.get_tile("tile_0_0")
	line = LineString([point(2000, -500), point(2000, 4500)])
	clipped = slices.clip_geometry(line, tile)
	coords = list(clipped.coords)
	north, east = geodesy.to_local(np.array([p[1] for p in coords]), np.array([p[0] for p in coords]))
	assert min(east) == pytest.approx(-250, abs=0.001)
	assert max(east) == pytest.approx(4250, abs=0.001)
	assert tile.contains(point(2000, -249).x, point(2000, -249).y)
	assert not tile.contains(point(2000, -251).x, point(2000, -251).y)


def test_overlap_rejects_depth_difference_and_nodata_mismatch():
	a = np.array([[1., 2.], [3., -99999.]])
	b = a.copy()
	assert tiles.overlap_result(a, b, -99999., -99999.)["passed"]
	b[0, 0] += 0.051
	assert not tiles.overlap_result(a, b, -99999., -99999.)["passed"]
	b = a.copy(); b[1, 1] = 4
	assert tiles.overlap_result(a, b, -99999., -99999.)["nodata_mismatches"] == 1
	assert not tiles.overlap_result(a, b, -99999., -99999.)["passed"]


def test_overlap_compares_only_common_footprint():
	a = np.array([[1., 10.]])
	b = np.array([[1., 20.]])
	assert tiles.overlap_result(a, b, None, None, np.array([[True, False]]))["passed"]
	assert not tiles.overlap_result(a, b, None, None)["passed"]


def test_gtfs_landings_use_ownership_instead_of_margin():
	import pandas as pd
	p = point(100, -100)
	feed = {"stops": pd.DataFrame([{"stop_id": "s", "stop_code": "", "stop_name": "Test",
		"stop_lat": p.y, "stop_lon": p.x, "wheelchair_boarding": "1"}]),
		"stop_times": pd.DataFrame([{"trip_id": "t", "stop_id": "s"}]),
		"trips": pd.DataFrame([{"trip_id": "t", "route_id": "r"}])}
	assert gtfs.landings(feed, tiles.get_tile("tile_0_0")) == []
	assert len(gtfs.landings(feed, tiles.get_tile("tile_0_-1"))) == 1


def test_ferry_stops_exclude_bus_shuttles():
	import pandas as pd
	feed = {"routes": pd.DataFrame([{"route_id": "f", "route_type": "4"}, {"route_id": "b", "route_type": "3"}]),
		"trips": pd.DataFrame([{"trip_id": "tf", "route_id": "f"}, {"trip_id": "tb", "route_id": "b"}]),
		"stop_times": pd.DataFrame([{"trip_id": "tf", "stop_id": "ferry"}, {"trip_id": "tb", "stop_id": "bus"}])}
	assert gtfs.ferry_stop_ids(feed) == {"ferry"}


@pytest.mark.parametrize("verb", ("fetch", "build", "validate"))
def test_cli_harbor_dispatches_without_slice(monkeypatch, verb):
	calls = []
	monkeypatch.setattr(tiles, verb, lambda **kwargs: calls.append(kwargs) or 0)
	assert cli.main([verb, "--harbor"]) == 0
	assert len(calls) == 1


def test_cli_rejects_ambiguous_scope_and_slice_tile_filter():
	for args in (["build", "--harbor", "--slice", "lower-east-river"],
		["build", "--slice", "lower-east-river", "--tiles", "tile_0_0"],
		["export-unity", "--harbor"]):
		with pytest.raises(SystemExit):
			cli.main(args)


def test_catalog_uses_usage_band_five_and_lat_lon_axis_order():
	from ferrydata import enc
	xml = '''<DS_Series xmlns="http://www.isotc211.org/2005/gmd"
		xmlns:gco="http://www.isotc211.org/2005/gco" xmlns:gml="http://www.opengis.net/gml/3.2">
		<MD_Metadata><identificationInfo><MD_DataIdentification><citation><CI_Citation>
		<title><gco:CharacterString>US5TEST1</gco:CharacterString></title></CI_Citation></citation>
		<extent><gml:Polygon><gml:exterior><gml:LinearRing>
		<gml:pos>40 -74</gml:pos><gml:pos>41 -74</gml:pos><gml:pos>41 -73</gml:pos>
		<gml:pos>40 -73</gml:pos><gml:pos>40 -74</gml:pos>
		</gml:LinearRing></gml:exterior></gml:Polygon></extent>
		</MD_DataIdentification></identificationInfo></MD_Metadata></DS_Series>'''
	assert enc.catalog_cells(xml, box(-73.9, 40.1, -73.8, 40.2)) == ("US5TEST1",)
	assert enc.catalog_cells(xml, box(-72, 40.1, -71, 40.2)) == ()
	assert enc.catalog_cells(xml.replace("US5TEST1", "US4TEST1"), box(-73.9, 40.1, -73.8, 40.2)) == ()


def test_sidecar_cannot_shadow_original_pins(monkeypatch, tmp_path):
	from ferrydata import manifest, paths
	monkeypatch.setattr(paths, "MANIFEST_DIR", tmp_path)
	base = manifest.Manifest.load("test"); base.record("a", "url", "123", 1); base.save()
	extra = manifest.Manifest.load("test-harbor"); extra.record("b", "url", "456", 2); extra.save()
	assert set(manifest.load_combined("test").files) == {"a", "b"}
	extra.record("a", "url", "789", 1); extra.save()
	with pytest.raises(ValueError, match="shadows"):
		manifest.load_combined("test")


def test_water_coverage_never_waives_nodata():
	import rasterio
	from rasterio.io import MemoryFile
	from rasterio.transform import from_origin
	from ferrydata import validate
	with MemoryFile() as memory:
		with memory.open(driver="GTiff", width=2, height=2, count=1, dtype="float32",
			transform=from_origin(0, 2, 1, 1), nodata=-99999.) as dataset:
			dataset.write(np.array([[1., -99999.], [2., 3.]], dtype="float32"), 1)
			assert validate.harbor_water_coverage(dataset, [box(0, 0, 2, 2)]) == {"charted_cells": 4, "nodata_cells": 1}
			assert validate.harbor_water_coverage(dataset, [box(0, 0, 1, 2)]) == {"charted_cells": 2, "nodata_cells": 0}
			assert validate.harbor_water_coverage(dataset, [box(0, 0, 2, 2)], box(0, 0, 1, 2)) == {"charted_cells": 2, "nodata_cells": 0}


def test_raster_overlap_rejects_unaligned_grids(tmp_path):
	import rasterio
	from rasterio.transform import from_origin
	from ferrydata import validate
	def write(name, x, values):
		p = tmp_path / name
		with rasterio.open(p, "w", driver="GTiff", width=3, height=2, count=1, dtype="float32",
			crs="EPSG:4269", transform=from_origin(x, 2, 1, 1), nodata=-99999.) as ds:
			ds.write(np.array(values, dtype="float32"), 1)
		return p
	a = write("a.tif", 0, [[0, 1, 2], [0, 1, 2]])
	b = write("b.tif", 1, [[1, 2, 3], [1, 2, 3]])
	assert validate.harbor_overlap(a, b, box(1, 0, 3, 2))["passed"]
	c = write("c.tif", 1.5, [[1, 2, 3], [1, 2, 3]])
	with pytest.raises(ValueError, match="do not align"):
		validate.harbor_overlap(a, c, box(1.5, 0, 3, 2))


def test_cudem_tile_windows_share_pixel_lattice_and_clip_square(monkeypatch, tmp_path):
	import rasterio
	from rasterio import features
	from rasterio.transform import from_origin
	from ferrydata import cudem, paths, validate
	monkeypatch.setattr(paths, "PROCESSED_ROOT", tmp_path / "processed")
	raw = tmp_path / "source.tif"
	with rasterio.open(raw, "w", driver="GTiff", width=200, height=200, count=1, dtype="float32",
		crs="EPSG:4269", transform=from_origin(-74.1, 40.8, .001, .001), nodata=cudem.NODATA) as ds:
		ds.write(np.arange(40000, dtype="float32").reshape(200, 200), 1)
	shifted = tmp_path / "shifted.tif"
	with rasterio.open(shifted, "w", driver="GTiff", width=200, height=200, count=1, dtype="float32",
		crs="EPSG:4269", transform=from_origin(-74.1 + 1e-8, 40.8 + 1e-8, .001, .001), nodata=cudem.NODATA) as ds:
		ds.write(np.arange(40000, dtype="float32").reshape(200, 200), 1)
	# Real quarter-degree CUDEM headers have slightly different rounded
	# origins/resolutions. Each output must use one global lattice anyway.
	monkeypatch.setattr(cudem, "_tile_files", lambda m, s: [raw if s.j == 0 else shifted])
	a, b = tiles.get_tile("tile_0_0"), tiles.get_tile("tile_0_1")
	for tile in (a, b):
		cudem.build(tile, log=lambda _: None)
		with rasterio.open(paths.processed_dir(tile.name) / "depth_navd88.tif") as ds:
			inside = features.geometry_mask([tile.polygon], out_shape=(ds.height, ds.width), transform=ds.transform, invert=True)
			assert (ds.read(1)[~inside] == cudem.NODATA).all()
			assert (ds.read(1)[inside] != cudem.NODATA).all()
	assert validate.harbor_overlap(paths.processed_dir(a.name) / "depth_navd88.tif",
		paths.processed_dir(b.name) / "depth_navd88.tif", a.polygon.intersection(b.polygon))["passed"]


def test_fetch_addition_does_not_replace_unrecorded_raw_file(monkeypatch, tmp_path):
	from ferrydata import fetcher, paths
	monkeypatch.setattr(paths, "MANIFEST_DIR", tmp_path / "manifests")
	monkeypatch.setenv("FERRYSIM_CACHE", str(tmp_path))
	p = paths.raw_dir("test") / "file"
	p.parent.mkdir(parents=True); p.write_bytes(b"preserve")
	with pytest.raises(fetcher.ChecksumError, match="unrecorded"):
		tiles._fetch_addition("test", "file", "https://example.test/file")
	assert p.read_bytes() == b"preserve"


def test_fetch_addition_cache_hit_never_touches_original_manifest(monkeypatch, tmp_path):
	from ferrydata import fetcher, manifest, paths
	monkeypatch.setattr(paths, "MANIFEST_DIR", tmp_path / "manifests")
	monkeypatch.setenv("FERRYSIM_CACHE", str(tmp_path))
	p = paths.raw_dir("test") / "file"
	p.parent.mkdir(parents=True); p.write_bytes(b"preserve")
	m = manifest.Manifest.load("test"); m.record("file", "https://example.test/file", manifest.sha256_file(p), p.stat().st_size); m.save()
	original = m.path.read_bytes()
	def no_download(*args, **kwargs):
		raise AssertionError("cache hit must not use network")
	monkeypatch.setattr(fetcher, "_download", no_download)
	assert tiles._fetch_addition("test", "file", "https://example.test/file", log=lambda _: None) == p
	assert m.path.read_bytes() == original
	assert not (paths.MANIFEST_DIR / "test-harbor.json").exists()


def test_fetch_addition_restores_missing_original_pin_without_changing_provenance(monkeypatch, tmp_path):
	import hashlib
	from ferrydata import fetcher, manifest, paths
	monkeypatch.setattr(paths, "MANIFEST_DIR", tmp_path / "manifests")
	monkeypatch.setenv("FERRYSIM_CACHE", str(tmp_path))
	m = manifest.Manifest.load("test")
	m.record("file", "https://example.test/file", hashlib.sha256(b"pinned").hexdigest(), 6); m.save()
	original = m.path.read_bytes()
	def download(url, part):
		part.write_bytes(b"pinned")
		return 6
	monkeypatch.setattr(fetcher, "_download", download)
	p = tiles._fetch_addition("test", "file", "https://example.test/file", log=lambda _: None)
	assert p.read_bytes() == b"pinned"
	assert m.path.read_bytes() == original
	assert manifest.load_combined("test").verify("file", p)


def test_missing_harbor_index_writes_failed_report(monkeypatch, tmp_path):
	from ferrydata import paths, validate
	monkeypatch.setattr(paths, "PROCESSED_ROOT", tmp_path)
	assert validate.run_harbor(log=lambda _: None) == 1
	text = (tmp_path / "harbor" / "validation.md").read_text()
	assert "FAIL" in text and "FileNotFoundError" in text


def test_malformed_harbor_index_writes_failed_report(monkeypatch, tmp_path):
	from ferrydata import paths, validate
	monkeypatch.setattr(paths, "PROCESSED_ROOT", tmp_path)
	(tmp_path / "harbor").mkdir()
	(tmp_path / "harbor" / "index.json").write_text("{broken")
	assert validate.run_harbor(log=lambda _: None) == 1
	assert "JSONDecodeError" in (tmp_path / "harbor" / "validation.md").read_text()


def test_chart_depth_audit_reports_outliers_without_changing_raster():
	from rasterio.io import MemoryFile
	from rasterio.transform import from_origin
	from shapely.geometry import mapping
	from ferrydata import validate
	array = np.array([[-46., -10., -2.], [-21., -22., -17.]], dtype="float32")
	areas = [{"geometry": mapping(box(0, 0, 3, 2)), "properties":
		{"feature_class": "DEPARE", "min_depth_m": 10., "max_depth_m": 15.}}]
	with MemoryFile() as mem:
		with mem.open(driver="GTiff", width=3, height=2, count=1, dtype="float32",
			transform=from_origin(0, 2, 1, 1), nodata=-99999.) as ds:
			ds.write(array, 1)
			audit = validate.harbor_chart_depth_audit(ds, areas, -1.)
			assert audit["too_deep"]["cells"] == 2
			assert audit["too_shallow"]["cells"] == 1
			assert audit["too_deep"]["worst"]["excess_m"] == 30.
			assert audit["too_deep"]["worst"]["merged_depth_mllw_m"] == 45.
			assert audit["too_deep"]["worst"]["lon"] == .5
			assert audit["too_deep"]["worst"]["lat"] == 1.5
			assert audit["too_shallow"]["worst"]["excess_m"] == 9.
			np.testing.assert_array_equal(ds.read(1), array)


def test_chart_depth_audit_ignores_nodata_and_unknown_bounds():
	from rasterio.io import MemoryFile
	from rasterio.transform import from_origin
	from shapely.geometry import mapping
	from ferrydata import validate
	areas = [{"geometry": mapping(box(0, 0, 2, 1)), "properties":
		{"feature_class": "DEPARE", "min_depth_m": None, "max_depth_m": None}}]
	with MemoryFile() as mem:
		with mem.open(driver="GTiff", width=2, height=1, count=1, dtype="float32",
			transform=from_origin(0, 1, 1, 1), nodata=-99999.) as ds:
			ds.write(np.array([[-99999., -60.]], dtype="float32"), 1)
			audit = validate.harbor_chart_depth_audit(ds, areas, -1.)
			assert audit["too_deep"] == {"cells": 0, "worst": None}
			assert audit["too_shallow"] == {"cells": 0, "worst": None}
			assert audit["compared_cells"] == 0


def test_subset_build_preserves_other_tile_index(monkeypatch, tmp_path):
	from copy import deepcopy
	from types import SimpleNamespace
	from ferrydata import paths
	monkeypatch.setattr(paths, "PROCESSED_ROOT", tmp_path)
	entries = [{"id": "tile_0_0", "active": True, "outputs": []},
		{"id": "tile_0_1", "active": True, "outputs": []}]
	index = {"tiles": entries, "enc_cells": []}
	old = deepcopy(index)
	old["tiles"][1]["outputs"] = [{"file": "tiles/tile_0_1/unchanged", "sha256": "old-pin", "size_bytes": 3}]
	old["tiles"][1]["metrics"] = {"elapsed_seconds": 1, "peak_rss_bytes": 123}
	tiles._write(tmp_path / "harbor" / "index.json", old)
	monkeypatch.setattr(tiles, "inventory", lambda: deepcopy(index))
	calls = []
	def run(args, **kwargs):
		import json
		assert args[:2] == ["lockf", "-k"]
		assert args[2].endswith("/FerrySim-cache/heavy.lock")
		identifier, _ = json.loads(args[-1]); calls.append(identifier)
		tiles._write(tmp_path / "harbor" / "tiles" / identifier / "build_metrics.json",
			{"elapsed_seconds": 1, "peak_rss_bytes": 456})
		return SimpleNamespace(stdout="built\n", stderr="", returncode=0)
	monkeypatch.setattr(tiles.subprocess, "run", run)
	assert tiles.build(["tile_0_0"], log=lambda _: None) == 0
	assert calls == ["tile_0_0"]
	assert tiles._json(tmp_path / "harbor" / "index.json")["tiles"][1] == old["tiles"][1]
