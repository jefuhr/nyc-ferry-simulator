"""Rebuild failure modes: timing bytes in pins, stale terrain depth/shore, unsafe metadata relinks."""
import copy
import json
from types import SimpleNamespace

import pytest

from ferrydata import paths, tiles


@pytest.fixture
def compact_build(monkeypatch, tmp_path):
	monkeypatch.setattr(paths, 'PROCESSED_ROOT', tmp_path)
	index = dict(enc_cells=[], tiles=[dict(id='tile_0_0', active=True)])
	monkeypatch.setattr(tiles, 'inventory', lambda: copy.deepcopy(index))
	counter = iter((1, 2, 3))
	def run(args, **kwargs):
		attempt = next(counter)
		target = tmp_path / 'harbor/tiles/tile_0_0'
		tiles._write(target / 'build_metrics.json', dict(elapsed_seconds=attempt, peak_rss_bytes=attempt * 1024))
		tiles._write(target / 'product.json', dict(stable=True))
		return SimpleNamespace(stdout=f'build attempt {attempt}\n', stderr='', returncode=0)
	monkeypatch.setattr(tiles.subprocess, 'run', run)
	return tmp_path / 'harbor'


def test_two_tile_builds_have_identical_index_and_product_pins(compact_build):
	assert tiles.build(log=lambda _: None) == 0
	before = (compact_build / 'index.json').read_bytes()
	pins = tiles._json(compact_build / 'index.json')['tiles'][0]['outputs']
	assert tiles.build(log=lambda _: None) == 0
	assert (compact_build / 'index.json').read_bytes() == before
	assert tiles._json(compact_build / 'index.json')['tiles'][0]['outputs'] == pins


def test_timing_and_logs_are_retained_outside_product_inventory(compact_build):
	assert tiles.build(log=lambda _: None) == 0
	entry = tiles._json(compact_build / 'index.json')['tiles'][0]
	assert 'metrics' not in entry
	assert [p['file'] for p in entry['outputs']] == ['tiles/tile_0_0/product.json']
	metrics = tiles._json(compact_build / 'build_metrics.json')['tiles']['tile_0_0']
	assert metrics['elapsed_seconds'] == 1
	assert metrics['peak_rss_bytes'] == 1024
	assert metrics['lock_wait_and_startup_seconds'] >= 0
	assert (compact_build / 'tiles/tile_0_0/build.log').read_text() == 'build attempt 1\n'


@pytest.mark.parametrize('failed_step', [None, 'export-unity'])
def test_rebuild_script_orders_commands_and_stops_on_failure(tmp_path, failed_step):
	import os
	import subprocess
	from pathlib import Path
	root = Path(__file__).resolve().parents[2]
	interpreter = tmp_path / 'python-fixture'
	interpreter.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
with open(os.environ['CALL_LOG'], 'a') as stream:
	stream.write(json.dumps(sys.argv[1:]) + '\\n')
if os.environ.get('FAIL_STEP') in sys.argv[1:]:
	sys.exit(7)
if sys.argv[2:4] == ['ferrydata.harbor_runtime', 'build']:
	out = pathlib.Path(sys.argv[sys.argv.index('--out') + 1])
	out.mkdir(parents=True, exist_ok=True)
	(out / 'DIGEST').write_text('fixture-digest\\n')
''')
	interpreter.chmod(0o755)
	# Record the lock wrapper so the test proves the tile build has no outer lock.
	lock = tmp_path / 'lockf'
	lock.write_text('''#!/bin/bash
printf 'locked\\n' >> "$LOCK_LOG"
shift 2
exec "$@"
''')
	lock.chmod(0o755)
	calls = tmp_path / 'calls.jsonl'
	env = dict(os.environ, FERRYDATA_PYTHON=str(interpreter), CALL_LOG=str(calls),
		LOCK_LOG=str(tmp_path / 'locks'), PATH=str(tmp_path) + os.pathsep + os.environ['PATH'])
	if failed_step:
		env['FAIL_STEP'] = failed_step
	result = subprocess.run(['bash', str(root / 'scripts/rebuild-harbor.sh'), '--out', str(tmp_path / 'runtime')],
		env=env, capture_output=True, text=True)
	commands = [json.loads(line) for line in calls.read_text().splitlines()]
	assert commands[0] == ['-m', 'ferrydata', 'build', '--harbor']
	assert commands[1] == ['-m', 'ferrydata', 'export-unity', '--harbor']
	if failed_step:
		assert result.returncode == 7
		assert len(commands) == 2
		assert not (tmp_path / 'locks').exists()
		assert 'DIGEST' not in result.stdout
	else:
		assert result.returncode == 0, result.stderr
		assert [c[1:3] for c in commands[2:]] == [['ferrydata.world3d', 'refresh'], ['ferrydata.harbor_runtime', 'build'], ['ferrydata.harbor_runtime', 'verify']]
		assert (tmp_path / 'locks').read_text().splitlines() == ['locked'] * 2
		assert result.stdout == 'DIGEST fixture-digest\n'
