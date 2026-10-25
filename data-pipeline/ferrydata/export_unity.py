"""Lossless deterministic interchange for Unity: the depth grid without a GeoTIFF dependency, and contacts.json."""
import hashlib
import json
import math
from pathlib import Path

import numpy as np
import rasterio
from rasterio.warp import calculate_default_transform, reproject, Resampling

from ferrydata import geodesy, paths


def export_grid(source, destination):
	source, destination = Path(source), Path(destination)
	with rasterio.open(source) as ds:
		if ds.crs not in (rasterio.crs.CRS.from_epsg(4326), rasterio.crs.CRS.from_epsg(4269)) or ds.transform.b != 0 or ds.transform.d != 0:
			raise ValueError('Unity export requires an unrotated EPSG:4326 raster')
		values = ds.read(1).astype('<f4')
		nodata = ds.nodata
		transform, width, height = ds.transform, ds.width, ds.height
		if ds.crs.to_epsg() == 4269:
			transform, width, height = calculate_default_transform(ds.crs, 'EPSG:4326', ds.width, ds.height, *ds.bounds)
			converted = np.full((height, width), nodata if nodata is not None else np.nan, dtype='<f4')
			reproject(values, converted, src_transform=ds.transform, src_crs=ds.crs,
				dst_transform=transform, dst_crs='EPSG:4326', src_nodata=nodata, dst_nodata=nodata, resampling=Resampling.nearest, num_threads=1)
			values = converted
		header = {
			'format_version': 1, 'width': width, 'height': height,
			'origin': [transform.c, transform.f],
			'cell_size': [transform.a, transform.e],
			'pixel_reference': 'upper-left corner; samples at cell centres',
			'crs': 'EPSG:4326', 'datum': 'NAVD88', 'unit': 'm',
			'dtype': 'little-endian float32', 'layout': 'row-major north-to-south',
			'nodata': nodata if nodata is not None and math.isfinite(nodata) else None,
			'source_raster': source.name, 'source_crs': ds.crs.to_string(),
			'reprojection': 'nearest, single-threaded GDAL NAD83 to WGS84' if ds.crs.to_epsg() == 4269 else 'none',
			'source_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
		}
	destination.mkdir(parents=True, exist_ok=True)
	(destination / 'depth.f32').write_bytes(values.tobytes(order='C'))
	(destination / 'depth.json').write_text(json.dumps(header, indent='\t', allow_nan=False) + '\n', encoding='utf-8')
	return header


CONTACTS_VERSION = 2
# Version 1 fields first, in the order the importer has always written them; version 2 adds the rest.
OPTIONAL_V1 = ('primary_face', 'water_side_evidence', 'crew_evidence', 'geometry_estimated', 'water_side_crew_reported')
V2_FIELDS = ('kind', 'role', 'berth_id', 'berth_rank', 'structure_id', 'layer', 'lnam', 'solid_side_evidence')


def contact_records(structures):
	"""contacts.json version 2 from landing_structures.geojson: one record per contact segment.

	Still a bare JSON array with every version 1 field, so a version 1 reader loads it and ignores the
	new optional fields. start_ne_m, end_ne_m and water_normal_ne come from ferrydata.geodesy here;
	World/Import recomputes them through the core's LocalTangentPlane from start_lonlat and end_lonlat.
	"""
	records = []
	features = sorted(structures['features'], key=lambda f: f['properties']['stop_id'])  # stable: file order within a stop
	for feature in features:
		p = feature['properties']
		positions = feature['geometry']['coordinates']
		lat = [position[1] for position in positions]
		lon = [position[0] for position in positions]
		north, east = geodesy.to_local(lat, lon)
		for segment in p['segments']:
			i = segment['index']
			start, end = [float(north[i]), float(east[i])], [float(north[i + 1]), float(east[i + 1])]
			length = math.hypot(end[0] - start[0], end[1] - start[1])
			record = {'id': segment['id'], 'stop_id': p['stop_id'], 'stop_name': p['stop_name'], 'start_ne_m': start, 'end_ne_m': end,
				'water_normal_ne': [(end[1] - start[1]) / length, -(end[0] - start[0]) / length],
				'source': segment['source'], 'confidence': segment['confidence'], 'polyline_id': p['polyline_id'], 'index': i,
				'verification': segment['verification'], 'water_side_verified_by_pipeline': segment['water_side_verified_by_pipeline']}
			record.update({key: segment[key] for key in OPTIONAL_V1 if key in segment})
			values = {'kind': segment['kind'], 'role': p['role'], 'berth_id': segment.get('berth_id'), 'berth_rank': p['berth_rank'] if segment.get('berth_id') else None,
				'structure_id': p['structure_id'], 'layer': segment['layer'], 'lnam': segment['lnam'], 'solid_side_evidence': segment['solid_side_evidence']}
			record.update({key: values[key] for key in V2_FIELDS if values[key] is not None})
			record['start_lonlat'], record['end_lonlat'] = positions[i][:2], positions[i + 1][:2]
			records.append(record)
	return records


def export_contacts(source, destination):
	"""Writes contacts.json version 2 beside the depth grid; returns the number of segments."""
	source, destination = Path(source), Path(destination)
	records = contact_records(json.loads(source.read_text(encoding='utf-8')))
	destination.parent.mkdir(parents=True, exist_ok=True)
	destination.write_text(json.dumps(records, indent='\t', ensure_ascii=False, allow_nan=False) + '\n', encoding='utf-8')
	return len(records)


def run(slice_):
	root = paths.processed_dir(slice_.name)
	header = export_grid(root / 'depth_merged_navd88.tif', root / 'unity')
	print(f"Unity grid: {header['width']}x{header['height']} little-endian float32 NAVD88 m")
	count = export_contacts(root / 'landing_structures.geojson', root / 'unity' / 'contacts.json')
	print(f"Unity contacts: {count} segments, contacts.json version {CONTACTS_VERSION}")
	return 0
