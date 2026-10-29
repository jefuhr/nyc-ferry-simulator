"""Write Harbor/Data/landing-audit.json from the landing matrix's berths.json and the adopted depth set's DIGEST.

Usage: python write_landing_audit.py <berths.json> <env/depth/DIGEST> <landing-audit.json>
Deterministic: the same inputs give the same bytes (tabs, LF, sorted as the matrix lists the berths).
"""
import json
import sys
from pathlib import Path


def ukc(value):
	return None if value is None or value != value else round(value, 3)


def main(argv):
	berths_path, digest_path, out_path = map(Path, argv[1:4])
	berths = json.loads(berths_path.read_text(encoding='utf-8'))
	rows = {h['rows'] for b in berths for h in b['hulls']}
	if len(rows) != 1:
		raise SystemExit(f'every hull must have the same row count, got {sorted(rows)}')
	out = {
		'schema': 'ferrysim-harbor-landing-audit/1',
		'source': 'python -m ferrydata.harbor_landings matrix on the adopted harbor environment packs (data-pipeline/ferrydata/harbor_landings/README.md)',
		'depth_digest': digest_path.read_text(encoding='ascii').strip(),
		'rows_per_hull': rows.pop(),
		'berths': [
			{
				'stop_id': b['stop_id'], 'stop_name': b['stop_name'], 'berth_id': b['berth_id'], 'berth_name': b['berth_name'],
				'eligible': b['eligible'],
				'hulls': [{'hull': h['hull'], 'rows': h['rows'], 'accepted': h['accepted'], 'warning': h['warning'], 'aground_refusal': h['aground_refusal'],
					'worst_ukc_m': ukc(h['worst_ukc_m'])} for h in b['hulls']],
			} for b in berths],
	}
	out_path.write_text(json.dumps(out, indent='\t', ensure_ascii=False) + '\n', encoding='utf-8')
	print(f'wrote {len(out["berths"])} berths to {out_path}')


if __name__ == '__main__':
	main(sys.argv)
