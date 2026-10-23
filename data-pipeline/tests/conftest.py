import sys
from pathlib import Path

# ferrydata is not installed (package = false), so make the project root importable from any cwd.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
