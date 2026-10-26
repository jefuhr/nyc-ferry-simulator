#!/usr/bin/env bash
# Builds, packages and smoke-tests a release: a universal macOS app in a disk image and a Windows x64 player in a
# zip, with SHA-256 checksums, in artifacts/release/<version>/ beside the build reports, the logs, the smoke run's
# report and screenshots, and release-manifest.json. See docs/releasing.md.
#
# The Mac app is tested as a user gets it: copied out of the mounted disk image and launched with -ferrysim-smoke
# (FerrySim.Slice.PlayerSmokeRun), which drives the slice and fails on any error; a window opens for about a minute.
# The Windows player cannot run here: its package is checked for the executable, Unity's player, the Cesium native
# plugin and the packaged data, and scripts/record-windows-smoke.sh records its smoke run on a PC afterwards.
#
# The players package the slice's processed data (docs/releasing.md, Before a release), so the environment packs must
# verify before anything is built, and every pack file must reach each player. Traffic packs must not: players refuse
# traffic for now.
#
# Usage: scripts/release.sh [--allow-dirty]
#   --allow-dirty  build from uncommitted changes; everything is labelled <version>-dirty and is not for publishing
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
UNITY_LOCK="$HOME/FerrySim-cache/unity.lock"
SETTINGS="unity/ProjectSettings/ProjectSettings.asset"
SMOKE_TIMEOUT_S=300
REGION="lower-east-river"

usage() { echo "usage: scripts/release.sh [--allow-dirty]" >&2; exit 2; }
fail() { echo "release: $*" >&2; exit 1; }

ALLOW_DIRTY=0
case "${1:-}" in
	"") ;;
	--allow-dirty) ALLOW_DIRTY=1 ;;
	*) usage ;;
esac
[[ $# -le 1 ]] || usage

# json_field <file> <key>: a top-level value from a JSON file.
json_field() { python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$1" "$2"; }

[[ -x "$UNITY_EDITOR" ]] || fail "no Unity editor at $UNITY_EDITOR (set UNITY_EDITOR to override)"
command -v uv > /dev/null || fail "uv is not on PATH; the environment packs are verified with the data pipeline's uv environment"
[[ -d "$(dirname "$UNITY_EDITOR")/../../../PlaybackEngines/WindowsStandaloneSupport" ]] \
	|| fail "Unity has no Windows Build Support (Mono) module; docs/releasing.md says how to add it"

VERSION="$(sed -n 's/^  bundleVersion: //p' "$SETTINGS")"
PRODUCT="$(sed -n 's/^  productName: //p' "$SETTINGS")"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "bundleVersion '$VERSION' in $SETTINGS is not major.minor.patch"
[[ -n "$PRODUCT" ]] || fail "no productName in $SETTINGS"
COMMIT="$(git rev-parse HEAD)"
DIRTY=0
if [[ -n "$(git status --porcelain)" ]]; then
	DIRTY=1
	if [[ $ALLOW_DIRTY -eq 0 ]]; then
		git status --short >&2
		fail "the working tree has uncommitted changes; commit them, or pass --allow-dirty for a build that is not for publishing"
	fi
fi

LABEL="$VERSION"
[[ $DIRTY -eq 0 ]] || LABEL="$VERSION-dirty"
SLUG="${PRODUCT// /-}"
OUT="$ROOT/artifacts/release/$LABEL"
if [[ -e "$OUT" ]]; then
	# Keep an earlier run's evidence rather than overwrite it.
	mv "$OUT" "$OUT.$(date -u +%Y%m%dT%H%M%SZ)"
fi
mkdir -p "$OUT/logs" "$(dirname "$UNITY_LOCK")"
MAC_APP="$OUT/mac/$PRODUCT.app"
WIN_DIR="$OUT/windows/$PRODUCT"
MAC_DMG="$OUT/$SLUG-$LABEL-mac.dmg"
WIN_ZIP="$OUT/$SLUG-$LABEL-windows-x64.zip"
SMOKE="$OUT/smoke"

# build <mac|windows> <Unity -buildTarget>: one player, under the shared Unity lock (one Unity process at a time).
build() {
	local target="$1" unity_target="$2"
	echo "--- Building the $target player (log: $OUT/logs/build-$target.log)"
	lockf -k "$UNITY_LOCK" "$UNITY_EDITOR" -batchmode -quit -projectPath "$ROOT/unity" -buildTarget "$unity_target" \
		-executeMethod FerrySim.Build.PlayerBuild.BuildFromCommandLine -ferrysimTarget "$target" -ferrysimOutput "$OUT/$target" \
		-ferrysimVersion "$VERSION" -logFile "$OUT/logs/build-$target.log" \
		|| fail "the $target build failed; see $OUT/logs/build-$target.log"
	[[ "$(json_field "$OUT/$target/build-report.json" result 2>/dev/null || true)" == Succeeded ]] \
		|| fail "the $target build did not succeed; see $OUT/$target/build-report.json"
}

# check_data: the processed data the players package is there, and the environment packs verify (their digests).
check_data() {
	echo "--- Checking the processed data (log: $OUT/logs/environment-verify.log)"
	[[ -f "data/processed/$REGION/geoid_offset.json" ]] \
		|| fail "data/processed/$REGION/geoid_offset.json is missing; build the slice first (docs/releasing.md, Before a release)"
	[[ -d "data/processed/$REGION/env/days" ]] \
		|| fail "data/processed/$REGION/env has no packs; build them first (docs/releasing.md, Before a release)"
	PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment verify --region "$REGION" \
		> "$OUT/logs/environment-verify.log" 2>&1 \
		|| fail "the environment packs do not verify; see $OUT/logs/environment-verify.log"
	tail -1 "$OUT/logs/environment-verify.log"
}

# verify_payload <StreamingAssets folder> <target>: the build record names this version, commit and tree state; both
# vessel files, the slice's geoid offset and every file of its environment packs are packaged, and no traffic is; every
# packaged file is there with the recorded hash and the repository's bytes; and StreamingAssets holds nothing else.
verify_payload() {
	python3 -I - "$1" "$ROOT" "$VERSION" "$COMMIT" "$DIRTY" "$2" "$REGION" <<'PY'
import hashlib, json, os, sys
streaming, root, version, commit, dirty, target, region = sys.argv[1:8]
record = json.load(open(os.path.join(streaming, "build-info.json")))
problems = []
if record.get("version") != version:
	problems.append(f"version {record.get('version')} is not {version}")
if record.get("commit") != commit:
	problems.append(f"commit {record.get('commit')} is not {commit}")
if bool(record.get("dirty")) != (dirty == "1"):
	problems.append(f"the build saw dirty={record.get('dirty')}, the script dirty={dirty == '1'}")
paths = [entry["path"] for entry in record.get("payload", [])]
for required in ("vessels/t-class.json", "vessels/k-class.json", f"data/processed/{region}/geoid_offset.json"):
	if required not in paths:
		problems.append(f"{required} is not packaged")
# Every file of the environment packs, as FerrySim.Build.PlayerPayload walks a folder: no part of the path may start with a dot.
env = f"data/processed/{region}/env"
expected = set()
for folder, _, files in os.walk(os.path.join(root, env)):
	for name in files:
		relative = os.path.relpath(os.path.join(folder, name), root).replace(os.sep, "/")
		if not any(part.startswith(".") for part in relative.split("/")):
			expected.add(relative)
packaged_env = {path for path in paths if path.startswith(env + "/")}
if not expected:
	problems.append(f"{env} has no files")
for label, names in (("not packaged", sorted(expected - packaged_env)), ("packaged but not in the repository", sorted(packaged_env - expected))):
	if names:
		problems.append(f"{len(names)} environment pack files are {label}, the first {names[0]}")
# What the player really carries, not only what the build recorded: anything else in StreamingAssets is unvetted data.
present = set()
for folder, _, files in os.walk(streaming):
	for name in files:
		present.add(os.path.relpath(os.path.join(folder, name), streaming).replace(os.sep, "/"))
unrecorded = sorted(path for path in present - set(paths) - {"build-info.json"} if not any(part.startswith(".") for part in path.split("/")))
if unrecorded:
	problems.append(f"{len(unrecorded)} files in StreamingAssets are not in the build record, the first {unrecorded[0]}")
traffic = sorted(path for path in present | set(paths) if "traffic" in path.split("/"))
if traffic:
	problems.append(f"{len(traffic)} traffic files are in the player, the first {traffic[0]}; players refuse traffic for now")
real_root = os.path.realpath(root)
for entry in record.get("payload", []):
	packaged = os.path.join(streaming, entry["path"])
	# The repository's copy must really be inside the repository: no symbolic link out of it on the way.
	real_source = os.path.realpath(os.path.join(root, entry["path"]))
	if os.path.commonpath([real_source, real_root]) != real_root:
		problems.append(f"{entry['path']} resolves outside the repository, to {real_source}")
		continue
	if os.path.islink(packaged) or not os.path.isfile(packaged):
		problems.append(f"{entry['path']} is missing from the player, or is a link")
		continue
	data = open(packaged, "rb").read()
	if hashlib.sha256(data).hexdigest() != entry["sha256"] or len(data) != entry["bytes"]:
		problems.append(f"{entry['path']} does not match its recorded hash and size")
	if data != open(os.path.join(root, entry["path"]), "rb").read():
		problems.append(f"{entry['path']} differs from the repository's copy")
if problems:
	print(f"{target} payload problems:\n  " + "\n  ".join(problems))
	sys.exit(1)
size = sum(entry["bytes"] for entry in record.get("payload", []))
others = ", ".join(path for path in paths if not path.startswith(env + "/"))
print(f"{target} payload: {len(paths)} files, {size / 1e6:.1f} MB: {len(packaged_env)} environment pack files, {others}; "
	f"build {record['version']} ({record['commit'][:7]}{'-dirty' if record.get('dirty') else ''})")
PY
}

package_windows() {
	echo "--- Packaging the Windows player"
	# No `producer | grep -q` here: grep -q exits at its first match, the producer dies of SIGPIPE, and pipefail
	# turns that into a false failure. Checks read whole values or files instead.
	local exe="$WIN_DIR/$PRODUCT.exe" kind
	[[ -f "$exe" ]] || fail "no executable at $exe"
	kind="$(file -b "$exe")"
	[[ "$kind" == *"PE32+ executable"*"x86-64"* ]] || fail "$exe is not an x64 Windows executable: $kind"
	[[ -f "$WIN_DIR/UnityPlayer.dll" ]] || fail "UnityPlayer.dll is missing from $WIN_DIR"
	local cesium
	cesium="$(find "$WIN_DIR/${PRODUCT}_Data/Plugins" -name CesiumForUnityNative.dll 2>/dev/null | head -1 || true)"
	[[ -n "$cesium" ]] || fail "the Cesium native plugin is missing from ${PRODUCT}_Data/Plugins"
	kind="$(file -b "$cesium")"
	[[ "$kind" == *x86-64* ]] || fail "the Cesium native plugin is not x64: $kind"
	verify_payload "$WIN_DIR/${PRODUCT}_Data/StreamingAssets" windows || fail "the Windows player's data is wrong"
	(cd "$OUT/windows" && zip -q -r -X "$WIN_ZIP" "$PRODUCT" -x '*_DoNotShip/*' -x '*_ButDontShipItWithYourGame/*')
	local listing="$OUT/logs/windows-zip-listing.txt"
	unzip -l "$WIN_ZIP" > "$listing" || fail "$WIN_ZIP does not list"
	grep -q -F "$PRODUCT/$PRODUCT.exe" "$listing" || fail "$WIN_ZIP does not hold $PRODUCT/$PRODUCT.exe"
	! grep -q -i 'DoNotShip' "$listing" || fail "$WIN_ZIP holds Burst debug information"
}

package_mac() {
	echo "--- Packaging the Mac app"
	[[ -d "$MAC_APP" ]] || fail "no app at $MAC_APP"
	local archs
	archs="$(lipo -archs "$MAC_APP/Contents/MacOS/$PRODUCT" 2>&1 || true)"
	[[ "$archs" == *x86_64* && "$archs" == *arm64* ]] || fail "the app is not universal: $archs"
	local cesium="$MAC_APP/Contents/PlugIns/libCesiumForUnityNative.dylib"
	[[ -f "$cesium" ]] || fail "the Cesium native plugin is missing from Contents/PlugIns"
	archs="$(lipo -archs "$cesium" 2>&1 || true)"
	[[ "$archs" == *x86_64* && "$archs" == *arm64* ]] || fail "the Cesium native plugin is not universal: $archs"
	verify_payload "$MAC_APP/Contents/Resources/Data/StreamingAssets" mac || fail "the Mac app's data is wrong"
	# The data goes in after Unity has written the bundle, so seal it again: ad hoc, as there is no Developer ID.
	codesign --force --deep --sign - "$MAC_APP" > "$OUT/logs/codesign.log" 2>&1 || fail "ad hoc signing failed; see $OUT/logs/codesign.log"
	codesign --verify --deep --strict --verbose=2 "$MAC_APP" >> "$OUT/logs/codesign.log" 2>&1 || fail "the signed app does not verify; see $OUT/logs/codesign.log"
	# Gatekeeper rejects an app that is not notarized; recorded, not a failure (docs/releasing.md, Installing).
	spctl --assess --type execute --verbose=2 "$MAC_APP" >> "$OUT/logs/codesign.log" 2>&1 || true
	local staging="$OUT/dmg-staging"
	rm -rf "$staging"
	mkdir -p "$staging"
	ditto "$MAC_APP" "$staging/$PRODUCT.app"
	ln -s /Applications "$staging/Applications"
	hdiutil create -volname "$PRODUCT $LABEL" -srcfolder "$staging" -ov -format UDZO "$MAC_DMG" > "$OUT/logs/hdiutil.log" 2>&1 \
		|| fail "the disk image was not made; see $OUT/logs/hdiutil.log"
	rm -rf "$staging"
	hdiutil verify "$MAC_DMG" >> "$OUT/logs/hdiutil.log" 2>&1 || fail "the disk image does not verify; see $OUT/logs/hdiutil.log"
}

smoke_mac() {
	echo "--- Smoke run of the app installed from the disk image (a window opens for about a minute)"
	mkdir -p "$SMOKE/mount" "$SMOKE/Applications" "$SMOKE/result"
	hdiutil attach -nobrowse -readonly -mountpoint "$SMOKE/mount" "$MAC_DMG" >> "$OUT/logs/hdiutil.log" 2>&1 \
		|| fail "the disk image does not mount; see $OUT/logs/hdiutil.log"
	ditto "$SMOKE/mount/$PRODUCT.app" "$SMOKE/Applications/$PRODUCT.app"
	hdiutil detach "$SMOKE/mount" -quiet || hdiutil detach "$SMOKE/mount" -force -quiet
	local status=0
	# perl's alarm is the timeout: a hung player is killed after SMOKE_TIMEOUT_S seconds.
	lockf -k "$UNITY_LOCK" perl -e 'alarm shift; exec @ARGV' "$SMOKE_TIMEOUT_S" "$SMOKE/Applications/$PRODUCT.app/Contents/MacOS/$PRODUCT" \
		-ferrysim-smoke "$SMOKE/result" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile "$OUT/logs/smoke-player.log" \
		> "$OUT/logs/smoke-player.stdout.log" 2>&1 || status=$?
	[[ -f "$SMOKE/result/smoke.json" ]] || fail "the smoke run wrote no report (exit $status); see $OUT/logs/smoke-player.log"
	python3 -I -c 'import json, sys; r = json.load(open(sys.argv[1])); print("smoke run:", "passed" if r["passed"] else "FAILED", "; ".join(r["failures"]))' "$SMOKE/result/smoke.json"
	[[ $status -eq 0 ]] || fail "the smoke run failed (exit $status); see $SMOKE/result/smoke.json and $OUT/logs/smoke-player.log"
}

write_manifest() {
	(cd "$OUT" && shasum -a 256 "$(basename "$MAC_DMG")" "$(basename "$WIN_ZIP")" > SHA256SUMS.txt)
	python3 -I - "$OUT" "$PRODUCT" "$VERSION" "$LABEL" "$COMMIT" "$DIRTY" "$MAC_DMG" "$WIN_ZIP" <<'PY'
import datetime, hashlib, json, os, sys
out, product, version, label, commit, dirty, dmg, archive = sys.argv[1:9]
def artifact(path):
	data = open(path, "rb").read()
	return {"file": os.path.basename(path), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
smoke = json.load(open(os.path.join(out, "smoke", "result", "smoke.json")))
keys = ("passed", "failures", "build", "operating_system", "graphics_device", "drive_simulated_s", "displacement_m",
	"max_speed_over_ground_mps", "landing_segments", "recording_bytes", "min_screenshot_colours", "errors", "warnings",
	"average_fps", "persistent_data", "environment", "environment_pack", "environment_digest", "environment_packaged")
manifest = {
	"product": product,
	"version": version,
	"label": label,
	"commit": commit,
	"dirty": dirty == "1",
	"created_utc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
	"artifacts": [artifact(dmg), artifact(archive)],
	"mac_smoke_run": {key: smoke.get(key) for key in keys},
	"windows_run": "not run yet: built and inspected on macOS; scripts/record-windows-smoke.sh records a run on a PC",
}
with open(os.path.join(out, "release-manifest.json"), "w") as handle:
	json.dump(manifest, handle, indent=2)
	handle.write("\n")
PY
}

echo "=== $PRODUCT $LABEL from $COMMIT into $OUT"
check_data
build windows Win64
# The Mac build goes last so the project is left on the target the editor and the tests use.
build mac OSXUniversal
if [[ $DIRTY -eq 0 && -n "$(git status --porcelain)" ]]; then
	git status --short >&2
	fail "the builds changed the working tree; a release has to be the commit as it is"
fi
package_windows
package_mac
smoke_mac
write_manifest
echo "=== $PRODUCT $LABEL ($COMMIT) in $OUT"
cat "$OUT/SHA256SUMS.txt"
