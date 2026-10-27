"""Failures: HTTP-200 error payloads, hash drift, uncached network repeats and changed query identity."""
import json
import pytest
from ferrydata import manifest
from ferrydata.buildings import sources


def test_overpass_partial_success_is_not_cached(tmp_path,monkeypatch):
	def bad(url,path):
		path.write_text(json.dumps({'elements':[],'remark':'runtime error: Query timed out'}))
		return path.stat().st_size
	monkeypatch.setattr(sources.fetcher,'_download',bad)
	with pytest.raises(ValueError,match='incomplete'):
		sources.download(tmp_path,'test.json','https://overpass-api.de/api/interpreter?data=fixture',kind='overpass',pause=0)
	assert not (tmp_path/'test.json').exists()
	assert not (tmp_path/'test.json.part').exists()
	assert not sources.pins(tmp_path).files


def test_download_pins_url_timestamp_sha_and_uses_cache(tmp_path,monkeypatch):
	calls=[]
	def get(url,path):
		calls.append(url)
		path.write_text('[]')
		return 2
	monkeypatch.setattr(sources.fetcher,'_download',get)
	url='https://data.cityofnewyork.us/resource/5zhs-2jue.json?fixture=1'
	assert sources.download(tmp_path,'test.json',url,kind='nyc',pause=0)==[]
	entry=sources.pins(tmp_path).files['test.json']
	assert entry['url']==url and entry['retrieved_utc'] and entry['sha256']==manifest.sha256_file(tmp_path/'test.json')
	assert sources.download(tmp_path,'test.json',url,kind='nyc',pause=0)==[]
	assert calls==[url]


def test_changed_query_cannot_silently_reuse_old_response(tmp_path,monkeypatch):
	def get(url,path):
		path.write_text('[]')
		return 2
	monkeypatch.setattr(sources.fetcher,'_download',get)
	sources.download(tmp_path,'test.json','https://data.cityofnewyork.us/first',kind='nyc',pause=0)
	with pytest.raises(ValueError,match='URL changed'):
		sources.download(tmp_path,'test.json','https://data.cityofnewyork.us/second',kind='nyc',pause=0)


def test_polite_policy_bounds_retries_and_request_count(monkeypatch,tmp_path):
	calls=[]
	def unavailable(url,path,**kwargs):
		calls.append(kwargs)
		raise RuntimeError('HTTP 502')
	monkeypatch.setattr(sources.time,'sleep',lambda _:None)
	policy=sources.RequestPolicy(max_requests=3)
	with pytest.raises(RuntimeError,match='502'):
		policy.download(unavailable,'https://overpass-api.de/api/interpreter',tmp_path/'part')
	assert len(calls)==3
	assert all(c['retries']==1 for c in calls)
	with pytest.raises(RuntimeError,match='request cap'):
		policy.download(unavailable,'https://overpass-api.de/api/interpreter',tmp_path/'part')
	assert len(calls)==3


def test_nj_boundary_skips_eastern_chunks():
	from shapely.geometry import box
	assert sources.needs_nj(box(-74.2,40.5,-74.,40.9),(-74.1,40.6,-74.05,40.7))
	assert not sources.needs_nj(box(-74.2,40.5,-74.,40.9),(-73.99,40.6,-73.9,40.7))


def test_mirror_keeps_original_endpoint_pin_and_rejects_query_change(tmp_path):
	query='[out:json];way[building](0,0,1,1);out geom;'
	original=sources.OVERPASS+'?'+sources.urlencode({'data':query})
	m=sources.pins(tmp_path)
	m.record('osm.json',original,'a'*64,0,retrieved_utc='2026-01-01T00:00:00Z')
	m.save()
	assert sources.overpass_url(tmp_path,'osm.json',query,sources.APPROVED_MIRROR)==original
	assert sources.overpass_url(tmp_path,'new.json',query,sources.APPROVED_MIRROR).startswith(sources.APPROVED_MIRROR)
	with pytest.raises(ValueError,match='URL changed'):
		sources.overpass_url(tmp_path,'osm.json',query+' ',sources.APPROVED_MIRROR)


def test_osm_snapshot_recorded_without_rewriting_retrieval_time(tmp_path,monkeypatch):
	payload={'elements':[],'osm3s':{'timestamp_osm_base':'2026-10-26T00:00:00Z'}}
	def get(url,path):
		path.write_text(json.dumps(payload))
		return path.stat().st_size
	monkeypatch.setattr(sources.fetcher,'_download',get)
	url=sources.OVERPASS+'?fixture=1'
	sources.download(tmp_path,'osm.json',url,kind='overpass',pause=0)
	entry=sources.pins(tmp_path).files['osm.json']
	assert entry['osm_base']==payload['osm3s']['timestamp_osm_base']
	assert entry['endpoint']==sources.OVERPASS
	sources.download(tmp_path,'osm.json',url,kind='overpass',pause=0)
	assert sources.pins(tmp_path).files['osm.json']==entry


def test_land_filter_ignores_zero_area_chart_objects():
	from shapely.geometry import GeometryCollection,Point,LineString,box
	land=GeometryCollection([box(-74.1,40.6,-74.,40.7),Point(-73.9,40.8),LineString([(-74.2,40.5),(-73.8,40.9)])])
	boundary=box(-74.3,40.4,-73.7,41.)
	assert sources.needs_nj(boundary,(-74.1,40.6,-74.,40.7),land)
	assert not sources.needs_nj(boundary,(-73.91,40.79,-73.89,40.81),land)


def test_subchunk_fallback_shares_three_attempt_budget_and_records_actual_url(monkeypatch,tmp_path):
	seen=[]
	def get(url,path,**kwargs):
		seen.append(url)
		if len(seen)==1:raise RuntimeError('HTTP 502')
		path.write_text('{}')
		return 2
	monkeypatch.setattr(sources.time,'sleep',lambda _:None)
	policy=sources.RequestPolicy()
	url=sources.OVERPASS+'?data=fixture'
	assert policy.download(get,url,tmp_path/'part')==2
	assert seen==[url,sources.APPROVED_MIRROR+'?data=fixture']
	assert policy.effective_urls[url]==seen[-1]
	assert policy.requests==2


def test_capped_subchunk_failure_publishes_explicit_missing_coverage_then_stops(monkeypatch,tmp_path):
	from shapely.geometry import box,mapping
	monkeypatch.setattr(sources,'active_tiles',lambda _:([(0,0)],{}))
	monkeypatch.setattr(sources,'fetch_nyc',lambda *a,**k:[])
	monkeypatch.setattr(sources,'nj_boundary',lambda *a,**k:box(-75,40,-73,42))
	monkeypatch.setattr(sources,'read_json',lambda _: {'features':[{'geometry':mapping(box(-75,40,-73,42))}]})
	calls=[]
	def unavailable(cache,name,url,**kwargs):
		calls.append(name)
		raise RuntimeError('capped public-server failure')
	monkeypatch.setattr(sources,'download',unavailable)
	result=sources.fetch_overpass(tmp_path/'harbor',tmp_path/'cache',log=lambda _:None)
	assert not result['coverage']['complete']
	assert len(result['coverage']['missing'])==16
	assert len(calls)==1
	assert (tmp_path/'cache'/'inventory.json').is_file()
