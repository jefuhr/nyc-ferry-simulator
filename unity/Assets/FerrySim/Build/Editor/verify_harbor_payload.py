"""Compare preflight inventory, build record, source bytes, copies, and built scenes."""
import hashlib
import json
import pathlib
import sys


def digest(path):
	with path.open('rb') as stream:
		return hashlib.file_digest(stream, 'sha256').hexdigest()


def verify(streaming, root, intended, version, commit, dirty, target, report):
	streaming, root = pathlib.Path(streaming).resolve(), pathlib.Path(root).resolve()
	inventory = json.loads(pathlib.Path(intended).read_text())
	record = json.loads((streaming / 'build-info.json').read_text())
	build = json.loads(pathlib.Path(report).read_text())
	problems = []
	expected = {item['path']: item for item in inventory['files']}
	entries = record.get('payload', [])
	copied = {item['path']: item for item in entries}
	if len(copied) != len(entries):
		problems.append('duplicate paths in build-info.json')
	if copied != expected:
		problems.append(f'preflight vs build-info differs: missing {sorted(expected.keys() - copied.keys())[:5]}, extra {sorted(copied.keys() - expected.keys())[:5]}')
	metadata = {key: value for key, value in inventory.items() if key != 'files'}
	if record.get('harbor') != metadata:
		problems.append('harbor metadata differs from preflight')
	for key, value in (('version', version), ('commit', commit), ('dirty', dirty == '1'), ('target', {'mac': 'StandaloneOSX', 'windows': 'StandaloneWindows64'}[target])):
		if record.get(key) != value:
			problems.append(f'{key}: expected {value}, got {record.get(key)}')
	scenes = ['Assets/FerrySim/Harbor/Scenes/Harbor.unity']
	if record.get('scenes') != scenes or build.get('scenes') != scenes:
		problems.append('build report or payload record does not contain exactly Harbor.unity')
	if any('/World/Generated/' in path or path.endswith('/LowerEastRiverSlice.unity') for path in build.get('packed_assets', [])):
		problems.append('slice scene/generated assets leaked into built dependencies')
	present = {path.relative_to(streaming).as_posix() for path in streaming.rglob('*') if path.is_file()}
	if present != set(expected) | {'build-info.json'}:
		problems.append('actual StreamingAssets inventory differs from preflight')
	for name, item in expected.items():
		for base, label in ((root, 'source'), (streaming, 'copy')):
			path = base / name
			if not path.resolve().is_relative_to(base) or any(parent.is_symlink() for parent in [path, *path.parents] if parent != base):
				problems.append(f'{label} path escapes root or uses a symlink: {name}')
			elif not path.is_file() or path.stat().st_size != item['bytes'] or digest(path) != item['sha256']:
				problems.append(f'{label} does not match preflight: {name}')
	if problems:
		raise ValueError('\n'.join(problems))
	print(f'{target} harbor payload: {inventory["file_count"]} files, {inventory["bytes"]} bytes, 28 environment + traffic days; scene Harbor only; all hashes match')


if __name__ == '__main__':
	try:
		verify(*sys.argv[1:])
	except (OSError, ValueError, KeyError) as error:
		print(f'harbor payload verification failed: {error}', file=sys.stderr)
		sys.exit(1)
