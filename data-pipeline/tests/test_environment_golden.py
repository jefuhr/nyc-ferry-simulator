"""The pipeline against the independent golden field pack the C# tests use (core/dotnet/FerrySim.Core.Tests/Environment/
python_field_pack.py). Failure modes guarded: the pipeline's reader refusing a pack written to the spec by someone else,
or its sampling rule (field.evaluate, and the station blend outside the grid) drifting from the one the core implements.
"""
import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from ferrydata.environment import field, pack, validation

SCRIPT = Path(__file__).resolve().parents[2] / 'core' / 'dotnet' / 'FerrySim.Core.Tests' / 'Environment' / 'python_field_pack.py'
TOLERANCE = 1e-12  # the same as PythonFieldPackFixture.Tolerance


@pytest.fixture(scope='module')
def golden(tmp_path_factory):
	root = tmp_path_factory.mktemp('golden')
	result = subprocess.run([sys.executable, '-I', str(SCRIPT), str(root)], check=True, capture_output=True, text=True)
	return root / 'lower-east-river' / 'env', json.loads(result.stdout)


def stored_basis(env):
	manifest = json.loads((env / 'field' / 'manifest.json').read_text())
	grid = manifest['grids'][0]
	shape = (grid['rows'], grid['cols'])
	values = np.fromfile(env / 'field' / grid['values_file'], dtype='<f4').reshape(shape + (4 + grid['slots'],)).astype(float)
	slots = np.fromfile(env / 'field' / grid['stations_file'], dtype=np.uint8).reshape(shape + (grid['slots'],))
	return SimpleNamespace(lattice=field.Lattice(grid['origin_north_m'], grid['origin_east_m'], grid['cell_m'], *shape),
		stations=[field.Station(s['id'], s['north_m'], s['east_m']) for s in manifest['stations']],
		frames=np.array([[s['frame_north'], s['frame_east']] for s in manifest['stations']]),
		axis_north=values[..., 0], axis_east=values[..., 1], along=values[..., 2], cross=values[..., 3], slots=slots,
		slot_weights=values[..., 4:])


def velocity(rows, t):
	times = rows[:, 0]
	if t in times:
		return rows[times == t][0, 1:]
	k = int(np.searchsorted(times, t)) - 1
	fraction = (t - times[k]) / (times[k + 1] - times[k])
	return rows[k, 1:] + fraction * (rows[k + 1, 1:] - rows[k, 1:])


def test_the_pipeline_verifies_the_golden_pack_with_its_digests(golden):
	env, out = golden
	assert pack.verify(env) == [out['day']]
	assert pack.read_digest(env / 'field') == out['field'] == pack.field_digest(env / 'field')
	assert pack.read_digest(env / 'depth') == out['depth']


def test_the_pipeline_samples_the_golden_points_like_the_core(golden):
	env, out = golden
	basis = stored_basis(env)
	day = json.loads((env / 'days' / '2025-07-14' / 'manifest.json').read_text())
	rows = {}
	for line in (env / 'days' / '2025-07-14' / 'currents.csv').read_text().splitlines()[1:]:
		sid, t, n, e = line.split(',')
		rows.setdefault(sid, []).append((float(t), float(n), float(e)))
	rows = {sid: np.array(v) for sid, v in rows.items()}
	lat = basis.lattice
	for label, north, east, t, vn, ve in out['samples']:
		at = {sid: velocity(r, t)[None, :] for sid, r in rows.items()}
		inside = 0 <= (north - lat.origin_north) / lat.cell <= lat.rows - 1 and 0 <= (east - lat.origin_east) / lat.cell <= lat.cols - 1
		if inside:
			u = field.evaluate(basis, at, np.array([north]), np.array([east]))[0]
		else:
			stations = [field.Station(s['id'], s['north_m'], s['east_m']) for s in day['current_stations']]
			u = validation.blend(stations, {sid: v[0] for sid, v in at.items()}, north, east)
		assert u == pytest.approx([vn, ve], abs=TOLERANCE), label
