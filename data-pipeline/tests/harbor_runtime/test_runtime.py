"""Failure modes: seam duplicates/conflicts, name-only bridge merges, lost berth/back edges,
noncanonical datum seams, invalid/outside query geometry, stale input hashes, tampering,
path escapes, and timestamps/order leaking into deterministic output bytes.
"""
import copy
import hashlib
import json
from pathlib import Path

import numpy as np
import pytest
from shapely.geometry import shape

from ferrydata.harbor_runtime import build, verify
from ferrydata.harbor_runtime.geometry import sample_geoid


def write(path, value):
	path.parent.mkdir(parents=True, exist_ok=True)
	path.write_text(json.dumps(value) + '\n')


def feature(id_, type_, coordinates, **props):
	return dict(type='Feature', geometry=dict(type=type_, coordinates=coordinates), local_coordinates=coordinates,
		properties=dict(id=id_, **props))


@pytest.fixture
def harbor(tmp_path):
	root = tmp_path / 'harbor'
	entries = []
	for j in (0, 1):
		tile = f'tile_0_{j}'
		directory = root / 'tiles' / tile
		entry = dict(id=tile, i=0, j=j, active=True, bounds_local_m=[0, j*4000, 4000, (j+1)*4000],
			grown_bounds_local_m=[-250, j*4000-250, 4250, (j+1)*4000+250], stop_ids=['s'] if j == 0 else [], outputs=[])
		aid = feature('aid-seam', 'Point', [2000, 4000], lights=[dict(characteristic='Fl', period_s=4., sequence=[dict(state='light', seconds=.4)])])
		bridges = [feature('span-a', 'Polygon', [[[100,3990],[200,3990],[200,4001],[100,4001],[100,3990]]], name='same',
			vertical_clearance_m=12., clearance_datum='MHW', horizontal_clearance_m=20.)]
		if j:
			bridges += [feature('span-b', 'Polygon', [[[200,3990],[300,3990],[300,4001],[200,4001],[200,3990]]], name='same', vertical_clearance_m=None, clearance_datum='MHW'),
				feature('other', 'Polygon', [[[500,5000],[600,5000],[600,5010],[500,5010],[500,5000]]], name='same', vertical_clearance_m=None, clearance_datum='MHW')]
		front = feature('unused', 'LineString', [[1000,1000],[1000,1010],[1020,1010],[1020,1000]],
			stop_id='s', stop_name='landing', structure_id='barge', polyline_id='front', role='landing', source='estimated',
			segments=[dict(id=1, index=1, kind='berth-face', confidence='estimated')])
		back = feature('unused', 'LineString', [[1020,1000],[1000,1000]], stop_id='s', stop_name='landing',
			structure_id='barge', polyline_id='back', role='landing', source='estimated', segments=[dict(id=2, index=0, kind='barge-side', confidence='estimated')])
		for name, features in [('aids',[aid]),('bridges',bridges),('landing_structures',[front,back] if not j else []),
			('land_areas',[feature('land','Polygon',[[[0,0],[0,100],[100,100],[100,0],[0,0]]])]),
			('shoreline_constructions',[feature('pier','LineString',[[100,100],[100,110]], feature_class='SLCONS')])]:
			write(directory / (name+'.geojson'), dict(type='FeatureCollection', features=features))
		points = {}
		for key,n,e in [('sw',-250,j*4000-250),('se',-250,(j+1)*4000+250),('nw',4250,j*4000-250),('ne',4250,(j+1)*4000+250)]:
			points[key] = dict(north_m=n,east_m=e,navd88_zero_ellipsoidal_height_m=-32+n*1e-5+e*2e-5)
		write(directory/'geoid_offset.json',dict(points=points,geoid_model='fixture'))
		for path in sorted(directory.iterdir()):
			entry['outputs'].append(dict(file=path.relative_to(root).as_posix(),sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
		entries.append(entry)
	write(root/'index.json',dict(schema='harbor-tiles-v1',origin_latlon=[40.7,-74.015], tile_size_m=4000, margin_m=250, tiles=entries,
		stops=[dict(stop_id='s',stop_name='landing',tile='tile_0_0')]))
	write(root/'bridge_clearance_audit.json',dict(report_only=True,group_distance_m=2.,physical_bridges=[dict(ids=['span-a','span-b'],names=['same']),dict(ids=['other'],names=['same'])]))
	write(root/'unity/contacts.json',[dict(id=1,stop_id='s',stop_name='landing',structure_id='barge',berth_face=True,primary_face=True,
		berth_name='main',berth_order=0,berth_face_segment_ids=[1],start_ne_m=[1000,1010],end_ne_m=[1020,1010],berth_use_crew_note='fixture')])
	write(root/'3dtiles/index.json',dict(schema='harbor-3dtiles-v1',sources=[]))
	write(root/'env/depth/manifest.json',dict(format='ferrysim-env-depth/1',grids=[]))
	body=(root/'env/depth/manifest.json').read_bytes()
	(root/'env/depth/DIGEST').write_text(hashlib.sha256(b'manifest.json\0'+body+b'\0').hexdigest()+'\n')
	return root


def load(root,name):
	return json.loads((root/name).read_text())


def test_seam_aid_once_preserves_character(harbor):
	out=harbor/'runtime'; build(harbor,out)
	aids=load(out,'aids.json')['aids']
	assert len(aids)==1
	assert aids[0]['id']=='enc:aid-seam'
	assert aids[0]['properties']['lights'][0]['sequence']==[dict(state='light',seconds=.4)]


def test_physical_groups_not_names_and_no_clearance_inheritance(harbor):
	out=harbor/'runtime'; build(harbor,out)
	groups=load(out,'bridges.json')['bridges']
	assert len(groups)==2
	group=next(g for g in groups if len(g['spans'])==2)
	assert {s['source_id'] for s in group['spans']}=={'span-a','span-b'}
	assert next(s for s in group['spans'] if s['source_id']=='span-b')['clearances'][0]['vertical_clearance_m'] is None


def test_full_barge_closes_using_back_and_berth_metadata(harbor):
	out=harbor/'runtime'; build(harbor,out)
	landings=load(out,'landings.json')
	assert len(landings['barges'])==1
	barge=landings['barges'][0]
	assert shape(barge['geometry_ne_m']).area==200
	assert barge['geometry_ne_m']['coordinates'][0][0]==barge['geometry_ne_m']['coordinates'][0][-1]
	berth=landings['stops'][0]['berths'][0]
	assert berth['structure_id']==barge['id']=='barge'
	assert berth['berth_order']==0 and berth['berth_face_segment_ids']==[1]
	assert berth['berth_use_crew_note']=='fixture'
	assert (out/'contacts.json').read_bytes()==(harbor/'unity/contacts.json').read_bytes()


def test_missing_back_is_rejected_instead_of_closed_by_guess(harbor):
	p=harbor/'tiles/tile_0_0/landing_structures.geojson'; v=load(p.parent,p.name); v['features'].pop(); write(p,v)
	with pytest.raises(ValueError,match='hash|outline'):
		build(harbor,harbor/'runtime')


def test_canonical_geoid_continuity_and_planar_parity(harbor):
	out=harbor/'runtime'; build(harbor,out)
	for north in (0.,2000.,4000.):
		left=sample_geoid(out,north,4000.-1e-6); right=sample_geoid(out,north,4000.+1e-6)
		assert abs(left-right)<1e-9
		assert sample_geoid(out,north,4000.)==pytest.approx(-32+north*1e-5+4000*2e-5,abs=1e-12)
	with pytest.raises(ValueError,match='outside'):
		sample_geoid(out,-1.,4000.)


def test_query_footprints_valid_inside_domain_and_separate_semantics(harbor):
	out=harbor/'runtime'; build(harbor,out)
	data=load(out,'footprints.json'); domain=shape(data['coverage_ne_m'])
	assert {f['semantic'] for f in data['footprints']}=={'shoreline','structure','bridge','barge'}
	for f in data['footprints']:
		g=shape(f['geometry_ne_m']); assert not g.is_empty and g.is_valid and domain.covers(g)
		assert f['collision_authority'] is False


@pytest.mark.parametrize('name',['aids.json','manifest.json','geoid.f64','DIGEST'])
def test_tamper_detected(harbor,name):
	out=harbor/'runtime'; build(harbor,out)
	p=out/name; p.write_bytes(p.read_bytes()+b' ')
	with pytest.raises(ValueError,match='digest|hash|DIGEST'):
		verify(out)


def test_verifier_rejects_unlisted_file(harbor):
	out=harbor/'runtime'; build(harbor,out); (out/'surprise').write_text('x')
	with pytest.raises(ValueError,match='inventory'):
		verify(out)


def test_build_twice_identical_and_input_unchanged(harbor,tmp_path):
	before={p.relative_to(harbor):p.read_bytes() for p in harbor.rglob('*') if p.is_file()}
	one=tmp_path/'one'; two=tmp_path/'two'; build(harbor,one); build(harbor,two)
	assert {p.name:p.read_bytes() for p in one.iterdir()}=={p.name:p.read_bytes() for p in two.iterdir()}
	assert before=={p.relative_to(harbor):p.read_bytes() for p in harbor.rglob('*') if p.is_file()}
	assert verify(one,harbor)['format']=='ferrysim-harbor-runtime/1'


def test_input_tamper_rejected(harbor):
	p=harbor/'tiles/tile_0_1/aids.geojson'; p.write_bytes(p.read_bytes()+b' ')
	with pytest.raises(ValueError,match='hash'):
		build(harbor,harbor/'runtime')


def test_no_output_over_input(harbor):
	with pytest.raises(ValueError,match='output'):
		build(harbor,harbor/'tiles')


def repin(harbor,path):
	index=load(harbor,'index.json')
	for entry in index['tiles']:
		for output in entry['outputs']:
			if output['file']==path.relative_to(harbor).as_posix():
				output['sha256']=hashlib.sha256(path.read_bytes()).hexdigest()
	write(harbor/'index.json',index)


def test_conflicting_aid_seam_is_rejected_even_with_valid_pin(harbor):
	p=harbor/'tiles/tile_0_1/aids.geojson'; data=load(p.parent,p.name)
	data['features'][0]['properties']['lights'][0]['period_s']=10.; write(p,data); repin(harbor,p)
	with pytest.raises(ValueError,match='conflicting seam'):
		build(harbor,harbor/'runtime')


def test_incomplete_barge_rejected_even_with_valid_pin(harbor):
	p=harbor/'tiles/tile_0_0/landing_structures.geojson'; data=load(p.parent,p.name)
	data['features'].pop(); write(p,data); repin(harbor,p)
	with pytest.raises(ValueError,match='closed outline'):
		build(harbor,harbor/'runtime')


def test_shared_barge_has_multiple_ordered_berths_without_duplicate_mesh(harbor):
	p=harbor/'unity/contacts.json'; contacts=load(p.parent,p.name)
	second=copy.deepcopy(contacts[0]); second.update(id=2,berth_name='back',berth_order=1,berth_face_segment_ids=[2]); contacts.append(second)
	write(p,contacts); out=harbor/'runtime'; build(harbor,out)
	data=load(out,'landings.json')
	assert len(data['barges'])==1
	assert [b['berth_name'] for b in data['stops'][0]['berths']]==['main','back']


def test_unknown_height_and_unknown_pier_inventory_stay_explicit(harbor):
	out=harbor/'runtime'; build(harbor,out)
	for bridge in load(out,'bridges.json')['bridges']:
		for span in bridge['spans']:
			assert span['vertical_bounds_navd88_m']==[None,None]
			assert span['pier_inventory_complete'] is False
			assert all(c['underside_navd88_m'] is None for c in span['clearances'])


def test_piers_require_charted_pier_category_and_geometric_membership(harbor):
	p=harbor/'tiles/tile_0_0/shoreline_constructions.geojson'
	write(p,dict(features=[feature('touching','Polygon',[[[100,3990],[101,3990],[101,3991],[100,3991],[100,3990]]],feature_class='SLCONS',category_code=4),
		feature('far','Point',[2000,2000],feature_class='SLCONS',category_code=4),
		feature('not-pier','Point',[150,3995],feature_class='SLCONS',category_code=1)])); repin(harbor,p)
	out=harbor/'runtime'; build(harbor,out)
	group=next(g for g in load(out,'bridges.json')['bridges'] if len(g['spans'])==2)
	assert [p['id'] for p in group['piers']]==['enc:touching']
	assert group['piers'][0]['membership_evidence'][0]['method'].startswith('charted SLCONS')


def test_geoid_hole_is_not_interpolated_as_water(harbor):
	index=load(harbor,'index.json'); third=copy.deepcopy(index['tiles'][1]); third.update(id='tile_0_3',j=3,bounds_local_m=[0,12000,4000,16000])
	# Leave a complete inactive 4 km tile between the two pieces of coverage.
	old=harbor/'tiles/tile_0_1'; new=harbor/'tiles/tile_0_3'
	import shutil
	shutil.copytree(old,new)
	geo=load(new,'geoid_offset.json')
	for point in geo['points'].values():
		point['east_m']+=8000
	write(new/'geoid_offset.json',geo)
	third['outputs']=[dict(file=p.relative_to(harbor).as_posix(),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted(new.iterdir())]
	index['tiles'].append(third); write(harbor/'index.json',index)
	out=harbor/'runtime'; build(harbor,out)
	with pytest.raises(ValueError,match='outside'):
		sample_geoid(out,1000,10000)


def test_manifest_path_escape_rejected(harbor):
	index=load(harbor,'index.json'); index['tiles'][0]['id']='../../../escape'; write(harbor/'index.json',index)
	with pytest.raises(ValueError,match='unsafe|tile'):
		build(harbor,harbor/'runtime')


def test_verify_sources_detects_changed_bridge_audit(harbor):
	out=harbor/'runtime'; build(harbor,out)
	p=harbor/'bridge_clearance_audit.json'; p.write_bytes(p.read_bytes()+b' ')
	with pytest.raises(ValueError,match='source hash'):
		verify(out,harbor)


def test_build_failure_preserves_previous_pack(harbor):
	out=harbor/'runtime'; build(harbor,out)
	before={p.name:p.read_bytes() for p in out.iterdir()}
	p=harbor/'tiles/tile_0_1/aids.geojson'; p.write_bytes(p.read_bytes()+b' ')
	with pytest.raises(ValueError):
		build(harbor,out)
	assert before=={p.name:p.read_bytes() for p in out.iterdir()}


def test_revetment_is_not_a_bridge_pier(harbor):
	p=harbor/'tiles/tile_0_0/shoreline_constructions.geojson'
	write(p,dict(features=[feature('revetment','Point',[150,3995],feature_class='SLCONS',category_code=9)])); repin(harbor,p)
	out=harbor/'runtime'; build(harbor,out)
	assert not any(g['piers'] for g in load(out,'bridges.json')['bridges'])


def test_tile_bounds_must_match_the_frame(harbor):
	index=load(harbor,'index.json'); index['tiles'][0]['bounds_local_m'][0]=-250.; write(harbor/'index.json',index)
	with pytest.raises(ValueError,match='tile.*bounds|bounds.*tile'):
		build(harbor,harbor/'runtime')


def resign(out,name,value):
	from ferrydata.harbor_runtime.pack import digest
	write(out/name,value)
	manifest=load(out,'manifest.json'); manifest['files'][name]=hashlib.sha256((out/name).read_bytes()).hexdigest()
	write(out/'manifest.json',manifest); (out/'DIGEST').write_text(digest(out)+'\n')


def test_structural_verification_catches_outside_polygon_after_resigning(harbor):
	out=harbor/'runtime'; build(harbor,out)
	data=load(out,'footprints.json'); data['footprints'][0]['geometry_ne_m']=dict(type='Polygon',coordinates=[[[-10,-10],[-5,-10],[-5,-5],[-10,-5],[-10,-10]]])
	resign(out,'footprints.json',data)
	with pytest.raises(ValueError,match='outside'):
		verify(out)


def test_structural_verification_checks_all_counts(harbor):
	from ferrydata.harbor_runtime.pack import digest
	out=harbor/'runtime'; build(harbor,out)
	manifest=load(out,'manifest.json'); manifest['counts']['aids']=9000
	write(out/'manifest.json',manifest); (out/'DIGEST').write_text(digest(out)+'\n')
	with pytest.raises(ValueError,match='count'):
		verify(out)


def test_source_verification_checks_depth_bytes(harbor):
	p=harbor/'env/depth/test.f32'; p.write_bytes(np.array([-5.],dtype='<f4').tobytes())
	m=load(harbor/'env/depth','manifest.json'); m['grids']=[dict(file='test.f32',sha256=hashlib.sha256(p.read_bytes()).hexdigest())]
	write(p.parent/'manifest.json',m)
	from ferrydata.environment.pack import depth_digest
	(p.parent/'DIGEST').write_text(depth_digest(p.parent)+'\n')
	out=harbor/'runtime'; build(harbor,out); p.write_bytes(b'tampered')
	with pytest.raises(ValueError,match='depth|hash'):
		verify(out,harbor)


def test_nonmatching_source_tile_geoids_still_have_one_continuous_seam(harbor):
	p=harbor/'tiles/tile_0_1/geoid_offset.json'; data=load(p.parent,p.name)
	for point in data['points'].values():
		point['navd88_zero_ellipsoidal_height_m']+=.01
	write(p,data); repin(harbor,p)
	out=harbor/'runtime'; build(harbor,out)
	left=sample_geoid(out,2000.,4000.-1e-6); right=sample_geoid(out,2000.,4000.+1e-6)
	assert abs(left-right)<1e-9


def test_digest_framing_matches_existing_environment_packs(harbor):
	from ferrydata.environment.pack import _file_digest
	from ferrydata.harbor_runtime.pack import FILES
	out=harbor/'runtime'; build(harbor,out)
	assert (out/'DIGEST').read_text()==_file_digest(out,['manifest.json',*FILES])+'\n'


# Review regressions: reversed N/E water normals, lost hole winding or canonical order,
# footprint re-normalization, and valid hashes masking unreadable geoid metadata.
def assert_water_side(polygon):
	import math
	from shapely.geometry import Point, Polygon
	for index,ring in enumerate((polygon.exterior, *polygon.interiors)):
		for (n0,e0),(n1,e1) in zip(ring.coords, list(ring.coords)[1:]):
			dn,de=n1-n0,e1-e0; length=math.hypot(dn,de)
			assert length>0
			# Literal ContactSegment.WaterNormal in north/east order, 1 mm into water.
			point=Point((n0+n1)/2+.001*de/length,(e0+e1)/2-.001*dn/length)
			assert not polygon.covers(point)
			if index:
				# Interior ring water is inside the hole, opposite exterior winding.
				assert Polygon(ring).contains(point)


def review_polygon():
	from shapely.geometry import Polygon
	return Polygon([(10,10),(30,10),(30,25),(22,25),(22,19),(16,19),(16,30),(10,30)],
		[[(12,12),(15,12),(15,16),(13,17),(12,16)]])


def test_asymmetric_barge_water_normals_and_recanonicalization():
	from ferrydata.harbor_runtime.geometry import canonical
	polygon=review_polygon()
	one=canonical(polygon,solid_right=True)
	assert_water_side(shape(one))
	assert canonical(shape(one),solid_right=True)==one
	# Reversal and different ring starts must not change canonical bytes/order.
	from shapely.geometry import Polygon
	def shifted(ring):
		points=list(ring.coords)[:-1]
		return (points[2:]+points[:2])[::-1]
	variant=Polygon(shifted(polygon.exterior),[shifted(r) for r in polygon.interiors])
	assert canonical(variant,solid_right=True)==one


def test_barge_component_order_is_canonical():
	from shapely.geometry import MultiPolygon
	from shapely.affinity import translate
	from ferrydata.harbor_runtime.geometry import canonical
	left=review_polygon(); right=translate(left,xoff=50)
	one=canonical(MultiPolygon([left,right]),solid_right=True)
	assert one==canonical(MultiPolygon([right,left]),solid_right=True)
	assert one==canonical(shape(one),solid_right=True)
	for polygon in shape(one).geoms:
		assert_water_side(polygon)


def test_footprints_preserve_barge_water_side():
	from ferrydata.harbor_runtime.build import footprints
	from ferrydata.harbor_runtime.geometry import canonical
	polygon=review_polygon()
	barge=dict(id='concave',geometry_ne_m=canonical(polygon,solid_right=True))
	for _ in range(2):
		data=footprints([dict(bounds_local_m=[0,0,100,100])],
			{'land_areas.geojson':[], 'shoreline_constructions.geojson':[]},
			{'bridges':[]},{'barges':[barge]})
		geometry=data['footprints'][0]['geometry_ne_m']
		assert_water_side(shape(geometry))
		assert geometry==barge['geometry_ne_m']
		barge['geometry_ne_m']=geometry


def test_built_barges_water_side_in_both_payloads(harbor):
	out=harbor/'runtime'; build(harbor,out)
	for barge in load(out,'landings.json')['barges']:
		assert_water_side(shape(barge['geometry_ne_m']))
	for item in load(out,'footprints.json')['footprints']:
		if item['semantic']=='barge':
			assert_water_side(shape(item['geometry_ne_m']))


@pytest.mark.parametrize('name',['landings.json','footprints.json'])
def test_verify_rejects_reversed_barge_after_resigning(harbor,name):
	out=harbor/'runtime'; build(harbor,out)
	data=load(out,name)
	record=data['barges'][0] if name=='landings.json' else next(f for f in data['footprints'] if f['semantic']=='barge')
	record['geometry_ne_m']['coordinates'][0].reverse()
	resign(out,name,data)
	with pytest.raises(ValueError,match='barge.*orientation'):
		verify(out)


@pytest.mark.parametrize('field,value',[
	('mask_shape',[1,1]),('mask_shape',[16.,32.]),('mask_shape',[True,32]),
	('mask_shape',None),('mask_shape',[16,32,1]),
	('rows',17.),('rows',True),('rows','17'),('rows',1),
	('cols',33.),('cols',False),('cols',None),
	('format','ferrysim-harbor-geoid/2'),('layout','column-major'),
	('values_type','big-endian float64'),
])
def test_verify_rejects_unreadable_geoid_metadata_after_resigning(harbor,tmp_path,field,value):
	import shutil
	out=harbor/'runtime'; build(harbor,out)
	copy=tmp_path/'malformed'; shutil.copytree(out,copy)
	metadata=load(copy,'geoid.json'); metadata[field]=value
	resign(copy,'geoid.json',metadata)
	with pytest.raises(ValueError,match='geoid schema'):
		verify(copy)
