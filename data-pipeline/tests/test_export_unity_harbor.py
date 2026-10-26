"""The harbor's contacts.json from the tiles' landing files, and a C#-free port of Unity's LandingGeometry gate. Failure
modes guarded: a contact duplicated across tiles (the same id, or the same physical edge either way round); a landing
from a tile that does not own it, in two tiles, or missing; records not the slice export's (order, fields); and the gate
port disagreeing with LandingGeometry's rule (the cases of its EditMode tests: chart or crew water side, solid side off
the primary face only, a normal off by more than a degree, malformed fields), or with the pipeline's runtime_contact.
"""
import copy
import json
import math

import pytest

from ferrydata import export_unity, landings


def segment(index, ident, **extra):
	base = dict(index=index, id=ident, kind='barge-side', id_key=f'k{ident}', source='enc-feature', confidence='medium',
		verification='needs-crew-verification', layer='PONTON', lnam='L', water_side_verified_by_pipeline=True,
		water_side_evidence=dict(verified=True), solid_side_evidence=dict(verified=True, method='enc-closed-outline'))
	base.update(extra)
	return base


def feature(stop, polyline, lonlat, segments, role='landing'):
	"""A polyline feature as landing_structures.geojson writes it (WGS84 [lon, lat] vertices)."""
	return dict(type='Feature', geometry=dict(type='LineString', coordinates=lonlat), properties=dict(stop_id=stop, stop_name=f'Stop {stop}',
		polyline_id=polyline, role=role, structure_id='enc:L', berth_id='enc:L', berth_rank=1, segments=segments))


def write_tiles(root, tiles_):
	"""tiles_: {tile id: (stop ids it owns, features or None for no file)}."""
	entries = []
	for tile, (stops, features) in tiles_.items():
		_, i, j = tile.split('_')
		entries.append(dict(id=tile, i=int(i), j=int(j), active=True, stop_ids=stops))
		folder = root / 'tiles' / tile
		folder.mkdir(parents=True)
		if features is not None:
			(folder / 'landing_structures.geojson').write_text(json.dumps(dict(type='FeatureCollection', features=features)))
	(root / 'index.json').write_text(json.dumps(dict(tiles=entries)))


A = [[-74.0101, 40.7031], [-74.0100, 40.7031], [-74.0100, 40.7032]]
B = [[-73.9901, 40.7131], [-73.9900, 40.7131]]


@pytest.fixture
def two_tiles(tmp_path):
	write_tiles(tmp_path, {
		'tile_0_0': (['8'], [feature('8', 'landing-8', A, [segment(0, 11, primary_face=True), segment(1, 12)])]),
		'tile_0_1': (['20'], [feature('20', 'landing-20', B, [segment(0, 21)])]),
		'tile_1_0': ([], None),
	})
	return tmp_path


def test_the_harbor_export_has_every_landing_once_in_the_slice_exports_order(two_tiles):
	records = export_unity.export_harbor_contacts(two_tiles, two_tiles / 'unity' / 'contacts.json')
	assert [(r['stop_id'], r['id']) for r in records] == [('20', 21), ('8', 11), ('8', 12)], 'by stop id as text, then file order'
	assert json.loads((two_tiles / 'unity' / 'contacts.json').read_text()) == records
	structures, owners = export_unity.harbor_structures(two_tiles)
	assert records == export_unity.contact_records(structures), 'the slice export of the union'
	assert owners == {'8': 'tile_0_0', '20': 'tile_0_1'}


def test_a_contact_id_in_two_tiles_is_refused(two_tiles):
	write_tiles(two_tiles / 'again', {
		'tile_0_0': (['8'], [feature('8', 'landing-8', A, [segment(0, 11), segment(1, 12)])]),
		'tile_0_1': (['20'], [feature('20', 'landing-20', B, [segment(0, 11)])]),
	})
	with pytest.raises(ValueError, match='contact id 11 appears twice'):
		export_unity.export_harbor_contacts(two_tiles / 'again', two_tiles / 'again' / 'contacts.json')


def test_the_same_edge_twice_is_refused_even_reversed(tmp_path):
	write_tiles(tmp_path, {
		'tile_0_0': (['8'], [feature('8', 'landing-8', A, [segment(0, 11), segment(1, 12)])]),
		'tile_0_1': (['20'], [feature('20', 'berth-20', A[:2][::-1], [segment(0, 31)], role='berth')]),
	})
	with pytest.raises(ValueError, match='segment 11 .* the same edge as segment 31'):
		export_unity.export_harbor_contacts(tmp_path, tmp_path / 'contacts.json')


@pytest.mark.parametrize('layout,expected', [
	({'tile_0_0': (['8'], [feature('20', 'landing-20', B, [segment(0, 21)])])}, 'landing 20 is not one tile_0_0 owns'),
	({'tile_0_0': (['8'], [feature('8', 'landing-8', A, [segment(0, 11)])]), 'tile_0_1': (['8'], [feature('8', 'berth-8', B, [segment(0, 12)])])},
		'landing 8 is in tile_0_0 and tile_0_1'),
	({'tile_0_0': (['8', '20'], [feature('8', 'landing-8', A, [segment(0, 11)])])}, r"landings without structures: \['20'\]"),
	({'tile_0_0': (['8'], None)}, 'missing, but tile_0_0 owns landings'),
], ids=['a landing outside its owner', 'a landing in two tiles', 'a landing missing', 'a landing file missing'])
def test_landing_ownership_is_enforced(tmp_path, layout, expected):
	write_tiles(tmp_path, layout)
	with pytest.raises(ValueError, match=expected):
		export_unity.export_harbor_contacts(tmp_path, tmp_path / 'contacts.json')


def record(**changes):
	"""A contacts.json record a metre long running north: water on its left (west), so water_normal_ne is (0, -1)."""
	base = {'id': 7, 'stop_id': '8', 'stop_name': 'Stop 8', 'start_ne_m': [0., 0.], 'end_ne_m': [1., 0.], 'water_normal_ne': [0., -1.],
		'source': 'enc-feature', 'confidence': 'high', 'verification': 'needs-crew-verification', 'water_side_verified_by_pipeline': True}
	base.update(changes)
	return {k: v for k, v in base.items() if v is not ...}


def turned(degrees):
	a = math.radians(degrees)
	return [-math.sin(a), -math.cos(a)]  # (0, -1) turned by degrees


@pytest.mark.parametrize('changes,verdict', [
	(dict(), 'kept'),
	(dict(water_side_verified_by_pipeline=True, water_side_crew_reported=True), 'kept'),
	(dict(water_side_verified_by_pipeline=False, water_side_crew_reported=True), 'kept'),
	(dict(water_side_verified_by_pipeline=..., water_side_crew_reported=True), 'kept'),
	(dict(water_side_verified_by_pipeline=False, water_side_crew_reported=False), 'left out'),
	(dict(water_side_verified_by_pipeline=False), 'left out'),
	(dict(water_side_verified_by_pipeline=...), 'left out'),
	(dict(water_side_verified_by_pipeline=False, solid_side_evidence=dict(verified=True)), 'kept'),
	(dict(water_side_verified_by_pipeline=False, solid_side_evidence=dict(verified=True), primary_face=True), 'left out'),
	(dict(water_side_verified_by_pipeline=False, solid_side_evidence=dict(verified=False)), 'left out'),
	(dict(water_normal_ne=turned(180.)), 'left out'),
	(dict(water_normal_ne=turned(90.)), 'left out'),
	(dict(water_normal_ne=turned(5.)), 'left out'),
	(dict(water_normal_ne=turned(.5)), 'kept'),
	(dict(water_side_verified_by_pipeline=False, water_side_crew_reported=True, water_normal_ne=turned(5.)), 'left out'),
	(dict(start_ne_m=[1., 0.], end_ne_m=[0., 0.]), 'left out'),
	(dict(id=0), 'malformed'),
	(dict(id=2 ** 31), 'malformed'),
	(dict(id=7.0), 'malformed'),
	(dict(id=True), 'malformed'),
	(dict(stop_id=' '), 'malformed'),
	(dict(stop_name=None), 'malformed'),
	(dict(source=...), 'malformed'),
	(dict(end_ne_m=[1., float('nan')]), 'malformed'),
	(dict(end_ne_m=[1.]), 'malformed'),
	(dict(water_normal_ne=[0., -1.01]), 'malformed'),
	(dict(water_side_crew_reported='yes'), 'malformed'),
	(dict(primary_face=1), 'malformed'),
	(dict(kind=' '), 'malformed'),
	(dict(berth_id=''), 'kept'),
	(dict(solid_side_evidence=dict(verified='true')), 'malformed'),
	(dict(solid_side_evidence=[]), 'malformed'),
	(dict(end_ne_m=[0.0005, 0.]), 'malformed'),
	(dict(extra_field=1), 'kept'),
])
def test_the_gate_port_follows_landing_geometrys_rule(changes, verdict):
	assert export_unity.landing_gate(record(**changes))[0] == verdict


def test_the_gate_and_the_pipelines_mark_agree_segment_by_segment(tmp_path):
	cases = [dict(), dict(water_side_verified_by_pipeline=False, solid_side_evidence=dict(verified=True)),
		dict(water_side_verified_by_pipeline=False, solid_side_evidence=dict(verified=True), primary_face=True),
		dict(water_side_verified_by_pipeline=False, water_side_crew_reported=True, primary_face=True),
		dict(water_side_verified_by_pipeline=False, solid_side_evidence=dict(verified=False))]
	lonlat = [[-74.0101 + 1e-4 * k, 40.7031] for k in range(len(cases) + 1)]
	features = [feature('8', 'landing-8', lonlat, [segment(k, 40 + k, **c) for k, c in enumerate(cases)])]
	write_tiles(tmp_path, {'tile_0_0': (['8'], features)})
	records = export_unity.export_harbor_contacts(tmp_path, tmp_path / 'contacts.json')
	counts = export_unity.gate_counts(records, export_unity.harbor_structures(tmp_path)[0])
	assert counts['8']['segments'] == 5 and counts['8']['gate'] == counts['8']['pipeline'] == 3 and counts['8']['disagree'] == []
	assert [landings.runtime_contact(s) for s in features[0]['properties']['segments']] == [True, True, False, True, False]
	broken = copy.deepcopy(records)
	broken[0]['water_normal_ne'] = [-x for x in broken[0]['water_normal_ne']]
	assert export_unity.gate_counts(broken, export_unity.harbor_structures(tmp_path)[0])['8']['disagree'] == [40]


def test_ferrydata_export_unity_harbor_writes_the_harbors_contacts(tmp_path, monkeypatch):
	from ferrydata import cli, paths
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', tmp_path)
	write_tiles(tmp_path / 'harbor', {'tile_0_0': (['8'], [feature('8', 'landing-8', A, [segment(0, 11, primary_face=True), segment(1, 12)])])})
	assert cli.main(['export-unity', '--harbor']) == 0
	assert [r['id'] for r in json.loads((tmp_path / 'harbor' / 'unity' / 'contacts.json').read_text())] == [11, 12]
	with pytest.raises(SystemExit):
		cli.main(['export-unity', '--harbor', '--slice', 'lower-east-river'])
