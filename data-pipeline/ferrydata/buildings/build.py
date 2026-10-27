"""Deterministic LoD1 skyline tiles beside world3d terrain, with per-building semantic sidecars."""
import json
import math
import gzip
import io
import shutil
import resource
import time
import sys
from collections import Counter, defaultdict
from pathlib import Path
import numpy as np
import shapely
from shapely.geometry import Polygon, shape, mapping, box
from shapely.ops import polygonize
from ferrydata import geodesy, manifest, paths
from ferrydata.buildings import geometry as geo, sources as raw, selection
from ferrydata.world3d import frames, heights, glb, mesh
from ferrydata.world3d.build import active_tiles, read_json, write_json, file_records, box as mesh_box, content_region

SCHEMA = 'harbor-buildings-3dtiles-v1'
MAX_TOTAL_BYTES = 100_000_000
MAX_TILE_BYTES = 25_000_000
MAX_CHUNK_TRIANGLES = 250_000
EXCLUSION_M = 5.


def default_output():
	return paths.processed_dir('harbor')/'3dtiles-buildings'


def compact(path, data):
	payload=(json.dumps(data,sort_keys=True,separators=(',',':'),allow_nan=False)+'\n').encode('utf-8')
	buffer=io.BytesIO()
	with gzip.GzipFile(filename='',fileobj=buffer,mode='wb',mtime=0) as stream:
		stream.write(payload)
	Path(path).write_bytes(buffer.getvalue())


def read_attributes(folder):
	return json.loads(gzip.decompress((Path(folder)/'attributes.json.gz').read_bytes()))['buildings']


def osm_features(elements):
	member_ids = {m['ref'] for e in elements if e['type']=='relation' for m in e.get('members',[]) if m['type']=='way'}
	for e in elements:
		if e['type']=='way':
			if e['id'] in member_ids:
				continue
			points = [(p['lon'],p['lat']) for p in e.get('geometry',[])]
			if len(points)<4 or points[0]!=points[-1]:
				yield {'id':f"osm-nj:way:{e['id']}",'error':'open-or-incomplete-ring'}
				continue
			p=Polygon(points)
		elif e['type']=='relation':
			from shapely.geometry import LineString
			lines={'outer':[],'inner':[]}
			for m in e.get('members',[]):
				role=m.get('role') or 'outer'
				points=[(p['lon'],p['lat']) for p in m.get('geometry',[]) if p]
				if role in lines and len(points)>=2:
					lines[role].append(LineString(points))
			outer=shapely.union_all(list(polygonize(lines['outer'])))
			inner=shapely.union_all(list(polygonize(lines['inner'])))
			p=outer.difference(inner)
			if p.is_empty:
				yield {'id':f"osm-nj:relation:{e['id']}",'error':'empty-or-incomplete-relation'}
				continue
		else:
			continue
		yield {'id':f"osm-nj:{e['type']}:{e['id']}",'source':'osm-nj','geometry':mapping(p),'properties':e.get('tags',{})}


def read_features(cache):
	inv,pins=raw.verified_inventory(cache)
	features=[]
	# Merge OSM elements before relation-member suppression across chunk boundaries.
	osm={}
	for entry in inv['files']:
		if entry.get('format')=='osm-pbf':
			from ferrydata.buildings import bulk
			p=Path(cache)/entry['file']
			boundary=bulk.boundary(p)
			features.extend(f for f in bulk.features(p,entry['read_bbox']) if boundary.covers(shape(f['geometry']).representative_point()))
			continue
		d=read_json(Path(cache)/entry['file'])
		if entry['source']=='nyc':
			for row in d:
				features.append({'id':f"nyc:{row['doitt_id']}",'source':'nyc','geometry':row['the_geom'],
					'properties':{k:v for k,v in row.items() if k not in ('the_geom','name','base_bbl','mappluto_bbl','bin')}})
		else:
			for element in d['elements']:
				key=(element['type'],element['id'])
				if key in osm and osm[key]!=element:
					raise ValueError(f'conflicting OSM snapshot for {key}; refetch a consistent snapshot')
				osm[key]=element
	features.extend(osm_features([osm[k] for k in sorted(osm)]))
	return geo.deduplicate(features),inv,pins


def land_and_exclusions(folder):
	def features(name):
		return read_json(folder/f'{name}.geojson')['features']
	land=shapely.union_all([p for f in features('land_areas') if f.get('geometry') for p in geo.polygons(geo.local(shape(f['geometry'])))])
	if not land.is_valid:
		land=shapely.make_valid(land)
	excluded=[]
	for name in ('landing_structures','aids','bridges'):
		for f in features(name):
			if f.get('geometry'):
				excluded.append(geo.local(shape(f['geometry'])).buffer(EXCLUSION_M))
	return land,shapely.union_all(excluded)


def check_output(out):
	if out.exists() and any(out.iterdir()):
		marker=out/'index.json'
		if not marker.is_file() or read_json(marker).get('schema')!=SCHEMA:
			raise ValueError(f'refusing to replace unowned output {out}')


def build(harbor_dir=None,cache=None,out_dir=None,log=print,policy=selection.DEFAULT_POLICY):
	started=time.perf_counter()
	harbor=Path(harbor_dir or paths.processed_dir('harbor'))
	cache=Path(cache or raw.cache_default())
	out=Path(out_dir or default_output())
	check_output(out)
	active,hindex=active_tiles(harbor)
	features,inv,pins=read_features(cache)
	if sorted(tuple(ij) for ij in inv['active'])!=active:
		raise ValueError('raw inventory active tiles differ from harbor; run fetch again')
	sources=heights.Sources(harbor,active)
	shore=selection.from_harbor(harbor) if policy!='A' else None
	selection_record=selection.policy_record(policy)
	omitted=defaultdict(set)
	renderable=defaultdict(set)
	selected_flags=[]
	geometries=[]
	normalized=[]
	rejected=Counter()
	pending=defaultdict(list)
	entries=[]
	active_area=shapely.union_all([box(j*heights.SIZE_M,i*heights.SIZE_M,(j+1)*heights.SIZE_M,(i+1)*heights.SIZE_M) for i,j in active])
	input_count=len(features)
	input_by_source=dict(sorted(Counter(f.get('source','osm-nj') for f in features).items()))
	for f in features:
		if 'error' in f:
			rejected[f['error']]+=1
			continue
		if f['source']=='nyc' and str(f['properties'].get('feature_code','2100')) not in ('2100','5110'):
			rejected['nyc-nonbuilding-or-construction']+=1
			continue
		g=geo.local(shape(f['geometry']))
		if not g.is_valid:
			g=shapely.make_valid(g)
		g=shapely.union_all(list(geo.polygons(g)))
		if g.is_empty:
			rejected['invalid-or-empty-footprint']+=1
			continue
		# Keep the repaired original for policy inputs, including the estimated ground sample.
		p=g.representative_point()
		home=(math.floor(p.y/heights.SIZE_M),math.floor(p.x/heights.SIZE_M))
		if home in sources.owners.active:
			i,j=home
		else:
			oi,oj=sources.owners.owner(np.array([p.y]),np.array([p.x]))
			i,j=int(oi[0]),int(oj[0])
		if i==heights.NO_TILE:
			inside=g.intersection(active_area)
			if inside.is_empty or inside.area<=.01:
				rejected['outside-active-tiles']+=1
				continue
			# A large building may cross the boundary with its source representative point beyond the terrain halo.
			# Keep its clipped mass and sample estimated ground at a deterministic point in the active footprint.
			p=inside.representative_point()
			oi,oj=sources.owners.owner(np.array([p.y]),np.array([p.x]))
			i,j=int(oi[0]),int(oj[0])
		try:
			d=geo.dimensions(f['source'],f['properties'],np.nan)
		except ValueError:
			d=None
			pending[i,j].append((len(entries),p.y,p.x))
		entries.append([f,g,d])
	# One raster read per needed tile, sampled as a batch; published NYC ground does not require a raster read.
	for (i,j),points in sorted(pending.items()):
		lat,lon=geodesy.from_local(np.array([p[1] for p in points]),np.array([p[2] for p in points]))
		values=sources.raster(i,j).sample(lat,lon)
		for (index,north,east),ground in zip(points,values):
			f=entries[index][0]
			try:
				entries[index][2]=geo.dimensions(f['source'],f['properties'],float(ground))
				entries[index][2]['ground_sample_ne_m']=[float(north),float(east)]
			except ValueError:
				rejected['missing-ground']+=1
	for f,g,d in entries:
		if d is not None:
			selected_flags.append(selection.keep(g,d['height_m'],shore,policy))
			normalized.append({'id':f['id'],'source':f['source'],**d})
			geometries.append(g.simplify(geo.SIMPLIFY_M,preserve_topology=True))
	del entries,features
	tree=shapely.STRtree(geometries)
	stage=out.with_name(out.name+'.building')
	if stage.exists():
		check_output(stage)
		shutil.rmtree(stage)
	stage.mkdir(parents=True)
	# Marker makes interrupted staging recognizable, never authorizes removing arbitrary directories.
	write_json(stage/'index.json',{'schema':SCHEMA})
	records,root_children,used,part_ids=[],[],set(),set()
	source_records=[{'file':'index.json','sha256':manifest.sha256_file(harbor/'index.json')}]
	harbor_hashes={o['file']:o['sha256'] for t in hindex['tiles'] for o in t.get('outputs',[])}
	for i,j in active:
		frame=frames.TileFrame(i,j)
		folder=sources.tile_dir(i,j)
		for name in ('land_areas.geojson','landing_structures.geojson','aids.geojson','bridges.geojson','geoid_offset.json','depth_merged_navd88.tif')+(('shoreline_constructions.geojson',) if policy!='A' else ()):
			p=folder/name
			relative=p.relative_to(harbor).as_posix()
			digest=manifest.sha256_file(p)
			if relative in harbor_hashes and harbor_hashes[relative]!=digest:
				raise ValueError(f'harbor index hash mismatch: {relative}')
			source_records.append({'file':relative,'sha256':digest})
		land,exclusions=land_and_exclusions(folder)
		shapely.prepare(land)
		shapely.prepare(exclusions)
		core=box(frame.east0,frame.north0,frame.east1,frame.north1)
		def geoid_at(north,east):
			if np.all((north>=frame.north0)&(north<frame.north1)&(east>=frame.east0)&(east<frame.east1)):
				return sources.geoid(i,j)(north,east)
			return sources.geoid_at(north,east)
		groups=defaultdict(list)
		attrs=[]
		for index in sorted(tree.query(core)):
			g=geometries[index]
			f=normalized[index]
			# Already simplified once, then clip against land/exclusions and the exact world square.
			pieces=g.intersection(core)
			if not land.covers(pieces):
				pieces=pieces.intersection(land)
			if exclusions.intersects(pieces):
				pieces=pieces.difference(exclusions)
			pieces=shapely.set_precision(pieces,.001)
			pieces=sorted(geo.polygons(pieces),key=lambda p:shapely.normalize(p).wkb)
			if pieces:
				renderable[f['source']].add(f['id'])
				if not selected_flags[index]:
					omitted[f['source']].add(f['id'])
					continue
			for part,p in enumerate(pieces):
				p=geo.canonical(p)
				pid=f"{f['id']}@{frame.id}:{part}"
				if pid in part_ids:
					raise ValueError(f'duplicate part {pid}')
				part_ids.add(pid)
				used.add(f['id'])
				point=p.representative_point()
				row=min(3,int((point.y-frame.north0)//1000))
				col=min(3,int((point.x-frame.east0)//1000))
				groups[row,col].append((pid,p,f))
		children=[]
		tile_positions=[]
		tile_bytes,tile_triangles=0,0
		target=stage/'tiles'/frame.id
		target.mkdir(parents=True)
		for (row,col),items in sorted(groups.items()):
			parts=mesh.Parts()
			index_offset=0
			name=f'c{row}_{col}.glb'
			for pid,p,f in items:
				start_v,start_i=parts.count,index_offset
				try:
					arrays=geo.extrude(p,f['ground_navd88_m'],f['roof_navd88_m'],frame,geoid_at)
				except ValueError as error:
					raise ValueError(f'{pid}: {error}') from error
				parts.add(*arrays)
				index_offset+=len(arrays[3])
				attrs.append({**f,'part_id':pid,'mesh':name,'vertex_start':start_v,'vertices':len(arrays[0]),
					'index_start':start_i,'indices':len(arrays[3]),'footprint':mapping(shapely.set_precision(p,.001))})
			arrays=parts.arrays()
			triangles=len(arrays[3])//3
			if triangles>MAX_CHUNK_TRIANGLES:
				raise ValueError(f'{frame.id}/{name}: {triangles} triangles exceeds chunk budget')
			blob=glb.encode(*arrays)
			(target/name).write_bytes(blob)
			tile_bytes+=len(blob)
			tile_triangles+=triangles
			tile_positions.append(arrays[0])
			children.append({'boundingVolume':{'box':mesh_box(arrays[0])},'geometricError':0.,'content':{'uri':name}})
		if children:
			pos=np.concatenate(tile_positions)
			region=content_region(frame,pos)
			write_json(target/'tileset.json',{'asset':{'version':'1.1'},'geometricError':1e4,'root':{
				'boundingVolume':{'box':mesh_box(pos)},'transform':frame.transform(),'geometricError':1e4,'refine':'ADD','children':children}})
			compact(target/'attributes.json.gz',{'schema':'harbor-building-attributes-v1','coordinates':'world east,north metres; NAVD88 vertical metres',
				'buildings':attrs})
			root_children.append({'boundingVolume':{'region':region},'geometricError':1e4,'content':{'uri':f'tiles/{frame.id}/tileset.json'}})
			tile_bytes=sum(p.stat().st_size for p in target.iterdir())
		if tile_bytes>MAX_TILE_BYTES:
			raise ValueError(f'{frame.id}: {tile_bytes} exceeds tile size budget')
		records.append({'id':frame.id,'i':i,'j':j,'parts':len(attrs),'buildings':len({f['id'] for f in attrs}),
			'by_source':dict(sorted(Counter(f['source'] for f in attrs).items())), 'bytes':tile_bytes,'triangles':tile_triangles,'chunks':len(children)})
		log(f'[buildings] {frame.id}: {len(attrs)} parts, {tile_triangles} triangles, {tile_bytes/1e6:.2f} MB')
		if sum(t['bytes'] for t in records)>MAX_TOTAL_BYTES:
			raise ValueError('partial skyline already exceeds 100 MB; stopped generation, ask coordinator before continuing')
	if not root_children:
		raise ValueError('no buildings survived masks')
	write_json(stage/'tileset.json',{'asset':{'version':'1.1','generator':'ferrydata.buildings; world3d glTF writer'},'geometricError':1e5,
		'root':{'boundingVolume':{'region':frames.union_region([c['boundingVolume']['region'] for c in root_children])},
			'geometricError':1e5,'refine':'ADD','children':root_children}})
	files=file_records(stage)
	# world3d file_records excludes index.json itself.
	counts={'input_unique':input_count,'input_by_source':input_by_source,'buildings':len(used),'parts':len(part_ids),
		'by_source':dict(sorted(Counter(f['source'] for f in normalized if f['id'] in used).items())),
		'height_basis':dict(sorted(Counter(f['height_basis'] for f in normalized if f['id'] in used).items())),
		'rejected':dict(sorted(rejected.items())), 'selection_omitted_by_source':{s:len(ids) for s,ids in sorted(omitted.items())},
		'eligible_after_masks_by_source':{s:len(ids) for s,ids in sorted(renderable.items())},
		'masked_or_outside':len(normalized)-sum(len(ids) for ids in renderable.values())}
	index={'schema':SCHEMA,'tileset':'tileset.json','selection':selection_record,'coverage':inv.get('coverage',{'complete':True,'missing':[]}),
		'raw_inventory_sha256':manifest.sha256_file(cache/'inventory.json'),
		'overpass_crosscheck':inv.get('overpass_crosscheck'),'counts':counts,'tiles':records,'files':files,'harbor_sources':source_records,
		'raw_sources':[{'file':name,**pins.files[name]} for name in [e['file'] for e in inv['files']]+inv.get('support_files',[])],
		'parameters':{'simplify_m':geo.SIMPLIFY_M,'exclusion_m':EXCLUSION_M,'default_height_m':geo.DEFAULT_HEIGHT_M,'level_height_m':geo.LEVEL_HEIGHT_M,
			'max_total_bytes':MAX_TOTAL_BYTES,'max_tile_bytes':MAX_TILE_BYTES,'max_chunk_triangles':MAX_CHUNK_TRIANGLES},
		'frames':'world3d TileFrame and Owners, glTF east/up/south, ECEF curvature; NAVD88 plus owner geoid at every vertex',
		'lod':'Single exact LoD1, 1 km representative-point chunks inside 4 km terrain tiles. Contentless parents force traversal; leaves have zero error. No false coarse-error claims.',
		'licences':{'nyc':'NYC Open Data terms; City of New York, OTI, Building Footprints (5zhs-2jue)', 'osm-nj':'© OpenStreetMap contributors; ODbL 1.0; https://www.openstreetmap.org/copyright'},
		'totals':{'bytes':sum(f['bytes'] for f in files),'triangles':sum(t['triangles'] for t in records)}}
	write_json(stage/'index.json',index)
	total=sum(p.stat().st_size for p in stage.rglob('*') if p.is_file())
	if total>MAX_TOTAL_BYTES:
		raise ValueError(f'{total} bytes exceeds 100 MB skyline budget; ask coordinator before proceeding')
	# The previous output remains intact until all geometry, sizes and index writes succeed.
	if out.exists():
		shutil.rmtree(out)
	stage.rename(out)
	peak=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
	return {**counts,'bytes':total,'triangles':index['totals']['triangles'],'elapsed_s':round(time.perf_counter()-started,3),
		'peak_rss_bytes':int(peak if sys.platform=='darwin' else peak*1024)}
