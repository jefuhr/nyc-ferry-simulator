"""Fixed inventory and raw-byte digest framing, matching the runtime pack."""
from pathlib import Path
import re
import json

from ferrydata.harbor_runtime.pack import digest, read_json, safe_file, sha, write_json

FORMAT='ferrysim-harbor-chart/1'
LAYERS=('land','depth_areas','contours','soundings','hazards','channels','radar_obstructions','references')
FILES=tuple(name+'.json' for name in LAYERS)


def verify(folder,runtime=None):
	folder=Path(folder)
	if {p.name for p in folder.iterdir()}!={'manifest.json','DIGEST',*FILES}:
		raise ValueError('chart file inventory mismatch')
	manifest=read_json(safe_file(folder,'manifest.json'))
	if manifest.get('depth_datum')!='MLLW' or set(manifest.get('counts',{}))!=set(LAYERS) or manifest.get('format')!=FORMAT or manifest.get('schema_version')!=1 or set(manifest.get('files',{}))!=set(FILES):
		raise ValueError('chart schema or inventory mismatch')
	body=safe_file(folder,'DIGEST').read_bytes()
	if re.fullmatch(b'[0-9a-f]{64}\n',body) is None or digest(folder,('manifest.json',*FILES))!=body[:64].decode():
		raise ValueError('chart digest mismatch')
	if runtime and any(manifest[k]!=runtime[k] for k in ('origin_latlon','coordinate_frame')):
		raise ValueError('chart frame mismatch')
	for name in LAYERS:
		file=name+'.json'
		if sha(safe_file(folder,file))!=manifest['files'][file]:
			raise ValueError(f'{file}: hash mismatch')
		payload=read_json(folder/file)
		from ferrydata.harbor_chart.build import layer_payload,expand
		rows=expand(payload)
		if json.dumps(payload,sort_keys=True)!=json.dumps(layer_payload(rows,name=='references'),sort_keys=True) or len(payload['features'])!=manifest['counts'][name]:
			raise ValueError(f'{file}: invalid geometry, counts or grid index')
	return manifest
