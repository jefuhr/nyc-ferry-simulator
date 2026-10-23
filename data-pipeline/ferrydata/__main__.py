"""Entry point: `python -m ferrydata` (cwd data-pipeline) or `python data-pipeline/ferrydata` (any cwd).

The project is not installed as a package, so make sure the directory that holds `ferrydata/` is importable.
"""

import sys
from pathlib import Path

_project_root = str(Path(__file__).resolve().parent.parent)
if _project_root not in sys.path:
	sys.path.insert(0, _project_root)

from ferrydata.cli import main  # noqa: E402

if __name__ == "__main__":
	sys.exit(main())
