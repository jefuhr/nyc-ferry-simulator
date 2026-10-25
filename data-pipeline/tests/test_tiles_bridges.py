"""Bridge gate regressions: multi-span topology, seams, route types and unknowns."""

import pandas as pd
import pytest
from shapely.geometry import LineString, mapping

from ferrydata import enc, validate


def feature(identifier, points, clearance=None, **properties):
	return {"geometry": mapping(LineString(points)),
		"local_coordinates": [[north, east] for east, north in points],
		"properties": {"id": identifier, "name": "Test Bridge", "category_codes": [1],
			"vertical_clearance_m": clearance, **properties}}


def route(points, route_id="F"):
	return feature("shape", points, route_ids=[route_id])


def three_spans():
	return [feature("west", [(0, 0), (10, 0)]),
		feature("channel", [(10, 0), (20, 0)], 30.),
		feature("east", [(20, 0), (30, 0)])]


def analyze(bridges, routes):
	return validate.harbor_bridge_clearances({"tile_0_0": bridges}, {"tile_0_0": routes}, {"F"})


def test_channel_span_clearance_passes():
	check, audit = analyze(three_spans(), [route([(15, -5), (15, 5)])])
	assert check.passed
	assert audit["missing_clearance_features"] == 2
	assert audit["physical_bridges_with_missing_clearance"] == 1
	crossing = audit["physical_bridges"][0]["route_crossings"][0]
	assert crossing["charted_crossed_spans"] == [{"id": "channel", "vertical_clearance_m": 30.}]


def test_channel_and_side_span_crossing_passes():
	check, audit = analyze(three_spans(), [route([(5, -5), (5, 5), (15, 5), (15, -5)])])
	assert check.passed
	assert audit["physical_bridges"][0]["route_crossings"][0]["crossed_span_ids"] == ["channel", "west"]


def test_only_uncharted_side_span_fails_naming_bridge_and_route():
	check, _ = analyze(three_spans(), [route([(5, -5), (5, 5)])])
	assert not check.passed
	assert all(value in check.summary for value in ("Test Bridge", "west", "channel", "east", "F"))


def test_uncharted_bridge_crossed_by_ferry_fails():
	check, _ = analyze([feature("unknown", [(0, 0), (10, 0)])], [route([(5, -5), (5, 5)])])
	assert not check.passed


def test_uncrossed_uncharted_bridge_is_report_only():
	check, audit = analyze([feature("unknown", [(0, 0), (10, 0)])], [])
	assert check.passed
	group = audit["physical_bridges"][0]
	assert group["names"] == ["Test Bridge"]
	assert group["category_codes"] == [1]
	assert group["tiles"] == ["tile_0_0"]
	assert group["missing_clearance_spans"][0]["ferry_route_ids"] == []
	assert audit["report_only"]


def test_split_lnam_is_merged_before_grouping_across_tiles():
	check, audit = validate.harbor_bridge_clearances(
		{"tile_0_0": [feature("same", [(0, 0), (10, 0)])],
		 "tile_0_1": [feature("same", [(10, 0), (20, 0)]), feature("next", [(20, 0), (30, 0)], 30.)]},
		{"tile_0_1": [route([(25, -5), (25, 5)])]}, {"F"})
	assert check.passed
	assert audit["feature_count"] == 2
	assert len(audit["physical_bridges"]) == 1
	assert audit["physical_bridges"][0]["missing_clearance_spans"][0]["tiles"] == ["tile_0_0", "tile_0_1"]


@pytest.mark.parametrize("gap,groups", [(1.9, 1), (2., 1), (2.1, 2)])
def test_grouping_distance_is_two_local_metres(gap, groups):
	_, audit = analyze([feature("a", [(0, 0), (10, 0)]),
		feature("b", [(10 + gap, 0), (20, 0)])], [])
	assert len(audit["physical_bridges"]) == groups


def test_bus_shuttle_shape_does_not_gate():
	check, audit = analyze([feature("unknown", [(0, 0), (10, 0)])], [route([(5, -5), (5, 5)], "BUS")])
	assert check.passed
	assert audit["physical_bridges"][0]["route_crossings"] == []


def test_opening_bridge_verccl_counts_as_charted():
	bridge = enc.bridge_records(pd.DataFrame([{"LNAM": "opening", "VERCCL": 8.,
		"CATBRG": "2", "geometry": LineString([(0, 0), (10, 0)])}]), "MHW")[0]
	assert bridge["vertical_clearance_m"] == 8.
	check, _ = analyze([feature("opening", [(0, 0), (10, 0)], **{k: v for k, v in bridge.items()
		if k not in ("id", "geometry", "vertical_clearance_m")}, clearance=bridge["vertical_clearance_m"])],
		[route([(5, -5), (5, 5)])])
	assert check.passed


def test_one_routes_clearance_cannot_cover_another_routes_side_only_crossing():
	check, _ = validate.harbor_bridge_clearances({"tile_0_0": three_spans()},
		{"tile_0_0": [route([(15, -5), (15, 5)]), route([(5, -5), (5, 5)], "OTHER")]}, {"F", "OTHER"})
	assert not check.passed
	assert "OTHER" in check.summary


def test_grouping_is_transitive_and_reproducible():
	bridges = [feature("a", [(0, 0), (10, 0)]), feature("b", [(11, 0), (20, 0)]),
		feature("c", [(21, 0), (30, 0)])]
	_, audit = analyze(bridges, [])
	assert len(audit["physical_bridges"]) == 1
	assert analyze(list(reversed(bridges)), [])[1] == audit


@pytest.mark.parametrize("route_id,exit_code", [(None, 0), ("BUS", 0), ("F", 1)])
def test_harbor_report_audits_unknowns_indexes_hashes_and_gates_only_ferries(monkeypatch, tmp_path, route_id, exit_code):
	from ferrydata import manifest, paths, tiles
	monkeypatch.setattr(paths, "PROCESSED_ROOT", tmp_path)
	stop_ids = [str(i) for i in range(24)]
	entries = [{"id": tile.id, "i": tile.i, "j": tile.j, "bounds_local_m": list(tile.local_bounds),
		"grown_bounds_local_m": list(tile.grown_local_bounds), "grown_corners_latlon": {},
		"read_bbox_lonlat": [], "active": tile.id == "tile_0_0", "reasons": [],
		"stop_ids": stop_ids if tile.id == "tile_0_0" else []} for tile in tiles.grid()]
	index = {"tiles": entries, "stops": [{"stop_id": key, "tile": "tile_0_0"} for key in stop_ids],
		"excluded_stops": [], "enc_cells": [], "schema": "harbor-tiles-v1", "origin_latlon": [],
		"bounds_order": [], "tile_size_m": tiles.SIZE_M, "margin_m": tiles.MARGIN_M}
	out = tmp_path / "harbor"
	tiles._write(out / "index.json", index)
	folder = out / "tiles" / "tile_0_0"
	tiles._write(folder / "bridges.geojson", {"features": [feature("unknown", [(0, 0), (10, 0)])]})
	tiles._write(folder / "routes.geojson", {"features": [] if route_id is None else [route([(5, -5), (5, 5)], route_id)]})
	monkeypatch.setattr(tiles, "inventory", lambda: index)
	monkeypatch.setattr(tiles, "_feed", lambda: {"routes": pd.DataFrame([
		{"route_id": "F", "route_type": "4"}, {"route_id": "BUS", "route_type": "3"}])})
	monkeypatch.setattr(validate, "_harbor_tile_checks", lambda *args: [validate.CheckResult(
		"tile_0_0: bridge clearance audit (report only)", True, "1 unknown span")])
	logs = []
	assert validate.run_harbor(log=logs.append) == exit_code
	audit = tiles._json(out / "bridge_clearance_audit.json")
	assert audit["missing_clearance_features"] == 1
	assert audit["physical_bridges_with_missing_clearance"] == 1
	assert any(line.startswith("INFO tile_0_0: bridge clearance audit") for line in logs)
	report = (out / "validation.md").read_text()
	assert "INFO depth/chart and bridge clearance audit rows" in report
	assert "| tile_0_0: bridge clearance audit (report only) | INFO |" in report
	outputs = tiles._json(out / "index.json")["outputs"]
	assert {item["file"] for item in outputs} == {"validation.md", "depth_chart_audit.json", "bridge_clearance_audit.json"}
	for item in outputs:
		assert item["sha256"] == manifest.sha256_file(out / item["file"])
