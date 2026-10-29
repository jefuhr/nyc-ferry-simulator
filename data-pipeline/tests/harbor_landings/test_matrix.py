"""Matrix gates: low tide between hours, exclusions retained, confirmed faces still fail physics."""
import json
import math

import pytest

from ferrydata import operational_depth as od
from ferrydata.harbor_landings import assessment as a, faces, matrix


def test_low_water_uses_breakpoints_between_hourly_samples():
	levels = od.DayLevels([od.Gauge('g', 0., 0., [0., 360., 720., 3600., 86340.], [0., -2., 0., 1., 0.])], (-3600., 90000.))
	assert matrix.low_time(levels, 0., 0.) == 360.


def test_confirmed_face_retains_independent_depth_and_contact_refusal():
	assert a.status(-.01, True) == 'aground_refusal'
	assert a.status(10., True, 0., True) == 'contact_refusal'
	assert a.status(math.nan, True) == 'unknown_depth'


def test_production_pose_uses_bow_clearance_and_heading_against_normal():
	v = a.vessel('t-class')
	berth = dict(mid=[100., 200.], normal=[0., 1.])
	n, e, heading = matrix.pose(berth, v)
	assert n == pytest.approx(100., abs=1e-7)
	assert e == pytest.approx(217.76, abs=1e-7)
	assert heading == 270.
