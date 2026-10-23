"""ferrydata validate: check the processed outputs of a slice and write validation_report.md.

Thresholds come from the data-pipeline brief and are not relaxed here; a failing check is reported, not hidden.
"""

import json
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone

import numpy as np
import pandas as pd
import rasterio
from shapely.geometry import LineString, MultiLineString, MultiPolygon, Point, Polygon, shape

from ferrydata import depth, geodesy, landings as landing_module, paths

SHORE_MAX_M = 50.0
UKC_MARGIN_M = 0.5
GEOID_RANGE_M = (-35.0, -29.0)
MAX_SERIES_GAP_H = 1.0
REQUIRED_BRIDGES = ("Brooklyn", "Manhattan", "Williamsburg")
MIN_VALID_FRACTION = 0.5
APPROACH_BOX_M = 30.0
GEODESY_TOLERANCE_M = 1e-3
CSHARP_VECTORS = paths.REPO_ROOT / "core" / "dotnet" / "FerrySim.Core.Tests" / "Contracts" / "LocalTangentPlaneTests.cs"
K_CLASS = paths.REPO_ROOT / "vessels" / "k-class.json"

REQUIRED_OUTPUTS = (
	"landings.geojson", "depth_areas.geojson", "routes.geojson", "schedule_summary.json", "depth_navd88.tif", "depth_navd88.json", "aids.geojson", "channels.geojson",
	"shoreline_constructions.geojson", "bridges.geojson", "coops_stations.json", "geoid_offset.json",
	"landing_faces.geojson", "land_areas.geojson", "soundings.geojson", "depth_merged_navd88.tif", "depth_merged_navd88.json",
	"landing_depth_diff.csv", "depth_disagreement_gt1m.tif", "depth_chart_navd88.tif",
)


@dataclass
class CheckResult:
	name: str
	passed: bool
	summary: str
	details: list = field(default_factory=list)


# ---- helpers ------------------------------------------------------------------------------------------------


def _load_json(slice_name, filename):
	path = paths.processed_dir(slice_name) / filename
	if not path.exists():
		raise FileNotFoundError(f"{path} is missing; run ferrydata build first")
	return json.loads(path.read_text(encoding="utf-8"))


def local_geometry(feature):
	"""Shapely geometry in (east, north) metres rebuilt from the feature's local_coordinates ([north, east] pairs)."""
	kind = feature["geometry"]["type"]
	coords = feature["local_coordinates"]

	def swap(points):
		return [(east, north) for north, east in points]

	if kind == "Point":
		return Point(coords[1], coords[0])
	if kind == "LineString":
		return LineString(swap(coords))
	if kind == "MultiLineString":
		return MultiLineString([swap(line) for line in coords])
	if kind == "Polygon":
		return Polygon(swap(coords[0]), [swap(ring) for ring in coords[1:]])
	if kind == "MultiPolygon":
		return MultiPolygon([Polygon(swap(p[0]), [swap(r) for r in p[1:]]) for p in coords])
	raise ValueError(f"unsupported geometry type {kind}")


def coverage_gap_hours(times, start, end):
	"""Largest gap in hours across [start, end), counting the distance from each window edge to the nearest sample."""
	stamps = sorted(pd.to_datetime(times, utc=True))
	if not stamps:
		return (end - start).total_seconds() / 3600.0
	edges = [start] + stamps + [end]
	return max((b - a).total_seconds() for a, b in zip(edges[:-1], edges[1:])) / 3600.0


def csharp_reference_vectors():
	text = CSHARP_VECTORS.read_text(encoding="utf-8")
	number = r"(-?\d+(?:\.\d+)?)"
	pattern = re.compile(r"\[TestCase\(" + ", ".join([number] * 5) + r"\)\]")
	return [tuple(float(g) for g in m.groups()) for m in pattern.finditer(text)]


def k_class_max_draft_m():
	data = json.loads(K_CLASS.read_text(encoding="utf-8"))
	return float(data["particulars"]["maxDraft"]["value"])


def _md_table(headers, rows):
	lines = ["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"]
	lines += ["| " + " | ".join(str(c) for c in row) + " |" for row in rows]
	return lines


def _fmt(value, digits=2):
	return "n/a" if value is None else f"{value:.{digits}f}"


# ---- checks -------------------------------------------------------------------------------------------------


def check_outputs(slice_):
	out = paths.processed_dir(slice_.name)
	ais_dir = paths.ais_processed_dir()
	expected = [out / name for name in REQUIRED_OUTPUTS]
	expected += [ais_dir / "candidate_roster.csv", ais_dir / "nycferry_tracks.parquet"]
	missing = [str(path.relative_to(paths.REPO_ROOT)) for path in expected if not path.exists()]
	for folder in ("currents", "water_levels"):
		if not list((out / folder).glob("*.csv")):
			missing.append(f"{folder}/*.csv")
	return CheckResult("Required outputs exist", not missing, "all present" if not missing else "missing: " + ", ".join(missing))


def shoreline_result(rows):
	bad = [r for r in rows if r["distance_m"] is None or not np.isfinite(r["distance_m"]) or r["distance_m"] > SHORE_MAX_M or not r["water_side"]]
	table = _md_table(["stop_id", "landing", "face distance (m)", "water side", "source", "confidence"],
		[(r["stop_id"], r["stop_name"], _fmt(r["distance_m"], 1), r["water_side"], r["source"], r["confidence"]) for r in rows])
	summary = (f"all {len(rows)} landings have a face within {SHORE_MAX_M:g} m on the water side" if rows and not bad
		else f"{len(bad)} of {len(rows)} landings lack a valid face: " + ", ".join(r["stop_name"] for r in bad))
	return CheckResult(f"GTFS landing faces within {SHORE_MAX_M:g} m and on the water side", bool(rows) and not bad, summary, table)


def check_shoreline(slice_):
	out = paths.processed_dir(slice_.name)
	stops = _load_json(slice_.name, "landings.geojson")["features"]
	faces = _load_json(slice_.name, "landing_faces.geojson")["features"]
	water, land = landing_module.chart_masks(out)
	rows = []
	for stop in stops:
		p = stop["properties"]
		matches = [f for f in faces if f["properties"]["stop_id"] == p["stop_id"]]
		row = {"stop_id": p["stop_id"], "stop_name": p["stop_name"], "distance_m": None, "water_side": False, "source": "missing/duplicate", "confidence": "unknown"}
		if len(matches) == 1:
			props = matches[0]["properties"]
			face = landing_module.to_local(shape(props["face_geometry"]))
			point = Point(p["east_m"], p["north_m"])
			row.update(distance_m=face.distance(point), source=props["source"], confidence=props["confidence"],
				water_side=landing_module.face_on_water_side(face, props["outward_normal_ne"], props["bulkhead_anchor_en_m"], water, land))
		rows.append(row)
	result = shoreline_result(rows)
	result.details += ["", "The face line, not the GTFS passenger point or nearest bulkhead, defines the berth. Water-side validation recomputes coverage 0.5 m outside the face and at a 12 m outward probe against ENC depth and land polygons and checks the outward normal points away from the fixed shoreline. Derived faces and operational berth assignments require crew verification."]
	return result


def reference_water_level(slice_):
	station = slice_.water_level_stations[0]
	frame = pd.read_csv(paths.processed_dir(slice_.name) / "water_levels" / f"{station}.csv")
	row = frame.loc[frame["water_level_navd88_m"].idxmin()]
	return station, float(row["water_level_navd88_m"]), row["time_utc"]


@dataclass
class Approach:
	props: dict
	anchor_ne: tuple
	tangent_en: tuple
	how: str
	boxes: list  # one dict per evaluated box: direction (north, east), corners (north, east), stats
	route: object = None


def _box_stats(dataset, anchor_ne, direction, min_level):
	corners = depth.landing_box_local(anchor_ne, direction, APPROACH_BOX_M)
	polygon = depth.landing_box_polygon(anchor_ne, direction, APPROACH_BOX_M)
	sample = depth.read_polygon(dataset, polygon)
	stats = depth.depth_stats(sample.values, min_level)
	stats["valid_fraction"] = len(sample.values) / sample.cell_count if sample.cell_count else 0.0
	stats["nodata"] = sample.nodata_count
	return {"direction": direction, "corners": corners, "stats": stats}


def compute_approaches(slice_, dataset, min_level):
	stops = _load_json(slice_.name, "landings.geojson")["features"]
	faces = _load_json(slice_.name, "landing_faces.geojson")["features"]
	approaches = []
	for stop in stops:
		props = stop["properties"]
		matches = [f for f in faces if f["properties"]["stop_id"] == props["stop_id"]]
		if len(matches) != 1:
			raise ValueError(f"{props['stop_name']}: missing or duplicate landing face")
		p = matches[0]["properties"]
		face = landing_module.to_local(shape(p["face_geometry"]))
		mid = face.interpolate(.5, normalized=True)
		a, b = face.coords[0], face.coords[-1]
		tangent = ((b[0] - a[0]) / face.length, (b[1] - a[1]) / face.length)
		anchor = (mid.y, mid.x)
		boxes = [_box_stats(dataset, anchor, p["outward_normal_ne"], min_level)]
		approaches.append(Approach({**props, "face_source": p["source"], "face_confidence": p["confidence"]}, anchor, tangent, p["side_chosen_by"], boxes))
	return approaches


def _worst_box(boxes):
	valid = [b for b in boxes if b["stats"]["min_depth_m"] is not None]
	return min(valid, key=lambda b: b["stats"]["min_depth_m"]) if valid else None


def charted_range_at_centre(box, depth_areas):
	"""'DRVAL1-DRVAL2' of the shallowest ENC depth area containing the box centre (metres below MLLW), or None."""
	polygon = Polygon([(east, north) for north, east in box["corners"]])
	centre = polygon.centroid
	hits = [p["props"] for p in depth_areas if p["props"].get("min_depth_m") is not None and p["geometry"].contains(centre)]
	if not hits:
		return None
	best = min(hits, key=lambda p: p["min_depth_m"])
	deep = best.get("max_depth_m")
	return f"{best['min_depth_m']:g}-{deep:g}" if deep is not None else f">={best['min_depth_m']:g}"


def depth_verdict(stats, required):
	return stats["min_depth_m"] is not None and np.isfinite(stats["min_depth_m"]) and stats["valid_fraction"] >= MIN_VALID_FRACTION and stats["min_depth_m"] >= required


def check_depth(slice_):
	out = paths.processed_dir(slice_.name)
	required = k_class_max_draft_m() + UKC_MARGIN_M
	station, min_level, min_time = reference_water_level(slice_)
	shore = [local_geometry(f) for f in _load_json(slice_.name, "shoreline_constructions.geojson")["features"]]
	with rasterio.open(out / "depth_merged_navd88.tif") as merged, rasterio.open(out / "depth_navd88.tif") as cudem:
		if merged.shape != cudem.shape or merged.transform != cudem.transform or merged.crs != cudem.crs:
			raise ValueError("merged and CUDEM grids differ")
		approaches = compute_approaches(slice_, merged, min_level)
		baseline = compute_approaches(slice_, cudem, min_level)
		write_approach_figure(slice_, approaches, merged, min_level, out / "landing_approaches.png", shore)
	failures, cudem_failures, rows = [], [], []
	for item, original in zip(approaches, baseline):
		stats, cs = item.boxes[0]["stats"], original.boxes[0]["stats"]
		ok, c_ok = depth_verdict(stats, required), depth_verdict(cs, required)
		if not ok:
			failures.append(item.props["stop_name"])
		if not c_ok:
			cudem_failures.append(item.props["stop_name"])
		rows.append((item.props["stop_id"], item.props["stop_name"], item.props["face_source"], item.props["face_confidence"],
			_fmt(cs["min_depth_m"]), "pass" if c_ok else "FAIL", _fmt(stats["min_depth_m"]),
			_fmt(stats["p5_depth_m"]), f"{stats['valid_fraction']:.0%}", "pass" if ok else "FAIL"))
	details = _md_table(["stop_id", "landing", "face source", "confidence", "CUDEM min depth (m)", "CUDEM verdict", "merged min depth (m)", "merged p5 (m)", "valid cells", "merged verdict"], rows)
	details += ["", f"Minimum water level: {min_level:.3f} m NAVD88 at gauge {station} ({min_time} UTC). Required depth: K-class max draft {k_class_max_draft_m():.2f} m + {UKC_MARGIN_M:g} m = {required:.2f} m.",
		f"Each {APPROACH_BOX_M:g} m square starts at the berthing face and extends along its outward normal. The shallowest valid merged cell must meet the depth requirement with at least {MIN_VALID_FRACTION:.0%} coverage. CUDEM-only results use the identical face and box.",
		f"CUDEM-only: {len(cudem_failures)} of {len(approaches)} fail" + (": " + ", ".join(cudem_failures) if cudem_failures else ""),
		"The merge only deepens valid CUDEM cells within 60 m of structures/faces, outside ENC land, and no farther than conservative chart support. See landing_depth_diff.csv, depth_merged_navd88.json and depth_disagreement_gt1m.tif for datum provenance and differences. A chart/derived face is not operational berth verification.",
		"", "![approach boxes over merged elevation](landing_approaches.png)", "",
		"Figure: merged elevation (m NAVD88), lowest-water contour, ENC shoreline (magenta), GTFS stop (red star), face midpoint (white dot) and approach box (red)."]
	summary = (f"all {len(approaches)} approach boxes at least {required:.2f} m deep at {min_level:.2f} m NAVD88" if not failures
		else f"{len(failures)} of {len(approaches)} approach boxes fail: " + ", ".join(failures))
	summary += f"; CUDEM-only {len(cudem_failures)} of {len(approaches)} fail"
	return CheckResult(f"Landing approach depth >= {required:.2f} m at the window's lowest water level", not failures, summary, details)


def write_approach_figure(slice_, approaches, dataset, min_level, target, shore_geoms):
	import matplotlib

	matplotlib.use("Agg")
	import matplotlib.pyplot as plt

	array = dataset.read(1)
	columns = 4
	rows = -(-len(approaches) // columns)
	fig, axes = plt.subplots(rows, columns, figsize=(5 * columns, 4.6 * rows), squeeze=False)
	half = 90.0
	image = None
	for ax, item in zip(axes.ravel(), approaches):
		props = item.props
		north_axis = np.linspace(props["north_m"] - half, props["north_m"] + half, 181)
		east_axis = np.linspace(props["east_m"] - half, props["east_m"] + half, 181)
		east, north = np.meshgrid(east_axis, north_axis)
		lat, lon = geodesy.from_local(north, east)
		rows_idx, cols_idx = rasterio.transform.rowcol(dataset.transform, lon.ravel(), lat.ravel())
		grid = array[np.clip(rows_idx, 0, array.shape[0] - 1), np.clip(cols_idx, 0, array.shape[1] - 1)].reshape(east.shape)
		grid = np.where(grid == dataset.nodata, np.nan, grid)
		image = ax.pcolormesh(east, north, grid, cmap="terrain", vmin=-10, vmax=6, shading="auto")
		ax.contour(east, north, grid, levels=[min_level], colors="k", linewidths=0.8, linestyles="dashed")
		here = Point(props["east_m"], props["north_m"])
		for geometry in shore_geoms:
			if geometry.distance(here) < 1.5 * half:
				for part in getattr(geometry, "geoms", [geometry]):
					xs, ys = part.exterior.xy if part.geom_type == "Polygon" else part.xy
					ax.plot(xs, ys, color="magenta", lw=1.0)
		if item.route is not None:
			for part in ([item.route] if item.route.geom_type == "LineString" else item.route.geoms):
				if part.geom_type == "LineString":
					ax.plot(*part.xy, color="cyan", lw=1.4)
		ax.plot(props["east_m"], props["north_m"], "r*", ms=13)
		ax.plot(item.anchor_ne[1], item.anchor_ne[0], "wo", mec="k")
		for box in item.boxes:
			ys = [c[0] for c in box["corners"]] + [box["corners"][0][0]]
			xs = [c[1] for c in box["corners"]] + [box["corners"][0][1]]
			ax.plot(xs, ys, "r-", lw=2)
		ax.set_title(f"{props['stop_name']} [{item.how}]", fontsize=9)
		ax.set_xlim(props["east_m"] - half, props["east_m"] + half)
		ax.set_ylim(props["north_m"] - half, props["north_m"] + half)
		ax.set_aspect("equal")
		ax.tick_params(labelsize=6)
	for ax in axes.ravel()[len(approaches):]:
		ax.axis("off")
	if image is not None:
		fig.colorbar(image, ax=axes, shrink=0.5, label="elevation, m NAVD88")
	fig.savefig(target, dpi=70)
	plt.close(fig)


def check_aids(fc):
	features = fc["features"]
	lights = [l for f in features for l in f["properties"].get("lights", [])]
	good = [l for l in lights if l.get("parsed") and l.get("colours") and l.get("period_s") is not None]
	passed = len(features) > 0 and len(good) > 0
	summary = f"{len(features)} aids, {len(lights)} lights, {len(good)} with a parsed characteristic, colour and period"
	details = [f"- example: `{good[0]['notation']}`"] if good else []
	return CheckResult("Aids to navigation present with parsed light characteristics", passed, summary, details)


def check_bridges(fc):
	rows = []
	missing = []
	for key in REQUIRED_BRIDGES:
		matches = [f["properties"] for f in fc["features"] if f["properties"].get("name") and key.lower() in f["properties"]["name"].lower()]
		with_clearance = [p for p in matches if p.get("vertical_clearance_m") is not None and p.get("horizontal_clearance_m") is not None]
		if not with_clearance:
			missing.append(key)
			rows.append((key, len(matches), "n/a", "n/a", "n/a"))
		else:
			best = with_clearance[0]
			rows.append((key, len(matches), best["vertical_clearance_m"], best["horizontal_clearance_m"], best.get("clearance_datum")))
	details = _md_table(["bridge", "features", "vertical clearance (m)", "horizontal clearance (m)", "datum"], rows)
	summary = "Brooklyn, Manhattan and Williamsburg present with clearances" if not missing else "missing or without clearances: " + ", ".join(missing)
	return CheckResult("Brooklyn, Manhattan and Williamsburg bridges present with clearances", not missing, summary, details)


def check_currents(slice_):
	out = paths.processed_dir(slice_.name)
	stations = _load_json(slice_.name, "coops_stations.json")
	start = pd.Timestamp(slice_.window_start, tz="UTC")
	end = pd.Timestamp(slice_.window_end, tz="UTC") + pd.Timedelta(days=1)
	rows = []
	failures = []
	for station in stations["stations"]:
		if station["kind"] != "current_prediction":
			continue
		if station["station_type"] != "harmonic":
			rows.append((station["id"], station["name"], "subordinate (max/slack events only)", "n/a", "n/a", "not a continuous series; excluded"))
			continue
		frame = pd.read_csv(out / station["file"])
		gap = coverage_gap_hours(frame["time_utc"], start, end)
		ok = gap <= MAX_SERIES_GAP_H
		if not ok:
			failures.append(station["name"])
		rows.append((station["id"], station["name"], "6-minute series", len(frame), f"{gap:.2f}", "pass" if ok else "FAIL"))
	series = [r for r in rows if r[2] == "6-minute series"]
	if not series:
		failures.append("no continuous current series")
	details = _md_table(["station", "name", "kind", "rows", "largest gap (h)", "verdict"], rows)
	details += ["", f"Window: {start:%Y-%m-%dT%H:%M}Z to {end:%Y-%m-%dT%H:%M}Z. Gap includes the distance from each window edge to the nearest sample."]
	summary = f"{len(series)} continuous series cover the window with no gap over {MAX_SERIES_GAP_H:g} h" if not failures else "failing: " + ", ".join(failures)
	return CheckResult(f"Current series cover the window with no gaps over {MAX_SERIES_GAP_H:g} h", not failures, summary, details)


def check_geoid(payload):
	heights = [p["navd88_zero_ellipsoidal_height_m"] for p in payload.get("points", {}).values()]
	low, high = GEOID_RANGE_M
	passed = bool(heights) and all(low <= h <= high for h in heights)
	summary = f"NAVD88 zero is {min(heights):.3f} to {max(heights):.3f} m above the ellipsoid (required {low:g} to {high:g})" if heights else "no geoid points"
	return CheckResult("Geoid offset within -35 to -29 m", passed, summary)


def check_geodesy():
	vectors = csharp_reference_vectors()
	worst = 0.0
	for lat, lon, north, east, down in vectors:
		n, e, d = geodesy.to_ned(lat, lon)
		worst = max(worst, abs(float(n) - north), abs(float(e) - east), abs(float(d) - down))
	passed = len(vectors) >= 4 and worst <= GEODESY_TOLERANCE_M
	return CheckResult("geodesy.py matches the C# reference vectors", passed, f"{len(vectors)} vectors from LocalTangentPlaneTests.cs, worst difference {worst:.2e} m (tolerance {GEODESY_TOLERANCE_M:g} m)")


# ---- driver -------------------------------------------------------------------------------------------------


def _guard(name, function):
	try:
		return function()
	except Exception as error:  # a missing or malformed output is a failed check, not a crash
		return CheckResult(name, False, f"could not run: {type(error).__name__}: {error}")


def write_report(slice_, results):
	passed = all(r.passed for r in results)
	lines = [
		f"# Validation report: {slice_.name}",
		"",
		f"Generated {datetime.now(timezone.utc):%Y-%m-%dT%H:%M:%SZ} by `ferrydata validate --slice {slice_.name}`.",
		f"Window: {slice_.window_start} to {slice_.window_end} (UTC). Overall: **{'PASS' if passed else 'FAIL'}**.",
		"",
		"## Summary",
		"",
	]
	lines += _md_table(["check", "result", "detail"], [(r.name, "PASS" if r.passed else "FAIL", r.summary) for r in results])
	for r in results:
		lines += ["", f"## {r.name}", "", f"**{'PASS' if r.passed else 'FAIL'}**: {r.summary}", ""]
		lines += r.details
	text = "\n".join(lines).rstrip() + "\n"
	target = paths.processed_dir(slice_.name) / "validation_report.md"
	target.parent.mkdir(parents=True, exist_ok=True)
	target.write_text(text, encoding="utf-8")
	return target, passed


def run(slice_, log=print):
	name = slice_.name
	results = [
		_guard("Required outputs exist", lambda: check_outputs(slice_)),
		_guard("GTFS landing faces", lambda: check_shoreline(slice_)),
		_guard("Landing approach depth", lambda: check_depth(slice_)),
		_guard("Aids", lambda: check_aids(_load_json(name, "aids.geojson"))),
		_guard("Bridges", lambda: check_bridges(_load_json(name, "bridges.geojson"))),
		_guard("Currents", lambda: check_currents(slice_)),
		_guard("Geoid", lambda: check_geoid(_load_json(name, "geoid_offset.json"))),
		_guard("Geodesy", check_geodesy),
	]
	target, passed = write_report(slice_, results)
	for r in results:
		log(f"{'PASS' if r.passed else 'FAIL'}  {r.name}: {r.summary}")
	log(f"report: {target}")
	return 0 if passed else 1
