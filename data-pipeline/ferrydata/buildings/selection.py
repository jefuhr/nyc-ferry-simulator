"""Explicit water-view skyline policies, priced offline before selecting a product default."""
from pathlib import Path
import shapely
from shapely.geometry import shape
from ferrydata.buildings import geometry
from ferrydata.world3d.build import active_tiles,read_json

DEFAULT_POLICY='B'
POLICIES={'A':(None,None),'B':(600.,25.),'C':(400.,40.)}


class Shoreline:
	def __init__(self,lines):
		self.lines=[line for line in lines if not line.is_empty and line.length>0]
		if not self.lines:
			raise ValueError('no real ENC COALNE shoreline; refusing artificial tile-boundary distances')
		self.tree=shapely.STRtree(self.lines)

	def distance(self,footprint):
		return float(footprint.distance(self.lines[int(self.tree.nearest(footprint))]))


def from_harbor(harbor):
	"""Actual ENC COALNE lines, never clipped land polygon boundaries which include artificial tile edges."""
	harbor=Path(harbor)
	active,_=active_tiles(harbor)
	lines=[]
	for i,j in active:
		for f in read_json(harbor/'tiles'/f'tile_{i}_{j}'/'shoreline_constructions.geojson')['features']:
			if f['properties'].get('feature_class')=='COALNE' and f.get('geometry'):
				g=geometry.local(shape(f['geometry']))
				if g.geom_type in ('LineString','MultiLineString'):
					lines.append(g)
	return Shoreline(lines)


def keep(footprint,height_m,shoreline,policy):
	radius,minimum=POLICIES[policy]
	return radius is None or height_m>=minimum or shoreline.distance(footprint)<=radius


def policy_record(policy):
	radius,minimum=POLICIES[policy]
	return {'id':policy,'shoreline_distance_m':radius,'inland_minimum_height_above_ground_m':minimum,
		'shoreline_source':'ENC COALNE, not tile/land clipping boundaries',
		'selection_stage':'original footprint before tile/land clipping',
		'notes':'All cached building footprints retained' if radius is None else 'Inland low-rise omitted: not visible from the water; water-view heuristic, not an occlusion proof'}
