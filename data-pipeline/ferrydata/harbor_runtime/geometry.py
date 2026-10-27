"""Resident query geometry and one continuous datum lattice, all in world north/east metres."""
import json
import math
from pathlib import Path

import numpy as np
from shapely import make_valid, normalize
from shapely.geometry import box, mapping, shape
from shapely.ops import orient, unary_union

from ferrydata.world3d.heights import Sources

GEOID_CELL_M = 250.


def local_geometry(feature):
	return shape(dict(type=feature['geometry']['type'], coordinates=feature['local_coordinates']))


def canonical(geometry,*,solid_right=False):
	# Normalize first for stable ring starts and component/hole order. In N/E axes,
	# CCW exteriors put (deltaEast, -deltaNorth) outside; holes wind oppositely.
	geometry=normalize(geometry)
	if solid_right:
		geometry=orient(geometry,sign=1.)
	return mapping(geometry)


def domain(entries):
	# Shapely x/y are north/east here, deliberately not longitude/latitude or Unity axes.
	return unary_union([box(*entry['bounds_local_m']) for entry in entries])


def parts(geometry):
	if geometry.geom_type.startswith('Multi') or geometry.geom_type == 'GeometryCollection':
		return [part for child in geometry.geoms for part in parts(child)]
	return [] if geometry.is_empty else [geometry]


def merge_geometry(features, coverage):
	geometries = [make_valid(local_geometry(f)) for f in features]
	merged = unary_union(geometries).intersection(coverage)
	if not merged.is_valid:
		raise ValueError('invalid merged footprint')
	return merged


def geoid_grid(harbor, entries, cell_m=GEOID_CELL_M):
	"""Sample existing terrain datum owners once per shared node; bilinear consumers never switch tile fields."""
	coverage = domain(entries)
	n0,e0,n1,e1 = coverage.bounds
	rows,cols = int(round((n1-n0)/cell_m))+1,int(round((e1-e0)/cell_m))+1
	if any(abs(v/cell_m-round(v/cell_m)) > 1e-9 for v in (n0,e0,n1,e1)):
		raise ValueError('geoid domain is not on the canonical lattice')
	north,east = np.meshgrid(n0+np.arange(rows)*cell_m,e0+np.arange(cols)*cell_m,indexing='ij')
	sources = Sources(harbor,[(e['i'],e['j']) for e in entries])
	values = sources.geoid_at(north,east)
	mask = np.zeros((rows-1,cols-1),dtype=np.uint8)
	for entry in entries:
		a,b,c,d = entry['bounds_local_m']
		mask[round((a-n0)/cell_m):round((c-n0)/cell_m),round((b-e0)/cell_m):round((d-e0)/cell_m)] = 1
	used = np.zeros((rows,cols),dtype=bool)
	for dr,dc in ((0,0),(0,1),(1,0),(1,1)):
		used[dr:dr+rows-1,dc:dc+cols-1] |= mask.astype(bool)
	if not np.isfinite(values[used]).all():
		raise ValueError('geoid has missing nodes in active coverage')
	# Unused values are explicitly masked; zero storage avoids platform-dependent NaN payloads.
	values[~used] = 0.
	metadata = dict(format='ferrysim-harbor-geoid/1', origin_north_m=n0, origin_east_m=e0, cell_m=cell_m, rows=rows,cols=cols,
		values_file='geoid.f64',values_type='little-endian float64',layout='row-major south-to-north; columns west-to-east',
		mask_file='geoid_cells.u8',mask_shape=[rows-1,cols-1],mask_meaning='1 active cell, 0 outside; no extrapolation',
		value='ellipsoidal height of NAVD88 zero, metres; add to NAVD88 elevation',
		method='terrain owner at each node; continuous bilinear interpolation on one 250 m lattice',
		ellipsoid_note='Source GEOID18 uses NAD83(2011)/GRS80 treated as WGS84, retaining the existing frame approximation')
	return metadata,np.asarray(values,dtype='<f8'),mask


def sample_geoid(directory,north,east):
	"""Reference scalar sampler for the proposed 3b reader; reject holes/outside, including at the bounding rectangle."""
	directory=Path(directory)
	m=json.loads((directory/'geoid.json').read_text())
	values=np.frombuffer((directory/m['values_file']).read_bytes(),dtype='<f8').reshape(m['rows'],m['cols'])
	mask=np.frombuffer((directory/m['mask_file']).read_bytes(),dtype=np.uint8).reshape(m['mask_shape'])
	r=(north-m['origin_north_m'])/m['cell_m']; c=(east-m['origin_east_m'])/m['cell_m']
	if not math.isfinite(r+c) or not 0 <= r <= m['rows']-1 or not 0 <= c <= m['cols']-1:
		raise ValueError('outside geoid coverage')
	ri,ci=min(math.floor(r),m['rows']-2),min(math.floor(c),m['cols']-2)
	# An exact edge/corner can use any adjacent active cell; values are the same shared nodes.
	candidates=[(a,b) for a in (ri,ri-1) for b in (ci,ci-1)
		if 0 <= a < mask.shape[0] and 0 <= b < mask.shape[1] and a <= r <= a+1 and b <= c <= b+1 and mask[a,b]]
	if not candidates:
		raise ValueError('outside geoid active cells')
	ri,ci=candidates[0]; u,v=r-ri,c-ci
	return float((values[ri,ci]*(1-v)+values[ri,ci+1]*v)*(1-u)+(values[ri+1,ci]*(1-v)+values[ri+1,ci+1]*v)*u)
