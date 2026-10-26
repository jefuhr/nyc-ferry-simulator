"""Build, verify or validate ferrysim-env/1 packs without changing the main pipeline CLI."""
import argparse
import json
import sys

from ferrydata import paths
from ferrydata.environment import pack, series


def main(argv=None):
	parser = argparse.ArgumentParser(description=__doc__)
	sub = parser.add_subparsers(dest='command', required=True)
	for verb in ('build', 'verify', 'validate', 'transit-route', 'transit-check'):
		p = sub.add_parser(verb)
		p.add_argument('--region', required=True, choices=['harbor'] if verb.startswith('transit') else ['lower-east-river', 'harbor'])
		if verb == 'transit-route':
			p.add_argument('--start', required=True, help='north,east of the start, world metres')
			p.add_argument('--end', required=True, help='north,east of the end, world metres')
			p.add_argument('--deep', type=float, default=-6., help='the route keeps to water whose bed is below this, m NAVD88')
			p.add_argument('--clear', type=float, default=25., help='and plans its path at least this far from shallower water, m')
			p.add_argument('--out', required=True, help='the waypoints CSV for ferrysim maneuver transit')
		if verb == 'build':
			group = p.add_mutually_exclusive_group(required=True)
			group.add_argument('--all', action='store_true')
			group.add_argument('--days', help='comma-separated scenario dates')
		if verb == 'transit-check':
			p.add_argument('--out', required=True, help='folder for the table and the plots')
			p.add_argument('csv', nargs='+', help='ferrysim transit CSVs run on a harbor pack')
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
	elif args.command == 'transit-route':
		from ferrydata.environment import harbor, harbor_transit
		start, end = [tuple(float(x) for x in text.split(',')) for text in (args.start, args.end)]
		waypoints = harbor_transit.plan(pack.load_depth(harbor.tiles_root() / 'env' / 'depth'), start, end, args.deep, args.clear)
		harbor_transit.write_route(args.out, waypoints)
		print(f'[transit-route] {len(waypoints)} waypoints to {args.out}: ' + '; '.join(f'{n:.1f},{e:.1f}' for n, e in waypoints), flush=True)
	elif args.command == 'transit-check':
		from ferrydata.environment import harbor_transit
		passed = harbor_transit.run(args.csv, args.out)
		print(f'[transit-check] {"PASS" if passed else "FAIL"}; written to {args.out}', flush=True)
		return 0 if passed else 1
	elif args.region == 'harbor':
		from ferrydata.environment import harbor_validation
		summary = harbor_validation.run(paths.REPO_ROOT / 'artifacts' / 'environment-pack' / 'harbor')
		print(f'[validate] harbor depth: cell-size evidence and {summary["seams"]} seams; written to {summary["destination"]}', flush=True)
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
