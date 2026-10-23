"""World-local tangent-plane maths from docs/conventions.md.

WGS84 geodetic -> ECEF -> NED through the shared origin (40.7000, -74.0150, h = 0). Mirrors
core/FerrySim.Core/Contracts/Geodesy/LocalTangentPlane.cs. Never use a flat lat/lon scaling.
"""

import numpy as np

ORIGIN_LAT = 40.7000
ORIGIN_LON = -74.0150

SEMI_MAJOR_AXIS = 6378137.0
FLATTENING = 1.0 / 298.257223563
ECCENTRICITY_SQUARED = FLATTENING * (2.0 - FLATTENING)

_GEODETIC_ITERATIONS = 6
_SURFACE_ITERATIONS = 4


def to_ecef(lat_deg, lon_deg, height=0.0):
	lat = np.radians(np.asarray(lat_deg, dtype=float))
	lon = np.radians(np.asarray(lon_deg, dtype=float))
	sin_lat = np.sin(lat)
	cos_lat = np.cos(lat)
	prime_vertical = SEMI_MAJOR_AXIS / np.sqrt(1.0 - ECCENTRICITY_SQUARED * sin_lat * sin_lat)
	x = (prime_vertical + height) * cos_lat * np.cos(lon)
	y = (prime_vertical + height) * cos_lat * np.sin(lon)
	z = (prime_vertical * (1.0 - ECCENTRICITY_SQUARED) + height) * sin_lat
	return x, y, z


def from_ecef(x, y, z):
	"""Returns (lat_deg, lon_deg, height) with a fixed iteration count."""
	x = np.asarray(x, dtype=float)
	y = np.asarray(y, dtype=float)
	z = np.asarray(z, dtype=float)
	p = np.sqrt(x * x + y * y)
	lon = np.arctan2(y, x)
	lat = np.arctan2(z, p * (1.0 - ECCENTRICITY_SQUARED))
	height = np.zeros_like(p)
	for _ in range(_GEODETIC_ITERATIONS):
		sin_lat = np.sin(lat)
		prime_vertical = SEMI_MAJOR_AXIS / np.sqrt(1.0 - ECCENTRICITY_SQUARED * sin_lat * sin_lat)
		height = p / np.cos(lat) - prime_vertical
		lat = np.arctan2(z, p * (1.0 - ECCENTRICITY_SQUARED * prime_vertical / (prime_vertical + height)))
	return np.degrees(lat), np.degrees(lon), height


_origin_ecef = to_ecef(ORIGIN_LAT, ORIGIN_LON, 0.0)
_sin_lat = np.sin(np.radians(ORIGIN_LAT))
_cos_lat = np.cos(np.radians(ORIGIN_LAT))
_sin_lon = np.sin(np.radians(ORIGIN_LON))
_cos_lon = np.cos(np.radians(ORIGIN_LON))


def to_ned(lat_deg, lon_deg, height=0.0):
	"""North, east, down in metres of geodetic points at an ellipsoidal height, relative to the origin."""
	x, y, z = to_ecef(lat_deg, lon_deg, height)
	dx = x - _origin_ecef[0]
	dy = y - _origin_ecef[1]
	dz = z - _origin_ecef[2]
	north = -_sin_lat * _cos_lon * dx - _sin_lat * _sin_lon * dy + _cos_lat * dz
	east = -_sin_lon * dx + _cos_lon * dy
	down = -_cos_lat * _cos_lon * dx - _cos_lat * _sin_lon * dy - _sin_lat * dz
	return north, east, down


def to_local(lat_deg, lon_deg):
	"""Simulation world position (north_m, east_m) of points on the ellipsoid surface."""
	north, east, _ = to_ned(lat_deg, lon_deg, 0.0)
	return north, east


def from_local(north_m, east_m):
	"""Ellipsoid-surface (lat_deg, lon_deg) whose world position is (north_m, east_m)."""
	north = np.asarray(north_m, dtype=float)
	east = np.asarray(east_m, dtype=float)
	down = np.zeros_like(north + east)
	lat = lon = None
	for _ in range(_SURFACE_ITERATIONS):
		x = _origin_ecef[0] - _sin_lat * _cos_lon * north - _sin_lon * east - _cos_lat * _cos_lon * down
		y = _origin_ecef[1] - _sin_lat * _sin_lon * north + _cos_lon * east - _cos_lat * _sin_lon * down
		z = _origin_ecef[2] + _cos_lat * north - _sin_lat * down
		lat, lon, height = from_ecef(x, y, z)
		down = down + height
	return lat, lon
