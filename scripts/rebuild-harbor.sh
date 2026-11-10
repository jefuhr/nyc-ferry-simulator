#!/usr/bin/env bash
# Offline harbor rebuild. Never enclose this script in heavy.lock: tiles lock per worker.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
OUT="$ROOT/data/processed/harbor/runtime"
case "${1:-}" in
	"") ;;
	--out) [[ $# == 2 ]] || { echo 'usage: scripts/rebuild-harbor.sh [--out runtime-directory]' >&2; exit 2; }; OUT="$2" ;;
	*) echo 'usage: scripts/rebuild-harbor.sh [--out runtime-directory]' >&2; exit 2 ;;
esac
if [[ -n "${FERRYDATA_PYTHON:-}" ]]; then
	PYTHON=("$FERRYDATA_PYTHON")
else
	# Use the existing locked environment without installing or fetching anything.
	PYTHON=(uv run --cache-dir "$ROOT/.cache/uv" --no-sync --project "$ROOT/data-pipeline" python)
fi
export PYTHONPATH="$ROOT/data-pipeline${PYTHONPATH:+:$PYTHONPATH}"
export PYTHONDONTWRITEBYTECODE=1
HARBOR="$ROOT/data/processed/harbor"
LOCK="$HOME/FerrySim-cache/heavy.lock"
"${PYTHON[@]}" -m ferrydata build --harbor
"${PYTHON[@]}" -m ferrydata export-unity --harbor
lockf -k "$LOCK" "${PYTHON[@]}" -m ferrydata.world3d refresh --harbor "$HARBOR"
lockf -k "$LOCK" "${PYTHON[@]}" -m ferrydata.harbor_runtime build --harbor "$HARBOR" --out "$OUT"
"${PYTHON[@]}" -m ferrydata.harbor_runtime verify --harbor "$HARBOR" --out "$OUT"
printf 'DIGEST %s\n' "$(cat "$OUT/DIGEST")"
