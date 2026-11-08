"""Compare complete exports and verify their receipts against actual FBX payloads.

FBX container timestamps may differ; canonical geometry, source data and materials may not.
"""
import argparse
import hashlib
import json
from pathlib import Path

EXPECTED = {f'{name}/LOD{lod}' for name in ('t-class','k-class') for lod in (0,1)}
FIELDS = ('geometry_sha256','triangles','vertices','dimensions','mounts','bounds_blender','layout_sha256','materials')


def verify(first_path, second_path):
	first,second = (json.loads((path/'manifest.json').read_text()) for path in (first_path,second_path))
	for manifest in (first,second):
		if manifest['schema'] != 'ferrysim-models/2' or set(manifest['models']) != EXPECTED:
			raise ValueError('Expected the complete T/K LOD0/LOD1 export set and schema v2')
	results = {}
	for key in sorted(EXPECTED):
		model,other = first['models'][key],second['models'][key]
		for field in FIELDS:
			if model[field] != other[field]:
				raise ValueError(f'{key}: non-deterministic {field}')
		budget = 5000 if key.endswith('LOD1') else 80000
		if not 0 < model['triangles'] <= budget:
			raise ValueError(f'{key}: triangle count outside 1..{budget}')
		for path,receipt in ((first_path,model),(second_path,other)):
			actual = hashlib.sha256((path/(key+'.fbx')).read_bytes()).hexdigest()
			if actual != receipt['fbx_sha256']:
				raise ValueError(f'{path}/{key}: stale or tampered FBX receipt')
		results[key] = dict(geometry_sha256=model['geometry_sha256'],triangles=model['triangles'],
			fbx_bytes_identical=model['fbx_sha256']==other['fbx_sha256'],first_fbx_sha256=model['fbx_sha256'],second_fbx_sha256=other['fbx_sha256'])
	for name in ('NeutralPalette.png','audited-classes.json'):
		if (first_path/name).read_bytes() != (second_path/name).read_bytes():
			raise ValueError('Non-deterministic '+name)
	return dict(passed=True,models=results)


if __name__ == '__main__':
	parser = argparse.ArgumentParser()
	parser.add_argument('first',type=Path)
	parser.add_argument('second',type=Path)
	args = parser.parse_args()
	print(json.dumps(verify(args.first,args.second),indent=2,sort_keys=True))
