"""The current-field extension of ferrysim-env/1: the field set, the day's field_digest and derivations. Failure modes
guarded:

- a pack without a field whose digest changes (existing packs must keep theirs), or a day digest that ignores the field;
- a changed byte in any field file accepted, or refused without naming the file;
- cells outside the format (non-unit axis on water, weights not summing to 1, a slot naming no station, weight in an
  empty slot, factors outside [0, 1], non-finite values, a dry cell with values), slots outside 1 to 8, unknown keys, or a
  field with no grids;
- a day whose field_digest is not the field's, that lacks a field station, or places one beyond the tolerance;
- a derivation that is not NOAA's subordinate method for a subordinate station, with non-positive ratios, directions
  outside [0, 360) or unknown keys.
"""
import json

import numpy as np
import pytest

from ferrydata import geodesy
from ferrydata.environment import pack

PARAMETERS = dict(cell_m=20., computation_north_m=[-10., 30.], computation_east_m=[-10., 30.], inflow='south', outflow='north',
	conveyance_exponent=0., min_depth_m=1., smoothing_m=400., shelter_threshold=.85, depth_exponent=1. / 6., depth_smoothing_m=200.,
	cross_taper_m=60., station_disk_m=40., station_reach_m=120., across_reach_m=500., tolerance=1e-10)
DERIVATION = dict(method='noaa-subordinate', reference_station='NYH1924', reference_bin=6, slack_before_flood_min=1., max_flood_min=-6.,
	slack_before_ebb_min=-2., max_ebb_min=8., flood_ratio=.6, ebb_ratio=1.12, flood_dir_deg=37., ebb_dir_deg=215.,
	sources=[dict(name='offsets_B.json', sha256='4' * 64)])


def station(sid, north, east):
	lat, lon = geodesy.from_local(np.array([north]), np.array([east]))
	n, e = geodesy.to_local(lat[0], lon[0])
	return dict(id=sid, name='fixture ' + sid, lat=float(lat[0]), lon=float(lon[0]), north_m=float(n), east_m=float(e),
		source='fixture.json', source_sha256='0' * 64)


def grid_arrays(slots=2):
	"""3 by 3 cells of 20 m from (-10, -10): axis north, both factors 1, station 0 in the south row, 1 in the north row,
	half and half between; cell (2, 2) is dry."""
	values = np.zeros((3, 3, 4 + slots), dtype='<f4')
	index = np.full((3, 3, slots), pack.EMPTY_SLOT, dtype=np.uint8)
	for r in range(3):
		for c in range(3):
			if (r, c) == (2, 2):
				continue
			values[r, c, :4] = (1., 0., 1., 1.)
			weights = [(0, 1.)] if r == 0 else [(1, 1.)] if r == 2 else [(0, .5), (1, .5)]
			for j, (k, w) in enumerate(weights[:slots]):
				index[r, c, j], values[r, c, 4 + j] = k, w
	return values, index


def write_field(root, values=None, index=None, stations=None, parameters=None):
	if values is None:
		values, index = grid_arrays()
	stations = stations or [dict(id='A', north_m=None, east_m=None, frame_north=1., frame_east=0.), dict(id='B', north_m=None, east_m=None, frame_north=1., frame_east=0.)]
	return pack.write_field(root / 'field', 'lower-east-river', stations, [dict(id='slice', origin_north_m=-10., origin_east_m=-10., cell_m=20.,
		values=values, slots=index)], parameters or PARAMETERS, [dict(name='depth/DIGEST', sha256='1' * 64, note='fixture')], ['fixture'])


@pytest.fixture
def field_pack(tmp_path):
	root = tmp_path / 'env'
	(root / 'depth').mkdir(parents=True)
	body = np.array([[-10, -11], [-12, -13]], dtype='<f4').tobytes()
	(root / 'depth' / 'slice.f32').write_bytes(body)
	import hashlib
	grid = dict(id='slice', file='slice.f32', origin_north_m=0., origin_east_m=0., cell_m=5., rows=2, cols=2, nodata='NaN',
		sha256=hashlib.sha256(body).hexdigest())
	pack.write_json(root / 'depth' / 'manifest.json', dict(format='ferrysim-env-depth/1', region='lower-east-river', datum='NAVD88',
		value='bed elevation, metres, positive up (water is negative)', grids=[grid], outside_depth_m=20., sources=[]))
	pack.write_digest(root / 'depth', pack.depth_digest(root / 'depth'))
	a, b = station('A', -5., 0.), station('B', 25., 5.)
	fields = [dict(id='A', north_m=a['north_m'], east_m=a['east_m'], frame_north=1., frame_east=0.),
		dict(id='B', north_m=b['north_m'], east_m=b['east_m'], frame_north=1., frame_east=0.)]
	write_field(root, stations=fields)
	args = dict(root=root, region='lower-east-river', day='2025-07-14', depth_digest=pack.read_digest(root / 'depth'),
		current_stations=[dict(a, kind='harmonic-prediction', bin_depth_m=1.5), dict(b, kind='subordinate-prediction', bin_depth_m=None, derivation=DERIVATION)],
		water_level_stations=[a], wind_stations=[],
		currents=[('A', -3600., 1., 0.), ('A', 90000., 0., 1.), ('B', -3600., .5, 0.), ('B', 90000., 0., .5)],
		water_levels=[('A', -3600., 0., 'observed'), ('A', 90000., 1., 'observed')], wind=[], notes=['fixture'],
		field_digest=pack.read_digest(root / 'field'))
	pack.write_day(**args)
	return root, args, fields


def reseal(root, args, **changes):
	"""Rewrites the day with changes, over the field on disk now."""
	args = dict(args, field_digest=pack.read_digest(root / 'field'), **changes)
	pack.write_day(**args)


def test_a_field_pack_verifies_and_its_day_digest_covers_the_field(field_pack):
	root, args, _ = field_pack
	day = root / 'days' / '2025-07-14'
	field_hash = pack.read_digest(root / 'field')
	assert pack.verify(root) == [pack.read_digest(day)]
	assert pack.read_digest(day) == pack.day_digest(day, args['depth_digest'], field_hash)
	assert pack.read_digest(day) != pack.day_digest(day, args['depth_digest'])
	metadata = json.loads((day / 'manifest.json').read_text())
	assert list(metadata)[5:7] == ['depth_digest', 'field_digest']
	before = {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}
	write_field(root, stations=json.loads((root / 'field' / 'manifest.json').read_text())['stations'])
	pack.write_day(**args)
	assert before == {str(p.relative_to(root)): p.read_bytes() for p in root.rglob('*') if p.is_file()}


def test_without_a_field_the_day_digest_rule_is_unchanged(field_pack, tmp_path):
	root, args, _ = field_pack
	args = dict(args, field_digest=None, current_stations=[dict(args['current_stations'][0])], currents=args['currents'][:2])
	metadata = pack.write_day(**args)
	day = root / 'days' / '2025-07-14'
	assert 'field_digest' not in metadata
	assert pack.read_digest(day) == pack.day_digest(day, args['depth_digest'])
	assert pack.verify(root) == [pack.read_digest(day)]


@pytest.mark.parametrize('name', ['field/manifest.json', 'field/slice.f32', 'field/slice.u8', 'field/DIGEST'])
def test_a_changed_byte_in_any_field_file_is_refused_naming_it(field_pack, name):
	root, _, _ = field_pack
	p = root / name
	body = bytearray(p.read_bytes())
	index = body.index(b'\t') if p.suffix == '.json' else len(body) // 2
	body[index] = ord(' ') if p.suffix == '.json' else body[index] ^ 1
	p.write_bytes(body)
	with pytest.raises(ValueError, match=p.name):
		pack.verify(root)


def damage(change):
	values, index = grid_arrays()
	change(values, index)
	return values, index


@pytest.mark.parametrize('change,message', [
	(lambda v, i: v.__setitem__((1, 1, 0), .9), 'unit axis'),
	(lambda v, i: v.__setitem__((1, 1, 4), .4), 'summing to 1'),
	(lambda v, i: i.__setitem__((0, 0, 0), 2), 'names no station'),
	(lambda v, i: v.__setitem__((0, 0, 5), .1), 'empty slots'),
	(lambda v, i: v.__setitem__((0, 1, 2), 1.5), r'\[0, 1\]'),
	(lambda v, i: v.__setitem__((0, 1, 3), np.nan), 'non-finite'),
	(lambda v, i: v.__setitem__((2, 2, 2), .5), 'dry cells'),
])
def test_cells_outside_the_format_are_refused(field_pack, change, message):
	root, args, fields = field_pack
	values, index = damage(change)
	write_field(root, values, index, stations=fields)
	reseal(root, args)
	with pytest.raises(ValueError, match=message):
		pack.verify(root)


@pytest.mark.parametrize('slots', [0, 9])
def test_slots_outside_one_to_eight_are_refused(field_pack, slots):
	root, args, fields = field_pack
	values = np.zeros((3, 3, 4 + slots), dtype='<f4')
	write_field(root, values, np.full((3, 3, slots), pack.EMPTY_SLOT, dtype=np.uint8), stations=fields)
	reseal(root, args)
	with pytest.raises(ValueError, match='slots'):
		pack.verify(root)


def test_a_field_with_no_grids_is_refused(tmp_path):
	# The core refuses it ("grids: must list at least one grid"), so a pipeline that passed it would ship an unloadable pack.
	directory = tmp_path / 'field'
	stations = [dict(id='A', north_m=5., east_m=5., frame_north=1., frame_east=0.)]
	pack.write_field(directory, 'lower-east-river', stations, [], PARAMETERS, [], [])
	with pytest.raises(ValueError, match='at least one grid'):
		pack.verify_field(directory, 'lower-east-river')


@pytest.mark.parametrize('where,key', [('manifest', 'extra'), ('parameters', 'extra'), ('stations', 'extra')])
def test_unknown_field_keys_are_refused(field_pack, where, key):
	root, args, fields = field_pack
	if where == 'manifest':
		manifest = json.loads((root / 'field' / 'manifest.json').read_text())
		manifest[key] = 1
		pack.write_json(root / 'field' / 'manifest.json', manifest)
		pack.write_digest(root / 'field', pack.field_digest(root / 'field'))
	elif where == 'parameters':
		write_field(root, stations=fields, parameters=dict(PARAMETERS, **{key: 1.}))
	else:
		write_field(root, stations=[dict(fields[0], **{key: 1.}), fields[1]])
	reseal(root, args)
	with pytest.raises(ValueError, match='unknown keys'):
		pack.verify(root)


def test_a_day_naming_another_field_is_refused(field_pack):
	root, args, _ = field_pack
	pack.write_day(**dict(args, field_digest='5' * 64))
	with pytest.raises(ValueError, match='field_digest'):
		pack.verify(root)


@pytest.mark.parametrize('north_shift,expected', [(.009, None), (.011, 'more than 0.01 m')])
def test_field_stations_must_be_where_the_day_places_them(field_pack, north_shift, expected):
	root, args, fields = field_pack
	write_field(root, stations=[dict(fields[0], north_m=fields[0]['north_m'] + north_shift), fields[1]])
	reseal(root, args)
	if expected is None:
		assert len(pack.verify(root)) == 1
	else:
		with pytest.raises(ValueError, match=expected):
			pack.verify(root)


def test_a_day_without_a_field_station_is_refused(field_pack):
	root, args, _ = field_pack
	reseal(root, args, current_stations=[args['current_stations'][0]], currents=args['currents'][:2])
	with pytest.raises(ValueError, match='field station B is missing'):
		pack.verify(root)


@pytest.mark.parametrize('change,station,message', [
	(dict(method='harmonic'), 1, 'derivation must be'),
	(dict(flood_ratio=0.), 1, 'derivation must be'),
	(dict(ebb_dir_deg=360.), 1, 'derivation must be'),
	(dict(extra=1), 1, 'unknown keys'),
	({}, 0, 'derivation must be'),
])
def test_derivations_outside_the_format_are_refused(field_pack, change, station, message):
	root, args, _ = field_pack
	stations = [dict(s) for s in args['current_stations']]
	stations[station]['derivation'] = dict(DERIVATION, **change)
	reseal(root, args, current_stations=stations)
	with pytest.raises(ValueError, match=message):
		pack.verify(root)
