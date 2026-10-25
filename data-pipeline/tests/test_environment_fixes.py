"""Rehashed malformed packs, unresolved water, and provenance-preserving cache restore.

Failure modes: the producer writes water NaNs; integrity-valid packs violate the
runtime schema, geodesy, epoch or CSV grammar; a cache restore overwrites its pin
or accepts different bytes. All downloads in these tests are stubbed.
"""
import hashlib
import json

import numpy as np
import pytest
import rasterio

from ferrydata import slices
from ferrydata.environment import depth, pack, sources
from ferrydata.fetcher import ChecksumError
from ferrydata.manifest import Manifest
from test_environment import tiny_pack
from test_environment_build import environment_fixture


DAY = 'days/2025-07-14'
DEPTH_KEYS = 'format region datum value grids outside_depth_m sources'.split()
DAY_KEYS = 'format region date epoch_utc time_coverage_s depth_digest current_stations water_level_stations wind_stations files notes'.split()
GRID_KEYS = 'id file origin_north_m origin_east_m cell_m rows cols nodata sha256'.split()
STATION_KEYS = 'id name lat lon north_m east_m source source_sha256'.split()
SCHEMAS = [
	('depth/manifest.json', (), DEPTH_KEYS),
	('depth/manifest.json', ('grids', 0), GRID_KEYS),
	('depth/manifest.json', ('sources', 0), ['name', 'sha256']),
	(f'{DAY}/manifest.json', (), DAY_KEYS),
	(f'{DAY}/manifest.json', ('files',), list(pack.COLUMNS)),
	*[(f'{DAY}/manifest.json', (name, 0), STATION_KEYS + extra) for name, extra in (
		('current_stations', ['kind', 'bin_depth_m']), ('water_level_stations', []), ('wind_stations', ['sensor_height_m']))],
]


def rehash(root):
	"""Repair every checksum so only semantic validation can refuse the fixture."""
	dm = json.loads((root / 'depth/manifest.json').read_bytes())
	for grid in dm['grids']:
		grid['sha256'] = hashlib.sha256((root / 'depth' / grid['file']).read_bytes()).hexdigest()
	pack.write_json(root / 'depth/manifest.json', dm)
	digest = pack.depth_digest(root / 'depth')
	pack.write_digest(root / 'depth', digest)
	for directory in (root / 'days').iterdir():
		metadata = json.loads((directory / 'manifest.json').read_bytes())
		metadata['depth_digest'] = digest
		for name in pack.COLUMNS:
			metadata['files'][name] = hashlib.sha256((directory / name).read_bytes()).hexdigest()
		pack.write_json(directory / 'manifest.json', metadata)
		pack.write_digest(directory, pack.day_digest(directory, digest))


def mutate(root, file, path, fn):
	p = root / file
	metadata = json.loads(p.read_bytes())
	# Exercise the nonempty depth-source schema too.
	if file == 'depth/manifest.json' and path == ('sources', 0):
		metadata['sources'] = [dict(name='fixture.tif', sha256='0'*64)]
	node = metadata
	for key in path:
		node = node[key]
	fn(node)
	pack.write_json(p, metadata)


@pytest.mark.parametrize('file,path,keys', SCHEMAS)
@pytest.mark.parametrize('mode', ['missing', 'wrong_type', 'unknown'])
def test_all_manifest_objects_refuse_schema_violations(tiny_pack, file, path, keys, mode):
	root, _ = tiny_pack
	# Test each required key independently, starting from the valid bytes.
	original = (root / file).read_bytes()
	for key in keys if mode != 'unknown' else ['unexpected']:
		(root / file).write_bytes(original)
		def change(node):
			if mode == 'missing':
				node.pop(key)
			else:
				node[key] = {'wrong': 'type'}
		mutate(root, file, path, change)
		# Rehash raw bytes without relying on valid schema fields.
		if file.startswith('depth/'):
			dm = json.loads((root / file).read_bytes())
			if path == ('grids', 0) and key == 'file':
				names = ['slice.f32']
			else:
				names = [g['file'] for g in dm['grids']] if isinstance(dm.get('grids'), list) else ['slice.f32']
			digest = pack._file_digest(root / 'depth', ['manifest.json', *names])
			pack.write_digest(root / 'depth', digest)
			day = json.loads((root / DAY / 'manifest.json').read_bytes())
			day['depth_digest'] = digest
			pack.write_json(root / DAY / 'manifest.json', day)
		else:
			digest = pack.read_digest(root / 'depth')
		pack.write_digest(root / DAY, pack.day_digest(root / DAY, digest))
		with pytest.raises(ValueError):
			pack.verify(root)


@pytest.mark.parametrize('station_key', ['current_stations', 'water_level_stations', 'wind_stations'])
@pytest.mark.parametrize('key,value', [
	('north_m', 100.), ('east_m', 100.), ('lat', 91.), ('lon', -181.),
	('lat', True), ('north_m', '0'), ('east_m', None),
	('source_sha256', 'A'*64), ('source_sha256', 'bad'),
	('id', ''), ('id', 'a,b'), ('id', 'a"b'),
])
def test_station_values_match_loader(tiny_pack, station_key, key, value):
	root, _ = tiny_pack
	mutate(root, f'{DAY}/manifest.json', (station_key, 0), lambda node: node.update({key: value}))
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('key,value', [
	('kind', None), ('bin_depth_m', '1.5'), ('bin_depth_m', True),
])
def test_current_specific_types(tiny_pack, key, value):
	root, _ = tiny_pack
	mutate(root, f'{DAY}/manifest.json', ('current_stations', 0), lambda node: node.update({key: value}))
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('key,value', [
	('sensor_height_m', '10'), ('sensor_height_m', False),
])
def test_wind_specific_types(tiny_pack, key, value):
	root, _ = tiny_pack
	mutate(root, f'{DAY}/manifest.json', ('wind_stations', 0), lambda node: node.update({key: value}))
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('key,value', [
	('rows', 2.), ('cols', True), ('rows', 1), ('cols', 1048577),
	('cell_m', 0.), ('cell_m', '5'), ('origin_north_m', True),
	('nodata', 'nan'), ('id', '../slice'), ('id', 'different'),
])
def test_grid_types_ranges_and_names(tiny_pack, key, value):
	root, _ = tiny_pack
	mutate(root, 'depth/manifest.json', ('grids', 0), lambda node: node.update({key: value}))
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('key,value', [
	('format', 'ferrysim-env/2'), ('region', ''), ('epoch_utc', '2025-07-14T05:00:00Z'),
	('epoch_utc', '2025-07-14T04:00:00+00:00'), ('date', '20250714'),
	('date', '2006-07-14'), ('notes', [42]), ('time_coverage_s', [-3600, True]),
	('current_stations', []), ('water_level_stations', []),
	('current_stations', [None]),
])
def test_day_values_match_loader(tiny_pack, key, value):
	root, _ = tiny_pack
	def change(node):
		node[key] = value
		if key == 'date' and value.startswith('2006'):
			node['epoch_utc'] = '2006-07-14T04:00:00Z'
	mutate(root, f'{DAY}/manifest.json', (), change)
	rehash(root)
	if key == 'date':
		(root / DAY).rename(root / 'days' / value)
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('name', list(pack.COLUMNS))
@pytest.mark.parametrize('case', ['quoted_header', 'blank_line', 'crlf', 'quoted_number', 'space_number', 'underscore_number',
	'nonfinite_time', 'nonfinite_value', 'duplicate_time', 'backward_time', 'unknown_station', 'missing_rows', 'extra_column', 'short_row', 'invalid_utf8'])
def test_csv_grammar_and_rows_match_loader(tiny_pack, name, case):
	root, _ = tiny_pack
	p = root / DAY / name
	lines = p.read_bytes().splitlines()
	fields = lines[1].split(b',')
	if case == 'quoted_header':
		lines[0] = b'"station_id"' + lines[0][len(b'station_id'):]
	elif case == 'blank_line':
		lines.insert(1, b'')
	elif case == 'crlf':
		lines[0] += b'\r'
	elif case in ('quoted_number', 'space_number', 'underscore_number', 'nonfinite_time'):
		fields[1] = {'quoted_number': b'"-3600.0"', 'space_number': b' -3600.0', 'underscore_number': b'-3_600.0', 'nonfinite_time': b'NaN'}[case]
		lines[1] = b','.join(fields)
	elif case == 'nonfinite_value':
		fields[2] = b'Infinity'
		lines[1] = b','.join(fields)
	elif case == 'duplicate_time':
		lines.insert(2, lines[1])
	elif case == 'backward_time':
		lines[1], lines[2] = lines[2], lines[1]
	elif case == 'unknown_station':
		lines[1] = b'b' + lines[1][1:]
	elif case == 'missing_rows':
		lines = lines[:1]
	elif case == 'extra_column':
		lines[1] += b',0'
	elif case == 'short_row':
		lines[1] = b','.join(fields[:-1])
	elif case == 'invalid_utf8':
		lines[1] = b'\xff' + lines[1]
	p.write_bytes(b'\n'.join(lines) + b'\n')
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


def test_negative_gust_is_refused_with_valid_hashes(tiny_pack):
	root, _ = tiny_pack
	p = root / DAY / 'wind.csv'
	p.write_bytes(p.read_bytes().replace(b',2.0\n', b',-2.0\n'))
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


def test_invalid_water_source_is_refused(tiny_pack):
	root, _ = tiny_pack
	p = root / DAY / 'water_levels.csv'
	p.write_bytes(p.read_bytes().replace(b'observed', b'unknown'))
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('case', ['duplicate_id', 'ungrouped_rows', 'listed_without_rows'])
def test_multiple_station_failures(tiny_pack, case):
	root, _ = tiny_pack
	def change(node):
		node['current_stations'].append(dict(node['current_stations'][0], id='a' if case == 'duplicate_id' else 'b'))
	mutate(root, f'{DAY}/manifest.json', (), change)
	if case == 'ungrouped_rows':
		p = root / DAY / 'currents.csv'
		lines = p.read_bytes().splitlines()
		lines.insert(2, b'b,-3600.0,0.0,0.0')
		lines.append(b'b,90000.0,0.0,0.0')
		p.write_bytes(b'\n'.join(lines)+b'\n')
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


def test_duplicate_json_property_is_refused_even_when_rehashed(tiny_pack):
	root, _ = tiny_pack
	p = root / DAY / 'manifest.json'
	p.write_bytes(p.read_bytes().replace(b'{', b'{"region":"lower-east-river",', 1))
	pack.write_digest(p.parent, pack.day_digest(p.parent, pack.read_digest(root / 'depth')))
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('existing_output', [False, True])
def test_unfillable_water_hole_fails_before_writing_depth(environment_fixture, existing_output):
	out, _ = environment_fixture
	(out / 'shoreline_constructions.geojson').write_text('{"type":"FeatureCollection","features":[]}')
	for name in ('depth_merged_navd88.tif', 'depth_chart_navd88.tif'):
		with rasterio.open(out / name, 'r+') as ds:
			values = ds.read(1)
			values[40:60, 40:60] = ds.nodata
			ds.write(values, 1)
	destination = out / 'env/depth'
	if existing_output:
		destination.mkdir(parents=True)
		(destination / 'previous.f32').write_bytes(b'previous accepted depth')
	before = {p.name: p.read_bytes() for p in destination.iterdir()} if existing_output else {}
	with pytest.raises(ValueError, match=r'63 unfilled water cells.*63 cells at north_m=0.0, east_m=0.0'):
		depth.build(slices.get_slice('lower-east-river'), out, destination)
	if existing_output:
		assert {p.name: p.read_bytes() for p in destination.iterdir()} == before
	else:
		assert not destination.exists()


@pytest.mark.parametrize('downloaded,expected_size,accepted', [(b'{}', 2, True), (b'[]', 2, False), (b'{}', 3, False)])
def test_restore_missing_pin_preserves_manifest_and_requires_size_and_hash(tmp_path, monkeypatch, downloaded, expected_size, accepted):
	m = Manifest.load('fixture', tmp_path / 'manifests')
	m.record('missing.json', 'https://example.invalid/original', hashlib.sha256(b'{}').hexdigest(), expected_size,
		retrieved_utc='2000-01-01T00:00:00Z')
	m.save()
	before = m.path.read_bytes()
	entry = dict(m.files['missing.json'])
	inputs = sources.Inputs.__new__(sources.Inputs)
	inputs.cache, inputs.manifest, inputs.loaded, inputs.downloads, inputs.log = tmp_path, m, {}, 0, lambda *a, **k: None
	calls = []
	def download(url, part):
		calls.append((url, part.name))
		part.write_bytes(downloaded)
		return len(downloaded)
	monkeypatch.setattr(sources.fetcher, '_download', download)
	if accepted:
		assert inputs.get('missing.json', 'https://example.invalid/current') == b'{}'
		assert (tmp_path / 'missing.json').read_bytes() == b'{}'
		assert inputs.downloads == 1
	else:
		with pytest.raises(ChecksumError):
			inputs.get('missing.json', 'https://example.invalid/current')
		assert not (tmp_path / 'missing.json').exists()
		assert inputs.downloads == 0
	assert calls == [('https://example.invalid/original', 'missing.json.part')]
	assert not (tmp_path / 'missing.json.part').exists()
	assert m.path.read_bytes() == before
	assert m.files['missing.json'] == entry


@pytest.mark.parametrize('file,path,key,value', [
	('depth/manifest.json', (), 'outside_depth_m', True),
	('depth/manifest.json', (), 'outside_depth_m', 0),
	('depth/manifest.json', (), 'datum', 'MSL'),
	('depth/manifest.json', (), 'format', 'ferrysim-env-depth/2'),
	('depth/manifest.json', ('sources', 0), 'note', 42),
	(f'{DAY}/manifest.json', ('current_stations', 0), 'lat', float('nan')),
	(f'{DAY}/manifest.json', ('current_stations', 0), 'bin_depth_m', float('inf')),
	(f'{DAY}/manifest.json', ('wind_stations', 0), 'sensor_height_m', float('-inf')),
	('depth/manifest.json', ('grids', 0), 'cell_m', float('inf')),
])
def test_additional_metadata_values_are_refused(tiny_pack, file, path, key, value):
	root, _ = tiny_pack
	p = root / file
	metadata = json.loads(p.read_bytes())
	if path == ('sources', 0):
		metadata['sources'] = [dict(name='fixture.tif', sha256='0'*64)]
	node = metadata
	for part in path:
		node = node[part]
	node[key] = value
	# Deliberately write nonstandard JSON NaN/Infinity to exercise finite checks.
	p.write_text(json.dumps(metadata)+'\n')
	depth_hash = pack.depth_digest(root / 'depth')
	pack.write_digest(root / 'depth', depth_hash)
	day = root / DAY
	if file.startswith('depth/'):
		dm = json.loads((day / 'manifest.json').read_bytes())
		dm['depth_digest'] = depth_hash
		pack.write_json(day / 'manifest.json', dm)
	pack.write_digest(day, pack.day_digest(day, depth_hash))
	with pytest.raises(ValueError):
		pack.verify(root)


@pytest.mark.parametrize('case', ['infinite_bed', 'truncated_grid', 'overlap', 'duplicate_grid_id'])
def test_depth_set_cross_field_failures(tiny_pack, case):
	root, _ = tiny_pack
	directory = root / 'depth'
	if case == 'infinite_bed':
		(directory / 'slice.f32').write_bytes(np.array([-10, -10, -10, np.inf], dtype='<f4').tobytes())
	elif case == 'truncated_grid':
		(directory / 'slice.f32').write_bytes(b'\0'*4)
	else:
		dm = json.loads((directory / 'manifest.json').read_bytes())
		second = dict(dm['grids'][0])
		if case == 'overlap':
			second.update(id='other', file='other.f32', origin_north_m=5.)
			(directory / 'other.f32').write_bytes((directory / 'slice.f32').read_bytes())
		else:
			# Isolate duplicate identity from overlap: same ID/file, separate lattice.
			second['origin_north_m'] = 10.
		dm['grids'].append(second)
		pack.write_json(directory / 'manifest.json', dm)
	rehash(root)
	with pytest.raises(ValueError):
		pack.verify(root)


def test_nullable_station_numbers_and_optional_source_note_are_valid(tiny_pack):
	root, _ = tiny_pack
	mutate(root, f'{DAY}/manifest.json', ('current_stations', 0), lambda node: node.update(bin_depth_m=None))
	mutate(root, 'depth/manifest.json', ('sources', 0), lambda node: node.update(note=None))
	rehash(root)
	assert len(pack.verify(root)) == 1


def test_new_source_names_use_ordinary_fetch(tmp_path, monkeypatch):
	m = Manifest.load('fixture', tmp_path / 'manifests')
	inputs = sources.Inputs.__new__(sources.Inputs)
	inputs.cache, inputs.manifest, inputs.loaded, inputs.downloads, inputs.log = tmp_path, m, {}, 0, lambda *a, **k: None
	calls = []
	def download(url, part):
		calls.append(url)
		part.write_bytes(b'{}')
		return 2
	monkeypatch.setattr(sources.fetcher, '_download', download)
	assert inputs.get('new.json', 'https://example.invalid/new') == b'{}'
	assert calls == ['https://example.invalid/new']
	assert m.files['new.json']['url'] == calls[0]
	assert m.verify('new.json', tmp_path / 'new.json')
	assert m.path.exists()


def test_pinned_restore_validates_payload_before_promoting(tmp_path, monkeypatch):
	m = Manifest.load('fixture', tmp_path / 'manifests')
	body = b'not json'
	m.record('missing.json', 'https://example.invalid/original', hashlib.sha256(body).hexdigest(), len(body))
	m.save()
	before = m.path.read_bytes()
	inputs = sources.Inputs.__new__(sources.Inputs)
	inputs.cache, inputs.manifest, inputs.loaded, inputs.downloads, inputs.log = tmp_path, m, {}, 0, lambda *a, **k: None
	def download(url, part):
		part.write_bytes(body)
		return len(body)
	monkeypatch.setattr(sources.fetcher, '_download', download)
	with pytest.raises(ValueError):
		inputs.get('missing.json', 'https://example.invalid/current')
	assert not (tmp_path / 'missing.json').exists()
	assert not (tmp_path / 'missing.json.part').exists()
	assert m.path.read_bytes() == before


@pytest.mark.parametrize('north,east,accepted', [(.009, 0., True), (.006, .006, True), (.008, .008, False), (.011, 0., False)])
def test_position_tolerance_is_euclidean_in_metres(tiny_pack, north, east, accepted):
	root, _ = tiny_pack
	mutate(root, f'{DAY}/manifest.json', ('current_stations', 0), lambda node: node.update(north_m=north, east_m=east))
	rehash(root)
	if accepted:
		assert len(pack.verify(root)) == 1
	else:
		with pytest.raises(ValueError):
			pack.verify(root)


def test_utf16_manifest_is_refused_despite_valid_digest(tiny_pack):
	root, _ = tiny_pack
	p = root / DAY / 'manifest.json'
	p.write_bytes(p.read_text().encode('utf-16'))
	pack.write_digest(p.parent, pack.day_digest(p.parent, pack.read_digest(root / 'depth')))
	with pytest.raises(ValueError):
		pack.verify(root)


def test_csv_final_lf_is_optional_in_reader(tiny_pack):
	root, _ = tiny_pack
	for name in pack.COLUMNS:
		p = root / DAY / name
		p.write_bytes(p.read_bytes().rstrip(b'\n'))
	rehash(root)
	assert len(pack.verify(root)) == 1
