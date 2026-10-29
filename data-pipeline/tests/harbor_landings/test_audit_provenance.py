"""Fail closed on relabelled depth, missing provenance and changed vessel bytes."""
import hashlib
import importlib.util
import json
import sys
from types import SimpleNamespace

import numpy as np
import pytest

from ferrydata import paths, operational_depth as od
from ferrydata.harbor_landings import matrix


SCRIPT = paths.REPO_ROOT / 'unity/Assets/FerrySim/Harbor/Data/write_landing_audit.py'
spec = importlib.util.spec_from_file_location('landing_audit_writer', SCRIPT)
writer = importlib.util.module_from_spec(spec)
# The writer lives under Unity Assets; importing a test helper must not create Unity-visible cache assets.
previous_bytecode = sys.dont_write_bytecode
sys.dont_write_bytecode = True
try:
	spec.loader.exec_module(writer)
finally:
	sys.dont_write_bytecode = previous_bytecode


def sha(path):
	return hashlib.sha256(path.read_bytes()).hexdigest()


@pytest.fixture
def bundle(tmp_path, monkeypatch):
	shipped = tmp_path / 'shipped'
	(shipped / 'vessels').mkdir(parents=True)
	for hull in ('t-class', 'k-class'):
		(shipped / f'vessels/{hull}.json').write_bytes((paths.REPO_ROOT / f'vessels/{hull}.json').read_bytes())
	monkeypatch.setattr(writer, 'REPO_ROOT', shipped)
	berths = tmp_path / 'berths.json'
	berths.write_text(json.dumps([dict(stop_id='87', stop_name='Pier 11', berth_id='south', berth_name='South', eligible=True,
		hulls=[dict(hull=h, rows=700, accepted=n, warning=0, aground_refusal=700-n, worst_ukc_m=-.6)
			for h, n in [('t-class', 467), ('k-class', 598)]])]))
	digest = tmp_path / 'DIGEST'
	digest.write_text('a' * 64 + '\n')
	provenance = dict(depth_digest='a' * 64, contacts_sha256='c' * 64, day_digests={'2025-07-16': 'd' * 64},
		vessel_sha256={f'vessels/{h}.json': sha(paths.REPO_ROOT / f'vessels/{h}.json') for h in ('t-class', 'k-class')},
		berths_sha256=sha(berths))
	(tmp_path / 'provenance.json').write_text(json.dumps(provenance))
	return berths, digest, tmp_path / 'audit.json', provenance


def write(bundle):
	berths, digest, out, _ = bundle
	writer.main(['write_landing_audit.py', str(berths), str(digest), str(out)])
	return out


def test_audit_carries_the_assessed_inputs_and_is_deterministic(bundle):
	out = write(bundle)
	first = out.read_bytes()
	assert json.loads(first)['matrix_provenance'] == bundle[3]
	write(bundle)
	assert out.read_bytes() == first
	assert [h['accepted'] for h in json.loads(first)['berths'][0]['hulls']] == [467, 598]


def test_old_matrix_cannot_be_relabelled_with_adopted_depth(bundle):
	bundle[1].write_text('b' * 64 + '\n')
	with pytest.raises((ValueError, SystemExit), match='depth'):
		write(bundle)
	assert not bundle[2].exists()


def test_missing_provenance_refuses_before_writing(bundle):
	bundle[0].with_name('provenance.json').unlink()
	with pytest.raises((ValueError, SystemExit, FileNotFoundError), match='provenance'):
		write(bundle)
	assert not bundle[2].exists()


def test_changed_vessel_file_invalidates_the_assessment(bundle):
	vessel = writer.REPO_ROOT / 'vessels/t-class.json'
	changed = json.loads(vessel.read_text())
	changed['particulars']['maxDraft']['value'] += .1
	vessel.write_text(json.dumps(changed))
	with pytest.raises((ValueError, SystemExit), match='vessels/t-class.json'):
		write(bundle)
	assert not bundle[2].exists()


def test_changed_berth_counts_are_not_the_assessed_output(bundle):
	bundle[0].write_text(bundle[0].read_text().replace('467', '162'))
	with pytest.raises((ValueError, SystemExit), match='berths'):
		write(bundle)


def test_matrix_hashes_the_exact_vessel_snapshots_it_loaded(tmp_path, monkeypatch):
	contacts = tmp_path / 'contacts.json'
	contacts.write_text('[]')
	monkeypatch.setattr(matrix.pack, 'verify', lambda _: None)
	monkeypatch.setattr(matrix.pack, 'load_depth', lambda _: SimpleNamespace(digest='a' * 64))
	monkeypatch.setattr(matrix.pack, 'read_digest', lambda _: 'd' * 64)
	monkeypatch.setattr(matrix.series, 'DAYS', ('2025-07-16',))
	monkeypatch.setattr(matrix.a, 'load_levels', lambda _: od.DayLevels([od.Gauge('g', 0, 0, [0, 90000], [0, 0])], (0, 90000)))
	monkeypatch.setattr(matrix, 'catalogue', lambda _: [dict(stop_id=str(i), stop_name=str(i), berth_id=str(i),
		berth_name='Main', eligible=False, selected_segment_ids=[], mid=np.array([0., 0.]),
		normal=np.array([0., 1.])) for i in range(26)])
	out = tmp_path / 'matrix'
	matrix.build(contacts, tmp_path / 'env', out)
	p = json.loads((out / 'provenance.json').read_text())
	assert p['vessel_sha256'] == {f'vessels/{h}.json': sha(paths.REPO_ROOT / f'vessels/{h}.json') for h in ('t-class', 'k-class')}
	assert p['berths_sha256'] == sha(out / 'berths.json')
