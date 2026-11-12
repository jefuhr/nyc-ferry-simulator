"""Fit failure modes: ambiguous GPS heading, same-face grouping, noise, size
conflicts, calibration drift, unsupported chart associations and repeatability.
"""
import copy
import json
import math

import numpy as np
import pytest
from shapely.geometry import box

from ferrydata import paths, survey, survey_fit


def slips(points):
	return [dict(name=f'Slip {i+1}', point_en=p) for i, p in enumerate(points)]


def test_two_slips_on_one_face_fit_direction_and_group_names():
	result = survey_fit.fit(slips([(12, -8), (12, 8)]), box(-5, -15, 5, 15), (-40, 0), 7, length=30, width=10)
	assert result['centre_en'] == pytest.approx([0, 0], abs=1e-8)
	assert result['heading_deg'] == pytest.approx(0)
	assert len(result['berths']) == 1
	assert set(result['berths'][0]['name'].split(' / ')) == {'Slip 1', 'Slip 2'}
	assert len(result['slips']) == 2
	assert result['confidence'] in ('low', 'medium')


def test_opposite_ends_use_long_axis_and_both_faces():
	result = survey_fit.fit(slips([(0, -22), (0, 22)]), box(-5, -15, 5, 15), (-40, 0), 7, length=30, width=10)
	assert result['centre_en'] == pytest.approx([0, 0], abs=1e-8)
	assert result['heading_deg'] == pytest.approx(0)
	assert len(result['berths']) == 2
	assert result['length_m'] == 30


def test_single_slip_cannot_determine_heading_uses_shoreline():
	result = survey_fit.fit(slips([(12, 0)]), box(-5, -15, 5, 15), (-40, 0), 7, length=30, width=10)
	assert result['heading_determined'] is False
	assert result['confidence'] == 'low'
	assert result['heading_deg'] == pytest.approx(0)
	assert 'shoreline' in result['reason']


def test_five_metre_noise_is_disclosed_and_does_not_create_high_confidence():
	result = survey_fit.fit(slips([(17, -10), (7, 10)]), box(-5, -15, 5, 15), (-40, 0), 7, length=30, width=10)
	assert np.linalg.norm(result['centre_en']) < 6
	assert result['confidence'] == 'low'
	assert result['alternatives']


def test_slips_that_cannot_fit_published_size_are_rejected():
	with pytest.raises(ValueError, match='cannot fit'):
		survey_fit.fit(slips([(0, -60), (0, 60)]), box(-5, -15, 5, 15), (-40, 0), 7)


def test_no_fitted_barge_exceeds_published_dimensions():
	actual, report = survey_fit.generate()
	assert '120' not in actual['stops'] and '136' not in actual['stops']
	for stop in actual['stops'].values():
		for barge in stop['barges']:
			if 'length' in barge:
				assert barge['length']['value'] <= 27.432
				assert barge['width']['value'] <= 10.668
	for sid in ('120', '136'):
		assert len(report['stops'][sid]['slips']) == 4
		assert report['stops'][sid]['barges'][0]['before'] == report['stops'][sid]['barges'][0]['after']


def test_calibration_reacts_to_a_changed_committed_slip():
	inputs = survey_fit.inputs()
	payload = json.loads((paths.MANIFEST_DIR / 'crew-slip-positions.json').read_text())
	before = survey_fit.calibrate(inputs, payload)
	changed = copy.deepcopy(payload)
	stop = next(s for s in changed['stops'] if s['stop_id'] == '11')
	stop['slips'][0]['position'][1] -= .00005
	after = survey_fit.calibrate(inputs, changed)
	assert before['mean_m'] != after['mean_m']
	assert before['samples'] != after['samples']
	assert before['sample_count'] > 15


def test_committed_survey_is_reproducible_and_validates():
	actual, report = survey_fit.generate()
	assert actual == json.loads((paths.MANIFEST_DIR / 'landing-survey.json').read_text())
	assert survey.load() == actual['stops']
	assert '111' not in actual['stops']
	assert len(actual['stops']) == 20
	assert not any('landed_heading' in b for s in actual['stops'].values() for g in s['barges'] for b in g.get('berths', []))


def test_same_face_near_corner_keeps_hypothesized_face_not_nearest_end():
	result = survey_fit.fit(slips([(12, -16), (12, 16)]), box(-5, -15, 5, 15), (-40, 0), 7, length=30, width=10)
	assert result['layout'] == 'same face'
	assert len(result['berths']) == 1
	assert {r['face_index'] for r in result['slips']} == {result['slips'][0]['face_index']}
	assert all(r['off_m'] == pytest.approx(7) for r in result['slips'])
	assert sorted(r['along_m'] for r in result['slips']) == pytest.approx([-1,31])
