"""Raw-byte runtime parity and optional survey metadata on the existing compact
harbor fixture (two tiles, one stop); no full harbor data is needed.
"""
import hashlib
import json

from test_runtime import harbor, load, write
from ferrydata import paths
from ferrydata.harbor_runtime import build, verify


def test_empty_survey_keeps_all_runtime_bytes_and_digest(harbor, tmp_path, monkeypatch):
	manifests = tmp_path / 'manifests'; manifests.mkdir()
	monkeypatch.setattr(paths, 'MANIFEST_DIR', manifests)
	one = tmp_path / 'one'; two = tmp_path / 'two'
	build(harbor, one)
	write(manifests / 'landing-survey.json', dict(schema_version=1, purpose='fixture', stops={}))
	build(harbor, two)
	assert {p.name: p.read_bytes() for p in one.iterdir()} == {p.name: p.read_bytes() for p in two.iterdir()}
	assert (one / 'DIGEST').read_bytes() == (two / 'DIGEST').read_bytes()


def test_runtime_reads_local_survey_fields_from_structures_without_manifest(harbor, tmp_path, monkeypatch):
	monkeypatch.setattr(paths, 'MANIFEST_DIR', tmp_path / 'absent-manifests')
	file = harbor / 'tiles/tile_0_0/landing_structures.geojson'
	structures = load(file.parent, file.name)
	for f in structures['features']:
		f['properties']['survey_local'] = dict(gangways=[dict(shore_hinge_ne_m=[1, 2], barge_hinge_ne_m=[3, 4])],
			piles=[dict(centre_ne_m=[5, 6])], canopy=dict(centre_ne_m=[7, 8], length_m=9, width_m=3),
			shore=[dict(kind='platform', geometry_ne_m=dict(type='Polygon', coordinates=[[[0, 0], [0, 1], [1, 1], [0, 0]]]))])
		f['properties']['survey_berths'] = [dict(id='barge', order=0, alias='Review slip', landed_heading=1.2)]
	write(file, structures)
	index = load(harbor, 'index.json')
	for pin in index['tiles'][0]['outputs']:
		if pin['file'] == file.relative_to(harbor).as_posix():
			pin['sha256'] = hashlib.sha256(file.read_bytes()).hexdigest()
	write(harbor / 'index.json', index)
	out = tmp_path / 'runtime'
	build(harbor, out); verify(out, harbor)
	data = load(out, 'landings.json')
	assert data['barges'][0]['gangways'][0]['barge_hinge_ne_m'] == [3, 4]
	assert data['barges'][0]['piles'][0]['centre_ne_m'] == [5, 6]
	assert data['barges'][0]['canopy']['length_m'] == 9
	assert data['barges'][0]['shore'][0]['kind'] == 'platform'
	assert data['stops'][0]['berths'][0]['landed_heading'] == 1.2
	assert data['stops'][0]['berths'][0]['alias'] == 'Review slip'
