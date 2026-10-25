"""NOAA ENC (S-57) cells: aids to navigation, channels, shoreline constructions and bridges."""

import io
import zipfile
import xml.etree.ElementTree as ET

import geopandas as gpd
import numpy as np
import pandas as pd
import pyogrio
from shapely.geometry import Point, Polygon, box

from ferrydata import fetcher, geojsonio, lights as lights_module, manifest as manifest_module, paths
from ferrydata.slices import clip_geometry

SOURCE = "noaa-enc"
URL = "https://www.charts.noaa.gov/ENCs/{cell}.zip"
CATALOG_URL = "https://www.charts.noaa.gov/ENCs/ENCProdCat_19115.xml"

USER_AGREEMENT = (
	"NOAA ENC User Agreement (USERAGREEMENT.TXT in every cell download): NOAA ENCs are an official product of NOAA. 'You may download, "
	"use, and redistribute them without restriction, and without payment to NOAA, except as noted herein.' Redistributed NOAA ENCs are not "
	"official NOAA ENCs and do not meet chart carriage regulations unless redistributed by a certified distributor. Not for navigation."
)

AID_LAYERS = ("BOYLAT", "BOYSPP", "BOYCAR", "BOYISD", "BOYSAW", "BCNLAT", "BCNSPP", "BCNCAR", "BCNISD", "BCNSAW", "DAYMAR", "LITFLT", "LITVES", "LIGHTS")
CHANNEL_LAYERS = ("FAIRWY", "DRGARE", "NAVLNE", "RECTRC", "DWRTCL", "DWRTPT")
SHORE_LAYERS = ("SLCONS", "PONTON", "FLODOC", "MORFAC", "COALNE", "HULKES")
DEPTH_AREA_LAYERS = ("DEPARE", "DRGARE")

# S-57 VERDAT / DSPM_VDAT codes used by NOAA cells (the full list has 30 entries).
DATUMS = {
	1: "MLWS", 2: "MLLWS", 3: "MSL", 4: "LLW", 5: "MLW", 6: "LLWS", 7: "AMLWS", 9: "LWS", 12: "MLLW", 13: "LW", 16: "MHW", 17: "MHWS",
	18: "HW", 20: "HWS", 21: "MHHW", 23: "LAT", 24: "local datum", 26: "MWL",
}
LATERAL_CATEGORY = {1: "port-hand", 2: "starboard-hand", 3: "preferred channel to starboard", 4: "preferred channel to port"}
CATSLC = {
	1: "breakwater", 2: "groyne", 3: "mole", 4: "pier (jetty)", 5: "promenade pier", 6: "wharf (quay)", 7: "training wall", 8: "rip rap",
	9: "revetment", 10: "sea wall", 11: "landing steps", 12: "ramp", 13: "slipway", 14: "fender", 15: "solid face wharf", 16: "open face wharf",
}
TRAFIC = {1: "inbound", 2: "outbound", 3: "one-way", 4: "two-way"}


def catalog_cells(xml, polygon):
	"""Band 5 cells intersecting a lon/lat polygon in NOAA's ISO 19115 catalogue.

	GML pos uses latitude then longitude (EPSG geographic axis order).
	"""
	ns = {"g": "http://www.isotc211.org/2005/gmd", "c": "http://www.isotc211.org/2005/gco",
		"m": "http://www.opengis.net/gml/3.2"}
	# Stream the national catalogue: a full ElementTree retains hundreds of
	# megabytes of cells irrelevant to the harbor.
	source = io.BytesIO(xml.encode() if isinstance(xml, str) else xml) if isinstance(xml, (str, bytes)) else xml
	cells = set()
	for _, metadata in ET.iterparse(source, events=("end",)):
		if metadata.tag != f"{{{ns['g']}}}MD_Metadata":
			continue
		info = metadata.find("g:identificationInfo/g:MD_DataIdentification", ns)
		if info is None:
			metadata.clear()
			continue
		name = info.findtext("g:citation/g:CI_Citation/g:title/c:CharacterString", namespaces=ns)
		if not name or not name.startswith("US5"):
			metadata.clear()
			continue
		for ring in info.findall(".//m:exterior/m:LinearRing", ns):
			positions = [list(map(float, p.text.split())) for p in ring.findall("m:pos", ns)]
			if len(positions) >= 4 and Polygon([(p[1], p[0]) for p in positions]).intersects(polygon):
				cells.add(name)
		metadata.clear()
	return tuple(sorted(cells))


def datum_name(code):
	return DATUMS.get(int(code), f"code {int(code)}")


def _num(value):
	if value is None:
		return None
	try:
		number = float(value)
	except (TypeError, ValueError):
		return None
	return None if np.isnan(number) else number


def _text(value):
	if value is None or (isinstance(value, float) and np.isnan(value)):
		return None
	text = str(value).strip()
	return text or None


def _ints(value):
	return lights_module._int_list(value)


def _refs(value):
	if value is None or (isinstance(value, float) and np.isnan(value)):
		return []
	return [str(v) for v in list(value)]


def dedupe(gdf):
	"""Cells overlap at their edges. Points are the same object when the LNAM matches; lines and polygons are
	pieces cut at cell borders, so only exact duplicates (same LNAM and same geometry) are dropped."""
	if gdf.empty:
		return gdf
	is_point = gdf.geometry.geom_type == "Point"
	points = gdf[is_point].drop_duplicates(subset="LNAM")
	rest = gdf[~is_point]
	if not rest.empty:
		key = rest["LNAM"].astype(str) + "|" + rest.geometry.to_wkb(hex=True)
		rest = rest[~key.duplicated()]
	return pd.concat([points, rest]) if len(points) and len(rest) else (points if len(points) else rest)


# ---- record builders (pure, tested) ------------------------------------------------------------------------


def aid_records(layers):
	"""One record per physical aid. Lights referenced by a buoy/beacon are attached to it; others stand alone."""
	light_rows = {}
	if "LIGHTS" in layers:
		for row in layers["LIGHTS"].itertuples(index=False):
			light_rows[row.LNAM] = row
	parents = []
	for layer, gdf in layers.items():
		if layer == "LIGHTS":
			continue
		for row in gdf.itertuples(index=False):
			parents.append((layer, row))
	attached = set()
	records = []
	for layer, row in parents:
		own_lights = []
		for ref in _refs(getattr(row, "LNAM_REFS", None)):
			if ref in light_rows:
				own_lights.append(lights_module.parse_light(light_rows[ref]._asdict()))
				attached.add(ref)
		records.append(_aid_record(layer, row, own_lights))
	for lnam, row in light_rows.items():
		if lnam in attached:
			continue
		records.append(_aid_record("LIGHTS", row, [lights_module.parse_light(row._asdict())]))
	return sorted(records, key=lambda r: r["id"])


def _aid_record(layer, row, lights):
	attrs = row._asdict()
	colours = [lights_module.COLOUR[c] for c in _ints(attrs.get("COLOUR")) if c in lights_module.COLOUR]
	lateral = _ints(attrs.get("CATLAM"))
	return {
		"id": attrs["LNAM"],
		"feature_class": layer,
		"name": _text(attrs.get("OBJNAM")),
		"colours": colours,
		"lateral_category": LATERAL_CATEGORY.get(lateral[0]) if lateral else None,
		"shape_code": (_ints(attrs.get("BOYSHP")) or _ints(attrs.get("BCNSHP")) or [None])[0],
		"status": _ints(attrs.get("STATUS")) or None,
		"information": _text(attrs.get("INFORM")),
		"source_date": _text(attrs.get("SORDAT")),
		"lights": lights,
		"geometry": attrs["geometry"],
	}


def bridge_records(gdf, vertical_datum):
	records = []
	for row in gdf.itertuples(index=False):
		attrs = row._asdict()
		vertical = _num(attrs.get("VERCLR"))
		closed = _num(attrs.get("VERCCL"))
		opened = _num(attrs.get("VERCOP"))
		horizontal = _num(attrs.get("HORCLR"))
		records.append(
			{
				"id": attrs["LNAM"],
				"name": _text(attrs.get("OBJNAM")),
				"category_codes": _ints(attrs.get("CATBRG")) or None,
				"vertical_clearance_m": vertical if vertical is not None else closed,
				"vertical_clearance_closed_m": closed,
				"vertical_clearance_open_m": opened,
				"horizontal_clearance_m": horizontal,
				"clearance_datum": vertical_datum,
				"clearance_note": f"vertical clearances are metres above {vertical_datum}; horizontal clearance is the navigable width between fenders/piers",
				"has_clearance": any(v is not None for v in (vertical, closed, opened, horizontal)),
				"information": _text(attrs.get("INFORM")),
				"geometry": attrs["geometry"],
			}
		)
	return records


def _channel_record(layer, row):
	attrs = row._asdict()
	traffic = _ints(attrs.get("TRAFIC"))
	return {
		"id": attrs["LNAM"],
		"feature_class": layer,
		"name": _text(attrs.get("OBJNAM")),
		"min_depth_m": _num(attrs.get("DRVAL1")),
		"depth_datum": "MLLW (sounding datum of the cell)",
		"traffic_flow": TRAFIC.get(traffic[0]) if traffic else None,
		"orientation_deg": _num(attrs.get("ORIENT")),
		"geometry": attrs["geometry"],
	}


def depth_area_records(gdf, layer="DEPARE"):
	"""Charted depth ranges (DRVAL1 shallowest, DRVAL2 deepest), metres below the cell's sounding datum (MLLW)."""
	records = []
	for row in gdf.itertuples(index=False):
		attrs = row._asdict()
		records.append(
			{
				"id": attrs["LNAM"],
				"feature_class": layer,
				"cell": _text(attrs.get("cell")),
				"name": _text(attrs.get("OBJNAM")),
				"min_depth_m": _num(attrs.get("DRVAL1")),
				"max_depth_m": _num(attrs.get("DRVAL2")),
				"depth_datum": "MLLW (sounding datum of the cell)",
				"geometry": attrs["geometry"],
			}
		)
	return records


def _shore_record(layer, row):
	attrs = row._asdict()
	category = _ints(attrs.get("CATSLC"))
	return {
		"id": attrs["LNAM"],
		"feature_class": layer,
		"name": _text(attrs.get("OBJNAM")),
		"cell": _text(attrs.get("cell")),
		"source_date": _text(attrs.get("SORDAT")),
		"mooring_category_codes": _ints(attrs.get("CATMOR")) or None,
		"category": CATSLC.get(category[0]) if category and layer == "SLCONS" else None,
		"category_code": category[0] if category else None,
		"water_level_effect_code": (_ints(attrs.get("WATLEV")) or [None])[0],
		"condition_code": (_ints(attrs.get("CONDTN")) or [None])[0],
		"information": _text(attrs.get("INFORM")),
		"geometry": attrs["geometry"],
	}


# ---- fetch / build -----------------------------------------------------------------------------------------


def _manifest():
	m = manifest_module.Manifest.load(SOURCE)
	m.describe(
		description="NOAA Office of Coast Survey Electronic Navigational Charts (S-57) for the cells covering the slice",
		homepage="https://nauticalcharts.noaa.gov/enconline/enconline.html",
		license="NOAA ENCs may be downloaded, used and redistributed without restriction or payment (NOAA ENC User Agreement). Not for navigation.",
		terms_url="https://www.charts.noaa.gov/ENCs/ENCs.shtml",
		terms_text=USER_AGREEMENT,
		notes=[
			f"Cells were chosen from NOAA's ENC product catalogue ({CATALOG_URL}) as the largest-scale cells (usage band 5, 1:12000) intersecting the slice bounding box.",
			"Each zip holds a base cell (.000) and its update files (.001...), which GDAL's S-57 driver applies on read. The sha256 pins the weekly edition used.",
			"Soundings are in the cell's sounding datum (MLLW); heights and bridge clearances are above MHW (DSPM_VDAT 16).",
		],
	)
	return m


def fetch(slice_, force=False, log=print):
	m = _manifest()
	for cell in slice_.enc_cells:
		status = fetcher.fetch_file(m, f"{cell}.zip", URL.format(cell=cell), paths.raw_dir(SOURCE) / f"{cell}.zip", force=force)
		log(f"  {cell}.zip: {status}")


def _cell_path(m, cell):
	if f"{cell}.zip" not in m.files:
		m = manifest_module.load_combined(SOURCE)
	zip_path = paths.raw_dir(SOURCE) / f"{cell}.zip"
	if not m.verify(f"{cell}.zip", zip_path):
		raise SystemExit(f"{zip_path} is missing or does not match the manifest; run fetch first")
	target = paths.raw_dir(SOURCE) / "extracted" / m.files[f"{cell}.zip"]["sha256"][:12] / cell
	base = target / "ENC_ROOT" / cell / f"{cell}.000"
	if not base.exists():
		target.mkdir(parents=True, exist_ok=True)
		with zipfile.ZipFile(zip_path) as archive:
			archive.extractall(target)
	return base


def read_layers(slice_, layer_names, m=None):
	"""Read the named S-57 layers from every cell, clipped to the slice bbox, deduplicated across cells."""
	m = m or _manifest()
	collected = {}
	datum = {}
	for cell in slice_.enc_cells:
		base = _cell_path(m, cell)
		available = {name for name, _ in pyogrio.list_layers(base)}
		dsid = pyogrio.read_dataframe(base, layer="DSID").iloc[0]
		datum[cell] = {"vertical": datum_name(dsid["DSPM_VDAT"]), "sounding": datum_name(dsid["DSPM_SDAT"]), "edition": int(dsid["DSID_EDTN"]), "update": int(dsid["DSID_UPDN"]), "issue_date": str(dsid["DSID_ISDT"])}
		for name in layer_names:
			if name not in available:
				continue
			gdf = pyogrio.read_dataframe(base, layer=name, bbox=slice_.bbox)
			if gdf.empty:
				continue
			gdf = gdf[gdf.geometry.notna()].copy()
			gdf["cell"] = cell
			collected.setdefault(name, []).append(gdf)
	layers = {}
	for name, frames in collected.items():
		merged = dedupe(gpd.GeoDataFrame(pd.concat(frames, ignore_index=True), crs="EPSG:4326"))
		layers[name] = merged
	return layers, datum


def _features(records, slice_, extra_props=None):
	features = []
	for record in records:
		geometry = clip_geometry(record.pop("geometry"), slice_)
		if geometry is None:
			continue
		features.append(geojsonio.make_feature(geometry, {**record, **(extra_props or {})}))
	return features


def build(slice_, log=print):
	m = _manifest()
	out = paths.processed_dir(slice_.name)
	out.mkdir(parents=True, exist_ok=True)
	layers, datum = read_layers(slice_, AID_LAYERS + CHANNEL_LAYERS + SHORE_LAYERS + DEPTH_AREA_LAYERS + ("BRIDGE", "SOUNDG", "LNDARE"), m)
	cells_info = {"cells": datum}

	aids = _features(aid_records({k: v for k, v in layers.items() if k in AID_LAYERS}), slice_)
	geojsonio.write_collection(out / "aids.geojson", aids, "aids", {"source": "NOAA ENC", **cells_info, "light_notes": "range_nm is nominal range in nautical miles; height_m is above the vertical datum (MHW); sector angles are bearings from seaward"})
	with_lights = sum(1 for f in aids if f["properties"]["lights"])
	parsed = sum(1 for f in aids for l in f["properties"]["lights"] if l["parsed"])
	log(f"  aids.geojson: {len(aids)} aids, {with_lights} with lights ({parsed} parsed characteristics)")

	channel_records = []
	for name in CHANNEL_LAYERS:
		if name in layers:
			channel_records += [_channel_record(name, row) for row in layers[name].itertuples(index=False)]
	channels = _features(channel_records, slice_)
	geojsonio.write_collection(out / "channels.geojson", channels, "channels", {"source": "NOAA ENC", **cells_info})
	log(f"  channels.geojson: {len(channels)} features")

	shore_records = []
	for name in SHORE_LAYERS:
		if name in layers:
			shore_records += [_shore_record(name, row) for row in layers[name].itertuples(index=False)]
	shore = _features(shore_records, slice_)
	geojsonio.write_collection(out / "shoreline_constructions.geojson", shore, "shoreline_constructions", {"source": "NOAA ENC", "feature_classes": list(SHORE_LAYERS), **cells_info})
	log(f"  shoreline_constructions.geojson: {len(shore)} features")

	area_records = []
	for name in DEPTH_AREA_LAYERS:
		if name in layers:
			area_records += depth_area_records(layers[name], name)
	areas = _features(area_records, slice_)
	geojsonio.write_collection(out / "depth_areas.geojson", areas, "depth_areas", {"source": "NOAA ENC", "note": "charted depth ranges (supplementary: a cross-check on CUDEM near berths)", **cells_info})
	log(f"  depth_areas.geojson: {len(areas)} features")

	land_records = []
	if "LNDARE" in layers:
		land_records = [{"id": r.LNAM, "geometry": r.geometry, "cell": r.cell} for r in layers["LNDARE"].itertuples(index=False)]
	geojsonio.write_collection(out / "land_areas.geojson", _features(land_records, slice_), "land_areas", {"source": "NOAA ENC", **cells_info})
	soundings = []
	if "SOUNDG" in layers:
		for row in layers["SOUNDG"].itertuples(index=False):
			if datum[row.cell]["sounding"] != "MLLW":
				raise ValueError(f"unsupported sounding datum in {row.cell}")
			points = row.geometry.geoms if row.geometry.geom_type == "MultiPoint" else [row.geometry]
			for i, point in enumerate(points):
				if not point.has_z or not slice_.contains(point.x, point.y):
					continue
				soundings.append(geojsonio.make_feature(Point(point.x, point.y), {"id": f"{row.LNAM}:{i}", "feature_class": "SOUNDG", "cell": row.cell, "depth_m": point.z, "depth_datum": "MLLW"}))
	geojsonio.write_collection(out / "soundings.geojson", soundings, "soundings", {"source": "NOAA ENC", **cells_info})
	log(f"  land_areas.geojson: {len(land_records)}; soundings.geojson: {len(soundings)}")

	vertical = sorted({d["vertical"] for d in datum.values()})
	if len(vertical) != 1:
		raise SystemExit(f"cells disagree on the vertical datum for clearances: {vertical}")
	bridges = _features(bridge_records(layers["BRIDGE"], vertical[0]) if "BRIDGE" in layers else [], slice_)
	geojsonio.write_collection(out / "bridges.geojson", bridges, "bridges", {"source": "NOAA ENC", "clearance_datum": vertical[0], **cells_info})
	log(f"  bridges.geojson: {len(bridges)} features ({sum(1 for f in bridges if f['properties']['has_clearance'])} with clearances)")
