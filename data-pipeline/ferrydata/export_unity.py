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


def harbor_structures(root):
	"""The active harbor tiles' landing structures as one collection, each landing from the one tile that owns it (the
	ownership validate --harbor checks); refuses a landing outside its owner, in two tiles, or missing."""
	root = Path(root)
	index = json.loads((root / 'index.json').read_text(encoding='utf-8'))
	features, owners = [], {}
	for entry in index['tiles']:
		if not entry['active']:
			continue
		path = root / 'tiles' / entry['id'] / 'landing_structures.geojson'
		if not path.is_file():
			if entry['stop_ids']:
				raise ValueError(f"{path}: missing, but {entry['id']} owns landings {entry['stop_ids']}; run build --harbor")
			continue
		for feature in json.loads(path.read_text(encoding='utf-8'))['features']:
			stop = feature['properties']['stop_id']
			if stop not in entry['stop_ids']:
				raise ValueError(f"{path}: landing {stop} is not one {entry['id']} owns ({entry['stop_ids']})")
			if owners.setdefault(stop, entry['id']) != entry['id']:
				raise ValueError(f"landing {stop} is in {owners[stop]} and {entry['id']}")
			features.append(feature)
	missing = sorted({stop for e in index['tiles'] if e['active'] for stop in e['stop_ids']} - set(owners), key=lambda s: (len(s), s))
	if missing:
		raise ValueError(f'landings without structures: {missing}')
	return dict(type='FeatureCollection', name='landing_structures', features=features), owners


def check_unique(records):
	"""Every contact id once, and every physical segment (its two WGS84 end points, either way round) once."""
	ids, edges = {}, {}
	for record in records:
		if record['id'] in ids:
			raise ValueError(f"contact id {record['id']} appears twice: {ids[record['id']]} and {record['stop_id']} {record['polyline_id']}")
		ids[record['id']] = f"{record['stop_id']} {record['polyline_id']}"
		edge = tuple(sorted((tuple(record['start_lonlat']), tuple(record['end_lonlat']))))
		if edge in edges:
			raise ValueError(f"segment {record['id']} ({record['stop_id']}) is the same edge as segment {edges[edge]}")
		edges[edge] = record['id']


def export_harbor_contacts(root, destination):
	"""Writes the harbor's contacts.json version 2 from the 29 tiles' landing files: every landing, every id once, every
	segment once. Records and their order are export_contacts's, over the whole harbor; returns the records."""
	structures, _ = harbor_structures(root)
	records = contact_records(structures)
	check_unique(records)
	destination = Path(destination)
	destination.parent.mkdir(parents=True, exist_ok=True)
	destination.write_text(json.dumps(records, indent='\t', ensure_ascii=False, allow_nan=False) + '\n', encoding='utf-8')
	return records


# Unity's LandingGeometry (unity/Assets/FerrySim/Slice/Landings/LandingGeometry.cs) reads contacts.json with these limits.
GATE_REQUIRED = ('id', 'stop_id', 'stop_name', 'start_ne_m', 'end_ne_m', 'water_normal_ne', 'source', 'confidence', 'verification')
GATE_NORMAL_TOLERANCE_DEG = 1.
GATE_UNIT_NORMAL_TOLERANCE = 1e-3
GATE_MINIMUM_LENGTH_M = 1e-3


def landing_gate(record):
	"""What LandingGeometry does with one contacts.json record, ported without C#: ('malformed', reason) for a record
	that would stop the file loading, ('left out', reason), or ('kept', None). A segment is kept when the pipeline
	verified its water side or crew reported it, or, off the primary face only, when its charted solid side is
	verified; and its water_normal_ne is within 1 degree of the left-hand normal of start to end."""
	def number(x):
		return type(x) in (int, float) and math.isfinite(x)
	def point(x):
		return isinstance(x, list) and len(x) == 2 and all(number(v) for v in x)
	def flag(key):
		value = record.get(key)
		if value is not None and type(value) is not bool:
			raise TypeError(key)
		return bool(value)
	missing = [key for key in GATE_REQUIRED if key not in record]
	if missing:
		return 'malformed', f'{missing} missing'
	if type(record['id']) is not int or not 1 <= record['id'] <= 2 ** 31 - 1:
		return 'malformed', 'id must be a whole number from 1 to 2147483647'
	for key in ('stop_id', 'source', 'confidence', 'verification'):
		if not isinstance(record[key], str) or not record[key].strip():
			return 'malformed', f'{key} must be non-empty text'
	if not isinstance(record['stop_name'], str):
		return 'malformed', 'stop_name must be text'
	if not all(point(record[key]) for key in ('start_ne_m', 'end_ne_m', 'water_normal_ne')):
		return 'malformed', 'start_ne_m, end_ne_m and water_normal_ne must be two finite numbers'
	if abs(math.hypot(*record['water_normal_ne']) - 1) > GATE_UNIT_NORMAL_TOLERANCE:
		return 'malformed', 'water_normal_ne must be a unit vector'
	try:
		verified, crew, primary = flag('water_side_verified_by_pipeline'), flag('water_side_crew_reported'), flag('primary_face')
	except TypeError as error:
		return 'malformed', f'{error} must be true or false'
	for key, empty in (('kind', False), ('berth_id', True)):
		if record.get(key) is not None and (not isinstance(record[key], str) or (not empty and not record[key].strip())):
			return 'malformed', f'{key} must be text'
	evidence = record.get('solid_side_evidence')
	if evidence is not None and (not isinstance(evidence, dict) or type(evidence.get('verified')) is not bool):
		return 'malformed', 'solid_side_evidence.verified must be true or false'
	solid = bool(evidence and evidence['verified'])
	(n0, e0), (n1, e1) = record['start_ne_m'], record['end_ne_m']
	length = math.hypot(n1 - n0, e1 - e0)
	if not math.isfinite(length) or length < GATE_MINIMUM_LENGTH_M:
		return 'malformed', 'shorter than 1 mm'
	if not verified and not crew and (primary or not solid):
		return 'left out', 'water side unverified' + (', and a primary face is never kept on its solid side alone' if primary and solid else '')
	normal = record['water_normal_ne']
	cos = ((e1 - e0) / length * normal[0] - (n1 - n0) / length * normal[1]) / math.hypot(*normal)
	if math.degrees(math.acos(max(-1., min(1., cos)))) > GATE_NORMAL_TOLERANCE_DEG:
		return 'left out', 'water_normal_ne off the left-hand normal'
	return 'kept', None


def gate_counts(records, structures):
	"""Per landing: segments, kept by the gate, kept by landings.runtime_contact (the pipeline's mark), and segments where
	the two disagree. Refuses a malformed record."""
	from ferrydata import landings
	marks = {}
	for feature in structures['features']:
		for segment in feature['properties']['segments']:
			marks[segment['id']] = landings.runtime_contact(segment)
	counts = {}
	for record in records:
		verdict, reason = landing_gate(record)
		if verdict == 'malformed':
			raise ValueError(f"contacts.json segment {record.get('id')}: {reason}; LandingGeometry would refuse the file")
		row = counts.setdefault(record['stop_id'], dict(stop_name=record['stop_name'], segments=0, gate=0, pipeline=0, disagree=[]))
		row['segments'] += 1
		row['gate'] += verdict == 'kept'
		row['pipeline'] += marks[record['id']]
		if (verdict == 'kept') != marks[record['id']]:
			row['disagree'].append(record['id'])
	return counts


def run_harbor():
	root = paths.processed_dir('harbor')
	records = export_harbor_contacts(root, root / 'unity' / 'contacts.json')
	counts = gate_counts(records, harbor_structures(root)[0])
	disagree = sum(len(row['disagree']) for row in counts.values())
	print(f"Unity harbor contacts: {len(records)} segments for {len(counts)} landings, contacts.json version {CONTACTS_VERSION}; "
		f"LandingGeometry's gate keeps {sum(row['gate'] for row in counts.values())}, the pipeline marks {sum(row['pipeline'] for row in counts.values())} "
		f"kept, {disagree} disagree")
	return 0 if disagree == 0 else 1
