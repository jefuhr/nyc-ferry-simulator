"""ferrydata command line: fetch (download to the cache), build (process from the cache only), validate."""

import argparse
import sys
import time

from ferrydata import ais, coops, cudem, enc, geoid, gtfs, landings, navdepth, slices, tiles

# Order matters for build: ais needs the GTFS landings, validate needs everything.
SOURCES = {
	"gtfs": gtfs,
	"enc": enc,
	"cudem": cudem,
	"coops": coops,
	"geoid": geoid,
	"ais": ais,
}


def _selected(args):
	names = args.only.split(",") if args.only else list(SOURCES)
	unknown = [n for n in names if n not in SOURCES]
	if unknown:
		raise SystemExit(f"unknown source(s): {', '.join(unknown)}; choose from {', '.join(SOURCES)}")
	skip = set(args.skip.split(",")) if args.skip else set()
	return [n for n in names if n not in skip]


def _run(verb, args):
	slice_ = slices.get_slice(args.slice)
	for name in _selected(args):
		print(f"[{verb}] {name}", flush=True)
		started = time.time()
		module = SOURCES[name]
		if verb == "fetch":
			module.fetch(slice_, force=args.force)
		else:
			module.build(slice_)
		print(f"[{verb}] {name} done in {time.time() - started:.1f}s", flush=True)
	# Derived products require GTFS + ENC + CUDEM + CO-OPS. Partial source builds
	# remain useful; request the full build to refresh all dependent outputs.
	if verb == "build" and set(("gtfs", "enc", "cudem", "coops")).issubset(_selected(args)):
		landings.build(slice_)
		navdepth.build(slice_)
	return 0


def _validate(args):
	from ferrydata import validate

	return validate.run(slices.get_slice(args.slice))


def main(argv=None) -> int:
	parser = argparse.ArgumentParser(prog="ferrydata", description=__doc__)
	sub = parser.add_subparsers(dest="command", required=True)
	for verb, help_text in (("export-unity", "export merged depth as a deterministic Unity float grid"), ("fetch", "download raw data into the cache (idempotent, checksum-verified)"), ("build", "process from the cache into data/processed/<slice>/"), ("validate", "check the processed outputs and write validation_report.md")):
		p = sub.add_parser(verb, help=help_text)
		scope = p.add_mutually_exclusive_group(required=True)
		scope.add_argument("--slice", help="slice name, e.g. lower-east-river")
		scope.add_argument("--harbor", action="store_true",
			help="the harbor's contacts.json from the tiles' landing files" if verb == "export-unity" else "the 4 km harbor tile grid")
		if verb == "build":
			p.add_argument("--tiles", help="comma-separated harbor tile ids (requires --harbor)")
		if verb not in ("validate", "export-unity"):
			p.add_argument("--only", help="comma-separated sources to run: " + ",".join(SOURCES))
			p.add_argument("--skip", help="comma-separated sources to skip")
		if verb == "fetch":
			p.add_argument("--force", action="store_true", help="re-download and accept changed upstream data (updates the manifest)")
	args = parser.parse_args(argv)
	if getattr(args, "tiles", None) and not getattr(args, "harbor", False):
		parser.error("--tiles requires --harbor")
	if args.command == "export-unity" and args.harbor:
		from ferrydata import export_unity
		return export_unity.run_harbor()
	if getattr(args, "harbor", False):
		if getattr(args, "only", None) or getattr(args, "skip", None) or getattr(args, "force", False):
			parser.error("harbor commands build all v1 sources and never replace pinned downloads")
		kwargs = {"tile_ids": args.tiles.split(",")} if getattr(args, "tiles", None) else {}
		return getattr(tiles, args.command)(**kwargs)
	if args.command == "export-unity":
		from ferrydata import export_unity
		return export_unity.run(slices.get_slice(args.slice))
	if args.command == "validate":
		return _validate(args)
	return _run(args.command, args)


if __name__ == "__main__":
	sys.exit(main())
