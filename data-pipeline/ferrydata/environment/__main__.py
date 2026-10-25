"""Build or verify ferrysim-env/1 packs without changing the main pipeline CLI."""
import argparse
import sys

from ferrydata import paths
from ferrydata.environment import pack, series


def main(argv=None):
	parser = argparse.ArgumentParser(description=__doc__)
	sub = parser.add_subparsers(dest='command', required=True)
	for verb in ('build', 'verify'):
		p = sub.add_parser(verb)
		p.add_argument('--region', required=True, choices=['lower-east-river'])
		if verb == 'build':
			group = p.add_mutually_exclusive_group(required=True)
			group.add_argument('--all', action='store_true')
			group.add_argument('--days', help='comma-separated scenario dates')
	args = parser.parse_args(argv)
	if args.command == 'build':
		from ferrydata.environment.build import build
		days = series.DAYS if args.all else args.days.split(',')
		if any(day not in series.DAYS for day in days):
			parser.error('days must belong to the 28 cached AIS scenario days')
		build(args.region, days)
	else:
		root = paths.processed_dir(args.region) / 'env'
		digests = pack.verify(root)
		for directory, digest in zip(sorted(p for p in (root / 'days').iterdir() if p.is_dir()), digests):
			print(f'{directory.name} {digest}')
		print(f'verified {len(digests)} packs')
	return 0


if __name__ == '__main__':
	sys.exit(main())
