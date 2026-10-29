"""Offline chart pack commands."""
import argparse
from pathlib import Path
from ferrydata.harbor_chart.build import build
from ferrydata.harbor_chart.pack import verify
from ferrydata.harbor_runtime import pack as runtime_pack

parser=argparse.ArgumentParser(description='Resident harbor chart data; not for navigation')
parser.add_argument('command',choices=('build','verify'))
parser.add_argument('--harbor',type=Path,default=Path('data/processed/harbor'))
parser.add_argument('--out',type=Path)
args=parser.parse_args()
if args.command=='build':
	result=build(args.harbor,args.out)
else:
	runtime=runtime_pack.verify(args.harbor/'runtime')
	result=verify(args.out or args.harbor/'chart',runtime)
	if result['runtime_digest']!=(args.harbor/'runtime/DIGEST').read_text().strip():
		raise ValueError('chart runtime digest mismatch')
print(result['counts'])
