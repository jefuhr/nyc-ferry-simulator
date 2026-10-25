"""Environment interchange with raw-byte integrity, digests and offline structural verification."""
import csv
import hashlib
import io
import json
import math
import re
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


# These schemas mirror EnvironmentPackJson.Node, including required nullable keys.
POSITION = dict(id='text', name='text', lat='number', lon='number', north_m='number', east_m='number', source='text', source_sha256='hex')
GRID = dict(id='text', file='text', origin_north_m='number', origin_east_m='number', cell_m='number', rows='count', cols='count', nodata='text', sha256='hex')
SOURCE = dict(name='text', sha256='hex')
DEPTH = dict(format='text', region='text', datum='text', value='text', grids=[GRID], outside_depth_m='number', sources=[SOURCE])
DAY = dict(format='text', region='text', date='text', epoch_utc='text', time_coverage_s=['number'], depth_digest='hex',
	current_stations=[dict(POSITION, kind='text', bin_depth_m='nullable-number')], water_level_stations=[POSITION],
	wind_stations=[dict(POSITION, sensor_height_m='nullable-number')], files={name: 'hex' for name in COLUMNS}, notes=['text'])


def _validate(value, schema, label):
	if isinstance(schema, dict):
		if not isinstance(value, dict):
			raise ValueError(f'{label}: expected an object')
		optional = {'note': 'nullable-text'} if schema is SOURCE else {}
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


def day_digest(directory, depth):
	return _file_digest(directory, ['manifest.json', *COLUMNS], depth.encode('ascii') + b'\0')


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


def write_day(root, region, day, depth_digest, current_stations, water_level_stations, wind_stations, currents, water_levels, wind, notes):
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
		time_coverage_s=list(series.COVERAGE), depth_digest=depth_digest,
		current_stations=current_stations, water_level_stations=water_level_stations,
		wind_stations=wind_stations, files=files, notes=sorted(set(notes)))
	write_json(directory / 'manifest.json', metadata)
	write_digest(directory, day_digest(directory, depth_digest))
	return metadata


def _safe_file(directory, name):
	if not isinstance(name, str) or Path(name).name != name:
		raise ValueError(f'unsafe pack file: {name}')
	return Path(directory) / name


def verify(root):
	root = Path(root)
	directory = root / 'depth'
	depth = _read_manifest(directory)
	_validate(depth, DEPTH, str(directory / 'manifest.json'))
	if depth['format'] != 'ferrysim-env-depth/1' or depth['datum'] != 'NAVD88' or not depth['grids']:
		raise ValueError('invalid depth manifest format/datum/grids')
	if not math.isfinite(depth['outside_depth_m']) or depth['outside_depth_m'] <= 0:
		raise ValueError('invalid outside depth')
	bounds, grid_ids = [], set()
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
		for a in bounds:
			if max(a[0],b[0]) <= min(a[2],b[2]) and max(a[1],b[1]) <= min(a[3],b[3]):
				raise ValueError('overlapping grids')
		bounds.append(b)
	depth_hash = _verify_digest(directory, depth_digest(directory))
	digests = []
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
		digests.append(_verify_digest(directory, day_digest(directory, depth_hash)))
	if not digests:
		raise ValueError('no scenario-day packs')
	return digests
