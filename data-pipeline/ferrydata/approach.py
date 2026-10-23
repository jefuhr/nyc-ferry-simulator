"""Finding the landing face and the water side that a ferry approaches from. All coordinates are (east, north)
metres for shapely geometries and (north, east) metres for the vectors passed to the depth box, as noted per function."""

import math

from shapely.geometry import MultiLineString, Point
from shapely.ops import nearest_points

PROBE_NEAR_M = 12.0
PROBE_ROUTE_M = 60.0


def _boundary(geometry):
	return geometry.boundary if geometry.geom_type in ("Polygon", "MultiPolygon") else geometry


def _nearest_component(line, point):
	if isinstance(line, MultiLineString) or line.geom_type == "GeometryCollection":
		return min(line.geoms, key=lambda part: part.distance(point))
	return line


def face_geometry(landing_en, shoreline_geoms):
	"""Nearest point on the nearest shoreline feature and the unit tangent there.

	landing_en is (east, north); returns (anchor (east, north), tangent (east, north))."""
	point = Point(landing_en)
	nearest = min(shoreline_geoms, key=lambda g: g.distance(point))
	line = _nearest_component(_boundary(nearest), point)
	_, anchor = nearest_points(point, line)
	along = line.project(anchor)
	before = line.interpolate(max(along - 1.0, 0.0))
	after = line.interpolate(min(along + 1.0, line.length))
	dx, dy = after.x - before.x, after.y - before.y
	norm = math.hypot(dx, dy) or 1.0
	return (anchor.x, anchor.y), (dx / norm, dy / norm)


def choose_outward(anchor_ne, tangent_en, is_water, route=None):
	"""Pick the face normal (as (north, east)) pointing at the water the ferry approaches from.

	anchor_ne is (north, east); tangent_en is (east, north); is_water(north, east) -> bool;
	route is a shapely line in (east, north) or None. Returns (outward (north, east) or None, how):

	- only-water-side: one normal has water at the probe, the other does not
	- route-side: both (or, with the -dry suffix, neither) probes are water; the side the route runs along wins
	- ambiguous: both candidates remain and there is no route to decide; the caller must evaluate both
	  (with the -dry suffix, no probe found water at all)
	"""
	# Normals of a tangent (te, tn): (-tn, te) in (east, north); convert to (north, east).
	te, tn = tangent_en
	candidates = [(te, -tn), (-te, tn)]
	wet = [c for c in candidates if is_water(anchor_ne[0] + PROBE_NEAR_M * c[0], anchor_ne[1] + PROBE_NEAR_M * c[1])]
	if len(wet) == 1:
		return wet[0], "only-water-side"
	suffix = "" if wet else "-dry"
	contenders = wet or candidates
	if route is None or route.is_empty:
		return None, "ambiguous" + suffix
	far = [Point(anchor_ne[1] + PROBE_ROUTE_M * c[1], anchor_ne[0] + PROBE_ROUTE_M * c[0]) for c in contenders]
	nearer = min(range(len(contenders)), key=lambda i: route.distance(far[i]))
	return contenders[nearer], "route-side" + suffix
