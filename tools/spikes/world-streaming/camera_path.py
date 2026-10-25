"""The fixed camera path both streaming approaches are measured on.

Pier 11, up the East River (Brooklyn, Manhattan and Williamsburg bridges, the west channel of Roosevelt
Island) to Hell Gate, back to Pier 11, then across the Upper Bay toward St. George. Waypoints are mid-channel
estimates from the chart, good enough for a camera; the path is not a vessel route. Speed is the T-class
service speed from vessels/t-class.json (25 kn).
"""

import json
from pathlib import Path

import numpy as np

from ferrydata import geodesy

# (latitude, longitude), WGS84 degrees.
EAST_RIVER = [
	(40.70240, -74.00170),  # off Wall St/Pier 11
	(40.70540, -73.99600),  # Brooklyn Bridge
	(40.70700, -73.99000),  # Manhattan Bridge
	(40.70620, -73.97900),  # off the Navy Yard
	(40.71200, -73.97200),  # Williamsburg Bridge
	(40.72000, -73.96950),
	(40.73000, -73.96800),  # off Stuyvesant Cove
	(40.74400, -73.96550),  # East 34th St / Hunters Point
	(40.74900, -73.96200),  # United Nations
	(40.75400, -73.96060),  # west channel of Roosevelt Island
	(40.75900, -73.95600),  # Queensboro Bridge
	(40.76400, -73.95110),
	(40.76900, -73.94680),
	(40.77450, -73.94050),  # north end of Roosevelt Island
	(40.77900, -73.93400),  # Hell Gate
]

UPPER_BAY = [
	(40.70240, -74.00170),  # off Wall St/Pier 11
	(40.69950, -74.00800),  # off the Battery Maritime Building
	(40.69650, -74.01750),  # between the Battery and Governors Island
	(40.69450, -74.02600),
	(40.68800, -74.03100),
	(40.67200, -74.04300),  # Upper Bay
	(40.65800, -74.06000),
	(40.64950, -74.06900),  # approaching St. George
]

SERVICE_SPEED_MPS = 12.8611  # vessels/t-class.json performance.serviceSpeed (25 kn running speed)
EYE_HEIGHT_M = 6.0  # above NAVD88 zero: a wheelhouse eye at about 6 m

# Named screenshot points: (name, leg, distance along the leg in metres, or None for the leg's end).
SHOTS = [
	("pier11", "up-east-river", 0.0),
	("brooklyn-bridge", "up-east-river", 350.0),
	("williamsburg-bridge", "up-east-river", 2900.0),
	("east-34th", "up-east-river", 5000.0),
	("roosevelt-island", "up-east-river", 7700.0),
	("hell-gate", "up-east-river", None),
	("governors-island", "upper-bay", 1600.0),
	("upper-bay", "upper-bay", 4400.0),
	("st-george", "upper-bay", None),
]


def _local(points):
	lat = np.array([p[0] for p in points])
	lon = np.array([p[1] for p in points])
	north, east = geodesy.to_local(lat, lon)
	return np.stack([north, east], axis=1)


def _length(points):
	return float(np.sum(np.linalg.norm(np.diff(points, axis=0), axis=1)))


def build():
	"""The three legs as world (north, east) polylines, with lengths and named shots."""
	up = _local(EAST_RIVER)
	legs = [
		{"name": "up-east-river", "points_ne": up},
		{"name": "down-east-river", "points_ne": up[::-1].copy()},
		{"name": "upper-bay", "points_ne": _local(UPPER_BAY)},
	]
	for leg in legs:
		leg["length_m"] = _length(leg["points_ne"])
	lengths = {leg["name"]: leg["length_m"] for leg in legs}
	shots = [{"name": name, "leg": leg, "distance_m": lengths[leg] if distance is None else distance} for name, leg, distance in SHOTS]
	return {"legs": legs, "shots": shots, "speed_mps": SERVICE_SPEED_MPS, "eye_height_m": EYE_HEIGHT_M}


def sample(path, spacing):
	"""Points every `spacing` metres along all legs, (n, 2) world (north, east)."""
	out = []
	for leg in path["legs"]:
		points = leg["points_ne"]
		for a, b in zip(points[:-1], points[1:]):
			steps = max(1, int(np.ceil(np.linalg.norm(b - a) / spacing)))
			t = np.arange(steps)[:, None] / steps
			out.append(a + (b - a) * t)
		out.append(points[-1:])
	return np.concatenate(out)


def distance_to_path(path, north, east):
	"""Distance (m) from world points to the nearest path segment."""
	p = np.stack([np.asarray(north, dtype=float).ravel(), np.asarray(east, dtype=float).ravel()], axis=1)
	best = np.full(len(p), np.inf)
	# The second leg retraces the first, so the first and third cover every segment.
	for leg in path["legs"][::2]:
		points = leg["points_ne"]
		for a, b in zip(points[:-1], points[1:]):
			ab = b - a
			t = np.clip(((p - a) @ ab) / float(ab @ ab), 0.0, 1.0)
			best = np.minimum(best, np.linalg.norm(p - (a + t[:, None] * ab), axis=1))
	return best.reshape(np.shape(north))


def write(path, destination):
	document = {
		"description": "World streaming spike camera path: world-frame (north, east) metres, legs in order.",
		"speed_mps": path["speed_mps"],
		"speed_source": "vessels/t-class.json performance.serviceSpeed (25 kn)",
		"eye_height_navd88_m": path["eye_height_m"],
		"legs": [{"name": leg["name"], "length_m": round(leg["length_m"], 3),
			"points_ne": [[round(float(n), 3), round(float(e), 3)] for n, e in leg["points_ne"]]} for leg in path["legs"]],
		"shots": [{"name": s["name"], "leg": s["leg"], "distance_m": round(float(s["distance_m"]), 3)} for s in path["shots"]],
	}
	Path(destination).write_text(json.dumps(document, indent="\t") + "\n", encoding="utf-8")
	return document
