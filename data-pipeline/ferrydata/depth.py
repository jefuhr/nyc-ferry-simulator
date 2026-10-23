"""Depth sampling under a landing approach box.

Depth = water level (NAVD88) minus seabed elevation (NAVD88). The approach box is a 30 m by 30 m square whose
near edge lies on the landing face and which extends out over the water.
"""

from dataclasses import dataclass

import numpy as np
from rasterio import features, windows
from shapely.geometry import Polygon

from ferrydata import geodesy


@dataclass
class Sample:
	values: np.ndarray  # elevations of valid cells, NAVD88 metres
	nodata_count: int
	cell_count: int  # cells under the polygon, valid or not


def landing_box_local(anchor, outward, size_m=30.0):
	"""Corners (north, east) of the box: near edge centred on `anchor`, running size_m across the face and out along `outward`."""
	norm = float(np.hypot(*outward))
	n = (outward[0] / norm, outward[1] / norm)  # unit vector away from the face
	t = (-n[1], n[0])  # unit vector along the face
	half = size_m / 2.0
	corners = []
	for along, out in ((-half, 0.0), (half, 0.0), (half, size_m), (-half, size_m)):
		corners.append((anchor[0] + along * t[0] + out * n[0], anchor[1] + along * t[1] + out * n[1]))
	return corners


def landing_box_polygon(anchor, outward, size_m=30.0):
	"""The same box as a lon/lat polygon (shapely x = lon, y = lat)."""
	corners = landing_box_local(anchor, outward, size_m)
	lat, lon = geodesy.from_local(np.array([c[0] for c in corners]), np.array([c[1] for c in corners]))
	return Polygon(list(zip(lon.tolist(), lat.tolist())))


def sample_polygon(array, transform, polygon, nodata):
	"""Elevations of cells whose centres fall inside `polygon` (cells it merely touches if it contains no centre)."""
	shape = array.shape
	inside = features.geometry_mask([polygon], out_shape=shape, transform=transform, invert=True, all_touched=False)
	if not inside.any():
		inside = features.geometry_mask([polygon], out_shape=shape, transform=transform, invert=True, all_touched=True)
	cells = array[inside]
	valid = ~np.isnan(cells)
	if nodata is not None:
		valid &= cells != nodata
	return Sample(values=cells[valid].astype(float), nodata_count=int((~valid).sum()), cell_count=int(inside.sum()))


def read_polygon(dataset, polygon):
	"""Sample an open rasterio dataset under a lon/lat polygon, reading only the window around it."""
	window = windows.from_bounds(*polygon.bounds, transform=dataset.transform).round_offsets().round_lengths()
	window = window.intersection(windows.Window(0, 0, dataset.width, dataset.height)) if _overlaps(window, dataset) else None
	if window is None or window.width < 1 or window.height < 1:
		return Sample(values=np.array([]), nodata_count=0, cell_count=0)
	array = dataset.read(1, window=window)
	return sample_polygon(array, dataset.window_transform(window), polygon, dataset.nodata)


def _overlaps(window, dataset):
	return not (window.col_off >= dataset.width or window.row_off >= dataset.height or window.col_off + window.width <= 0 or window.row_off + window.height <= 0)


def depth_stats(elevations, water_level_m):
	"""Depth statistics for seabed elevations at a given water level (both NAVD88 metres)."""
	elevations = np.asarray(elevations, dtype=float)
	if elevations.size == 0:
		return {"cells": 0, "min_depth_m": None, "p5_depth_m": None, "mean_depth_m": None, "max_depth_m": None, "dry_cells": 0}
	depths = water_level_m - elevations
	return {
		"cells": int(depths.size),
		"min_depth_m": float(depths.min()),
		"p5_depth_m": float(np.percentile(depths, 5)),
		"mean_depth_m": float(depths.mean()),
		"max_depth_m": float(depths.max()),
		"dry_cells": int((depths <= 0).sum()),
	}
