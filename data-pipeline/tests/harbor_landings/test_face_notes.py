"""Failure modes: missing or stale notes unlock a face, or crew evidence bypasses physical gates."""
import copy

from ferrydata.harbor_landings import faces


RECORD = dict(stop_id='137', berth_id='enc:fixture', start_ne_m=[0., 0.], end_ne_m=[1., 0.], water_normal_ne=[0., -1.])


def note():
	return dict(stop_id='137', berth_id='enc:fixture', geometry_sha256=faces.geometry_digest([RECORD]),
		date='2026-10-26', source='project crew reviewer', statement='The existing NE face is used.',
		reviewed=True, confirms_existing_face=True)


def test_empty_notes_keep_all_three_faces_excluded():
	assert faces.load_notes() == []
	for stop in ('113', '118', '137'):
		assert not faces.face_eligible(stop, 'enc:fixture', [RECORD], [], True)


def test_only_a_reviewed_matching_confirmation_unlocks_the_face():
	assert faces.face_eligible('137', 'enc:fixture', [RECORD], [note()], False)
	for key, value in [('reviewed', False), ('confirms_existing_face', False), ('geometry_sha256', '0' * 64), ('source', ''), ('statement', '')]:
		n = note(); n[key] = value
		assert not faces.face_eligible('137', 'enc:fixture', [RECORD], [n], False)
	changed = copy.deepcopy(RECORD); changed['end_ne_m'][0] = 2.
	assert not faces.face_eligible('137', 'enc:fixture', [changed], [note()], False)


def test_normal_faces_keep_the_production_gate():
	assert faces.face_eligible('4', 'enc:fixture', [RECORD], [], True)
	assert not faces.face_eligible('4', 'enc:fixture', [RECORD], [], False)


def test_landing_build_reads_note_then_still_assesses_depth():
	from ferrydata.harbor_landings import matrix, assessment
	contact = dict(RECORD, id=1, stop_name='fixture', source='fixture', confidence='estimated', verification='needs-crew-verification',
		primary_face=True, berth_face=True, berth_face_segment_ids=[1], solid_side_evidence={'verified': True})
	assert not matrix.catalogue([contact], [])[0]['eligible']
	approved = matrix.catalogue([contact], [note()])[0]
	assert approved['eligible']
	assert assessment.status(1., approved['eligible']) == 'startable'
	assert assessment.status(-1., approved['eligible']) == 'aground_refusal'
