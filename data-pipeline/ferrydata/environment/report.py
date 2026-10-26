"""Durable per-day coverage, source limitations and one day's tide/current plot."""
import csv
import json
from pathlib import Path

from ferrydata.environment import pack


def _plot(root, day, target):
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	fig, (currents, tide) = plt.subplots(2, 1, figsize=(11, 7), sharex=True, layout='constrained')
	folder = Path(root) / 'days' / day
	with (folder / 'currents.csv').open() as stream:
		rows = list(csv.DictReader(stream))
	for station in sorted({r['station_id'] for r in rows}):
		data = [r for r in rows if r['station_id'] == station]
		# Signed along-axis speed uses flood/ebb projection against published flood
		# direction only in the source report. This plot shows unambiguous north components.
		currents.plot([float(r['t_s'])/3600 for r in data], [float(r['north_mps']) for r in data], label=station)
	currents.set_ylabel('Current north component (m/s)')
	currents.legend(ncol=3, fontsize=8)
	currents.grid(alpha=.25)
	with (folder / 'water_levels.csv').open() as stream:
		rows = list(csv.DictReader(stream))
	for station in ('8518750', '8516945', '8516990'):
		data = [r for r in rows if r['station_id'] == station]
		if data:
			tide.plot([float(r['t_s'])/3600 for r in data], [float(r['level_navd88_m']) for r in data], label=station)
	tide.set_ylabel('Water level NAVD88 (m)')
	tide.set_xlabel('Hours since local midnight (America/New_York)')
	tide.legend()
	tide.grid(alpha=.25)
	fig.suptitle(f'Lower East River environment — {day}\nHarmonic current predictions and observed/predicted water levels')
	fig.savefig(target, dpi=160, metadata={'Software': 'ferrydata.environment'})
	plt.close(fig)


def write(result, root, destination):
	destination = Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	pack.write_json(destination / 'build-summary.json', result)
	depth, stats = result['depth'], result['depth_stats']
	grid = depth['grids'][0]
	lines = ['# Environment Pack Report', '',
		f'Region: `{result["region"]}`. {len(result["days"])} days built in this invocation. All packs present passed offline verification.', '',
		f'Total environment directory: {result["total_pack_bytes"]:,} bytes (depth shared once). New raw downloads in this invocation: {result["new_fetch_count"]}; provenance manifest contains {result["total_raw_files"]} files.', '',
		'## Reproduction', '', 'Run from the repository root:', '', '```sh',
		'uv run --project data-pipeline python data-pipeline/ferrydata build --slice lower-east-river',
		'PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --all',
		'PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment verify --region lower-east-river', '```', '',
		'`PYTHONPATH` is required because the existing project uses `package = false`; no project wiring or packages were changed.', '',
		'## Depth', '',
		f'{grid["rows"]} rows × {grid["cols"]} columns, 5 m cell centres, row 0 south, columns growing east. Bed elevation is positive up in NAVD88; negative values are underwater at NAVD88 zero.', '',
		f'Raster horizontal CRS: `{stats["crs"]}`. Each world-frame cell centre goes through `ferrydata.geodesy.from_local` (WGS84), then `pyproj.Transformer` to the raster CRS, then bilinear sampling. Float32 output is little-endian and row-major.', '',
		'CUDEM is NAVD88; ENC MLLW chart depths were converted through The Battery MLLW–NAVD88 station datum offset in the merged raster. That station offset is spatially constant; there is no VDatum surface correction. The source raster and metadata SHA-256 values are in the depth manifest.', '',
		f'Water area below NAVD88 zero: {stats["water_area_m2"]:,} m²; finite sample area: {stats["finite_area_m2"]:,} m²; NaN area: {stats["nan_area_m2"]:,} m². Bed range: {stats["min_m"]:.6f} to {stats["max_m"]:.6f} m. Area is centre-count × 25 m², an approximation to the lattice footprint.', '',
		f'Initial padding excluded: {stats["excluded_padding_cells"]} cells. Interior NaNs before chart fill: {stats["nan_cells_before"]}; water NaNs before: {stats["water_nan_before"]}; chart-filled water cells: {stats["chart_filled_cells"]}; water NaNs after: {stats["water_nan_after"]}. The largest world-aligned interior rectangle is used; no edge extrapolation. Points outside it use outside_depth_m = 20 m.', '',
		f'Unresolved water clusters: {json.dumps(stats["water_nan_clusters"], sort_keys=True)}. Context labels are nearby chart feature classes, not a crew-verified structure survey.', '',
		'## Digests', '',
		f'Depth digest (`depth/DIGEST`): `{result["depth_digest"]}`.', '',
		'Each DIGEST is 64 lowercase SHA-256 hex characters followed by LF. Neither manifest has a digest key. The day manifest’s depth_digest equals the depth DIGEST.', '',
		'Digests hash raw bytes on disk, without JSON re-serialisation. Each file contributes its UTF-8 filename, NUL, its bytes, NUL. Depth order is manifest.json followed by grid files in manifest grids order. Day order is manifest.json, currents.csv, water_levels.csv, wind.csv, followed by the 64 ASCII depth-digest characters and NUL (without LF). Every manifest field is covered, including outside_depth_m, time_coverage_s and station metadata.', '',
		'Offline verify recomputes both digests, checks DIGEST syntax, individual grid/CSV SHA-256 values and the day-to-depth reference, and refuses mismatches with the affected path. Digests detect byte changes, not authenticity. The per-day table below lists every day DIGEST.', '',
		'## Source policy and limitations', '',
		'- All calibration current files are verified against `data/manifests/coops-calibration.json` before parsing. A mismatched or untracked raw file fails without refetching.',
		'- Current components point toward flow; metric NOAA major-axis velocities arrive in cm/s. Bin/depth and daily peak flood/ebb magnitudes below come from the selected API-default response, without asserting shallowest selection.',
		'- Dimond Reef (NYH1919) uses its API-default bin 9, which CO-OPS measures 9.4 m up from the seabed (depthType S): its shallowest bin, about 5 m below the surface. Between stations the current field (field_digest) interpolates along the channels.',
		'- Water levels use NAVD88 only. Observations win; missing observations use same-datum predictions with `source=predicted`. Prediction raw file names and SHA-256 values appear in day notes when used.',
		'- Wind uses the first available station per day in Battery, Kings Point, Robbins Reef, Bergen Point order; only that station is in the pack. Meteorological from-directions become toward-components. Gusts are preserved and sensor height remains unknown.',
		'- NOAA reports no Battery wind for all four weeks. Kings Point is more exposed on Long Island Sound and is explicitly labelled a spatial proxy. No missing wind is invented.',
		'- Bergen Point lacks NAVD88 and is omitted from water levels. Willets Point has NAVD88 but no observed water levels in these windows; its same-datum predictions supply its series.',
		'- Gaps are missing rows. Readers interpolate only up to 1800 s; longer gaps and endpoint coverage are recorded in each day’s notes.',
		'- NOAA API reference: https://api.tidesandcurrents.noaa.gov/api/prod/ . All raw responses, including no-data JSON, are recorded in `data/manifests/coops-environment.json` with URL, retrieval time and SHA-256.', '',
		'## Per-day coverage and ranges', '',
		'| Day | Current rows | Water rows observed / predicted | Wind station / rows | Water range NAVD88 m | Wind speed range m/s / max gust m/s | Digest |',
		'|---|---:|---:|---|---|---|---|']
	for day, item in result['days'].items():
		water, wind = list(item['water_levels'].values()), list(item['wind'].values())
		wind_range = f'{min(s["min_mps"] for s in wind):.3f}–{max(s["max_mps"] for s in wind):.3f}' if wind else 'still air (unavailable)'
		gusts = [s['max_gust_mps'] for s in wind if s['max_gust_mps'] is not None]
		wind_range += f' / {max(gusts):.3f}' if gusts else ' / unknown'
		lines.append(f'| {day} | {sum(s["rows"] for s in item["currents"].values())} | {sum(s["observed"] for s in water)} / {sum(s["predicted"] for s in water)} | {", ".join(item["wind"])} / {sum(s["rows"] for s in wind)} | {min(s["min_m"] for s in water):.3f}–{max(s["max_m"] for s in water):.3f} | {wind_range} | `{item["digest"]}` |')
	lines += ['', '## Daily station coverage, gaps and fills', '']
	for day, item in result['days'].items():
		lines += [f'### {day}', '', '| Series | Station | Rows | First / last t_s | Observed / predicted |', '|---|---|---:|---|---|']
		for kind in ('currents', 'water_levels', 'wind'):
			for station, s in item[kind].items():
				lines.append(f'| {kind} | {station} | {s["rows"]} | {s["first_s"]:g} / {s["last_s"]:g} | {s.get("observed", "—")} / {s.get("predicted", "—")} |')
		lines += ['', *[f'- {note}' for note in item['notes']], '']
	lines += ['## Daily peak flood and ebb speed', '', '| Day | Station | Bin | Depth m | Peak flood m/s | Peak ebb m/s |', '|---|---|---:|---:|---:|---:|']
	for day, item in result['days'].items():
		for station, s in item['currents'].items():
			flood = f'{s["flood"]:.4f}' if s['flood'] is not None else 'not present'
			ebb = f'{s["ebb"]:.4f}' if s['ebb'] is not None else 'not present'
			bin_, depth_ = ('—', '—') if s['bin'] is None else (s['bin'], s['depth_m'])
			lines.append(f'| {day} | {station} | {bin_} | {depth_} | {flood} | {ebb} |')
	day = next(iter(result['days']))
	_plot(root, day, destination / 'currents-tide.png')
	lines += ['', '## One-day plot', '', f'![Current north components and tide, {day}](currents-tide.png)', '']
	(destination / 'report.md').write_text('\n'.join(lines), encoding='utf-8')
