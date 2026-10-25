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
from shapely.ops import nearest_points

from ferrydata import crew_notes, depth, geodesy, landings as landing_module, paths

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
	notes, notes_sha256 = crew_notes.load()
	construction_file = out / 'shoreline_constructions.geojson'
	constructions = [(landing_module.to_local(shape(f['geometry'])), f['properties']) for f in _load_json(slice_.name, 'shoreline_constructions.geojson')['features']] if construction_file.exists() else None
	rows = []
	for stop in stops:
		p = stop["properties"]
		matches = [f for f in faces if f["properties"]["stop_id"] == p["stop_id"]]
		row = {"stop_id": p["stop_id"], "stop_name": p["stop_name"], "distance_m": None, "water_side": False, "source": "missing/duplicate", "confidence": "unknown"}
		if len(matches) == 1:
			props = matches[0]["properties"]
			face = landing_module.to_local(shape(props["face_geometry"]))
			point = Point(p["east_m"], p["north_m"])
			blockers = landing_module.surface_structures(constructions, props.get('enc_id')) if constructions is not None else []
			proof = landing_module.water_side_evidence(face, props['outward_normal_ne'], props['bulkhead_anchor_en_m'], water, land, blockers,
				landing_module.to_local(shape(matches[0]['geometry'])) if props['source'] == 'enc-feature' and constructions is not None else None)
			note = notes.get(p['stop_id'])
			if note and note.get('water_side_crew_reported') and props.get('crew_notes_sha256') == notes_sha256:
				barge = landing_module.to_local(shape(matches[0]['geometry']))
				if note.get('face_outward_compass'):
					vector = landing_module.crew_compass_direction(note)
				else:
					reference = next(f['properties'] for f in stops if f['properties']['stop_id'] == note['face_toward_fixed_land_near_stop_id'])
					reference_point = Point(reference['east_m'], reference['north_m'])
					areas = [landing_module.to_local(shape(f['geometry'])) for f in _load_json(slice_.name, 'land_areas.geojson')['features']]
					manhattan = min(areas, key=lambda g: g.distance(reference_point))
					_, shore = nearest_points(barge.centroid, manhattan.boundary)
					vector = np.array([shore.y - barge.centroid.y, shore.x - barge.centroid.x]); vector /= np.linalg.norm(vector)
				expected, normal = landing_module.berthing_face(barge, vector)
				if expected.hausdorff_distance(face) < .03 and np.dot(normal, props['outward_normal_ne']) > .999:
					proof = {**proof, 'chart_verified': proof['verified'], 'chart_method': proof['method'], 'verified': True, 'method': 'crew-reported'}
			row.update(distance_m=face.distance(point), source=props["source"], confidence=props["confidence"], water_side=proof['verified'], proof=proof)
		rows.append(row)
	result = shoreline_result(rows)
	result.details += ["", "The 12 m probe and coverage 0.5 m outside the face remain the primary test, with the full face swept between 0.5 and 12 m checked against LNDARE and surface/unknown construction blockers. A separate topology fallback is restricted to charted floating outlines at least 8 m long whose 12 m point misses DEPARE: an 8 m wide corridor extends 30 m outward, intersects no LNDARE or surface/unknown construction, and has its whole far end in charted water. WATLEV=3 (always submerged) structures are disclosed separately as depth hazards and do not block the horizontal corridor. This proves the water side, not depth or operational berth. Faces shorter than 8 m cannot use this fixed-width fallback, but may pass the direct test. Fallback faces remain low confidence unless independent chart support and a face-orientation crew note justify medium; berth-use-policy notes never change geometry confidence; unresolved details retain needs-crew-verification.", ""]
	result.details += _md_table(['landing', 'water-side method', 'chart check', 'near-face DEPARE gap along corridor (m)'],
		[(r['stop_name'], r.get('proof', {}).get('method', 'missing'), 'PASS' if r.get('proof', {}).get('chart_verified', r['water_side']) else 'FAIL', _fmt(r.get('proof', {}).get('near_face_coverage_gap_m'), 1)) for r in rows])
	for r in rows:
		if r['stop_id'] in notes:
			proof = r.get('proof', {})
			result.details += ['', f"### Crew evidence for {r['stop_name']}", ''] + crew_evidence_details(notes[r['stop_id']],
				{'verified': proof.get('chart_verified', r['water_side']), 'method': proof.get('chart_method', proof.get('method'))})
	return result


def crew_evidence_details(note, chart):
	lines = [f"{note['date']} — {note['source']}: {note['statement']}",
		f"Crew provenance: {note.get('id', 'legacy note')} ({note.get('kind', 'unspecified')}); separate from ordinal geometry confidence.",
		f"Independent chart: {'PASS' if chart['verified'] else 'FAIL'} ({chart['method']}). Crew evidence does not change chart depths or clear a charted obstruction."]
	for key in ('limitations', 'modelling_decision', 'follow_up'):
		if note.get(key):
			lines.append(note[key])
	return lines


def filtered_contact_topology(line, records):
	"""Runtime's chart OR crew inclusion gate; preserve separate runs across gaps."""
	retained = [r['index'] for r in records if r.get('water_side_verified_by_pipeline') or r.get('water_side_crew_reported')]
	runs = []
	coords = list(line.coords)
	for index in retained:
		if runs and index == runs[-1][-1] + 1:
			runs[-1].append(index)
		else:
			runs.append([index])
	return {'retained': retained, 'dropped': [i for i in range(len(coords) - 1) if i not in retained], 'runs': runs,
		'primary_retained': any(r['on_primary_face'] for r in records) and all(r['index'] in retained for r in records if r['on_primary_face'])}


def check_contact_polylines(slice_):
	rows, errors, filtered_rows = [], [], []
	out = paths.processed_dir(slice_.name)
	for f in _load_json(slice_.name, 'landing_faces.geojson')['features']:
		p = f['properties']
		if 'contact_geometry' not in p:
			problems = ['missing connected contact_geometry']
		else:
			line = landing_module.to_local(shape(p['contact_geometry']))
			problems = landing_module.polyline_errors(line, landing_module.to_local(shape(f['geometry'])))
			if 'contact_segment_evidence' in p:
				water, land = landing_module.chart_masks(out)
				shore = [(landing_module.to_local(shape(s['geometry'])), s['properties']) for s in _load_json(slice_.name, 'shoreline_constructions.geojson')['features']]
				barge = landing_module.to_local(shape(f['geometry'])) if p['source'] == 'enc-feature' else None
				face = landing_module.to_local(shape(p['face_geometry']))
				blockers = landing_module.surface_structures(shore, p.get('enc_id'))
				primary_chart = landing_module.water_side_evidence(face, p['outward_normal_ne'], p['bulkhead_anchor_en_m'], water, land, blockers, barge)
				recomputed = landing_module.contact_segment_evidence(line, face,
					primary_chart, p['bulkhead_anchor_en_m'], water, land, blockers,
					barge, p.get('water_side_crew_reported', False))
				stored = p['contact_segment_evidence']
				if len(stored) != len(recomputed) or any(
					any(a.get(k, False) != b.get(k, False) for k in ('index', 'on_primary_face', 'water_side_verified_by_pipeline', 'water_side_crew_reported'))
					for a, b in zip(stored, recomputed)):
					problems.append('stored segment eligibility differs from independent per-side evidence')
				topology = filtered_contact_topology(line, recomputed)
				if not topology['primary_retained']:
					problems.append('runtime filter loses a primary face subdivision')
				for run in topology['runs']:
					problems.extend(landing_module.polyline_errors(LineString(list(line.coords)[run[0]:run[-1] + 2]), landing_module.to_local(shape(f['geometry']))))
				filtered_rows.append((p['stop_name'], ', '.join(map(str, topology['retained'])), ', '.join(map(str, topology['dropped'])) or 'none',
					str(topology['runs']), 'yes' if topology['primary_retained'] else 'NO'))
		rows.append((p['stop_name'], len(p.get('contact_geometry', {}).get('coordinates', [])) - 1, '; '.join(problems) or 'pass'))
		errors.extend(f"{p['stop_name']}: {problem}" for problem in problems)
	return CheckResult('Landing contact polylines connected and solid-right', bool(rows) and not errors,
		'; '.join(errors) if errors else f'all {len(rows)} open polylines preserve shared vertices and water-left orientation',
		_md_table(['landing', 'segments', 'result'], rows) + ['', 'Runtime eligibility is chart verification OR crew water-side support. Indices are original polyline indices; gaps remain separate open runs, never bridged.', ''] +
		_md_table(['landing', 'retained indices', 'dropped indices', 'open runs after filter', 'all primary subdivisions retained'], filtered_rows))


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
	from ferrydata import landing_audit
	details += ['', '## Per-landing source and depth diagnosis', ''] + landing_audit.write(slice_, approaches, baseline, min_level, required)
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
		_guard("Landing contact polylines", lambda: check_contact_polylines(slice_)),
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


def harbor_water_coverage(dataset, polygons, footprint=None):
	"""Count missing cells in charted water; no interpolation or nodata waiver."""
	from rasterio import features
	array = dataset.read(1)
	inside = features.geometry_mask(polygons, out_shape=array.shape, transform=dataset.transform,
		invert=True, all_touched=False) if polygons else np.zeros(array.shape, dtype=bool)
	if footprint is not None:
		# GeoJSON's seven-decimal coordinate rounding can extend a clipped
		# DEPARE a few mm past the true output square. Those outside cells
		# are deliberately masked; they are not water coverage gaps.
		inside &= features.geometry_mask([footprint], out_shape=array.shape, transform=dataset.transform, invert=True)
	valid = np.isfinite(array)
	if dataset.nodata is not None:
		valid &= array != dataset.nodata
	return {"charted_cells": int(inside.sum()), "nodata_cells": int((inside & ~valid).sum())}


def harbor_chart_depth_audit(dataset, area_features, mllw_navd88_offset_m):
	"""Report-only DEPARE range outliers, comparing depth in the chart's MLLW datum.

	Count each cell once per category. On overlapping areas, test every finite
	bound: the shallowest DRVAL2 and deepest DRVAL1 give the union of outliers.
	This also exposes contradictory overlapping chart ranges for later review.
	"""
	from rasterio import features
	array = dataset.read(1)
	valid = np.isfinite(array) & (array != dataset.nodata) if dataset.nodata is not None else np.isfinite(array)
	charted = [f for f in area_features if f['properties'].get('feature_class') == 'DEPARE']
	bounds = {}
	for name, prop, reverse in (('upper', 'max_depth_m', True), ('lower', 'min_depth_m', False)):
		shapes = [(f['geometry'], float(f['properties'][prop])) for f in charted
			if f['properties'].get(prop) is not None and np.isfinite(f['properties'][prop])]
		bounds[name] = features.rasterize(sorted(shapes, key=lambda p: p[1], reverse=reverse),
			out_shape=array.shape, transform=dataset.transform, fill=np.nan, dtype='float64') if shapes else np.full(array.shape, np.nan)
	depths = mllw_navd88_offset_m - array.astype(float)
	result = {'compared_cells': int((valid & (np.isfinite(bounds['upper']) | np.isfinite(bounds['lower']))).sum()),
		'overlap_policy': 'Unique cells disagreeing with at least one finite DEPARE bound; shallowest DRVAL2 and deepest DRVAL1 on overlaps.',
		'deep_threshold_m': 5., 'shallow_threshold_m': 2., 'report_only': True}
	for name, excess, bound, threshold in (
		('too_deep', depths - bounds['upper'], bounds['upper'], 5.),
		('too_shallow', bounds['lower'] - depths, bounds['lower'], 2.)):
		flagged = valid & np.isfinite(bound) & (excess > threshold)
		count = int(flagged.sum())
		worst = None
		if count:
			row, col = np.unravel_index(np.argmax(np.where(flagged, excess, -np.inf)), array.shape)
			lon, lat = dataset.xy(int(row), int(col))
			north, east = geodesy.to_local(lat, lon)
			worst = {'excess_m': float(excess[row, col]), 'merged_depth_mllw_m': float(depths[row, col]),
				'bed_elevation_navd88_m': float(array[row, col]), 'chart_bound_mllw_m': float(bound[row, col]),
				'lon': float(lon), 'lat': float(lat), 'north_m': float(north), 'east_m': float(east)}
		result[name] = {'cells': count, 'worst': worst}
	return result


def harbor_overlap(path_a, path_b, polygon):
	"""Compare coincident cell centres in the shared grown-square footprint."""
	from rasterio import features, windows
	from ferrydata import tiles
	with rasterio.open(path_a) as a, rasterio.open(path_b) as b:
		if a.crs != b.crs or not np.allclose(a.res, b.res, rtol=0, atol=1e-12):
			raise ValueError("overlap rasters have different CRS or resolution")
		if a.transform.b != 0 or a.transform.d != 0 or b.transform.b != 0 or b.transform.d != 0:
			raise ValueError("rotated overlap grids are unsupported")
		dx = (b.transform.c - a.transform.c) / a.transform.a
		dy = (b.transform.f - a.transform.f) / a.transform.e
		if abs(dx - round(dx)) > 1e-6 or abs(dy - round(dy)) > 1e-6:
			raise ValueError("overlap raster pixel centres do not align")
		w, s, e, n = polygon.bounds
		w = max(w, a.bounds.left, b.bounds.left); e = min(e, a.bounds.right, b.bounds.right)
		s = max(s, a.bounds.bottom, b.bounds.bottom); n = min(n, a.bounds.top, b.bounds.top)
		if w >= e or s >= n:
			raise ValueError("neighbour rasters have no shared cells")
		win = windows.from_bounds(w, s, e, n, a.transform).round_offsets().round_lengths()
		win = win.intersection(windows.Window(0, 0, a.width, a.height))
		other = windows.Window(win.col_off - round(dx), win.row_off - round(dy), win.width, win.height)
		other = other.intersection(windows.Window(0, 0, b.width, b.height))
		win = windows.Window(other.col_off + round(dx), other.row_off + round(dy), other.width, other.height)
		aa, bb = a.read(1, window=win), b.read(1, window=other)
		mask = features.geometry_mask([polygon], out_shape=aa.shape, transform=a.window_transform(win), invert=True)
		if not mask.any():
			raise ValueError("neighbour overlap contains no raster cell centres")
		return tiles.overlap_result(aa, bb, a.nodata, b.nodata, mask)


def harbor_bridge_clearances(bridges_by_tile, routes_by_tile, ferry_route_ids):
	"""Merge LNAM pieces, group within 2 local metres, then gate each ferry crossing.

	Unknown spans are always audited. A clearance is usable only on a span
	intersected by the same ferry route, not merely elsewhere on its bridge.
	"""
	from shapely.ops import unary_union
	pieces = {}
	for tile_id, features in sorted(bridges_by_tile.items()):
		for feature in features:
			props = feature['properties']
			identifier = props['id']
			item = pieces.setdefault(identifier, {'geometries': [], 'names': set(), 'category_codes': set(),
				'tiles': set(), 'clearances': set()})
			item['geometries'].append(local_geometry(feature))
			item['tiles'].add(tile_id)
			if props.get('name'):
				item['names'].add(props['name'])
			item['category_codes'].update(props.get('category_codes') or [])
			value = props.get('vertical_clearance_m')
			if value is not None and np.isfinite(value):
				item['clearances'].add(float(value))
	identifiers = sorted(pieces)
	geometries = {key: unary_union(pieces[key]['geometries']) for key in identifiers}
	parents = {key: key for key in identifiers}
	def root(key):
		while parents[key] != key:
			parents[key] = parents[parents[key]]
			key = parents[key]
		return key
	for pos, key in enumerate(identifiers):
		for other in identifiers[pos + 1:]:
			if geometries[key].distance(geometries[other]) <= 2.:
				parents[root(other)] = root(key)
	groups = {}
	for key in identifiers:
		groups.setdefault(root(key), []).append(key)
	# Merge route pieces too, retaining only GTFS ferry route ids. One shape
	# may serve multiple routes; each must have its own crossed-span evidence.
	route_pieces = {}
	for _, features in sorted(routes_by_tile.items()):
		for feature in features:
			for route_id in sorted(set(feature['properties']['route_ids']) & set(ferry_route_ids)):
				route_pieces.setdefault(route_id, []).append(local_geometry(feature))
	routes = {key: unary_union(value) for key, value in sorted(route_pieces.items())}
	crossed = {key: [route_id for route_id, geometry in routes.items() if geometry.intersects(geometries[key])]
		for key in identifiers}
	audit = {'report_only': True, 'group_distance_m': 2., 'feature_count': len(identifiers),
		'missing_clearance_features': 0, 'physical_bridges_with_missing_clearance': 0, 'physical_bridges': []}
	failures = []
	crossed_groups = 0
	for ids in sorted(groups.values()):
		names = sorted({name for key in ids for name in pieces[key]['names']})
		group = {'ids': ids, 'names': names,
			'category_codes': sorted({code for key in ids for code in pieces[key]['category_codes']}),
			'tiles': sorted({tile for key in ids for tile in pieces[key]['tiles']}),
			'missing_clearance_spans': [], 'route_crossings': []}
		for key in ids:
			if not pieces[key]['clearances']:
				group['missing_clearance_spans'].append({'id': key, 'names': sorted(pieces[key]['names']),
					'category_codes': sorted(pieces[key]['category_codes']), 'tiles': sorted(pieces[key]['tiles']),
					'ferry_route_ids': crossed[key], 'crossed_by_ferry_route': bool(crossed[key])})
		for route_id in sorted({route_id for key in ids for route_id in crossed[key]}):
			span_ids = [key for key in ids if route_id in crossed[key]]
			charted = [{'id': key, 'vertical_clearance_m': min(pieces[key]['clearances'])}
				for key in span_ids if pieces[key]['clearances']]
			group['route_crossings'].append({'route_id': route_id, 'crossed_span_ids': span_ids,
				'charted_crossed_spans': charted})
			if not charted:
				failures.append(f"bridge names={names}, ids={ids}; route={route_id}, crossed spans={span_ids}: no vertical clearance on a crossed span")
		crossed_groups += bool(group['route_crossings'])
		audit['missing_clearance_features'] += len(group['missing_clearance_spans'])
		audit['physical_bridges_with_missing_clearance'] += bool(group['missing_clearance_spans'])
		audit['physical_bridges'].append(group)
	summary = f"{crossed_groups} ferry-route physical bridges / {len(groups)} physical bridges ({len(identifiers)} ENC features); {len(failures)} uncharted route crossings"
	if failures:
		summary += '; ' + '; '.join(failures)
	return CheckResult('Ferry-route bridge vertical clearances', not failures, summary), audit


def _harbor_tile_checks(tile, entry):
	from ferrydata import enc, manifest, tiles
	out = paths.processed_dir(tile.name)
	checks = []
	def check(name, passed, detail):
		checks.append(CheckResult(f"{tile.id}: {name}", bool(passed), str(detail)))
	required = [name for name in REQUIRED_OUTPUTS if name not in
		('landing_faces.geojson', 'landing_depth_diff.csv')] + ['raw_sources.json', 'depth_chart_navd88.tif']
	missing = sorted({name for name in required if not (out / name).is_file()})
	check('outputs', not missing and not entry.get('build_error'), entry.get('build_error') or f"missing: {missing}")
	if missing:
		return checks
	outputs = entry.get('outputs', [])
	hashes_bad = [p['file'] for p in outputs if not (paths.processed_dir('harbor') / p['file']).is_file()
		or manifest.sha256_file(paths.processed_dir('harbor') / p['file']) != p['sha256']]
	listed = {p['file'].split('/')[-1] for p in outputs}
	check('output hashes', not hashes_bad and set(required).issubset(listed), f"changed: {hashes_bad}; required files indexed: {set(required).issubset(listed)}")
	stations = tiles._json(out / 'coops_stations.json')
	expected_raw = tiles._raw_sources(tile, stations)
	raw = tiles._json(out / 'raw_sources.json')['files']
	bad = [f"{r['source']}/{r['name']}" for r in raw if not manifest.load_combined(r['source']).verify(r['name'], paths.raw_dir(r['source']) / r['name'])
		or manifest.load_combined(r['source']).files[r['name']]['sha256'] != r['sha256']]
	check('raw provenance', not bad and raw == expected_raw, f"unverified raw files: {bad}; complete dependency list: {raw == expected_raw}")
	landings = tiles._json(out / 'landings.geojson')['features']
	ids = [f['properties']['stop_id'] for f in landings]
	check('landing ownership', sorted(ids) == sorted(entry['stop_ids']) and len(ids) == len(set(ids)), f"expected {entry['stop_ids']}; actual {ids}")
	fc = tiles._json(out / 'depth_areas.geojson')
	polygons = [f['geometry'] for f in fc['features'] if f['properties']['feature_class'] == 'DEPARE']
	with rasterio.open(out / 'depth_merged_navd88.tif') as raster:
		coverage = harbor_water_coverage(raster, polygons, tile.polygon)
		merged_meta = tiles._json(out / 'depth_merged_navd88.json')
		audit = harbor_chart_depth_audit(raster, fc['features'], merged_meta['mllw_zero_navd88_m'])
	check('charted-water depth', coverage['nodata_cells'] == 0, f"{coverage['nodata_cells']} nodata cells / {coverage['charted_cells']} charted cells")
	check('depth/chart audit (report only)', True, json.dumps(audit))
	bridges = tiles._json(out / 'bridges.geojson')['features']
	no_clearance = [f['properties']['id'] for f in bridges if f['properties'].get('vertical_clearance_m') is None
		or not np.isfinite(f['properties']['vertical_clearance_m'])]
	check('bridge clearance audit (report only)', True, f"{len(bridges)} bridge pieces; {len(no_clearance)} without vertical clearance; ids={no_clearance}; see bridge_clearance_audit.json for physical groups")
	layers, datum = enc.read_layers(tile, enc.AID_LAYERS + ('BRIDGE', 'DEPARE'))
	expected_aids = enc._features(enc.aid_records({k: v for k, v in layers.items() if k in enc.AID_LAYERS}), tile)
	aids = tiles._json(out / 'aids.geojson')['features']
	check('ENC aids', aids == expected_aids, f"expected {len(expected_aids)} aids; actual {len(aids)}")
	vertical = sorted({d['vertical'] for d in datum.values()})
	expected_bridges = enc._features(enc.bridge_records(layers['BRIDGE'], vertical[0]) if 'BRIDGE' in layers else [], tile)
	check('ENC bridges', bridges == expected_bridges, f"expected {len(expected_bridges)} bridges; actual {len(bridges)}")
	expected_water = enc._features(enc.depth_area_records(layers['DEPARE']) if 'DEPARE' in layers else [], tile)
	actual_water = [f for f in fc['features'] if f['properties']['feature_class'] == 'DEPARE']
	check('ENC charted water', actual_water == expected_water, f"expected {len(expected_water)} DEPARE; actual {len(actual_water)}")
	check('CO-OPS stations', stations == tiles._stations(tile), f"{len(stations['stations'])} current/water-level stations checked against cached NOAA listings")
	geoid_payload = tiles._json(out / 'geoid_offset.json')
	geoid_result = check_geoid(geoid_payload)
	check('geoid offset', geoid_result.passed and set(geoid_payload['points']) == {'centre', 'sw', 'se', 'nw', 'ne'}, geoid_result.summary)
	return checks


def run_harbor(log=print):
	"""Validate every candidate/active tile; fail visibly on missing source or output."""
	from ferrydata import tiles
	out = paths.processed_dir('harbor')
	out.mkdir(parents=True, exist_ok=True)
	results = []
	index = None
	bridge_audit = {'report_only': True, 'physical_bridges': [], 'incomplete': True}
	try:
		index = tiles._json(out / 'index.json')
		expected = tiles.inventory()
		keys = ('id', 'i', 'j', 'bounds_local_m', 'grown_bounds_local_m', 'grown_corners_latlon', 'read_bbox_lonlat', 'active', 'reasons', 'stop_ids')
		normal = lambda value: json.loads(json.dumps(value))
		actual_grid = [{k: e[k] for k in keys} for e in index['tiles']]
		expected_grid = normal([{k: e[k] for k in keys} for e in expected['tiles']])
		results.append(CheckResult('Grid and activity', actual_grid == expected_grid and len(actual_grid) == 36,
			f"{sum(e['active'] for e in expected['tiles'])} active / 36 candidates; activity recomputed from ENC DEPARE and ferry landings"))
		results.append(CheckResult('Index source inventory', index.get('stops') == expected['stops']
			and index.get('excluded_stops') == expected['excluded_stops'] and index.get('enc_cells') == expected['enc_cells']
			and index.get('schema') == expected['schema'] and index.get('origin_latlon') == expected['origin_latlon']
			and index.get('bounds_order') == expected['bounds_order']
			and index.get('tile_size_m') == tiles.SIZE_M and index.get('margin_m') == tiles.MARGIN_M,
			'GTFS stop details, exclusions, source cells, origin, tile size and margin checked against cached sources'))
		owners = [sid for e in index['tiles'] if e['active'] for sid in e['stop_ids']]
		stops = expected['stops']
		outside = [s['stop_id'] for s in stops if s['tile'] is None]
		passed = len(stops) == 24 and not outside and len(owners) == len(set(owners)) == 24 and set(owners) == {s['stop_id'] for s in stops}
		results.append(CheckResult('24 non-Rockaway ferry stops', passed, f"{len(stops)} ferry stops, {len(owners)} owners; outside extent: {outside}"))
		active = [e for e in index['tiles'] if e['active']]
		for entry in active:
			tile = tiles.get_tile(entry['id'], expected['enc_cells'])
			try:
				results.extend(_harbor_tile_checks(tile, entry))
			except (Exception, SystemExit) as error:
				results.append(CheckResult(f"{tile.id}: validation", False, f"{type(error).__name__}: {error}"))
		try:
			feed = tiles._feed()
			ferry_route_ids = set(feed['routes'].loc[feed['routes'].route_type == '4', 'route_id'])
			bridge_features = {}
			route_features = {}
			for entry in active:
				folder = out / 'tiles' / entry['id']
				bridge_features[entry['id']] = tiles._json(folder / 'bridges.geojson')['features']
				route_features[entry['id']] = tiles._json(folder / 'routes.geojson')['features']
			bridge_check, bridge_audit = harbor_bridge_clearances(bridge_features, route_features, ferry_route_ids)
			results.append(bridge_check)
		except (Exception, SystemExit) as error:
			results.append(CheckResult('Ferry-route bridge vertical clearances', False, f"{type(error).__name__}: {error}"))
		for a in active:
			for b in active:
				if (b['i'], b['j']) <= (a['i'], a['j']) or max(abs(a['i'] - b['i']), abs(a['j'] - b['j'])) != 1:
					continue
				ta, tb = tiles.get_tile(a['id']), tiles.get_tile(b['id'])
				try:
					comparison = harbor_overlap(paths.processed_dir(ta.name) / 'depth_merged_navd88.tif',
						paths.processed_dir(tb.name) / 'depth_merged_navd88.tif', ta.polygon.intersection(tb.polygon))
					results.append(CheckResult(f"overlap {ta.id} / {tb.id}", comparison['passed'], json.dumps(comparison)))
				except (Exception, SystemExit) as error:
					results.append(CheckResult(f"overlap {ta.id} / {tb.id}", False, f"{type(error).__name__}: {error}"))
	except (Exception, SystemExit) as error:
		results.append(CheckResult('Harbor inputs and index', False, f"{type(error).__name__}: {error}"))
	def report_only(result):
		return result.name.endswith((': depth/chart audit (report only)', ': bridge clearance audit (report only)'))
	def status(result):
		return 'INFO' if report_only(result) else ('PASS' if result.passed else 'FAIL')
	audits = {r.name.split(':', 1)[0]: json.loads(r.summary) for r in results if r.name.endswith(': depth/chart audit (report only)')}
	tiles._write(out / 'depth_chart_audit.json', {'report_only': True, 'tiles': audits})
	tiles._write(out / 'bridge_clearance_audit.json', bridge_audit)
	lines = ['# Harbor tiles v1 validation', '', 'Thresholds are unchanged. Missing data and unbuilt active tiles fail validation. INFO depth/chart and bridge clearance audit rows are findings for wave 2 and do not affect the verdict.', '',
		*_md_table(['check', 'result', 'detail'], [(r.name, status(r), r.summary.replace('\n', ' ')) for r in results]), '']
	(out / 'validation.md').write_text('\n'.join(lines), encoding='utf-8')
	# Include root validation outputs in the index without modifying any tile's
	# data or hashes. The report-only audit is carried by each tile's row above.
	if isinstance(index, dict):
		from ferrydata import manifest
		index['outputs'] = [{'file': name, 'sha256': manifest.sha256_file(out / name),
			'size_bytes': (out / name).stat().st_size} for name in ('validation.md', 'depth_chart_audit.json', 'bridge_clearance_audit.json')]
		tiles._write(out / 'index.json', index)
	for result in results:
		log(f"{status(result)} {result.name}: {result.summary}")
	gates = [r for r in results if not report_only(r)]
	info_count = sum(report_only(r) for r in results)
	log(f"INFO bridge clearance audit: {bridge_audit.get('missing_clearance_features', 'unknown')} features without vertical clearance in {bridge_audit.get('physical_bridges_with_missing_clearance', 'unknown')} physical bridges")
	log(f"{sum(r.passed for r in gates)}/{len(gates)} checks passed; {info_count} report-only audits; {out / 'validation.md'}")
	return 0 if gates and all(r.passed for r in gates) else 1
