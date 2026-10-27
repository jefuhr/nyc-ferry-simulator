"""Raw-byte pack integrity: filename, NUL, bytes, NUL, with manifest first (env/traffic convention)."""
import hashlib
import json
import re
from pathlib import Path

import numpy as np
from shapely.geometry import shape

FORMAT = 'ferrysim-harbor-runtime/1'
FILES = ('aids.json','bridges.json','contacts.json','landings.json','geoid.json','geoid.f64','geoid_cells.u8','footprints.json')


def sha(path):
	h=hashlib.sha256()
	with Path(path).open('rb') as stream:
		for chunk in iter(lambda:stream.read(1024*1024),b''):
			h.update(chunk)
	return h.hexdigest()


def write_json(path,value):
	Path(path).write_text(json.dumps(value,ensure_ascii=False,allow_nan=False,separators=(',',':'),sort_keys=True)+'\n',encoding='utf-8')


def read_json(path):
	def unique(pairs):
		out={}
		for key,value in pairs:
			if key in out:
				raise ValueError(f'{path}: duplicate JSON key {key}')
			out[key]=value
		return out
	return json.loads(Path(path).read_text(encoding='utf-8'),object_pairs_hook=unique,
		parse_constant=lambda value: (_ for _ in ()).throw(ValueError(f'{path}: nonfinite {value}')))


def safe_file(root,name):
	root=Path(root).resolve()
	if not isinstance(name,str) or not name or '\\' in name or Path(name).is_absolute() or '..' in Path(name).parts:
		raise ValueError(f'unsafe relative path {name!r}')
	path=root/name
	if not path.resolve().is_relative_to(root) or path.is_symlink() or not path.is_file():
		raise ValueError(f'unsafe or missing file {name!r}')
	return path


def digest(directory,names=('manifest.json',*FILES)):
	h=hashlib.sha256()
	for name in names:
		h.update(name.encode('utf-8')+b'\0')
		h.update(safe_file(directory,name).read_bytes())
		h.update(b'\0')
	return h.hexdigest()


def verify(directory,harbor=None):
	directory=Path(directory)
	manifest=read_json(directory/'manifest.json')
	if manifest.get('format') != FORMAT or manifest.get('files',{}).keys() != set(FILES):
		raise ValueError('runtime format or file inventory mismatch')
	if {p.name for p in directory.iterdir()} != {'manifest.json','DIGEST',*FILES}:
		raise ValueError('runtime directory inventory mismatch')
	body=safe_file(directory,'DIGEST').read_bytes()
	if re.fullmatch(b'[0-9a-f]{64}\n',body) is None:
		raise ValueError('DIGEST must be 64 lowercase hex and LF')
	if digest(directory) != body[:64].decode('ascii'):
		raise ValueError('runtime digest mismatch')
	for name,expected in manifest['files'].items():
		if sha(safe_file(directory,name)) != expected:
			raise ValueError(f'{name}: hash mismatch')
	if harbor is not None:
		for source in manifest['sources']:
			if sha(safe_file(harbor,source['file'])) != source['sha256']:
				raise ValueError(f"{source['file']}: source hash mismatch")
		from ferrydata.environment.pack import depth_digest, read_digest
		depth=Path(harbor)/'env/depth'
		depth_manifest=read_json(safe_file(harbor,'env/depth/manifest.json'))
		for grid in depth_manifest['grids']:
			if sha(safe_file(depth,grid['file']))!=grid['sha256']:
				raise ValueError(f"{grid['file']}: depth source hash mismatch")
		if depth_digest(depth)!=read_digest(depth) or read_digest(depth)!=manifest['environment_depth_digest']:
			raise ValueError('environment depth digest linkage mismatch')
	_validate_content(directory,manifest)
	return manifest


def _ids(records,label):
	ids=[r['id'] for r in records]
	if len(ids)!=len(set(ids)) or any(not i for i in ids):
		raise ValueError(f'{label}: duplicate or empty ids')


def _validate_content(directory,manifest):
	aids=read_json(directory/'aids.json')['aids']; _ids(aids,'aids')
	bridges=read_json(directory/'bridges.json')['bridges']; _ids(bridges,'bridges')
	landings=read_json(directory/'landings.json'); _ids(landings['barges'],'barges')
	stops=landings['stops']; stop_ids=[s['stop_id'] for s in stops]
	if len(stop_ids)!=len(set(stop_ids)) or len(stops)!=manifest['counts']['stops']:
		raise ValueError('stop inventory mismatch')
	barges={b['id']:b for b in landings['barges']}
	for barge in barges.values():
		g=shape(barge['geometry_ne_m'])
		if not g.is_valid or g.is_empty or g.geom_type!='Polygon' or g.area<=0:
			raise ValueError('invalid closed barge outline')
		_barge_orientation(g)
	for stop in stops:
		for berth in stop['berths']:
			if berth['structure_id'] not in barges:
				raise ValueError('berth references missing barge')
	footprints=read_json(directory/'footprints.json'); coverage=shape(footprints['coverage_ne_m'])
	actual_counts=dict(stops=len(stops),berths=sum(len(s['berths']) for s in stops),barges=len(barges),
		aids=len(aids),bridges=len(bridges),spans=sum(len(b['spans']) for b in bridges),footprints=len(footprints['footprints']))
	if actual_counts!=manifest['counts']:
		raise ValueError('runtime content count mismatch')
	if not coverage.is_valid or coverage.is_empty:
		raise ValueError('invalid harbor coverage')
	_ids(footprints['footprints'],'footprints')
	for item in footprints['footprints']:
		g=shape(item['geometry_ne_m'])
		if not g.is_valid or g.is_empty or not coverage.covers(g) or item['collision_authority'] is not False:
			raise ValueError('invalid or outside semantic footprint')
		if item['semantic']=='barge':
			_barge_orientation(g)
	m=read_json(directory/'geoid.json')
	if (m.get('format')!='ferrysim-harbor-geoid/1' or m.get('values_file')!='geoid.f64' or m.get('mask_file')!='geoid_cells.u8'
		or m.get('values_type')!='little-endian float64' or m.get('layout')!='row-major south-to-north; columns west-to-east'
		or any(type(m.get(key)) is not int or m[key]<2 for key in ('rows','cols'))
		or not isinstance(m.get('mask_shape'),list) or any(type(v) is not int for v in m['mask_shape'])
		or m['mask_shape']!=[m['rows']-1,m['cols']-1] or m['cell_m']<=0):
		raise ValueError('invalid geoid schema')
	values=np.frombuffer((directory/'geoid.f64').read_bytes(),dtype='<f8')
	mask=np.frombuffer((directory/'geoid_cells.u8').read_bytes(),dtype=np.uint8)
	if values.size != m['rows']*m['cols'] or mask.size != (m['rows']-1)*(m['cols']-1) or not np.isfinite(values).all() or not np.isin(mask,[0,1]).all():
		raise ValueError('invalid geoid grid length/values')


def _barge_orientation(geometry):
	from ferrydata.harbor_runtime.geometry import parts
	for polygon in parts(geometry):
		if polygon.geom_type!='Polygon' or not polygon.exterior.is_ccw or any(ring.is_ccw for ring in polygon.interiors):
			raise ValueError('invalid barge ring orientation: solid must be on the right')
