"""Conservative chart/CUDEM merge near waterfront structures, on the original CUDEM grid.

Raster values are elevations (positive up). Only a valid CUDEM cell with deeper chart
support can change. DEPARE minima are lower bounds on depth; sounding support is one
cell only. No interpolation or extrapolation of individual soundings is performed.
"""
import json

import numpy as np
import pandas as pd
import rasterio
from rasterio import features
from shapely.geometry import shape
from shapely.ops import unary_union

from ferrydata import depth, geojsonio, landings, manifest, operational_depth, paths

BUFFER_M = 60.
NODATA = -99999.


def datum_offset(payload):
	"""Elevation of MLLW zero in NAVD88 = MLLW(STND) - NAVD88(STND), metres."""
	if payload.get('units') not in ('meters', 'metres', 'metric'):
		raise ValueError('CO-OPS datums must be in metres')
	values = {d['name']: float(d['value']) for d in payload.get('datums', [])}
	if not all(name in values and np.isfinite(values[name]) for name in ('MLLW', 'NAVD88')):
		raise ValueError('CO-OPS station lacks finite MLLW and NAVD88 datums')
	return values['MLLW'] - values['NAVD88']


def chart_elevation(depth_mllw_m, offset_m):
	return offset_m - depth_mllw_m


def rasterize_chart(areas, soundings, out_shape, transform, offset_m):
	"""Shallowest area minimum wins on overlap; a SOUNDG measurement wins in its cell.

	Areas cover cell centres; SOUNDG covers only the cell containing its point. The
	shallowest sounding wins if several share a cell. Area minima are bounds, not
	measurements; a measured sounding may replace a broad lower bound. Both
	are deliberately conservative. Unknown cells remain NaN.
	"""
	chart = np.full(out_shape, np.nan, dtype='float64')
	# Deepest first, shallower last gives order-independent conservative overlap.
	polygons = [(g, chart_elevation(d, offset_m)) for g, d in areas if np.isfinite(d)]
	if polygons:
		chart = features.rasterize(sorted(polygons, key=lambda x: x[1]), out_shape=out_shape,
			transform=transform, fill=np.nan, dtype='float64')
	sounding_cells = {}
	for point, sounding in soundings:
		if not np.isfinite(sounding):
			continue
		row, col = rasterio.transform.rowcol(transform, point.x, point.y)
		if 0 <= row < out_shape[0] and 0 <= col < out_shape[1]:
			value = chart_elevation(sounding, offset_m)
			key = (row, col)
			sounding_cells[key] = max(sounding_cells.get(key, -np.inf), value)
	for (row, col), value in sounding_cells.items():
		chart[row, col] = value
	return chart


def merge_elevations(cudem, chart, allowed, nodata):
	valid = np.isfinite(cudem) & (cudem != nodata) & np.isfinite(chart) & allowed
	changed = valid & (chart < cudem)
	merged = cudem.copy()
	merged[changed] = chart[changed]
	# A float32 output must round toward shallower water, never below the
	# double-precision chart elevation bound even by one representable value.
	rounded_deeper = changed & (merged.astype('float64') < chart)
	merged[rounded_deeper] = np.nextafter(merged[rounded_deeper], np.array(np.inf, dtype=merged.dtype))
	changed &= merged < cudem
	# Flag disagreement in either direction even when conservative rules keep CUDEM.
	flagged = valid & (np.abs(cudem - chart) > 1.)
	return merged, changed, flagged


def _load(out, name):
	return geojsonio.read_collection(out / name)


def _write_raster(target, array, profile, tags):
	with rasterio.open(target, 'w', **profile) as dataset:
		dataset.write(array, 1)
		dataset.update_tags(**tags)


def build(slice_, log=print):
	out = paths.processed_dir(slice_.name)
	stations = _load(out, 'coops_stations.json')['stations']
	# The Battery is the slice-wide chart-datum reference, not a tidal observation.
	station = next(s for s in stations if s['id'] == slice_.water_level_stations[0] and s['kind'] == 'water_level')
	offset = station['mllw_navd88_offset_m']
	area_fc = _load(out, 'depth_areas.geojson')
	if any(c['sounding'] != 'MLLW' for c in area_fc['cells'].values()):
		raise ValueError('unsupported ENC sounding datum; expected MLLW')
	areas = [(shape(f['geometry']), f['properties']['min_depth_m']) for f in area_fc['features'] if f['properties'].get('min_depth_m') is not None]
	soundings = [(shape(f['geometry']), f['properties']['depth_m']) for f in _load(out, 'soundings.geojson')['features']]
	faces = _load(out, 'landing_faces.geojson')['features']
	shore = [landings.to_local(shape(f['geometry'])) for f in _load(out, 'shoreline_constructions.geojson')['features']
		if f['properties']['feature_class'] != 'MORFAC']  # mooring buoys are not shoreline constructions
	shore += [landings.to_local(shape(f['properties']['face_geometry'])) for f in faces]
	buffer = landings.to_wgs84(unary_union([g.buffer(BUFFER_M) for g in shore]))
	land = [shape(f['geometry']) for f in _load(out, 'land_areas.geojson')['features']]
	with rasterio.open(out / 'depth_navd88.tif') as source:
		cudem = source.read(1)
		profile = source.profile.copy()
		chart = rasterize_chart(areas, soundings, cudem.shape, source.transform, offset)
		allowed = features.geometry_mask([buffer], out_shape=cudem.shape, transform=source.transform, invert=True)
		if land:
			allowed &= ~features.geometry_mask(land, out_shape=cudem.shape, transform=source.transform, invert=True)
		charted, changed, flagged = merge_elevations(cudem, chart, allowed, source.nodata)
		from ferrydata import validate
		required = validate.k_class_max_draft_m() + validate.UKC_MARGIN_M
		levels = pd.read_csv(out / station['file'])
		low = float(levels['water_level_navd88_m'].min())
		# Where the fleet's T- and K-class boats were afloat, the bed goes no shallower than their AIS says it can be.
		merged, operational = operational_depth.build_slice(slice_, out, charted, profile, low, required, log)
		tags = {'vertical_datum': 'NAVD88', 'units': 'm', 'positive': 'up', 'datum_station': station['id'],
			'mllw_zero_navd88_m': str(offset), 'datum_epoch': station['datum_epoch']}
		_write_raster(out / 'depth_merged_navd88.tif', merged, profile, {**tags, 'operational_floor': operational_depth.METHOD,
			'operational_floor_file': operational_depth.FLOOR_FILE, 'operational_lowered_file': operational_depth.LOWERED_FILE})
		chart_profile = {**profile, 'dtype': 'float32', 'nodata': NODATA}
		_write_raster(out / 'depth_chart_navd88.tif', np.where(np.isfinite(chart), chart, NODATA).astype('float32'), chart_profile, tags)
		flag_profile = {**profile, 'dtype': 'uint8', 'nodata': None, 'predictor': 1}
		_write_raster(out / 'depth_disagreement_gt1m.tif', flagged.astype('uint8'), flag_profile,
			{'meaning': '1 = chart/CUDEM absolute elevation difference > 1 m inside water/structure buffer; 0 = not flagged'})
		rows = []
		for f in faces:
			p = f['properties']; mid = p['face_midpoint']
			polygon = depth.landing_box_polygon((mid['north_m'], mid['east_m']), p['outward_normal_ne'])
			cs = depth.sample_polygon(cudem, source.transform, polygon, source.nodata)
			bs = depth.sample_polygon(charted, source.transform, polygon, source.nodata)
			ms = depth.sample_polygon(merged, source.transform, polygon, source.nodata)
			chs = depth.sample_polygon(np.where(np.isfinite(chart), chart, NODATA), source.transform, polygon, NODATA)
			fs = depth.sample_polygon(flagged.astype('float32'), source.transform, polygon, NODATA)
			cstats, bstats, mstats, chstats = [depth.depth_stats(sample.values, low) for sample in (cs, bs, ms, chs)]
			fraction = len(ms.values) / ms.cell_count if ms.cell_count else 0
			rows.append({'stop_id': p['stop_id'], 'landing': p['stop_name'], 'face_source': p['source'],
				'confidence': p['confidence'], 'verification': p['verification'], 'water_side': p['water_side'],
				'cudem_min_depth_m': cstats['min_depth_m'], 'charted_min_depth_m': chstats['min_depth_m'],
				'merged_min_depth_m': mstats['min_depth_m'], 'charted_valid_fraction': len(chs.values) / chs.cell_count if chs.cell_count else 0,
				'merged_valid_fraction': fraction, 'required_depth_m': required,
				'cudem_verdict': 'pass' if validate.depth_verdict({**cstats, 'valid_fraction': fraction}, required) else 'FAIL',
				'merged_verdict': 'pass' if validate.depth_verdict({**mstats, 'valid_fraction': fraction}, required) else 'FAIL',
				'cells_diff_gt1m': int(fs.values.sum()), 'datum_station': station['id'],
				'mllw_zero_navd88_m': offset, 'lowest_water_navd88_m': low,
				'merged_before_floor_min_depth_m': bstats['min_depth_m'],
				'merged_before_floor_verdict': 'pass' if validate.depth_verdict({**bstats, 'valid_fraction': fraction}, required) else 'FAIL'})
		pd.DataFrame(rows).to_csv(out / 'landing_depth_diff.csv', index=False)
	metadata = {'source_cudem': 'depth_navd88.tif', 'source_chart': ['depth_areas.geojson', 'soundings.geojson'],
		'buffer_m': BUFFER_M, 'datum_station': station, 'mllw_zero_navd88_m': offset,
		'source_manifest_sha256': {name: manifest.sha256_file(paths.MANIFEST_DIR / f'{name}.json') for name in ('noaa-enc', 'coops', 'cudem')},
		'changed_cells': int(changed.sum()), 'cells_diff_gt1m': int(flagged.sum()),
		'rules': 'Keep valid CUDEM unless supported chart elevation is lower within 60 m of construction/face and outside ENC LNDARE. Use shallowest DEPARE DRVAL1 per cell; SOUNDG measurements override area bounds only in their sampled cells, with shallowest sounding on overlap. SOUNDG is never extrapolated. Never fill CUDEM nodata or deepen past chart. Existing CUDEM deeper than chart is retained and flagged. Then the operational-ais floor (operational_ais): where T- and K-class boats were afloat (AIS within 200 m of a primary-face midpoint at SOG at most 2.5 m/s on the 28 scenario days), each water cell within half the class length of a report is lowered to at most the simulated water level then minus the class maxDraft minus a 0.3 m keel margin; never raised, never on ENC LNDARE or dry SLCONS areas, never into nodata.',
		'uncertainty': 'Station datum offset is spatially constant for this slice; no VDatum surface transformation. Chart minima are conservative bounds, not bathymetric surveys. Face placement requires crew verification. The operational floor is AIS evidence of boats afloat, not a survey; its keel margin is an estimate and AIS antenna offsets are unknown.',
		'operational_ais': operational}
	(out / 'depth_merged_navd88.json').write_text(json.dumps(metadata, indent='\t') + '\n')
	log(f"  depth_merged_navd88.tif: {changed.sum()} cells deepened by the chart; {operational['cells_lowered']} lowered by the "
		f"operational-ais floor (largest {operational['max_lowering_m']:.2f} m); {flagged.sum()} chart/CUDEM differences > 1 m")
	log('  landing_depth_diff.csv; depth_chart_navd88.tif; depth_disagreement_gt1m.tif')


def build_harbor(tile, log=print):
	"""The conservative merge without landing faces (harbor v1 excludes them).

	Read an extra 100 m beyond the output square so the 60 m shoreline support
	buffer is identical on both sides of a tile seam, including its outer edge.
	The Battery's measured MLLW/NAVD88 offset is a disclosed harbor-wide
	approximation, not a spatial VDatum conversion or a depth-gap filler.
	"""
	from ferrydata import enc, tiles
	out = paths.processed_dir(tile.name)
	station_id = '8518750'
	datum_path = tiles._cached('coops', f'datums_{station_id}.json')
	datum = json.loads(datum_path.read_text(encoding='utf-8'))
	offset = datum_offset(datum)
	layers, cells = enc.read_layers(tile.expanded(100), enc.SHORE_LAYERS + enc.DEPTH_AREA_LAYERS + ('SOUNDG', 'LNDARE'))
	if any(c['sounding'] != 'MLLW' for c in cells.values()):
		raise ValueError('unsupported ENC sounding datum; expected MLLW')
	areas = []
	for name in enc.DEPTH_AREA_LAYERS:
		if name in layers:
			areas += [(r.geometry, float(r.DRVAL1)) for r in layers[name].itertuples() if r.DRVAL1 is not None and np.isfinite(r.DRVAL1)]
	soundings = []
	if 'SOUNDG' in layers:
		for r in layers['SOUNDG'].itertuples():
			points = r.geometry.geoms if r.geometry.geom_type == 'MultiPoint' else [r.geometry]
			soundings += [(p, p.z) for p in points if p.has_z]
	shore = [landings.to_local(g) for name in enc.SHORE_LAYERS if name != 'MORFAC' and name in layers for g in layers[name].geometry]
	buffer = landings.to_wgs84(unary_union([g.buffer(BUFFER_M) for g in shore]))
	land = list(layers['LNDARE'].geometry) if 'LNDARE' in layers else []
	with rasterio.open(out / 'depth_navd88.tif') as source:
		cudem = source.read(1)
		profile = source.profile.copy()
		chart = rasterize_chart(areas, soundings, cudem.shape, source.transform, offset)
		inside = features.geometry_mask([tile.polygon], out_shape=cudem.shape, transform=source.transform, invert=True)
		chart[~inside] = np.nan
		allowed = features.geometry_mask([buffer], out_shape=cudem.shape, transform=source.transform, invert=True) if not buffer.is_empty else np.zeros(cudem.shape, dtype=bool)
		if land:
			allowed &= ~features.geometry_mask(land, out_shape=cudem.shape, transform=source.transform, invert=True)
		merged, changed, flagged = merge_elevations(cudem, chart, allowed & inside, source.nodata)
		tags = {'vertical_datum': 'NAVD88', 'units': 'm', 'positive': 'up', 'datum_station': station_id,
			'mllw_zero_navd88_m': str(offset), 'datum_epoch': datum.get('epoch', '')}
		_write_raster(out / 'depth_merged_navd88.tif', merged, profile, tags)
		_write_raster(out / 'depth_chart_navd88.tif', np.where(np.isfinite(chart), chart, NODATA).astype('float32'),
			{**profile, 'dtype': 'float32', 'nodata': NODATA}, tags)
		_write_raster(out / 'depth_disagreement_gt1m.tif', flagged.astype('uint8'),
			{**profile, 'dtype': 'uint8', 'nodata': None, 'predictor': 1}, {'meaning': '1 = chart/CUDEM difference > 1 m inside support buffer'})
	metadata = {'source_cudem': 'depth_navd88.tif', 'source_chart': ['depth_areas.geojson', 'soundings.geojson'],
		'buffer_m': BUFFER_M, 'support_halo_m': 100, 'datum_station': station_id, 'datum_epoch': datum.get('epoch'),
		'datum_source': manifest.load_combined('coops').files[f'datums_{station_id}.json'], 'mllw_zero_navd88_m': offset,
		'changed_cells': int(changed.sum()), 'cells_diff_gt1m': int(flagged.sum()),
		'rules': 'Keep valid CUDEM unless supported chart elevation is lower within 60 m of construction and outside ENC LNDARE. SOUNDG measurements override DEPARE bounds in their sampled cells only. Never extrapolate soundings or fill CUDEM nodata.',
		'uncertainty': 'The Battery datum offset is spatially constant across the harbor; no VDatum surface transformation. Harbor v1 excludes landing faces and contact geometry.'}
	(out / 'depth_merged_navd88.json').write_text(json.dumps(metadata, indent='\t') + '\n', encoding='utf-8')
	log(f"  depth_merged_navd88.tif: {changed.sum()} cells deepened; {flagged.sum()} chart/CUDEM differences > 1 m")
