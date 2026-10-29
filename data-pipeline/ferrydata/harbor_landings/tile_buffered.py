"""One per-tile locked derivation of a hazard-buffered review candidate."""
import json
import sys
from pathlib import Path

import numpy as np
import rasterio
from shapely import wkb

from ferrydata import manifest, operational_depth as od
from ferrydata.environment import pack
from ferrydata.harbor_landings import buffered


def main():
	source, base, target = map(Path, sys.argv[1:4])
	distance = float(sys.argv[4])
	items = json.loads((target / 'hazards.wkb.json').read_text(encoding='utf-8'))
	for h in items:
		h['geometry'] = wkb.loads(h.pop('wkb'), hex=True)
	with rasterio.open(source / 'depth_merged_navd88.tif') as ds:
		bed, profile = ds.read(1), ds.profile
	with rasterio.open(base / od.FLOOR_FILE) as ds:
		base_floor = ds.read(1)
	new, lowered, changed, floor = buffered.apply(bed, base_floor, profile['transform'], profile.get('nodata'), items, distance)
	tags = dict(method=od.METHOD, vertical_datum='NAVD88', units='m', hazard_buffer_m=str(distance))
	od._write(target / od.FLOOR_FILE, floor, {**profile, 'dtype': 'float32', 'nodata': float('nan')}, {**tags, 'positive': 'up'})
	od._write(target / od.LOWERED_FILE, lowered, {**profile, 'dtype': 'float64', 'nodata': float('nan')}, {**tags, 'positive': 'down'})
	od._write(target / 'depth_merged_navd88.tif', new, profile, {**tags, 'positive': 'up'})
	prov = json.loads((base / od.REPORT_FILE).read_text(encoding='utf-8'))
	prov['parameters']['hazard_buffer_m'] = distance
	prov['floor_cells'], prov['cells_lowered'] = int(np.isfinite(floor).sum()), int(changed.sum())
	amounts = lowered[changed]
	prov['max_lowering_m'] = float(amounts.max()) if amounts.size else 0.
	prov['mean_lowering_m'] = float(amounts.mean()) if amounts.size else 0.
	prov['floor_sha256'], prov['lowered_sha256'] = manifest.sha256_file(target / od.FLOOR_FILE), manifest.sha256_file(target / od.LOWERED_FILE)
	prov['sources'].update(base_floor_sha256=manifest.sha256_file(base / od.FLOOR_FILE),
		hazards_sha256=manifest.sha256_file(target / 'hazards.wkb.json'))
	prov['notes'].append('Review-only hazard buffer measured from cell centres in world metres; no adoption approved by building this variant.')
	pack.write_json(target / od.REPORT_FILE, prov)
	metadata = json.loads((source / 'depth_merged_navd88.json').read_text(encoding='utf-8'))
	metadata['operational_ais'] = dict(prov, report_file=od.REPORT_FILE, report_sha256=manifest.sha256_file(target / od.REPORT_FILE))
	pack.write_json(target / 'depth_merged_navd88.json', metadata)


if __name__ == '__main__':
	main()
