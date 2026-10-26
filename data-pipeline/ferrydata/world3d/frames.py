"""Each harbor tile's east-north-up frame through ECEF, its 3D Tiles transform, glTF's axes and regions.

Tile (i, j) of harbor tiles v1 covers north [4000 i, 4000 (i + 1)) and east [4000 j, 4000 (j + 1)) metres of the world
frame (docs/conventions.md). Its content is written in an east-north-up frame whose origin is the tile's centre on the
WGS84 ellipsoid (height 0), found through ferrydata.geodesy, so every vertex goes through ECEF from its own latitude,
longitude and ellipsoidal height: curvature is in the tiles, and nothing corrects vertices at run time. The tile's 3D
Tiles `transform` pins that frame to the globe.

glTF is y-up: content is written as (x east, y up, z south), which the 3D Tiles 1.1 y-up to z-up rotation turns back
into the tile's (x east, y north, z up). Both are proper rotations, so triangles keep their winding.
"""

import math

import numpy as np

from ferrydata import geodesy, tiles

SIZE_M = float(tiles.SIZE_M)

# 3D Tiles 1.1: glTF content is y-up; a runtime rotates it about x by +90 degrees into the tile's z-up frame.
Y_UP_TO_Z_UP = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]])


def tile_id(i, j):
	return f"tile_{i}_{j}"


class TileFrame:
	"""East-north-up frame at a tile's centre on the WGS84 ellipsoid (height 0)."""

	def __init__(self, i, j):
		self.i, self.j = int(i), int(j)
		self.id = tile_id(self.i, self.j)
		self.north0, self.north1 = SIZE_M * self.i, SIZE_M * (self.i + 1)
		self.east0, self.east1 = SIZE_M * self.j, SIZE_M * (self.j + 1)
		self.centre_north = 0.5 * (self.north0 + self.north1)
		self.centre_east = 0.5 * (self.east0 + self.east1)
		lat, lon = geodesy.from_local(self.centre_north, self.centre_east)
		self.latitude, self.longitude = float(lat), float(lon)
		self.origin = np.array([float(v) for v in geodesy.to_ecef(self.latitude, self.longitude, 0.0)])
		phi, lam = math.radians(self.latitude), math.radians(self.longitude)
		self.east = np.array([-math.sin(lam), math.cos(lam), 0.0])
		self.north = np.array([-math.sin(phi) * math.cos(lam), -math.sin(phi) * math.sin(lam), math.cos(phi)])
		self.up = np.array([math.cos(phi) * math.cos(lam), math.cos(phi) * math.sin(lam), math.sin(phi)])
		self.rotation = np.stack([self.east, self.north, self.up])  # rows: ECEF -> ENU

	def enu(self, ecef):
		"""ENU (x east, y north, z up) in this frame of ECEF points, (..., 3) in and out, float64."""
		return (np.asarray(ecef, dtype=np.float64) - self.origin) @ self.rotation.T

	def enu_vectors(self, vectors):
		"""Directions (normals) from ECEF into this frame."""
		return np.asarray(vectors, dtype=np.float64) @ self.rotation.T

	def ecef(self, enu):
		return np.asarray(enu, dtype=np.float64) @ self.rotation + self.origin

	def transform(self):
		"""3D Tiles `transform`: the column-major 4x4 from this frame (z up) to ECEF."""
		values = []
		for column in (self.east, self.north, self.up):
			values.extend(float(v) for v in column)
			values.append(0.0)
		values.extend(float(v) for v in self.origin)
		values.append(1.0)
		return values


def matrix(transform):
	"""The 4x4 of a 3D Tiles column-major `transform` list."""
	return np.array(transform, dtype=np.float64).reshape(4, 4).T


def gltf_from_enu(enu):
	"""(x east, y up, z south): glTF's y-up axes for tile-frame points or directions."""
	enu = np.asarray(enu)
	return np.stack([enu[..., 0], enu[..., 2], -enu[..., 1]], axis=-1)


def enu_from_gltf(gltf):
	gltf = np.asarray(gltf)
	return np.stack([gltf[..., 0], -gltf[..., 2], gltf[..., 1]], axis=-1)


def ecef_from_content(gltf_positions, transform):
	"""ECEF of glTF content positions under a tile's `transform`: y-up to z-up, then the transform, in float64."""
	m = matrix(transform)
	enu = np.asarray(gltf_positions, dtype=np.float64) @ Y_UP_TO_Z_UP.T
	return enu @ m[:3, :3].T + m[:3, 3]


# Regions are padded so content on their edges stays inside after rounding: about 1 m horizontally and vertically.
REGION_PAD_RAD = 1.6e-7
REGION_PAD_M = 1.0


def region(lat_deg, lon_deg, height_m):
	"""3D Tiles `region` [west, south, east, north, minimum height, maximum height] (radians, metres) around points."""
	lat, lon, height = (np.asarray(v, dtype=np.float64) for v in (lat_deg, lon_deg, height_m))
	return [math.radians(float(lon.min())) - REGION_PAD_RAD, math.radians(float(lat.min())) - REGION_PAD_RAD,
		math.radians(float(lon.max())) + REGION_PAD_RAD, math.radians(float(lat.max())) + REGION_PAD_RAD,
		float(height.min()) - REGION_PAD_M, float(height.max()) + REGION_PAD_M]


def union_region(regions):
	regions = np.asarray(regions, dtype=np.float64)
	return [float(regions[:, 0].min()), float(regions[:, 1].min()), float(regions[:, 2].max()),
		float(regions[:, 3].max()), float(regions[:, 4].min()), float(regions[:, 5].max())]
