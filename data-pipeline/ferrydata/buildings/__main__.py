"""python -m ferrydata.buildings fetch|build|validate"""
import argparse
import json
from pathlib import Path
from ferrydata import paths
from ferrydata.buildings import sources,selection
from ferrydata.buildings.build import build,default_output
from ferrydata.buildings.validate import validate


def main():
	parser=argparse.ArgumentParser(description=__doc__)
	parser.add_argument('command',choices=('fetch','build','validate'))
	parser.add_argument('--harbor',type=Path,default=paths.processed_dir('harbor'))
	parser.add_argument('--cache',type=Path,default=sources.cache_default())
	parser.add_argument('--out',type=Path,default=default_output())
	parser.add_argument('--compare',type=Path,help='Validate byte equality against a separately built output directory')
	parser.add_argument('--policy',choices=('A','B','C'),default=selection.DEFAULT_POLICY,help='Explicit water-view selection, approved default B is 600 m shoreline plus 25 m inland height')
	parser.add_argument('--force',action='store_true',help='Explicitly accept changed upstream responses on fetch')
	parser.add_argument('--report',type=Path,help='Write build metrics or validation JSON')
	args=parser.parse_args()
	if args.command=='fetch':
		result=sources.fetch(args.harbor,args.cache,args.force)
	elif args.command=='build':
		result=build(args.harbor,args.cache,args.out,policy=args.policy)
	else:
		result=validate(args.harbor,args.cache,args.out,repeat_dir=args.compare)
	text=json.dumps(result,indent='\t',allow_nan=False)
	if args.report:
		args.report.parent.mkdir(parents=True,exist_ok=True)
		args.report.write_text(text+'\n')
	print(text)
	if args.command=='fetch' and not result['coverage']['complete']:
		return 2
	return 0 if args.command!='validate' or result['passed'] else 1


if __name__=='__main__':
	raise SystemExit(main())
