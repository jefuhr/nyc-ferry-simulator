"""Evidence for the harbor depth set, written by validate --region harbor:

- the cell size: the bed of a 5 m and a 10 m lattice (each centre from its owner's raster, as the build samples it)
  against the merged rasters' own bilinear bed, in front of every landing's primary face and over the harbor's water;
- the seams: the built set sampled 1 mm and 1e-9 m either side of every shared edge, and the rasters' own difference
  at the shared centres, which the last cell before a seam spreads out;
- that the built set is what the lattice rule gives;
- the subordinate current stations' series against what CO-OPS publishes for each (validation.subordinate_check).
"""
import json
from pathlib import Path

import numpy as np

from ferrydata import tiles
from ferrydata.environment import harbor, pack, validation
from ferrydata.environment.sources import Inputs

CELLS = (5., 10.)
# In front of each landing's primary face: out along its outward normal, and along the face either side of its midpoint.
LANDING_OUT_M = (2., 5., 10., 15., 20., 30., 50.)
LANDING_ALONG_M = (-10., 0., 10.)
# Over the water: a lattice of points 97.3 m apart, off every depth lattice, where the raster's bed is below -2 m NAVD88.
WATER_SPACING_M = 97.3
WATER_BELOW_M = -2.


def sources(north, east, active):
	"""harbor.source for arrays of lattice centres: (i, j) arrays of the tile whose raster gives each."""
	i, j = np.floor(north / tiles.SIZE_M).astype(int), np.floor(east / tiles.SIZE_M).astype(int)
	for k in np.nonzero([(a, b) not in active for a, b in zip(i.tolist(), j.tolist())])[0]:
		tile = harbor.source(float(north[k]), float(east[k]), active)
		i[k], j[k] = tile if tile is not None else (10 ** 6, 10 ** 6)
	return i, j


def centres(north, east, active, rasters):
	"""The bed at lattice centres, each from its source tile's raster (NaN where no active tile holds it)."""
	values = np.full(north.shape, np.nan)
	i, j = sources(north, east, active)
	for tile in sorted(set(zip(i.tolist(), j.tolist()))):
		if tile in active:
			mask = (i == tile[0]) & (j == tile[1])
			values[mask] = rasters[tile].sample(north[mask], east[mask]).astype('<f4')
	return values


def lattice_bed(north, east, cell, active, rasters):
	"""The bed a depth set on a global lattice of this cell size gives at points: bilinear between the four surrounding
	centres, each from its owner's raster, stored as float32 (DepthSet's arithmetic, on the cell around the point)."""
	r, c = np.floor(north / cell), np.floor(east / cell)
	up, across = north / cell - r, east / cell - c
	sw = centres(r * cell, c * cell, active, rasters)
	se = centres(r * cell, (c + 1) * cell, active, rasters)
	nw = centres((r + 1) * cell, c * cell, active, rasters)
	ne = centres((r + 1) * cell, (c + 1) * cell, active, rasters)
	lower = sw + across * (se - sw)
	upper = nw + across * (ne - nw)
	return lower + up * (upper - lower)


def raster_bed(north, east, active, rasters):
	"""The merged rasters' own bilinear bed at points, from the tile that owns each point."""
	return centres(north, east, active, rasters)


def statistics(errors):
	errors = np.abs(errors[np.isfinite(errors)])
	if not errors.size:
		return dict(points=0)
	return dict(points=int(errors.size), rms_m=float(np.sqrt(np.mean(errors ** 2))), p95_m=float(np.percentile(errors, 95)),
		p99_m=float(np.percentile(errors, 99)), max_m=float(errors.max()))


def landing_points(root, active):
	"""Every landing's points in front of its primary face, from the owning tile's landing_faces.geojson."""
	result = []
	for i, j in active:
		path = Path(root) / 'tiles' / harbor.tile_id(i, j) / 'landing_faces.geojson'
		if not path.is_file():
			continue
		for feature in json.loads(path.read_text(encoding='utf-8'))['features']:
			p = feature['properties']
			mid, normal = p['face_midpoint'], p['outward_normal_ne']
			tangent = (-normal[1], normal[0])
			points = [(mid['north_m'] + out * normal[0] + along * tangent[0], mid['east_m'] + out * normal[1] + along * tangent[1], out)
				for out in LANDING_OUT_M for along in LANDING_ALONG_M]
			result.append(dict(stop_id=p['stop_id'], stop_name=p['stop_name'], tile=harbor.tile_id(i, j), points=points))
	return sorted(result, key=lambda x: (len(x['stop_id']), x['stop_id']))


def water_points(active, rasters):
	north, east = [], []
	for i, j in active:
		n = tiles.SIZE_M * i + WATER_SPACING_M / 2 + np.arange(int(tiles.SIZE_M / WATER_SPACING_M)) * WATER_SPACING_M
		e = tiles.SIZE_M * j + WATER_SPACING_M / 2 + np.arange(int(tiles.SIZE_M / WATER_SPACING_M)) * WATER_SPACING_M
		nn, ee = np.meshgrid(n, e, indexing='ij')
		north.append(nn.ravel())
		east.append(ee.ravel())
	north, east = np.concatenate(north), np.concatenate(east)
	bed = raster_bed(north, east, set(active), rasters)
	water = np.isfinite(bed) & (bed < WATER_BELOW_M)
	return north[water], east[water], bed[water]


def seams(depth, active, rasters, step=1.):
	"""Along every edge two active tiles share: the built set's bed either side of it, and the rasters' difference at
	the shared centres."""
	rows = []
	cell = depth.grids[0]['cell_m']
	for i, j in active:
		for di, dj in ((1, 0), (0, 1)):
			if (i + di, j + dj) not in active:
				continue
			along = tiles.SIZE_M * (j if di else i) + np.arange(0., tiles.SIZE_M + step / 2, step)
			fixed = tiles.SIZE_M * ((i + 1) if di else (j + 1))
			def at(offset, positions=along):
				return np.array([depth.sample(fixed + offset, x).bed if di else depth.sample(x, fixed + offset).bed for x in positions])
			below, above = at(-1e-3), at(1e-3)
			nearest_below, nearest_above = at(-1e-9), at(1e-9)
			# What the slope on each side gives over that millimetre: inside the cells next to the seam the bilinear bed is
			# linear across it, so a step at the seam would remain in the difference.
			slopes = (below - at(-.501)) / .5 + (at(.501) - above) / .5
			# The rasters at the shared centres: the south (west) tile's own against the owner's (the north or east tile's).
			grid_positions = tiles.SIZE_M * (j if di else i) + np.arange(int(tiles.SIZE_M / cell) + 1) * cell
			n = np.full(grid_positions.shape, float(fixed)) if di else grid_positions
			e = grid_positions if di else np.full(grid_positions.shape, float(fixed))
			own = rasters[(i, j)].sample(n, e).astype('<f4').astype(float)
			other = rasters[(i + di, j + dj)].sample(n, e).astype('<f4').astype(float)
			rows.append(dict(seam=f'{harbor.tile_id(i, j)} / {harbor.tile_id(i + di, j + dj)}', direction='north' if di else 'east',
				points=int(along.size), step_1mm_max_m=float(np.max(np.abs(above - below))),
				step_1e9_max_m=float(np.max(np.abs(nearest_above - nearest_below))),
				excess_1mm_max_m=float(np.max(np.abs(above - below - 1e-3 * slopes))),
				raster_difference_max_m=float(np.max(np.abs(own - other)))))
	return rows


def run(destination, transit=None):
	destination = Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	root = harbor.tiles_root()
	active = harbor.active_tiles(root)
	rasters = harbor.Rasters(root, active)
	depth = pack.load_depth(root / 'env' / 'depth')
	built = depth.grids[0]['cell_m']
	print(f'[validate] harbor: {len(depth.grids)} grids of {built:g} m cells; landings, water and seams', flush=True)
	landings_ = []
	for landing in landing_points(root, active):
		n = np.array([p[0] for p in landing['points']])
		e = np.array([p[1] for p in landing['points']])
		truth = raster_bed(n, e, set(active), rasters)
		row = dict(stop_id=landing['stop_id'], stop_name=landing['stop_name'], tile=landing['tile'], points=len(n),
			raster_min_m=float(np.nanmin(truth)), raster_max_m=float(np.nanmax(truth)))
		for cell in CELLS:
			bed = lattice_bed(n, e, cell, set(active), rasters)
			error = bed - truth
			row[f'max_{cell:g}_m'] = float(np.nanmax(np.abs(error)))
			row[f'rms_{cell:g}_m'] = float(np.sqrt(np.nanmean(error ** 2)))
			# Within 10 m of the face, where a ferry lies alongside.
			near = np.array([p[2] <= 10. for p in landing['points']])
			row[f'max_near_{cell:g}_m'] = float(np.nanmax(np.abs(error[near])))
		landings_.append(row)
	north, east, truth = water_points(active, rasters)
	water = {f'{cell:g}': statistics(lattice_bed(north, east, cell, set(active), rasters) - truth) for cell in CELLS}
	# The built set against the lattice rule it was built by, at the same points.
	built_bed = np.array([depth.sample(n_, e_).bed for n_, e_ in zip(north, east)])
	consistency = float(np.max(np.abs(built_bed - lattice_bed(north, east, built, set(active), rasters))))
	seam_rows = seams(depth, active, rasters)
	print('[validate] harbor: subordinate series against the published events', flush=True)
	currents, _ = harbor.stations(root, active)
	days = sorted(p.name for p in (root / 'env' / 'days').iterdir() if p.is_dir())
	subordinates = validation.subordinate_check(Inputs(lambda *a, **k: None), [s['id'] for s in currents if s['type'] == 'S'], days, destination)
	sizes = {f'{cell:g}': dict(grid_bytes=int(len(active) * 4 * (tiles.SIZE_M / cell + 1) ** 2), grids=len(active),
		centres_per_grid=int((tiles.SIZE_M / cell + 1) ** 2)) for cell in CELLS}
	summary = dict(built_cell_m=built, sizes=sizes, landings=landings_, water=water, water_points=int(north.size),
		built_against_lattice_rule_max_m=consistency, seams=seam_rows, subordinates=subordinates)
	pack.write_json(destination / 'validation.json', summary)
	write(summary, destination)
	return dict(seams=len(seam_rows), destination=str(destination))


def write(summary, destination):
	lines = ['# Harbor depth set: cell size and seams', '',
		f'The built set has {len(summary["seams"])} shared edges between {next(iter(summary["sizes"].values()))["grids"]} tile grids of '
		f'{summary["built_cell_m"]:g} m cells. Every lattice centre takes its bed from the merged raster of the tile that owns it; '
		'"raster" below is that raster\'s own bilinear bed at the point (its pixels are about 2.6 by 3.4 m).', '',
		'## Sizes', '', '| Cell | Grid bytes (all tiles) | Centres per grid |', '|---|---:|---:|']
	for cell, s in summary['sizes'].items():
		lines.append(f'| {cell} m | {s["grid_bytes"]:,} | {s["centres_per_grid"]:,} |')
	lines += ['', '## Over the water', '',
		f'{summary["water_points"]:,} points {WATER_SPACING_M} m apart, off both lattices, where the raster\'s bed is below {WATER_BELOW_M:g} m NAVD88: '
		'the lattice bed minus the raster bed.', '', '| Cell | RMS m | 95th percentile m | 99th percentile m | Largest m |', '|---|---:|---:|---:|---:|']
	for cell, s in summary['water'].items():
		lines.append(f'| {cell} m | {s["rms_m"]:.3f} | {s["p95_m"]:.3f} | {s["p99_m"]:.3f} | {s["max_m"]:.2f} |')
	lines += ['', '## In front of the landings', '',
		f'Per landing, {len(LANDING_OUT_M) * len(LANDING_ALONG_M)} points out from its primary face ({", ".join(f"{x:g}" for x in LANDING_OUT_M)} m along '
		f'its outward normal, at {", ".join(f"{x:g}" for x in LANDING_ALONG_M)} m along the face): the lattice bed minus the raster bed. '
		'"Near" is within 10 m of the face, where a ferry lies alongside.', '',
		'| Landing | Tile | Raster bed m | 5 m: largest / RMS / near | 10 m: largest / RMS / near |', '|---|---|---|---|---|']
	for row in summary['landings']:
		lines.append(f'| {row["stop_id"]} {row["stop_name"]} | {row["tile"]} | {row["raster_min_m"]:.2f} to {row["raster_max_m"]:.2f} | '
			f'{row["max_5_m"]:.2f} / {row["rms_5_m"]:.2f} / {row["max_near_5_m"]:.2f} | {row["max_10_m"]:.2f} / {row["rms_10_m"]:.2f} / {row["max_near_10_m"]:.2f} |')
	lines += ['', '## Seams', '',
		'Along each shared edge every metre: the built set\'s bed 1 mm either side; that difference less what the bed\'s slope on '
		'each side gives over its millimetre (inside the cells next to the seam the bilinear bed is linear across it, so a step '
		'would remain); the difference 1e-9 m either side; and the two tiles\' rasters at the shared centres, whose difference '
		'the last cell before the seam spreads over its width.', '',
		'| Seam | Points | Difference over 2 mm, largest m | Less the slopes, largest m | Over 2e-9 m, largest m | Rasters\' difference, largest m |',
		'|---|---:|---:|---:|---:|---:|']
	for row in summary['seams']:
		lines.append(f'| {row["seam"]} | {row["points"]:,} | {row["step_1mm_max_m"]:.2e} | {row["excess_1mm_max_m"]:.2e} | '
			f'{row["step_1e9_max_m"]:.2e} | {row["raster_difference_max_m"]:.3f} |')
	lines += ['', f'The built set against the lattice rule at the water points, largest difference: {summary["built_against_lattice_rule_max_m"]:.2e} m.', '',
		'## Subordinate current stations', '', f'{len(summary["subordinates"])} stations over every built day: subordinates.md.', '']
	(Path(destination) / 'validation.md').write_text('\n'.join(lines), encoding='utf-8')
