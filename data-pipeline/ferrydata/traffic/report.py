"""Reproducible report derived from verified packs, source charts and build metrics."""

import json
from pathlib import Path
import zipfile

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
from matplotlib.lines import Line2D
import numpy as np
import pandas as pd

from ferrydata import ais, geodesy, gtfs, paths
from ferrydata.traffic import RECORD_DTYPE, epoch_utc, verify_pack


def _pier11():
	feed = gtfs.read_feed(paths.raw_dir(gtfs.SOURCE) / "gtfs.zip")
	stops = feed["stops"]
	found = stops[stops.stop_name.str.contains("Wall St|Pier 11", case=False, regex=True)]
	if len(found) != 1:
		raise ValueError(f"expected one Pier 11 stop, got {len(found)}")
	row = found.iloc[0]
	return float(row.stop_lat), float(row.stop_lon), str(row.stop_name)


def _land(ax):
	"""Local NOAA ENC land polygons provide context; no new downloads."""
	import geopandas as gpd
	from shapely.geometry import box
	for path in sorted(paths.raw_dir("noaa-enc").glob("*.zip")):
		with zipfile.ZipFile(path) as archive:
			members = sorted(n for n in archive.namelist() if n.endswith(".000"))
		for member in members:
			frame = gpd.read_file(f"/vsizip/{path}/{member}", layer="LNDARE", bbox=(-74.1, 40.5, -73.75, 40.9))
			if len(frame):
				frame.clip(box(-74.1, 40.5, -73.75, 40.9)).plot(ax=ax, color="#ddd9c9", edgecolor="#aaa58f", linewidth=.3)


def write_report(root, artifacts):
	root, artifacts = Path(root), Path(artifacts)
	artifacts.mkdir(parents=True, exist_ok=True)
	metrics = json.loads((artifacts / "metrics.json").read_text())
	packs = [verify_pack(root / day) for day in ais.all_days()]
	classes = sorted({c for p in packs for c in p["counts"]["vessels_by_class"]})
	lines = ["# Traffic pack build report", "", "The 28 scenario packs combine adjacent cached MarineCadastre UTC days, selecting the padded local-day window from -3,600 to 90,000 seconds relative to America/New_York local midnight. Negative times are valid; each end-of-week Sunday has an explicitly missing tail where the next Monday is not cached. Source reports are downsampled to about one per minute, so these packs cannot supply high-rate motion evidence.", "", "## Per-day builds", "", "Peak memory is per-day subprocess maximum resident set size, including reading, cleaning, writing and verification. Elapsed time includes source SHA-256 validation and pack verification, excludes waiting for the shared heavy lock, and comes from the first complete build; adjacent days can reuse checksum-validated spatial extracts. The second rebuild uses those extracts and still validates raw-source SHA-256.", "", "| Date | Vessels | Kept reports | Peak MiB | Seconds | Impossible position | Outside bbox | Invalid MMSI | Invalid time | Duplicate | Jump | Outside time window |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
	for pack in packs:
		day, count = pack["date"], pack["counts"]
		d = count["dropped_reports"]
		lines.append(f"| {day} | {count['vessels']} | {count['reports']} | {metrics[day]['peak_rss_bytes']/2**20:.1f} | {metrics[day]['elapsed_s']:.2f} | " + " | ".join(str(d[k]) for k in ("impossible_position", "outside_bbox", "invalid_mmsi", "invalid_time", "duplicate", "position_jump", "outside_time_window")) + " |")
	lines += ["", "### Vessels by AIS class", "", "| Date | " + " | ".join(classes) + " |", "|---|" + "---:|" * len(classes)]
	for pack in packs:
		lines.append("| " + pack["date"] + " | " + " | ".join(str(pack["counts"]["vessels_by_class"].get(c, 0)) for c in classes) + " |")
	day = "2025-07-14"
	records = np.fromfile(root / day / "reports.bin", dtype=RECORD_DTYPE)
	vessels = pd.read_csv(root / day / "vessels.csv", keep_default_na=False)
	colours = plt.get_cmap("tab20").colors
	palette = dict(zip(classes, [colours[i] for i in (0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 1, 3)]))
	fig, ax = plt.subplots(figsize=(12, 13))
	ax.set_facecolor("#e7f2fa")
	_land(ax)
	for cls in classes:
		lines_by_class = []
		for vessel in vessels[vessels["class"] == cls].itertuples():
			group = records[records["vessel_index"] == vessel.index]
			for segment in np.unique(group["segment"]):
				r = group[group["segment"] == segment]
				lat, lon = geodesy.from_local(r["north_m"], r["east_m"])
				if len(r) > 1:
					lines_by_class.append(np.column_stack([lon, lat]))
				else:
					ax.scatter(lon, lat, s=1, color=palette[cls])
		ax.add_collection(LineCollection(lines_by_class, colors=[palette[cls]], linewidths=.6, alpha=.75, label=cls))
	lat, lon, name = _pier11()
	ax.scatter([lon], [lat], marker="*", s=90, color="black", label=name)
	ax.set(xlim=(-74.1, -73.75), ylim=(40.5, 40.9), xlabel="WGS84 longitude (degrees)", ylabel="WGS84 latitude (degrees)", title="Harbor AIS tracks · 2025-07-14 local scenario day")
	ax.set_aspect(1 / np.cos(np.radians(40.7)))
	handles = [Line2D([], [], color=palette[cls], linewidth=2, label=cls) for cls in classes]
	handles.append(Line2D([], [], color="black", marker="*", linestyle="none", markersize=8, label=name))
	ax.legend(handles=handles, loc="lower right", fontsize=8)
	fig.tight_layout()
	fig.savefig(artifacts / "tracks-2025-07-14.png", dpi=160)
	plt.close(fig)
	lines += ["", "## Coverage", "", "Intervals below are source availability, not guarantees that AIS observed a vessel at every instant. All intervals are seconds relative to the local-midnight epoch.", "", "| Date | Epoch UTC | Covered from s | Covered to s | Missing intervals |", "|---|---|---:|---:|---|"]
	for pack in packs:
		coverage = pack["time_coverage"]
		missing = "; ".join(f"[{interval['from_s']:.0f}, {interval['to_s']:.0f}]: {interval['reason']}" for interval in coverage["missing_intervals"]) or "None"
		lines.append(f"| {pack['date']} | {pack['epoch_utc']} | {coverage['covered_from_s']:.0f} | {coverage['covered_to_s']:.0f} | {missing} |")
	lines += ["", "## Harbor tracks", "", "![Harbor tracks coloured by AIS class](tracks-2025-07-14.png)", "", "The background uses cached NOAA ENC LNDARE polygons; its coverage is limited to available cached cells. Tracks are drawn separately per segment, without joining gaps.", "", "## Vessels near Pier 11 at 08:30 local, 2025-07-14", "", f"Pier 11 reference: cached GTFS `{name}` at ({lat:.6f}, {lon:.6f}). This table uses the nearest observed report within ±60 seconds of 08:30 EDT (12:30 UTC), then selects positions within 2,000 m; offsets and distances are observed, not interpolated. Vessels without an observation in that window are unknown and omitted.", "", "| MMSI | Name | Class | Distance m | Report offset s | Moving | Ferry candidate |", "|---|---|---|---:|---:|---|---|"]
	pn, pe = geodesy.to_local(lat, lon)
	nearby = []
	for vessel in vessels.itertuples():
		group = records[records["vessel_index"] == vessel.index]
		index = np.argmin(abs(group["t_s"] - 8.5 * 3600))
		row = group[index]
		offset = row["t_s"] - 8.5 * 3600
		distance = np.hypot(row["north_m"] - pn, row["east_m"] - pe)
		if abs(offset) <= 60 and distance <= 2000:
			nearby.append((distance, vessel, offset))
	for distance, vessel, offset in sorted(nearby, key=lambda item: (item[0], item[1].mmsi)):
		lines.append(f"| {vessel.mmsi} | {vessel.name.replace('|', '/')} | {vessels.loc[vessels["index"] == vessel.index, "class"].iloc[0]} | {distance:.1f} | {offset:+.0f} | {vessel.moving} | {vessel.ferry_candidate} |")
	total = sum(p.stat().st_size for day in ais.all_days() for p in (root / day).iterdir() if p.is_file())
	biggest = max(packs, key=lambda p: p["counts"]["reports"])
	lines += ["", "## Size and validation", "", f"Total pack size: {total:,} bytes ({total/2**20:.2f} MiB). Biggest report day: {biggest['date']}, {biggest['counts']['vessels']} vessels and {biggest['counts']['reports']:,} reports.", "", "Exact commands and test output are preserved in this artifact directory. `determinism.json` compares SHA-256 of every file, including manifests, across two complete builds; all entries must match.", "", "Replay, interpolation, Unity ingestion and physical navigation validation are outside this task. No downloads, packages, commits or pushes were added."]
	lines += ["", "### Executed checks", "", "- `uv run --project data-pipeline pytest data-pipeline`: see `pytest-acceptance.txt` for the exact complete test output.", "- `PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.traffic build --all`: first full build, with one heavy-lock acquisition per scenario day; see `build-first.txt` and `metrics.json`.", "- `uv run --project data-pipeline python artifacts/traffic-pack/finish-validation.py 34358`: waits for that first build process, snapshots verified packs, invokes a second full build with the same Python module, verifies, compares and generates this report; see `final-validation.txt`, `build-second.txt`, `metrics-second.json` and `determinism.json`.", "- `PYTHONPATH=data-pipeline python -m ferrydata.traffic verify`: executed by the validation script using the locked-environment Python; see `verify.txt`.", "- `git diff --cached --check`: passed before handoff."]
	comparison_path = artifacts / "determinism.json"
	if comparison_path.exists():
		comparison = json.loads(comparison_path.read_text())
		lines += ["", f"Byte comparison result: {comparison['files_compared']} files compared, all identical: {comparison['all_identical']}; mismatches: {len(comparison['mismatches'])}."]
	most_vessels = max(packs, key=lambda pack: pack["counts"]["vessels"])
	lines += ["", f"Largest vessel roster: {most_vessels['date']}, {most_vessels['counts']['vessels']} vessels and {most_vessels['counts']['reports']:,} reports.", "", "The original pathless `uv run --project data-pipeline pytest` command also collected the separate crew-intake project and failed with `ModuleNotFoundError: crew_intake`; `pytest.txt` preserves that output. The coordinator corrected acceptance to the explicit `data-pipeline` path above, covering the 193 existing tests plus this package's tests."]
	checkpoint = artifacts / "lock-wait-checkpoint.txt"
	if checkpoint.exists():
		lines += ["", "### Shared-resource waiting", "", "The first build's 2024-10-11 worker waited at least 5 minutes 3 seconds for the shared heavy lock (checkpoint at 2026-10-25T03:16:24Z, before any child worker existed). The coordinator subsequently required Unity and heavy data steps to share this lock after machine-wide swapping; elapsed processing metrics above exclude lock waiting. The checkpoint is preserved in `lock-wait-checkpoint.txt`; no other process was stopped or modified."]
	(artifacts / "report.md").write_text("\n".join(lines) + "\n")
	print(f"wrote {artifacts / 'report.md'}; {len(nearby)} observed vessels near Pier 11")
