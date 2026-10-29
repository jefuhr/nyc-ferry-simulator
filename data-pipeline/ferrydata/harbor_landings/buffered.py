"""Review-only 10/25 m chart-hazard variants derived from the same operational-AIS floor.

The base floor is already stored rounded toward deeper water as float32, exactly as the
candidate bed. Removing its covered cells inside a buffer therefore reproduces the same
min(original, floor) rule on all remaining cells; no AIS parameter or required bed changes.
Distances are exact world-metre distances from raster cell centres to hazard geometry.
"""
import json
import shutil
import subprocess
from pathlib import Path

import numpy as np
import pandas as pd
import rasterio
from shapely import points
from shapely.geometry import Point
from shapely.strtree import STRtree

from ferrydata import geodesy, landings, manifest, operational_depth as od, paths, tiles
from ferrydata.environment import harbor, pack
from ferrydata.harbor_landings.candidate import cell_key


def protected_centres(items, transform, grid_shape, distance, covered):
	mask = np.zeros(grid_shape, bool)
	if distance <= 0. or not items or not covered.any():
		return mask
	r, c = np.nonzero(covered)
	n, e = geodesy.to_local(transform.f + (r + .5) * transform.e, transform.c + (c + .5) * transform.a)
	tree = STRtree([landings.to_local(h['geometry']) for h in items])
	indices, distances = tree.query_nearest(points(e, n), max_distance=float(distance), return_distance=True, all_matches=False)
	selected = indices[0, distances <= distance]
	mask[r[selected], c[selected]] = True
	return mask


def apply(original, base_floor, transform, nodata, items, distance):
	if not np.isfinite(distance) or distance < 0.:
		raise ValueError('hazard buffer must be finite and nonnegative')
	floor = base_floor.copy()
	protected = protected_centres(items, transform, original.shape, distance, np.isfinite(floor))
	floor[protected] = np.nan
	new, lowered, changed = od.apply_floor(original, floor, np.isfinite(floor), nodata)
	return new, lowered, changed, floor


def run_tile(source, base, target, distance, *, lock_held=False):
	command = [str(paths.REPO_ROOT / 'data-pipeline' / '.venv' / 'bin' / 'python'), '-m',
		'ferrydata.harbor_landings.tile_buffered', str(source), str(base), str(target), str(float(distance))]
	if not lock_held:
		command = ['lockf', '-k', str(Path.home() / 'FerrySim-cache' / 'heavy.lock'), *command]
	return subprocess.run(command, capture_output=True, text=True)


def build(source, base, destination, distance, log=print, *, lock_held=False):
	source, base, destination = map(Path, (source, base, destination))
	if distance not in (10., 25.):
		raise ValueError('review variants are 10 m and 25 m')
	for immutable in (source, base):
		if (immutable.resolve() == destination.resolve() or immutable.resolve() in destination.resolve().parents
			or destination.resolve() in immutable.resolve().parents):
			raise ValueError('buffered destination must be separate from both source trees')
	pack.verify_depth(base / 'env' / 'depth')
	active = harbor.active_tiles(source)
	destination.mkdir(parents=True, exist_ok=True)
	shutil.copyfile(base / od.REPORTS_FILE, destination / od.REPORTS_FILE)
	index = json.loads((base / 'index.json').read_text(encoding='utf-8'))
	index['hazard_buffer_m'] = float(distance)
	unique, near, summaries = {}, [], []
	for i, j in active:
		identifier = harbor.tile_id(i, j)
		folder, base_tile, target = source / 'tiles' / identifier, base / 'tiles' / identifier, destination / 'tiles' / identifier
		target.mkdir(parents=True, exist_ok=True)
		for name in ('landing_faces.geojson', 'landing_structures.geojson', 'land_areas.geojson', 'shoreline_constructions.geojson', 'coops_stations.json'):
			shutil.copyfile(base_tile / name, target / name)
		cells = tuple(json.loads((folder / 'shoreline_constructions.geojson').read_text(encoding='utf-8'))['cells'])
		items = od.hazards(folder, tiles.make_tile(i, j, cells), areas_only=False)
		pack.write_json(target / 'hazards.wkb.json', [dict(**{k: v for k, v in h.items() if k != 'geometry'}, wkb=h['geometry'].wkb_hex) for h in items])
		run = run_tile(folder, base_tile, target, distance, lock_held=lock_held)
		if run.returncode:
			log(run.stdout + run.stderr, flush=True)
			run.check_returncode()
		with rasterio.open(folder / 'depth_merged_navd88.tif') as ds:
			before, t = ds.read(1), ds.transform
		with rasterio.open(base_tile / 'depth_merged_navd88.tif') as ds:
			a = ds.read(1)
		with rasterio.open(target / 'depth_merged_navd88.tif') as ds:
			after = ds.read(1)
		valid = np.isfinite(before)
		if not (np.all(after[valid] >= a[valid]) and np.all(after[valid] <= before[valid])):
			raise ValueError(f'{identifier}: buffered depth is not between A and the original')
		r, c = np.nonzero(after < before)
		lat, lon = t.f + (r + .5) * t.e, t.c + (c + .5) * t.a
		n, e = geodesy.to_local(lat, lon)
		local = [landings.to_local(h['geometry']) for h in items]
		tree = STRtree(local)
		for rr, cc, nn, ee, ll, lo in zip(r, c, n, e, lat, lon):
			key = cell_key(ll, lo)
			values = (float(before[rr, cc]), float(after[rr, cc]))
			if key in unique and unique[key] != values:
				raise ValueError(f'{identifier}: inconsistent buffered overlap at {key}')
			unique[key] = values
			point = Point(ee, nn)
			for k in tree.query(point.buffer(50.)):
				separation = point.distance(local[k])
				if separation <= distance:
					raise ValueError(f'{identifier}: lowered cell still inside {distance:g} m hazard buffer')
				if separation <= 50.:
					near.append(dict(tile=identifier, row=int(rr), col=int(cc), north_m=float(nn), east_m=float(ee),
						lowering_m=values[0] - values[1], hazard_layer=items[k]['layer'], hazard_id=items[k]['id'], distance_m=separation))
		prov = json.loads((target / od.REPORT_FILE).read_text(encoding='utf-8'))
		summaries.append(dict(prov, tile=identifier))
		for entry in index['tiles']:
			if entry['id'] == identifier:
				entry['outputs'] = [dict(file=f'tiles/{identifier}/{p.name}', sha256=manifest.sha256_file(p), size_bytes=p.stat().st_size)
					for p in sorted(target.iterdir()) if p.is_file()]
		log(f'{identifier}: {len(r)} cells lowered with {distance:g} m buffer', flush=True)
	pack.write_json(destination / 'index.json', index)
	harbor.build_depth(destination, destination / 'env' / 'depth', log=log)
	pd.DataFrame(near, columns=['tile', 'row', 'col', 'north_m', 'east_m', 'lowering_m', 'hazard_layer', 'hazard_id', 'distance_m']).to_csv(
		destination / 'lowered-within-50m-hazard.csv', index=False, lineterminator='\n')
	base_summary = json.loads((base / 'candidate.json').read_text(encoding='utf-8'))
	amounts = [a - b for a, b in unique.values()]
	result = dict(base_summary, hazard_buffer_m=float(distance), base_candidate_sha256=manifest.sha256_file(base / 'candidate.json'),
		cells_lowered=len(unique), max_lowering_m=max(amounts, default=0.), near_hazard_records=len(near), tiles=summaries,
		depth_digest=pack.read_digest(destination / 'env' / 'depth'))
	pack.write_json(destination / 'candidate.json', result)
	return result
