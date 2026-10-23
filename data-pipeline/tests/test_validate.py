import pandas as pd
import pytest
from shapely.geometry import LineString, Polygon

from ferrydata import approach, validate


# ---- landing face and approach side ------------------------------------------------------------------------


def test_face_anchor_and_tangent_on_a_straight_quay():
	quay = LineString([(-100, 0), (100, 0)])  # (east, north) metres: runs east-west along north = 0
	landing = (10.0, 5.0)  # (east, north)
	anchor, tangent = approach.face_geometry(landing, [quay])
	assert anchor == pytest.approx((10.0, 0.0))
	assert abs(tangent[0]) == pytest.approx(1.0) and tangent[1] == pytest.approx(0.0)


def test_face_uses_polygon_boundary_and_picks_the_nearest_feature():
	pier = Polygon([(0, 0), (4, 0), (4, 40), (0, 40)])
	far = LineString([(500, 500), (600, 500)])
	anchor, _ = approach.face_geometry((2.0, 20.0), [far, pier])
	assert anchor[0] in (pytest.approx(0.0), pytest.approx(4.0))


def test_only_water_side_wins():
	# Land to the south (north < 0), water to the north.
	is_water = lambda north, east: north > 0
	outward, how = approach.choose_outward((0.0, 0.0), (1.0, 0.0), is_water, route=None)
	assert outward == pytest.approx((1.0, 0.0)) and how == "only-water-side"


def test_two_water_sides_pick_the_side_of_the_route():
	is_water = lambda north, east: True
	route = LineString([(0.0, 80.0), (0.0, 200.0)])  # east=0, runs north of the anchor
	outward, how = approach.choose_outward((0.0, 0.0), (1.0, 0.0), is_water, route=route)
	assert outward == pytest.approx((1.0, 0.0)) and how == "route-side"


def test_two_water_sides_without_route_is_ambiguous_not_guessed():
	outward, how = approach.choose_outward((0.0, 0.0), (1.0, 0.0), lambda n, e: True, route=None)
	assert outward is None and how == "ambiguous"


def test_no_water_probe_falls_back_to_the_route_side_and_says_so():
	route = LineString([(0.0, 80.0), (0.0, 200.0)])
	outward, how = approach.choose_outward((0.0, 0.0), (1.0, 0.0), lambda n, e: False, route=route)
	assert outward == pytest.approx((1.0, 0.0)) and how == "route-side-dry"


def test_no_water_probe_and_no_route_is_ambiguous_dry():
	outward, how = approach.choose_outward((0.0, 0.0), (1.0, 0.0), lambda n, e: False, route=None)
	assert outward is None and how == "ambiguous-dry"


# ---- series coverage ---------------------------------------------------------------------------------------


def _times(*stamps):
	return pd.to_datetime(list(stamps), utc=True)


def test_coverage_gap_includes_the_window_edges():
	start, end = pd.Timestamp("2026-06-14T00:00Z"), pd.Timestamp("2026-06-15T00:00Z")
	good = _times("2026-06-14T00:00Z", "2026-06-14T00:30Z", "2026-06-14T23:54Z")
	assert validate.coverage_gap_hours(good, start, end) == pytest.approx(23.4, abs=0.01)  # 00:30 to 23:54
	# a series that stops early leaves a long gap at the end
	short = _times("2026-06-14T00:00Z", "2026-06-14T10:00Z")
	assert validate.coverage_gap_hours(short, start, end) == pytest.approx(14.0)


def test_dense_series_has_small_gap():
	start, end = pd.Timestamp("2026-06-14T00:00Z"), pd.Timestamp("2026-06-14T06:00Z")
	times = pd.date_range(start, end - pd.Timedelta(minutes=6), freq="6min", tz="UTC")
	assert validate.coverage_gap_hours(times, start, end) == pytest.approx(0.1)


# ---- simple checks over loaded data ------------------------------------------------------------------------


def _bridge(name, vertical, horizontal):
	return {"properties": {"name": name, "vertical_clearance_m": vertical, "horizontal_clearance_m": horizontal, "clearance_datum": "MHW"}}


def test_bridge_check_needs_each_named_bridge_with_both_clearances():
	fc = {"features": [_bridge("Brooklyn Bridge", 38.7, 411.4), _bridge("Manhattan Bridge", 40.8, 365.7), _bridge("Williamsburg Bridge", 40.5, 468.1), _bridge(None, None, None)]}
	assert validate.check_bridges(fc).passed
	missing = {"features": fc["features"][:2]}
	result = validate.check_bridges(missing)
	assert not result.passed and "Williamsburg" in result.summary
	no_clear = {"features": [_bridge("Brooklyn Bridge", 38.7, None), _bridge("Manhattan Bridge", 40.8, 365.7), _bridge("Williamsburg Bridge", 40.5, 468.1)]}
	assert not validate.check_bridges(no_clear).passed


def test_aids_check_requires_parsed_lights():
	ok = {"features": [{"properties": {"id": "A", "lights": [{"parsed": True, "notation": "Fl G 4s", "colours": ["G"], "period_s": 4.0}]}}]}
	assert validate.check_aids(ok).passed
	assert not validate.check_aids({"features": []}).passed
	unparsed = {"features": [{"properties": {"id": "A", "lights": [{"parsed": False}]}}]}
	assert not validate.check_aids(unparsed).passed


def test_geoid_check_range():
	good = {"points": {"centre": {"navd88_zero_ellipsoidal_height_m": -31.8}, "sw": {"navd88_zero_ellipsoidal_height_m": -31.9}}}
	assert validate.check_geoid(good).passed
	assert not validate.check_geoid({"points": {"centre": {"navd88_zero_ellipsoidal_height_m": -28.0}}}).passed
	assert not validate.check_geoid({"points": {"centre": {"navd88_zero_ellipsoidal_height_m": -36.0}}}).passed
	assert not validate.check_geoid({"points": {}}).passed


def test_csharp_reference_vectors_are_parsed_from_the_test_file():
	vectors = validate.csharp_reference_vectors()
	assert len(vectors) >= 4
	assert (40.5800, -73.8200, -13307.3042, 16510.0303, 35.2544) in vectors


def test_geodesy_check_passes_against_the_real_vectors():
	assert validate.check_geodesy().passed


def test_depth_requirement_uses_k_class_max_draft_from_the_vessel_file():
	assert validate.k_class_max_draft_m() == pytest.approx(1.6)


def test_landing_face_check_requires_all_stops_within_50m_and_on_water_side():
	rows = [{"stop_id": "1", "stop_name": "A", "distance_m": 50., "water_side": True, "source": "enc-feature", "confidence": "medium"}]
	assert validate.shoreline_result(rows).passed
	assert not validate.shoreline_result([]).passed
	for changes in ({"distance_m": 50.01}, {"water_side": False}, {"distance_m": None}):
		assert not validate.shoreline_result([{**rows[0], **changes}]).passed


def test_depth_verdict_keeps_coverage_and_2_point_1m_threshold():
	assert validate.depth_verdict({"min_depth_m": 2.1, "valid_fraction": .5}, 2.1)
	assert not validate.depth_verdict({"min_depth_m": 2.099, "valid_fraction": 1.}, 2.1)
	assert not validate.depth_verdict({"min_depth_m": 10., "valid_fraction": .49}, 2.1)
	assert not validate.depth_verdict({"min_depth_m": None, "valid_fraction": 1.}, 2.1)


def test_charted_range_is_taken_at_the_box_centre():
	box = {"corners": [(0, 0), (0, 30), (30, 30), (30, 0)]}  # (north, east); centre (15, 15)
	shoal = {"props": {"min_depth_m": 0.0, "max_depth_m": 2.0}, "geometry": Polygon([(0, 0), (30, 0), (30, 5), (0, 5)])}  # does not contain the centre
	deep = {"props": {"min_depth_m": 4.5, "max_depth_m": 9.1}, "geometry": Polygon([(0, 0), (40, 0), (40, 40), (0, 40)])}
	assert validate.charted_range_at_centre(box, [shoal, deep]) == "4.5-9.1"
	assert validate.charted_range_at_centre(box, [shoal]) is None


def test_shoreline_validation_recomputes_water_side_and_rejects_missing_face(tmp_path, monkeypatch):
	from types import SimpleNamespace
	from shapely.geometry import Point, box
	from ferrydata import geojsonio, landings, paths
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', tmp_path)
	out = tmp_path / 'fixture'
	out.mkdir()
	stop = geojsonio.make_feature(landings.to_wgs84(Point(0, 0)), {'stop_id': 'a', 'stop_name': 'fixture'})
	geojsonio.write_collection(out / 'landings.geojson', [stop], 'landings')
	face = geojsonio.make_feature(landings.to_wgs84(LineString([(-10, 30), (10, 30)])), {})
	props = {'stop_id': 'a', 'source': 'derived-from-gtfs', 'confidence': 'low', 'face_geometry': face['geometry'],
		'outward_normal_ne': [1, 0], 'bulkhead_anchor_en_m': [0, 0], 'water_side': False}
	barge = geojsonio.make_feature(landings.to_wgs84(box(-10, 20, 10, 30)), props)
	geojsonio.write_collection(out / 'landing_faces.geojson', [barge], 'faces')
	monkeypatch.setattr(landings, 'chart_masks', lambda _: (box(-100, 0, 100, 100), box(-100, -100, 100, 0)))
	slice_ = SimpleNamespace(name='fixture')
	assert validate.check_shoreline(slice_).passed  # stale stored water_side is not trusted
	barge['properties']['outward_normal_ne'] = [-1, 0]
	barge['properties']['water_side'] = True
	geojsonio.write_collection(out / 'landing_faces.geojson', [barge], 'faces')
	assert not validate.check_shoreline(slice_).passed
	geojsonio.write_collection(out / 'landing_faces.geojson', [], 'faces')
	assert not validate.check_shoreline(slice_).passed


def test_approach_samples_the_face_instead_of_bulkhead(tmp_path, monkeypatch):
	import numpy as np
	from rasterio.io import MemoryFile
	from rasterio.transform import from_origin
	from types import SimpleNamespace
	from shapely.geometry import Point, box
	from ferrydata import geojsonio, landings, paths, geodesy
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', tmp_path)
	out = tmp_path / 'fixture'; out.mkdir()
	stop = geojsonio.make_feature(landings.to_wgs84(Point(0, 0)), {'stop_id': 'a', 'stop_name': 'fixture'})
	geojsonio.write_collection(out / 'landings.geojson', [stop], 'landings')
	face = geojsonio.make_feature(landings.to_wgs84(LineString([(-14, 30), (14, 30)])), {})
	barge = geojsonio.make_feature(landings.to_wgs84(box(-14, 20, 14, 30)), {'stop_id': 'a', 'source': 'derived-from-gtfs', 'confidence': 'low', 'side_chosen_by': 'only-water-side', 'face_geometry': face['geometry'], 'outward_normal_ne': [1, 0]})
	geojsonio.write_collection(out / 'landing_faces.geojson', [barge], 'faces')
	lat, lon = geodesy.from_local(100, -100)
	with MemoryFile() as mem:
		with mem.open(driver='GTiff', height=100, width=100, count=1, dtype='float32', crs='EPSG:4326', transform=from_origin(float(lon), float(lat), .000025, .000025), nodata=-99999.) as ds:
			ds.write(np.full((100, 100), -4., dtype='float32'), 1)
			got = validate.compute_approaches(SimpleNamespace(name='fixture'), ds, -.8)
	assert got[0].anchor_ne == pytest.approx((30, 0), abs=.02)
	assert got[0].boxes[0]['stats']['min_depth_m'] == pytest.approx(3.2)
	assert validate.depth_verdict(got[0].boxes[0]['stats'], 2.1)
