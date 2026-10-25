"""NYC Ferry static GTFS: landings, route shapes and a schedule summary. Names and ids come only from the feed."""

import io
import json
import zipfile

import pandas as pd
from shapely.geometry import LineString, Point

from ferrydata import fetcher, geojsonio, manifest as manifest_module, paths
from ferrydata.slices import clip_geometry

SOURCE = "nycferry-gtfs"
URL = "http://nycferry.connexionz.net/rtt/public/resource/gtfs.zip"
DEVELOPER_PAGE = "https://www.ferry.nyc/developer-tools/"

TERMS = (
	"NYC Ferry Developer Terms (quoted from https://www.ferry.nyc/developer-tools/, retrieved with the feed): NYC Ferry offers static "
	"GTFS and various tools ('Developer Tools') for site developers, application API partners and others who may be interested in "
	"integrating NYC Ferry data or features into their sites, applications, products or services. Subject to the Terms and Conditions "
	"of the website (https://www.ferry.nyc/legal/), NYC Ferry grants non-exclusive, non-transferable, non-assignable, revocable rights "
	"to use the provided Developer Tools to test, design, and support your products and services. By downloading the Developer Tools, "
	"you acknowledge that NYC Ferry, without limitation, retains all rights, interest and title in the Developer Tools."
)

_TABLES = ("stops", "routes", "trips", "stop_times", "shapes")


def read_feed(path):
	"""Read the GTFS zip into DataFrames. Ids are kept as strings."""
	feed = {}
	with zipfile.ZipFile(path) as archive:
		for table in _TABLES:
			with archive.open(f"{table}.txt") as handle:
				feed[table] = pd.read_csv(io.TextIOWrapper(handle, encoding="utf-8-sig"), dtype=str, keep_default_na=False)
	feed["stops"]["stop_lat"] = feed["stops"]["stop_lat"].astype(float)
	feed["stops"]["stop_lon"] = feed["stops"]["stop_lon"].astype(float)
	feed["stop_times"]["stop_sequence"] = feed["stop_times"]["stop_sequence"].astype(int)
	feed["shapes"]["shape_pt_lat"] = feed["shapes"]["shape_pt_lat"].astype(float)
	feed["shapes"]["shape_pt_lon"] = feed["shapes"]["shape_pt_lon"].astype(float)
	feed["shapes"]["shape_pt_sequence"] = feed["shapes"]["shape_pt_sequence"].astype(int)
	return feed


def ferry_stop_ids(feed):
	"""Stops actually served by ferry routes (GTFS route_type 4), excluding bus shuttles."""
	routes = set(feed["routes"].loc[feed["routes"].route_type == "4", "route_id"])
	trips = set(feed["trips"].loc[feed["trips"].route_id.isin(routes), "trip_id"])
	return set(feed["stop_times"].loc[feed["stop_times"].trip_id.isin(trips), "stop_id"])


def _routes_by_stop(feed):
	joined = feed["stop_times"][["trip_id", "stop_id"]].merge(feed["trips"][["trip_id", "route_id"]], on="trip_id")
	return {stop: sorted(set(group.route_id)) for stop, group in joined.groupby("stop_id")}


def landings(feed, slice_):
	routes_by_stop = _routes_by_stop(feed)
	rows = []
	contains = getattr(slice_, "owns", slice_.contains)
	for stop in feed["stops"].itertuples():
		if not contains(stop.stop_lon, stop.stop_lat):
			continue
		rows.append(
			{
				"stop_id": stop.stop_id,
				"stop_code": stop.stop_code,
				"stop_name": stop.stop_name,
				"lon": stop.stop_lon,
				"lat": stop.stop_lat,
				"wheelchair_boarding": stop.wheelchair_boarding,
				"route_ids": routes_by_stop.get(stop.stop_id, []),
			}
		)
	return sorted(rows, key=lambda row: row["stop_id"])


def route_shapes(feed, slice_):
	"""Shapes used by at least one trip, clipped to the slice; one dict per shape with a non-empty clip."""
	trips = feed["trips"]
	routes = feed["routes"].set_index("route_id")
	out = []
	for shape_id, points in feed["shapes"].groupby("shape_id"):
		used = trips[trips.shape_id == shape_id]
		if used.empty:
			continue
		ordered = points.sort_values("shape_pt_sequence")
		line = LineString(list(zip(ordered.shape_pt_lon, ordered.shape_pt_lat)))
		clipped = clip_geometry(line, slice_)
		if clipped is None:
			continue
		route_ids = sorted(set(used.route_id))
		out.append(
			{
				"shape_id": shape_id,
				"route_ids": route_ids,
				"route_names": [routes.loc[r, "route_long_name"] for r in route_ids if r in routes.index],
				"direction_ids": sorted(set(used.direction_id)),
				"headsigns": sorted(set(used.trip_headsign)),
				"geometry": clipped,
				"full_length_points": len(ordered),
			}
		)
	return sorted(out, key=lambda row: row["shape_id"])


def schedule_summary(feed, slice_):
	stops = feed["stops"].set_index("stop_id")
	in_slice = {sid: slice_.contains(row.stop_lon, row.stop_lat) for sid, row in stops.iterrows()}
	times = feed["stop_times"].merge(feed["trips"][["trip_id", "route_id", "direction_id"]], on="trip_id")
	routes = []
	for route in feed["routes"].itertuples():
		route_times = times[times.route_id == route.route_id]
		directions = {}
		for direction, group in route_times.groupby("direction_id"):
			# The direction's stop order is the most common full stop sequence among its trips.
			sequences = group.sort_values(["trip_id", "stop_sequence"]).groupby("trip_id").stop_id.apply(tuple)
			common = sequences.value_counts().idxmax()
			directions[direction] = {
				"trips": int(len(sequences)),
				"stops": [
					{"stop_id": sid, "stop_name": stops.loc[sid, "stop_name"], "in_slice": bool(in_slice[sid])}
					for sid in common
				],
			}
		stop_ids = sorted(set(route_times.stop_id))
		in_slice_ids = [sid for sid in stop_ids if in_slice[sid]]
		routes.append(
			{
				"route_id": route.route_id,
				"short_name": route.route_short_name,
				"long_name": route.route_long_name,
				"color": route.route_color,
				"trips": int(route_times.trip_id.nunique()),
				"serves_slice": bool(in_slice_ids),
				"stop_ids": stop_ids,
				"stop_ids_in_slice": in_slice_ids,
				"directions": directions,
			}
		)
	return {"slice": slice_.name, "source": "NYC Ferry static GTFS", "routes": routes}


# ---- fetch / build ------------------------------------------------------------------------------------------


def _manifest():
	m = manifest_module.Manifest.load(SOURCE)
	m.describe(
		description="NYC Ferry static GTFS feed (routes, trips, stops, stop_times, shapes)",
		homepage=DEVELOPER_PAGE,
		license="NYC Ferry Developer Terms: revocable, non-exclusive, non-transferable licence to use the feed to test, design and support products and services; NYC Ferry retains all rights, interest and title. Not an open licence: check with NYC Ferry before any public release.",
		terms_url=DEVELOPER_PAGE,
		terms_text=TERMS,
		notes=[
			"The feed URL is the one listed on https://www.ferry.nyc/developer-tools/ (Routes, Trips & Stops).",
			"The feed is republished by NYC Ferry roughly weekly; the sha256 pins the version this slice was built from. Use fetch --force to refresh.",
		],
	)
	return m


def fetch(slice_, force=False, log=print):
	m = _manifest()
	status = fetcher.fetch_file(m, "gtfs.zip", URL, paths.raw_dir(SOURCE) / "gtfs.zip", force=force)
	log(f"  gtfs.zip: {status}")


def build(slice_, log=print):
	m = _manifest()
	path = paths.raw_dir(SOURCE) / "gtfs.zip"
	if not m.verify("gtfs.zip", path):
		raise SystemExit(f"{path} is missing or does not match the manifest; run fetch first")
	feed = read_feed(path)
	out = paths.processed_dir(slice_.name)

	features = [
		geojsonio.make_feature(Point(row["lon"], row["lat"]), {k: v for k, v in row.items() if k not in ("lon", "lat")})
		for row in landings(feed, slice_)
	]
	geojsonio.write_collection(out / "landings.geojson", features, "landings", {"source": "NYC Ferry GTFS stops.txt"})
	log(f"  landings.geojson: {len(features)} landings")

	features = []
	for shape in route_shapes(feed, slice_):
		props = {k: v for k, v in shape.items() if k != "geometry"}
		features.append(geojsonio.make_feature(shape["geometry"], props))
	geojsonio.write_collection(out / "routes.geojson", features, "routes", {"source": "NYC Ferry GTFS shapes.txt clipped to the slice"})
	log(f"  routes.geojson: {len(features)} shapes")

	summary = schedule_summary(feed, slice_)
	(out / "schedule_summary.json").write_text(json.dumps(summary, indent="\t", ensure_ascii=False) + "\n", encoding="utf-8")
	log(f"  schedule_summary.json: {sum(r['serves_slice'] for r in summary['routes'])} routes serve the slice")
