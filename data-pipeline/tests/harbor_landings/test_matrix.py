"""Matrix gates: low tide between hours, exclusions retained, confirmed faces still fail physics."""
import json
import math

import pytest

from ferrydata import operational_depth as od
from ferrydata.harbor_landings import assessment as a, faces, matrix


def test_catalogue_matches_each_runtime_face_in_pack_order_and_keeps_legacy_ids():
	from ferrydata import paths
	root = paths.REPO_ROOT / 'data/processed/harbor'
	contacts = json.loads((root / 'unity/contacts.json').read_text())
	runtime = json.loads((root / 'runtime/landings.json').read_text())
	# Failure modes: grouping by physical barge drops faces; ring order changes the
	# default; display aliases or existing saved-scenario IDs disappear.
	actual = matrix.catalogue(contacts, runtime=runtime)
	expected = [(s['stop_id'], b) for s in runtime['stops'] for b in s['berths']]
	assert len(actual) == len(expected)
	seen = set()
	for row, (stop, face) in zip(actual, expected, strict=True):
		key = (stop, face['berth_id'])
		assert row['stop_id'] == stop
		if stop == '17':
			legacy = face['berth_face_segment_ids'] == [1238696637, 926901007]
			assert row['berth_id'] == ('enc:02261ACE4C291C82' if legacy else face['id'])
		else:
			assert row['berth_id'] == (face['id'] if key in seen else face['berth_id'])
		assert row['berth_name'] == face.get('alias', face['berth_name'])
		assert row['selected_segment_ids'] == face['berth_face_segment_ids']
		seen.add(key)


def test_runtime_face_omission_is_rejected_instead_of_silently_skipping_contacts():
	from ferrydata import paths
	root = paths.REPO_ROOT / 'data/processed/harbor'
	contacts = json.loads((root / 'unity/contacts.json').read_text())
	runtime = json.loads((root / 'runtime/landings.json').read_text())
	runtime['stops'][0]['berths'].pop()
	with pytest.raises(ValueError, match='runtime.*contact.*faces'):
		matrix.catalogue(contacts, runtime=runtime)


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
