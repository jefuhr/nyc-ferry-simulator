import json

import pytest
from shapely.geometry import LineString, Point

from ferrydata import geodesy, geojsonio


def test_point_feature_carries_lonlat_and_local():
	feature = geojsonio.make_feature(Point(-74.0, 40.71), {"name": "x", "bad": float("nan")})
	props = feature["properties"]
	assert props["lon"] == -74.0 and props["lat"] == 40.71
	north, east = geodesy.to_local(40.71, -74.0)
	assert props["north_m"] == pytest.approx(north, abs=1e-3)
	assert props["east_m"] == pytest.approx(east, abs=1e-3)
	assert props["bad"] is None
	assert feature["local_coordinates"] == [pytest.approx(north, abs=1e-3), pytest.approx(east, abs=1e-3)]


def test_line_feature_local_coordinates_mirror_geometry():
	feature = geojsonio.make_feature(LineString([(-74.01, 40.70), (-74.0, 40.71)]), {})
	assert len(feature["local_coordinates"]) == 2
	north0, east0 = geodesy.to_local(40.70, -74.01)
	assert feature["local_coordinates"][0] == [pytest.approx(north0, abs=1e-3), pytest.approx(east0, abs=1e-3)]
	assert "north_m" in feature["properties"]


def test_write_collection_is_valid_json(tmp_path):
	out = tmp_path / "a.geojson"
	geojsonio.write_collection(out, [geojsonio.make_feature(Point(-74.0, 40.7), {})], "a")
	data = json.loads(out.read_text())
	assert data["type"] == "FeatureCollection" and len(data["features"]) == 1
