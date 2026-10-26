#!/usr/bin/env bash
# Records the Windows player's smoke run on a PC for a release that scripts/release.sh built (docs/releasing.md,
# Windows smoke run): checks that the run is from this release's Windows build, copies its report, screenshots and player
# log into artifacts/release/<label>/windows-smoke/, and writes the run's summary into release-manifest.json as
# windows_run. A failed run is recorded too, and exits 1.
#
# Usage: scripts/record-windows-smoke.sh <label> <folder>
#   <label>   the release's folder name under artifacts/release, for example 0.2.0
#   <folder>  the smoke run's output copied from the PC: smoke.json and its screenshots, and the player's log if kept
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() { echo "usage: scripts/record-windows-smoke.sh <label> <folder>" >&2; exit 2; }
fail() { echo "record-windows-smoke: $*" >&2; exit 1; }

[[ $# -eq 2 ]] || usage
OUT="$ROOT/artifacts/release/$1"
SOURCE="$2"
[[ -f "$OUT/release-manifest.json" ]] || fail "no release-manifest.json in $OUT; build the release with scripts/release.sh first"
[[ -f "$SOURCE/smoke.json" ]] || fail "no smoke.json in $SOURCE"

python3 -I - "$OUT" "$SOURCE" <<'PY'
import datetime, glob, json, os, shutil, sys
out, source = sys.argv[1:3]
manifest_path = os.path.join(out, "release-manifest.json")
manifest = json.load(open(manifest_path))
smoke = json.load(open(os.path.join(source, "smoke.json")))
expected = f"{manifest['version']} ({manifest['commit'][:7]}{'-dirty' if manifest['dirty'] else ''})"
problems = []
if smoke.get("platform") != "WindowsPlayer":
	problems.append(f"the run is from {smoke.get('platform')}, not the Windows player")
if smoke.get("build") != expected:
	problems.append(f"the run's build is {smoke.get('build')}, not this release's {expected}")
if problems:
	print("record-windows-smoke: not recorded:\n  " + "\n  ".join(problems), file=sys.stderr)
	sys.exit(1)

destination = os.path.join(out, "windows-smoke")
real_source, real_destination = os.path.realpath(source), os.path.realpath(destination)
if os.path.commonpath([real_source, real_destination]) == real_destination:
	print(f"record-windows-smoke: not recorded: {source} is this release's own windows-smoke folder; pass the folder copied from the PC", file=sys.stderr)
	sys.exit(1)
if os.path.exists(destination):
	# Keep an earlier recording's evidence rather than overwrite it.
	os.rename(destination, destination + "." + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ"))
os.makedirs(destination)
for pattern in ("smoke.json", "*.png", "*.log"):
	for path in sorted(glob.glob(os.path.join(source, pattern))):
		if os.path.isfile(path):
			shutil.copy2(path, destination)

keys = ("passed", "failures", "build", "operating_system", "processor", "graphics_device", "drive_simulated_s", "displacement_m",
	"max_speed_over_ground_mps", "landing_segments", "recording_bytes", "min_screenshot_colours", "errors", "warnings",
	"average_fps", "environment", "environment_pack", "environment_digest", "environment_packaged")
run = {key: smoke.get(key) for key in keys}
run["recorded_utc"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
run["evidence"] = "windows-smoke/"
manifest["windows_run"] = run
with open(manifest_path, "w") as handle:
	json.dump(manifest, handle, indent=2)
	handle.write("\n")
print("Windows smoke run:", "passed" if smoke.get("passed") else "FAILED", "; ".join(smoke.get("failures") or []),
	f"({smoke.get('operating_system')}, {smoke.get('graphics_device')}, {smoke.get('average_fps')} fps)")
sys.exit(0 if smoke.get("passed") else 1)
PY
