"""A small synthetic harbor tiles v1 build for the 3D Tiles writer's tests: three active tiles in an L, with rasters,
geoid corners, chart structures and landing files laid out as `ferrydata build --harbor` writes them.

The rasters share one 1/9 arc-second lattice (as the real build's do). Tile (0, 1)'s raster reads 2 cm higher than its
neighbours' and every tile's geoid corners carry their own offset, so a vertex that took its height from the wrong
tile shows up as a crack. Structures: a pier across the (0, 0)/(0, 1) seam, a bulkhead exactly on that seam, a coastline,
a landing barge (named by the landing file, so left out), an ordinary pontoon (floating, drawn), and a point dolphin.
"""

import hashlib
import json
from pathlib import Path

import numpy as np
import rasterio
from rasterio.transform import from_origin
from shapely.geometry import LineString, Polygon, Point, mapping

from ferrydata import geodesy, tiles

CELL_DEG = 1.0 / 32400.0
ACTIVE = ((0, 0), (0, 1), (1, 0))
RASTER_BIAS = {(0, 1): 0.02}
BARGE = "0226BARGE0000001"
PONTOON = "0226PONTOON00002"
PIER = "0226PIER00000003"
SEAM = "0226SEAM00000004"
COAST = "0226COAST0000005"
DOLPHIN = "0226DOLPHIN00006"


def terrain(north, east):
	"""NAVD88 metres: a channel along the middle, land rising to the north-east, some relief."""
	return -14.0 + 0.0035 * east + 0.0025 * north + 2.5 * np.sin(north / 650.0) * np.cos(east / 900.0)


def geoid_corner(north, east, i, j):
	return -31.8 + 1.0e-5 * north - 2.0e-5 * east + 0.003 * (i + 2 * j)


def lonlat(points_ne):
	north = np.array([p[0] for p in points_ne], dtype=float)
	east = np.array([p[1] for p in points_ne], dtype=float)
	lat, lon = geodesy.from_local(north, east)
	return [(float(x), float(y)) for x, y in zip(lon, lat)]


def write_raster(path, i, j):
	tile = tiles.make_tile(i, j)
	west, south, east, north = tile.polygon.bounds
	col0 = int(np.floor(west / CELL_DEG)) - 2
	col1 = int(np.ceil(east / CELL_DEG)) + 2
	row0 = int(np.ceil(north / CELL_DEG)) + 2
	row1 = int(np.floor(south / CELL_DEG)) - 2
	lon = (np.arange(col0, col1) + 0.5) * CELL_DEG
	lat = (row0 - np.arange(0, row0 - row1) - 0.5) * CELL_DEG
	LON, LAT = np.meshgrid(lon, lat)
	n, e = geodesy.to_local(LAT, LON)
	values = (terrain(n, e) + RASTER_BIAS.get((i, j), 0.0)).astype("float32")
	values[0, :] = -99999.0  # nodata along the outer rows, as the real rasters have outside the grown square
	values[-1, :] = -99999.0
	with rasterio.open(path, "w", driver="GTiff", width=values.shape[1], height=values.shape[0], count=1, dtype="float32",
			crs="EPSG:4269", transform=from_origin(col0 * CELL_DEG, row0 * CELL_DEG, CELL_DEG, CELL_DEG), nodata=-99999.0) as ds:
		ds.write(values, 1)
		ds.update_tags(vertical_datum="NAVD88")


def write_geoid(path, i, j):
	points = {}
	for label, (n, e) in {"centre": (4000 * i + 2000, 4000 * j + 2000), "sw": (4000 * i - 250, 4000 * j - 250),
			"se": (4000 * i - 250, 4000 * j + 4250), "nw": (4000 * i + 4250, 4000 * j - 250), "ne": (4000 * i + 4250, 4000 * j + 4250)}.items():
		lat, lon = geodesy.from_local(float(n), float(e))
		points[label] = {"lon": float(lon), "lat": float(lat), "north_m": float(n), "east_m": float(e),
			"navd88_zero_ellipsoidal_height_m": round(geoid_corner(n, e, i, j), 4)}
	path.write_text(json.dumps({"slice": f"harbor/tiles/tile_{i}_{j}", "points": points}, indent="\t") + "\n")


def structures():
	"""(lnam, feature class, WATLEV, shapely geometry in lon/lat)."""
	pier = Polygon(lonlat([(1000, 3900), (1000, 4100), (1030, 4100), (1030, 3900)]))
	seam = LineString(lonlat([(200, 4000), (600, 4000)]))
	coast = LineString(lonlat([(500, 100), (900, 700), (1600, 1400), (3000, 2000)]))
	barge = Polygon(lonlat([(2000, 1000), (2000, 1027.432), (2010.668, 1027.432), (2010.668, 1000)]))
	pontoon = Polygon(lonlat([(4500, 1500), (4500, 1530), (4512, 1530), (4512, 1500)]))
	dolphin = Point(lonlat([(2100, 1100)])[0])
	return [(PIER, "SLCONS", 2, pier), (SEAM, "SLCONS", 2, seam), (COAST, "COALNE", None, coast),
		(BARGE, "PONTON", None, barge), (PONTOON, "PONTON", None, pontoon), (DOLPHIN, "MORFAC", 2, dolphin)]


def write_structures(path, i, j):
	tile = tiles.make_tile(i, j)
	features = []
	for lnam, layer, watlev, geometry in structures():
		clipped = geometry.intersection(tile.polygon)
		if clipped.is_empty:
			continue
		features.append({"type": "Feature", "geometry": mapping(clipped), "properties": {"id": lnam, "feature_class": layer,
			"water_level_effect_code": watlev, "category_code": None}})
	path.write_text(json.dumps({"type": "FeatureCollection", "features": features}, indent="\t") + "\n")


def write_landings(path, i, j):
	features = []
	if (i, j) == (0, 0):
		ring = list(structures()[3][3].exterior.coords)
		segments = [{"index": k, "id": 100 + k, "kind": "berth-face" if k == 0 else ("barge-side" if k % 2 == 0 else "barge-end"),
			"layer": "PONTON", "lnam": BARGE} for k in range(len(ring) - 1)]
		segments.append({"index": len(ring) - 1, "id": 200, "kind": "pier", "layer": "LNDARE", "lnam": PIER})
		features.append({"type": "Feature", "geometry": {"type": "LineString", "coordinates": [list(p) for p in ring] + [list(ring[1])]},
			"properties": {"stop_id": "999", "polyline_id": "landing-999", "segments": segments}})
	path.write_text(json.dumps({"type": "FeatureCollection", "features": features}, indent="\t") + "\n")


def sha256(path):
	return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def make_harbor(root):
	"""Writes the synthetic build under `root` (the stand-in for data/processed/harbor) and returns it."""
	root = Path(root)
	entries = []
	for i in tiles.INDICES:
		for j in tiles.INDICES:
			entry = {"id": f"tile_{i}_{j}", "i": i, "j": j, "active": (i, j) in ACTIVE, "outputs": []}
			if (i, j) in ACTIVE:
				folder = root / "tiles" / entry["id"]
				folder.mkdir(parents=True, exist_ok=True)
				write_raster(folder / "depth_merged_navd88.tif", i, j)
				write_geoid(folder / "geoid_offset.json", i, j)
				write_structures(folder / "shoreline_constructions.geojson", i, j)
				write_landings(folder / "landing_structures.geojson", i, j)
				entry["outputs"] = [{"file": str(p.relative_to(root)), "sha256": sha256(p), "size_bytes": p.stat().st_size}
					for p in sorted(folder.iterdir())]
			entries.append(entry)
	(root / "index.json").write_text(json.dumps({"schema": "harbor-tiles-v1", "tile_size_m": 4000, "margin_m": 250,
		"tiles": entries}, indent="\t") + "\n")
	return root


def make_slice(root, harbor_root, bias=None):
	"""A stand-in for the slice's Unity export (unity/depth.f32 and depth.json) over part of tile (0, 0), cut from the
	same lattice and values as the harbor raster, and an import manifest naming its hash. `bias(lat, lon)` perturbs it."""
	root = Path(root)
	(root / "unity").mkdir(parents=True, exist_ok=True)
	with rasterio.open(Path(harbor_root) / "tiles" / "tile_0_0" / "depth_merged_navd88.tif") as ds:
		values = ds.read(1).astype("float32")
		transform = ds.transform
	# A window well inside tile (0, 0): about 1 km by 1 km around north 1500 m, east 1500 m.
	lat, lon = geodesy.from_local(1500.0, 1500.0)
	row = int((transform.f - float(lat)) / CELL_DEG)
	col = int((float(lon) - transform.c) / CELL_DEG)
	window = values[row - 150:row + 150, col - 200:col + 200].copy()
	origin_lon = transform.c + (col - 200) * CELL_DEG
	origin_lat = transform.f - (row - 150) * CELL_DEG
	if bias is not None:
		lons = origin_lon + (np.arange(window.shape[1]) + 0.5) * CELL_DEG
		lats = origin_lat - (np.arange(window.shape[0]) + 0.5) * CELL_DEG
		LON, LAT = np.meshgrid(lons, lats)
		window = (window + bias(LAT, LON)).astype("float32")
	(root / "unity" / "depth.f32").write_bytes(window.astype("<f4").tobytes())
	(root / "unity" / "depth.json").write_text(json.dumps({"format_version": 1, "width": window.shape[1], "height": window.shape[0],
		"origin": [origin_lon, origin_lat], "cell_size": [CELL_DEG, -CELL_DEG], "crs": "EPSG:4326", "datum": "NAVD88",
		"nodata": -99999.0}, indent="\t") + "\n")
	manifest = root / "import-manifest.json"
	manifest.write_text(json.dumps({"slice": "lower-east-river", "source_sha256": {"unity/depth.f32": sha256(root / "unity" / "depth.f32")}}))
	return root, manifest
