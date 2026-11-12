"""Crew GPS support must be complete, outward by at least 1 m, and stop-scoped."""
import json
import shutil

import pytest
from shapely.geometry import LineString

from ferrydata import geojsonio, landings, paths, survey, tiles
from ferrydata.export_unity import contact_records


def points(*distances):
	return [dict(name=f'Slip {i + 1}', point_ne_m=[distance, 5.]) for i, distance in enumerate(distances)]


def test_every_assigned_point_at_least_one_metre_outward_has_explicit_provenance():
	proof = survey.crew_slip_evidence(LineString([(0., 0.), (10., 0.)]), (1., 0.), points(1., 2.))
	assert proof == {'source': 'crew-reported: project crew reviewer, slip positions 2026-11',
		'slip_names': ['Slip 1', 'Slip 2'], 'minimum_signed_distance_m': 1.}


@pytest.mark.parametrize('distances', [(1., .9), (1., -2.), ()])
def test_incomplete_or_insufficient_outward_support_gives_no_crew_flag(distances):
	assert survey.crew_slip_evidence(LineString([(0., 0.), (10., 0.)]), (1., 0.), points(*distances)) is None


def test_committed_survey_restores_only_three_faces_without_changing_chart_flags(tmp_path, monkeypatch):
	root = paths.PROCESSED_ROOT
	index = json.loads((root / 'harbor/index.json').read_text())
	target = tmp_path / 'processed'
	before, after = {}, {}
	apply = survey.apply_crew_slip_report
	for source in sorted((root / 'harbor/tiles').glob('*/landing_faces.geojson')):
		if not geojsonio.read_collection(source)['features']:
			continue
		out = target / 'harbor/tiles' / source.parent.name; out.mkdir(parents=True)
		for file in source.parent.glob('*.geojson'):
			shutil.copyfile(file, out / file.name)
		tile = tiles.get_tile(source.parent.name, index['enc_cells'])
		with monkeypatch.context() as m:
			m.setattr(paths, 'PROCESSED_ROOT', target)
			m.setattr(paths, 'ais_processed_dir', lambda: root / 'ais')
			for fn, destination in ((lambda *_: None, before), (apply, after)):
				m.setattr(survey, 'apply_crew_slip_report', fn)
				landings.build_tile(tile, log=lambda _: None)
				rows = contact_records(geojsonio.read_collection(out / 'landing_structures.geojson'))
				for sid in {r['stop_id'] for r in rows}:
					destination[sid] = [r for r in rows if r['stop_id'] == sid]
	encode = lambda rows: json.dumps(rows, sort_keys=True, separators=(',', ':')).encode()
	assert {sid for sid in before if encode(before[sid]) != encode(after[sid])} == {'4', '24', '138'}
	for sid in before:
		assert [(r['id'], r['water_side_verified_by_pipeline']) for r in before[sid]] == [(r['id'], r['water_side_verified_by_pipeline']) for r in after[sid]]
		if sid not in {'4', '24', '138'}:
			assert encode(before[sid]) == encode(after[sid])
		else:
			primary = [r for r in after[sid] if r.get('primary_face')]
			assert primary
			for record in primary:
				assert record['water_side_crew_reported'] is True
				assert record['water_side_verified_by_pipeline'] is False
				assert record['crew_evidence']['slip_names']
				assert record['crew_evidence']['minimum_signed_distance_m'] >= 1.
