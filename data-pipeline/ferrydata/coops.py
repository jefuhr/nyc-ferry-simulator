"""NOAA CO-OPS (tides and currents): current predictions and NAVD88 water levels.

Direction semantics: NOAA current directions are the direction the water flows TOWARD, degrees true. A
prediction's signed major-axis velocity is positive on the flood (toward meanFloodDir) and negative on the ebb
(toward meanEbbDir). Speeds below are magnitudes in m/s; the API reports cm/s when units=metric.
"""

import io
import json
import math

import pandas as pd

from ferrydata import fetcher, geodesy, manifest as manifest_module, paths

API = "https://api.tidesandcurrents.noaa.gov/api/prod/datagetter"
MDAPI = "https://api.tidesandcurrents.noaa.gov/mdapi/prod/webapi"
SOURCE = "coops"
LICENSE = (
	"Public domain: NOAA CO-OPS data are produced by the US Government. Disclaimer and data use: "
	"https://tidesandcurrents.noaa.gov/disclaimers.html (NOAA asks that users cite NOAA CO-OPS as the source)."
)


# ---- pure parsing -------------------------------------------------------------------------------------------


def current_vector(velocity_major_cm_s, mean_flood_dir, mean_ebb_dir):
	"""(speed m/s, direction toward in degrees [0, 360), phase) from a signed major-axis velocity in cm/s."""
	velocity = velocity_major_cm_s / 100.0
	if velocity > 0:
		phase, direction = "flood", mean_flood_dir
	elif velocity < 0:
		phase, direction = "ebb", mean_ebb_dir
	else:
		phase, direction = "slack", mean_flood_dir
	return abs(velocity), direction % 360.0, phase


def components(speed, direction_toward_deg):
	"""(north, east) components of a flow of `speed` toward `direction_toward_deg`."""
	radians = math.radians(direction_toward_deg)
	return speed * math.cos(radians), speed * math.sin(radians)


def _check_api_text(text):
	# The API answers 200 with an error line, sometimes after the CSV header.
	if any(line.lstrip().lower().startswith("error") for line in text.splitlines()) or "<html" in text[:200].lower():
		raise ValueError(text.strip()[:300])


def _utc_iso(series):
	return pd.to_datetime(series, utc=True).dt.strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_currents_csv(text):
	"""CO-OPS currents_predictions CSV (time_zone=gmt, units=metric) -> tidy frame."""
	_check_api_text(text)
	raw = pd.read_csv(io.StringIO(text), skipinitialspace=True)
	rows = []
	for velocity, flood, ebb in zip(raw["Velocity_Major"], raw["meanFloodDir"], raw["meanEbbDir"]):
		speed, direction, phase = current_vector(float(velocity), float(flood), float(ebb))
		north, east = components(speed, direction)
		rows.append((speed, direction, phase, north, east))
	out = pd.DataFrame(rows, columns=["speed_mps", "direction_toward_deg", "phase", "north_mps", "east_mps"])
	out.insert(0, "time_utc", _utc_iso(raw["Time"]))
	out["velocity_major_mps"] = raw["Velocity_Major"].astype(float) / 100.0
	out["depth_m"] = pd.to_numeric(raw["Depth"], errors="coerce")
	out["bin"] = raw["Bin"]
	return out


def parse_currents_events_csv(text):
	"""MAX_SLACK events of a subordinate station (no continuous series is published)."""
	_check_api_text(text)
	raw = pd.read_csv(io.StringIO(text), skipinitialspace=True)
	rows = []
	for velocity, flood, ebb in zip(raw["Velocity_Major"], raw["meanFloodDir"], raw["meanEbbDir"]):
		speed, direction, phase = current_vector(float(velocity), float(flood), float(ebb))
		rows.append((speed, direction, phase))
	out = pd.DataFrame(rows, columns=["speed_mps", "direction_toward_deg", "phase"])
	out.insert(0, "time_utc", _utc_iso(raw["Time"]))
	out["event"] = raw["Type"]
	return out


def parse_water_level_csv(text):
	_check_api_text(text)
	raw = pd.read_csv(io.StringIO(text), skipinitialspace=True)
	raw.columns = [c.strip() for c in raw.columns]
	out = pd.DataFrame(
		{
			"time_utc": _utc_iso(raw["Date Time"]),
			"water_level_navd88_m": pd.to_numeric(raw["Water Level"], errors="coerce"),
			"quality": raw["Quality"].astype(str),
		}
	)
	return out


def max_gap_hours(times):
	"""Largest gap between consecutive timestamps in hours; inf when there are fewer than two."""
	times = pd.Series(pd.to_datetime(times, utc=True)).sort_values()
	if len(times) < 2:
		return math.inf
	return float(times.diff().max().total_seconds() / 3600.0)


def select_current_stations(stations_json, slice_):
	"""One entry per station id inside the slice bbox plus margin. 'H' stations are harmonic (full series)."""
	margin = slice_.station_margin_deg
	chosen = {}
	for station in stations_json["stations"]:
		lat, lon = station["lat"], station["lng"]
		if not (slice_.south - margin <= lat <= slice_.north + margin and slice_.west - margin <= lon <= slice_.east + margin):
			continue
		entry = chosen.setdefault(
			station["id"],
			{
				"id": station["id"],
				"name": station["name"],
				"lat": lat,
				"lon": lon,
				"type": station.get("type"),
				"depths_m": [],
			},
		)
		if station.get("depth") is not None:
			entry["depths_m"].append(station["depth"])
	return [chosen[key] for key in sorted(chosen)]


# ---- fetch --------------------------------------------------------------------------------------------------


def _date(text):
	return text.replace("-", "")


def currents_url(station_id, slice_, events=False):
	interval = "MAX_SLACK" if events else "6"
	return (
		f"{API}?product=currents_predictions&station={station_id}&begin_date={_date(slice_.window_start)}"
		f"&end_date={_date(slice_.window_end)}&interval={interval}&time_zone=gmt&units=metric&format=csv"
	)


def water_level_url(station_id, slice_):
	return (
		f"{API}?product=water_level&datum=NAVD&station={station_id}&begin_date={_date(slice_.window_start)}"
		f"&end_date={_date(slice_.window_end)}&time_zone=gmt&units=metric&format=csv"
	)


def _text_validator(path):
	_check_api_text(path.read_text(encoding="utf-8", errors="replace"))


def _water_level_validator(path):
	text = path.read_text(encoding="utf-8", errors="replace")
	_check_api_text(text)
	if len(text.strip().splitlines()) < 2:
		raise ValueError("no water level observations returned for the window")


def _manifest():
	m = manifest_module.Manifest.load(SOURCE)
	m.describe(
		description="NOAA CO-OPS current predictions (6-minute series at harmonic stations, max/slack events at subordinate ones) and observed water levels in NAVD88",
		homepage="https://tidesandcurrents.noaa.gov/",
		license=LICENSE,
		terms_url="https://tidesandcurrents.noaa.gov/disclaimers.html",
		notes=[
			"Times are UTC (time_zone=gmt). Currents are requested with units=metric: velocities arrive in cm/s and are converted to m/s at build time.",
			"Current directions are the direction the water flows toward, degrees true; the default (shallowest) bin of each station is used.",
			"Water levels are observed (verified when available) at 6 minute intervals in metres relative to NAVD88.",
			"Stations without a NAVD88 datum answer with an error and are skipped; the skipped ids are listed in skipped_water_level_stations.",
		],
	)
	return m


def fetch(slice_, force=False, log=print):
	m = _manifest()
	cache = paths.raw_dir(SOURCE)
	stations_url = f"{MDAPI}/stations.json?type=currentpredictions&units=metric"
	status = fetcher.fetch_file(m, "stations_currentpredictions.json", stations_url, cache / "stations_currentpredictions.json", force=force, pinned=False)
	log(f"  current prediction station list: {status}")
	stations = select_current_stations(json.loads((cache / "stations_currentpredictions.json").read_text(encoding="utf-8")), slice_)
	for station in stations:
		events = station["type"] != "H"
		name = f"currents_{station['id']}{'_events' if events else ''}.csv"
		status = fetcher.fetch_file(m, name, currents_url(station["id"], slice_, events), cache / name, force=force, validate=_text_validator)
		log(f"  currents {station['id']} ({station['name']}): {status}")

	skipped = []
	for station_id in slice_.water_level_stations:
		meta_name = f"station_{station_id}.json"
		fetcher.fetch_file(m, meta_name, f"{MDAPI}/stations/{station_id}.json", cache / meta_name, force=force, pinned=False)
		name = f"water_level_{station_id}.csv"
		try:
			status = fetcher.fetch_file(m, name, water_level_url(station_id, slice_), cache / name, force=force, validate=_water_level_validator)
		except ValueError as error:
			skipped.append({"station": station_id, "reason": str(error)})
			log(f"  water level {station_id}: skipped ({error})")
			continue
		log(f"  water level {station_id}: {status}")
		datum_name = f"datums_{station_id}.json"
		fetcher.fetch_file(m, datum_name, f"{MDAPI}/stations/{station_id}/datums.json?units=metric", cache / datum_name, force=force)
	m.header["skipped_water_level_stations"] = skipped
	m.header["window_utc"] = {"start": slice_.window_start, "end": slice_.window_end}
	m.save()


# ---- build --------------------------------------------------------------------------------------------------


def _locate(lat, lon):
	north, east = geodesy.to_local(lat, lon)
	return {"lat": lat, "lon": lon, "north_m": round(float(north), 3), "east_m": round(float(east), 3)}


def build(slice_, log=print):
	cache = paths.raw_dir(SOURCE)
	out = paths.processed_dir(slice_.name)
	(out / "currents").mkdir(parents=True, exist_ok=True)
	(out / "water_levels").mkdir(parents=True, exist_ok=True)
	m = _manifest()
	listing = json.loads((cache / "stations_currentpredictions.json").read_text(encoding="utf-8"))
	stations_out = []
	for station in select_current_stations(listing, slice_):
		events = station["type"] != "H"
		name = f"currents_{station['id']}{'_events' if events else ''}.csv"
		if not m.verify(name, cache / name):
			raise SystemExit(f"{cache / name} is missing or does not match the manifest; run fetch first")
		text = (cache / name).read_text(encoding="utf-8")
		frame = parse_currents_events_csv(text) if events else parse_currents_csv(text)
		target = f"currents/{station['id']}{'_events' if events else ''}.csv"
		frame.to_csv(out / target, index=False)
		stations_out.append(
			{
				"id": station["id"],
				"name": station["name"],
				"kind": "current_prediction",
				"station_type": "harmonic" if not events else "subordinate",
				"series": "6-minute" if not events else "max-slack events only",
				"depths_m": station["depths_m"],
				"in_slice": slice_.contains(station["lon"], station["lat"]),
				"file": target,
				"rows": int(len(frame)),
				**_locate(station["lat"], station["lon"]),
			}
		)
		log(f"  currents/{station['id']}: {len(frame)} rows")
	skipped = {entry["station"] for entry in m.header.get("skipped_water_level_stations", [])}
	for station_id in slice_.water_level_stations:
		if station_id in skipped:
			continue
		name = f"water_level_{station_id}.csv"
		if not m.verify(name, cache / name):
			raise SystemExit(f"{cache / name} is missing or does not match the manifest; run fetch first")
		frame = parse_water_level_csv((cache / name).read_text(encoding="utf-8"))
		frame.to_csv(out / "water_levels" / f"{station_id}.csv", index=False)
		meta = json.loads((cache / f"station_{station_id}.json").read_text(encoding="utf-8"))["stations"][0]
		datum_name = f"datums_{station_id}.json"
		if not m.verify(datum_name, cache / datum_name):
			raise SystemExit(f"{cache / datum_name} is missing or does not match the manifest; run fetch --only coops first")
		from ferrydata.navdepth import datum_offset
		datums = json.loads((cache / datum_name).read_text(encoding="utf-8"))
		offset = datum_offset(datums)
		stations_out.append(
			{
				"id": station_id,
				"name": meta["name"],
				"kind": "water_level",
				"mllw_navd88_offset_m": offset,
				"datum_epoch": datums.get("epoch"),
				"datum_source": m.files[datum_name],
				"datum": "NAVD88",
				"in_slice": slice_.contains(meta["lng"], meta["lat"]),
				"file": f"water_levels/{station_id}.csv",
				"rows": int(len(frame)),
				**_locate(meta["lat"], meta["lng"]),
			}
		)
		log(f"  water_levels/{station_id}: {len(frame)} rows")
	(out / "coops_stations.json").write_text(json.dumps({"window_utc": {"start": slice_.window_start, "end": slice_.window_end}, "stations": stations_out}, indent="\t") + "\n", encoding="utf-8")
