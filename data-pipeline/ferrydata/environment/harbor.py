"""The harbor region: harbor tiles v1 (ferrydata.tiles) as one ferrysim-env/1 region, for runs anywhere in the harbor.

- Depth: one grid per active tile, its 4 km square with both edges on one world lattice, so neighbouring grids share
  their edge rows and columns of centres. A centre's bed comes from the merged raster of the tile that owns it (the
  half-open squares of ferrydata.tiles), or, where that tile is not active, of the first active tile in (i, j) order
  whose square holds it. So every grid that holds a centre holds the same value there, as the depth-set rule for
  several grids requires, and the bed is continuous across every seam.
- Stations: every current prediction station the tiles list (inside a square grown by 250 m) but the weak and variable
  ones, which have no predictions; subordinate stations by NOAA's reference-station method. The water levels and the
  wind follow the slice's rules, with the gauges the tiles list and the region's named ones.
- No current field yet: the days name none, so the core blends the stations by inverse distance squared everywhere.
"""
import json
import math
from pathlib import Path

import numpy as np
import rasterio
from pyproj import Transformer

from ferrydata import coops, geodesy, manifest, paths, tiles
from ferrydata.environment import depth as depth_module, pack, series
from ferrydata.environment.sources import Inputs

REGION = 'harbor'
# World-frame spacing of the tile grids' centres, m: the slice's 5 m. Against the rasters' own bed, a 10 m lattice is
# about 2.5 times further off over the water and up to 0.8 m off within 10 m of a landing face, for a quarter of the
# bytes (validate --region harbor writes the evidence).
CELL_M = 5.
OUTSIDE_DEPTH_M = 20.
# The slice's wind rule: the first station with observations, in the coordinator-approved order.
WIND_ORDER = ('8518750', '8516945', '8530973', '8519483')
DIMOND_REEF_NOTE = ('Dimond Reef NYH1919 uses the API-default bin 9; CO-OPS references its bins to the seabed (depthType S), so its '
	'bin_depth_m 9.4 is height above the bed, about 4 to 5 m below the surface there and the shallowest of its three bins.')


def tiles_root():
	return paths.processed_dir(REGION)


def active_tiles(root=None):
	"""(i, j) of every active tile in harbor tiles v1, in (i, j) order; refuses a tile whose build failed or is missing."""
	root = Path(root or tiles_root())
	index = root / 'index.json'
	if not index.is_file():
		raise ValueError(f'{index} is missing; run python data-pipeline/ferrydata build --harbor first')
	active = []
	for entry in json.loads(index.read_text(encoding='utf-8'))['tiles']:
		if not entry['active']:
			continue
		folder = root / 'tiles' / entry['id']
		if entry.get('build_error') or not (folder / 'depth_merged_navd88.tif').is_file() or not (folder / 'coops_stations.json').is_file():
			raise ValueError(f'{folder}: active tile not built; run python data-pipeline/ferrydata build --harbor first')
		active.append((entry['i'], entry['j']))
	if not active:
		raise ValueError(f'{index}: no active tiles')
	return sorted(active)


def tile_id(i, j):
	return f'tile_{i}_{j}'


def lattice(i, j, cell=CELL_M):
	"""Tile (i, j)'s depth grid: its square from first to last centre, both edges included."""
	count = tiles.SIZE_M / cell
	if count != math.floor(count):
		raise ValueError(f'cell {cell} m does not divide the {tiles.SIZE_M} m tile')
	return dict(id=tile_id(i, j), file=tile_id(i, j) + '.f32', origin_north_m=float(tiles.SIZE_M * i), origin_east_m=float(tiles.SIZE_M * j),
		cell_m=float(cell), rows=int(count) + 1, cols=int(count) + 1, nodata='NaN')


def source(north, east, active):
	"""The tile whose raster gives the bed at a world centre: the tile that owns it if active, else the first active tile
	(in (i, j) order) whose closed square holds it, else None."""
	owner = (math.floor(north / tiles.SIZE_M), math.floor(east / tiles.SIZE_M))
	if owner in active:
		return owner
	for i, j in sorted(active):
		if tiles.SIZE_M * i <= north <= tiles.SIZE_M * (i + 1) and tiles.SIZE_M * j <= east <= tiles.SIZE_M * (j + 1):
			return (i, j)
	return None


class Raster:
	"""A tile's merged NAVD88 raster, sampled at world-frame points the slice's way: geodesy.from_local, pyproj from
	WGS84 to the raster's CRS (always_xy), then strict bilinear between pixel centres (NaN at nodata and off the raster)."""

	def __init__(self, path):
		self.path = Path(path)
		with rasterio.open(self.path) as ds:
			if ds.tags().get('vertical_datum', ds.tags().get('VERTICAL_DATUM')) != 'NAVD88':
				raise ValueError(f'{self.path}: merged raster must declare NAVD88')
			self.array = ds.read(1).astype('<f4')
			if ds.nodata is not None:
				self.array[self.array == ds.nodata] = np.nan
			self.transform = ds.transform
			self.crs = ds.crs.to_string()
			self.project = Transformer.from_crs('EPSG:4326', ds.crs, always_xy=True)

	def sample(self, north, east):
		lat, lon = geodesy.from_local(np.asarray(north, dtype=float), np.asarray(east, dtype=float))
		return depth_module.bilinear(self.array, self.transform, *self.project.transform(lon, lat))


class Rasters:
	"""The active tiles' rasters, each read once when first needed."""

	def __init__(self, root, active):
		self.root, self.active, self.loaded = Path(root), set(active), {}

	def __getitem__(self, tile):
		if tile not in self.loaded:
			self.loaded[tile] = Raster(self.root / 'tiles' / tile_id(*tile) / 'depth_merged_navd88.tif')
		return self.loaded[tile]


def grid_values(grid, active, rasters):
	"""The bed at every centre of a tile grid, each from the raster of the tile source() names for it."""
	north = grid['origin_north_m'] + np.arange(grid['rows']) * grid['cell_m']
	east = grid['origin_east_m'] + np.arange(grid['cols']) * grid['cell_m']
	n, e = np.meshgrid(north, east, indexing='ij')
	owners = {}
	# Inside the square and on its south and west edges a centre is the tile's own; on its north and east edges it
	# belongs to the neighbour there, or, where that is not active, to source()'s choice. Group the centres by tile.
	rows, cols = np.floor(n / tiles.SIZE_M).astype(int), np.floor(e / tiles.SIZE_M).astype(int)
	for owner in sorted(set(zip(rows.ravel().tolist(), cols.ravel().tolist()))):
		mask = (rows == owner[0]) & (cols == owner[1])
		if owner in active:
			owners.setdefault(owner, np.zeros(n.shape, dtype=bool))
			owners[owner] |= mask
			continue
		for r, c in zip(*np.nonzero(mask)):
			chosen = source(float(n[r, c]), float(e[r, c]), active)
			owners.setdefault(chosen, np.zeros(n.shape, dtype=bool))[r, c] = True
	values = np.full(n.shape, np.nan, dtype='<f4')
	for tile in sorted(owners):
		mask = owners[tile]
		values[mask] = rasters[tile].sample(n[mask], e[mask])
	return values, {tile_id(*tile): int(mask.sum()) for tile, mask in sorted(owners.items())}


def build_depth(root, destination, cell=CELL_M, log=print):
	"""Writes the harbor depth set: one grid per active tile, shared edges identical by construction, checked by the
	depth-set rule before the digest is written."""
	root, destination = Path(root), Path(destination)
	active = active_tiles(root)
	rasters = Rasters(root, active)
	destination.mkdir(parents=True, exist_ok=True)
	for stale in destination.glob('*.f32'):
		stale.unlink()
	grids, sources, stats = [], [], {}
	for i, j in active:
		grid = lattice(i, j, cell)
		values, by_source = grid_values(grid, set(active), rasters)
		unknown = int(np.isnan(values).sum())
		if unknown:
			raise ValueError(f'{grid["id"]}: {unknown} centres have no bed (nodata in a merged raster next to them); '
				'harbor tiles v1 have none inside their squares, so a rebuild of the tiles has changed')
		values.astype('<f4').tofile(destination / grid['file'])
		grid['sha256'] = manifest.sha256_file(destination / grid['file'])
		grids.append(grid)
		stats[grid['id']] = dict(centres=int(values.size), sources=by_source, min_m=float(values.min()), max_m=float(values.max()),
			water_area_m2=float((values < 0).sum() * cell * cell))
		log(f'  {grid["id"]}: {grid["rows"]} x {grid["cols"]} centres, bed {values.min():.2f} to {values.max():.2f} m', flush=True)
	for i, j in active:
		folder = root / 'tiles' / tile_id(i, j)
		raster = rasters[(i, j)]
		sources.append(dict(name=f'tiles/{tile_id(i, j)}/depth_merged_navd88.tif', sha256=manifest.sha256_file(raster.path),
			note=f'harbor tiles v1 merged raster (CUDEM with ENC charted depths near berths), horizontal {raster.crs}; gives the '
				'centres this tile owns (its half-open 4 km square), in its own grid and on its neighbours\' edges; WGS84 '
				'centres transformed with pyproj always_xy; bilinear, no extrapolation'))
		metadata = folder / 'depth_merged_navd88.json'
		sources.append(dict(name=f'tiles/{tile_id(i, j)}/depth_merged_navd88.json', sha256=manifest.sha256_file(metadata),
			note='Datum chain: CUDEM NAVD88; ENC MLLW converted using The Battery MLLW minus NAVD88 station datum; no VDatum surface correction'))
	result = dict(format='ferrysim-env-depth/1', region=REGION, datum='NAVD88', value='bed elevation, metres, positive up (water is negative)',
		grids=grids, outside_depth_m=OUTSIDE_DEPTH_M, sources=sources)
	pack.write_json(destination / 'manifest.json', result)
	pack.write_digest(destination, pack.depth_digest(destination))
	pack.verify_depth(destination)
	return result, dict(cell_m=cell, grids=stats, bytes=sum((destination / g['file']).stat().st_size for g in grids))


def stations(root, active):
	"""What the tiles list: current prediction stations (one entry per id), and water-level gauges, each sorted by id."""
	currents, gauges = {}, {}
	for i, j in active:
		listing = json.loads((Path(root) / 'tiles' / tile_id(i, j) / 'coops_stations.json').read_text(encoding='utf-8'))
		for station in listing['stations']:
			target = currents if station['kind'] == 'current_prediction' else gauges if station['kind'] == 'water_level' else None
			if target is None:
				raise ValueError(f'{tile_id(i, j)}: unknown station kind {station["kind"]!r}')
			target.setdefault(station['id'], station)
	return [currents[k] for k in sorted(currents)], [gauges[k] for k in sorted(gauges)]


def seabed_bins(station_ids):
	"""Of these stations, those whose CO-OPS bins are heights above the seabed (depthType S), from the verified station
	listing harbor tiles v1 read."""
	listing = json.loads(tiles._cached(coops.SOURCE, 'stations_currentpredictions.json').read_text(encoding='utf-8'))
	return sorted({s['id'] for s in listing['stations'] if s['id'] in station_ids and s.get('depthType') == 'S'})


def gauge_ids(listed):
	"""The slice's rule for gauges, over the harbor's: the region's named gauges (The Battery first), then any other
	gauge a tile lists, in id order. NAVD88 is checked when the day is built."""
	named = list(tiles.extent().water_level_stations)
	return named + sorted(g['id'] for g in listed if g['id'] not in named)


def build(days, log=print):
	if not days or any(day not in series.DAYS for day in days):
		raise ValueError('choose one or more of the 28 AIS scenario days')
	root = tiles_root()
	active = active_tiles(root)
	env = root / 'env'
	inputs = Inputs(log)
	current_listing, gauge_listing = stations(root, active)
	weak = [s for s in current_listing if s['type'] == 'W']
	harmonic = [s for s in current_listing if s['type'] == 'H']
	subordinates = [s['id'] for s in current_listing if s['type'] == 'S']
	unknown = [s['id'] for s in current_listing if s['type'] not in ('H', 'S', 'W')]
	if unknown:
		raise ValueError(f'current stations of unknown CO-OPS type: {unknown}')
	water_stations, skipped_datums = [], []
	for station_id in gauge_ids(gauge_listing):
		station, datums = inputs.station(station_id), inputs.datums(station_id)
		if any(d['name'] == 'NAVD88' for d in datums.get('datums', [])):
			water_stations.append(station)
		else:
			skipped_datums.append(f'Water gauge {station_id} ({station["name"]}) omitted: no NAVD88 datum; no datum substitution.')
	log(f'[build] harbor depth: {len(active)} tile grids of {CELL_M:g} m cells', flush=True)
	depth_manifest, depth_stats = build_depth(root, env / 'depth', CELL_M, log)
	depth_hash = pack.read_digest(env / 'depth')
	omitted = ', '.join(f'{s["id"]} ({s["name"]})' for s in weak)
	seabed = [sid for sid in seabed_bins({s['id'] for s in harmonic}) if sid != 'NYH1919']
	daily = {}
	for day in sorted(set(days)):
		log(f'[build] {REGION}/{day}', flush=True)
		week = next(w for w in series.WEEKS if 0 <= (series.epoch(day) - series.epoch(w)).days < 7)
		notes = list(skipped_datums) + [
			DIMOND_REEF_NOTE,
			'Current stations are NOAA predictions (harmonic, and subordinate by the reference-station method), not observed '
			'velocity. The harbor has no current field yet: the core blends all of the day\'s current stations by inverse '
			'distance squared everywhere (StationBlendCurrentField), across land as well as along the channels.',
			'Current stations are every current prediction station harbor tiles v1 list, inside a tile\'s square grown by 250 m'
			+ (f'; weak and variable stations {omitted} are omitted because CO-OPS publishes no predictions for them.' if weak else '.'),
			'Wind sensor height is unknown; gusts are preserved but not applied by the v1 core.',
		] + ([f'{", ".join(seabed)} also reference their bins to the seabed (depthType S in the CO-OPS station listing): their '
			'bin_depth_m is the bin\'s height above the bed, and the API-default bin each uses is its shallowest.'] if seabed else [])
		station_metadata = dict(current_stations=[], water_level_stations=[], wind_stations=[])
		rows = dict(currents=[], water_levels=[], wind=[])
		stats = dict(currents={}, water_levels={}, wind={})
		for station in harmonic:
			metadata, current, station_notes, peaks = inputs.current(station, day, weekly=True)
			station_metadata['current_stations'].append(metadata)
			rows['currents'].extend(current)
			notes.extend(station_notes)
			stats['currents'][station['id']] = dict(rows=len(current), first_s=current[0][1], last_s=current[-1][1], **peaks)
		for station_id in subordinates:
			metadata, derivation, current, velocity, _ = inputs.subordinate_current(station_id, day)
			station_metadata['current_stations'].append(dict(metadata, derivation=derivation))
			rows['currents'].extend(current)
			notes.append(f'{station_id}: subordinate prediction from {derivation["reference_station"]} bin {derivation["reference_bin"]} '
				'by NOAA\'s reference-station method; offsets, ratios and sources in its derivation.')
			stats['currents'][station_id] = dict(rows=len(current), first_s=current[0][1], last_s=current[-1][1],
				bin=None, depth_m=None, flood=float(velocity.max()) if velocity.max() > 0 else None,
				ebb=float(-velocity.min()) if velocity.min() < 0 else None)
		station_metadata['current_stations'].sort(key=lambda s: s['id'])
		for station in water_stations:
			metadata, levels, station_notes, water_stats = inputs.water_levels(station, day, week)
			notes.extend(station_notes)
			if metadata:
				station_metadata['water_level_stations'].append(metadata)
				rows['water_levels'].extend(levels)
				stats['water_levels'][station['id']] = water_stats
		if not station_metadata['water_level_stations'] or station_metadata['water_level_stations'][0]['id'] != '8518750':
			raise ValueError(f'{day}: required reference water gauge The Battery is unavailable')
		for station_id in WIND_ORDER:
			station = inputs.station(station_id)
			metadata, wind, station_notes, wind_stats = inputs.wind(station, day, week)
			notes.extend(station_notes)
			if metadata:
				station_metadata['wind_stations'].append(metadata)
				rows['wind'] = wind
				stats['wind'][station_id] = wind_stats
				notes.append(f'Uniform v1 wind uses {station_id} ({station["name"]}), the first available station in Battery, Kings Point, Robbins Reef, Bergen Point order.')
				if station_id == '8516945':
					notes.append('Kings Point is on Long Island Sound and more exposed than the East River and the Upper Bay; its uniform wind is a spatial proxy.')
				break
		if not station_metadata['wind_stations']:
			notes.append('No wind station has valid observations for this day; empty wind.csv represents still air in the core coverage report.')
		metadata = pack.write_day(root=env, region=REGION, day=day, depth_digest=depth_hash, **station_metadata, **rows, notes=notes)
		day_hash = pack.read_digest(env / 'days' / day)
		daily[day] = dict(digest=day_hash, **stats, notes=metadata['notes'])
		log(f'  day digest {day_hash}', flush=True)
	digests = pack.verify(env)
	result = dict(region=REGION, days=daily, depth_digest=depth_hash, depth=dict(grids=depth_manifest['grids'], outside_depth_m=OUTSIDE_DEPTH_M),
		depth_stats=depth_stats, current_stations=[dict(id=s['id'], name=s['name'], type=s['type'], lat=s['lat'], lon=s['lon'],
			owner_tile=s.get('owner_tile')) for s in current_listing], weak_and_variable_omitted=[s['id'] for s in weak],
		water_level_stations=[s['id'] for s in water_stations], skipped_water_level_stations=skipped_datums,
		new_fetch_count=inputs.downloads, total_raw_files=len(inputs.manifest.files),
		total_pack_bytes=sum(p.stat().st_size for p in env.rglob('*') if p.is_file()))
	write_report(result, paths.REPO_ROOT / 'artifacts' / 'environment-pack' / REGION)
	log(f'[verify] {len(digests)} packs; {result["total_pack_bytes"]} bytes; {inputs.downloads} new fetches', flush=True)
	return result


def write_report(result, destination):
	destination = Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	pack.write_json(destination / 'build-summary.json', result)
	stats = result['depth_stats']
	lines = ['# Harbor environment packs', '',
		f'Region `harbor`, {len(result["days"])} days built in this invocation; every pack present passed offline verification. '
		f'Total environment folder: {result["total_pack_bytes"]:,} bytes, of which {stats["bytes"]:,} are the depth grids (shared by every day). '
		f'New raw downloads in this invocation: {result["new_fetch_count"]}.', '',
		'## Depth', '',
		f'{len(stats["grids"])} grids, one per active tile, {stats["cell_m"]:g} m cells, each its 4 km square with both edges, so '
		'neighbours share their edge rows and columns of centres. A centre takes its bed from the tile that owns it; the counts '
		'below say how many came from each raster. Depth digest: `' + result['depth_digest'] + '`.', '',
		'| Grid | Centres | From (tile: centres) | Bed min / max m | Below NAVD88 0, km² |', '|---|---:|---|---|---:|']
	for grid_id, s in stats['grids'].items():
		lines.append(f'| {grid_id} | {s["centres"]:,} | {", ".join(f"{k}: {v:,}" for k, v in s["sources"].items())} | '
			f'{s["min_m"]:.2f} / {s["max_m"]:.2f} | {s["water_area_m2"] / 1e6:.2f} |')
	lines += ['', '## Stations', '',
		f'Water levels: {", ".join(result["water_level_stations"])}. ' + ' '.join(result['skipped_water_level_stations']), '',
		f'Weak and variable current stations omitted: {", ".join(result["weak_and_variable_omitted"]) or "none"}.', '',
		'| Current station | Name | CO-OPS type | Owner tile |', '|---|---|---|---|']
	for s in result['current_stations']:
		lines.append(f'| {s["id"]} | {s["name"]} | {s["type"]} | {s["owner_tile"] or "outside the grid"} |')
	lines += ['', '## Days', '', '| Day | Current stations | Water rows observed / predicted | Wind station | Digest |', '|---|---:|---|---|---|']
	for day, item in result['days'].items():
		water = list(item['water_levels'].values())
		lines.append(f'| {day} | {len(item["currents"])} | {sum(s["observed"] for s in water)} / {sum(s["predicted"] for s in water)} | '
			f'{", ".join(item["wind"]) or "still air"} | `{item["digest"]}` |')
	(destination / 'report.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
