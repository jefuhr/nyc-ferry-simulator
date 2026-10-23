"""Where raw downloads, processed outputs and manifests live."""

import os
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MANIFEST_DIR = REPO_ROOT / "data" / "manifests"
PROCESSED_ROOT = REPO_ROOT / "data" / "processed"


def cache_root() -> Path:
	"""Raw downloads live outside the repo. FERRYSIM_CACHE overrides the default (used by tests)."""
	base = os.environ.get("FERRYSIM_CACHE")
	root = Path(base) if base else Path.home() / "FerrySim-cache"
	return root / "data" / "raw"


def raw_dir(source: str) -> Path:
	return cache_root() / source


def processed_dir(slice_name: str) -> Path:
	return PROCESSED_ROOT / slice_name


def ais_processed_dir() -> Path:
	return PROCESSED_ROOT / "ais"
