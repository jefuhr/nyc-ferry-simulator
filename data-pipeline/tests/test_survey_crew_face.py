"""A same-face alias must not change crew-supported collision eligibility."""
from ferrydata import export_unity, survey
from test_landing_structures import _build_fixture


def test_charted_crew_face_alias_keeps_exposed_run_and_primary_support(tmp_path, monkeypatch):
	measurements = survey.load()
	with monkeypatch.context() as m:
		m.setattr(survey, 'load', lambda: {})
		_, before, old_structures = _build_fixture(m, tmp_path / 'before', 'south-williamsburg.json')
	with monkeypatch.context() as m:
		m.setattr(survey, 'load', lambda: {'8': measurements['8']})
		_, after, new_structures = _build_fixture(m, tmp_path / 'after', 'south-williamsburg.json')
	old = before[0]['properties']; new = after[0]['properties']
	assert new['contact_geometry'] == old['contact_geometry']
	assert new['contact_local_coordinates'] == old['contact_local_coordinates']
	assert [r['on_primary_face'] for r in new['contact_segment_evidence']] == [r['on_primary_face'] for r in old['contact_segment_evidence']]
	assert all(r.get('water_side_crew_reported') for r in new['contact_segment_evidence'] if r['on_primary_face'])
	assert not new['water_side_verified_by_pipeline']
	old_contacts = export_unity.contact_records(old_structures)
	new_contacts = export_unity.contact_records(new_structures)
	assert [r['id'] for r in new_contacts if export_unity.landing_gate(r)[0] == 'kept'] == [r['id'] for r in old_contacts if export_unity.landing_gate(r)[0] == 'kept']
	assert new['berths'][0]['alias'] == measurements['8']['barges'][0]['berths'][0]['name']
