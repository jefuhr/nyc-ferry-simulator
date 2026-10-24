import io
import json
from urllib.parse import parse_qs, urlparse

import pytest

from calibration import inputs


@pytest.mark.parametrize('cached', [False, True])
def test_api_default_bin_preserves_dimond_reef_response_and_metadata(tmp_path, monkeypatch, cached):
	# Recorded processed station shape: metadata depths do not establish the API default.
	station = dict(id='NYH1919', name='Dimond Reef', kind='current_prediction',
		station_type='harmonic', series='6-minute', depths_m=[3.35, 5.49, 9.45],
		in_slice=False, file='currents/NYH1919.csv', rows=720,
		lat=40.697898864746094, lon=-74.02130126953125, north_m=-233.308, east_m=-532.572)
	(tmp_path / 'coops_stations.json').write_text(json.dumps({'stations': [station]}))
	monkeypatch.setattr(inputs.paths, 'processed_dir', lambda _: tmp_path)
	monkeypatch.setattr(inputs, 'CACHE', tmp_path / 'raw')
	monkeypatch.setattr(inputs, 'MANIFEST', tmp_path / 'manifest.json')
	monkeypatch.setattr(inputs.ais, 'WEEKS', ['2024-10-07'])
	data = (b'Time,Depth,Velocity_Major,meanFloodDir,meanEbbDir,Bin\n'
		b'2024-10-07 00:00,9.4,17.4,23.0,279.0,9\n'
		b'2024-10-07 00:06,9.4,18.0,23.0,279.0,9\n')
	urls = []
	def response(url, **kwargs):
		assert not cached, 'verified cache must not fetch replacements'
		urls.append(url)
		return io.BytesIO(data)
	monkeypatch.setattr(inputs, 'urlopen', response)
	if cached:
		inputs.CACHE.mkdir()
		path = inputs.CACHE / 'NYH1919_2024-10-07.csv'
		path.write_bytes(data)
		inputs.MANIFEST.write_text(json.dumps({'files': [dict(name=path.name,
			url=inputs.coops.currents_url(station['id'], inputs.SimpleNamespace(
				window_start='2024-10-07', window_end='2024-10-14')),
			station=station['id'], week='2024-10-07', retrieved_utc='original',
			sha256=inputs.digest(path), size_bytes=len(data), rows=2, depth_m=[9.4])]}))
	inputs.fetch_currents()
	manifest = json.loads(inputs.MANIFEST.read_text())
	assert manifest['stations'] == [station]
	assert manifest['bin_selection'] == 'api_default'
	assert manifest['files'][0]['depth_m'] == [9.4]
	assert manifest['files'][0]['bin'] == [9]
	assert 'bin' not in parse_qs(urlparse(manifest['files'][0]['url']).query)
	assert 'shallowest selection not verified' in ' '.join(manifest['limitations'])
	assert (inputs.CACHE / 'NYH1919_2024-10-07.csv').read_bytes() == data
	if cached:
		assert manifest['files'][0]['retrieved_utc'] == 'original'
	else:
		assert len(urls) == 1
