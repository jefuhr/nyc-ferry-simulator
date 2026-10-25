"""Bilinear raster samples on a south-to-north world-frame cell-centre lattice."""
import json
from collections import deque
import math
from pathlib import Path

import numpy as np
import rasterio
from pyproj import Transformer

from ferrydata import geodesy, manifest
from ferrydata.environment import pack


def bilinear(array, transform, x, y):
	col, row = pixel_coordinates(transform, x, y)
	valid = (row >= 0) & (col >= 0) & (row <= array.shape[0]-1) & (col <= array.shape[1]-1)
	r0 = np.floor(np.clip(row, 0, array.shape[0]-1)).astype(int)
	c0 = np.floor(np.clip(col, 0, array.shape[1]-1)).astype(int)
	r1 = np.minimum(r0+1, array.shape[0]-1)
	c1 = np.minimum(c0+1, array.shape[1]-1)
	dy, dx = row-r0, col-c0
	a, b, c, d = array[r0,c0], array[r0,c1], array[r1,c0], array[r1,c1]
	result = a*(1-dy)*(1-dx) + b*(1-dy)*dx + c*dy*(1-dx) + d*dy*dx
	return np.where(valid, result, np.nan)


def sample_lattice(grid, sampler):
	# Chunk by row to bound memory even when a later region is much larger.
	out = np.empty((grid['rows'], grid['cols']), dtype='<f4')
	east = grid['origin_east_m'] + np.arange(grid['cols'])*grid['cell_m']
	for r in range(grid['rows']):
		north = np.full(grid['cols'], grid['origin_north_m'] + r*grid['cell_m'])
		out[r] = sampler(north, east)
	return out


def pixel_coordinates(transform, x, y):
	# Explicit affine arithmetic avoids deprecated ndarray multiplication dispatch.
	inverse = ~transform
	x, y = np.asarray(x), np.asarray(y)
	return inverse.a*x + inverse.b*y + inverse.c - .5, inverse.d*x + inverse.e*y + inverse.f - .5


def largest_rectangle(mask):
	"""Largest all-true cell rectangle, exclusive end indices; stable first winner on ties."""
	heights = np.zeros(mask.shape[1], dtype=int)
	best, bounds = 0, None
	for row, values in enumerate(mask):
		heights = np.where(values, heights+1, 0)
		stack = []
		for col in range(len(heights)+1):
			height = int(heights[col]) if col < len(heights) else 0
			start = col
			while stack and stack[-1][1] > height:
				left, h = stack.pop()
				area = h*(col-left)
				if area > best:
					best, bounds = area, (row-h+1, left, row+1, col)
				start = left
			if not stack or stack[-1][1] < height:
				stack.append((start, height))
	if bounds is None:
		raise ValueError('raster has no interior sampling rectangle')
	return bounds


def _land_mask(source_dir, grid):
	import shapely
	from shapely.geometry import shape
	payload = json.loads((source_dir / 'land_areas.geojson').read_text())
	land = shapely.union_all([shape(f['geometry']) for f in payload['features']])
	def sample(north, east):
		lat, lon = geodesy.from_local(north, east)
		return shapely.intersects_xy(land, lon, lat)
	return sample_lattice(grid, sample).astype(bool)


def _clusters(mask, grid, source_dir):
	from shapely.geometry import Point, shape
	from ferrydata.landings import to_local
	features = json.loads((source_dir / 'shoreline_constructions.geojson').read_text())['features']
	structures = [(to_local(shape(f['geometry'])), f['properties'].get('feature_class', 'structure')) for f in features]
	pending = set(zip(*np.where(mask)))
	clusters = []
	while pending:
		seed = min(pending)
		pending.remove(seed)
		queue, cells = deque([seed]), []
		while queue:
			r, c = queue.popleft()
			cells.append((r,c))
			for adjacent in ((r-1,c), (r+1,c), (r,c-1), (r,c+1)):
				if adjacent in pending:
					pending.remove(adjacent)
					queue.append(adjacent)
		north = grid['origin_north_m'] + float(np.mean([r for r,c in cells]))*grid['cell_m']
		east = grid['origin_east_m'] + float(np.mean([c for r,c in cells]))*grid['cell_m']
		point = Point(east, north)
		nearby = sorted((g.distance(point), label) for g,label in structures)
		context = f'{nearby[0][1]} at {nearby[0][0]:.1f} m' if nearby and nearby[0][0] <= 100 else 'open water; no charted shoreline construction within 100 m'
		clusters.append(dict(north_m=north, east_m=east, cells=len(cells), nearby=context))
	return clusters


def build(slice_, source_dir, destination):
	source_dir, destination = Path(source_dir), Path(destination)
	source = source_dir / 'depth_merged_navd88.tif'
	chart_file = source_dir / 'depth_chart_navd88.tif'
	metadata_file = source.with_suffix('.json')
	metadata = json.loads(metadata_file.read_text())
	corners = [geodesy.to_local(*p) for p in slice_.corners().values()]
	n0, e0 = [math.floor(min(p[i] for p in corners) / 5)*5 for i in (0, 1)]
	n1, e1 = [math.ceil(max(p[i] for p in corners) / 5)*5 for i in (0, 1)]
	grid = dict(id='slice', file='slice.f32', origin_north_m=float(n0), origin_east_m=float(e0), cell_m=5.,
		rows=int((n1-n0)/5)+1, cols=int((e1-e0)/5)+1, nodata='NaN')
	with rasterio.open(source) as ds:
		if ds.tags().get('vertical_datum', ds.tags().get('VERTICAL_DATUM')) != 'NAVD88':
			raise ValueError('merged raster must declare NAVD88')
		array = ds.read(1).astype(float)
		if ds.nodata is not None:
			array[array == ds.nodata] = np.nan
		project = Transformer.from_crs('EPSG:4326', ds.crs, always_xy=True)
		def coordinates(north, east):
			lat, lon = geodesy.from_local(north, east)
			return project.transform(lon, lat)
		def domain(north, east):
			col, row = pixel_coordinates(ds.transform, *coordinates(north, east))
			return (row >= 0) & (col >= 0) & (row <= ds.height-1) & (col <= ds.width-1)
		mask = sample_lattice(grid, domain).astype(bool)
		padding_cells = int((~mask).sum())
		r0, c0, r1, c1 = largest_rectangle(mask)
		grid.update(origin_north_m=grid['origin_north_m']+r0*5, origin_east_m=grid['origin_east_m']+c0*5,
			rows=r1-r0, cols=c1-c0)
		values = sample_lattice(grid, lambda n,e: bilinear(array, ds.transform, *coordinates(n,e)))
		crs = ds.crs.to_string()
	unknown_before = np.isnan(values)
	land = _land_mask(source_dir, grid) if unknown_before.any() else np.zeros(values.shape, dtype=bool)
	water_before = unknown_before & ~land
	filled = 0
	if water_before.any():
		with rasterio.open(chart_file) as chart:
			array = chart.read(1).astype(float)
			if chart.nodata is not None:
				array[array == chart.nodata] = np.nan
			project = Transformer.from_crs('EPSG:4326', chart.crs, always_xy=True)
			def sample_chart(north, east):
				lat, lon = geodesy.from_local(north, east)
				x, y = project.transform(lon, lat)
				return bilinear(array, chart.transform, x, y)
			chart_values = sample_lattice(grid, sample_chart)
			fill = water_before & np.isfinite(chart_values)
			values[fill] = chart_values[fill]
			filled = int(fill.sum())
	water_after = np.isnan(values) & ~land
	clusters = _clusters(water_after, grid, source_dir) if water_after.any() else []
	if clusters:
		details = '; '.join(f'{c["cells"]} cells at north_m={c["north_m"]!r}, east_m={c["east_m"]!r} ({c["nearby"]})' for c in clusters)
		raise ValueError(f'{int(water_after.sum())} unfilled water cells after chart fill; clusters: {details}')
	destination.mkdir(parents=True, exist_ok=True)
	values.astype('<f4').tofile(destination / grid['file'])
	grid['sha256'] = manifest.sha256_file(destination / grid['file'])
	result = dict(format='ferrysim-env-depth/1', region=slice_.name, datum='NAVD88',
		value='bed elevation, metres, positive up (water is negative)', grids=[grid], outside_depth_m=20.,
		sources=[dict(name=source.name, sha256=manifest.sha256_file(source), note=f'CUDEM merged with ENC charted depths near berths; horizontal {crs}; WGS84 cell centres transformed with pyproj always_xy; largest interior world-aligned rectangle, no edge extension; {padding_cells} initial padding cells excluded'),
			dict(name=metadata_file.name, sha256=manifest.sha256_file(metadata_file), note='Datum chain: CUDEM NAVD88; ENC MLLW converted using The Battery MLLW minus NAVD88 station datum; no VDatum surface correction'),
			dict(name=chart_file.name, sha256=manifest.sha256_file(chart_file), note=f'Chart-derived NAVD88 water-hole fill: {filled} cells; {int(water_before.sum())} water NaNs before, {int(water_after.sum())} after')])
	pack.write_json(destination / 'manifest.json', result)
	pack.write_digest(destination, pack.depth_digest(destination))
	stats = dict(crs=crs, cells=int(values.size), finite_area_m2=int(np.isfinite(values).sum())*25,
		water_area_m2=int((values < 0).sum())*25, nan_area_m2=int(np.isnan(values).sum())*25,
		min_m=float(np.nanmin(values)), max_m=float(np.nanmax(values)), datum_chain=metadata,
		excluded_padding_cells=padding_cells, nan_cells_before=int(unknown_before.sum()), water_nan_before=int(water_before.sum()),
		chart_filled_cells=filled, water_nan_after=int(water_after.sum()), water_nan_clusters=clusters)
	return result, stats
