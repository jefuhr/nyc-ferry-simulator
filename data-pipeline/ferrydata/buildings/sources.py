"""Sequential, pinned NYC Open Data and OSM Overpass downloads; no other building sources."""
import json
import random
import time
from pathlib import Path
from urllib.parse import urlencode, urlsplit

from ferrydata import fetcher, manifest, paths, tiles
from ferrydata.world3d.build import active_tiles, read_json, write_json

NYC_ID = "5zhs-2jue"
NYC_API = f"https://data.cityofnewyork.us/resource/{NYC_ID}.json"
OVERPASS = "https://overpass-api.de/api/interpreter"
APPROVED_MIRROR = "https://overpass.private.coffee/api/interpreter"
PAGE_SIZE = 5000
MAX_REQUESTS = 240
MAX_ATTEMPTS = 3


class RequestPolicy:
	"""One request at a time, bounded attempts, exponential backoff and jitter; no output randomness."""
	def __init__(self, max_requests=MAX_REQUESTS):
		self.requests = 0
		self.max_requests = max_requests
		self.effective_urls = {}

	def download(self, original, url, path):
		for attempt in range(MAX_ATTEMPTS):
			if self.requests >= self.max_requests:
				raise RuntimeError(f"fetch request cap reached ({self.max_requests}); stop and review coverage")
			self.requests += 1
			try:
				parts=urlsplit(url)
				is_overpass=parts.netloc in (urlsplit(OVERPASS).netloc,urlsplit(APPROVED_MIRROR).netloc)
				actual=(APPROVED_MIRROR+'?'+parts.query) if is_overpass and attempt>0 else url
				result=original(actual,path,timeout=200 if is_overpass else 110,retries=1)
				self.effective_urls[url]=actual
				return result
			except RuntimeError as error:
				if attempt == MAX_ATTEMPTS-1:
					raise RuntimeError(f'request failed after {MAX_ATTEMPTS} bounded attempts: {error}') from error
				delay=min(120,30 * 2**attempt) + random.uniform(0,5)
				print(f'[buildings] {urlsplit(url).netloc}: retry {attempt+2}/{MAX_ATTEMPTS} after {delay:.1f} s',flush=True)
				time.sleep(delay)


def needs_nj(boundary, bbox, land=None):
	from shapely.geometry import box
	import shapely
	from ferrydata.buildings.geometry import polygons
	region = boundary.intersection(box(*bbox))
	if land is not None:
		region = region.intersection(shapely.union_all(list(polygons(land,min_area=0))))
	return region.area > 0


def overpass_url(cache, name, query, endpoint):
	if endpoint not in (OVERPASS,APPROVED_MIRROR):
		raise ValueError('unapproved Overpass endpoint')
	url=endpoint+'?'+urlencode({'data':query})
	entry=pins(cache).files.get(name)
	if entry:
		old=urlsplit(entry['url'])
		if old.query!=urlsplit(url).query or old.scheme+'://'+old.netloc+old.path not in (OVERPASS,APPROVED_MIRROR):
			raise ValueError(f'URL changed for pinned {name}')
		return entry['url']
	return url


def nj_boundary(cache, policy):
	from shapely.geometry import LineString
	from shapely.ops import polygonize
	import shapely
	query = '[out:json][timeout:90];relation["ISO3166-2"="US-NJ"][admin_level=4];out geom;'
	d = download(cache,"osm-nj-boundary.json",OVERPASS+"?"+urlencode({"data":query}),kind="overpass",pause=2.,policy=policy)
	outer,inner = [],[]
	for e in d['elements']:
		for m in e.get('members',[]):
			pts=[(p['lon'],p['lat']) for p in m.get('geometry',[]) if p]
			if len(pts)>=2 and m.get('role') in ('outer','inner',''):
				(inner if m.get('role')=='inner' else outer).append(LineString(pts))
	boundary=shapely.union_all(list(polygonize(outer))).difference(shapely.union_all(list(polygonize(inner))))
	if boundary.is_empty or not boundary.is_valid:
		raise ValueError('NJ state boundary is incomplete; refusing guessed east/west skip rules')
	return boundary


def cache_default():
	return paths.raw_dir("buildings")


def pins(cache):
	cache=Path(cache)
	m=manifest.Manifest.load("buildings",directory=cache)
	bundle=Path(__file__).with_name('source-manifest.json')
	if not m.path.exists() and cache==cache_default() and bundle.exists():
		snapshot=read_json(bundle)
		m.header={k:v for k,v in snapshot.items() if k!='files'}
		m.files={entry['name']:entry for entry in snapshot['files']}
	return m


def download(cache, name, url, force=False, kind="json", pause=1.0, policy=None):
	cache = Path(cache)
	m = pins(cache)
	if name in m.files and m.files[name]["url"] != url and not force:
		raise ValueError(f"URL changed for pinned {name}; use --force to accept a new query")
	m.describe(description="Approved harbor footprint sources, pinned sequential API responses", license="NYC Open Data terms; OSM ODbL 1.0")
	def validate(path):
		d = read_json(path)
		if kind == "nyc" and not isinstance(d,list):
			raise ValueError("NYC API did not return a row array")
		if kind == "overpass" and ("elements" not in d or d.get("remark")):
			raise ValueError(f"Overpass incomplete response: {d.get('remark', 'missing elements')}")
		if kind=='overpass' and not d.get('osm3s',{}).get('timestamp_osm_base'):
			raise ValueError('Overpass response missing osm_base timestamp')
	original = fetcher._download
	original_agent = fetcher.USER_AGENT
	try:
		if policy is not None:
			fetcher._download = lambda u,p: policy.download(original,u,p)
		fetcher.USER_AGENT = "ferrydata-buildings/0.1 (NYC Ferry Simulator; public LoD1 skyline; sequential cached research)"
		state = fetcher.fetch_file(m,name,url,cache/name,force=force,validate=validate)
	finally:
		fetcher._download = original
		fetcher.USER_AGENT = original_agent
	if state == "downloaded":
		time.sleep(pause)
	data=read_json(cache/name)
	if policy is not None and url in policy.effective_urls:
		m.files[name]['url']=policy.effective_urls[url]
	if kind=='overpass':
		entry=m.files[name]
		snapshot=data.get('osm3s',{}).get('timestamp_osm_base')
		if not snapshot:
			raise ValueError(f'Overpass response missing osm_base timestamp: {name}')
		parts=urlsplit(entry['url'])
		entry.update(osm_base=snapshot,endpoint=parts.scheme+'://'+parts.netloc+parts.path)
		m.save()
	return data


def metadata(cache=None,policy=None):
	cache = Path(cache or cache_default())
	return download(cache,"nyc-metadata.json", f"https://data.cityofnewyork.us/api/views/{NYC_ID}.json",policy=policy)


def fetch_nyc(cache, active, force=False, policy=None, log=print):
	"""Download NYC independently; never publish an incomplete combined inventory."""
	cache=Path(cache)
	policy=policy or RequestPolicy()
	meta=metadata(cache,policy)
	columns={c['fieldName'] for c in meta['columns']}
	geom=next((x for x in ('the_geom','shape') if x in columns),None)
	if geom is None:
		raise ValueError(f'NYC API missing geometry column: {sorted(columns)}')
	key=next(x for x in ('doitt_id','doittid') if x in columns)
	inventory=[]
	for i,j in active:
		tile=tiles.make_tile(i,j,margin_m=0)
		w,s,e,n=tile.bbox
		page=0
		while True:
			name=f'nyc_{i}_{j}_{page:04}.json'
			query={'$where':f"intersects({geom}, 'POLYGON(({w} {s},{e} {s},{e} {n},{w} {n},{w} {s}))')",
				'$order':key,'$limit':PAGE_SIZE,'$offset':page*PAGE_SIZE}
			rows=download(cache,name,NYC_API+'?'+urlencode(query),force,kind='nyc',policy=policy)
			inventory.append({'file':name,'source':'nyc','rows':len(rows)})
			log(f'[buildings] {name}: {len(rows)} rows')
			if len(rows)<PAGE_SIZE:
				break
			page+=1
	return inventory


def fetch_overpass(harbor_dir=None, cache=None, force=False, log=print):
	"""Fetch NYC and 1 km NJ land subchunks; publish explicit incomplete coverage after a bounded failure."""
	from shapely.geometry import shape
	import shapely
	cache=Path(cache or cache_default())
	harbor_dir=Path(harbor_dir or paths.processed_dir('harbor'))
	cache.mkdir(parents=True,exist_ok=True)
	active,_=active_tiles(harbor_dir)
	policy=RequestPolicy()
	inventory=fetch_nyc(cache,active,force,policy,log)
	boundary=nj_boundary(cache,policy)
	plans=[]
	m=pins(cache)
	for i,j in active:
		tile=tiles.make_tile(i,j,margin_m=0)
		land=shapely.union_all([shape(f['geometry']) for f in read_json(harbor_dir/'tiles'/tile.id/'land_areas.geojson')['features'] if f.get('geometry')])
		whole=f'osm_nj_{i}_{j}.json'
		whole_cached=whole in m.files and m.verify(whole,cache/whole)
		for row in range(4):
			for col in range(4):
				bbox=tiles._square(i*4000+row*1000,j*4000+col*1000,i*4000+(row+1)*1000,j*4000+(col+1)*1000).bounds
				if not needs_nj(boundary,bbox,land):
					continue
				name=f'osm_nj_{i}_{j}_{row}_{col}.json'
				cached=whole if whole_cached else (name if name in m.files and m.verify(name,cache/name) else None)
				plans.append({'id':f'{tile.id}/c{row}_{col}','bbox':list(bbox),'file':cached,'request_file':name})
	actual_files={}
	stopped=False
	failure=None
	for p in plans:
		name=p['file']
		if name is not None:
			if name not in actual_files:
				d=download(cache,name,m.files[name]['url'],kind='overpass',pause=2.,policy=policy)
				actual_files[name]={'file':name,'source':'osm-nj','rows':len(d['elements'])}
			p['status']='cached'
			continue
		if stopped:
			p['status']='missing'
			continue
		name=p['request_file']
		w,s,e,n=p['bbox']
		query=f'[out:json][timeout:180];area["ISO3166-2"="US-NJ"][admin_level=4]->.nj;(way[building](area.nj)({s},{w},{n},{e});relation[building][type=multipolygon](area.nj)({s},{w},{n},{e}););out geom;'
		try:
			d=download(cache,name,overpass_url(cache,name,query,OVERPASS),force=force,kind='overpass',pause=2.,policy=policy)
			actual_files[name]={'file':name,'source':'osm-nj','rows':len(d['elements'])}
			p.update(file=name,status='downloaded')
			log(f"[buildings] {name}: {len(d['elements'])} elements")
		except (RuntimeError,ValueError) as error:
			stopped=True
			failure=str(error)
			p['status']='missing'
			log(f"[buildings] stopped NJ acquisition at {p['id']}: {error}")
	missing=[p['id'] for p in plans if not p['file']]
	for p in plans:
		p.pop('request_file')
	inventory.extend(actual_files[name] for name in sorted(actual_files))
	coverage={'complete':not missing,'nj_subchunk_size_m':1000,'chunks':plans,'missing':missing,'failure':failure}
	document={'schema':'harbor-building-raw-v1','nyc_dataset':NYC_ID,'active':active,'files':inventory,
		'support_files':['nyc-metadata.json','osm-nj-boundary.json'],'coverage':coverage}
	write_json(cache/'inventory.json',document)
	log(f"[buildings] NJ coverage: {len(plans)-len(missing)}/{len(plans)} land subchunks, {len(missing)} missing")
	return document


def verified_inventory(cache):
	cache = Path(cache)
	inv = read_json(cache/"inventory.json")
	m = pins(cache)
	for name in [e['file'] for e in inv['files']]+inv.get('support_files',[]):
		if not m.verify(name,cache/name):
			raise ValueError(f"raw cache hash mismatch: {name}")
	return inv,m


def fetch(harbor_dir=None,cache=None,force=False,log=print):
	"""Approved final route: pinned NYC pages and one uniform NJ Geofabrik snapshot; no Overpass fallback."""
	from ferrydata.buildings import bulk
	return bulk.fetch_inventory(harbor_dir,cache,force,log)
