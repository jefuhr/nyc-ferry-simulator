"""Lossless deterministic raster interchange for Unity without a GeoTIFF dependency."""
import hashlib
import json
import math
from pathlib import Path

import numpy as np
import rasterio
from rasterio.warp import calculate_default_transform, reproject, Resampling

from ferrydata import paths


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


def run(slice_):
	root = paths.processed_dir(slice_.name)
	header = export_grid(root / 'depth_merged_navd88.tif', root / 'unity')
	print(f"Unity grid: {header['width']}x{header['height']} little-endian float32 NAVD88 m")
	return 0
