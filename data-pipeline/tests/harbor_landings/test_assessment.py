"""Failure modes: shoals between vertices, missing depth, float32 arithmetic, wrong tide blend,
max-draft fallback, threshold boundaries, and an unreviewed face becoming an offered start.
"""
import math

import numpy as np
import pytest

from ferrydata.environment import pack
from ferrydata.harbor_landings import assessment as a
from ferrydata import operational_depth as od


def depth(values):
	values = np.asarray(values, dtype='<f4')
	g = dict(origin_north_m=0., origin_east_m=0., cell_m=1., rows=values.shape[0], cols=values.shape[1], values=values)
	return pack.DepthSet('fixture', {}, [g])


def test_vector_bed_matches_core_order_and_unknown_even_at_zero_weight():
	d = depth([[-3., -1., -2.], [-4., -5., -6.], [-7., -8., np.nan]])
	n, e = np.array([.3, 1., 2., -.1]), np.array([.7, 1., 2., 0.])
	actual = a.beds(d, n, e)
	for i in range(len(n)):
		s = d.sample(n[i], e[i])
		if s.kind == 'known':
			assert actual[i] == s.bed
		else:
			assert math.isnan(actual[i])


def test_full_footprint_includes_grid_nodes_edges_and_quarter_metre_lattice():
	d = depth(np.full((5, 5), -10.))
	d.grids[0]['values'][2, 2] = -1.
	outline = np.array([[.1, .1], [3.9, .1], [3.9, 3.9], [.1, 3.9]])
	n, e = a.footprint_points(outline, d)
	assert np.any((n == 2.) & (e == 2.))
	assert np.any((n == .25) & (e == .25))
	assert np.any(n == .1)
	levels = od.DayLevels([od.Gauge('g', 0., 0., [0., 360.], [0., 0.])], (0., 360.))
	assert a.PreparedFootprint(d, outline, levels).minimum(levels, 0.) == 1.
	d.grids[0]['values'][2, 2] = np.nan
	assert math.isnan(a.PreparedFootprint(d, outline, levels).minimum(levels, 0.))


@pytest.mark.parametrize('ukc,status', [(-1e-9, 'aground_refusal'), (0., 'warning'), (.299999, 'warning'), (.3, 'startable')])
def test_status_thresholds(ukc, status):
	assert a.status(ukc, True) == status


def test_unknown_and_excluded_never_start():
	assert a.status(float('nan'), True) == 'unknown_depth'
	assert a.status(5., False) == 'excluded_face'


def test_world_outline_uses_the_contact_hull_and_max_draft():
	v = a.vessel('t-class')
	o = a.world_outline(v, 10., 20., 0.)
	assert o[:, 0].max() == pytest.approx(23.76)
	assert a.maximum_draft(v) == (1.9, 'maxDraft')
	v['particulars'].pop('maxDraft')
	assert a.maximum_draft(v)[1] == 'draft (maxDraft missing)'


def test_contact_clearance_matches_face_clearance_minimum_one_metre():
	assert a.status(5., True, .999999) == 'contact_refusal'
	assert a.status(5., True, 1.) == 'startable'
	assert a.status(5., True, 1.5) == 'startable'


def test_null_maximum_draft_uses_the_explicit_fallback_basis():
	v = a.vessel('t-class')
	v['particulars']['maxDraft'] = None
	assert a.maximum_draft(v) == (v['particulars']['draft']['value'], 'draft (maxDraft missing)')


def test_nan_contact_clearance_fails_closed_like_csharp_is_clear():
	assert a.status(5., True, float('nan')) == 'contact_refusal'
	assert a.status(5., True, float('inf')) == 'startable'


def test_one_gauge_returns_its_exact_value_without_weight_rounding_at_the_start_threshold():
	d = depth(np.full((5, 5), -.5))
	outline = np.array([[.1, .1], [3.9, .1], [3.9, 3.9], [.1, 3.9]])
	levels = od.DayLevels([od.Gauge('g', 0., 0., [0., 360.], [1.5, 1.5])], (0., 360.))
	minimum = a.PreparedFootprint(d, outline, levels).minimum(levels, 0.)
	assert minimum == 2.
	assert a.status(minimum - 2., True) == 'warning'
