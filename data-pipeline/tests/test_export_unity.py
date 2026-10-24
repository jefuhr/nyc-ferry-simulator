import hashlib
import json

import numpy as np
import pytest
import rasterio
from rasterio.transform import from_origin

from ferrydata.export_unity import export_grid


def test_export_is_little_endian_lossless_and_deterministic(tmp_path):
	source = tmp_path / 'depth_merged_navd88.tif'
	values = np.array([[1.25, -9999], [-12.5, 0]], dtype=np.float32)
	with rasterio.open(source, 'w', driver='GTiff', width=2, height=2, count=1,
		dtype='float32', crs='EPSG:4326', transform=from_origin(-74, 41, .001, .002), nodata=-9999) as ds:
		ds.write(values, 1)
	dest = tmp_path / 'unity'
	export_grid(source, dest)
	first = {p.name: p.read_bytes() for p in dest.iterdir()}
	export_grid(source, dest)
	assert first == {p.name: p.read_bytes() for p in dest.iterdir()}
	assert np.array_equal(np.frombuffer(first['depth.f32'], dtype='<f4').reshape(2, 2), values)
	header = json.loads(first['depth.json'])
	assert header['source_sha256'] == hashlib.sha256(source.read_bytes()).hexdigest()
	assert header['datum'] == 'NAVD88'
	assert header['crs'] == 'EPSG:4326'
	assert header['origin'] == [-74, 41]
	assert header['cell_size'] == [.001, -.002]
	assert header['nodata'] == -9999


def test_projected_input_rejected_instead_of_misplaced(tmp_path):
	source = tmp_path / 'projected.tif'
	with rasterio.open(source, 'w', driver='GTiff', width=2, height=2, count=1,
		dtype='float32', crs='EPSG:32618', transform=from_origin(500000, 4500000, 3, 3)) as ds:
		ds.write(np.ones((2, 2), dtype=np.float32), 1)
	with pytest.raises(ValueError, match='EPSG:4326'):
		export_grid(source, tmp_path / 'unity')


def test_nad83_cudem_is_converted_to_wgs84(tmp_path):
	source = tmp_path / 'nad83.tif'
	with rasterio.open(source, 'w', driver='GTiff', width=2, height=2, count=1,
		dtype='float32', crs='EPSG:4269', transform=from_origin(-74, 41, .001, .002), nodata=-9999) as ds:
		ds.write(np.ones((2, 2), dtype=np.float32), 1)
	header = export_grid(source, tmp_path / 'unity')
	assert header['crs'] == 'EPSG:4326'
	assert header['source_crs'] == 'EPSG:4269'
	assert np.all(np.fromfile(tmp_path / 'unity/depth.f32', dtype='<f4') == 1)
