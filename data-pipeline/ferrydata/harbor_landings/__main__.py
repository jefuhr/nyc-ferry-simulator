"""Offline candidate depth and evidence-backed landing assessment; adoption is a separate reviewed step."""
import argparse
import json
from pathlib import Path

from ferrydata.harbor_landings import assessment, candidate, matrix


def main(argv=None):
	parser = argparse.ArgumentParser(description=__doc__)
	sub = parser.add_subparsers(dest='command', required=True)
	p = sub.add_parser('parity')
	p.add_argument('--s1', required=True)
	p.add_argument('--env', required=True)
	p.add_argument('--out', required=True)
	p = sub.add_parser('candidate')
	p.add_argument('--source', required=True)
	p.add_argument('--out', required=True)
	p = sub.add_parser('buffered')
	p.add_argument('--source', required=True)
	p.add_argument('--base', required=True)
	p.add_argument('--out', required=True)
	p.add_argument('--buffer-m', required=True, type=float, choices=(10., 25.))
	p.add_argument('--heavy-lock-held', action='store_true', help='only inside an explicitly authorized outer heavy.lock batch; disables inner tile locks')
	p = sub.add_parser('matrix')
	p.add_argument('--contacts', required=True)
	p.add_argument('--env', required=True)
	p.add_argument('--depth')
	p.add_argument('--out', required=True)
	args = parser.parse_args(argv)
	if args.command == 'parity':
		print(json.dumps(assessment.parity(args.s1, args.env, args.out), indent=2))
	elif args.command == 'candidate':
		result = candidate.build(args.source, args.out)
		print(json.dumps({k: result[k] for k in ('cells_lowered', 'max_lowering_m', 'near_hazard_records', 'depth_digest')}, indent=2))
	elif args.command == 'buffered':
		from ferrydata.harbor_landings import buffered
		result = buffered.build(args.source, args.base, args.out, args.buffer_m, lock_held=args.heavy_lock_held)
		print(json.dumps({k: result[k] for k in ('cells_lowered', 'max_lowering_m', 'near_hazard_records', 'depth_digest')}, indent=2))
	else:
		matrix.build(args.contacts, args.env, args.out, args.depth)
	return 0


if __name__ == '__main__':
	raise SystemExit(main())
