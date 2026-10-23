"""Ellipsoidal height of NAVD88 zero (GEOID18) so Unity can place the water surface. Never hard-code ~-32 m."""

import json

import pyproj

from ferrydata import fetcher, geodesy, manifest as manifest_module, paths

SOURCE = "geoid"
GRID_NAME = "us_noaa_g2018u0.tif"
GRID_URL = f"https://cdn.proj.org/{GRID_NAME}"
# NAD83(2011) + NAVD88 height -> NAD83(2011) 3D. PROJ implements it with the GEOID18 grid above.
NAVD88_CRS = "EPSG:6349"
ELLIPSOIDAL_CRS = "EPSG:6319"


def _manifest():
	m = manifest_module.Manifest.load(SOURCE)
	m.describe(
		description="GEOID18 CONUS geoid undulation grid (NAD83(2011) ellipsoid to NAVD88), PROJ-network GeoTIFF distribution",
		homepage="https://www.ngs.noaa.gov/GEOID/GEOID18/",
		license="Public domain: NOAA National Geodetic Survey product (US Government work). Grid converted to GeoTIFF and hosted by the PROJ CDN (https://cdn.proj.org/, grid license in the PROJ-data repository: https://github.com/OSGeo/PROJ-data/tree/master/us_noaa).",
		terms_url="https://github.com/OSGeo/PROJ-data/tree/master/us_noaa",
		notes=[
			"Downloaded explicitly (PROJ network access stays off); build points pyproj at this directory in-process.",
			"Approved by the coordinator for download into the raw cache before use.",
		],
	)
	return m


def fetch(slice_, force=False, log=print):
	m = _manifest()
	status = fetcher.fetch_file(m, GRID_NAME, GRID_URL, paths.raw_dir(SOURCE) / GRID_NAME, force=force)
	log(f"  {GRID_NAME}: {status}")


def make_transformer():
	"""pyproj transformer NAVD88 -> ellipsoidal heights that is guaranteed to use the GEOID18 grid, else fails loudly."""
	m = _manifest()
	grid_dir = paths.raw_dir(SOURCE)
	if not m.verify(GRID_NAME, grid_dir / GRID_NAME):
		raise SystemExit(f"{grid_dir / GRID_NAME} is missing or does not match the manifest; run fetch first")
	pyproj.network.set_network_enabled(False)
	pyproj.datadir.append_data_dir(str(grid_dir))
	group = pyproj.transformer.TransformerGroup(NAVD88_CRS, ELLIPSOIDAL_CRS, always_xy=True)
	best = group.transformers[0] if group.transformers else None
	if best is None or GRID_NAME not in best.to_wkt() and GRID_NAME not in best.definition:
		raise SystemExit("pyproj did not pick the GEOID18 grid; refusing to guess a geoid offset")
	return best


def compute_offsets(slice_, transform):
	"""Offsets at the slice centre and corners. `transform(lon, lat, 0.0)` -> (lon, lat, ellipsoidal height of NAVD88 zero)."""
	points = {"centre": slice_.centre, **slice_.corners()}
	out = {}
	for label, (lat, lon) in points.items():
		_, _, height = transform(lon, lat, 0.0)
		north, east = geodesy.to_local(lat, lon)
		out[label] = {
			"lon": lon,
			"lat": lat,
			"north_m": round(float(north), 3),
			"east_m": round(float(east), 3),
			"navd88_zero_ellipsoidal_height_m": round(float(height), 4),
		}
	return out


def build(slice_, log=print):
	transformer = make_transformer()
	points = compute_offsets(slice_, transformer.transform)
	heights = [p["navd88_zero_ellipsoidal_height_m"] for p in points.values()]
	payload = {
		"slice": slice_.name,
		"geoid_model": "GEOID18 (CONUS), via PROJ grid " + GRID_NAME,
		"meaning": "ellipsoidal height (m) of the NAVD88 = 0 surface; ellipsoidal height of a NAVD88 elevation H is H + this value",
		"ellipsoid_note": "heights are NAD83(2011)/GRS80; WGS84 ellipsoidal heights differ by well under 2 m (frame offset) and are treated as equal here",
		"transformation": transformer.description,
		"pyproj_version": pyproj.__version__,
		"proj_version": pyproj.proj_version_str,
		"points": points,
		"min_m": min(heights),
		"max_m": max(heights),
		"mean_m": round(sum(heights) / len(heights), 4),
	}
	out = paths.processed_dir(slice_.name)
	out.mkdir(parents=True, exist_ok=True)
	(out / "geoid_offset.json").write_text(json.dumps(payload, indent="\t") + "\n", encoding="utf-8")
	log(f"  geoid_offset.json: centre {points['centre']['navd88_zero_ellipsoidal_height_m']} m (range {min(heights)} to {max(heights)})")
