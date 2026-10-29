"""Build only from verified cached sources and the adopted runtime/skyline sidecars."""
from collections import defaultdict
import gzip
import json
import math
from pathlib import Path
import shutil
import tempfile
import xml.etree.ElementTree as ET

import numpy as np
import pyogrio
from shapely import get_num_coordinates, force_2d, make_valid, normalize, set_precision
from shapely.geometry import Point, box, mapping, shape
from shapely.ops import transform, unary_union

from ferrydata import enc, geodesy, manifest as manifests, paths
from ferrydata.harbor_runtime import pack as runtime_pack
from ferrydata.harbor_runtime.geometry import parts
from ferrydata.harbor_chart.pack import FILES, FORMAT, LAYERS, digest, read_json, safe_file, sha, verify, write_json

ATTRIBUTES={'DRVAL1','DRVAL2','VALDCO','VALSOU','WATLEV','CATOBS','CATWRK','CATROC','CATREA','CATNAV','CATTRK','CATTSS','CATFWD','CATDRG','ORIENT','TRAFIC','STATUS','OBJNAM','VERDAT','QUASOU','TECSOU','EXPSOU','SOUACC','RESTRN','NATSUR','NATQUA','CONRAD','CONVIS','CATCOA','CONDTN','NATCON','HEIGHT','ELEVAT','VERACC','VERLEN','COLOUR'}
GRID_M=250
LAND_SIMPLIFY_M=1.
BUILDING_SIMPLIFY_M=1.
MAX_BYTES=25_000_000
CLASSES={'land':('LNDARE','COALNE'),'depth_areas':('DEPARE','DRGARE'),'contours':('DEPCNT',),
	'soundings':('SOUNDG',),'hazards':('OBSTRN','WRECKS','UWTROC'),'channels':('FAIRWY','NAVLNE','RECTRC')}


def local(geometry):
	def project(x,y,z=None):
		n,e=geodesy.to_local(np.asarray(y),np.asarray(x))
		return n,e
	return transform(project,force_2d(geometry))


def clean(value):
	if value is None:
		return None
	if isinstance(value,(list,tuple,np.ndarray)):
		return [clean(v) for v in value]
	if isinstance(value,(np.integer,int)):
		return int(value)
	if isinstance(value,(np.floating,float)):
		return float(value) if math.isfinite(value) else None
	return str(value)


def check_datum(cell,code,properties,declared='MLLW'):
	cell_datum=enc.datum_name(code)
	feature_code=properties.get('VERDAT')
	feature_datum=enc.datum_name(feature_code) if feature_code is not None else cell_datum
	if cell_datum!=declared or feature_datum!=declared:
		raise ValueError(f'{cell}: datum mismatch: declared {declared}, DSPM_SDAT {cell_datum}, VERDAT {feature_datum}')
	return declared


def cached_cell(cell,pins):
	path=paths.raw_dir(enc.SOURCE)/(cell+'.zip')
	if not path.is_file():
		raise ValueError(f'missing cached ENC cell: {path}; offline build never downloads')
	if cell+'.zip' not in pins or sha(path)!=pins[cell+'.zip']['sha256']:
		raise ValueError(f'{path}: cached source hash mismatch')
	# Read in place through GDAL; no extraction or raw-cache mutation.
	return f'/vsizip/{path}/ENC_ROOT/{cell}/{cell}.000'


def catalog(path,names):
	ns={'g':'http://www.isotc211.org/2005/gmd','c':'http://www.isotc211.org/2005/gco'}
	result={}
	for _,metadata in ET.iterparse(path,events=('end',)):
		if metadata.tag!='{'+ns['g']+'}MD_Metadata':
			continue
		citation=metadata.find('g:identificationInfo/g:MD_DataIdentification/g:citation/g:CI_Citation',ns)
		if citation is not None:
			name=citation.findtext('g:title/c:CharacterString',namespaces=ns)
			if name in names:
				dates={}
				for date in citation.findall('g:date/g:CI_Date',ns):
					kind=date.find('g:dateType/g:CI_DateTypeCode',ns).get('codeListValue')
					dates[kind]=date.findtext('g:date/c:Date',namespaces=ns)
				result[name]=dict(catalog_edition=citation.findtext('g:edition/c:CharacterString',namespaces=ns),catalog_dates=dates)
		metadata.clear()
	if set(result)!=set(names):
		raise ValueError('catalogue missing required runtime cells')
	return result


def owners(cells,domain):
	"""Highest usage band, then smallest scale denominator, then cell name owns overlap."""
	covered=shape({'type':'GeometryCollection','geometries':[]})
	result={}
	for cell in sorted(cells,key=lambda c:(-c['usage_band'],c['scale'],c['name'])):
		coverage=make_valid(cell['coverage']).intersection(domain)
		result[cell['name']]=coverage.difference(covered)
		covered=unary_union([covered,coverage])
	return result


def merge_records(records,masks):
	groups=defaultdict(list)
	for row in records:
		g=make_valid(row['geometry']).intersection(masks[row['cell']])
		# Keep original dimensionality; clipping must not invent boundary points/lines.
		dimension=row['geometry'].geom_type
		allowed=('Polygon',) if 'Polygon' in dimension else ('LineString',) if 'LineString' in dimension else ('Point',)
		selected=[p for p in parts(g) if p.geom_type in allowed]
		if selected:
			groups[(row['feature_class'],row['source_id'])].append((row,unary_union(selected)))
	out=[]
	for (kind,key),items in sorted(groups.items()):
		# Source LNAM identifies a feature across adjacent cells; conflicting attributes fail.
		props={json.dumps(r['properties'],sort_keys=True) for r,g in items}
		if len(props)!=1:
			raise ValueError(f'{key}: conflicting chart attributes across cells')
		g=set_precision(unary_union([g for r,g in items]),.001)
		if g.is_empty:
			raise ValueError(f'{key}: source feature collapsed during preparation')
		out.append(dict(id='enc:'+key,source_id=key,feature_class=kind,cells=sorted({r['cell'] for r,g in items}),
			properties=items[0][0]['properties'],geometry_ne_m=mapping(normalize(g))))
	return sorted(out,key=lambda r:r['id'])


def read_enc(names,domain):
	pins=manifests.load_combined(enc.SOURCE).files
	cat=paths.raw_dir(enc.SOURCE)/'ENCProdCat_19115.xml'
	if not cat.is_file() or 'ENCProdCat_19115.xml' not in pins or sha(cat)!=pins['ENCProdCat_19115.xml']['sha256']:
		raise ValueError('missing or corrupt cached ENC catalogue; offline build never downloads')
	metadata=catalog(cat,names)
	cells=[];records=defaultdict(list)
	for name in sorted(names):
		base=cached_cell(name,pins)
		available={str(a) for a,b in pyogrio.list_layers(base)}
		dsid=pyogrio.read_dataframe(base,layer='DSID').iloc[0]
		check_datum(name,dsid.DSPM_SDAT,{})
		if dsid.DSPM_DUNI!=1:
			raise ValueError(f'{name}: ENC depth units are not metres')
		coverage=pyogrio.read_dataframe(base,layer='M_COVR')
		coverage=unary_union([local(r.geometry) for r in coverage.itertuples() if r.CATCOV==1])
		cell=dict(name=name,usage_band=int(dsid.DSID_INTU),scale=int(dsid.DSPM_CSCL),coverage=coverage,
			edition=int(dsid.DSID_EDTN),update=int(dsid.DSID_UPDN),issue_date=str(dsid.DSID_ISDT),update_date=str(dsid.DSID_UADT),
			sounding_datum=enc.datum_name(dsid.DSPM_SDAT),vertical_datum=enc.datum_name(dsid.DSPM_VDAT),
			dspm_sdat=int(dsid.DSPM_SDAT),dspm_vdat=int(dsid.DSPM_VDAT),sha256=pins[name+'.zip']['sha256'],**metadata[name])
		cells.append(cell)
		for layer,kinds in CLASSES.items():
			for kind in kinds:
				if kind not in available:
					continue
				for row in pyogrio.read_dataframe(base,layer=kind).to_dict('records'):
					g=row.pop('geometry')
					if g is None or g.is_empty:
						continue
					props={k:clean(v) for k,v in row.items() if k in ATTRIBUTES and clean(v) is not None}
					if layer in ('depth_areas','contours','soundings','hazards'):
						check_datum(name,dsid.DSPM_SDAT,props)
						props['depth_datum']='MLLW'
					if kind=='SOUNDG':
						for point in sorted(parts(g),key=lambda p:tuple(p.coords[0])):
							if not point.has_z or not math.isfinite(point.z):
								raise ValueError(f'{name}: sounding missing depth')
							n,e=geodesy.to_local(point.y,point.x)
							# Coordinate identity deduplicates split/reordered multipoints across cells.
							key=str(row['LNAM'])+f':{point.x:.7f},{point.y:.7f}'
							records[layer].append(dict(source_id=key,cell=name,usage_band=cell['usage_band'],feature_class=kind,
								geometry=Point(float(n),float(e)),properties={**props,'depth_m':float(point.z),'depth_datum':'MLLW'}))
					else:
						records[layer].append(dict(source_id=str(row['LNAM']),cell=name,usage_band=cell['usage_band'],feature_class=kind,geometry=local(g),properties=props))
	masks=owners(cells,domain)
	return {layer:merge_records(rows,masks) for layer,rows in records.items()},[{k:v for k,v in c.items() if k!='coverage'} for c in cells],dict(file='noaa-enc/ENCProdCat_19115.xml',sha256=sha(cat))


def roof_height(record):
	# Above-ground height plus estimated terrain is not a known NAVD88 roof.
	if record.get('height_basis') not in ('heightroof-feet','height') or record.get('ground_basis')!='nyc-groundelev-feet':
		return None
	return record.get('roof_navd88_m')


def buildings(folder):
	index=read_json(folder/'index.json');groups=defaultdict(list);sources=[]
	files=index['files']
	# Only semantic sidecars are read, never GLBs. Verify every consumed byte against the skyline index.
	for entry in files:
		name=entry['path']
		if name.endswith('/attributes.json.gz'):
			path=safe_file(folder,name)
			if sha(path)!=entry['sha256']:
				raise ValueError(f'{name}: building sidecar hash mismatch')
			sources.append(dict(file='3dtiles-buildings/'+name,sha256=entry['sha256']))
			for r in json.loads(gzip.decompress(path.read_bytes()))['buildings']:
				groups[r['id']].append(r)
	out=[]
	for key,rows in sorted(groups.items()):
		heights={roof_height(r) for r in rows}
		if len(heights)!=1:
			raise ValueError(f'{key}: conflicting roof heights')
		g=unary_union([transform(lambda e,n:(n,e),shape(r['footprint'])) for r in rows])
		g=set_precision(g.simplify(BUILDING_SIMPLIFY_M,preserve_topology=True),.001)
		if g.is_empty:
			raise ValueError(f'{key}: source building collapsed during simplification')
		out.append(dict(id='building:'+key,source_id=key,feature_class='building',geometry_ne_m=mapping(normalize(g)),properties=dict(
			roof_navd88_m=next(iter(heights)))))
	return out,sources,index['licences'],index['selection']


def quantise(geometry):
	"""Round each NE coordinate to 1 cm, at most 0.005 m error per axis."""
	return make_valid(transform(lambda n,e:(np.round(n,2),np.round(e,2)),geometry))


def layer_payload(features,reference=False):
	features=[dict(r) for r in sorted(features,key=lambda r:r['id'])]
	attributes=[];attribute_ids={}
	if len({r['id'] for r in features})!=len(features):
		raise ValueError('duplicate chart feature id')
	cells=defaultdict(list)
	for i,r in enumerate(features):
		if 'properties' in r:
			properties=r.pop('properties');key=json.dumps(properties,sort_keys=True)
			if key not in attribute_ids:
				attribute_ids[key]=len(attributes);attributes.append(properties)
			r['attribute_index']=attribute_ids[key]
		g=quantise(shape(r['geometry_ne_m'])) if 'geometry_ne_m' in r else None
		if g is not None:
			r['geometry_ne_m']=mapping(g)
		bounds=list(g.bounds) if g is not None else r['bounds_ne_m']
		if (g is not None and (g.is_empty or not g.is_valid)) or len(bounds)!=4 or not all(math.isfinite(v) for v in bounds) or bounds[0]>bounds[2] or bounds[1]>bounds[3]:
			raise ValueError(f"{r['id']}: invalid chart geometry")
		r['bounds_ne_m']=bounds
		a,b,c,d=[math.floor(v/GRID_M) for v in bounds]
		for n in range(a,c+1):
			for e in range(b,d+1):
				cells[f'{n},{e}'].append(i)
	# Column tuples avoid repeating JSON field names for ~84,000 features. Bounds
	# are derived from geometry, not stored twice. References retain only extents.
	is_reference=reference or bool(features) and 'runtime_id' in features[0]
	fields=['id','runtime_layer','runtime_id','bounds_ne_m'] if is_reference else ['id','source_id','feature_class','attribute_index','geometry_ne_m','cells']
	rows=[]
	for feature in features:
		if not is_reference:
			g=feature['geometry_ne_m']
			feature['geometry_ne_m']=[g['type'],g.get('coordinates',g.get('geometries'))]
		rows.append([feature.get(field) for field in fields])
	return dict(format='ferrysim-chart-layer/1',fields=fields,features=rows,attributes=attributes,grid=dict(cell_m=GRID_M,cells=dict(sorted(cells.items()))))


def expand(payload):
	rows=[]
	for values in payload['features']:
		if len(values)!=len(payload['fields']):
			raise ValueError('chart tuple length mismatch')
		r=dict(zip(payload['fields'],values,strict=True))
		if 'attribute_index' in r:
			index=r.pop('attribute_index')
			if not isinstance(index,int) or not 0<=index<len(payload['attributes']):
				raise ValueError('invalid chart attribute index')
			r['properties']=payload['attributes'][index]
			kind,coordinates=r['geometry_ne_m']
			r['geometry_ne_m']={'type':kind,'geometries' if kind=='GeometryCollection' else 'coordinates':coordinates}
			if r.get('cells') is None:
				r.pop('cells',None)
		rows.append(r)
	return rows


def publish(out,layers,metadata):
	out=Path(out).absolute();out.parent.mkdir(parents=True,exist_ok=True)
	if out.is_symlink():
		raise ValueError('refusing symlink chart output')
	if out.exists() and {p.name for p in out.iterdir()}-{'manifest.json','DIGEST',*FILES}:
		raise ValueError('refusing to replace unowned chart directory')
	with tempfile.TemporaryDirectory(prefix='.chart-',dir=out.parent) as temporary:
		stage=Path(temporary)
		for name in LAYERS:
			write_json(stage/(name+'.json'),layer_payload(layers[name],name=='references'))
		manifest=dict(metadata,format=FORMAT,schema_version=1,notice='Not for navigation',noaa_user_agreement=enc.USER_AGREEMENT,
			coordinate_quantisation_m=.01,coordinate_max_axis_error_m=.005,grid_cell_m=GRID_M,depth_datum='MLLW',counts={name:len(layers[name]) for name in LAYERS},
			files={name:sha(stage/name) for name in FILES},sizes_bytes={name:(stage/name).stat().st_size for name in FILES})
		write_json(stage/'manifest.json',manifest)
		(stage/'DIGEST').write_text(digest(stage,('manifest.json',*FILES))+'\n')
		total=sum(p.stat().st_size for p in stage.iterdir())
		print(json.dumps(dict(total_bytes=total,layers={name:dict(features=len(layers[name]),vertices=sum(int(get_num_coordinates(shape(r['geometry_ne_m']))) for r in layers[name] if 'geometry_ne_m' in r),bytes=(stage/(name+'.json')).stat().st_size) for name in LAYERS}),sort_keys=True))
		if total>MAX_BYTES:
			raise ValueError(f'chart pack {total} bytes exceeds {MAX_BYTES} byte budget; coordinator approval required')
		verify(stage)
		out.mkdir(exist_ok=True)
		for p in stage.iterdir():
			shutil.copyfile(p,out/p.name)
	return manifest


def build(harbor=None,out=None):
	harbor=Path(harbor or paths.processed_dir('harbor'))
	out=Path(out or harbor/'chart').absolute()
	root=harbor.resolve()
	if out==root or out in root.parents or (out.is_relative_to(root) and out!=root/'chart'):
		raise ValueError('output must be harbor/chart or outside harbor inputs')
	runtime=runtime_pack.verify(harbor/'runtime',harbor)
	index=read_json(harbor/'index.json')
	if runtime['origin_latlon']!=[geodesy.ORIGIN_LAT,geodesy.ORIGIN_LON]:
		raise ValueError('runtime origin differs from shared geodesy frame')
	coverage=shape(read_json(harbor/'runtime/footprints.json')['coverage_ne_m'])
	layers,cells,cat=read_enc(index['enc_cells'],coverage)
	for name in LAYERS:
		layers.setdefault(name,[])
	structures,sources,licences,selection=buildings(harbor/'3dtiles-buildings')
	layers['radar_obstructions']=structures+[dict(r,id='radar:'+r['id'],geometry_ne_m=mapping(normalize(shape(r['geometry_ne_m']).simplify(LAND_SIMPLIFY_M,preserve_topology=True)))) for r in layers['land'] if r['feature_class']=='LNDARE']
	# References carry identity and index bounds only; all draw geometry remains in runtime.
	for name,key in [('aids','aids'),('bridges','bridges'),('footprints','footprints')]:
		for r in read_json(harbor/f'runtime/{name}.json')[key]:
			if name=='footprints' and r['semantic']!='shoreline':
				continue
			if name=='aids':
				n,e=r['position_ne_m'];bounds=[n,e,n,e]
			elif name=='bridges':
				bounds=list(unary_union([shape(s['geometry_ne_m']) for s in r['spans']]).bounds)
			else:
				bounds=list(shape(r['geometry_ne_m']).bounds)
			layers['references'].append(dict(id=name+':'+r['id'],runtime_id=r['id'],runtime_layer=name,bounds_ne_m=bounds))
	meta=dict(origin_latlon=runtime['origin_latlon'],coordinate_frame=runtime['coordinate_frame'],runtime_digest=(harbor/'runtime/DIGEST').read_text().strip(),
		cells=cells,sources=[cat,dict(file='runtime/manifest.json',sha256=sha(harbor/'runtime/manifest.json')),dict(file='3dtiles-buildings/index.json',sha256=sha(harbor/'3dtiles-buildings/index.json')),*sources],
		simplification_m=dict(land=LAND_SIMPLIFY_M,buildings=BUILDING_SIMPLIFY_M,source_buildings=.35),building_licences=licences,building_selection=selection,
		overlap_rule='M_COVR CATCOV=1 ownership: highest usage band, then smallest scale denominator, then lexical cell name; clip to owned coverage and active runtime coverage, union LNAM fragments; point duplicates collapse by identity',
		vertical_note='ENC depths retain MLLW; no conversion. Building roofs above NAVD88 are null if height or ground is estimated. Semantic data only; no collision authority.')
	return publish(out,layers,meta)
