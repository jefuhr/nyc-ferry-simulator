"""MarineCadastre AIS: find passenger vessels of ferry size whose tracks repeatedly stop at GTFS landings.

Method (details and limits in data-pipeline/README.md):
1. keep AIS rows inside the harbor box from passenger vessels (AIS ship type 60-69) 25 to 31 m long
2. a "visit" to a landing is a run of reports within VISIT_RADIUS_M of a GTFS stop at SOG <= VISIT_MAX_SOG_KN,
   runs separated by more than VISIT_GAP_MIN minutes counting separately
3. a vessel joins the candidate roster with enough visits to enough different landings
"""

import json
from datetime import date, timedelta

import numpy as np
import pandas as pd
import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.csv as pacsv

from ferrydata import fetcher, geodesy, gtfs, manifest as manifest_module, paths
from ferrydata.slices import AIS_HARBOR_BBOX

SOURCE = "marinecadastre-ais"
BASE = "https://noaaocm.blob.core.windows.net/ais/csv2"
# Four Monday-to-Sunday weeks, one per season.
WEEKS = ("2024-10-07", "2025-01-13", "2025-04-14", "2025-07-14")

LENGTH_RANGE_M = (25, 31)
PASSENGER_TYPES = (60, 69)
VISIT_RADIUS_M = 75.0
VISIT_MAX_SOG_KN = 2.0
VISIT_GAP_MIN = 10.0
MIN_VISITS = 20
MIN_LANDINGS = 2
KNOT = 1852.0 / 3600.0

_COLUMNS = ["mmsi", "base_date_time", "longitude", "latitude", "sog", "cog", "heading", "vessel_name", "vessel_type", "length", "width"]


def week_days(start):
	first = date.fromisoformat(start)
	return [(first + timedelta(days=i)).isoformat() for i in range(7)]


def day_url(day):
	return f"{BASE}/csv{day[:4]}/ais-{day}.csv.zst"


def day_name(day):
	return f"ais-{day}.csv.zst"


def clean_ais(df):
	"""AIS marks 'not available' with sentinel values; turn them into NaN."""
	out = df.copy()
	out["sog"] = out["sog"].where(out["sog"] <= 102.2)
	out["cog"] = out["cog"].where(out["cog"] < 360.0)
	out["heading"] = out["heading"].astype(float).where(out["heading"] < 360)
	return out


def static_match(df):
	low, high = PASSENGER_TYPES
	short, long = LENGTH_RANGE_M
	return (df["vessel_type"].between(low, high)) & (df["length"].between(short, long))


def count_visits(points, landings):
	"""Visits per stop_id for one vessel's points (needs time_utc, north_m, east_m, sog in knots)."""
	slow = points[points["sog"].fillna(99.0) <= VISIT_MAX_SOG_KN].sort_values("time_utc")
	if slow.empty:
		return {}
	lat = np.array([l["lat"] for l in landings])
	lon = np.array([l["lon"] for l in landings])
	l_north, l_east = geodesy.to_local(lat, lon)
	d_north = slow["north_m"].to_numpy()[:, None] - l_north[None, :]
	d_east = slow["east_m"].to_numpy()[:, None] - l_east[None, :]
	near = np.hypot(d_north, d_east) <= VISIT_RADIUS_M
	visits = {}
	times = slow["time_utc"].reset_index(drop=True)
	for index, landing in enumerate(landings):
		at = times[near[:, index]]
		if at.empty:
			continue
		gaps = at.diff().dt.total_seconds().fillna(np.inf) / 60.0
		count = int((gaps > VISIT_GAP_MIN).sum())
		visits[landing["stop_id"]] = count
	return visits


def _mode(series):
	series = series.dropna()
	return series.mode().iloc[0] if len(series) else None


def build_roster(points, landings, min_visits=MIN_VISITS, min_landings=MIN_LANDINGS):
	rows = []
	for mmsi, group in points.groupby("mmsi"):
		visits = count_visits(group, landings)
		visits = {k: v for k, v in visits.items() if v > 0}
		total = sum(visits.values())
		if total < min_visits or len(visits) < min_landings:
			continue
		names = {l["stop_id"]: l["stop_name"] for l in landings}
		row = {
			"mmsi": int(mmsi),
			"name": _mode(group["vessel_name"]),
			"length_m": _mode(group["length"]),
			"beam_m": _mode(group["width"]),
			"vessel_type": _mode(group["vessel_type"]),
			"visits_total": total,
			"landings_visited": len(visits),
			"first_seen": group["time_utc"].min().strftime("%Y-%m-%dT%H:%M:%SZ"),
			"last_seen": group["time_utc"].max().strftime("%Y-%m-%dT%H:%M:%SZ"),
			"days_seen": int(group["time_utc"].dt.date.nunique()),
			"report_count": int(len(group)),
			"visits_by_landing": json.dumps({names[k]: v for k, v in sorted(visits.items(), key=lambda kv: -kv[1])}),
		}
		for landing in landings:
			row[f"visits_{landing['stop_id']}"] = visits.get(landing["stop_id"], 0)
		rows.append(row)
	columns = [
		"mmsi", "name", "length_m", "beam_m", "vessel_type", "visits_total", "landings_visited", "first_seen", "last_seen", "days_seen",
		"report_count", "visits_by_landing",
	] + [f"visits_{l['stop_id']}" for l in landings]
	roster = pd.DataFrame(rows, columns=columns)
	return roster.sort_values(["visits_total", "mmsi"], ascending=[False, True]).reset_index(drop=True)


# ---- fetch / build ------------------------------------------------------------------------------------------


def _manifest():
	m = manifest_module.Manifest.load(SOURCE)
	m.describe(
		description="NOAA / USCG Nationwide AIS, MarineCadastre 'csv2' daily files (reports filtered to one per minute), four weeks across 2024-2025",
		homepage="https://hub.marinecadastre.gov/pages/vesseltraffic",
		license="CC0 1.0 Universal public domain dedication (NOAA Office for Coastal Management, MarineCadastre AIS). Acknowledgement: U.S. Coast Guard Navigation Center.",
		terms_url="https://github.com/ocm-marinecadastre/ais-vessel-traffic",
		notes=[
			"Daily zstd-compressed CSV from the NOAA OCM open-data container; the GeoParquet product only covers 2024, so csv2 is used for both years.",
			"Weeks (Monday to Sunday, UTC): " + ", ".join(WEEKS) + ".",
			"Citation: Martin, D. R., J. Brass, M. Dornback, and J. Fontenault. Nationwide Automatic Identification System. NOAA Office for Coastal Management.",
		],
	)
	return m


def all_days():
	return [day for week in WEEKS for day in week_days(week)]


def fetch(slice_, force=False, log=print):
	m = _manifest()
	days = all_days()
	for i, day in enumerate(days, 1):
		status = fetcher.fetch_file(m, day_name(day), day_url(day), paths.raw_dir(SOURCE) / day_name(day), force=force)
		log(f"  [{i}/{len(days)}] {day_name(day)}: {status}")


def _read_day(path):
	box = AIS_HARBOR_BBOX
	with pa.CompressedInputStream(pa.OSFile(str(path)), "zstd") as stream:
		table = pacsv.read_csv(stream, convert_options=pacsv.ConvertOptions(include_columns=_COLUMNS))
	mask = pc.and_(
		pc.and_(pc.greater_equal(table["latitude"], box["south"]), pc.less_equal(table["latitude"], box["north"])),
		pc.and_(pc.greater_equal(table["longitude"], box["west"]), pc.less_equal(table["longitude"], box["east"])),
	)
	mask = pc.and_(mask, pc.and_(pc.greater_equal(table["vessel_type"], PASSENGER_TYPES[0]), pc.less_equal(table["vessel_type"], PASSENGER_TYPES[1])))
	mask = pc.and_(mask, pc.and_(pc.greater_equal(table["length"], LENGTH_RANGE_M[0] - 5), pc.less_equal(table["length"], LENGTH_RANGE_M[1] + 5)))
	return table.filter(mask).to_pandas()


def harbor_landings(m=None):
	"""Every GTFS stop inside the AIS harbor box (not only the slice), from the cached feed."""
	box = AIS_HARBOR_BBOX
	feed_path = paths.raw_dir(gtfs.SOURCE) / "gtfs.zip"
	if not gtfs._manifest().verify("gtfs.zip", feed_path):
		raise SystemExit(f"{feed_path} is missing or does not match the manifest; run fetch first")
	feed = gtfs.read_feed(feed_path)
	stops = feed["stops"]
	inside = stops[(stops.stop_lat.between(box["south"], box["north"])) & (stops.stop_lon.between(box["west"], box["east"]))]
	return [{"stop_id": s.stop_id, "stop_name": s.stop_name, "lat": s.stop_lat, "lon": s.stop_lon} for s in inside.itertuples()]


def build(slice_, log=print):
	m = _manifest()
	cache = paths.raw_dir(SOURCE)
	frames = []
	for day in all_days():
		path = cache / day_name(day)
		if not m.verify(day_name(day), path):
			raise SystemExit(f"{path} is missing or does not match the manifest; run fetch first")
		frame = _read_day(path)
		frames.append(frame)
		log(f"  {day}: {len(frame)} passenger-vessel reports in the harbor box")
	points = pd.concat(frames, ignore_index=True)
	points["time_utc"] = pd.to_datetime(points["base_date_time"], utc=True)
	points = clean_ais(points)
	points["north_m"], points["east_m"] = geodesy.to_local(points["latitude"].to_numpy(), points["longitude"].to_numpy())

	# Vessel size is judged per MMSI on its most common reported dimensions, then all of its reports are kept.
	dims = points.groupby("mmsi").agg(length=("length", _mode), width=("width", _mode), vessel_type=("vessel_type", _mode))
	keep = static_match(dims)
	points = points[points["mmsi"].isin(dims.index[keep])].copy()
	log(f"  {points['mmsi'].nunique()} passenger vessels of {LENGTH_RANGE_M[0]}-{LENGTH_RANGE_M[1]} m seen in the harbor box")

	landings = harbor_landings()
	points = points.rename(columns={"latitude": "lat", "longitude": "lon"})
	roster = build_roster(points, landings)
	out = paths.ais_processed_dir()
	out.mkdir(parents=True, exist_ok=True)
	roster.to_csv(out / "candidate_roster.csv", index=False)
	log(f"  candidate_roster.csv: {len(roster)} vessels")

	tracks = points[points["mmsi"].isin(roster["mmsi"])].sort_values(["mmsi", "time_utc"])
	table = pd.DataFrame(
		{
			"mmsi": tracks["mmsi"].astype("int64"),
			"time_utc": tracks["time_utc"],
			"lat": tracks["lat"],
			"lon": tracks["lon"],
			"north_m": tracks["north_m"],
			"east_m": tracks["east_m"],
			"sog_kn": tracks["sog"],
			"sog_mps": tracks["sog"] * KNOT,
			"cog_deg": tracks["cog"],
			"heading_deg": tracks["heading"],
		}
	)
	table.to_parquet(out / "nycferry_tracks.parquet", index=False)
	log(f"  nycferry_tracks.parquet: {len(table)} reports from {table['mmsi'].nunique()} vessels")
