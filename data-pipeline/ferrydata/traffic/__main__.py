"""Build one source day per locked subprocess so peak RSS is measured independently."""

import argparse
import json
import os
from pathlib import Path
import resource
import subprocess
import sys
import time

from ferrydata import ais, paths
from ferrydata.manifest import Manifest, sha256_file
from ferrydata.traffic import build_pack, output_root, scenario_input, verify_pack


def main():
	parser = argparse.ArgumentParser(description="Build or verify deterministic ferrysim-traffic/1 packs from cached AIS")
	sub = parser.add_subparsers(dest="command", required=True)
	build = sub.add_parser("build")
	selection = build.add_mutually_exclusive_group(required=True)
	selection.add_argument("--all", action="store_true")
	selection.add_argument("--days", nargs="+")
	build.add_argument("--output", type=Path, default=output_root())
	build.add_argument("--metrics", type=Path, default=paths.REPO_ROOT / "artifacts/traffic-pack/metrics.json")
	verify = sub.add_parser("verify")
	verify.add_argument("--output", type=Path, default=output_root())
	verify.add_argument("--days", nargs="+")
	day_parser = sub.add_parser("_day", help=argparse.SUPPRESS)
	day_parser.add_argument("day")
	day_parser.add_argument("--output", type=Path, required=True)
	day_parser.add_argument("--metrics", type=Path, required=True)
	report = sub.add_parser("report")
	report.add_argument("--output", type=Path, default=output_root())
	report.add_argument("--artifacts", type=Path, default=paths.REPO_ROOT / "artifacts/traffic-pack")
	args = parser.parse_args()
	if args.command == "build":
		days = ais.all_days() if args.all else args.days
		if any(day not in ais.all_days() for day in days):
			parser.error("days must belong to the 28 scenario days")
		lock = Path.home() / "FerrySim-cache/heavy.lock"
		lock.parent.mkdir(parents=True, exist_ok=True)
		for day in sorted(set(days)):
			subprocess.run(["lockf", "-k", str(lock), sys.executable, "-m", "ferrydata.traffic", "_day", day,
				"--output", str(args.output.resolve()), "--metrics", str(args.metrics.resolve())], check=True)
	elif args.command == "_day":
		start = time.monotonic()
		frame, drops, rows, sources, coverage = scenario_input(args.day, args.output / "_sources")
		pack = build_pack(frame, args.day, args.output / args.day, source=sources[0], sources=sources,
			coverage=coverage, initial_drops=drops, source_rows=rows)
		verify_pack(args.output / args.day)
		metrics = json.loads(args.metrics.read_text()) if args.metrics.exists() else {}
		rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
		metrics[args.day] = {"elapsed_s": time.monotonic() - start, "peak_rss_bytes": rss if sys.platform == "darwin" else rss * 1024}
		args.metrics.parent.mkdir(parents=True, exist_ok=True)
		args.metrics.write_text(json.dumps(metrics, indent="\t") + "\n")
		print(f"{args.day}: {pack['counts']['vessels']} vessels, {pack['counts']['reports']} reports, {metrics[args.day]['elapsed_s']:.2f} s, peak RSS {metrics[args.day]['peak_rss_bytes'] / 2**20:.1f} MiB", flush=True)
	elif args.command == "verify":
		days = args.days or ais.all_days()
		for day in days:
			verify_pack(args.output / day)
		print(f"verified {len(days)} traffic packs")
	elif args.command == "report":
		from ferrydata.traffic.report import write_report
		write_report(args.output, args.artifacts)


if __name__ == "__main__":
	main()
