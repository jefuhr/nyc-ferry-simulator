"""Build the harbor operational-ais candidate beside, never over, the default harbor outputs."""
import json
import math
import shutil
import subprocess
from pathlib import Path

import numpy as np
import pandas as pd
import rasterio
from shapely.geometry import Point
from shapely.strtree import STRtree

from ferrydata import geodesy, landings, manifest, operational_depth as od, paths, tiles
from ferrydata.environment import harbor, pack, series
from ferrydata.harbor_landings.assessment import load_levels


def snapshot(directory):
	return {str(p.relative_to(directory)): manifest.sha256_file(p) for p in sorted(Path(directory).rglob('*')) if p.is_file()}


def cell_key(lat, lon):
	"""Global 1/9 arc-second pixel index. Centres are half-integers; round() would merge neighbours."""
	return math.floor(float(lat) * 32400), math.floor(float(lon) * 32400)


def build(source, destination, tracks=None, log=print):
	"""No downloads and no default mutation. Candidate includes source geometry and newly built depth only."""
	source, destination = Path(source), Path(destination)
	if (source.resolve() == destination.resolve() or source.resolve() in destination.resolve().parents
		or destination.resolve() in source.resolve().parents):
		raise ValueError('candidate must be separate from the default harbor root')
	active = harbor.active_tiles(source)
	destination.mkdir(parents=True, exist_ok=True)
	original_index = json.loads((source / 'index.json').read_text(encoding='utf-8'))
	index = dict(schema='harbor-operational-candidate/1', source_index_sha256=manifest.sha256_file(source / 'index.json'),
		tiles=[{key: entry[key] for key in ('id', 'i', 'j', 'active')} for entry in original_index['tiles']])
	faces = []
	for i, j in active:
		folder = source / 'tiles' / harbor.tile_id(i, j)
		faces += od.face_anchors(json.loads((folder / 'landing_faces.geojson').read_text(encoding='utf-8'))['features'])
	frame = pd.read_parquet(tracks or od.tracks_path(), columns=['mmsi', 'time_utc', 'lat', 'lon', 'north_m', 'east_m', 'sog_mps'])

	def inside(lat, lon):
		n, e = geodesy.to_local(lat, lon)
		return np.array([(math.floor(nn / tiles.SIZE_M), math.floor(ee / tiles.SIZE_M)) in active for nn, ee in zip(n, e)])

	reports, dropped = od.select_reports(frame, od.identity_classes(), faces, inside)
	levels = {day: load_levels(source / 'env' / 'days' / day) for day in series.DAYS}
	classes = od.vessel_classes()
	reports = od.required_beds(reports, levels, classes)
	reports.to_csv(destination / od.REPORTS_FILE, index=False, lineterminator='\n')
	sources = {od.REPORTS_FILE: manifest.sha256_file(destination / od.REPORTS_FILE),
		'identity-audit.csv': manifest.sha256_file(od.identity_audit_path()),
		'fleet.parquet': manifest.sha256_file(tracks or od.tracks_path()),
		'coops-environment.json': manifest.sha256_file(paths.MANIFEST_DIR / 'coops-environment.json'),
		**{c.file: c.sha256 for c in classes.values()}}
	summaries, unique, all_near = [], {}, []
	for i, j in active:
		identifier = harbor.tile_id(i, j)
		folder, target = source / 'tiles' / identifier, destination / 'tiles' / identifier
		target.mkdir(parents=True, exist_ok=True)
		for name in ('landing_faces.geojson', 'landing_structures.geojson', 'land_areas.geojson', 'shoreline_constructions.geojson', 'coops_stations.json'):
			shutil.copyfile(folder / name, target / name)
		# The sidecar lists the cached cells that contributed to this tile; no fetch/catalogue discovery.
		cells = tuple(json.loads((folder / 'shoreline_constructions.geojson').read_text(encoding='utf-8'))['cells'])
		region = tiles.make_tile(i, j, cells)
		items = od.hazards(folder, region, areas_only=False)
		with rasterio.open(folder / 'depth_merged_navd88.tif') as ds:
			bed, profile = ds.read(1), ds.profile
		bounds = region.grown_local_bounds
		margin = max(c.footprint_radius_m for c in classes.values()) + 10.
		own = reports[(reports.north_m >= bounds[0] - margin) & (reports.north_m <= bounds[2] + margin)
			& (reports.east_m >= bounds[1] - margin) & (reports.east_m <= bounds[3] + margin)]
		# Match the harbor tile build's shared heavy resource discipline; never lock the whole harbor build.
		lock = Path.home() / 'FerrySim-cache' / 'heavy.lock'
		# lockf runs this one tile's floor application, with already selected deterministic inputs.
		own.to_pickle(target / 'reports.pkl')
		pack.write_json(target / 'hazards.json', [dict(**{k: v for k, v in h.items() if k != 'geometry'}, wkb=h['geometry'].wkb_hex) for h in items])
		command = ['lockf', '-k', str(lock), str(paths.REPO_ROOT / 'data-pipeline' / '.venv' / 'bin' / 'python'),
			'-m', 'ferrydata.harbor_landings.tile', str(folder), str(target)]
		run = subprocess.run(command, capture_output=True, text=True)
		if run.returncode:
			log(run.stdout + run.stderr, flush=True)
			run.check_returncode()
		provenance = json.loads((target / od.REPORT_FILE).read_text(encoding='utf-8'))
		with rasterio.open(target / 'depth_merged_navd88.tif') as ds:
			new = ds.read(1)
		lowered = new < bed
		r, c = np.nonzero(lowered)
		t = profile['transform']
		lat, lon = t.f + (r + .5) * t.e, t.c + (c + .5) * t.a
		n, e = geodesy.to_local(lat, lon)
		amounts = bed[r, c].astype(float) - new[r, c].astype(float)
		local_hazards = [landings.to_local(h['geometry']) for h in items]
		tree = STRtree(local_hazards)
		for rr, cc, nn, ee, ll, lo, amount in zip(r, c, n, e, lat, lon, amounts):
			key = cell_key(ll, lo)
			if key in unique and unique[key] != (float(bed[rr, cc]), float(new[rr, cc])):
				raise ValueError(f'{identifier}: inconsistent overlap correction at {key}')
			unique[key] = (float(bed[rr, cc]), float(new[rr, cc]))
			point = Point(ee, nn)
			for k in tree.query(point.buffer(50.)):
				distance = point.distance(local_hazards[k])
				if distance <= 50.:
					all_near.append(dict(tile=identifier, row=int(rr), col=int(cc), north_m=float(nn), east_m=float(ee),
						lowering_m=float(amount), hazard_layer=items[k]['layer'], hazard_id=items[k]['id'], distance_m=distance))
		for entry in index['tiles']:
			if entry['id'] == identifier:
				entry['operational_ais_candidate'] = True
				entry['outputs'] = [dict(file=f'tiles/{identifier}/{p.name}', sha256=manifest.sha256_file(p), size_bytes=p.stat().st_size)
					for p in sorted(target.iterdir()) if p.is_file() and p.suffix != '.pkl' and p.name != 'hazards.json']
		provenance['tile'] = identifier
		summaries.append(provenance)
		for name in ('reports.pkl', 'hazards.json'):
			(target / name).unlink()
		log(f'{identifier}: {int(lowered.sum())} cells lowered', flush=True)
	(destination / 'index.json').write_text(json.dumps(index, indent='\t') + '\n', encoding='utf-8')
	# build_depth only needs active entries and the candidate rasters. Its ownership rule keeps seam centres identical.
	depth_metadata, stats = harbor.build_depth(destination, destination / 'env' / 'depth', log=log)
	amounts = [before - after for before, after in unique.values()]
	pd.DataFrame(all_near, columns=['tile', 'row', 'col', 'north_m', 'east_m', 'lowering_m', 'hazard_layer', 'hazard_id', 'distance_m']).to_csv(
		destination / 'lowered-within-50m-hazard.csv', index=False, lineterminator='\n')
	summary = dict(method=od.METHOD, candidate_only=True, reports=int(len(reports)), dropped_reports=dropped,
		reports_by_class=od._counts(reports.vessel_class), cells_lowered=len(unique),
		max_lowering_m=max(amounts, default=0.), near_hazard_records=len(all_near),
		sources=sources, tiles=summaries, depth_digest=pack.read_digest(destination / 'env' / 'depth'))
	pack.write_json(destination / 'candidate.json', summary)
	return summary
