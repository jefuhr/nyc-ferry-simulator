"""Failures: incomplete responses, duplicate relations, corrupt frames, nondeterminism and simplified policy geometry."""
import json
from pathlib import Path
import numpy as np
import pytest
import rasterio
from rasterio.transform import from_bounds
from shapely.geometry import box, mapping, shape, LineString
from ferrydata import geodesy, manifest
from ferrydata.buildings.build import build, read_features
from ferrydata.buildings.validate import validate
from ferrydata.world3d.build import digest, write_json, read_json
from ferrydata.buildings.geometry import geographic


@pytest.fixture
def inputs(tmp_path):
	harbor,cache = tmp_path/'harbor',tmp_path/'raw'
	cache.mkdir()
	entries=[]
	for j in (0,1):
		folder=harbor/'tiles'/f'tile_0_{j}'
		folder.mkdir(parents=True)
		write_json(folder/'land_areas.geojson',{'type':'FeatureCollection','features':[{'type':'Feature','properties':{},'geometry':mapping(geographic(box(j*4000,0,(j+1)*4000,4000)))}]})
		for name in ('landing_structures','aids','bridges'):
			write_json(folder/f'{name}.geojson',{'type':'FeatureCollection','features':[]})
		pts={}
		for k,(n,e) in {'sw':(-250,j*4000-250),'se':(-250,(j+1)*4000+250),'nw':(4250,j*4000-250),'ne':(4250,(j+1)*4000+250)}.items():
			pts[k]={'north_m':n,'east_m':e,'navd88_zero_ellipsoidal_height_m':-32.}
		write_json(folder/'geoid_offset.json',{'points':pts})
		with rasterio.open(folder/'depth_merged_navd88.tif','w',driver='GTiff',width=16,height=16,count=1,dtype='float32',crs='EPSG:4326',transform=from_bounds(-74.1,40.6,-73.8,40.9,16,16)) as ds:
			ds.write(np.ones((16,16),dtype=np.float32)*4,1)
		write_json(folder/'shoreline_constructions.geojson',{'type':'FeatureCollection','features':[{'type':'Feature','properties':{'feature_class':'COALNE'},'geometry':mapping(geographic(LineString([(0,0),(0,4000)])))}]})
		entries.append({'i':0,'j':j,'active':True})
	write_json(harbor/'index.json',{'schema':'harbor-tiles-v1','tiles':entries})
	rows=[{'doitt_id':'1','height_roof':'100','ground_elevation':'20','feature_code':'2100','the_geom':mapping(geographic(box(3990,100,4010,120)))}]
	write_json(cache/'nyc.json',rows)
	m=manifest.Manifest.load('buildings',directory=cache)
	m.record('nyc.json','https://data.cityofnewyork.us/fixture',manifest.sha256_file(cache/'nyc.json'),(cache/'nyc.json').stat().st_size,retrieved_utc='2026-01-01T00:00:00Z')
	m.save()
	write_json(cache/'inventory.json',{'schema':'harbor-building-raw-v1','active':[[0,0],[0,1]],'files':[{'file':'nyc.json','source':'nyc','rows':1}]})
	return harbor,cache


def test_writer_validation_repeat_and_corruption(inputs,tmp_path):
	h,c=inputs
	a,b=tmp_path/'a',tmp_path/'b'
	build(h,c,a,log=lambda _:None)
	build(h,c,b,log=lambda _:None)
	assert digest(a)==digest(b)
	v=validate(h,c,a,repeat_dir=b)
	assert v['determinism']['checked'] and v['determinism']['identical']
	assert v['passed'],v
	idx=json.loads((a/'index.json').read_text())
	assert idx['counts']['buildings']==1
	assert idx['counts']['parts']==2
	assert len(idx['tiles'])==2
	mesh=next(a.rglob('*.glb'))
	mesh.write_bytes(mesh.read_bytes()+b'bad')
	assert not validate(h,c,a)['passed']


def test_raw_hash_mismatch_fails_before_build(inputs,tmp_path):
	h,c=inputs
	(c/'nyc.json').write_text('[]')
	with pytest.raises(ValueError,match='hash mismatch'):
		build(h,c,tmp_path/'out',log=lambda _:None)


def test_refuses_unowned_output_directory(inputs,tmp_path):
	h,c=inputs
	out=tmp_path/'keep'
	out.mkdir()
	(out/'precious.txt').write_text('keep')
	with pytest.raises(ValueError,match='refusing'):
		build(h,c,out,log=lambda _:None)
	assert (out/'precious.txt').read_text()=='keep'


def test_osm_relation_suppresses_tagged_members_and_keeps_courtyard(tmp_path):
	from ferrydata.buildings.build import osm_features
	def ring(points):
		return [{'lon':x,'lat':y} for x,y in points]
	outer=ring([(0,0),(2,0),(2,2),(0,2),(0,0)])
	inner=ring([(.5,.5),(.5,1.5),(1.5,1.5),(1.5,.5),(.5,.5)])
	elements=[{'type':'way','id':1,'geometry':outer,'tags':{'building':'yes'}},
		{'type':'relation','id':2,'tags':{'type':'multipolygon','building':'yes'},'members':[
			{'type':'way','ref':1,'role':'outer','geometry':outer},{'type':'way','ref':3,'role':'inner','geometry':inner}]}]
	features=list(osm_features(elements))
	assert len(features)==1
	assert shape(features[0]['geometry']).area==3


def test_validate_rejects_wrong_tile_transform_even_with_refreshed_hash(inputs,tmp_path):
	from ferrydata.world3d.build import file_records
	h,c=inputs
	out=tmp_path/'out'
	build(h,c,out,log=lambda _:None)
	p=out/'tiles/tile_0_0/tileset.json'
	tree=json.loads(p.read_text())
	tree['root']['transform'][12]+=1
	write_json(p,tree)
	idx=json.loads((out/'index.json').read_text())
	idx['files']=file_records(out)
	write_json(out/'index.json',idx)
	v=validate(h,c,out)
	assert not v['passed']
	assert any('frame transform' in e for e in v['errors'])


def test_shared_seam_uses_terrain_owner_even_when_geoids_disagree(inputs,tmp_path):
	from ferrydata.world3d import frames,glb
	from ferrydata.buildings.build import read_attributes
	h,c=inputs
	p=h/'tiles/tile_0_1/geoid_offset.json'
	d=json.loads(p.read_text())
	for point in d['points'].values():
		point['navd88_zero_ellipsoidal_height_m']=-31.5
	write_json(p,d)
	out=tmp_path/'out'
	build(h,c,out,log=lambda _:None)
	v=validate(h,c,out)
	assert v['passed'],v['errors']
	seams=[]
	for j in (0,1):
		folder=out/'tiles'/f'tile_0_{j}'
		a=read_attributes(folder)[0]
		_,arr=glb.decode((folder/a['mesh']).read_bytes())
		ecef=frames.ecef_from_content(arr['POSITION'],frames.TileFrame(0,j).transform())
		lat,lon,_=geodesy.from_ecef(ecef[:,0],ecef[:,1],ecef[:,2])
		n,e=geodesy.to_local(lat,lon)
		pts=ecef[np.abs(e-4000)<.001]
		seams.append(pts)
	assert len(seams[0]) and len(seams[1])
	for point in seams[0]:
		assert np.min(np.linalg.norm(seams[1]-point,axis=1))<.001


def test_cached_build_is_validated_without_hiding_incomplete_nj(inputs,tmp_path):
	h,c=inputs
	inv=json.loads((c/'inventory.json').read_text())
	inv['coverage']={'complete':False,'missing':['tile_0_0/c0_0'],'chunks':[]}
	write_json(c/'inventory.json',inv)
	out=tmp_path/'out'
	build(h,c,out,log=lambda _:None)
	v=validate(h,c,out)
	assert v['passed'],v['errors']
	assert not v['coverage']['complete']
	assert v['coverage']['missing']==['tile_0_0/c0_0']


def test_osm_id_deduplicates_across_subchunk_files(inputs):
	h,c=inputs
	from ferrydata.buildings.sources import pins
	inv=read_json(c/'inventory.json')
	m=pins(c)
	way={'type':'way','id':42,'tags':{'building':'apartments','height':'36'},'geometry':[
		{'lon':-74.,'lat':40.7},{'lon':-73.9999,'lat':40.7},{'lon':-73.9999,'lat':40.7001},
		{'lon':-74.,'lat':40.7001},{'lon':-74.,'lat':40.7}]}
	for name in ('osm_sub0.json','osm_sub1.json'):
		write_json(c/name,{'elements':[way]})
		m.record(name,'https://overpass-api.de/fixture',manifest.sha256_file(c/name),(c/name).stat().st_size)
		inv['files'].append({'file':name,'source':'osm-nj','rows':1})
	m.save();write_json(c/'inventory.json',inv)
	features,_,_=read_features(c)
	assert sum(f['id']=='osm-nj:way:42' for f in features)==1


def test_build_records_explicit_water_view_policy_and_omissions(inputs,tmp_path):
	from shapely.geometry import LineString
	h,c=inputs
	shore={'type':'FeatureCollection','features':[{'type':'Feature','properties':{'feature_class':'COALNE'},'geometry':mapping(geographic(LineString([(0,0),(0,4000)])))}]}
	for j in (0,1):write_json(h/'tiles'/f'tile_0_{j}'/'shoreline_constructions.geojson',shore)
	rows=read_json(c/'nyc.json')
	for identifier,east,height_ft in [('2',2000,'30'),('3',2500,'140')]:
		rows.append({'doitt_id':identifier,'height_roof':height_ft,'ground_elevation':'20','feature_code':'2100','the_geom':mapping(geographic(box(east,100,east+20,120)))})
	write_json(c/'nyc.json',rows)
	m=manifest.Manifest.load('buildings',directory=c)
	m.record('nyc.json','https://data.cityofnewyork.us/fixture',manifest.sha256_file(c/'nyc.json'),(c/'nyc.json').stat().st_size)
	m.save()
	b,c_out=tmp_path/'b',tmp_path/'c'
	build(h,c,b,log=lambda _:None,policy='B')
	build(h,c,c_out,log=lambda _:None,policy='C')
	idx=read_json(b/'index.json');c_idx=read_json(c_out/'index.json')
	assert idx['selection']['shoreline_distance_m']==600
	assert idx['counts']['buildings']==2
	assert c_idx['selection']['inland_minimum_height_above_ground_m']==40
	assert c_idx['counts']['buildings']==1
	assert idx['counts']['selection_omitted_by_source']=={'nyc':1}
	assert c_idx['counts']['selection_omitted_by_source']=={'nyc':2}
	assert validate(h,c,b)['passed']


@pytest.mark.parametrize('ground_elevation',['20',None])
def test_build_selects_original_shoreward_projection_before_simplifying(inputs,tmp_path,ground_elevation):
	# A shallow projection crosses 600 m but disappears at the render tolerance.
	from shapely.geometry import Polygon
	from ferrydata.buildings import geometry,selection
	from ferrydata.buildings.build import read_attributes
	h,c=inputs
	original=Polygon([(600.1,100),(610,100),(610,110),(600.1,110),
		(600.1,106),(599.9,105),(600.1,104)])
	rows=[{'doitt_id':'projection','height_roof':'30','ground_elevation':ground_elevation,
		'feature_code':'2100','the_geom':mapping(geographic(original))}]
	write_json(c/'nyc.json',rows)
	m=manifest.Manifest.load('buildings',directory=c)
	m.record('nyc.json','https://data.cityofnewyork.us/fixture',manifest.sha256_file(c/'nyc.json'),(c/'nyc.json').stat().st_size)
	m.save()
	shore=selection.from_harbor(h)
	repaired=geometry.local(shape(rows[0]['the_geom']))
	assert shore.distance(repaired)==pytest.approx(599.9,abs=1e-5)
	assert shore.distance(repaired.simplify(geometry.SIMPLIFY_M,preserve_topology=True))==pytest.approx(600.1,abs=1e-5)
	out=tmp_path/'out'
	build(h,c,out,log=lambda _:None,policy='B')
	idx=read_json(out/'index.json')
	assert idx['counts']['by_source']=={'nyc':1}
	assert idx['counts']['selection_omitted_by_source']=={}
	attrs=read_attributes(out/'tiles/tile_0_0')
	assert [a['id'] for a in attrs]==['nyc:projection']
	assert shape(attrs[0]['footprint']).bounds[0]==pytest.approx(600.1,abs=.001)
	if ground_elevation is None:
		point=repaired.representative_point()
		assert attrs[0]['ground_sample_ne_m']==pytest.approx([point.y,point.x],abs=1e-7)
	assert validate(h,c,out)['passed']


def test_building_center_outside_region_does_not_drop_its_clipped_mass(inputs,tmp_path):
	h,c=inputs
	rows=read_json(c/'nyc.json')
	rows.append({'doitt_id':'4','height_roof':'100','ground_elevation':'20','feature_code':'2100','the_geom':mapping(geographic(box(7900,100,8900,200)))})
	write_json(c/'nyc.json',rows)
	m=manifest.Manifest.load('buildings',directory=c)
	m.record('nyc.json','https://data.cityofnewyork.us/fixture',manifest.sha256_file(c/'nyc.json'),(c/'nyc.json').stat().st_size)
	m.save()
	out=tmp_path/'out'
	result=build(h,c,out,log=lambda _:None)
	assert result['buildings']==2
	assert validate(h,c,out)['passed']


def test_validator_recounts_sources_and_refuses_policy_metadata_drift(inputs,tmp_path):
	h,c=inputs;out=tmp_path/'out'
	build(h,c,out,log=lambda _:None)
	idx=read_json(out/'index.json')
	idx['counts']['by_source']={'osm-nj':1}
	write_json(out/'index.json',idx)
	v=validate(h,c,out)
	assert not v['passed'] and any('source count' in e for e in v['errors'])
	idx['counts']['by_source']={'nyc':1}
	idx['selection']['shoreline_distance_m']=700
	write_json(out/'index.json',idx)
	v=validate(h,c,out)
	assert not v['passed'] and any('selection policy' in e for e in v['errors'])
