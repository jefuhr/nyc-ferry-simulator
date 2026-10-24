#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""Entry point for the crew intake tool. Standard library only.

Usage: uv run tools/crew_intake/intake.py --help
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from crew_intake.cli import main  # noqa: E402

if __name__ == "__main__":
	sys.exit(main(sys.argv[1:]))
