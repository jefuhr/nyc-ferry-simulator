"""One lockf-protected harbor tile application; called by candidate.build."""
import json
import sys
from pathlib import Path

import pandas as pd
import rasterio
from shapely import wkb

from ferrydata import manifest, operational_depth as od
from ferrydata.environment import pack


def main():
	source, target = map(Path, sys.argv[1:])
	items = json.loads((target / 'hazards.json').read_text(encoding='utf-8'))
	for h in items:
		h['geometry'] = wkb.loads(h.pop('wkb'), hex=True)
	with rasterio.open(source / 'depth_merged_navd88.tif') as ds:
		bed, profile = ds.read(1), ds.profile
	sources = {od.REPORTS_FILE: manifest.sha256_file(target.parent.parent / od.REPORTS_FILE),
		'identity-audit.csv': manifest.sha256_file(od.identity_audit_path())}
	_, provenance = od.build_harbor_tile(source, target, bed, profile, pd.read_pickle(target / 'reports.pkl'), od.vessel_classes(), sources, items)
	pack.write_json(target / od.REPORT_FILE, provenance)
	metadata = json.loads((source / 'depth_merged_navd88.json').read_text(encoding='utf-8'))
	metadata['operational_ais'] = dict(provenance, report_file=od.REPORT_FILE, report_sha256=manifest.sha256_file(target / od.REPORT_FILE))
	pack.write_json(target / 'depth_merged_navd88.json', metadata)


if __name__ == '__main__':
	main()
