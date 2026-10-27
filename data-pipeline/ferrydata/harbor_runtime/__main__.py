"""Build and verify the harbor's deterministic resident runtime data (offline)."""
import argparse
import json
import sys
from pathlib import Path

from ferrydata import paths
from ferrydata.harbor_runtime import build, verify


def main(argv=None):
	parser=argparse.ArgumentParser(prog='python -m ferrydata.harbor_runtime',description=__doc__)
	commands=parser.add_subparsers(dest='command',required=True)
	for verb in ('build','verify'):
		p=commands.add_parser(verb)
		p.add_argument('--harbor',type=Path,help='existing harbor root; verify also checks source hashes when provided')
		p.add_argument('--out',type=Path,help='runtime directory (default: harbor/runtime)')
	args=parser.parse_args(argv)
	harbor=args.harbor or paths.PROCESSED_ROOT/'harbor'
	out=args.out or harbor/'runtime'
	try:
		result=build(harbor,out) if args.command=='build' else verify(out,args.harbor)
	except (ValueError,OSError,KeyError,TypeError) as error:
		print(f'{args.command} failed: {error}',file=sys.stderr)
		return 1
	print(json.dumps(dict(command=args.command,passed=True,format=result['format'],counts=result['counts']),sort_keys=True))
	return 0


if __name__=='__main__':
	sys.exit(main())
