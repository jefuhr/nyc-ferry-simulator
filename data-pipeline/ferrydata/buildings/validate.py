"""Read-back validation of the skyline, its sources, tile frames, semantic footprints and budgets."""
from pathlib import Path
from collections import Counter
import numpy as np
import shapely
from shapely.geometry import shape, box
from ferrydata import geodesy, manifest, paths
from ferrydata.buildings import build as pipeline, sources as raw,selection
from ferrydata.world3d import frames, heights, glb
from ferrydata.world3d.build import active_tiles, read_json, file_records, content_region, digest


def validate(harbor_dir=None,cache=None,out_dir=None,repeat_dir=None):
	harbor=Path(harbor_dir or paths.processed_dir('harbor'))
	cache=Path(cache or raw.cache_default())
	out=Path(out_dir or pipeline.default_output())
	errors=[]
	def require(ok, message):
		if not ok:
			errors.append(message)
	try:
		index=read_json(out/'index.json')
		require(index.get('schema')==pipeline.SCHEMA,'wrong skyline schema')
		require(index['selection']==selection.policy_record(index['selection']['id']),'selection policy metadata drift')
		require(manifest.sha256_file(cache/'inventory.json')==index['raw_inventory_sha256'],'raw coverage inventory changed')
		require(index['files']==file_records(out),'file inventory, size or SHA-256 mismatch')
		for source in index['harbor_sources']:
			require(manifest.sha256_file(harbor/source['file'])==source['sha256'],f"harbor source changed: {source['file']}")
		for source in index['raw_sources']:
			p=cache/source['file']
			require(p.stat().st_size==source['size_bytes'] and manifest.sha256_file(p)==source['sha256'],f"raw source changed: {source['file']}")
		active,_=active_tiles(harbor)
		require([(r['i'],r['j']) for r in index['tiles']]==active,'active tile list mismatch')
		sources=heights.Sources(harbor,active)
		root=read_json(out/'tileset.json')['root']
		wanted=[f"tiles/{r['id']}/tileset.json" for r in index['tiles'] if r['parts']]
		for child in root['children']:
			region=child['boundingVolume']['region']
			parent=root['boundingVolume']['region']
			require(all(region[k]>=parent[k] for k in (0,1,4)) and all(region[k]<=parent[k] for k in (2,3,5)),'root region bounds')
		require([c['content']['uri'] for c in root['children']]==wanted,'root tile references mismatch')
		parts_seen,building_ids=set(),set()
		identities={}
		total_triangles=0
		for tile in index['tiles']:
			if not tile['parts']:
				require(tile['bytes']==0 and tile['triangles']==0,f"empty tile metrics: {tile['id']}")
				continue
			folder=out/'tiles'/tile['id']
			frame=frames.TileFrame(tile['i'],tile['j'])
			tree=read_json(folder/'tileset.json')['root']
			require(tree['transform']==frame.transform(),f"frame transform: {tile['id']}")
			require(tree['geometricError']>0 and root['geometricError']>tree['geometricError'],f"parent errors: {tile['id']}")
			land,exclusions=pipeline.land_and_exclusions(sources.tile_dir(tile['i'],tile['j']))
			land=land.buffer(.002)
			shapely.prepare(land)
			exclusion_core=exclusions.buffer(-.002)
			shapely.prepare(exclusion_core)
			core=box(frame.east0,frame.north0,frame.east1,frame.north1).buffer(.002)
			attrs=pipeline.read_attributes(folder)
			require(len(attrs)==tile['parts'],f"part count: {tile['id']}")
			meshes={}
			world_positions={}
			mesh_triangles=0
			root_box=np.array(tree['boundingVolume']['box'])
			for child in tree['children']:
				name=child['content']['uri']
				require(Path(name).name==name and name.endswith('.glb'),f'unsafe mesh URI: {name}')
				_,arrays=glb.decode((folder/name).read_bytes())
				meshes[name]=arrays
				pos=arrays['POSITION']
				xyz=frames.ecef_from_content(pos,frame.transform())
				lat,lon,h=geodesy.from_ecef(xyz[:,0],xyz[:,1],xyz[:,2])
				n,e=geodesy.to_local(lat,lon)
				navd=h-sources.geoid_at(np.round(n,3),np.round(e,3))
				world_positions[name]=(n,e,navd)
				enu=frames.enu_from_gltf(pos.astype(np.float64))
				for b in (child['boundingVolume']['box'],root_box):
					b=np.asarray(b)
					require(np.all(np.abs(enu-b[:3])<=b[[3,7,11]]+.0001),f"bounds: {tile['id']}/{name}")
				require(child['geometricError']==0.,f'leaf error: {name}')
				idx=arrays['indices'].reshape(-1,3)
				tri=pos[idx].astype(np.float64)
				cross=np.cross(tri[:,1]-tri[:,0],tri[:,2]-tri[:,0])
				require(np.all(np.linalg.norm(cross,axis=1)>1e-8),f'degenerate triangles: {name}')
				require(np.all(np.einsum('ij,ij->i',cross,arrays['NORMAL'][idx[:,0]])>0),f'winding: {name}')
				require(len(idx)<=pipeline.MAX_CHUNK_TRIANGLES,f'triangle budget: {name}')
				mesh_triangles+=len(idx)
				region=content_region(frame,pos)
				parent=next(c['boundingVolume']['region'] for c in root['children'] if c['content']['uri']==f"tiles/{tile['id']}/tileset.json")
				require(all(region[k]>=parent[k]-1e-8 for k in (0,1,4)) and all(region[k]<=parent[k]+1e-8 for k in (2,3,5)),f'region: {name}')
			for a in attrs:
				pid=a['part_id']
				require(pid not in parts_seen,f'duplicate building part: {pid}')
				parts_seen.add(pid)
				building_ids.add(a['id'])
				identity=tuple(a[k] for k in ('source','height_m','ground_navd88_m','roof_navd88_m','height_basis','levels','use'))
				require(a['id'] not in identities or identities[a['id']]==identity,f'inconsistent building attributes across seams: {a["id"]}')
				identities[a['id']]=identity
				require(a['id'].startswith(a['source']+':'),f'source identity mismatch: {a["id"]}')
				p=shape(a['footprint'])
				require(p.is_valid and not p.is_empty and p.area>.01,f'invalid footprint: {pid}')
				require(core.covers(p),f'outside tile: {pid}')
				require(land.covers(p),f'building over water: {pid}')
				require(not exclusion_core.intersects(p) or p.intersection(exclusion_core).area<=1e-8,f'protected silhouette overlap: {pid}')
				require(1<=a['height_m']<=650 and abs(a['roof_navd88_m']-a['ground_navd88_m']-a['height_m'])<1e-8,f'height: {pid}')
				arr=meshes[a['mesh']]
				start,end=a['vertex_start'],a['vertex_start']+a['vertices']
				n_all,e_all,navd_all=world_positions[a['mesh']]
				n,e,navd=n_all[start:end],e_all[start:end],navd_all[start:end]
				require(np.all(shapely.covers(p.buffer(.002),shapely.points(e,n))),f'geometry/footprint disagreement: {pid}')
				err=np.minimum(abs(navd-a['ground_navd88_m']),abs(navd-a['roof_navd88_m']))
				require(np.all(err<.003),f'NAVD88/geoid mismatch: {pid}')
				i0,i1=a['index_start'],a['index_start']+a['indices']
				own=arr['indices'][i0:i1]
				require(len(own)==a['indices'] and np.all((own>=start)&(own<end)),f'attribute vertex/index range: {pid}')
			require(mesh_triangles==tile['triangles'],f"triangle count: {tile['id']}")
			total_triangles+=mesh_triangles
			actual_bytes=sum(p.stat().st_size for p in folder.iterdir() if p.is_file())
			require(actual_bytes==tile['bytes'] and actual_bytes<=pipeline.MAX_TILE_BYTES,f"tile size: {tile['id']}")
		require(len(parts_seen)==index['counts']['parts'] and len(building_ids)==index['counts']['buildings'],'total building counts')
		require(total_triangles==index['totals']['triangles'],'total triangles')
		require(dict(Counter(v[0] for v in identities.values()))==index['counts']['by_source'],'source count mismatch')
		require(dict(Counter(v[4] for v in identities.values()))==index['counts']['height_basis'],'height basis count mismatch')
		for source,count in index['counts']['eligible_after_masks_by_source'].items():
			require(count==index['counts']['by_source'].get(source,0)+index['counts']['selection_omitted_by_source'].get(source,0),'source count selection reconciliation')
		total_bytes=sum(p.stat().st_size for p in out.rglob('*') if p.is_file())
		require(total_bytes<=pipeline.MAX_TOTAL_BYTES,'total size budget')
		determinism={'checked':repeat_dir is not None}
		if repeat_dir is not None:
			first,second=digest(out),digest(Path(repeat_dir))
			determinism.update(identical=first==second,first_sha256=first,second_sha256=second)
			require(first==second,'two builds differ byte-for-byte')
		return {'passed':not errors,'errors':errors,'determinism':determinism,'coverage':index['coverage'],'counts':index['counts'],'bytes':total_bytes,'triangles':total_triangles,'tiles':index['tiles']}
	except (ValueError,KeyError,IndexError,OSError,TypeError) as error:
		errors.append(f'{type(error).__name__}: {error}')
		return {'passed':False,'errors':errors}
