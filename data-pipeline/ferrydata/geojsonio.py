"""GeoJSON writer that carries both WGS84 lon/lat and world-local north_m/east_m.

Geometry coordinates are WGS84 [lon, lat]. Every feature also carries `local_coordinates`, a foreign member that
mirrors the geometry's coordinate nesting with [north_m, east_m] pairs from ferrydata.geodesy, and properties
lon/lat/north_m/east_m for a representative point (the point itself for point features).
"""

import json
import math
from pathlib import Path

import numpy as np
from shapely.geometry import mapping

from ferrydata import geodesy

LONLAT_DIGITS = 7
LOCAL_DIGITS = 3


def _is_position(node):
	return len(node) >= 2 and all(isinstance(v, (int, float, np.floating)) for v in node)


def _round_coords(node):
	if _is_position(node):
		return [round(float(v), LONLAT_DIGITS) if i < 2 else round(float(v), 3) for i, v in enumerate(node)]
	return [_round_coords(child) for child in node]


def _local(node):
	if _is_position(node):
		north, east = geodesy.to_local(node[1], node[0])
		return [round(float(north), LOCAL_DIGITS), round(float(east), LOCAL_DIGITS)]
	if node and all(_is_position(child) for child in node):
		arr = np.asarray(node, dtype=float)
		north, east = geodesy.to_local(arr[:, 1], arr[:, 0])
		return [[round(float(n), LOCAL_DIGITS), round(float(e), LOCAL_DIGITS)] for n, e in zip(north, east)]
	return [_local(child) for child in node]


def _clean(value):
	"""Make property values JSON-safe: numpy scalars to python, NaN to None."""
	if isinstance(value, (np.integer,)):
		return int(value)
	if isinstance(value, (np.floating, float)):
		return None if math.isnan(float(value)) else float(value)
	if isinstance(value, np.ndarray):
		return [_clean(v) for v in value.tolist()]
	if isinstance(value, (list, tuple)):
		return [_clean(v) for v in value]
	if isinstance(value, dict):
		return {k: _clean(v) for k, v in value.items()}
	return value


def make_feature(geometry, properties):
	geo = mapping(geometry)
	geo = {"type": geo["type"], "coordinates": _round_coords(geo["coordinates"])}
	point = geometry if geometry.geom_type == "Point" else geometry.representative_point()
	north, east = geodesy.to_local(point.y, point.x)
	props = {k: _clean(v) for k, v in properties.items()}
	props.update(
		{
			"lon": round(float(point.x), LONLAT_DIGITS),
			"lat": round(float(point.y), LONLAT_DIGITS),
			"north_m": round(float(north), LOCAL_DIGITS),
			"east_m": round(float(east), LOCAL_DIGITS),
		}
	)
	return {"type": "Feature", "geometry": geo, "local_coordinates": _local(geo["coordinates"]), "properties": props}


def write_collection(path, features, name, extra=None):
	path = Path(path)
	path.parent.mkdir(parents=True, exist_ok=True)
	collection = {
		"type": "FeatureCollection",
		"name": name,
		"crs_note": "geometry is WGS84 [lon, lat]; local_coordinates are [north_m, east_m] in the world tangent plane at 40.7000, -74.0150 (docs/conventions.md)",
		"features": features,
	}
	if extra:
		collection.update(extra)
	path.write_text(json.dumps(collection, separators=(",", ":"), ensure_ascii=False) + "\n", encoding="utf-8")
	return path


def read_collection(path):
	return json.loads(Path(path).read_text(encoding="utf-8"))
