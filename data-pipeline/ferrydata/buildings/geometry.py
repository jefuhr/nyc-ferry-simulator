"""SI normalization, nonoverlapping footprint pieces, and curved LoD1 prisms."""
import json
import math
import re
import numpy as np
import shapely
from shapely.geometry import Polygon, MultiPolygon, box
from shapely.geometry.polygon import orient
from ferrydata import geodesy, tiles
from ferrydata.world3d import frames, heights

SIMPLIFY_M = 0.35
DEFAULT_HEIGHT_M = 9.
LEVEL_HEIGHT_M = 3.
MIN_HEIGHT_M = 1.
MAX_HEIGHT_M = 650.


def number(value):
	try:
		v = float(value)
		return v if math.isfinite(v) else None
	except (TypeError,ValueError):
		return None


def length(value):
	if value is None:
		return None
	m = re.fullmatch(r"\s*([+-]?[\d.]+)\s*(m|metres|meters|ft|feet|')?\s*",str(value))
	if not m:
		return None
	v = number(m[1])
	return v*(.3048 if m[2] in ("ft","feet", "'") else 1) if v is not None else None


def dimensions(source, tags, terrain_ground):
	if source == "nyc":
		h = number(tags.get("heightroof", tags.get("height_roof")))
		h = h*.3048 if h is not None else None
		ground = number(tags.get("groundelev", tags.get("ground_elevation")))
		ground = ground*.3048 if ground is not None else None
		levels = number(tags.get("num_floors"))
		basis = "heightroof-feet"
	else:
		h,ground = length(tags.get("height")),None
		levels = number(tags.get("building:levels"))
		basis = "height"
	if h is None or not MIN_HEIGHT_M <= h <= MAX_HEIGHT_M:
		if levels is not None and MIN_HEIGHT_M <= levels*LEVEL_HEIGHT_M <= MAX_HEIGHT_M:
			h,basis = levels*LEVEL_HEIGHT_M,"levels-estimate"
		else:
			h,basis = DEFAULT_HEIGHT_M,"default-estimate"
	ground_basis = "nyc-groundelev-feet" if ground is not None else "terrain-navd88-estimate"
	if ground is None:
		ground = number(terrain_ground)
	if ground is None or not -20 <= ground <= 250:
		raise ValueError("no sane NAVD88 ground elevation")
	use=tags.get("building:use") or tags.get("building") or tags.get("use")
	if use in ("yes","no"):
		use=None
	return {"height_m":h,"ground_navd88_m":ground,"roof_navd88_m":ground+h,"height_basis":basis,
		"ground_basis":ground_basis,"levels":levels,"use":use}


def local(geometry):
	def project(x,y,z=None):
		n,e = geodesy.to_local(np.asarray(y),np.asarray(x))
		return np.round(e,8),np.round(n,8)
	return shapely.transform(geometry,project,interleaved=False)


def geographic(geometry):
	def project(e,n,z=None):
		lat,lon = geodesy.from_local(np.asarray(n),np.asarray(e))
		return lon,lat
	return shapely.transform(geometry,project,interleaved=False)


def polygons(geometry,min_area=.01):
	if isinstance(geometry,Polygon):
		if not geometry.is_empty and geometry.area > min_area:
			yield geometry
	elif hasattr(geometry,"geoms"):
		for g in geometry.geoms:
			yield from polygons(g,min_area)


def canonical(p):
	# Canonical ring starts and component order make API row/ring order immaterial.
	return orient(shapely.normalize(p),sign=1.)


def clip_parts(footprint, active, land, exclusion=None):
	if not footprint.is_valid:
		footprint = shapely.make_valid(footprint)
	# Simplify before clipping so every tile sees the same straight seam intersections.
	footprint = footprint.simplify(SIMPLIFY_M,preserve_topology=True).intersection(land)
	if exclusion is not None:
		footprint = footprint.difference(exclusion)
	parts = []
	for i,j in sorted(active):
		bounds = box(j*tiles.SIZE_M,i*tiles.SIZE_M,(j+1)*tiles.SIZE_M,(i+1)*tiles.SIZE_M)
		for p in sorted(polygons(footprint.intersection(bounds)),key=lambda g:shapely.normalize(g).wkb):
			parts.append(((i,j),canonical(p)))
	return parts


def deduplicate(features):
	seen = {}
	for feature in features:
		key = feature["id"]
		if key in seen and seen[key] != feature:
			raise ValueError(f"conflicting duplicate building {key}")
		seen[key] = feature
	return [seen[k] for k in sorted(seen)]


def extrude(footprint, ground, roof, frame, geoid_at):
	"""Closed prism with courtyard walls, face normals and outward CCW winding, through ECEF."""
	if not (footprint.is_valid and not footprint.is_empty and roof > ground):
		raise ValueError("invalid footprint or prism heights")
	p = canonical(footprint)
	vertices,faces,wall_ranges = [],[],[]
	rings=[np.asarray(r.coords)[:-1] for r in [p.exterior,*p.interiors]]
	xy=np.concatenate(rings)
	lookup={tuple(point):k for k,point in enumerate(xy)}
	count=len(xy)
	# Separate cap vertices preserve horizontal roof/base normals; adjacent wall edges share corner normals.
	vertices.extend((float(e),float(n),roof) for e,n in xy)
	vertices.extend((float(e),float(n),ground) for e,n in xy)
	for tri in shapely.constrained_delaunay_triangles(p).geoms:
		keys=[lookup[tuple(point)] for point in np.asarray(orient(tri,sign=1.).exterior.coords)[:3]]
		faces.append(tuple(keys))
		faces.append(tuple(count+k for k in keys[::-1]))
	for ring in rings:
		start=len(vertices)
		first_face=len(faces)
		n=len(ring)
		vertices.extend((float(e),float(n),ground) for e,n in ring)
		vertices.extend((float(e),float(n),roof) for e,n in ring)
		for k in range(n):
			next_k=(k+1)%n
			faces.extend([(start+k,start+next_k,start+next_k+n),(start+k,start+next_k+n,start+k+n)])
		wall_ranges.append((start,n,first_face))
	v = np.asarray(vertices,dtype=np.float64)
	lat,lon = geodesy.from_local(v[:,1],v[:,0])
	h = v[:,2]+geoid_at(v[:,1],v[:,0])
	positions = frames.gltf_from_enu(frame.enu(heights.surface_points(lat,lon,h)))
	idx = np.asarray(faces,dtype=np.uint32)
	normals = np.zeros_like(positions)
	cross = np.cross(positions[idx[:,1]]-positions[idx[:,0]],positions[idx[:,2]]-positions[idx[:,0]])
	for k in range(3):
		np.add.at(normals,idx[:,k],cross)
	for start,n,first_face in wall_ranges:
		face=cross[first_face:first_face+2*n:2]
		unit=face/np.linalg.norm(face,axis=1,keepdims=True)
		corner=unit+np.roll(unit,1,axis=0)
		normals[start:start+n]=corner
		normals[start+n:start+2*n]=corner
	# Touching courtyard/exterior rings can share XY points. The triangulator may legitimately leave duplicate
	# cap vertices unused; remove those slots rather than treating an unused zero normal as invalid geometry.
	used=np.unique(idx)
	remap=np.zeros(len(positions),dtype=np.uint32)
	remap[used]=np.arange(len(used),dtype=np.uint32)
	positions,normals,idx=positions[used],normals[used],remap[idx]
	norm = np.linalg.norm(normals,axis=1,keepdims=True)
	if not np.isfinite(positions).all() or not np.isfinite(normals).all() or np.any(norm == 0):
		raise ValueError("nonfinite or degenerate extrusion")
	normals /= norm
	colors = np.tile(np.array([160,163,168,255],dtype=np.uint8),(len(positions),1))
	return positions.astype(np.float32),normals.astype(np.float32),colors,idx.ravel()
