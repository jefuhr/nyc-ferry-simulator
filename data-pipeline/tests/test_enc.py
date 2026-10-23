import geopandas as gpd
import numpy as np
import pytest
from shapely.geometry import Point, Polygon

from ferrydata import enc, slices

LER = slices.LOWER_EAST_RIVER


def _buoys():
	return gpd.GeoDataFrame(
		{
			"LNAM": ["B1", "B2"],
			"LNAM_REFS": [np.array(["L1"]), None],
			"OBJNAM": ["Channel Lighted Buoy 3", "Plain Buoy 5"],
			"COLOUR": [np.array(["4"]), np.array(["3"])],
			"CATLAM": [1, 2],
		},
		geometry=[Point(-74.0, 40.70), Point(-73.99, 40.71)],
		crs="EPSG:4326",
	)


def _lights():
	return gpd.GeoDataFrame(
		{
			"LNAM": ["L1", "L2"],
			"LNAM_REFS": [np.array(["B1"]), None],
			"LITCHR": [2, 1],
			"COLOUR": [np.array(["4"]), np.array(["1"])],
			"SIGGRP": ["(1)", None],
			"SIGPER": [4.0, np.nan],
			"VALNMR": [4.0, np.nan],
			"HEIGHT": [np.nan, 12.0],
		},
		geometry=[Point(-74.0, 40.70), Point(-73.98, 40.72)],
		crs="EPSG:4326",
	)


def test_lights_attach_to_the_aid_that_references_them():
	feats = enc.aid_records({"BOYLAT": _buoys(), "LIGHTS": _lights()})
	by_id = {f["id"]: f for f in feats}
	assert by_id["B1"]["lights"][0]["notation"] == "Fl G 4s"
	assert by_id["B1"]["feature_class"] == "BOYLAT"
	assert by_id["B2"]["lights"] == []


def test_unreferenced_light_becomes_its_own_aid_and_attached_light_is_not_duplicated():
	feats = enc.aid_records({"BOYLAT": _buoys(), "LIGHTS": _lights()})
	assert sorted(f["id"] for f in feats) == ["B1", "B2", "L2"]
	standalone = next(f for f in feats if f["id"] == "L2")
	assert standalone["feature_class"] == "LIGHTS"
	assert standalone["lights"][0]["notation"] == "F W"


def test_lateral_category_and_colour_are_decoded():
	feats = {f["id"]: f for f in enc.aid_records({"BOYLAT": _buoys()})}
	assert feats["B1"]["lateral_category"] == "port-hand"
	assert feats["B2"]["lateral_category"] == "starboard-hand"
	assert feats["B1"]["colours"] == ["G"]
	assert feats["B2"]["name"] == "Plain Buoy 5"


def test_datum_codes():
	assert enc.datum_name(16) == "MHW"
	assert enc.datum_name(12) == "MLLW"
	assert enc.datum_name(999).startswith("code ")


def test_bridge_records_carry_clearances_and_datum():
	gdf = gpd.GeoDataFrame(
		{"LNAM": ["X1", "X2"], "OBJNAM": ["Test Bridge", None], "VERCLR": [38.7, np.nan], "VERCCL": [np.nan, np.nan], "VERCOP": [np.nan, np.nan], "HORCLR": [411.4, np.nan], "CATBRG": [np.array(["1"]), np.array(["1"])]},
		geometry=[Polygon([(-74, 40.70), (-73.99, 40.70), (-73.99, 40.71)]), Polygon([(-74, 40.72), (-73.99, 40.72), (-73.99, 40.73)])],
		crs="EPSG:4326",
	)
	recs = enc.bridge_records(gdf, vertical_datum="MHW")
	assert recs[0]["name"] == "Test Bridge"
	assert recs[0]["vertical_clearance_m"] == 38.7 and recs[0]["horizontal_clearance_m"] == 411.4
	assert recs[0]["clearance_datum"] == "MHW" and recs[0]["has_clearance"] is True
	assert recs[1]["name"] is None and recs[1]["has_clearance"] is False


def test_dedupe_keeps_one_point_per_lnam_but_distinct_line_pieces():
	from shapely.geometry import LineString

	pts = gpd.GeoDataFrame({"LNAM": ["A", "A"]}, geometry=[Point(0, 0), Point(0, 0)], crs="EPSG:4326")
	assert len(enc.dedupe(pts)) == 1
	lines = gpd.GeoDataFrame({"LNAM": ["A", "A"]}, geometry=[LineString([(0, 0), (1, 1)]), LineString([(1, 1), (2, 2)])], crs="EPSG:4326")
	assert len(enc.dedupe(lines)) == 2


def test_depth_area_records_carry_range_and_datum():
	gdf = gpd.GeoDataFrame(
		{"LNAM": ["D1"], "DRVAL1": [3.5], "DRVAL2": [9.1], "OBJNAM": [None]},
		geometry=[Polygon([(-74, 40.70), (-73.99, 40.70), (-73.99, 40.71)])],
		crs="EPSG:4326",
	)
	rec = enc.depth_area_records(gdf)[0]
	assert rec["min_depth_m"] == 3.5 and rec["max_depth_m"] == 9.1
	assert "MLLW" in rec["depth_datum"] and rec["name"] is None
