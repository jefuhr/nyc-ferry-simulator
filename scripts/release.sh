#!/usr/bin/env bash
# Builds, packages and smoke-tests a release: a universal macOS app in a disk image and a Windows x64 player in a
# zip, with SHA-256 checksums, in artifacts/release/<version>/ beside the build reports, the logs, the smoke run's
# report and screenshots, and release-manifest.json. See docs/releasing.md.
#
# The only release scene is Harbor.unity. An installed DMG copy runs the offline harbor smoke route.
# Cached harbor outputs must already exist; preflight verifies them before either build, without downloads.
# Usage: scripts/release.sh [--allow-dirty]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
HEAVY_LOCK="$HOME/FerrySim-cache/heavy.lock"
UNITY_LOCK="$HOME/FerrySim-cache/unity.lock"
SETTINGS="unity/ProjectSettings/ProjectSettings.asset"
SMOKE_TIMEOUT_S=300
PAYLOAD_TOOLS="$ROOT/unity/Assets/FerrySim/Build/Editor"

usage() { echo "usage: scripts/release.sh [--allow-dirty]" >&2; exit 2; }
fail() { echo "release: $*" >&2; exit 1; }

ALLOW_DIRTY=0
for option in "$@"; do
	case "$option" in
		--allow-dirty) ALLOW_DIRTY=1 ;;
		*) usage ;;
	esac
done

# BSD flock is not reentrant: do not wrap this script in heavy.lock.
heavy() { lockf -k "$HEAVY_LOCK" "$@"; }

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
	heavy lockf -k "$UNITY_LOCK" "$UNITY_EDITOR" -batchmode -quit -projectPath "$ROOT/unity" -buildTarget "$unity_target" \
		-executeMethod FerrySim.Build.PlayerBuild.BuildFromCommandLine -ferrysimTarget "$target" -ferrysimOutput "$OUT/$target" \
		-ferrysimVersion "$VERSION" -logFile "$OUT/logs/build-$target.log" \
		|| fail "the $target build failed; see $OUT/logs/build-$target.log"
	[[ "$(json_field "$OUT/$target/build-report.json" result 2>/dev/null || true)" == Succeeded ]] \
		|| fail "the $target build did not succeed; see $OUT/$target/build-report.json"
}

# Preflight verifies indices, URI containment, schema/digests and all 28 day dependencies before any build.
check_data() {
	echo "--- Verifying the exact offline harbor inventory"
	heavy lockf -k "$UNITY_LOCK" "$UNITY_EDITOR" -batchmode -nographics -quit -projectPath "$ROOT/unity" \
		-executeMethod FerrySim.Build.HarborPayload.PreflightFromCommandLine -ferrysimInventory "$OUT/payload-inventory.json" \
		-logFile "$OUT/logs/harbor-preflight.log" || fail "harbor preflight failed; see $OUT/logs/harbor-preflight.log"
	[[ -s "$OUT/payload-inventory.json" ]] || fail "harbor preflight wrote no inventory"
}

verify_payload() {
	python3 -I "$PAYLOAD_TOOLS/verify_harbor_payload.py" "$1" "$ROOT" "$OUT/payload-inventory.json" \
		"$VERSION" "$COMMIT" "$DIRTY" "$2" "$OUT/$2/build-report.json"
}

check_download_size() {
	local bytes
	bytes="$(stat -f %z "$1")"
	[[ "$bytes" -le 500000000 ]] || fail "$(basename "$1") is $bytes bytes, above 500 MB; stop and ask before optimising"
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
	(cd "$OUT/windows" && heavy zip -q -r -X "$WIN_ZIP" "$PRODUCT" -x '*_DoNotShip/*' -x '*_ButDontShipItWithYourGame/*')
	check_download_size "$WIN_ZIP"
	local listing="$OUT/logs/windows-zip-listing.txt"
	heavy unzip -t "$WIN_ZIP" > "$OUT/logs/windows-zip-test.txt" || fail "Windows ZIP failed CRC verification"
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
	heavy hdiutil create -volname "$PRODUCT $LABEL" -srcfolder "$staging" -ov -format UDZO "$MAC_DMG" > "$OUT/logs/hdiutil.log" 2>&1 \
		|| fail "the disk image was not made; see $OUT/logs/hdiutil.log"
	rm -rf "$staging"
	check_download_size "$MAC_DMG"
	heavy hdiutil verify "$MAC_DMG" >> "$OUT/logs/hdiutil.log" 2>&1 || fail "the disk image does not verify; see $OUT/logs/hdiutil.log"
}

smoke_mac() {
	echo "--- Offline harbor smoke from the app installed from the DMG"
	local installed
	installed="$(mktemp -d "${TMPDIR:-/tmp}/ferrysim-harbor-smoke.XXXXXX")"
	mkdir -p "$SMOKE/mount" "$SMOKE/result" "$installed/Applications" "$installed/empty-cwd"
	hdiutil attach -nobrowse -readonly -mountpoint "$SMOKE/mount" "$MAC_DMG" >> "$OUT/logs/hdiutil.log" 2>&1 \
		|| fail "the disk image does not mount; see $OUT/logs/hdiutil.log"
	ditto "$SMOKE/mount/$PRODUCT.app" "$installed/Applications/$PRODUCT.app"
	hdiutil detach "$SMOKE/mount" -quiet || hdiutil detach "$SMOKE/mount" -force -quiet
	verify_payload "$installed/Applications/$PRODUCT.app/Contents/Resources/Data/StreamingAssets" mac || fail "the installed DMG copy has an invalid harbor payload"
	local status=0
	# No repository working directory, paths or inherited environment. The extracted app can read only its own payload.
	(cd "$installed/empty-cwd" && heavy lockf -k "$UNITY_LOCK" env -i HOME="$HOME" USER="$USER" PATH=/usr/bin:/bin TMPDIR="${TMPDIR:-/tmp}" \
		perl -e 'alarm shift; exec @ARGV' "$SMOKE_TIMEOUT_S" "$installed/Applications/$PRODUCT.app/Contents/MacOS/$PRODUCT" \
		-ferrysim-smoke "$SMOKE/result" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile "$OUT/logs/smoke-player.log") \
		> "$OUT/logs/smoke-player.stdout.log" 2>&1 || status=$?
	[[ -f "$SMOKE/result/smoke.json" ]] || fail "the smoke run wrote no report (exit $status); see $OUT/logs/smoke-player.log"
	python3 -I - "$SMOKE/result" <<'PYSMOKE'
import json, pathlib, sys
folder = pathlib.Path(sys.argv[1])
r = json.loads((folder / 'smoke.json').read_text())
h = r.get('harbor', {})
assert r.get('passed') is True and not r.get('failures'), r.get('failures')
assert r.get('scene') == 'Harbor' and h.get('tiles_ready') is True
assert r.get('repository_fallback_available') is False
assert h.get('landing_stop_id') == '89' and h.get('traffic_enabled') is True
assert h.get('traffic_roster', 0) > 0 and h.get('traffic_active', 0) > 0
assert r.get('environment_packaged') and r.get('vessel_file_packaged')
assert len(h.get('layers', [])) == 2 and all(x['created_tiles'] > 0 for x in h['layers'])
for name in ('smoke-wheelhouse.png', 'smoke-chase.png'):
	assert (folder / name).stat().st_size > 0, name
print('harbor installed-player smoke: passed, Astoria, real tiles + environment + traffic, both views')
PYSMOKE
	[[ $status -eq 0 ]] || fail "the smoke run failed (exit $status); see $SMOKE/result/smoke.json"
	# Preserve the installed copy for review. Its location and cwd are in smoke.json.
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
	"average_fps", "persistent_data", "environment", "environment_pack", "environment_digest", "environment_packaged", "scene", "harbor", "working_directory", "screenshots")
manifest = {
	"product": product,
	"version": version,
	"label": label,
	"commit": commit,
	"dirty": dirty == "1",
	"created_utc": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
	"artifacts": [artifact(dmg), artifact(archive)],
	"harbor_payload": json.load(open(os.path.join(out, "payload-inventory.json"))),
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
