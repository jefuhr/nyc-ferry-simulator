"""The explicitly approved Geofabrik NJ snapshot, verified once and read with the existing GDAL OSM driver."""
import hashlib
import json
import re
from pathlib import Path
import pandas as pd
import pyogrio
from shapely.geometry import mapping
from ferrydata import fetcher,paths
from ferrydata.buildings import sources

URL='https://download.geofabrik.de/north-america/us/new-jersey-latest.osm.pbf'
NAME='geofabrik/new-jersey-latest.osm.pbf'
_HSTORE=re.compile(r'\s*"((?:[^"\\]|\\.)*)"\s*=>\s*(?:"((?:[^"\\]|\\.)*)"|(NULL))\s*(?:,|$)')


def verify_md5(path, checksum):
	match=re.match(r'^\s*([0-9a-fA-F]{32})(?:\s|$)',checksum)
	if not match:
		raise ValueError('invalid Geofabrik MD5 document')
	digest=hashlib.md5()
	with Path(path).open('rb') as stream:
		for chunk in iter(lambda:stream.read(1<<20),b''):
			digest.update(chunk)
	if digest.hexdigest()!=match[1].lower():
		raise ValueError('Geofabrik PBF MD5 mismatch; rejecting download')
	return digest.hexdigest()


def _fetch(cache,name,url,validate=None,extra=None,force=False):
	m=sources.pins(cache)
	original_download=fetcher._download
	original_open=fetcher.urllib.request.urlopen
	original_agent=fetcher.USER_AGENT
	details={'provider':'Geofabrik','license':'ODbL 1.0',**(extra or {})}
	def opened(*args,**kwargs):
		response=original_open(*args,**kwargs)
		details['last_modified']=response.headers.get('Last-Modified')
		return response
	try:
		fetcher.USER_AGENT='ferrydata-buildings/0.1 (NYC Ferry Simulator; one verified NJ snapshot)'
		fetcher._download=lambda u,p:original_download(u,p,timeout=300,retries=1)
		fetcher.urllib.request.urlopen=opened
		state=fetcher.fetch_file(m,name,url,Path(cache)/name,validate=validate,extra=details,force=force)
	finally:
		fetcher._download=original_download
		fetcher.urllib.request.urlopen=original_open
		fetcher.USER_AGENT=original_agent
	return state


def fetch(cache=None,log=print,force=False):
	cache=Path(cache or sources.cache_default())
	checksum_name=NAME+'.md5'
	_fetch(cache,checksum_name,URL+'.md5',validate=lambda p:verify_checksum_document(p),force=force)
	checksum=(cache/checksum_name).read_text(encoding='utf-8')
	state=_fetch(cache,NAME,URL,validate=lambda p:verify_md5(p,checksum),extra={'md5_document':checksum_name},force=force)
	# Check again on cache hits as well; the manifest's SHA-256 is checked by fetch_file.
	digest=verify_md5(cache/NAME,checksum)
	m=sources.pins(cache)
	entry=m.files[NAME]
	if not entry.get('last_modified'):
		raise ValueError('Geofabrik response lacks Last-Modified; stop rather than invent a snapshot time')
	entry['md5']=digest
	m.save()
	log(f"[buildings] Geofabrik {state}: {entry['size_bytes']} bytes, Last-Modified {entry['last_modified']}, SHA-256 {entry['sha256']}")
	return entry


def verify_checksum_document(path):
	if not re.match(r'^\s*[0-9a-fA-F]{32}(?:\s|$)',Path(path).read_text(encoding='utf-8')):
		raise ValueError('invalid Geofabrik MD5 document')


def other_tags(value):
	if isinstance(value,dict):
		return dict(value)
	if value is None or (not isinstance(value,str) and pd.isna(value)):
		return {}
	if not value:
		return {}
	if value.lstrip().startswith('{'):
		return json.loads(value)
	result={};pos=0
	while pos<len(value):
		match=_HSTORE.match(value,pos)
		if match is None:
			raise ValueError(f'malformed OSM other_tags at byte {pos}')
		unescape=lambda text:re.sub(r'\\(.)',r'\1',text)
		result[unescape(match[1])]=unescape(match[2]) if match[2] is not None else None
		pos=match.end()
	return result


def row_feature(row):
	way=row.get('osm_way_id')
	is_way=way is not None and not pd.isna(way) and str(way)!=''
	identifier=way if is_way else row.get('osm_id')
	if identifier is None or pd.isna(identifier):
		raise ValueError('OSM multipolygon has neither osm_way_id nor osm_id')
	tags=other_tags(row.get('other_tags'))
	tags['building']=row.get('building')
	# Retain only the building attributes the pipeline needs, never POI/user/contact tags.
	tags={k:v for k,v in tags.items() if k in ('height','building:levels','building:use','building','use') and v is not None}
	return {'id':f"osm-nj:{'way' if is_way else 'relation'}:{identifier}",'source':'osm-nj',
		'geometry':mapping(row['geometry']),'properties':tags}


def read_layer(path,**kwargs):
	"""Keep GDAL's temporary indexes in this task's artifacts, with a bounded in-memory database."""
	folder=paths.REPO_ROOT/'artifacts'/'skyline-data'/'gdal-tmp'
	folder.mkdir(parents=True,exist_ok=True)
	options={'CPL_TMPDIR':str(folder),'OSM_MAX_TMPFILE_SIZE':'128'}
	previous={key:pyogrio.get_gdal_config_option(key) for key in options}
	try:
		pyogrio.set_gdal_config_options(options)
		return pyogrio.read_dataframe(path,layer='multipolygons',TAGS_FORMAT='JSON',**kwargs)
	finally:
		pyogrio.set_gdal_config_options(previous)


def boundary(path):
	import shapely
	frame=read_layer(path,where="boundary='administrative' AND admin_level='4'",columns=['osm_id','other_tags'])
	polygons=[r['geometry'] for r in frame.to_dict('records') if other_tags(r.get('other_tags')).get('ISO3166-2')=='US-NJ']
	if not polygons:
		raise ValueError('Geofabrik snapshot has no complete NJ administrative boundary')
	result=shapely.union_all(polygons)
	if not result.is_valid or result.is_empty:
		raise ValueError('Geofabrik NJ administrative boundary is invalid')
	return result


def features(path,bbox):
	"""Read multipolygons with a support envelope, then let the pipeline clip exactly to active world tiles."""
	if pyogrio.list_drivers().get('OSM')!='r':
		raise ValueError('existing pyogrio GDAL OSM reader is unavailable')
	frame=read_layer(path,bbox=tuple(bbox),where="building IS NOT NULL AND building <> 'no'",columns=['osm_id','osm_way_id','building','other_tags'])
	for row in frame.to_dict('records'):
		if row['geometry'] is None or row['geometry'].is_empty:
			raise ValueError('GDAL returned an empty building geometry')
		yield row_feature(row)


def _crosscheck(old_inventory,cache,bulk_features,boundary):
	import shapely
	from shapely.geometry import shape,box
	from ferrydata.buildings import geometry as geo
	from ferrydata.buildings.build import osm_features
	from ferrydata.world3d.build import read_json
	m=sources.pins(cache)
	covered=[p for p in old_inventory.get('coverage',{}).get('chunks',[]) if p.get('file') and p['file'].endswith('.json')]
	old_elements={}
	for name in sorted({p['file'] for p in covered}):
		if not m.verify(name,Path(cache)/name):
			raise ValueError(f'Overpass cross-check pin differs: {name}')
		for element in read_json(Path(cache)/name)['elements']:
			key=(element['type'],element['id'])
			if key in old_elements and old_elements[key]!=element:
				raise ValueError(f'conflicting Overpass cross-check snapshots: {key}')
			old_elements[key]=element
	old=[f for f in osm_features(list(old_elements.values())) if 'error' not in f and boundary.covers(shape(f['geometry']).representative_point())]
	sets=[]
	for features_in in (old,bulk_features):
		geoms=[geo.local(shape(f['geometry'])) for f in features_in]
		geoms=[g if g.is_valid else shapely.make_valid(g) for g in geoms]
		sets.append((features_in,geoms,shapely.STRtree(geoms)))
	rows=[]
	for p in covered:
		match=re.fullmatch(r'tile_(-?\d+)_(-?\d+)/c(\d)_(\d)',p['id'])
		i,j,row,col=map(int,match.groups())
		square=box(j*4000+col*1000,i*4000+row*1000,j*4000+(col+1)*1000,i*4000+(row+1)*1000)
		members=[]
		for features_in,geoms,tree in sets:
			members.append({features_in[k]['id']:features_in[k] for k in tree.query(square) if geoms[k].intersection(square).area>.01})
		a,b=members
		changed=[]
		for identifier in sorted(a.keys()&b.keys()):
			before=geo.dimensions('osm-nj',a[identifier]['properties'],0)
			after=geo.dimensions('osm-nj',b[identifier]['properties'],0)
			if before['height_m']!=after['height_m'] or before['height_basis']!=after['height_basis']:
				changed.append({'id':identifier,'before_m':before['height_m'],'after_m':after['height_m'],
					'before_basis':before['height_basis'],'after_basis':after['height_basis']})
		rows.append({'id':p['id'],'overpass_file':p['file'],'overpass_osm_base':m.files[p['file']].get('osm_base'),
			'overpass_count':len(a),'bulk_count':len(b),'count_delta':len(b)-len(a),'common_ids':len(a.keys()&b.keys()),
			'height_changes':changed,'only_overpass':sorted(a.keys()-b.keys()),'only_bulk':sorted(b.keys()-a.keys())})
	return {'rule':'Original footprints with positive area overlap of each exact 1 km core, filtered by the uniform bulk NJ state boundary; heights use common fallbacks.',
		'notes':'Different snapshots, regional-extract completeness and Overpass node/bbox selection may affect counts; differences are reported, never hidden.',
		'chunks':rows}


def fetch_inventory(harbor_dir=None,cache=None,force=False,log=print):
	import shapely
	from shapely.geometry import shape
	from ferrydata import tiles
	from ferrydata.world3d.build import active_tiles,read_json,write_json
	cache=Path(cache or sources.cache_default())
	harbor=Path(harbor_dir or paths.processed_dir('harbor'))
	active,_=active_tiles(harbor)
	old=read_json(cache/'inventory.json') if (cache/'inventory.json').exists() else {}
	# Preserve the original Overpass comparison receipt on an idempotent re-fetch.
	previous_overpass=old.get('overpass_inventory',old)
	inventory=sources.fetch_nyc(cache,active,force,log=log)
	entry=fetch(cache,log,force=force)
	pbf=cache/NAME
	nj=boundary(pbf)
	bbox=tiles.extent().bbox
	data=[f for f in features(pbf,bbox) if nj.covers(shape(f['geometry']).representative_point())]
	if not data:
		raise ValueError('Geofabrik GDAL read returned no NJ buildings; refusing complete-coverage claim')
	log(f'[buildings] uniform NJ snapshot: {len(data)} building polygons inside harbor read bbox')
	plans=[]
	for i,j in active:
		land=shapely.union_all([shape(f['geometry']) for f in read_json(harbor/'tiles'/f'tile_{i}_{j}'/'land_areas.geojson')['features'] if f.get('geometry')])
		for row in range(4):
			for col in range(4):
				bounds=tiles._square(i*4000+row*1000,j*4000+col*1000,i*4000+(row+1)*1000,j*4000+(col+1)*1000).bounds
				if sources.needs_nj(nj,bounds,land):
					plans.append({'id':f'tile_{i}_{j}/c{row}_{col}','bbox':list(bounds),'file':NAME,'status':'available'})
	crosscheck=_crosscheck(previous_overpass,cache,data,nj)
	support=['nyc-metadata.json',NAME+'.md5']+sorted({r['overpass_file'] for r in crosscheck['chunks']})
	inventory.append({'file':NAME,'source':'osm-nj','format':'osm-pbf','read_bbox':list(bbox),'rows':len(data)})
	document={'schema':'harbor-building-raw-v1','nyc_dataset':sources.NYC_ID,'active':active,'files':inventory,'support_files':support,
		'coverage':{'complete':True,'nj_source':'One Geofabrik PBF snapshot, MD5 and SHA-256 verified, same-snapshot NJ state boundary',
			'last_modified':entry['last_modified'],'nj_subchunk_size_m':1000,'chunks':plans,'missing':[],'failure':None},
		'overpass_crosscheck':crosscheck,'overpass_inventory':previous_overpass}
	write_json(cache/'inventory.json',document)
	return document
