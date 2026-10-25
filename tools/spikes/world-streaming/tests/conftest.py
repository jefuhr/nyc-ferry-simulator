import sys
from pathlib import Path

# The spike's modules and the pipeline's ferrydata are scripts, not installed packages.
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parents[3] / "data-pipeline"))
