"""Depth sets with several grids (ferrysim-env-depth/1 as revised 2026-10-25), against the independent golden pack the C#
tests use (core/dotnet/FerrySim.Core.Tests/Environment/python_depth_pack.py). Failure modes guarded: the pipeline's
reader refusing a multi-grid pack written to the rule by someone else, or sampling it differently from the core (on and
next to seams and corners, in an overlap, next to unknown centres); grids in the wrong order winning, so swapping two
grids does not change where a point on their seam resolves; an ambiguous set accepted (a shared sample that differs or
is unknown in one grid only, grids that meet off one lattice or with different cell sizes), which the core refuses too
(DepthSetPackTests); a seam step when sampling across a shared edge; single-grid sets read differently than before.
"""
import hashlib
import json
import math
import subprocess
import sys
from pathlib import Path

import numpy as np
import pytest

from ferrydata.environment import pack

SCRIPT = Path(__file__).resolve().parents[2] / 'core' / 'dotnet' / 'FerrySim.Core.Tests' / 'Environment' / 'python_depth_pack.py'
TOLERANCE = 1e-12  # the same as PythonDepthPackFixture.Tolerance


def write_golden(root):
	result = subprocess.run([sys.executable, '-I', str(SCRIPT), str(root)], check=True, capture_output=True, text=True)
	return root / 'harbor' / 'env', json.loads(result.stdout)


@pytest.fixture
def golden(tmp_path):
	return write_golden(tmp_path / 'golden')


def reseal(env, edit=None, grid_edits=None):
	"""Edits the golden depth manifest and grid files, then reseals the depth set and the day over it, as the C# tests'
	EditDepth does."""
	directory = env / 'depth'
	manifest = json.loads((directory / 'manifest.json').read_text())
	for grid_id, index, value in grid_edits or []:
		path = directory / f'{grid_id}.f32'
		values = np.fromfile(path, dtype='<f4')
		values[index] = value
		path.write_bytes(values.astype('<f4').tobytes())
		next(g for g in manifest['grids'] if g['id'] == grid_id)['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
	if edit:
		edit(manifest)
	(directory / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
	pack.write_digest(directory, pack.depth_digest(directory))
	day = env / 'days' / '2025-07-14'
	metadata = json.loads((day / 'manifest.json').read_text())
	metadata['depth_digest'] = pack.read_digest(directory)
	(day / 'manifest.json').write_text(json.dumps(metadata, indent=2) + '\n')
	pack.write_digest(day, pack.day_digest(day, pack.read_digest(directory)))


def test_the_pipeline_verifies_the_golden_multi_grid_pack_with_its_digests(golden):
	env, out = golden
	assert pack.verify(env) == [out['day']]
	assert pack.read_digest(env / 'depth') == out['depth'] == pack.depth_digest(env / 'depth')


def test_the_pipeline_samples_the_golden_points_like_the_core(golden):
	env, out = golden
	depth = pack.load_depth(env / 'depth')
	assert [g['id'] for g in depth.grids] == ['sw', 'se', 'nw', 'ne', 'cap', 'far']
	for label, north, east, kind, grid, bed in out['samples']:
		sample = depth.sample(north, east)
		assert sample.kind == kind, label
		assert (None if sample.grid < 0 else depth.grids[sample.grid]['id']) == grid, label
		assert sample.bed == pytest.approx(bed, abs=TOLERANCE), label


def test_swapping_two_grids_changes_which_one_a_point_on_their_seam_resolves_to(golden):
	env, out = golden
	assert pack.load_depth(env / 'depth').sample(0., 15.).kind == 'unknown'
	def swap(manifest):
		grids = manifest['grids']
		grids[1], grids[3] = grids[3], grids[1]
	reseal(env, swap)
	pack.verify(env)
	depth = pack.load_depth(env / 'depth')
	sample = depth.sample(0., 15.)
	assert (sample.kind, depth.grids[sample.grid]['id']) == ('known', 'ne')
	north_of_it = next(s for s in out['samples'] if s[0] == 'just north of it, in ne')
	assert sample.bed == pytest.approx(north_of_it[5], abs=1e-8)


@pytest.mark.parametrize('grid_edits,edit,expected', [
	([('nw', 1, -20.)], None, 'sw and nw share the sample at north 0, east -20'),
	([('nw', 0, -9.)], None, 'sw and nw share the sample at north 0, east -30'),
	([], lambda m: m['grids'][2].update(origin_east_m=-25.), 'one lattice'),
	([], lambda m: m['grids'][4].update(cell_m=5.), 'cell size'),
], ids=['a shared sample differs', 'a shared sample unknown in one grid only', 'grids meeting half a cell off',
	'grids meeting with different cell sizes'])
def test_a_multi_grid_pack_that_breaks_the_rule_is_refused(golden, grid_edits, edit, expected):
	env, _ = golden
	reseal(env, edit, grid_edits)
	with pytest.raises(ValueError, match=expected):
		pack.verify(env)


def test_sampling_across_a_shared_edge_has_no_step(golden):
	env, _ = golden
	depth = pack.load_depth(env / 'depth')
	# Millimetre steps along a line across the sw/nw seam (north 0): bilinear in each cell, so each side has its own slope.
	beds = np.array([depth.sample(k * 1e-3, -12.5).bed for k in range(-2000, 2001)])
	steps = np.abs(np.diff(beds))
	south, seam, north = steps[:1999], steps[1999:2001], steps[2001:]
	assert seam.max() <= max(south.max(), north.max()) + 1e-12, 'no step at the seam beyond the slopes either side of it'
	assert abs(depth.sample(1e-9, -12.5).bed - depth.sample(-1e-9, -12.5).bed) < 1e-9


def write_depth(directory, grids):
	"""A depth folder of these grids, each (id, origin north, origin east, cell, rows of values)."""
	directory.mkdir(parents=True)
	entries = []
	for grid_id, north, east, cell, values in grids:
		body = np.asarray(values, dtype='<f4').tobytes()
		(directory / f'{grid_id}.f32').write_bytes(body)
		entries.append(dict(id=grid_id, file=f'{grid_id}.f32', origin_north_m=north, origin_east_m=east, cell_m=cell, rows=len(values),
			cols=len(values[0]), nodata='NaN', sha256=hashlib.sha256(body).hexdigest()))
	pack.write_json(directory / 'manifest.json', dict(format='ferrysim-env-depth/1', region='harbor', datum='NAVD88',
		value='bed elevation, metres, positive up (water is negative)', grids=entries, outside_depth_m=20., sources=[]))
	pack.write_digest(directory, pack.depth_digest(directory))
	return directory


@pytest.mark.parametrize('reversed_', [False, True])
def test_grids_sharing_an_edge_with_a_different_sample_are_refused_in_either_order(tmp_path, reversed_):
	# The wave 1 review's case: on the shared row one grid says -10 m and the other -20 m.
	grids = [('south', -10., 0., 10., [[-10.] * 2] * 2), ('north', 0., 0., 10., [[-20.] * 2] * 2)]
	directory = write_depth(tmp_path / 'depth', grids[::-1] if reversed_ else grids)
	with pytest.raises(ValueError, match='share the sample at north 0, east 0, but hold'):
		pack.verify_depth(directory)


def test_grids_that_do_not_meet_may_use_any_lattice_and_aligned_overlaps_resolve_to_the_first(tmp_path):
	bed = lambda n, e: -10. - .01 * n - .02 * e - .0001 * n * n
	grid = lambda gid, n0, e0, cell, rows, cols: (gid, n0, e0, cell, [[bed(n0 + r * cell, e0 + c * cell) for c in range(cols)] for r in range(rows)])
	directory = write_depth(tmp_path / 'depth', [grid('big', 0., 0., 10., 6, 6), grid('small', 20., 20., 10., 6, 6),
		grid('apart', 80.5, 0., 5., 3, 3), grid('odd', 0., 70.0001, 7., 2, 2)])
	depth = pack.load_depth(directory)
	assert [depth.sample(35., 35.).grid, depth.sample(65., 65.).grid, depth.sample(81., 1.).grid, depth.sample(80.25, 5.).grid] == [0, 1, 2, -1]


def test_a_single_grid_samples_as_before(tmp_path):
	values = [[-10., -11., -12.], [-13., -14.5, np.nan], [-16., -17., -18.25]]
	depth = pack.load_depth(write_depth(tmp_path / 'depth', [('only', -5., 2.5, 5., values)]))
	# A quarter of a cell north and half a cell east of the first centre: bilinear, not planar.
	lower, upper = -10. + .5 * (-11. + 10.), -13. + .5 * (-14.5 + 13.)
	sample = depth.sample(-3.75, 5.)
	assert (sample.kind, sample.grid, sample.bed) == ('known', 0, lower + .25 * (upper - lower))
	assert depth.sample(5., 2.5).bed == -16.
	assert depth.sample(2.5, 10.).kind == 'unknown'
	assert depth.sample(5., 12.5).kind == 'unknown'
	assert (depth.sample(5.0001, 2.5).kind, depth.sample(5.0001, 2.5).grid, depth.sample(5.0001, 2.5).bed) == ('outside', -1, 0.)
