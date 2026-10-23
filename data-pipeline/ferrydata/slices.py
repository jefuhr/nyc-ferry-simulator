"""Slice definitions (a slice is a bounding box the simulator ships as one vertical slice)."""

import math
from dataclasses import dataclass

from shapely.geometry import box


@dataclass(frozen=True)
class Slice:
	name: str
	south: float
	north: float
	west: float
	east: float
	enc_cells: tuple
	# CO-OPS stations are found by bbox + margin; water level stations are named because only a few have NAVD88.
	station_margin_deg: float
	water_level_stations: tuple
	window_start: str
	window_end: str

	@property
	def bbox(self):
		"""(west, south, east, north) in lon/lat order."""
		return (self.west, self.south, self.east, self.north)

	@property
	def polygon(self):
		return box(self.west, self.south, self.east, self.north)

	@property
	def centre(self):
		return ((self.south + self.north) / 2.0, (self.west + self.east) / 2.0)

	def corners(self):
		return {
			"sw": (self.south, self.west),
			"se": (self.south, self.east),
			"nw": (self.north, self.west),
			"ne": (self.north, self.east),
		}

	def contains(self, lon, lat) -> bool:
		return self.west <= lon <= self.east and self.south <= lat <= self.north


LOWER_EAST_RIVER = Slice(
	name="lower-east-river",
	south=40.695,
	north=40.750,
	west=-74.020,
	east=-73.960,
	# Found in NOAA's ENC product catalog (ENCProdCat_19115.xml): the usage band 5 cells that cover the bbox.
	enc_cells=("US5NYCDG", "US5NYCEG"),
	station_margin_deg=0.02,
	# The Battery first (the reference gauge); the others are nearby NAVD88 gauges, skipped if they lack the datum.
	water_level_stations=("8518750", "8519483", "8516945", "8516990"),
	window_start="2026-06-14",
	window_end="2026-06-16",
)

SLICES = {LOWER_EAST_RIVER.name: LOWER_EAST_RIVER}

# Harbor box for the AIS extract (wider than any one slice).
AIS_HARBOR_BBOX = {"south": 40.50, "north": 40.90, "west": -74.10, "east": -73.75}


def get_slice(name: str) -> Slice:
	try:
		return SLICES[name]
	except KeyError:
		raise SystemExit(f"unknown slice '{name}'; known: {', '.join(sorted(SLICES))}")


def clip_geometry(geometry, slice_: Slice):
	"""Clip a shapely geometry to the slice bbox. Returns None when nothing remains."""
	if geometry is None or geometry.is_empty:
		return None
	clipped = geometry.intersection(slice_.polygon)
	return None if clipped.is_empty else clipped


def cudem_tile_ids(slice_: Slice, tile_deg: float = 0.25):
	"""CUDEM 1/9 arc-second tiles are 0.25 degrees square, named by their north and west edges."""
	steps = round(1.0 / tile_deg)
	north_edges = range(math.floor(slice_.south * steps) + 1, math.ceil(slice_.north * steps - 1e-9) + 1)
	west_edges = range(math.floor(slice_.west * steps + 1e-9), math.ceil(slice_.east * steps - 1e-9))
	ids = []
	for n in north_edges:
		for w in west_edges:
			north = n / steps
			west = abs(w) / steps
			ids.append(f"ncei19_n{_edge(north, 2)}_w{_edge(west, 3)}")
	return sorted(ids)


def _edge(value: float, digits: int) -> str:
	whole = int(value)
	hundredths = int(round((value - whole) * 100))
	return f"{whole:0{digits}d}x{hundredths:02d}"
