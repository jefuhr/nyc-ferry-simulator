"""Harbor tiles v1: 4 km half-open squares in the shared world tangent plane.

The lon/lat bbox is only a read envelope. Vector clipping uses the transformed
local square, with sampled edges to preserve its shape to sub-millimetre accuracy.
"""

import math
import re
import json
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from functools import cached_property

import numpy as np
from shapely.geometry import Polygon
from shapely.geometry import Point, box

from ferrydata import geodesy
from ferrydata.slices import LOWER_EAST_RIVER, Slice

SIZE_M = 4000
MARGIN_M = 250
INDICES = range(-2, 4)
OVERLAP_TOLERANCE_M = 0.05


def _square(n0, e0, n1, e1):
	# At most 50 m between vertices: straight lon/lat chords approximate
	# the curved image of the local square, rather than a separate projection.
	count = math.ceil(max(n1 - n0, e1 - e0) / 50)
	t = np.linspace(0., 1., count, endpoint=False)
	north = np.concatenate((np.full(count, n0), n0 + t * (n1 - n0),
		np.full(count, n1), n1 - t * (n1 - n0)))
	east = np.concatenate((e0 + t * (e1 - e0), np.full(count, e1),
		e1 - t * (e1 - e0), np.full(count, e0)))
	lat, lon = geodesy.from_local(north, east)
	return Polygon(zip(lon, lat))


@dataclass(frozen=True)
class Tile(Slice):
	i: int
	j: int
	margin_m: float = MARGIN_M

	@property
	def id(self):
		return f"tile_{self.i}_{self.j}"

	@property
	def local_bounds(self):
		"""(south/north minimum, west/east minimum, north maximum, east maximum), m."""
		return (SIZE_M * self.i, SIZE_M * self.j, SIZE_M * (self.i + 1), SIZE_M * (self.j + 1))

	@property
	def grown_local_bounds(self):
		n0, e0, n1, e1 = self.local_bounds
		return (n0 - self.margin_m, e0 - self.margin_m, n1 + self.margin_m, e1 + self.margin_m)

	@cached_property
	def core_polygon(self):
		return _square(*self.local_bounds)

	@cached_property
	def polygon(self):
		return _square(*self.grown_local_bounds)

	@property
	def centre(self):
		n0, e0, n1, e1 = self.local_bounds
		lat, lon = geodesy.from_local((n0 + n1) / 2, (e0 + e1) / 2)
		return float(lat), float(lon)

	def corners(self):
		n0, e0, n1, e1 = self.grown_local_bounds
		return {key: tuple(float(x) for x in geodesy.from_local(n, e)) for key, (n, e) in
			{"sw": (n0, e0), "se": (n0, e1), "nw": (n1, e0), "ne": (n1, e1)}.items()}

	def contains(self, lon, lat):
		n, e = geodesy.to_local(lat, lon)
		n0, e0, n1, e1 = self.grown_local_bounds
		return bool(n0 <= n <= n1 and e0 <= e <= e1)

	def owns(self, lon, lat):
		north, east = geodesy.to_local(lat, lon)
		# Remove only the nanometre-scale ECEF round-trip noise at boundaries.
		north, east = round(float(north), 8), round(float(east), 8)
		n0, e0, n1, e1 = self.local_bounds
		return n0 <= north < n1 and e0 <= east < e1

	def expanded(self, extra_m):
		return make_tile(self.i, self.j, self.enc_cells, self.margin_m + extra_m)


def make_tile(i, j, enc_cells=(), margin_m=MARGIN_M):
	if i not in INDICES or j not in INDICES:
		raise ValueError(f"tile outside harbor extent: {i}, {j}")
	n0, e0 = SIZE_M * i - margin_m, SIZE_M * j - margin_m
	polygon = _square(n0, e0, SIZE_M * (i + 1) + margin_m, SIZE_M * (j + 1) + margin_m)
	west, south, east, north = polygon.bounds
	return Tile(f"harbor/tiles/tile_{i}_{j}", south, north, west, east, tuple(enc_cells), 0.,
		LOWER_EAST_RIVER.water_level_stations, LOWER_EAST_RIVER.window_start,
		LOWER_EAST_RIVER.window_end, i, j, margin_m)


def grid(enc_cells=()):
	return [make_tile(i, j, enc_cells) for i in INDICES for j in INDICES]


def get_tile(identifier, enc_cells=()):
	match = re.fullmatch(r"tile_(-?(?:0|[1-9][0-9]*))_(-?(?:0|[1-9][0-9]*))", identifier)
	if not match:
		raise ValueError(f"invalid tile id: {identifier}")
	i, j = map(int, match.groups())
	if identifier != f"tile_{i}_{j}":
		raise ValueError(f"noncanonical tile id: {identifier}")
	return make_tile(i, j, enc_cells)


def owner(north, east):
	i, j = math.floor(north / SIZE_M), math.floor(east / SIZE_M)
	return make_tile(i, j) if i in INDICES and j in INDICES else None


def activity(tile, water_geometries, landing_points):
	reasons = []
	if any(tile.core_polygon.intersects(g) for g in water_geometries):
		reasons.append("water")
	if any(tile.owns(p.x, p.y) for p in landing_points):
		reasons.append("landing")
	return reasons


def extent():
	"""Read envelope, including output overlap and a 100 m merge support halo."""
	polygons = [make_tile(i, j, margin_m=MARGIN_M + 100).polygon for i, j in ((-2, -2), (-2, 3), (3, -2), (3, 3))]
	west = min(p.bounds[0] for p in polygons); south = min(p.bounds[1] for p in polygons)
	east = max(p.bounds[2] for p in polygons); north = max(p.bounds[3] for p in polygons)
	s = LOWER_EAST_RIVER
	return Slice("harbor", south, north, west, east, (), 0., s.water_level_stations, s.window_start, s.window_end)


def overlap_result(a, b, nodata_a, nodata_b, mask=None):
	"""Compare already aligned matching cells; missing data on either side fails."""
	if a.shape != b.shape:
		raise ValueError("overlap arrays must have matching shapes")
	mask = np.ones(a.shape, dtype=bool) if mask is None else np.asarray(mask, dtype=bool)
	valid_a = np.isfinite(a) & (a != nodata_a) if nodata_a is not None else np.isfinite(a)
	valid_b = np.isfinite(b) & (b != nodata_b) if nodata_b is not None else np.isfinite(b)
	mismatches = int(((valid_a != valid_b) & mask).sum())
	valid = valid_a & valid_b & mask
	difference = float(np.max(np.abs(a[valid].astype(float) - b[valid].astype(float)))) if valid.any() else None
	return {"passed": mismatches == 0 and (difference is None or difference <= OVERLAP_TOLERANCE_M),
		"matching_cells": int(valid.sum()), "nodata_mismatches": mismatches,
		"max_difference_m": difference, "tolerance_m": OVERLAP_TOLERANCE_M}


def _json(path):
	return json.loads(path.read_text(encoding="utf-8"))


def _write(path, payload):
	path.parent.mkdir(parents=True, exist_ok=True)
	path.write_text(json.dumps(payload, indent="\t", ensure_ascii=False) + "\n", encoding="utf-8")


def _cached(source, name):
	from ferrydata import manifest, paths
	path = paths.raw_dir(source) / name
	if not manifest.load_combined(source).verify(name, path):
		raise ValueError(f"unverified or missing {source}/{name}; run fetch --harbor")
	return path


def _fetch_addition(source, name, url, log=print):
	from ferrydata import fetcher, manifest, paths
	combined = manifest.load_combined(source)
	dest = paths.raw_dir(source) / name
	if name in combined.files:
		if not combined.verify(name, dest):
			status = fetcher.restore_pinned_file(combined, name, dest)
		else:
			status = "cached"
	else:
		if dest.exists():
			raise fetcher.ChecksumError(f"refusing to replace existing unrecorded raw file {dest}")
		side = manifest.Manifest.load(source + "-harbor")
		base = manifest.Manifest.load(source)
		side.header = {**base.header, **side.header, "source": source + "-harbor"}
		side.header["harbor_note"] = "Additive harbor records sharing the original raw directory; original slice pins remain unchanged."
		status = fetcher.fetch_file(side, name, url, dest)
	log(f"  {source}/{name}: {status}")
	return dest


def _catalog():
	from ferrydata import enc
	return enc.catalog_cells(_cached(enc.SOURCE, "ENCProdCat_19115.xml"), box(*extent().bbox))


def fetch(log=print):
	"""Fetch missing harbor sources sequentially; existing raw files/pins are immutable."""
	from ferrydata import coops, cudem, enc, geoid, gtfs, manifest, paths
	from ferrydata.slices import cudem_tile_ids
	_fetch_addition(gtfs.SOURCE, "gtfs.zip", gtfs.URL, log)
	_fetch_addition(enc.SOURCE, "ENCProdCat_19115.xml", enc.CATALOG_URL, log)
	cells = _catalog()
	if not cells:
		raise ValueError("NOAA catalogue has no usage band 5 cells for harbor")
	for cell in cells:
		_fetch_addition(enc.SOURCE, cell + ".zip", enc.URL.format(cell=cell), log)
	known = manifest.load_combined(cudem.SOURCE)
	missing = []
	for tile_id in cudem_tile_ids(extent()):
		matches = [name for name in known.files if name.startswith(tile_id + "_")]
		if matches:
			name = max(matches, key=cudem._version_key)
			_fetch_addition(cudem.SOURCE, name, known.files[name]["url"], log)
		else:
			missing.append(tile_id)
	if missing:
		for _, key in sorted(cudem.resolve_tile_keys(missing).items()):
			_fetch_addition(cudem.SOURCE, key.rsplit("/", 1)[-1], f"{cudem.BUCKET}/{key}", log)
	_fetch_addition(coops.SOURCE, "stations_currentpredictions.json", f"{coops.MDAPI}/stations.json?type=currentpredictions&units=metric", log)
	listing = _fetch_addition(coops.SOURCE, "stations_waterlevels.json", f"{coops.MDAPI}/stations.json?type=waterlevels&units=metric", log)
	for station in _json(listing)["stations"]:
		if not extent().contains(float(station["lng"]), float(station["lat"])):
			continue
		sid = station["id"]
		_fetch_addition(coops.SOURCE, f"station_{sid}.json", f"{coops.MDAPI}/stations/{sid}.json", log)
		_fetch_addition(coops.SOURCE, f"datums_{sid}.json", f"{coops.MDAPI}/stations/{sid}/datums.json?units=metric", log)
	_cached(geoid.SOURCE, geoid.GRID_NAME)  # already pinned; no unapproved new geoid source
	return 0


def _feed():
	from ferrydata import gtfs
	return gtfs.read_feed(_cached(gtfs.SOURCE, "gtfs.zip"))


def _stop_inventory(feed):
	from ferrydata import gtfs
	ferry_ids = gtfs.ferry_stop_ids(feed)
	stops, excluded = [], []
	for s in feed["stops"].itertuples(index=False):
		n, e = (float(x) for x in geodesy.to_local(s.stop_lat, s.stop_lon))
		tile = owner(n, e)
		entry = {"stop_id": s.stop_id, "stop_name": s.stop_name, "lat": s.stop_lat, "lon": s.stop_lon,
			"north_m": n, "east_m": e, "tile": tile.id if tile is not None else None}
		if s.stop_id not in ferry_ids:
			excluded.append({**entry, "reason": "bus shuttle stop (not served by GTFS route_type 4)"})
		elif s.stop_id == "88":
			excluded.append({**entry, "reason": "Rockaway intentionally excluded: open-water seas are a later milestone"})
		else:
			stops.append(entry)
	return stops, excluded


def inventory():
	from dataclasses import replace
	from ferrydata import enc
	cells = _catalog()
	region = replace(extent(), enc_cells=cells)
	layers, _ = enc.read_layers(region, ("DEPARE",))
	water = list(layers["DEPARE"].geometry) if "DEPARE" in layers else []
	stops, excluded = _stop_inventory(_feed())
	points = [Point(s["lon"], s["lat"]) for s in stops]
	entries = []
	for tile in grid(cells):
		reasons = activity(tile, water, points)
		entries.append({"id": tile.id, "i": tile.i, "j": tile.j, "bounds_local_m": tile.local_bounds,
			"grown_bounds_local_m": tile.grown_local_bounds, "grown_corners_latlon": tile.corners(),
			"read_bbox_lonlat": tile.bbox, "active": bool(reasons), "reasons": reasons,
			"stop_ids": [s["stop_id"] for s in stops if s["tile"] == tile.id], "outputs": []})
	return {"schema": "harbor-tiles-v1", "origin_latlon": [geodesy.ORIGIN_LAT, geodesy.ORIGIN_LON],
		"tile_size_m": SIZE_M, "margin_m": MARGIN_M, "enc_cells": list(cells),
		"bounds_order": ["north_min_m", "east_min_m", "north_max_m", "east_max_m"],
		"stops": stops, "excluded_stops": excluded, "tiles": entries}


def _stations(tile):
	from ferrydata import coops, navdepth
	current = _json(_cached(coops.SOURCE, "stations_currentpredictions.json"))
	entries = []
	for s in coops.select_current_stations(current, tile):
		if tile.contains(s["lon"], s["lat"]):
			entries.append({**s, **coops._locate(s["lat"], s["lon"]), "kind": "current_prediction"})
	for s in _json(_cached(coops.SOURCE, "stations_waterlevels.json"))["stations"]:
		if not tile.contains(float(s["lng"]), float(s["lat"])):
			continue
		sid = s["id"]
		meta = _json(_cached(coops.SOURCE, f"station_{sid}.json"))["stations"][0]
		datums = _json(_cached(coops.SOURCE, f"datums_{sid}.json"))
		entry = {"id": sid, "name": meta["name"], "kind": "water_level",
			**coops._locate(float(meta["lat"]), float(meta["lng"])), "datum_epoch": datums.get("epoch")}
		try:
			entry["mllw_navd88_offset_m"] = navdepth.datum_offset(datums)
			entry["datum"] = "NAVD88"
		except ValueError as error:
			entry["datum_unavailable_reason"] = str(error)
		entries.append(entry)
	for entry in entries:
		n, e = geodesy.to_local(entry["lat"], entry["lon"])
		owned = owner(round(float(n), 8), round(float(e), 8))
		entry["owner_tile"] = owned.id if owned is not None else None
	return {"stations": sorted(entries, key=lambda s: (s["kind"], s["id"]))}


def _raw_sources(tile, stations):
	from ferrydata import coops, cudem, enc, geoid, gtfs, manifest, paths
	names = {gtfs.SOURCE: ["gtfs.zip"], enc.SOURCE: ["ENCProdCat_19115.xml"] + [c + ".zip" for c in tile.enc_cells],
		cudem.SOURCE: [p.name for p in cudem._tile_files(manifest.load_combined(cudem.SOURCE), tile)],
		coops.SOURCE: ["stations_currentpredictions.json", "stations_waterlevels.json", "datums_8518750.json"],
		geoid.SOURCE: [geoid.GRID_NAME]}
	for s in stations["stations"]:
		if s["kind"] == "water_level":
			names[coops.SOURCE] += [f"station_{s['id']}.json", f"datums_{s['id']}.json"]
	return [{"source": source, "name": name, "sha256": manifest.load_combined(source).files[name]["sha256"]}
		for source in sorted(names) for name in sorted(set(names[source]))]


def _build_tile(tile, log=print):
	import resource
	from ferrydata import cudem, enc, geoid, gtfs, landings, navdepth, paths
	started = time.perf_counter()
	out = paths.processed_dir(tile.name)
	gtfs.build(tile, log)
	enc.build(tile, log)
	cudem.build(tile, log)
	stations = _stations(tile)
	_write(out / "coops_stations.json", stations)
	geoid.build(tile, log)
	navdepth.build_harbor(tile, log)
	landings.build_tile(tile, log)
	_write(out / "raw_sources.json", {"files": _raw_sources(tile, stations)})
	peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
	_write(out / "build_metrics.json", {"elapsed_seconds": time.perf_counter() - started,
		"peak_rss_bytes": int(peak if sys.platform == "darwin" else peak * 1024),
		"measurement": "isolated tile subprocess RUSAGE_SELF high water RSS"})


def _worker():
	identifier, cells = json.loads(sys.argv[1])
	_build_tile(get_tile(identifier, cells))


def build(tile_ids=None, log=print):
	"""Build active tiles serially in isolated processes under the shared heavy lock."""
	from pathlib import Path
	from ferrydata import manifest, paths
	requested = set(tile_ids) if tile_ids is not None else None
	if requested is not None:
		for identifier in sorted(requested):
			get_tile(identifier)
	index = inventory()
	out = paths.processed_dir("harbor")
	previous = _json(out / "index.json") if (out / "index.json").exists() else {}
	previous_entries = {t["id"]: t for t in previous.get("tiles", [])}
	lock = Path.home() / "FerrySim-cache" / "heavy.lock"
	lock.parent.mkdir(parents=True, exist_ok=True)
	failed = False
	for entry in index["tiles"]:
		identifier = entry["id"]
		if not entry["active"]:
			continue
		if requested is not None and identifier not in requested:
			old = previous_entries.get(identifier, {})
			for key in ("outputs", "build_error", "metrics"):
				if key in old:
					entry[key] = old[key]
			continue
		log(f"[build] {identifier}")
		if log is print:
			sys.stdout.flush()
		env = dict(os.environ)
		env["PYTHONPATH"] = str(paths.REPO_ROOT / "data-pipeline") + os.pathsep + env.get("PYTHONPATH", "")
		launched = time.perf_counter()
		result = subprocess.run(["lockf", "-k", str(lock), sys.executable, "-c",
			"from ferrydata.tiles import _worker; _worker()", json.dumps([identifier, index["enc_cells"]])],
			env=env, capture_output=True, text=True)
		target = out / "tiles" / identifier
		target.mkdir(parents=True, exist_ok=True)
		(target / "build.log").write_text(result.stdout + result.stderr, encoding="utf-8")
		if result.returncode:
			entry["build_error"] = (result.stderr or result.stdout)[-3000:]
			failed = True
			log(f"  FAIL: {entry['build_error']}")
		else:
			entry["metrics"] = _json(target / "build_metrics.json")
			entry["metrics"]["lock_wait_and_startup_seconds"] = max(0., time.perf_counter() - launched - entry["metrics"]["elapsed_seconds"])
			_write(target / "build_metrics.json", entry["metrics"])
			log(f"  done in {entry['metrics']['elapsed_seconds']:.2f}s, peak RSS {entry['metrics']['peak_rss_bytes'] / 2**20:.1f} MiB")
		entry["outputs"] = [{"file": str(p.relative_to(out)), "sha256": manifest.sha256_file(p), "size_bytes": p.stat().st_size}
			for p in sorted(target.rglob("*")) if p.is_file()]
	_write(out / "index.json", index)
	return 1 if failed else 0


def validate(log=print):
	from ferrydata import validate as validation
	return validation.run_harbor(log)
