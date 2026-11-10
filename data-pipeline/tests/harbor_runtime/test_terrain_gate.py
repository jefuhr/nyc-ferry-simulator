"""Terrain must retain its own depth/shore linkage, even when runtime does not read the raster."""
import hashlib

import pytest

from test_runtime import harbor, load, write
from ferrydata.harbor_runtime import build


@pytest.mark.parametrize('name', ['depth_merged_navd88.tif', 'shoreline_constructions.geojson'])
def test_changed_terrain_input_refused_after_tile_rebuild(harbor, name):
	rel = 'tiles/tile_0_0/' + name
	p = harbor / rel
	if not p.exists():
		p.write_bytes(b'old depth')
	old = hashlib.sha256(p.read_bytes()).hexdigest()
	write(harbor / '3dtiles/index.json', dict(schema='harbor-3dtiles-v1', sources=[dict(file=rel, sha256=old)]))
	p.write_bytes(p.read_bytes() + b' ')
	index = load(harbor, 'index.json')
	entry = index['tiles'][0]
	entry['outputs'] = [o for o in entry['outputs'] if o['file'] != rel]
	entry['outputs'].append(dict(file=rel, sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
	write(harbor / 'index.json', index)
	with pytest.raises(ValueError, match='terrain index hash differs'):
		build(harbor, harbor / 'runtime')


def test_metadata_only_change_passes_runtime_after_relink(harbor):
	from ferrydata.world3d.build import source_records
	from ferrydata.world3d.link import relink
	index = load(harbor, 'index.json')
	active = [(e['i'], e['j']) for e in index['tiles']]
	for entry in index['tiles']:
		p = harbor / 'tiles' / entry['id'] / 'depth_merged_navd88.tif'
		p.write_bytes(b'fixture depth')
		entry['outputs'].append(dict(file=p.relative_to(harbor).as_posix(), sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
	write(harbor / 'index.json', index)
	world = dict(schema='harbor-3dtiles-v1', sources=source_records(harbor, index, active), files=[],
		tiles=[dict(id=e['id'], structures=dict(left_out_barges=[])) for e in index['tiles']])
	write(harbor / '3dtiles/index.json', world)
	p = harbor / 'tiles/tile_0_0/landing_structures.geojson'
	value = load(p.parent, p.name); value['features'][0]['properties']['crew_notes_sha256'] = 'refreshed evidence'
	write(p, value)
	for output in index['tiles'][0]['outputs']:
		if output['file'] == p.relative_to(harbor).as_posix():
			output['sha256'] = hashlib.sha256(p.read_bytes()).hexdigest()
	write(harbor / 'index.json', index)
	with pytest.raises(ValueError, match='terrain index hash differs'):
		build(harbor, harbor / 'runtime')
	assert relink(harbor, log=lambda _: None) == 'relinked'
	build(harbor, harbor / 'runtime')
