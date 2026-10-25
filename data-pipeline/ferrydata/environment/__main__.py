"""Build, verify or validate ferrysim-env/1 packs without changing the main pipeline CLI."""
import argparse
import json
import sys

from ferrydata import paths
from ferrydata.environment import pack, series


def main(argv=None):
	parser = argparse.ArgumentParser(description=__doc__)
	sub = parser.add_subparsers(dest='command', required=True)
	for verb in ('build', 'verify', 'validate'):
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
	elif args.command == 'verify':
		root = paths.processed_dir(args.region) / 'env'
		digests = pack.verify(root)
		for directory, digest in zip(sorted(p for p in (root / 'days').iterdir() if p.is_dir()), digests):
			print(f'{directory.name} {digest}')
		print(f'verified {len(digests)} packs')
	else:
		from ferrydata.environment import build as build_module, region_field, validation
		from ferrydata.environment.sources import Inputs
		source_dir = paths.processed_dir(args.region)
		root = source_dir / 'env'
		pack.verify(root)
		destination = paths.REPO_ROOT / 'artifacts' / 'current-field'
		days = sorted(p.name for p in (root / 'days').iterdir() if p.is_dir())
		print('[validate] subordinate series against the published events', flush=True)
		validation.subordinate_check(Inputs(lambda *a, **k: None), build_module.subordinate_ids(source_dir), days, destination)
		print(f'[validate] maps and landings, then leave-one-out and the parameter sweep on a {validation.LOO_CELL_M:g} m lattice', flush=True)
		lattice, water, bed, _ = region_field.inputs(args.region, root / 'depth')
		loo = region_field.inputs(args.region, root / 'depth', cell=validation.LOO_CELL_M)[:3]
		depth = json.loads((root / 'depth' / 'manifest.json').read_text())['grids'][0]
		extent = (depth['origin_north_m'], depth['origin_north_m'] + (depth['rows'] - 1) * depth['cell_m'],
			depth['origin_east_m'], depth['origin_east_m'] + (depth['cols'] - 1) * depth['cell_m'])
		faces = json.loads((source_dir / 'landing_faces.geojson').read_text())
		summary = validation.run((lattice, water, bed), loo, region_field.parameters(args.region), root, destination, extent=extent, faces=faces)
		pooled = summary['pooled']
		print(f'[validate] pooled RMS vector error: field {pooled["field"]["rms_vector_error_mps"]:.4f} m/s, '
			f'baseline {pooled["baseline"]["rms_vector_error_mps"]:.4f} m/s; written to {destination}', flush=True)
	return 0


if __name__ == '__main__':
	sys.exit(main())
