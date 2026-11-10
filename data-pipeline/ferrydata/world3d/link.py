"""Refresh terrain provenance only when every geometry-generating input is equivalent."""
from pathlib import Path
from types import SimpleNamespace

from ferrydata import paths
from ferrydata.world3d import build


class RebuildRequired(ValueError):
	"""The current inputs cannot be proved equivalent to the existing terrain."""


def relink(harbor_dir, log=print):
	"""Keep all terrain content bytes; update only landing hashes after checking barge exclusions.

	Terrain reads landing structures only through barge_lnams. With identical depth,
	geoid and shore bytes, equal per-tile excluded shore IDs prove equal mesh inputs.
	The old terrain index already records those exclusions, so legacy packs need no
	invented projection hash or hand-edited pin to migrate.
	"""
	root = Path(harbor_dir)
	active, index = build.active_tiles(root)
	out = root / '3dtiles'
	marker = out / 'index.json'
	if not marker.is_file():
		raise RebuildRequired('no terrain index')
	world = build.read_json(marker)
	if world.get('schema') != build.SCHEMA:
		raise ValueError('unsupported terrain schema')
	current = build.source_records(root, index, active)
	old = {s['file']: s['sha256'] for s in world['sources']}
	if len(old) != len(world['sources']):
		raise ValueError('duplicate terrain source pins')
	if old.keys() != {s['file'] for s in current}:
		raise RebuildRequired('terrain source inventory changed')
	for source in current:
		if not source['file'].endswith('/landing_structures.geojson') and old[source['file']] != source['sha256']:
			raise RebuildRequired(f"{source['file']}: terrain input changed")
	# Do not relink a damaged terrain product or erase its evidence.
	if build.file_records(out) != world['files']:
		raise ValueError('terrain file inventory or hashes differ')
	records = {t['id']: t for t in world['tiles']}
	if len(records) != len(world['tiles']) or records.keys() != {build.frames.tile_id(i, j) for i, j in active}:
		raise RebuildRequired('active terrain tiles changed')
	sources = SimpleNamespace(active=active, tile_dir=lambda i, j: root / 'tiles' / build.frames.tile_id(i, j))
	barges = build.barge_lnams(sources)
	for i, j in active:
		document = build.read_json(sources.tile_dir(i, j) / 'shoreline_constructions.geojson')
		excluded = sorted({f['properties'].get('id') for f in document['features'] if f['properties'].get('id') in barges})
		if excluded != records[build.frames.tile_id(i, j)]['structures']['left_out_barges']:
			raise RebuildRequired('landing barge exclusions changed')
	if world['sources'] == current:
		log('[world3d] terrain inputs unchanged')
		return 'unchanged'
	world['sources'] = current
	# Same-directory replace: an interrupted write cannot leave a partial index.
	staging = marker.with_suffix('.json.relinking')
	try:
		build.write_json(staging, world)
		staging.replace(marker)
	finally:
		staging.unlink(missing_ok=True)
	log('[world3d] relinked landing metadata; terrain content unchanged')
	return 'relinked'


def refresh(harbor_dir=None, log=print):
	"""Relink equivalent landing metadata, otherwise run the existing terrain builder."""
	root = Path(harbor_dir) if harbor_dir else paths.processed_dir('harbor')
	try:
		return relink(root, log)
	except RebuildRequired as error:
		log(f'[world3d] rebuild required: {error}')
		build.build(root, root / '3dtiles', log=log)
		return 'rebuilt'
