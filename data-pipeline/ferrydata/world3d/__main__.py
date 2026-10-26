"""Build or validate the harbor's local 3D Tiles without changing the main pipeline CLI.

	PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.world3d build
	PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.world3d validate
"""
import argparse
import json
import sys
from pathlib import Path


def main(argv=None):
	parser = argparse.ArgumentParser(prog="python -m ferrydata.world3d", description=__doc__.splitlines()[0])
	sub = parser.add_subparsers(dest="command", required=True)
	for verb, text in (("build", "write data/processed/harbor/3dtiles/ from harbor tiles v1"), ("validate", "check the 3D Tiles and write the report")):
		p = sub.add_parser(verb, help=text)
		p.add_argument("--harbor", help="the harbor tiles v1 folder (default data/processed/harbor)")
		p.add_argument("--out", help="the 3D Tiles folder (default data/processed/harbor/3dtiles)")
		if verb == "build":
			p.add_argument("--metrics", help="also write the build's time and peak memory to this JSON file")
		else:
			p.add_argument("--slice", help="the slice outputs to compare terrain with (default data/processed/lower-east-river)")
			p.add_argument("--import-manifest", help="the slice import's manifest (default World/Generated/LowerEastRiver/import-manifest.json)")
			p.add_argument("--report", help="where validation.md and validation.json go (default artifacts/world3d)")
	args = parser.parse_args(argv)
	if args.command == "build":
		from ferrydata.world3d.build import build
		metrics = build(args.harbor, args.out)
		if args.metrics:
			Path(args.metrics).parent.mkdir(parents=True, exist_ok=True)
			Path(args.metrics).write_text(json.dumps(metrics, indent="\t") + "\n", encoding="utf-8")
		return 0
	from ferrydata.world3d.validate import validate
	summary = validate(args.out, args.harbor, slice_dir=args.slice, import_manifest=args.import_manifest, report_dir=args.report)
	return 0 if summary["passed"] else 1


if __name__ == "__main__":
	sys.exit(main())
