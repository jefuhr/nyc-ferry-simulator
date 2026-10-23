"""NOAA NCEI CUDEM 1/9 arc-second topobathy tiles (NAVD88 metres, positive up) clipped to a slice."""

import json
import re
import urllib.parse
import xml.etree.ElementTree as ET

import numpy as np
import rasterio
from rasterio import features
from rasterio.crs import CRS
from rasterio.merge import merge
from rasterio.transform import array_bounds

from ferrydata import fetcher, geodesy, manifest as manifest_module, paths
from ferrydata.slices import cudem_tile_ids

SOURCE = "cudem"
BUCKET = "https://noaa-nos-coastal-lidar-pds.s3.amazonaws.com"
PREFIX = "dem/NCEI_ninth_Topobathy_2014_8483/"
DOI = "10.25921/ds9v-ky35"
NODATA = -99999.0
OUTPUT_CRS = CRS.from_epsg(4269)  # the tiles are NAD83 (EPSG:4269) + NAVD88; NAD83 and WGS84 agree to about a metre


def _list(prefix, delimiter=None):
	params = {"list-type": "2", "prefix": prefix, "max-keys": "1000"}
	if delimiter:
		params["delimiter"] = delimiter
	root = ET.fromstring(fetcher.http_get(f"{BUCKET}/?{urllib.parse.urlencode(params)}"))
	ns = {"s3": "http://s3.amazonaws.com/doc/2006-03-01/"}
	keys = [k.text for k in root.findall("s3:Contents/s3:Key", ns)]
	folders = [p.text for p in root.findall("s3:CommonPrefixes/s3:Prefix", ns)]
	return keys, folders


def _version_key(key):
	match = re.search(r"_(\d{4})v(\d+)\.tif$", key)
	return (int(match.group(1)), int(match.group(2))) if match else (0, 0)


def resolve_tile_keys(tile_ids):
	"""Find each tile's object key (the region folder and year/version suffix vary); newest version wins."""
	_, folders = _list(PREFIX, "/")
	resolved = {}
	for tile_id in tile_ids:
		found = []
		for folder in folders:
			keys, _ = _list(f"{folder}{tile_id}_")
			found.extend(k for k in keys if k.endswith(".tif"))
		if not found:
			raise SystemExit(f"no CUDEM tile found for {tile_id}")
		resolved[tile_id] = max(found, key=_version_key)
	return resolved


def _manifest():
	m = manifest_module.Manifest.load(SOURCE)
	m.describe(
		description="NOAA NCEI Continuously Updated Digital Elevation Model (CUDEM) 1/9 arc-second topobathy tiles, NAVD88 metres",
		homepage=f"https://doi.org/{DOI}",
		license="Public domain: NOAA NCEI data carry no use constraints (US Government work). Cite: NOAA NCEI, Continuously Updated Digital Elevation Model (CUDEM) - 1/9 Arc-Second Resolution Bathymetric-Topographic Tiles, DOI 10.25921/ds9v-ky35.",
		terms_url="https://www.ncei.noaa.gov/access/metadata/landing-page/bin/iso?id=gov.noaa.ngdc.mgg.dem:999919",
		notes=[
			"Tiles are read from the NOAA open-data bucket noaa-nos-coastal-lidar-pds (same product as the NCEI THREDDS server).",
			"Elevations are metres relative to NAVD88, positive up: the seabed is negative. Horizontal datum NAD83, treated as WGS84.",
		],
	)
	return m


def fetch(slice_, force=False, log=print):
	m = _manifest()
	resolved = resolve_tile_keys(cudem_tile_ids(slice_))
	for tile_id, key in resolved.items():
		name = key.rsplit("/", 1)[-1]
		status = fetcher.fetch_file(m, name, f"{BUCKET}/{key}", paths.raw_dir(SOURCE) / name, force=force)
		log(f"  {name}: {status}")


def _tile_files(m, slice_):
	cache = paths.raw_dir(SOURCE)
	files = []
	for tile_id in cudem_tile_ids(slice_):
		matches = sorted(n for n in m.files if n.startswith(tile_id))
		if not matches:
			raise SystemExit(f"no manifest entry for CUDEM tile {tile_id}; run fetch first")
		name = max(matches, key=_version_key)
		if not m.verify(name, cache / name):
			raise SystemExit(f"{cache / name} is missing or does not match the manifest; run fetch first")
		files.append(cache / name)
	return files


def _polygon_area_m2(ring):
	lon = np.array([p[0] for p in ring])
	lat = np.array([p[1] for p in ring])
	north, east = geodesy.to_local(lat, lon)
	return float(abs(np.sum(east[:-1] * north[1:] - east[1:] * north[:-1])) / 2.0)


def coverage_gaps(mask, transform, limit=20):
	"""Connected nodata regions as (bbox, area_m2), largest first."""
	gaps = []
	for geometry, value in features.shapes(mask.astype("uint8"), mask=mask, transform=transform, connectivity=8):
		ring = geometry["coordinates"][0]
		xs = [p[0] for p in ring]
		ys = [p[1] for p in ring]
		gaps.append({"bbox_lonlat": [min(xs), min(ys), max(xs), max(ys)], "area_m2": round(_polygon_area_m2(ring), 1)})
	gaps.sort(key=lambda g: -g["area_m2"])
	return gaps[:limit], len(gaps)


def build(slice_, log=print):
	m = _manifest()
	tiles = _tile_files(m, slice_)
	out = paths.processed_dir(slice_.name)
	out.mkdir(parents=True, exist_ok=True)
	datasets = [rasterio.open(t) for t in tiles]
	try:
		res = datasets[0].res
		array, transform = merge(datasets, bounds=slice_.bbox, res=res, nodata=NODATA, dtype="float32", method="first")
		tile_tags = {t.name: dict(d.tags()) for t, d in zip(tiles, datasets)}
	finally:
		for d in datasets:
			d.close()
	band = array[0]
	profile = {
		"driver": "GTiff", "height": band.shape[0], "width": band.shape[1], "count": 1, "dtype": "float32",
		"crs": OUTPUT_CRS, "transform": transform, "nodata": NODATA, "compress": "deflate", "predictor": 3, "tiled": True,
		"blockxsize": 256, "blockysize": 256,
	}
	target = out / "depth_navd88.tif"
	with rasterio.open(target, "w", **profile) as dst:
		dst.write(band, 1)
		dst.update_tags(VERTICAL_DATUM="NAVD88", UNITS="metres", POSITIVE="up", SOURCE=f"NOAA NCEI CUDEM 1/9 arc-second, doi:{DOI}")
		dst.update_tags(1, DESCRIPTION="elevation of seabed and land, metres above NAVD88")

	nodata_mask = band == NODATA
	valid = band[~nodata_mask]
	gaps, gap_count = coverage_gaps(nodata_mask, transform)
	west, south, east, north = array_bounds(band.shape[0], band.shape[1], transform)
	corners = {}
	for label, (lat, lon) in {"sw": (south, west), "se": (south, east), "nw": (north, west), "ne": (north, east)}.items():
		n, e = geodesy.to_local(lat, lon)
		corners[label] = {"lon": lon, "lat": lat, "north_m": round(float(n), 3), "east_m": round(float(e), 3)}
	lat_mid = (south + north) / 2.0
	metadata = {
		"file": "depth_navd88.tif",
		"source": "NOAA NCEI CUDEM 1/9 arc-second topobathy",
		"doi": DOI,
		"tiles": [t.name for t in tiles],
		"tile_tags": tile_tags,
		"vertical_datum": "NAVD88",
		"vertical_units": "metres",
		"elevation_positive": "up (seabed is negative; depth = water level NAVD88 - elevation)",
		"horizontal_crs": "EPSG:4269 (NAD83), treated as WGS84 lon/lat; the two agree to about a metre",
		"nodata": NODATA,
		"width": int(band.shape[1]),
		"height": int(band.shape[0]),
		"resolution_deg": [abs(transform.a), abs(transform.e)],
		"resolution_m_approx": {
			"east_west": round(abs(transform.a) * 111320.0 * float(np.cos(np.radians(lat_mid))), 2),
			"north_south": round(abs(transform.e) * 110950.0, 2),
		},
		"bounds_lonlat": {"west": west, "south": south, "east": east, "north": north},
		"corners": corners,
		"elevation_min_m": float(valid.min()) if valid.size else None,
		"elevation_max_m": float(valid.max()) if valid.size else None,
		"valid_cell_fraction": float(valid.size / band.size),
		"nodata_cells": int(nodata_mask.sum()),
		"coverage_gap_regions": gap_count,
		"coverage_gaps_largest": gaps,
		"notes": [
			"Coverage gaps are cells with no CUDEM value (often under piers and bridges, or beyond the survey).",
			"Bridges and piers are not in a bare-earth/bathymetry model; do not read the raster as obstruction clearance.",
		],
	}
	(out / "depth_navd88.json").write_text(json.dumps(metadata, indent="\t") + "\n", encoding="utf-8")
	log(f"  depth_navd88.tif: {band.shape[1]}x{band.shape[0]}, valid {metadata['valid_cell_fraction']:.1%}, min {metadata['elevation_min_m']:.1f} m, max {metadata['elevation_max_m']:.1f} m")
