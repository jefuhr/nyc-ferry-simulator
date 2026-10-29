"""Failure modes: a harbor floor raises beds, modifies protected cells or slice output,
loses deepest-report selection, or emits different bytes on repeated builds.
"""
import json

import numpy as np
import pandas as pd
import rasterio
from rasterio.transform import from_origin
from shapely.geometry import box, mapping

from ferrydata import geodesy, operational_depth as od


def test_harbor_tile_floor_protects_hazards_land_structures_nodata_and_is_deterministic(tmp_path):
	transform = from_origin(-74.016, 40.701, .00003, .00003)
	profile = dict(driver='GTiff', height=60, width=60, count=1, dtype='float32', crs='EPSG:4326', transform=transform, nodata=-9999.)
	bed = np.full((60, 60), -1., dtype='float32')
	bed[30, 30] = -9999.
	bed[31, 31] = -9.
	land = box(-74.01515, 40.7001, -74.01505, 40.7002)
	hazard = box(-74.015, 40.700, -74.0149, 40.7001)
	for name, geometries in [('land_areas.geojson', [land]), ('shoreline_constructions.geojson', [])]:
		(tmp_path / name).write_text(json.dumps(dict(features=[dict(geometry=mapping(g), properties={}) for g in geometries])))
	n, e = geodesy.to_local(40.70005, -74.015)
	reports = pd.DataFrame([dict(north_m=n, east_m=e, required_bed_navd88_m=x, footprint_radius_m=100., vessel_class='T') for x in (-2., -3.)])
	items = [dict(geometry=hazard)]
	a, provenance = od.build_harbor_tile(tmp_path, tmp_path / 'a', bed, profile, reports, od.vessel_classes(), {}, items)
	b, other = od.build_harbor_tile(tmp_path, tmp_path / 'b', bed, profile, reports.iloc[::-1], od.vessel_classes(), {}, items)
	assert np.array_equal(a, b)
	assert np.all(a <= bed)
	assert a[30, 30] == -9999. and a[31, 31] == -9.
	protected = od.fixed_mask(tmp_path, bed.shape, transform) | od.hazard_mask(items, bed.shape, transform)
	assert np.array_equal(a[protected], bed[protected])
	assert np.any(a == -3.)
	assert provenance == other
	for name in (od.FLOOR_FILE, od.LOWERED_FILE, 'depth_merged_navd88.tif'):
		assert (tmp_path / 'a' / name).read_bytes() == (tmp_path / 'b' / name).read_bytes()


def test_harbor_hazards_include_obstruction_and_wreck_points_and_lines_without_changing_slice_default(tmp_path, monkeypatch):
	import geopandas as gpd
	from shapely.geometry import Point, LineString
	from ferrydata import enc

	(tmp_path / 'shoreline_constructions.geojson').write_text('{"features": []}')
	layers = {'OBSTRN': gpd.GeoDataFrame(dict(LNAM=['point', 'area'], CATOBS=[1, 1]),
		geometry=[Point(-74., 40.7), box(-74., 40.7, -73.999, 40.701)]),
		'WRECKS': gpd.GeoDataFrame(dict(LNAM=['line'], CATWRK=[1]), geometry=[LineString([(-74., 40.7), (-73.999, 40.701)])])}
	monkeypatch.setattr(enc, 'read_layers', lambda *args: (layers, {}))
	assert [h['id'] for h in od.hazards(tmp_path, None)] == ['area']
	assert {h['id'] for h in od.hazards(tmp_path, None, areas_only=False)} == {'area', 'point', 'line'}


def test_global_cell_identity_does_not_merge_adjacent_half_integer_centres():
	from ferrydata.harbor_landings.candidate import cell_key
	lat = 1316975.5 / 32400
	west, east = -2398512.5 / 32400, -2398511.5 / 32400
	assert cell_key(lat, west) != cell_key(lat, east)
	assert cell_key(lat + 1e-13, west - 1e-13) == cell_key(lat, west)
	assert cell_key((1316976.5) / 32400, west) != cell_key(lat, west)
