#!/usr/bin/env bash
# Runs every automated check in the repo and prints a summary table.
# Results land under artifacts/ (core TRX, maneuver CSVs and plots, Unity XML and logs).
# Absent components are reported as SKIPPED with the reason, never silently omitted.
# Exit status is non-zero if any step FAILED.
#
# Usage: scripts/check.sh [--only core|cli|data-pipeline|crew-intake|unity]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

ARTIFACTS="$ROOT/artifacts"
UNITY_EDITOR="${UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.3.27f1/Unity.app/Contents/MacOS/Unity}"
UNITY_LOCK="$HOME/FerrySim-cache/unity.lock"
CLI_PROJECT="core/dotnet/FerrySim.Cli"

ONLY=""
case "${1:-}" in
	"") ;;
	--only)
		ONLY="${2:-}"
		case "$ONLY" in
			core | cli | data-pipeline | crew-intake | unity) ;;
			*) echo "usage: scripts/check.sh [--only core|cli|data-pipeline|crew-intake|unity]" >&2; exit 2 ;;
		esac
		;;
	*) echo "usage: scripts/check.sh [--only core|cli|data-pipeline|crew-intake|unity]" >&2; exit 2 ;;
esac

# want <step>: true when no --only filter is set or it names this step.
want() { [[ -z "$ONLY" || "$ONLY" == "$1" ]]; }

STEP_NAMES=()
STEP_STATUS=()
STEP_DETAIL=()

record() {
	STEP_NAMES+=("$1")
	STEP_STATUS+=("$2")
	STEP_DETAIL+=("$3")
}

# run_step <name> <function>: runs the function, records PASSED or FAILED, never aborts the script.
# The function must chain its commands with "|| return 1": errexit is off inside it.
run_step() {
	local name="$1" fn="$2" started=$SECONDS
	echo
	echo "==> $name"
	if "$fn"; then
		record "$name" "PASSED" "$((SECONDS - started)) s"
	else
		record "$name" "FAILED" "$((SECONDS - started)) s, see output above"
	fi
}

skip_step() {
	echo
	echo "==> $1"
	echo "SKIPPED ($2)"
	record "$1" "SKIPPED" "$2"
}

core_tests() {
	rm -rf "$ARTIFACTS/core"
	mkdir -p "$ARTIFACTS/core"
	# LogFilePrefix gives every test project its own file; LogFileName would let the last project overwrite the rest.
	dotnet test core/dotnet --logger "trx;LogFilePrefix=core" --results-directory "$ARTIFACTS/core" || return 1
	ls "$ARTIFACTS"/core/*.trx || return 1
}

cli_smoke() {
	local out="$ARTIFACTS/maneuvers" vessel maneuver base
	rm -rf "$out"
	mkdir -p "$out"
	dotnet build "$CLI_PROJECT" --nologo -v quiet || return 1
	for vessel in t k; do
		for maneuver in turning-circle crash-stop; do
			base="$out/$vessel-$maneuver"
			echo "--- $vessel-class $maneuver"
			dotnet run --no-build --project "$CLI_PROJECT" -- maneuver "$maneuver" --vessel "vessels/$vessel-class.json" --out "$base.csv" || return 1
			dotnet run --no-build --project "$CLI_PROJECT" -- summarize "$base.csv" | tee "$base.txt" || return 1
			dotnet run --no-build --project "$CLI_PROJECT" -- summarize "$base.csv" --json > "$base.json" || return 1
			if ! command -v uv > /dev/null 2>&1; then
				echo "uv is not installed, so $base.png cannot be plotted (brew install uv)"
				return 1
			fi
			uv run tools/plot_maneuver.py "$base.csv" --out "$base.png" || return 1
			[[ -s "$base.csv" && -s "$base.json" && -s "$base.png" ]] || { echo "missing or empty output for $base"; return 1; }
		done
	done
}

# Each Python project gets an explicit path: run from the repo root without one, pytest would also collect
# the other project's tests without that project's import path.
data_pipeline_tests() {
	uv run --project data-pipeline pytest data-pipeline || return 1
}

crew_intake_tests() {
	uv run --project tools/crew_intake pytest tools/crew_intake || return 1
}

# unity_result_ok <xml>: true only if the root <test-run> says result="Passed" and failed="0".
# Unity's process exit code can be 0 even when tests fail, so the XML is the source of truth.
unity_result_ok() {
	local tag
	tag="$(grep -m1 -o '<test-run [^>]*>' "$1" || true)"
	echo "    $tag"
	[[ -n "$tag" && "$tag" == *'result="Passed"'* && "$tag" == *' failed="0"'* ]]
}

unity_tests() {
	local platform lower results log graphics
	mkdir -p "$ARTIFACTS/unity" "$(dirname "$UNITY_LOCK")"
	if [[ ! -x "$UNITY_EDITOR" ]]; then
		echo "Unity editor not found at $UNITY_EDITOR (set UNITY_EDITOR to override)"
		return 1
	fi
	for platform in EditMode PlayMode; do
		lower="$(echo "$platform" | tr '[:upper:]' '[:lower:]')"
		# Unity resolves relative -testResults and -logFile paths against the project directory, so pass absolute ones.
		results="$ARTIFACTS/unity/$lower.xml"
		log="$ARTIFACTS/unity/$lower.log"
		rm -f "$results"
		# EditMode can run headless; PlayMode renders a screenshot and needs graphics.
		graphics=()
		[[ "$platform" == "EditMode" ]] && graphics=(-nographics)
		echo "--- Unity $platform tests (waiting on $UNITY_LOCK if another Unity process holds it)"
		# One Unity process at a time on this machine: lockf serialises every invocation on the shared lock file.
		lockf -k "$UNITY_LOCK" "$UNITY_EDITOR" -batchmode ${graphics[@]+"${graphics[@]}"} -projectPath "$ROOT/unity" \
			-runTests -testPlatform "$platform" -testResults "$results" -logFile "$log" \
			|| { echo "Unity $platform exited non-zero, see $log"; return 1; }
		[[ -s "$results" ]] || { echo "Unity wrote no $platform results, see $log"; return 1; }
		unity_result_ok "$results" || { echo "Unity $platform tests did not pass, see $results"; return 1; }
	done
}

if want core; then
	run_step "core tests (dotnet test core/dotnet)" core_tests
fi
if want cli; then
	run_step "CLI smoke runs and plots" cli_smoke
fi

if want data-pipeline; then
	if [[ -f data-pipeline/pyproject.toml ]]; then
		run_step "data-pipeline tests (pytest)" data_pipeline_tests
	else
		skip_step "data-pipeline tests (pytest)" "data-pipeline/pyproject.toml not found"
	fi
fi

if want crew-intake; then
	if [[ -f tools/crew_intake/pyproject.toml ]]; then
		run_step "crew intake tests (pytest)" crew_intake_tests
	else
		skip_step "crew intake tests (pytest)" "tools/crew_intake/pyproject.toml not found"
	fi
fi

if want unity; then
	if [[ -f unity/ProjectSettings/ProjectVersion.txt ]]; then
		run_step "Unity EditMode and PlayMode tests" unity_tests
	else
		skip_step "Unity EditMode and PlayMode tests" "unity/ProjectSettings/ProjectVersion.txt not found"
	fi
fi

echo
echo "================ summary ================"
failed=0
for i in "${!STEP_NAMES[@]}"; do
	printf '%-8s  %-46s  %s\n' "${STEP_STATUS[$i]}" "${STEP_NAMES[$i]}" "${STEP_DETAIL[$i]}"
	[[ "${STEP_STATUS[$i]}" == "FAILED" ]] && failed=1
done
echo "========================================="
if [[ "$failed" -ne 0 ]]; then
	echo "RESULT: FAILED"
	exit 1
fi
echo "RESULT: OK"
