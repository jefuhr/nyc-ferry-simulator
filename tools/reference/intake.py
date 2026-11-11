"""Consent and withdrawal boundary. Private manifests never belong in Git."""
from __future__ import annotations

import copy
import os
from pathlib import Path
import re

import reference as ref

MANIFEST_SCHEMA = ref.REPO / 'docs/contracts/reference-manifest.schema.json'
REQUEST_ID = re.compile(r'^(EXT|WH|SCR|MOT|LND|SND|HND)-[0-9]{2}(?:@[a-z0-9][a-z0-9-]*)?$')
STATUSES = ('open', 'received', 'screened', 'used', 'needs-reshoot')


def load_manifest(path):
	path = Path(path).expanduser()
	if not path.is_file():
		raise ValueError('manifest.json is required beside the sheets; no sheet is read without it')
	data = ref.load_json(path)
	errors = ref.schema_errors(data, ref.load_json(MANIFEST_SCHEMA))
	if errors:
		raise ValueError('Invalid manifest: ' + '; '.join(errors))
	for collection in (data['sheets'], data.get('derived', [])):
		paths = set()
		for entry in collection:
			name = entry['path']
			if name in paths:
				raise ValueError(f'Duplicate manifest path: {name}')
			paths.add(name)
			if collection is data['sheets']:
				ref.relative_path(name)
			if not re.fullmatch('[0-9a-f]{64}', entry['sha256']):
				raise ValueError(f'{name}: invalid sha256')
	for derived in data.get('derived', []):
		sheet = next((s for s in data['sheets'] if s['path'] == derived['path']), None)
		if sheet is not None:
			if sheet['sha256'] != derived['sha256']:
				raise ValueError(f"{sheet['path']}: sheets and derived hash must match")
			if set(derived['from']) != {source['source_id'] for source in sheet['sources']}:
				raise ValueError(f"{sheet['path']}: sheets and derived source ids must match")
	for sheet in data['sheets']:
		for source in sheet['sources']:
			if not re.fullmatch('[0-9a-f]{64}', source['sha256']):
				raise ValueError(f"{sheet['path']}: invalid source sha256")
		for request in sheet.get('tags', {}).get('requests', []):
			if not REQUEST_ID.fullmatch(request):
				raise ValueError('Invalid request id in manifest')
	return data


def source_issues(data, source_id):
	uses = [(sheet, source) for sheet in data['sheets'] for source in sheet['sources'] if source['source_id'] == source_id]
	if not uses:
		return ['missing source ' + source_id]
	issues = []
	for sheet, source in uses:
		if source['withdrawn']:
			issues.append('withdrawn source ' + source_id)
		consent = source['consent']
		if consent['scope'] == 'processing' and consent['version'] != data['consent_policy']:
			issues.append('consent version is not current for ' + source_id)
	if not any(sheet['screening']['decision'] in ('approved','approved-with-edits') for sheet,_ in uses):
		issues.append('screening decision is excluded for ' + source_id)
	return sorted(set(issues))


def entry_issues(data, name):
	entry = next((s for s in data['sheets'] + data.get('derived', []) if s['path'] == name), None)
	if entry is None:
		return ['missing manifest entry']
	ids = entry.get('from', [s['source_id'] for s in entry.get('sources', [])])
	issues = [issue for source_id in ids for issue in source_issues(data, source_id)]
	if 'screening' in entry and entry['screening']['decision'] not in ('approved','approved-with-edits'):
		issues.append('screening decision is excluded')
	return sorted(set(issues))


def gate(root, name, manifest=None, reproduce=True):
	data = load_manifest(manifest or Path(root)/'manifest.json')
	if not any(s['path'] == name for s in data['sheets']):
		raise ValueError(f'{name}: missing manifest entry under approved sheets; a derived entry alone is not an approved sheets entry')
	issues = entry_issues(data, name)
	if issues:
		raise ValueError(f"{name}: {'; '.join(issues)}")
	entry = next(s for s in data['sheets'] + data.get('derived', []) if s['path'] == name)
	path = ref.sheet_path(root, name)
	if ref.sha256(path) != entry['sha256']:
		raise ValueError(f'{name}: manifest hash mismatch')
	measurement_only = entry.get('allowed_use') == 'measurement-only' or any(s['consent']['scope'] == 'measurement-only' for s in entry.get('sources', []))
	if measurement_only and not reproduce:
		raise ValueError(f'{name}: measurement-only sources cannot be reproduced or reconstructed')
	return data, entry


def private_path(output):
	path = Path(output).expanduser().absolute()
	if any(p.is_symlink() for p in (path, *path.parents)):
		raise ValueError('Private output paths must not contain symlinks')
	path = path.resolve()
	if path.is_relative_to(ref.REPO) or any((p/'.git').exists() for p in (path, *path.parents)):
		raise ValueError('Private outputs must be outside every Git repository')
	return path


def record_derived(manifest, outputs, source_ids, method, **metadata):
	manifest = private_path(manifest)
	data = load_manifest(manifest)
	for source_id in source_ids:
		issues = source_issues(data, source_id)
		if issues:
			raise ValueError('; '.join(issues))
	for output in outputs:
		output = private_path(output)
		name = os.path.relpath(output, manifest.parent).replace(os.sep, '/')
		entry = dict(path=name, sha256=ref.sha256(output), method=method, **{'from': sorted(set(source_ids))}, **metadata)
		if any(s['path'] == name for s in data['sheets']):
			raise ValueError('Derived output cannot replace an approved sheet')
		data['derived'] = [e for e in data.get('derived', []) if e['path'] != name] + [entry]
	data['derived'] = sorted(data.get('derived', []), key=lambda e:e['path'])
	errors = ref.schema_errors(data, ref.load_json(MANIFEST_SCHEMA))
	if errors:
		raise ValueError('; '.join(errors))
	ref.write_json(data, manifest, private=True)


def derived_input(path, manifest):
	"""Validate a private intermediate and flatten its lineage to original source ids."""
	path = Path(path).expanduser().absolute()
	if any(p.is_symlink() for p in (path,*path.parents)):
		raise ValueError('Symlinks are not permitted in reconstruction input')
	path = path.resolve(strict=True)
	manifest = Path(manifest).expanduser().resolve(strict=True)
	data = load_manifest(manifest)
	name = os.path.relpath(path, manifest.parent).replace(os.sep, '/')
	entry = next((e for e in data.get('derived', []) if e['path'] == name), None)
	if entry is None:
		# An original sheet may enter frames/build directly, but never measurement-only.
		_, entry = gate(manifest.parent, name, manifest, reproduce=False)
		return [s['source_id'] for s in entry['sources']]
	issues = entry_issues(data, name)
	if issues or ref.sha256(path) != entry['sha256']:
		raise ValueError(f"{name}: {'; '.join(issues) if issues else 'manifest hash mismatch'}")
	for source_id in entry['from']:
		if any(s.get('allowed_use') == 'measurement-only' or source['consent']['scope'] == 'measurement-only'
			for s in data['sheets'] for source in s['sources'] if source['source_id'] == source_id):
			raise ValueError(f'{name}: measurement-only source cannot be reconstructed')
	return entry['from']


def audit(manifest, files):
	data = load_manifest(manifest)
	findings = []
	for file in files:
		document = ref.load_json(file)
		def check(source, location):
			# A repository file citation is not an approved sheet citation. Unresolved
			# media/role citations fail closed; do not guess a sheet from its basename.
			if not isinstance(source, str):
				findings.append(f'{file}:{location}: invalid source citation'); return
			try:
				repository_source = (ref.REPO/ref.relative_path(source)).resolve()
			except ValueError:
				repository_source = None
			if repository_source is not None and repository_source.is_relative_to(ref.REPO) and repository_source.is_file() and not any(e['path'] == source for e in data['sheets'] + data.get('derived', [])):
				return
			issues = entry_issues(data, source)
			if not issues and not (Path(manifest).parent/source).is_file():
				issues.append('missing sheet file')
			for issue in issues:
				findings.append(f'{file}:{location}: {source}: {issue}')
		def walk(item, location='$'):
			if isinstance(item, dict):
				if 'source' in item: check(item['source'], location)
				if 'image' in item: check(item['image'], location+'.image')
				for key, value in item.items(): walk(value, location+'.'+key)
			elif isinstance(item, list):
				for i, value in enumerate(item): walk(value, f'{location}[{i}]')
		walk(document)
	return findings


def requests(manifest, action, request=None, status=None):
	manifest = private_path(manifest)
	data = load_manifest(manifest)
	path = manifest.with_name('requests.json')
	register = ref.load_json(path) if path.exists() else dict(schema_version=1, requests=[])
	if register.get('schema_version') != 1 or not isinstance(register.get('requests'), list):
		raise ValueError('Invalid request register')
	seen = set()
	for row in register['requests']:
		if not isinstance(row, dict) or not REQUEST_ID.fullmatch(row.get('id','')) or row.get('status') not in STATUSES or row['id'] in seen:
			raise ValueError('Invalid or duplicate request register entry')
		seen.add(row['id'])
	if action == 'mark':
		if not REQUEST_ID.fullmatch(request):
			raise ValueError('Invalid C9 request id')
		row = next((r for r in register['requests'] if r['id'] == request), None)
		if row is None:
			row = dict(id=request, status='open', sheets=[]); register['requests'].append(row)
		row['status'] = status
	for row in register['requests']:
		row['sheets'] = sorted(s['path'] for s in data['sheets'] if row['id'] in s.get('tags',{}).get('requests',[]))
	if action == 'mark':
		register['requests'].sort(key=lambda r:r['id']); ref.write_json(register, path, private=True)
	for row in sorted(register['requests'], key=lambda r:r['id']):
		if action == 'mark' and row['id'] == request or action == 'status' and row['status'] in ('open','needs-reshoot'):
			print(f"{row['id']}\t{row['status']}\t{', '.join(row['sheets']) or 'no sheets'}")
