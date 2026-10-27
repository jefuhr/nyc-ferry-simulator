"""Compare two independent exports, allowing only FBX container metadata to vary."""
import argparse
import hashlib
import json
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('first', type=Path)
p.add_argument('second', type=Path)
a = p.parse_args()
first, second = (json.loads((d / 'manifest.json').read_text()) for d in (a.first, a.second))
results = {}
for key, model in first['models'].items():
	other = second['models'][key]
	for field in ('geometry_sha256', 'triangles', 'vertices', 'dimensions', 'mounts', 'bounds_blender'):
		assert model[field] == other[field], (key, field)
	assert model['triangles'] <= (5000 if key.endswith('LOD1') else 50000)
	results[key] = {'geometry_sha256': model['geometry_sha256'], 'triangles': model['triangles'],
		'fbx_bytes_identical': model['fbx_sha256'] == other['fbx_sha256'], 'first_fbx_sha256': model['fbx_sha256'], 'second_fbx_sha256': other['fbx_sha256']}
for name in ('NeutralPalette.png', 'audited-classes.json'):
	assert (a.first / name).read_bytes() == (a.second / name).read_bytes(), name
print(json.dumps({'passed': True, 'models': results}, indent=2, sort_keys=True))
