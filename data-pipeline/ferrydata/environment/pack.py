"""Environment interchange with raw-byte integrity, digests and offline structural verification."""
import csv
import hashlib
import io
import json
import math
import re
from collections import namedtuple
from datetime import date
from pathlib import Path

import numpy as np

from ferrydata import geodesy
from ferrydata.environment import series

COLUMNS = {
	'currents.csv': ('station_id', 't_s', 'north_mps', 'east_mps'),
	'water_levels.csv': ('station_id', 't_s', 'level_navd88_m', 'source'),
	'wind.csv': ('station_id', 't_s', 'north_mps', 'east_mps', 'gust_mps'),
}
STATIONS = dict(zip(COLUMNS, ('current_stations', 'water_level_stations', 'wind_stations')))


def write_json(path, value):
	Path(path).write_text(json.dumps(value, indent='\t', ensure_ascii=False, allow_nan=False) + '\n', encoding='utf-8')


def _file_digest(directory, names, suffix=b''):
	h = hashlib.sha256()
	for name in names:
		h.update(name.encode('utf-8') + b'\0')
		h.update(_safe_file(directory, name).read_bytes())
		h.update(b'\0')
	h.update(suffix)
	return h.hexdigest()


def _read_manifest(directory):
	p = Path(directory) / 'manifest.json'
	try:
		value = json.loads(p.read_bytes().decode('utf-8'), object_pairs_hook=_unique_object)
		if not isinstance(value, dict):
			raise ValueError('manifest must be an object')
		return value
	except (ValueError, UnicodeError) as error:
		raise ValueError(f'{p}: invalid JSON') from error


def _unique_object(pairs):
	result = {}
	for key, value in pairs:
		if key in result:
			raise ValueError(f'duplicate property {key}')
		result[key] = value
	return result


# These schemas mirror EnvironmentPackJson.Node, including required nullable keys; OPTIONAL lists the keys a schema
# may also have.
POSITION = dict(id='text', name='text', lat='number', lon='number', north_m='number', east_m='number', source='text', source_sha256='hex')
GRID = dict(id='text', file='text', origin_north_m='number', origin_east_m='number', cell_m='number', rows='count', cols='count', nodata='text', sha256='hex')
SOURCE = dict(name='text', sha256='hex')
DEPTH = dict(format='text', region='text', datum='text', value='text', grids=[GRID], outside_depth_m='number', sources=[SOURCE])
FILE = dict(name='text', sha256='hex')
DERIVATION = dict(method='text', reference_station='text', reference_bin='count', slack_before_flood_min='number', max_flood_min='number',
	slack_before_ebb_min='number', max_ebb_min='number', flood_ratio='number', ebb_ratio='number', flood_dir_deg='number',
	ebb_dir_deg='number', sources=[FILE])
CURRENT_STATION = dict(POSITION, kind='text', bin_depth_m='nullable-number')
DAY = dict(format='text', region='text', date='text', epoch_utc='text', time_coverage_s=['number'], depth_digest='hex',
	current_stations=[CURRENT_STATION], water_level_stations=[POSITION],
	wind_stations=[dict(POSITION, sensor_height_m='nullable-number')], files={name: 'hex' for name in COLUMNS}, notes=['text'])
FIELD_STATION = dict(id='text', north_m='number', east_m='number', frame_north='number', frame_east='number')
FIELD_GRID = dict(id='text', values_file='text', stations_file='text', origin_north_m='number', origin_east_m='number', cell_m='number',
	rows='count', cols='count', slots='count', values_sha256='hex', stations_sha256='hex')
FIELD_PARAMETERS = dict(cell_m='number', computation_north_m=['number'], computation_east_m=['number'], inflow='text', outflow='text',
	conveyance_exponent='number', min_depth_m='number', smoothing_m='number', shelter_threshold='number', depth_exponent='number',
	depth_smoothing_m='number', cross_taper_m='number', station_disk_m='number', station_reach_m='number', across_reach_m='number',
	tolerance='number')
FIELD = dict(format='text', region='text', method='text', stations=[FIELD_STATION], grids=[FIELD_GRID], parameters=FIELD_PARAMETERS,
	sources=[SOURCE], notes=['text'])
OPTIONAL = {id(SOURCE): {'note': 'nullable-text'}, id(DAY): {'field_digest': 'hex'}, id(CURRENT_STATION): {'derivation': DERIVATION}}

FIELD_FORMAT = 'ferrysim-env-field/1'
FIELD_METHOD = 'channel-harmonic/1'
SUBORDINATE_METHOD = 'noaa-subordinate'
# Shared with the C# reader (CurrentFieldGrid, EnvironmentPackJson): how far a water cell's axis may be from unit length
# and its weights from summing to 1 (float32 storage), and a day's station from the position the field solved it at.
UNIT_AXIS_TOLERANCE = 1e-5
WEIGHT_SUM_TOLERANCE = 1e-5
STATION_POSITION_TOLERANCE_M = .01
MAX_SLOTS = 8
EMPTY_SLOT = 255


def _validate(value, schema, label):
	if isinstance(schema, dict):
		if not isinstance(value, dict):
			raise ValueError(f'{label}: expected an object')
		optional = OPTIONAL.get(id(schema), {})
		missing, unknown = schema.keys() - value.keys(), value.keys() - schema.keys() - optional.keys()
		if missing or unknown:
			raise ValueError(f'{label}: missing keys {sorted(missing)}, unknown keys {sorted(unknown)}')
		for key, child in value.items():
			_validate(child, schema[key] if key in schema else optional[key], f'{label}.{key}')
		return
	if isinstance(schema, list):
		if not isinstance(value, list):
			raise ValueError(f'{label}: expected an array')
		for i, child in enumerate(value):
			_validate(child, schema[0], f'{label}[{i}]')
		return
	if schema.startswith('nullable-'):
		if value is None:
			return
		schema = schema[len('nullable-'):]
	valid = False
	if schema == 'text':
		valid = isinstance(value, str)
	elif schema == 'hex':
		valid = isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) is not None
	elif schema == 'count':
		valid = type(value) is int and 1 <= value <= 1 << 20
	elif schema == 'number':
		try:
			valid = type(value) in (int, float) and math.isfinite(value)
		except OverflowError:
			pass
	if not valid:
		raise ValueError(f'{label}: expected finite {schema}, found {value!r}')


def _stations(stations, label, required):
	ids = [station['id'] for station in stations]
	if (required and not ids) or len(ids) != len(set(ids)):
		raise ValueError(f'{label}: missing or duplicate stations')
	for station in stations:
		if not station['id'] or ',' in station['id'] or '"' in station['id']:
			raise ValueError(f'{label}: station id must be nonempty without comma or quote')
		lat, lon = station['lat'], station['lon']
		if not -90 <= lat <= 90 or not -180 <= lon <= 180:
			raise ValueError(f'{label}: station latitude/longitude must be WGS84 degrees')
		north, east = geodesy.to_local(lat, lon)
		if math.hypot(north-station['north_m'], east-station['east_m']) > .01:
			raise ValueError(f'{label}: station {station["id"]} world position differs from lat/lon by more than 0.01 m')
	return ids


def _csv_number(value, label):
	# Match the loader's invariant sign/decimal/exponent grammar, not float()'s
	# extra support for surrounding whitespace and digit separators.
	if re.fullmatch(r'[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?', value) is None:
		raise ValueError(f'{label}: invalid number {value!r}')
	number = float(value)
	if not math.isfinite(number):
		raise ValueError(f'{label}: nonfinite number {value!r}')
	return number


def depth_digest(directory):
	metadata = _read_manifest(directory)
	return _file_digest(directory, ['manifest.json', *[grid['file'] for grid in metadata['grids']]])


def day_digest(directory, depth, field=None):
	"""Without a field the 2026-10-25 rule exactly; with one, the field digest's hex and NUL after the depth digest's."""
	suffix = depth.encode('ascii') + b'\0'
	if field is not None:
		suffix += field.encode('ascii') + b'\0'
	return _file_digest(directory, ['manifest.json', *COLUMNS], suffix)


def field_digest(directory):
	metadata = _read_manifest(directory)
	names = ['manifest.json']
	for grid in metadata['grids']:
		names += [grid['values_file'], grid['stations_file']]
	return _file_digest(directory, names)


def write_digest(directory, digest):
	(Path(directory) / 'DIGEST').write_bytes((digest + '\n').encode('ascii'))


def read_digest(directory):
	p = Path(directory) / 'DIGEST'
	body = p.read_bytes()
	if re.fullmatch(b'[0-9a-f]{64}\n', body) is None:
		raise ValueError(f'{p}: expected 64 lowercase hex characters and LF')
	return body[:64].decode('ascii')


def _verify_digest(directory, actual):
	expected = read_digest(directory)
	if actual != expected:
		raise ValueError(f'{Path(directory) / "DIGEST"}: digest mismatch for manifest.json and data files')
	return expected


def write_field(directory, region, stations, grids, parameters, sources, notes):
	"""A field set: stations as dicts (id, north_m, east_m, frame_north, frame_east), grids as dicts (id, origin_north_m,
	origin_east_m, cell_m, values (rows, cols, 4 + slots) float32, slots (rows, cols, slots) uint8)."""
	directory = Path(directory)
	directory.mkdir(parents=True, exist_ok=True)
	entries = []
	for grid in grids:
		values = np.ascontiguousarray(grid['values'], dtype='<f4')
		slots = np.ascontiguousarray(grid['slots'], dtype=np.uint8)
		rows, cols, count = slots.shape
		if values.shape != (rows, cols, 4 + count):
			raise ValueError(f'grid {grid["id"]}: values and slots disagree on shape')
		names = (grid['id'] + '.f32', grid['id'] + '.u8')
		for name, body in zip(names, (values.tobytes(order='C'), slots.tobytes(order='C'))):
			(directory / name).write_bytes(body)
		entries.append(dict(id=grid['id'], values_file=names[0], stations_file=names[1], origin_north_m=float(grid['origin_north_m']),
			origin_east_m=float(grid['origin_east_m']), cell_m=float(grid['cell_m']), rows=int(rows), cols=int(cols), slots=int(count),
			values_sha256=hashlib.sha256(values.tobytes(order='C')).hexdigest(), stations_sha256=hashlib.sha256(slots.tobytes(order='C')).hexdigest()))
	metadata = dict(format=FIELD_FORMAT, region=region, method=FIELD_METHOD, stations=stations, grids=entries, parameters=parameters,
		sources=sources, notes=sorted(set(notes)))
	write_json(directory / 'manifest.json', metadata)
	write_digest(directory, field_digest(directory))
	return metadata


def write_day(root, region, day, depth_digest, current_stations, water_level_stations, wind_stations, currents, water_levels, wind, notes,
	field_digest=None):
	directory = Path(root) / 'days' / day
	directory.mkdir(parents=True, exist_ok=True)
	files = {}
	for name, rows in zip(COLUMNS, (currents, water_levels, wind)):
		out = io.StringIO(newline='')
		writer = csv.writer(out, lineterminator='\n')
		writer.writerow(COLUMNS[name])
		writer.writerows(sorted(rows, key=lambda r: (r[0], r[1])))
		body = out.getvalue().encode('utf-8')
		(directory / name).write_bytes(body)
		files[name] = hashlib.sha256(body).hexdigest()
	metadata = dict(format='ferrysim-env/1', region=region, date=day, epoch_utc=series.epoch_utc(day),
		time_coverage_s=list(series.COVERAGE), depth_digest=depth_digest)
	if field_digest is not None:
		metadata['field_digest'] = field_digest
	metadata.update(current_stations=current_stations, water_level_stations=water_level_stations,
		wind_stations=wind_stations, files=files, notes=sorted(set(notes)))
	write_json(directory / 'manifest.json', metadata)
	write_digest(directory, day_digest(directory, depth_digest, field_digest))
	return metadata


def _safe_file(directory, name):
	if not isinstance(name, str) or Path(name).name != name:
		raise ValueError(f'unsafe pack file: {name}')
	return Path(directory) / name


def _plain_id(value):
	return bool(value) and value not in ('.', '..') and re.fullmatch('[A-Za-z0-9_.-]+', value) is not None


def verify_field(directory, region):
	"""Checks a field set and returns its digest and manifest; refuses anything outside the format with the path."""
	directory = Path(directory)
	metadata = _read_manifest(directory)
	path = directory / 'manifest.json'
	_validate(metadata, FIELD, str(path))
	if metadata['format'] != FIELD_FORMAT or metadata['method'] != FIELD_METHOD or metadata['region'] != region:
		raise ValueError(f'{path}: format must be {FIELD_FORMAT}, method {FIELD_METHOD}, region {region}')
	stations = metadata['stations']
	ids = [x['id'] for x in stations]
	if not ids or len(ids) >= EMPTY_SLOT or len(ids) != len(set(ids)) or any(',' in i or '"' in i or not i for i in ids):
		raise ValueError(f'{path}: stations must be 1 to {EMPTY_SLOT - 1} unique ids without comma or quote')
	if any(not math.hypot(x['frame_north'], x['frame_east']) > 1e-3 for x in stations):
		raise ValueError(f'{path}: a station frame is near zero')
	parameters = metadata['parameters']
	if len(parameters['computation_north_m']) != 2 or len(parameters['computation_east_m']) != 2:
		raise ValueError(f'{path}: computation bounds must be [low, high]')
	if not metadata['grids']:
		raise ValueError(f'{path}: grids must list at least one grid')
	bounds, grid_ids = [], set()
	for grid in metadata['grids']:
		if grid['id'] in grid_ids or not _plain_id(grid['id']) or grid['values_file'] != grid['id'] + '.f32' or grid['stations_file'] != grid['id'] + '.u8':
			raise ValueError(f'{path}: grid IDs must be unique plain names, with files ID.f32 and ID.u8')
		grid_ids.add(grid['id'])
		rows, cols, slots = grid['rows'], grid['cols'], grid['slots']
		if rows < 2 or cols < 2 or not grid['cell_m'] > 0 or not 1 <= slots <= MAX_SLOTS:
			raise ValueError(f'{path}: grid {grid["id"]} needs at least 2 by 2 cells, a positive cell and 1 to {MAX_SLOTS} slots')
		bodies = {}
		for key in ('values', 'stations'):
			p = _safe_file(directory, grid[key + '_file'])
			bodies[key] = p.read_bytes()
			if hashlib.sha256(bodies[key]).hexdigest() != grid[key + '_sha256']:
				raise ValueError(f'{p}: checksum mismatch')
		values_path, slots_path = directory / grid['values_file'], directory / grid['stations_file']
		if len(bodies['values']) != rows * cols * (4 + slots) * 4 or len(bodies['stations']) != rows * cols * slots:
			raise ValueError(f'{values_path}: sizes do not fit {rows} by {cols} cells of {slots} slots')
		values = np.frombuffer(bodies['values'], dtype='<f4').reshape(rows, cols, 4 + slots).astype(float)
		slot = np.frombuffer(bodies['stations'], dtype=np.uint8).reshape(rows, cols, slots)
		weights = values[..., 4:]
		empty = slot == EMPTY_SLOT
		wet = ~empty.all(axis=-1)
		if not np.isfinite(values).all():
			raise ValueError(f'{values_path}: non-finite values')
		if ((values[..., 2:4] < 0) | (values[..., 2:4] > 1)).any():
			raise ValueError(f'{values_path}: along and cross factors must lie in [0, 1]')
		if (~empty & (slot >= len(ids))).any():
			raise ValueError(f'{slots_path}: a slot names no station of the field')
		if (empty & (weights != 0)).any() or (~empty & ((weights < 0) | (weights > 1))).any():
			raise ValueError(f'{values_path}: weights must be 0 in empty slots and in [0, 1] otherwise')
		if (np.abs(np.hypot(values[..., 0], values[..., 1]) - 1)[wet] > UNIT_AXIS_TOLERANCE).any():
			raise ValueError(f'{values_path}: water cells need a unit axis (within {UNIT_AXIS_TOLERANCE})')
		if (np.abs(weights.sum(axis=-1) - 1)[wet] > WEIGHT_SUM_TOLERANCE).any():
			raise ValueError(f'{values_path}: water cells need weights summing to 1 (within {WEIGHT_SUM_TOLERANCE})')
		if (values[..., :4][~wet] != 0).any():
			raise ValueError(f'{values_path}: dry cells (no station slot) must be all zeros')
		n, e, cell = grid['origin_north_m'], grid['origin_east_m'], grid['cell_m']
		b = (n, e, n + (rows - 1) * cell, e + (cols - 1) * cell)
		for a in bounds:
			if max(a[0], b[0]) < min(a[2], b[2]) and max(a[1], b[1]) < min(a[3], b[3]):
				raise ValueError(f'{path}: overlapping grids')
		bounds.append(b)
	return _verify_digest(directory, field_digest(directory)), metadata


def _check_derivation(station, label):
	d = station.get('derivation')
	if d is None:
		return
	if (d['method'] != SUBORDINATE_METHOD or not d['flood_ratio'] > 0 or not d['ebb_ratio'] > 0 or not d['sources']
		or not all(0 <= d[k] < 360 for k in ('flood_dir_deg', 'ebb_dir_deg')) or station['kind'] != 'subordinate-prediction'):
		raise ValueError(f'{label}: {station["id"]} derivation must be {SUBORDINATE_METHOD} for a subordinate-prediction, positive ratios, '
			'directions in [0, 360) and its sources')


def _number(value):
	text = repr(float(value))
	return text[:-2] if text.endswith('.0') else text


def _meet(a, b):
	"""Lattices, first to last centre inclusive, with a point in common: an overlap, a shared edge or a shared corner."""
	return (a['origin_north_m'] <= b['end_north_m'] and b['origin_north_m'] <= a['end_north_m']
		and a['origin_east_m'] <= b['end_east_m'] and b['origin_east_m'] <= a['end_east_m'])


def _check_shared(a, b, label):
	"""The depth-set rule for grids that meet (revised 2026-10-25, the same as DepthSet in the core): one lattice, so the
	same cell size and origins a whole number of cells apart, and every centre both hold the same float32 value or NaN in
	both. Grids that do not meet are independent."""
	if a['cell_m'] == b['cell_m']:
		# b's centre (r, c) is a's centre (r + rows, c + cols) when the offsets are whole numbers of cells.
		rows = (b['origin_north_m'] - a['origin_north_m']) / a['cell_m']
		cols = (b['origin_east_m'] - a['origin_east_m']) / a['cell_m']
		if rows == math.floor(rows) and cols == math.floor(cols):
			first_row, last_row = max(0., rows), min(a['rows'] - 1., rows + b['rows'] - 1.)
			first_col, last_col = max(0., cols), min(a['cols'] - 1., cols + b['cols'] - 1.)
			if first_row > last_row or first_col > last_col:
				return
			r0, r1, c0, c1, dr, dc = int(first_row), int(last_row), int(first_col), int(last_col), int(rows), int(cols)
			x = a['values'][r0:r1 + 1, c0:c1 + 1]
			y = b['values'][r0 - dr:r1 - dr + 1, c0 - dc:c1 - dc + 1]
			differ = ~((x == y) | (np.isnan(x) & np.isnan(y)))
			if differ.any():
				r, c = (int(k) for k in np.argwhere(differ)[0])
				raise ValueError(f"{label}: depth grids {a['id']} and {b['id']} share the sample at north "
					f"{_number(a['origin_north_m'] + (r0 + r) * a['cell_m'])}, east {_number(a['origin_east_m'] + (c0 + c) * a['cell_m'])}, "
					f"but hold {_number(x[r, c])} and {_number(y[r, c])}: grids may meet only where every shared sample is identical")
			return
	if _meet(a, b):
		raise ValueError(f"{label}: depth grids {a['id']} and {b['id']} meet with different cell sizes ({_number(a['cell_m'])} and "
			f"{_number(b['cell_m'])} m): grids that meet must lie on one lattice" if a['cell_m'] != b['cell_m'] else
			f"{label}: depth grids {a['id']} and {b['id']} meet with origins that are not a whole number of {_number(a['cell_m'])} m "
			'cells apart: grids that meet must lie on one lattice')


def verify_depth(directory):
	"""Checks a depth set and returns its digest, manifest and grids (each grid's manifest entry with its bed elevations
	as `values`, rows south to north); refuses anything outside the format with the path."""
	directory = Path(directory)
	depth = _read_manifest(directory)
	_validate(depth, DEPTH, str(directory / 'manifest.json'))
	if depth['format'] != 'ferrysim-env-depth/1' or depth['datum'] != 'NAVD88' or not depth['grids']:
		raise ValueError('invalid depth manifest format/datum/grids')
	if not math.isfinite(depth['outside_depth_m']) or depth['outside_depth_m'] <= 0:
		raise ValueError('invalid outside depth')
	grids, grid_ids = [], set()
	for grid in depth['grids']:
		if (grid['id'] in grid_ids or grid['id'] in ('', '.', '..')
			or re.fullmatch('[A-Za-z0-9_.-]+', grid['id']) is None or grid['file'] != grid['id'] + '.f32'):
			raise ValueError(f'{directory / "manifest.json"}: grid IDs must be unique plain names, with file ID.f32')
		grid_ids.add(grid['id'])
		p = _safe_file(directory, grid['file'])
		body = p.read_bytes()
		if hashlib.sha256(body).hexdigest() != grid['sha256']:
			raise ValueError(f'{p}: checksum mismatch')
		if (grid['rows'] < 2 or grid['cols'] < 2 or grid['cell_m'] <= 0 or grid['nodata'] != 'NaN'
			or len(body) != grid['rows']*grid['cols']*4 or np.isinf(np.frombuffer(body, dtype='<f4')).any()):
			raise ValueError(f'{p}: invalid grid dimensions/values')
		n, e, cell = grid['origin_north_m'], grid['origin_east_m'], grid['cell_m']
		b = (n, e, n+(grid['rows']-1)*cell, e+(grid['cols']-1)*cell)
		if not all(math.isfinite(x) for x in b):
			raise ValueError('nonfinite grid bounds')
		grid = dict(grid, end_north_m=b[2], end_east_m=b[3], values=np.frombuffer(body, dtype='<f4').reshape(grid['rows'], grid['cols']))
		for other in grids:
			_check_shared(other, grid, str(directory / 'manifest.json'))
		grids.append(grid)
	return _verify_digest(directory, depth_digest(directory)), depth, grids


Sample = namedtuple('Sample', 'kind grid bed')


class DepthSet:
	"""A verified depth set read the way the core's DepthSet reads it: a point belongs to the first grid whose centres,
	first to last inclusive, contain it, and its bed is bilinear between the four centres of its cell (the last row and
	column of centres inside); unknown when any of the four is NaN, whatever its weight."""

	def __init__(self, digest, manifest, grids):
		self.digest, self.manifest, self.grids = digest, manifest, grids

	def sample(self, north, east):
		"""Sample(kind, grid, bed): kind 'known', 'unknown' or 'outside', the index of the grid that decides (-1 outside
		every grid) and the bed elevation (NAVD88 m, positive up; 0 unless known)."""
		for index, g in enumerate(self.grids):
			row = (north - g['origin_north_m']) / g['cell_m']
			col = (east - g['origin_east_m']) / g['cell_m']
			if not (row >= 0. and row <= g['rows'] - 1 and col >= 0. and col <= g['cols'] - 1):
				continue
			r, c = min(int(row), g['rows'] - 2), min(int(col), g['cols'] - 2)
			v = g['values']
			south_west, south_east, north_west, north_east = float(v[r, c]), float(v[r, c + 1]), float(v[r + 1, c]), float(v[r + 1, c + 1])
			if any(math.isnan(x) for x in (south_west, south_east, north_west, north_east)):
				return Sample('unknown', index, 0.)
			across, up = col - c, row - r
			lower = south_west + across * (south_east - south_west)
			upper = north_west + across * (north_east - north_west)
			return Sample('known', index, lower + up * (upper - lower))
		return Sample('outside', -1, 0.)


def load_depth(directory):
	return DepthSet(*verify_depth(directory))


def verify(root):
	root = Path(root)
	depth_hash, depth, _ = verify_depth(root / 'depth')
	digests, field = [], None
	for directory in sorted((root / 'days').iterdir()):
		if not directory.is_dir():
			continue
		metadata = _read_manifest(directory)
		_validate(metadata, DAY, str(directory / 'manifest.json'))
		if metadata['format'] != 'ferrysim-env/1' or not metadata['region'] or metadata['region'] != depth['region']:
			raise ValueError(f'{directory}: format/region mismatch')
		if re.fullmatch('[0-9]{4}-[0-9]{2}-[0-9]{2}', metadata['date']) is None or date.fromisoformat(metadata['date']).year < 2007:
			raise ValueError(f'{directory / "manifest.json"}: date must be YYYY-MM-DD from 2007 onward')
		if metadata['date'] != directory.name or metadata['epoch_utc'] != series.epoch_utc(metadata['date']):
			raise ValueError(f'{directory}: invalid date or epoch')
		if metadata['time_coverage_s'] != list(series.COVERAGE) or metadata['depth_digest'] != depth_hash:
			raise ValueError(f'{directory / "manifest.json"}: coverage/depth digest mismatch')
		field_hash = metadata.get('field_digest')
		if field_hash is not None:
			if field is None:
				field = verify_field(root / 'field', depth['region'])
			if field_hash != field[0]:
				raise ValueError(f'{directory / "manifest.json"}: field_digest differs from field/DIGEST')
			currents = {x['id']: x for x in metadata['current_stations']}
			for station in field[1]['stations']:
				day_station = currents.get(station['id'])
				if day_station is None or math.hypot(day_station['north_m'] - station['north_m'],
					day_station['east_m'] - station['east_m']) > STATION_POSITION_TOLERANCE_M:
					raise ValueError(f'{directory / "manifest.json"}: field station {station["id"]} is missing or more than '
						f'{STATION_POSITION_TOLERANCE_M} m from where the field solved it')
		for station in metadata['current_stations']:
			_check_derivation(station, str(directory / 'manifest.json'))
		for name, columns in COLUMNS.items():
			p = directory / name
			body = p.read_bytes()
			if hashlib.sha256(body).hexdigest() != metadata['files'][name]:
				raise ValueError(f'{p}: checksum mismatch')
			if b'\r' in body:
				raise ValueError(f'{p}: line endings must be LF')
			try:
				lines = body.decode('utf-8').split('\n')
			except UnicodeError as error:
				raise ValueError(f'{p}: invalid UTF-8') from error
			if lines[-1] == '':
				lines.pop()
			if not lines or lines[0] != ','.join(columns):
				raise ValueError(f'{p}: invalid literal header')
			ids = _stations(metadata[STATIONS[name]], str(directory / 'manifest.json'), name != 'wind.csv')
			previous, times = None, {s: [] for s in ids}
			for line in lines[1:]:
				fields = line.split(',')
				if len(fields) != len(columns):
					raise ValueError(f'{p}: blank or malformed CSV row')
				row = dict(zip(columns, fields))
				station, t = row['station_id'], _csv_number(row['t_s'], str(p))
				key = (station, t)
				if station not in times or not math.isfinite(t) or (previous is not None and key <= previous):
					raise ValueError(f'{p}: invalid station, duplicate or unsorted row')
				previous = key
				times[station].append(t)
				for column in columns[2:]:
					value = row[column]
					if column == 'source':
						if value not in ('observed', 'predicted'):
							raise ValueError(f'{p}: invalid water source')
					elif column == 'gust_mps' and value == '':
						continue
					else:
						number = _csv_number(value, str(p))
						if column == 'gust_mps' and number < 0:
							raise ValueError(f'{p}: negative gust_mps')
			for station, ts in times.items():
				if not ts or ts[0] > series.COVERAGE[0] or ts[-1] < series.COVERAGE[1]:
					raise ValueError(f'{p}: {station} lacks required endpoint coverage')
		digests.append(_verify_digest(directory, day_digest(directory, depth_hash, field_hash)))
	if not digests:
		raise ValueError('no scenario-day packs')
	return digests
