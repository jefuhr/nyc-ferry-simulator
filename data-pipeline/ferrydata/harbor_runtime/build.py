"""Deterministic offline export from pinned harbor products; no render-mesh or slice dependencies."""
from collections import defaultdict
from pathlib import Path
import shutil
import tempfile

from shapely.geometry import shape
from shapely.ops import polygonize_full, unary_union

from ferrydata import paths
from ferrydata.environment.pack import depth_digest, read_digest
from ferrydata.harbor_runtime import geometry as geo
from ferrydata.harbor_runtime.pack import FILES, FORMAT, digest, read_json, safe_file, sha, verify, write_json

TILE_FILES=('aids.geojson','bridges.geojson','landing_structures.geojson','land_areas.geojson','shoreline_constructions.geojson','geoid_offset.json')
POSITION_FIELDS={'lon','lat','north_m','east_m'}


class Inputs:
	def __init__(self,root):
		self.root=Path(root)
		self.sources={}
		self.index=self.read('index.json')
		if self.index['schema']!='harbor-tiles-v1' or self.index['origin_latlon']!=[40.7,-74.015] or self.index['tile_size_m']!=4000 or self.index['margin_m']!=250:
			raise ValueError('unsupported harbor frame or tile schema')
		self.entries=sorted((e for e in self.index['tiles'] if e['active']),key=lambda e:(e['i'],e['j']))
		if not self.entries or len({e['id'] for e in self.entries})!=len(self.entries):
			raise ValueError('missing or duplicate active tiles')
		self.features={name:[] for name in TILE_FILES if name.endswith('.geojson')}
		for entry in self.entries:
			i,j=entry['i'],entry['j']
			if entry['id']!=f'tile_{i}_{j}' or entry['bounds_local_m']!=[i*4000,j*4000,(i+1)*4000,(j+1)*4000]:
				raise ValueError(f"tile {entry['id']}: bounds or identity disagree with canonical frame")
			pins={p['file']:p['sha256'] for p in entry['outputs']}
			for name in TILE_FILES:
				rel=f"tiles/{entry['id']}/{name}"
				value=self.read(rel,pins.get(rel))
				if rel not in pins:
					raise ValueError(f'{rel}: missing source hash in tile index')
				if name in self.features:
					self.features[name].extend(value['features'])
		self.contacts=self.read('unity/contacts.json')
		self.audit=self.read('bridge_clearance_audit.json')
		world=self.read('3dtiles/index.json')
		if world['schema']!='harbor-3dtiles-v1':
			raise ValueError('unsupported terrain index schema')
		# Cross-check shared dependencies against the terrain index; never read GLBs for semantic queries.
		for source in world['sources']:
			if source['file'] in self.sources and source['sha256']!=self.sources[source['file']]:
				raise ValueError(f"{source['file']}: terrain index hash differs from tile index")
		depth=self.read('env/depth/manifest.json')
		for grid in depth['grids']:
			rel='env/depth/'+grid['file']; p=safe_file(self.root,rel)
			if sha(p)!=grid['sha256']:
				raise ValueError(f'{rel}: depth hash mismatch')
		# Record depth linkage by its raw-byte manifest and DIGEST, rather than duplicating 74 MB of grids.
		if depth_digest(self.root/'env/depth')!=read_digest(self.root/'env/depth'):
			raise ValueError('environment depth digest mismatch')
		self.record('env/depth/DIGEST')
		self.depth_digest=read_digest(self.root/'env/depth')

	def record(self,name,expected=None):
		p=safe_file(self.root,name); actual=sha(p)
		if expected is not None and actual!=expected:
			raise ValueError(f'{name}: source hash mismatch')
		self.sources[name]=actual
		return p

	def read(self,name,expected=None):
		return read_json(self.record(name,expected))


def _properties(feature):
	return {key:value for key,value in feature['properties'].items() if key not in POSITION_FIELDS}


def aids(features):
	by_id={}
	for feature in features:
		key=feature['properties']['id']
		record=dict(id='enc:'+key,source_id=key,position_ne_m=feature['local_coordinates'],position_lonlat=feature['geometry']['coordinates'],properties=_properties(feature))
		if key in by_id and by_id[key]!=record:
			raise ValueError(f'aid {key}: conflicting seam records')
		by_id[key]=record
	return dict(format='ferrysim-harbor-aids/1',aids=[by_id[key] for key in sorted(by_id)])


def _by_source(features):
	groups=defaultdict(list)
	for feature in features:
		groups[feature['properties']['id']].append(feature)
	return groups


def bridges(features,audit,structures):
	by_id=_by_source(features)
	if audit.get('incomplete') or audit['group_distance_m']!=2.:
		raise ValueError('incomplete or unsupported bridge audit')
	members=[key for group in audit['physical_bridges'] for key in group['ids']]
	if len(members)!=len(set(members)) or set(members)!=set(by_id):
		raise ValueError('bridge audit membership differs from chart features')
	# Use the audit as the authoritative physical grouping, and source IDs as span identities.
	piers={}
	for key,fs in sorted(_by_source(structures).items()):
		if any(f['properties'].get('feature_class')=='SLCONS' and f['properties'].get('category_code') in (4,5) for f in fs):
			piers[key]=unary_union([geo.local_geometry(f) for f in fs])
	groups=[]
	for group in sorted(audit['physical_bridges'],key=lambda g:sorted(g['ids'])):
		spans=[]; group_piers={}
		for key in sorted(group['ids']):
			fs=by_id[key]; geometry=unary_union([geo.local_geometry(f) for f in fs])
			if not geometry.is_valid:
				raise ValueError(f'bridge {key}: invalid source geometry')
			variants={}
			for f in fs:
				p=_properties(f)
				# Preserve every source property variant; never propagate a clearance to a neighbouring span.
				variants[str(sorted(p.items()))]=p
			clearances=[]
			for p in sorted(variants.values(),key=lambda p:str(sorted(p.items()))):
				clearances.append(dict(p,source='NOAA ENC BRIDGE',underside_navd88_m=None,
					conversion_note='MHW clearance retained; no span-specific MHW to NAVD88 conversion supplied'))
			pier_ids=[]
			for pier_id,pier in piers.items():
				if geometry.intersects(pier):
					pier_ids.append('enc:'+pier_id)
					item=group_piers.setdefault(pier_id,dict(id='enc:'+pier_id,source_id=pier_id,geometry_ne_m=geo.canonical(pier),
						vertical_bounds_navd88_m=[None,None],membership_evidence=[]))
					item['membership_evidence'].append(dict(span_id='enc:'+key,method='charted SLCONS pier footprint intersects charted BRIDGE footprint',
						intersection_type=geometry.intersection(pier).geom_type,confidence='geometry-derived; not surveyed structural attachment'))
			spans.append(dict(id='enc:'+key,source_id=key,geometry_ne_m=geo.canonical(geometry),clearances=clearances,
				pier_ids=pier_ids,pier_inventory_complete=False,vertical_bounds_navd88_m=[None,None]))
		groups.append(dict(id='bridge:'+min(group['ids']),source_ids=sorted(group['ids']),names=group['names'],spans=spans,
			piers=[group_piers[key] for key in sorted(group_piers)],audit_evidence=group,
			grouping='bridge_clearance_audit.json: chart geometry distance <= 2 m, transitive',tower_extent_m=None))
	return dict(format='ferrysim-harbor-bridges/1',bridges=groups)


def landings(index,features,contacts):
	ids=[c['id'] for c in contacts]
	if len(ids)!=len(set(ids)):
		raise ValueError('duplicate contact ids')
	contact_by_id={c['id']:c for c in contacts}
	by_structure=defaultdict(list)
	for feature in features:
		p=feature['properties']
		if any(s['kind'] in ('berth-face','barge-side','barge-end') for s in p['segments']):
			by_structure[p['structure_id']].append(feature)
	barges=[]
	for key,fs in sorted(by_structure.items()):
		# Include all source polylines, including backs and sides that the contact eligibility gate omits.
		lines=unary_union([geo.local_geometry(f) for f in fs])
		polygons,cuts,dangles,invalid=polygonize_full(lines)
		if len(polygons.geoms)!=1 or not cuts.is_empty or not dangles.is_empty or not invalid.is_empty:
			raise ValueError(f'{key}: source polylines do not form exactly one complete closed outline')
		polygon=polygons.geoms[0]
		if not polygon.is_valid or polygon.area<=0:
			raise ValueError(f'{key}: invalid barge outline')
		barges.append(dict(id=key,geometry_ne_m=geo.canonical(polygon,solid_right=True),stop_ids=sorted({f['properties']['stop_id'] for f in fs}),
			floating=True,vertical_bounds_navd88_m=[None,None],
			provenance=[dict(polyline_id=f['properties']['polyline_id'],source=f['properties']['source'],
				confidence=sorted({s['confidence'] for s in f['properties']['segments']})) for f in sorted(fs,key=lambda f:f['properties']['polyline_id'])]))
	barge_ids={b['id'] for b in barges}
	stops=[]
	for stop in index['stops']:
		rows=[c for c in contacts if c['stop_id']==stop['stop_id']]
		berths={}
		for c in rows:
			if not c.get('berth_face'):
				continue
			if c['structure_id'] not in barge_ids:
				raise ValueError(f"berth {c['id']}: missing closed barge")
			key=(c['structure_id'],c['berth_order'])
			meta={k:v for k,v in c.items() if k.startswith('berth_') or k=='primary_face'}
			meta.update(id=f"berth:{stop['stop_id']}:{c['structure_id']}:{c['berth_order']}",structure_id=c['structure_id'])
			if key in berths and berths[key]!=meta:
				raise ValueError(f'conflicting berth metadata {key}')
			for segment_id in meta['berth_face_segment_ids']:
				if segment_id not in contact_by_id or contact_by_id[segment_id]['structure_id']!=c['structure_id'] or contact_by_id[segment_id]['stop_id']!=stop['stop_id']:
					raise ValueError('berth face references missing or unrelated contact')
			berths[key]=meta
		if not rows or not berths:
			raise ValueError(f"landing {stop['stop_id']}: missing contacts or berth metadata")
		stops.append(dict(stop,berths=sorted(berths.values(),key=lambda b:(b['berth_order'],b['id']))))
	if {c['stop_id'] for c in contacts}!={s['stop_id'] for s in stops}:
		raise ValueError('contacts and harbor stop inventories differ')
	return dict(format='ferrysim-harbor-landings/1',stops=stops,barges=barges)


def footprints(entries,features,bridge_data,landing_data):
	coverage=geo.domain(entries); items=[]
	def add(id_,semantic,geometry,source_ids,properties=None):
		clipped=geometry.intersection(coverage)
		if clipped.is_empty:
			return
		if not clipped.is_valid:
			raise ValueError(f'{id_}: invalid footprint')
		items.append(dict(id=id_,semantic=semantic,source_ids=source_ids,geometry_ne_m=geo.canonical(clipped,solid_right=semantic=='barge'),
			vertical_bounds_navd88_m=[None,None],collision_authority=False,properties=properties or {}))
	for name,semantic in [('land_areas.geojson','shoreline'),('shoreline_constructions.geojson','structure')]:
		for key,fs in sorted(_by_source(features[name]).items()):
			add(f'{semantic}:enc:{key}',semantic,geo.merge_geometry(fs,coverage),['enc:'+key],
				dict(source='NOAA ENC',simplification_m=0,invalid_source_repaired=any(not geo.local_geometry(f).is_valid for f in fs)))
	for bridge in bridge_data['bridges']:
		for span in bridge['spans']:
			add('bridge:'+span['id'],'bridge',shape(span['geometry_ne_m']),[span['id']],dict(physical_bridge_id=bridge['id']))
	for barge in landing_data['barges']:
		add('barge:'+barge['id'],'barge',shape(barge['geometry_ne_m']),[barge['id']],dict(floating=True))
	return dict(format='ferrysim-harbor-queries/1',coordinate_order=['north_m','east_m'],coverage_ne_m=geo.canonical(coverage),
		purpose='resident camera/radar/chartplotter semantics; never physics contact authority or render-tile geometry',
		footprints=sorted(items,key=lambda i:i['id']))


def build(harbor=None,out=None):
	harbor=Path(harbor) if harbor is not None else paths.PROCESSED_ROOT/'harbor'
	out=Path(out) if out is not None else harbor/'runtime'
	resolved=out.resolve(); root=harbor.resolve()
	if resolved==root or (resolved.is_relative_to(root) and resolved!=root/'runtime') or root.is_relative_to(resolved):
		raise ValueError('output must be harbor/runtime or a separate directory outside inputs')
	if out.exists() and (out.is_symlink() or {p.name for p in out.iterdir()}-{'manifest.json','DIGEST',*FILES}):
		raise ValueError('output contains files not owned by runtime exporter')
	inputs=Inputs(harbor)
	bridge_data=bridges(inputs.features['bridges.geojson'],inputs.audit,inputs.features['shoreline_constructions.geojson'])
	landing_data=landings(inputs.index,inputs.features['landing_structures.geojson'],inputs.contacts)
	aid_data=aids(inputs.features['aids.geojson'])
	query_data=footprints(inputs.entries,inputs.features,bridge_data,landing_data)
	geoid,values,mask=geo.geoid_grid(harbor,inputs.entries)
	out.parent.mkdir(parents=True,exist_ok=True)
	with tempfile.TemporaryDirectory(prefix='.harbor-runtime-',dir=out.parent) as temp:
		stage=Path(temp)
		for name,value in [('aids.json',aid_data),('bridges.json',bridge_data),('landings.json',landing_data),('footprints.json',query_data),('geoid.json',geoid)]:
			write_json(stage/name,value)
		(stage/'contacts.json').write_bytes((harbor/'unity/contacts.json').read_bytes())
		(stage/'geoid.f64').write_bytes(values.tobytes(order='C'))
		(stage/'geoid_cells.u8').write_bytes(mask.tobytes(order='C'))
		manifest=dict(format=FORMAT,region='harbor',schema_status='proposed wave 3b contract input',origin_latlon=[40.7,-74.015],
			coordinate_frame='NED horizontal metres; north,east; heights NAVD88 unless explicitly stated',
			files={name:sha(stage/name) for name in FILES},sources=[dict(file=name,sha256=checksum) for name,checksum in sorted(inputs.sources.items())],
			environment_depth_digest=inputs.depth_digest,
			counts=dict(stops=len(landing_data['stops']),berths=sum(len(s['berths']) for s in landing_data['stops']),barges=len(landing_data['barges']),
				aids=len(aid_data['aids']),bridges=len(bridge_data['bridges']),spans=sum(len(b['spans']) for b in bridge_data['bridges']),footprints=len(query_data['footprints'])),
			notes=['contacts.json copied verbatim, including optional berth metadata; contact eligibility is unchanged',
				'Unknown vertical bounds and datum conversion are null; no invented heights or physical collision scope',
				'Semantic geometry preserves chart vertices, clipped and merged across tiles, independent of renderer selection',
				'Building semantic layers may be separate future packs; this pack has no skyline or render-residency dependency'])
		write_json(stage/'manifest.json',manifest)
		(stage/'DIGEST').write_text(digest(stage)+'\n',encoding='ascii')
		verify(stage)
		out.mkdir(parents=True,exist_ok=True)
		for name in (*FILES,'manifest.json','DIGEST'):
			shutil.copyfile(stage/name,out/name)
	return manifest
