"""Buffered candidates: protect the exact metric radius, restore the original bed, never change the base,
and remain monotone (A <= 10 m <= 25 m <= original), including nodata and deep pre-existing water.
"""
import numpy as np
import rasterio
from rasterio.transform import from_origin
from shapely.geometry import Point

from ferrydata import geodesy, landings
from ferrydata.harbor_landings import buffered


def test_metric_buffers_restore_only_protected_cells_and_are_monotone():
	transform = from_origin(-74.016, 40.701, .00003, .00003)
	shape = (60, 60)
	original = np.full(shape, -1., dtype='float32')
	original[0, 0] = -9999.
	original[20, 20] = -8.
	floor = np.full(shape, -3., dtype='float32')
	floor[0, 0] = np.nan
	items = [dict(geometry=Point(-74.015, 40.7001))]
	unchanged = floor.copy()
	a, _, _, _ = buffered.apply(original, floor, transform, -9999., items, 0.)
	b, _, _, f10 = buffered.apply(original, floor, transform, -9999., items, 10.)
	c, _, _, f25 = buffered.apply(original, floor, transform, -9999., items, 25.)
	assert np.all(a <= b) and np.all(b <= c) and np.all(c <= original)
	assert c[0, 0] == -9999. and c[20, 20] == -8.
	assert np.any(b == original) and np.any(b < original)
	assert np.array_equal(floor, unchanged, equal_nan=True)
	r, col = np.indices(shape)
	n, e = geodesy.to_local(transform.f + (r + .5)*transform.e, transform.c + (col + .5)*transform.a)
	hn, he = geodesy.to_local(40.7001, -74.015)
	distance = np.hypot(n-hn, e-he)
	assert np.all(b[distance <= 10.] == original[distance <= 10.])
	assert np.all(c[distance <= 25.] == original[distance <= 25.])
	assert np.isnan(f10[distance <= 10.]).all() and np.isnan(f25[distance <= 25.]).all()


def test_buffered_derivation_is_repeatable_and_does_not_mutate_inputs():
	transform = from_origin(-74.016, 40.701, .00003, .00003)
	bed, floor = np.full((10, 10), -1., dtype='float32'), np.full((10, 10), -3., dtype='float32')
	a = buffered.apply(bed, floor, transform, None, [], 10.)
	b = buffered.apply(bed, floor, transform, None, [], 10.)
	assert all(np.array_equal(x, y, equal_nan=True) for x, y in zip(a, b))
	assert np.all(bed == -1.) and np.all(floor == -3.)


def test_authorized_outer_lock_mode_never_acquires_a_nested_heavy_lock(monkeypatch):
	from types import SimpleNamespace
	calls = []
	monkeypatch.setattr(buffered.subprocess, 'run', lambda command, **kwargs: calls.append(command) or SimpleNamespace(returncode=0))
	buffered.run_tile('source', 'base', 'target', 10., lock_held=True)
	assert 'lockf' not in calls[-1]
	assert not any(str(x).endswith('heavy.lock') for x in calls[-1])
	buffered.run_tile('source', 'base', 'target', 25., lock_held=False)
	assert calls[-1][0] == 'lockf'
	assert sum(str(x).endswith('heavy.lock') for x in calls[-1]) == 1
