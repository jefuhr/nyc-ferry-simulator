"""A rejected low-confidence fit must restore the exact shipped stop, not tune gates."""
import gzip
import json
import shutil
from pathlib import Path

from ferrydata import geojsonio, landings, paths, survey, survey_fit, tiles
from ferrydata.export_unity import contact_records
from ferrydata.harbor_landings import assessment, matrix

FIXTURES = Path(__file__).with_name('fixtures')


def test_hold_is_an_input_and_removing_it_reproduces_the_candidate():
	held, report = survey_fit.generate()
	assert '20' not in held['stops']
	assert report['stops']['20']['hold']['stop_id'] == '20'
	assert all(not s['accepted'] for s in report['stops']['20']['slips'])
	unheld, _ = survey_fit.generate(holds={'schema_version': 1, 'stops': []})
	assert unheld['stops']['20'] == json.loads((FIXTURES / 'landing-survey-unheld-20.json').read_text())
	assert {k:v for k,v in unheld['stops'].items() if k != '20'} == held['stops']


def test_held_contacts_identity_name_and_both_start_poses_match_shipped(tmp_path, monkeypatch):
	root = paths.PROCESSED_ROOT
	index = json.loads((root / 'harbor/index.json').read_text())
	tile_id = next(s['tile'] for s in index['stops'] if s['stop_id'] == '20')
	source = root / 'harbor/tiles' / tile_id
	out = tmp_path / 'harbor/tiles' / tile_id; out.mkdir(parents=True)
	for file in source.glob('*.geojson'):
		shutil.copyfile(file, out / file.name)
	held, _ = survey_fit.generate()
	unheld, _ = survey_fit.generate(holds={'schema_version': 1, 'stops': []})
	results = []
	with monkeypatch.context() as m:
		m.setattr(paths, 'PROCESSED_ROOT', tmp_path)
		m.setattr(paths, 'ais_processed_dir', lambda: root / 'ais')
		for document in (held, unheld):
			m.setattr(survey, 'load', lambda: document['stops'])
			landings.build_tile(tiles.get_tile(tile_id, index['enc_cells']), log=lambda _: None)
			results.append([r for r in contact_records(geojsonio.read_collection(out / 'landing_structures.geojson')) if r['stop_id'] == '20'])
	fixture = paths.REPO_ROOT / 'unity/Assets/FerrySim/Harbor/Tests/Landings/EditMode/landing-adoption-before.json.gz'
	before = [r for r in json.loads(gzip.decompress(fixture.read_bytes())) if r['stop_id'] == '20']
	encode = lambda x: json.dumps(x, sort_keys=True, separators=(',', ':')).encode()
	assert encode(results[0]) == encode(before)
	assert encode(results[1]) == gzip.decompress((FIXTURES / 'landing-survey-unheld-20-contacts.json.gz').read_bytes())
	old, new = matrix.catalogue(before), matrix.catalogue(results[0])
	assert len(old) == len(new) == 1
	assert new[0]['berth_id'] == old[0]['berth_id'] == 'derived-barge:20:02262FAC44F71C82'
	assert new[0]['berth_name'] == old[0]['berth_name'] == 'Main berth'
	for hull in ('t-class', 'k-class'):
		assert matrix.pose(new[0], assessment.vessel(hull)) == matrix.pose(old[0], assessment.vessel(hull))


def test_held_committed_audit_rows_match_shipped():
	audit = json.loads((paths.REPO_ROOT / 'unity/Assets/FerrySim/Harbor/Data/landing-audit.json').read_text())
	actual = [b for b in audit['berths'] if b['stop_id'] == '20']
	expected = json.loads((FIXTURES / 'landing-survey-held-20-audit.json').read_text())
	encode = lambda rows: json.dumps(rows, sort_keys=True, separators=(',', ':')).encode()
	assert encode(actual) == encode(expected)
