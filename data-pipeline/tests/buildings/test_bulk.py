"""Failures: corrupt bulk downloads, hstore escapes, way/relation ID confusion and GDAL bbox truncation."""
import hashlib
import pytest
from ferrydata.buildings.bulk import verify_md5,other_tags,row_feature


def test_bulk_md5_rejects_damaged_bytes(tmp_path):
	p=tmp_path/'sample.pbf';p.write_bytes(b'fixture PBF bytes')
	verify_md5(p,hashlib.md5(p.read_bytes()).hexdigest()+'  sample.pbf\n')
	with pytest.raises(ValueError,match='MD5'):
		verify_md5(p,'0'*32+'  sample.pbf')


def test_hstore_preserves_colons_units_and_escaped_quotes():
	tags=other_tags(r'"height"=>"100 ft","building:levels"=>"8","building:use"=>"office","note"=>"a \"quote\""')
	assert tags['height']=='100 ft'
	assert tags['building:levels']=='8'
	assert tags['building:use']=='office'
	assert tags['note']=='a "quote"'
	assert other_tags(None)=={}
	assert other_tags({'height':'20'})=={'height':'20'}
	with pytest.raises(ValueError,match='other_tags'):
		other_tags('malformed')


def test_bulk_way_and_relation_ids_stay_distinct():
	from shapely.geometry import box
	row={'osm_id':None,'osm_way_id':'42','building':'apartments','other_tags':'"height"=>"100","building:levels"=>"30"','geometry':box(-74.04,40.71,-74.039,40.711)}
	assert row_feature(row)['id']=='osm-nj:way:42'
	assert row_feature(row)['properties']['height']=='100'
	assert row_feature({**row,'osm_id':'42','osm_way_id':None})['id']=='osm-nj:relation:42'


def test_existing_gdal_reader_preserves_crossing_footprint_and_relation_identity(tmp_path):
	from ferrydata.buildings.bulk import features
	p=tmp_path/'buildings.osm'
	p.write_text('''<?xml version="1.0" encoding="UTF-8"?>
<osm version="0.6" generator="ferrydata fixture">
<node id="1" lat="40.7" lon="-74.04"/><node id="2" lat="40.7" lon="-74.03"/>
<node id="3" lat="40.71" lon="-74.03"/><node id="4" lat="40.71" lon="-74.04"/>
<way id="42"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/>
<tag k="building" v="yes"/><tag k="height" v="100"/></way>
<relation id="77"><member type="way" ref="42" role="outer"/><tag k="type" v="multipolygon"/>
<tag k="building" v="apartments"/><tag k="height" v="110"/><tag k="building:levels" v="30"/></relation>
</osm>''')
	rows=list(features(p,[-74.0401,40.6999,-74.0399,40.7001]))
	assert len(rows)==1
	assert rows[0]['id']=='osm-nj:relation:77'
	assert rows[0]['properties']['height']=='110'
	assert rows[0]['properties']['building:levels']=='30'
	assert rows[0]['geometry']['coordinates'][0][0]  # Driver returned complete geometry, not only in-bbox nodes.
	from shapely.geometry import shape
	assert shape(rows[0]['geometry']).area==pytest.approx(.0001)


def test_one_bulk_download_captures_headers_checksum_and_pins_cache(tmp_path,monkeypatch):
	import io
	from ferrydata.buildings import bulk,sources
	payload=b'literal bulk test payload'
	md5=hashlib.md5(payload).hexdigest()
	calls=[]
	class Response(io.BytesIO):
		def __init__(self,data):
			super().__init__(data)
			self.headers={'Content-Length':str(len(data)),'Last-Modified':'Mon, 26 Oct 2026 05:21:09 GMT'}
	def opened(request,**kwargs):
		calls.append(request.full_url)
		return Response((md5+'  new-jersey-latest.osm.pbf\n').encode() if request.full_url.endswith('.md5') else payload)
	monkeypatch.setattr(bulk.fetcher.urllib.request,'urlopen',opened)
	entry=bulk.fetch(tmp_path,log=lambda _:None)
	assert calls==[bulk.URL+'.md5',bulk.URL]
	assert entry['md5']==md5 and entry['last_modified']=='Mon, 26 Oct 2026 05:21:09 GMT'
	assert entry['sha256']==hashlib.sha256(payload).hexdigest()
	bulk.fetch(tmp_path,log=lambda _:None)
	assert calls==[bulk.URL+'.md5',bulk.URL]
	assert sources.pins(tmp_path).files[bulk.NAME]==entry
