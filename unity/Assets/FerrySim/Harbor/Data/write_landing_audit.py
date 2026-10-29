"""Write Harbor/Data/landing-audit.json from the landing matrix's berths.json and the adopted depth set's DIGEST.

Usage: python write_landing_audit.py <berths.json> <env/depth/DIGEST> <landing-audit.json>
Deterministic: the same inputs give the same bytes (tabs, LF, sorted as the matrix lists the berths).
"""
import json
import hashlib
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[5]


def ukc(value):
	return None if value is None or value != value else round(value, 3)


def main(argv):
	berths_path, digest_path, out_path = map(Path, argv[1:4])
	provenance_path = berths_path.with_name('provenance.json')
	provenance = json.loads(provenance_path.read_text(encoding='utf-8'))
	digest = digest_path.read_text(encoding='ascii').strip()
	if provenance.get('depth_digest') != digest:
		raise ValueError(f'{provenance_path}: matrix depth {provenance.get("depth_digest")} does not match adopted depth {digest} in {digest_path}')
	berths_bytes = berths_path.read_bytes()
	if provenance.get('berths_sha256') != hashlib.sha256(berths_bytes).hexdigest():
		raise ValueError(f'{provenance_path}: berths_sha256 does not match {berths_path}')
	if not provenance.get('contacts_sha256') or not provenance.get('day_digests'):
		raise ValueError(f'{provenance_path}: contacts_sha256 and day_digests are required')
	for hull in ('t-class', 'k-class'):
		file = f'vessels/{hull}.json'
		assessed = provenance.get('vessel_sha256', {}).get(file)
		actual = hashlib.sha256((REPO_ROOT / file).read_bytes()).hexdigest()
		if assessed != actual:
			raise ValueError(f'{provenance_path}: {file} SHA-256 mismatch; assessed {assessed}, shipped {actual}')
	berths = json.loads(berths_bytes)
	rows = {h['rows'] for b in berths for h in b['hulls']}
	if len(rows) != 1:
		raise SystemExit(f'every hull must have the same row count, got {sorted(rows)}')
	out = {
		'schema': 'ferrysim-harbor-landing-audit/1',
		'source': 'python -m ferrydata.harbor_landings matrix on the adopted harbor environment packs (data-pipeline/ferrydata/harbor_landings/README.md)',
		'depth_digest': digest,
		'matrix_provenance': provenance,
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
