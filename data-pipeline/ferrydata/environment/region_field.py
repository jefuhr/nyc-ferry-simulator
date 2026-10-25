"""A region's current field: the computation box, its ENC land and bed, and the build that writes its field set.

Raw inputs are read only after their checksums match the committed manifests (the original and harbor sidecars), and
every file used is listed with its SHA-256 in the field's sources.
"""
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import rasterio
import shapely
from pyproj import Transformer
from rasterio import features
from rasterio.transform import Affine
from shapely.geometry import box

from ferrydata import enc, geodesy, manifest, paths
from ferrydata.environment import depth as depth_module, field, pack

# World-frame cell-centre bounds (m) of each region's computation box, and the edges the flood pattern enters and
# leaves by.
REGIONS = {
	'lower-east-river': dict(
		# Every slice current station with water around it; the Upper Bay south past Red Hook, so Buttermilk Channel and
		# the main channel west of Governors Island open into the same bay; the Hudson and both channels past Roosevelt
		# Island's south half. Flood enters from the bay (south) and leaves up the Hudson and toward Hell Gate (north).
		# 10 m cells: the coarsest lattice whose values at the landing faces agree with the next finer one
		# (artifacts/current-field/convergence.md).
		north=(-3500., 8000.), east=(-2600., 6500.), cell=10., inflow='south', outflow='north'),
}
# Only charted land is solid to the current. In these cells every shoreline construction charted as an area is always
# under water (WATLEV 3, mostly old pier remains) or covers and uncovers (WATLEV 4); those above water are charted only
# as lines, the outlines of piers, wharves, seawalls and fenders. A pier charted only by its outline is open water here.
SOLID_LAYERS = ('LNDARE',)


def lattice(region, cell=None):
	"""The region's computation lattice, at its own cell size unless another is given (validation)."""
	box_ = REGIONS[region]
	(n0, n1), (e0, e1), cell = box_['north'], box_['east'], box_['cell'] if cell is None else cell
	rows, cols = int(round((n1 - n0) / cell)) + 1, int(round((e1 - e0) / cell)) + 1
	return field.Lattice(origin_north=n0, origin_east=e0, cell=cell, rows=rows, cols=cols)


def parameters(region):
	box_ = REGIONS[region]
	return field.Parameters(inflow=box_['inflow'], outflow=box_['outflow'])


def _transform(lat):
	# Raster x is east and y is north; row 0 is the southern row of cell centres.
	return Affine(lat.cell, 0., lat.origin_east - lat.cell / 2., 0., lat.cell, lat.origin_north - lat.cell / 2.)


def _lonlat_box(lat, margin_deg=.005):
	north = np.array([lat.origin_north, lat.origin_north + (lat.rows - 1) * lat.cell])
	east = np.array([lat.origin_east, lat.origin_east + (lat.cols - 1) * lat.cell])
	n, e = np.meshgrid(north, east, indexing='ij')
	la, lo = geodesy.from_local(n.ravel(), e.ravel())
	return (float(lo.min()) - margin_deg, float(la.min()) - margin_deg, float(lo.max()) + margin_deg, float(la.max()) + margin_deg)


def _to_world(geometry):
	def project(lonlat):
		north, east = geodesy.to_local(lonlat[:, 1], lonlat[:, 0])
		return np.column_stack([east, north])
	return shapely.transform(geometry, project)


def _verified(m, name, path):
	if not m.verify(name, path):
		raise ValueError(f'{path}: missing or checksum differs from the {m.source} manifests')
	return dict(name=name, sha256=m.files[name]['sha256'])


def land(lat):
	"""Cells whose centre is on ENC land, and the ENC files used."""
	m = manifest.load_combined('noaa-enc')
	catalogue = paths.raw_dir('noaa-enc') / 'ENCProdCat_19115.xml'
	sources = [dict(_verified(m, catalogue.name, catalogue), note='NOAA ENC product catalogue: cells covering the field box')]
	bbox = _lonlat_box(lat)
	cells = enc.catalog_cells(catalogue.read_bytes(), box(*bbox))
	for cell in cells:
		sources.append(dict(_verified(m, f'{cell}.zip', paths.raw_dir('noaa-enc') / f'{cell}.zip'),
			note=f'NOAA ENC cell {cell}: land areas (LNDARE) are solid'))
	layers, _ = enc.read_layers(SimpleNamespace(enc_cells=cells, bbox=bbox), SOLID_LAYERS, m)
	shapes = []
	for name in SOLID_LAYERS:
		for geometry in layers[name].geometry if name in layers else []:
			if geometry is not None and geometry.geom_type in ('Polygon', 'MultiPolygon'):
				shapes.append(_to_world(geometry))
	solid = features.rasterize([(g, 1) for g in shapes], out_shape=(lat.rows, lat.cols), transform=_transform(lat),
		all_touched=False, dtype='uint8').astype(bool) if shapes else np.zeros((lat.rows, lat.cols), bool)
	return solid, sources


def depth_set_bed(depth_dir, north, east):
	"""Bed elevations of a depth set at points, as the core reads them: bilinear between the four cell centres of the
	first grid whose lattice holds the point, NaN outside every grid or next to an unknown cell."""
	manifest_ = json.loads((Path(depth_dir) / 'manifest.json').read_text(encoding='utf-8'))
	values = np.full(north.shape, np.nan)
	for grid in manifest_['grids']:
		bed_ = np.fromfile(Path(depth_dir) / grid['file'], dtype='<f4').reshape(grid['rows'], grid['cols']).astype(float)
		r = (north - grid['origin_north_m']) / grid['cell_m']
		c = (east - grid['origin_east_m']) / grid['cell_m']
		inside = np.isnan(values) & (r >= 0) & (r <= grid['rows'] - 1) & (c >= 0) & (c <= grid['cols'] - 1)
		r0 = np.minimum(np.where(inside, r, 0).astype(np.int64), grid['rows'] - 2)
		c0 = np.minimum(np.where(inside, c, 0).astype(np.int64), grid['cols'] - 2)
		fr, fc = np.where(inside, r - r0, 0.), np.where(inside, c - c0, 0.)
		lower = bed_[r0, c0] + fc * (bed_[r0, c0 + 1] - bed_[r0, c0])
		upper = bed_[r0 + 1, c0] + fc * (bed_[r0 + 1, c0 + 1] - bed_[r0 + 1, c0])
		values = np.where(inside, lower + fr * (upper - lower), values)
	return values


def bed(lat, depth_dir):
	"""Bed elevation (NAVD88 m, positive up) at the cell centres: the region's depth set where it knows the bed, CUDEM
	elsewhere. A revised depth set only needs the field rebuilt."""
	north, east = np.meshgrid(lat.north(), lat.east(), indexing='ij')
	values = depth_set_bed(depth_dir, north.ravel(), east.ravel())
	sources = [dict(name='depth/DIGEST', sha256=pack.read_digest(depth_dir), note='the region\'s depth set (ferrysim-env-depth/1) where it knows the bed')]
	la, lo = geodesy.from_local(north.ravel(), east.ravel())
	m = manifest.load_combined('cudem')
	for name in sorted(n for n in m.files if n.startswith('ncei19_') and n.endswith('.tif')):
		path = paths.raw_dir('cudem') / name
		sources.append(dict(_verified(m, name, path), note='NOAA NCEI CUDEM 1/9 arc-second, outside the depth set'))
		with rasterio.open(path) as ds:
			array = ds.read(1).astype(float)
			if ds.nodata is not None:
				array[array == ds.nodata] = np.nan
			x, y = Transformer.from_crs('EPSG:4326', ds.crs, always_xy=True).transform(lo, la)
			sample = depth_module.bilinear(array, ds.transform, x, y)
		values = np.where(np.isnan(values), sample, values)
	return values.reshape(north.shape), sources


def inputs(region, depth_dir, cell=None):
	"""Lattice, water mask, bed and sources for a region's field, from its depth set, ENC land and CUDEM."""
	lat = lattice(region, cell)
	solid, land_sources = land(lat)
	elevation, bed_sources = bed(lat, depth_dir)
	water = ~solid & np.isfinite(elevation)
	return lat, water, elevation, land_sources + bed_sources


def stored_extent(lat, depth_dir):
	"""The rows and columns of `lat` that cover the depth set's grids: the cells a pack stores."""
	grids = json.loads((Path(depth_dir) / 'manifest.json').read_text(encoding='utf-8'))['grids']
	north0 = min(g['origin_north_m'] for g in grids)
	north1 = max(g['origin_north_m'] + (g['rows'] - 1) * g['cell_m'] for g in grids)
	east0 = min(g['origin_east_m'] for g in grids)
	east1 = max(g['origin_east_m'] + (g['cols'] - 1) * g['cell_m'] for g in grids)
	r0, r1 = int(np.floor((north0 - lat.origin_north) / lat.cell)), int(np.ceil((north1 - lat.origin_north) / lat.cell))
	c0, c1 = int(np.floor((east0 - lat.origin_east) / lat.cell)), int(np.ceil((east1 - lat.origin_east) / lat.cell))
	if r0 < 0 or c0 < 0 or r1 >= lat.rows or c1 >= lat.cols:
		raise ValueError('the depth set reaches beyond the field\'s computation box')
	return r0, r1, c0, c1


def stored_grid(basis, extent, grid_id):
	"""The float32 values and uint8 slots of the cells in extent (r0, r1, c0, c1), as a pack stores them."""
	r0, r1, c0, c1 = extent
	window = (slice(r0, r1 + 1), slice(c0, c1 + 1))
	wet = basis.wet[window]
	values = np.concatenate([basis.axis_north[window][..., None], basis.axis_east[window][..., None], basis.along[window][..., None],
		basis.cross[window][..., None], basis.slot_weights[window]], axis=-1)
	values[~wet] = 0.
	slots = basis.slots[window].copy()
	slots[~wet] = field.EMPTY
	lat = basis.lattice
	return dict(id=grid_id, origin_north_m=lat.origin_north + r0 * lat.cell, origin_east_m=lat.origin_east + c0 * lat.cell,
		cell_m=lat.cell, values=values.astype('<f4'), slots=slots)


def parameters_record(region, parameters):
	box_ = REGIONS[region]
	return dict(cell_m=box_['cell'], computation_north_m=list(box_['north']), computation_east_m=list(box_['east']),
		inflow=parameters.inflow, outflow=parameters.outflow, conveyance_exponent=parameters.conveyance_exponent,
		min_depth_m=parameters.min_depth_m, smoothing_m=parameters.smoothing_m, shelter_threshold=parameters.shelter_threshold,
		depth_exponent=parameters.depth_exponent, depth_smoothing_m=parameters.depth_smoothing_m, cross_taper_m=parameters.cross_taper_m,
		station_disk_m=parameters.station_disk_m, station_reach_m=parameters.station_reach_m, across_reach_m=parameters.across_reach_m,
		tolerance=parameters.tolerance)


def build(region, root, stations, log=print):
	"""Builds the region's basis from its depth set (root/depth), ENC land and CUDEM and the current stations (dicts
	with id, north_m, east_m), and writes root/field. Returns the manifest and build statistics."""
	depth_dir = Path(root) / 'depth'
	lat, water, elevation, sources = inputs(region, depth_dir)
	parameters_ = parameters(region)
	geometry = field.geometry(lat, water, elevation, parameters_)
	basis = field.with_stations(geometry, [field.Station(s['id'], s['north_m'], s['east_m']) for s in stations])
	extent = stored_extent(lat, depth_dir)
	grid = stored_grid(basis, extent, 'slice')
	# A station inside the stored grid takes its frame from the float32 axes a reader has, so the field there is exactly
	# the station's velocity; one outside it from the computation lattice.
	rows, cols = grid['slots'].shape[:2]
	stored = field.Lattice(grid['origin_north_m'], grid['origin_east_m'], grid['cell_m'], rows, cols)
	frames = []
	for s, f in zip(basis.stations, basis.frames):
		if 0 <= (s.north - stored.origin_north) / stored.cell <= rows - 1 and 0 <= (s.east - stored.origin_east) / stored.cell <= cols - 1:
			f = field.frame_on(stored, grid['values'][..., 0], grid['values'][..., 1], field.wet_cells(grid['slots']), s.north, s.east)
		frames.append(dict(id=s.id, north_m=s.north, east_m=s.east, frame_north=float(f[0]), frame_east=float(f[1])))
	dropped = 1. - np.sort(basis.weights, axis=0)[::-1][:parameters_.slots].sum(axis=0)
	r0, r1, c0, c1 = extent
	in_grid = basis.wet[r0:r1 + 1, c0:c1 + 1]
	stats = dict(lattice=dict(origin_north_m=lat.origin_north, origin_east_m=lat.origin_east, cell_m=lat.cell, rows=lat.rows, cols=lat.cols),
		water_cells=int(basis.wet.sum()), stored_cells=int(in_grid.size), stored_water_cells=int(in_grid.sum()),
		pattern_iterations=geometry.iterations, weight_iterations=basis.iterations['weights'],
		dropped_weight_max=float(dropped[basis.wet].max()), dropped_weight_p99=float(np.percentile(dropped[basis.wet], 99)),
		stored_dropped_weight_max=float(dropped[r0:r1 + 1, c0:c1 + 1][in_grid].max()),
		stored_dropped_weight_p99=float(np.percentile(dropped[r0:r1 + 1, c0:c1 + 1][in_grid], 99)))
	notes = [
		f'Method {pack.FIELD_METHOD}: potential-flow axis and sheltering from the water\'s shape, a mild depth factor, harmonic '
		f'weights through the water only, and each station\'s direction off the axis kept within {parameters_.across_reach_m:g} m '
		'of it; see data-pipeline/ferrydata/environment/README.md.',
		f'Truncation to {parameters_.slots} station slots drops at most {stats["dropped_weight_max"]:.4f} of a cell\'s weight '
		f'(99th percentile {stats["dropped_weight_p99"]:.4f}) over the computation box.',
		'Only ENC land (LNDARE) is solid. Shoreline constructions charted as areas are under water or awash (WATLEV 3 or 4), '
		'and those above water are charted only as outlines, so a pier charted by its outline is open water to the field.',
	]
	metadata = pack.write_field(Path(root) / 'field', region, frames, [grid], parameters_record(region, parameters_), sources, notes)
	log(f'  field: {lat.rows}x{lat.cols} lattice, {stats["water_cells"]} water cells; stored {grid["slots"].shape[0]}x{grid["slots"].shape[1]}; '
		f'truncation drops at most {stats["dropped_weight_max"]:.4f}', flush=True)
	return metadata, stats, basis
