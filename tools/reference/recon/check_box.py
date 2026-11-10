"""Run the numeric acceptance verdict against retained synthetic reconstruction metrics."""
import json
from pathlib import Path
import sys

metrics=json.loads(Path(sys.argv[1]).read_text())
print(json.dumps(metrics,sort_keys=True,indent='\t'))
failed=[f'{name}: {error:+.6f}%' for name,error in zip(('length','width','height'),metrics['error_percent']) if abs(error)>=2]
if failed:
	print('FAIL: '+', '.join(failed)); raise SystemExit(1)
print('PASS: independent reconstructed edges are within 2% after one-edge scaling')
