import zipfile

import pytest

from ferrydata import gtfs, slices

LER = slices.LOWER_EAST_RIVER

FILES = {
	"stops.txt": (
		"stop_id,stop_code,stop_name,stop_lat,stop_lon,wheelchair_boarding\n"
		'1,"100","Fixture Pier A",40.7033,-74.0069,1\n'
		'2,"101","Fixture Landing B",40.7036,-73.9892,1\n'
		'3,"102","Fixture Far Away",40.5800,-73.8200,1\n'
		'4,"103","Fixture North Dock",40.7430,-73.9710,0\n'
	),
	"routes.txt": (
		"route_id,agency_id,route_short_name,route_long_name,route_type,route_color,route_text_color\n"
		'"R1",1,"R1","Fixture River",4,00839C,FFFFFF\n'
		'"R2",1,"R2","Fixture Bay",4,FF6B00,FFFFFF\n'
	),
	"trips.txt": (
		"route_id,service_id,trip_id,trip_headsign,trip_short_name,direction_id,block_id,shape_id\n"
		'"R1",1,t1,"North Dock",1,0,1,s1\n'
		'"R1",1,t2,"Pier A",2,1,1,s1\n'
		'"R2",1,t3,"Far Away",3,0,2,s2\n'
	),
	"stop_times.txt": (
		"trip_id,arrival_time,departure_time,stop_id,stop_sequence,stop_headsign,pickup_type,drop_off_type,timepoint\n"
		"t1,06:00:00,06:00:00,1,1,,0,0,1\n"
		"t1,06:10:00,06:10:00,2,2,,0,0,1\n"
		"t1,06:20:00,06:20:00,4,3,,0,0,1\n"
		"t2,07:00:00,07:00:00,4,1,,0,0,1\n"
		"t2,07:10:00,07:10:00,2,2,,0,0,1\n"
		"t2,07:20:00,07:20:00,1,3,,0,0,1\n"
		"t3,08:00:00,08:00:00,3,1,,0,0,1\n"
		"t3,08:30:00,08:30:00,1,2,,0,0,1\n"
	),
	"shapes.txt": (
		"shape_id,shape_pt_lon,shape_pt_lat,shape_pt_sequence\n"
		"s1,-74.0069,40.7033,1\n"
		"s1,-73.9892,40.7036,2\n"
		"s1,-73.9710,40.7430,3\n"
		"s2,-73.8200,40.5800,1\n"
		"s2,-74.0069,40.7033,2\n"
		"s3,-73.9000,40.6000,1\n"
		"s3,-73.8900,40.6100,2\n"
	),
}


@pytest.fixture
def feed(tmp_path):
	path = tmp_path / "gtfs.zip"
	with zipfile.ZipFile(path, "w") as z:
		for name, text in FILES.items():
			z.writestr(name, text)
	return gtfs.read_feed(path)


def test_read_feed_parses_tables(feed):
	assert len(feed["stops"]) == 4
	assert all(isinstance(v, str) for v in feed["stops"].stop_id)  # ids stay strings, never numeric
	assert set(feed["routes"].route_id) == {"R1", "R2"}


def test_landings_are_clipped_to_slice_and_names_come_from_feed(feed):
	landings = gtfs.landings(feed, LER)
	assert [l["stop_id"] for l in landings] == ["1", "2", "4"]
	assert landings[0]["stop_name"] == "Fixture Pier A"
	assert all(LER.contains(l["lon"], l["lat"]) for l in landings)


def test_landings_list_routes_serving_each_stop(feed):
	by_id = {l["stop_id"]: l for l in gtfs.landings(feed, LER)}
	assert by_id["1"]["route_ids"] == ["R1", "R2"]
	assert by_id["2"]["route_ids"] == ["R1"]


def test_route_shapes_clip_to_slice(feed):
	shapes = gtfs.route_shapes(feed, LER)
	by_shape = {s["shape_id"]: s for s in shapes}
	assert "s3" not in by_shape  # not used by any trip, and outside the slice
	assert "s2" in by_shape  # only its last segment is inside the slice
	west, south, east, north = by_shape["s2"]["geometry"].bounds
	assert south >= LER.south - 1e-9 and east <= LER.east + 1e-9
	assert by_shape["s1"]["route_ids"] == ["R1"]


def test_schedule_summary_lists_stops_per_route_in_trip_order(feed):
	summary = gtfs.schedule_summary(feed, LER)
	r1 = next(r for r in summary["routes"] if r["route_id"] == "R1")
	assert [s["stop_id"] for s in r1["directions"]["0"]["stops"]] == ["1", "2", "4"]
	assert [s["stop_id"] for s in r1["directions"]["1"]["stops"]] == ["4", "2", "1"]
	assert r1["trips"] == 2
	assert r1["stop_ids_in_slice"] == ["1", "2", "4"]
	r2 = next(r for r in summary["routes"] if r["route_id"] == "R2")
	assert r2["stop_ids_in_slice"] == ["1"]
	assert next(s for s in r2["directions"]["0"]["stops"] if s["stop_id"] == "3")["in_slice"] is False


def test_routes_not_serving_the_slice_are_flagged(feed):
	summary = gtfs.schedule_summary(feed, LER)
	assert summary["slice"] == "lower-east-river"
	assert {r["route_id"] for r in summary["routes"] if r["serves_slice"]} == {"R1", "R2"}
