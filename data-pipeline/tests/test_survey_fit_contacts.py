"""Full cached-harbor integration: stop-scoped contact bytes, no peer leakage.

Run under heavy.lock. Build baseline and candidate in pytest temporary directories;
never rewrite the real processed pack and never modify a frozen consumer.
"""
import hashlib
import json
import shutil

from ferrydata import geojsonio, landings, paths, survey, tiles
from ferrydata.export_unity import contact_records

CHANGED_STOPS = {'4','8','11','17','18','19','20','23','24','25','87','89','90',
	'112','113','114','115','118','137','138','141'}
MOVED_STOPS = {'4','18','20','25','89','90','112','113','115','118','138','141'}


def encoded(records):
	return json.dumps(records,sort_keys=True,separators=(',',':')).encode()


def test_exact_changed_contact_stop_set_and_unchanged_governors_island(tmp_path,monkeypatch):
	root = paths.PROCESSED_ROOT
	index = json.loads((root/'harbor/index.json').read_text())
	measurements = survey.load()
	target = tmp_path/'processed'
	all_before,all_after = {},{}
	physical_before,physical_after = {},{}
	for source in sorted((root/'harbor/tiles').glob('*/landing_faces.geojson')):
		if not geojsonio.read_collection(source)['features']:
			continue
		out = target/'harbor/tiles'/source.parent.name;out.mkdir(parents=True)
		for file in source.parent.glob('*.geojson'):
			shutil.copyfile(file,out/file.name)
		tile = tiles.get_tile(source.parent.name,index['enc_cells'])
		with monkeypatch.context() as m:
			m.setattr(paths,'PROCESSED_ROOT',target)
			m.setattr(paths,'ais_processed_dir',lambda:root/'ais')
			for stops,destination,physical in (({},all_before,physical_before),(measurements,all_after,physical_after)):
				m.setattr(survey,'load',lambda:stops)
				landings.build_tile(tile,log=lambda _:None)
				contacts = contact_records(geojsonio.read_collection(out/'landing_structures.geojson'))
				for sid in {r['stop_id'] for r in contacts}:
					rows=[r for r in contacts if r['stop_id']==sid]
					destination[sid]=encoded(rows)
					physical[sid]=encoded([dict(start=r['start_ne_m'],end=r['end_ne_m'],normal=r['water_normal_ne']) for r in sorted(rows,key=lambda r:r['id'])])
	assert set(all_before)==set(all_after)
	changed={s for s in all_before if all_before[s]!=all_after[s]}
	moved={s for s in physical_before if physical_before[s]!=physical_after[s]}
	artifact=paths.REPO_ROOT/'artifacts/landing-angles/contact-parity.json';artifact.parent.mkdir(parents=True,exist_ok=True)
	artifact.write_text(json.dumps(dict(changed=sorted(changed),physical_changed=sorted(moved),
		stops={s:dict(baseline_sha256=hashlib.sha256(all_before[s]).hexdigest(),candidate_sha256=hashlib.sha256(all_after[s]).hexdigest()) for s in sorted(all_before)}),indent=2)+'\n')
	assert changed==CHANGED_STOPS
	assert moved==MOVED_STOPS
	assert all_before['111']==all_after['111']
	for sid in set(all_before)-CHANGED_STOPS:
		assert all_before[sid]==all_after[sid]
