"""Faithful, deterministic harbor AIS packs. Replay/interpolation belongs to the core."""

import hashlib
import json
from datetime import datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd
import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.csv as pacsv

from ferrydata import ais, geodesy, paths
from ferrydata.manifest import Manifest, sha256_file
from ferrydata.operational_depth import identity_classes
from ferrydata.slices import AIS_HARBOR_BBOX

FORMAT = "ferrysim-traffic/1"
KNOT = 1852. / 3600.
FILES = ("vessels.csv", "reports.bin", "reports.json")
FIELDS = [
	("vessel_index", "<i4", "int32", 0), ("segment", "<i4", "int32", 4),
	("t_s", "<f8", "float64", 8), ("north_m", "<f8", "float64", 16),
	("east_m", "<f8", "float64", 24), ("sog_mps", "<f4", "float32", 32),
	("cog_rad", "<f4", "float32", 36), ("heading_rad", "<f4", "float32", 40),
]
RECORD_DTYPE = np.dtype([(name, dtype) for name, dtype, _, _ in FIELDS])
LAYOUT = {"byte_order": "little-endian", "record_size_bytes": 44,
	"fields": [{"name": name, "type": type_, "offset_bytes": offset} for name, _, type_, offset in FIELDS],
	"missing_float": "IEEE 754 quiet NaN", "angles": "radians clockwise from true north",
	"positions": "NED horizontal metres via ferrydata.geodesy.to_local at 40.7000 N, -74.0150 E"}
COLUMNS = ["mmsi", "base_date_time", "latitude", "longitude", "sog", "cog", "heading", "vessel_name", "vessel_type", "length", "width", "draft"]
VESSEL_COLUMNS = "index,mmsi,name,ais_type,class,length_m,beam_m,draft_m,first_t_s,last_t_s,reports,segments,moving,ferry_candidate".split(",")
DROP_REASONS = ("impossible_position", "outside_bbox", "invalid_mmsi", "invalid_time", "duplicate", "position_jump", "outside_time_window")
CLASS_TABLE = {"30": "fishing", "31,32,52": "tug-tow", "35": "military", "36": "sailing", "37": "pleasure",
	"50": "pilot", "51": "search-rescue", "55": "law-enforcement", "60-69": "ferry-passenger",
	"70-79": "cargo", "80-89": "tanker", "all other, zero, missing or non-integer codes": "other"}
RULES = {"segment_gap_s": 300., "jump_extra_speed_kn": 10., "jump_slack_m": 30., "moving_radius_m": 50.,
	"jump_formula": "distance_m > max(valid endpoint SOG, default 0) * dt_s + 10 * 1852/3600 * dt_s + 30",
	"duplicates": "MMSI and UTC timestamp; retain first source row after stable chronological sort",
	"drop_order": ["impossible_position", "outside_bbox", "outside_time_window", "invalid_mmsi", "invalid_time", "duplicate", "position_jump"], "moving": "any report farther than 50 m from componentwise median NED position",
	"heading_valid_deg": "0 <= heading < 360", "cog_valid_deg": "0 <= COG < 360", "sog_valid_kn": "0 <= SOG <= 102.2",
	"missing_values": "invalid SOG/COG/heading become NaN; never synthesize or fill reports",
	"static_metadata": "mode among kept reports, smallest numeric/lexicographic value breaks ties; missing/nonpositive dimensions blank"}


def epoch_utc(day):
	return datetime.fromisoformat(day).replace(tzinfo=ZoneInfo("America/New_York")).astimezone(ZoneInfo("UTC")).strftime("%Y-%m-%dT%H:%M:%SZ")


def output_root():
	return paths.PROCESSED_ROOT / "harbor/traffic"


def ferry_candidates(path=None):
	return set(identity_classes(path))


def scenario_window(day, available_days):
	epoch = pd.Timestamp(epoch_utc(day))
	requested_from, requested_to = -3600., 90000.
	intervals, missing = [], []
	for offset in (0, 1):
		utc_day = (datetime.fromisoformat(day) + timedelta(days=offset)).date().isoformat()
		start = max(requested_from, (pd.Timestamp(utc_day, tz="UTC") - epoch).total_seconds())
		end = min(requested_to, (pd.Timestamp(utc_day, tz="UTC") + pd.Timedelta(days=1) - epoch).total_seconds())
		if utc_day in available_days:
			intervals.append((start, end))
		else:
			missing.append({"from_s": start, "to_s": end, "reason": f"UTC source {ais.day_name(utc_day)} is not cached"})
	return {"requested_from_s": requested_from, "requested_to_s": requested_to,
		"covered_from_s": min((a for a, _ in intervals), default=None),
		"covered_to_s": max((b for _, b in intervals), default=None), "missing_intervals": missing}


def cached_read_day(path, source_sha, cache):
	"""Cache only the spatial extract; callers first validate the complete raw source SHA."""
	cache = Path(cache)
	cache.mkdir(parents=True, exist_ok=True)
	parquet, metadata = cache / f"{source_sha}.parquet", cache / f"{source_sha}.json"
	if parquet.exists() and metadata.exists():
		entry = json.loads(metadata.read_text())
		if entry.get("schema") == "traffic-spatial/1" and entry.get("sha256") == sha256_file(parquet):
			return pd.read_parquet(parquet), entry["drops"], entry["source_rows"]
	frame, drops, count = read_day(path)
	frame.to_parquet(parquet, index=False)
	_json(metadata, {"schema": "traffic-spatial/1", "source_sha256": source_sha, "sha256": sha256_file(parquet), "drops": drops, "source_rows": count})
	return frame, drops, count


def scenario_input(day, cache=None):
	frames, sources, available = [], [], set()
	drops = {reason: 0 for reason in DROP_REASONS}
	rows = 0
	manifest = Manifest.load(ais.SOURCE)
	for offset in (0, 1):
		utc_day = (datetime.fromisoformat(day) + timedelta(days=offset)).date().isoformat()
		path = paths.raw_dir(ais.SOURCE) / ais.day_name(utc_day)
		if not path.exists():
			if offset == 0:
				raise ValueError(f"required source missing: {path}")
			continue
		if not manifest.verify(path.name, path):
			raise ValueError(f"source checksum mismatch: {path}")
		available.add(utc_day)
		sources.append({k: manifest.files[path.name][k] for k in ("name", "url", "sha256", "size_bytes")})
		frame, spatial_drops, count = cached_read_day(path, manifest.files[path.name]["sha256"], cache) if cache else read_day(path)
		rows += count
		for reason, value in spatial_drops.items():
			drops[reason] += value
		times = pd.to_datetime(frame.base_date_time, utc=True, errors="coerce", format="mixed")
		seconds = (times - pd.Timestamp(epoch_utc(day))).dt.total_seconds()
		inside = seconds.between(-3600., 90000.) | times.isna()
		drops["outside_time_window"] += int((~inside).sum())
		frames.append(frame.loc[inside])
	return pd.concat(frames, ignore_index=True), drops, rows, sources, scenario_window(day, available)


def ship_class(code):
	try:
		number = float(code)
		if not np.isfinite(number) or number != int(number):
			return "other"
		code = int(number)
	except (TypeError, ValueError, OverflowError):
		return "other"
	for low, high, value in [(60, 69, "ferry-passenger"), (70, 79, "cargo"), (80, 89, "tanker")]:
		if low <= code <= high:
			return value
	return {30: "fishing", 31: "tug-tow", 32: "tug-tow", 52: "tug-tow", 35: "military", 36: "sailing", 37: "pleasure", 50: "pilot", 51: "search-rescue", 55: "law-enforcement"}.get(code, "other")


def spatial_filter(frame):
	lat, lon = frame.latitude, frame.longitude
	valid = np.isfinite(lat) & np.isfinite(lon) & lat.between(-90, 90) & lon.between(-180, 180)
	box = AIS_HARBOR_BBOX
	inside = valid & lat.between(box["south"], box["north"]) & lon.between(box["west"], box["east"])
	return frame.loc[inside].copy(), {"impossible_position": int((~valid).sum()), "outside_bbox": int((valid & ~inside).sum())}


def read_day(path):
	"""Bounded Arrow batches; reject national rows before constructing pandas frames."""
	counts = {reason: 0 for reason in DROP_REASONS}
	frames = []
	rows = 0
	types = {c: pa.float64() for c in COLUMNS if c not in ("base_date_time", "vessel_name")}
	types.update(base_date_time=pa.string(), vessel_name=pa.string())
	with pa.CompressedInputStream(pa.OSFile(str(path)), "zstd") as stream:
		reader = pacsv.open_csv(stream, read_options=pacsv.ReadOptions(block_size=8 << 20),
			convert_options=pacsv.ConvertOptions(include_columns=COLUMNS, column_types=types, strings_can_be_null=True))
		for batch in reader:
			rows += batch.num_rows
			lat, lon = batch.column("latitude"), batch.column("longitude")
			valid = pc.fill_null(pc.and_(pc.and_(pc.is_finite(lat), pc.is_finite(lon)),
				pc.and_(pc.and_(pc.greater_equal(lat, -90), pc.less_equal(lat, 90)), pc.and_(pc.greater_equal(lon, -180), pc.less_equal(lon, 180)))), False)
			box = AIS_HARBOR_BBOX
			inside = pc.fill_null(pc.and_(valid, pc.and_(pc.and_(pc.greater_equal(lat, box["south"]), pc.less_equal(lat, box["north"])),
				pc.and_(pc.greater_equal(lon, box["west"]), pc.less_equal(lon, box["east"])))), False)
			valid_count = pc.sum(pc.cast(valid, pa.int64())).as_py() or 0
			kept = batch.filter(inside)
			counts["impossible_position"] += batch.num_rows - valid_count
			counts["outside_bbox"] += valid_count - kept.num_rows
			if kept.num_rows:
				frames.append(kept.to_pandas())
	return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame(columns=COLUMNS), counts, rows


def clean_reports(frame, day):
	frame, spatial = spatial_filter(frame)
	drops = {reason: spatial.get(reason, 0) for reason in DROP_REASONS}
	valid = np.isfinite(frame.mmsi) & frame.mmsi.between(1, 999999999) & (frame.mmsi % 1 == 0)
	drops["invalid_mmsi"] = int((~valid).sum())
	frame = frame.loc[valid].copy()
	frame["mmsi"] = frame.mmsi.astype("int64")
	frame["time_utc"] = pd.to_datetime(frame.base_date_time, utc=True, errors="coerce", format="mixed")
	drops["invalid_time"] = int(frame.time_utc.isna().sum())
	frame = frame.loc[frame.time_utc.notna()].sort_values(["mmsi", "time_utc"], kind="stable")
	duplicate = frame.duplicated(["mmsi", "time_utc"])
	drops["duplicate"] = int(duplicate.sum())
	frame = frame.loc[~duplicate].copy()
	frame["t_s"] = (frame.time_utc - pd.Timestamp(epoch_utc(day))).dt.total_seconds()
	frame["north_m"], frame["east_m"] = geodesy.to_local(frame.latitude.to_numpy(), frame.longitude.to_numpy())
	frame["sog_mps"] = frame.sog.where(frame.sog.between(0, 102.2)) * KNOT
	for raw, output in [("cog", "cog_rad"), ("heading", "heading_rad")]:
		frame[output] = np.radians(frame[raw].where(frame[raw].ge(0) & frame[raw].lt(360)))
	keep_indices, segments = [], []
	for _, group in frame.groupby("mmsi", sort=True):
		previous = None
		segment = 0
		for row in group.itertuples():
			if previous is not None:
				dt = row.t_s - previous.t_s
				speed = max([0.] + [v for v in (row.sog_mps, previous.sog_mps) if np.isfinite(v)])
				if np.hypot(row.north_m - previous.north_m, row.east_m - previous.east_m) > (speed + 10 * KNOT) * dt + 30.:
					drops["position_jump"] += 1
					continue
				if dt > 300.:
					segment += 1
			keep_indices.append(row.Index)
			segments.append(segment)
			previous = row
	frame = frame.loc[keep_indices].copy()
	frame["segment"] = np.array(segments, dtype=np.int32)
	return frame.reset_index(drop=True), drops


def _mode(series):
	values = series.dropna().mode()
	return values.iloc[0] if len(values) else None


def _json(path, value):
	path.write_text(json.dumps(value, indent="\t", ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")


def pack_digest(folder, epoch):
	digest = hashlib.sha256()
	for name in FILES:
		digest.update(name.encode() + b"\0")
		with (folder / name).open("rb") as handle:
			for chunk in iter(lambda: handle.read(1 << 20), b""):
				digest.update(chunk)
		digest.update(b"\0")
	digest.update(epoch.encode() + b"\0")
	return digest.hexdigest()


def build_pack(frame, day, folder, source=None, audit_path=None, initial_drops=None, source_rows=None, sources=None, coverage=None):
	folder = Path(folder)
	folder.mkdir(parents=True, exist_ok=True)
	clean, drops = clean_reports(frame, day)
	for reason, count in (initial_drops or {}).items():
		drops[reason] += count
	candidates = ferry_candidates(audit_path)
	vessels = []
	records = np.zeros(len(clean), dtype=RECORD_DTYPE)
	for index, (mmsi, group) in enumerate(clean.groupby("mmsi", sort=True)):
		type_ = _mode(group.vessel_type)
		median = group[["north_m", "east_m"]].median()
		moving = bool((np.hypot(group.north_m - median.north_m, group.east_m - median.east_m) > 50.).any())
		vessels.append(dict(zip(VESSEL_COLUMNS, [index, int(mmsi), _mode(group.vessel_name), type_, ship_class(type_),
			*[ _mode(group[c].where(group[c] > 0)) for c in ("length", "width", "draft")],
			float(group.t_s.iloc[0]), float(group.t_s.iloc[-1]), len(group), int(group.segment.max()) + 1, moving, int(mmsi) in candidates])))
		records["vessel_index"][group.index] = index
	for name in RECORD_DTYPE.names[1:]:
		records[name] = clean[name].to_numpy()
	roster = pd.DataFrame(vessels, columns=VESSEL_COLUMNS)
	roster.to_csv(folder / "vessels.csv", index=False, lineterminator="\n", float_format="%.17g")
	records.tofile(folder / "reports.bin")
	_json(folder / "reports.json", LAYOUT)
	audit = Path(audit_path) if audit_path else paths.REPO_ROOT / "data-pipeline/calibration/identity-audit.csv"
	manifest = {"format": FORMAT, "date": day, "epoch_utc": epoch_utc(day), "bbox": AIS_HARBOR_BBOX,
		"source": source or {"name": "in-memory-fixture", "sha256": None},
		"source_cadence": "MarineCadastre csv2 reports are downsampled to about one per minute; no missing reports are synthesized",
		"sources": sources or ([source] if source else []),
		"time_coverage": coverage or scenario_window(day, {day}),
		"drop_accounting": "whole national source files: spatial rejection first, then harbor reports outside the padded local-day window, then remaining cleaning rules",
		"counts": {"vessels": len(roster), "reports": len(records), "source_reports": source_rows if source_rows is not None else len(frame),
			"dropped_reports": drops, "vessels_by_class": {k: int(v) for k, v in sorted(roster["class"].value_counts().items())}},
		"cleaning": RULES, "class_mapping": CLASS_TABLE,
		"ferry_candidates": {"source": "data-pipeline/calibration/identity-audit.csv", "sha256": sha256_file(audit), "subsets": ["T", "K"]},
		"files": [{"name": name, "sha256": sha256_file(folder / name), "size_bytes": (folder / name).stat().st_size} for name in FILES],
		"digest": pack_digest(folder, epoch_utc(day))}
	_json(folder / "manifest.json", manifest)
	return manifest


def verify_pack(folder):
	folder = Path(folder)
	manifest = json.loads((folder / "manifest.json").read_text())
	def require(condition, reason):
		if not condition:
			raise ValueError(f"{folder}: {reason}")
	require(manifest["format"] == FORMAT, "unknown format")
	require(manifest["epoch_utc"] == epoch_utc(manifest["date"]), "epoch mismatch")
	require(manifest["bbox"] == AIS_HARBOR_BBOX, "bbox mismatch")
	require([f["name"] for f in manifest["files"]] == list(FILES), "file inventory mismatch")
	for entry in manifest["files"]:
		path = folder / entry["name"]
		require(path.stat().st_size == entry["size_bytes"] and sha256_file(path) == entry["sha256"], f"checksum mismatch: {path.name}")
	require(json.loads((folder / "reports.json").read_text()) == LAYOUT, "binary layout mismatch")
	require(pack_digest(folder, manifest["epoch_utc"]) == manifest["digest"], "digest mismatch")
	require((folder / "reports.bin").stat().st_size % 44 == 0, "truncated records")
	records = np.fromfile(folder / "reports.bin", dtype=RECORD_DTYPE)
	vessels = pd.read_csv(folder / "vessels.csv", converters={"first_t_s": float, "last_t_s": float})
	require(list(vessels.columns) == VESSEL_COLUMNS, "roster columns mismatch")
	require(len(records) == manifest["counts"]["reports"] and len(vessels) == manifest["counts"]["vessels"], "counts mismatch")
	require(manifest["counts"]["source_reports"] == len(records) + sum(manifest["counts"]["dropped_reports"].values()), "drop accounting mismatch")
	require(np.all(np.diff(records["vessel_index"]) >= 0), "vessel record order mismatch")
	for field in ("sog_mps", "cog_rad", "heading_rad"):
		values = records[field]
		require(not np.any(np.isinf(values)), f"infinite {field}")
		valid = values[np.isfinite(values)]
		limit = 102.2 * KNOT + 1e-5 if field == "sog_mps" else 2 * np.pi
		require(np.all((valid >= 0) & (valid <= limit)), f"out-of-range {field}")
	require(manifest["counts"]["vessels_by_class"] == {k: int(v) for k, v in sorted(vessels["class"].value_counts().items())}, "class counts mismatch")
	require(list(vessels["index"]) == list(range(len(vessels))), "vessel indices mismatch")
	require(list(vessels.mmsi) == sorted(set(vessels.mmsi)), "MMSI order mismatch")
	require(np.all(np.isfinite(records["t_s"])) and np.all(np.isfinite(records["north_m"])) and np.all(np.isfinite(records["east_m"])), "nonfinite time/position")
	require(np.all((records["vessel_index"] >= 0) & (records["vessel_index"] < len(vessels))), "unknown vessel index")
	for row in vessels.itertuples():
		group = records[records["vessel_index"] == row.index]
		require(len(group) == row.reports and len(group) > 0, "vessel reports mismatch")
		require(np.all(np.diff(group["t_s"]) > 0), "report time order mismatch")
		expected = np.r_[0, np.cumsum(np.diff(group["t_s"]) > 300)]
		require(np.array_equal(group["segment"], expected), "segment mismatch")
		require(row.segments == int(expected[-1]) + 1 and row.first_t_s == group["t_s"][0] and row.last_t_s == group["t_s"][-1], "vessel bounds mismatch")
	return manifest
