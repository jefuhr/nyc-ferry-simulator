"""An independent Python implementation of the depth-set rule for several grids (ferrysim-env-depth/1 as revised
2026-10-25), used to make golden vectors for the C# reader and the pipeline's reader. Grids that meet must lie on one
lattice and hold identical shared samples; a point inside several grids resolves to the first; sampling is bilinear
between the four surrounding centres of that grid. Positions use the same ECEF maths as ferrydata.geodesy. Made-up
values. python3 python_depth_pack.py <out-dir> writes the pack and prints its digests and golden samples as JSON."""
import hashlib, json, math, os, struct, sys

A = 6378137.0
F = 1.0 / 298.257223563
E2 = F * (2.0 - F)
LAT0, LON0 = 40.7000, -74.0150

def ecef(lat, lon, h=0.0):
	la, lo = math.radians(lat), math.radians(lon)
	n = A / math.sqrt(1.0 - E2 * math.sin(la) ** 2)
	return ((n + h) * math.cos(la) * math.cos(lo), (n + h) * math.cos(la) * math.sin(lo), (n * (1.0 - E2) + h) * math.sin(la))

X0, Y0, Z0 = ecef(LAT0, LON0)
SL, CL = math.sin(math.radians(LAT0)), math.cos(math.radians(LAT0))
SO, CO = math.sin(math.radians(LON0)), math.cos(math.radians(LON0))

def to_local(lat, lon):
	x, y, z = ecef(lat, lon)
	dx, dy, dz = x - X0, y - Y0, z - Z0
	return (-SL * CO * dx - SL * SO * dy + CL * dz, -SO * dx + CO * dy)

def write(path, data):
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "wb") as f:
		f.write(data)

def sha(b):
	return hashlib.sha256(b).hexdigest()

def digest(parts, suffix=b""):
	h = hashlib.sha256()
	for name, body in parts:
		h.update(name.encode() + b"\0" + body + b"\0")
	h.update(suffix)
	return h.hexdigest()

def f32(x):
	return struct.unpack("<f", struct.pack("<f", x))[0]

root = sys.argv[1]
env = os.path.join(root, "harbor", "env")

# The bed is one function of the world position, so every grid holding a centre holds the same value there: a
# non-bilinear pattern (a reader using the wrong cell would not reproduce it), exact in float32, unknown (NaN) at three
# centres. (0, -30) is a sample sw and nw share; (-10, 20) is on se's east edge, next to its seam with ne; (-20, -30)
# is sw's own corner.
UNKNOWN = {(0, -30), (-10, 20), (-20, -30)}

def bed(north, east):
	i, j = int(north // 10), int(east // 10)
	if (north, east) in UNKNOWN:
		return float("nan")
	return -8.0 - 0.75 * ((7 * i + 3 * j) % 11) + 0.0625 * i

# Four 10 m grids meet at the origin, sharing their edge rows and columns of centres; cap overlaps ne by a square of four
# centres; far uses another cell size and meets nothing. In grids order: sw, se, nw, ne, cap, far.
GRIDS = [
	("sw", -20.0, -30.0, 10.0, 3, 4),
	("se", -20.0, 0.0, 10.0, 3, 3),
	("nw", 0.0, -30.0, 10.0, 3, 4),
	("ne", 0.0, 0.0, 10.0, 3, 3),
	("cap", 10.0, 10.0, 10.0, 3, 3),
	("far", 100.0, 100.0, 25.0, 2, 2),
]

files, grids = {}, []
for gid, n0, e0, cell, rows, cols in GRIDS:
	values = [bed(n0 + r * cell, e0 + c * cell) for r in range(rows) for c in range(cols)]
	body = b"".join(struct.pack("<f", v) for v in values)
	files[gid + ".f32"] = body
	grids.append({"id": gid, "file": gid + ".f32", "origin_north_m": n0, "origin_east_m": e0, "cell_m": cell,
		"rows": rows, "cols": cols, "nodata": "NaN", "sha256": sha(body)})

depth_manifest = {
	"format": "ferrysim-env-depth/1", "region": "harbor", "datum": "NAVD88",
	"value": "bed elevation, metres, positive up (water is negative)",
	"grids": grids,
	"outside_depth_m": 20.0,
	"sources": [{"name": "depth_merged_navd88.tif", "sha256": "0" * 64, "note": "synthetic golden vector"}],
}
depth_bytes = (json.dumps(depth_manifest, indent=2) + "\n").encode()
depth_digest = digest([("manifest.json", depth_bytes)] + [(g["file"], files[g["file"]]) for g in grids])
write(os.path.join(env, "depth", "manifest.json"), depth_bytes)
for name, body in files.items():
	write(os.path.join(env, "depth", name), body)
write(os.path.join(env, "depth", "DIGEST"), (depth_digest + "\n").encode())

# Day: 2025-07-14 (EDT), one current station and one gauge, no wind, no current field.
def station(id, name, lat, lon, extra):
	n, e = to_local(lat, lon)
	s = {"id": id, "name": name}
	s.update({k: v for k, v in extra.items() if k == "kind"})
	s.update({"lat": lat, "lon": lon, "north_m": n, "east_m": e})
	s.update({k: v for k, v in extra.items() if k != "kind"})
	return s

currents = "station_id,t_s,north_mps,east_mps\nNYH1920,-3600,0.5,-0.25\nNYH1920,90000,-0.5,0.25\n"
levels = "station_id,t_s,level_navd88_m,source\n8518750,-3600,0.1,observed\n8518750,90000,-0.3,observed\n"
wind = "station_id,t_s,north_mps,east_mps,gust_mps\n"
series = {"currents.csv": currents.encode(), "water_levels.csv": levels.encode(), "wind.csv": wind.encode()}
day_manifest = {
	"format": "ferrysim-env/1", "region": "harbor", "date": "2025-07-14", "epoch_utc": "2025-07-14T04:00:00Z",
	"time_coverage_s": [-3600, 90000], "depth_digest": depth_digest,
	"current_stations": [station("NYH1920", "Brooklyn Bridge", 40.7063, -73.9971, {"kind": "harmonic-prediction", "bin_depth_m": 1.5,
		"source": "NYH1920_2025-07-14.json", "source_sha256": "1" * 64})],
	"water_level_stations": [station("8518750", "The Battery", 40.7006, -74.0142, {"source": "8518750_water_level.json", "source_sha256": "2" * 64})],
	"wind_stations": [],
	"files": {name: sha(b) for name, b in series.items()},
	"notes": ["synthetic golden vector with several depth grids"],
}
day_bytes = (json.dumps(day_manifest, indent=2) + "\n").encode()
day_digest = digest([("manifest.json", day_bytes)] + [(n, series[n]) for n in ["currents.csv", "water_levels.csv", "wind.csv"]],
	depth_digest.encode() + b"\0")
day = os.path.join(env, "days", "2025-07-14")
write(os.path.join(day, "manifest.json"), day_bytes)
for name, b in series.items():
	write(os.path.join(day, name), b)
write(os.path.join(day, "DIGEST"), (day_digest + "\n").encode())

# The sampling rule, written independently of ferrydata and of the C# reader: the first grid whose centres, first to
# last inclusive, surround the point; bilinear between the four centres of its cell (the last row and column of centres
# inside); unknown when any of the four is NaN, whatever its weight.
VALUES = {g["id"]: [f32(v) for v in struct.unpack("<%df" % (g["rows"] * g["cols"]), files[g["file"]])] for g in grids}

def sample(north, east):
	for g in grids:
		row = (north - g["origin_north_m"]) / g["cell_m"]
		col = (east - g["origin_east_m"]) / g["cell_m"]
		if not (row >= 0.0 and row <= g["rows"] - 1 and col >= 0.0 and col <= g["cols"] - 1):
			continue
		r, c = min(int(row), g["rows"] - 2), min(int(col), g["cols"] - 2)
		v, k = VALUES[g["id"]], r * g["cols"] + c
		south_west, south_east, north_west, north_east = v[k], v[k + 1], v[k + g["cols"]], v[k + g["cols"] + 1]
		if any(math.isnan(x) for x in (south_west, south_east, north_west, north_east)):
			return ("unknown", g["id"], 0.0)
		across, up = col - c, row - r
		lower = south_west + across * (south_east - south_west)
		upper = north_west + across * (north_east - north_west)
		return ("known", g["id"], lower + up * (upper - lower))
	return ("outside", None, 0.0)

E = 1e-9
points = [
	("inside sw", -15.0, -5.0),
	("inside sw next to its unknown corner", -15.0, -25.0),
	("inside se, which has three columns", -15.0, 5.0),
	("on the sw/nw seam", 0.0, -15.0),
	("just south of the sw/nw seam", -E, -15.0),
	("just north of the sw/nw seam", E, -15.0),
	("1 mm south of the sw/nw seam", -0.001, -12.5),
	("1 mm north of the sw/nw seam", 0.001, -12.5),
	("on the sw/se seam", -15.0, 0.0),
	("just west of the sw/se seam", -15.0, -E),
	("just east of the sw/se seam", -15.0, E),
	("on the corner of four grids", 0.0, 0.0),
	("next to the corner, north-east", E, E),
	("next to the corner, south-east", -E, E),
	("next to the corner, north-west", E, -E),
	("on the se/ne seam, where se's cell has an unknown corner of weight zero", 0.0, 15.0),
	("just north of it, in ne", E, 15.0),
	("in the overlap of ne and cap", 15.0, 15.0),
	("on cap's south edge inside ne", 10.0, 13.0),
	("in cap only", 25.0, 25.0),
	("next to a shared unknown sample, in nw", 2.0, -25.0),
	("on the seam next to a shared unknown sample", 0.0, -25.0),
	("on the last centre of nw, unshared", 20.0, -30.0),
	("in far, another cell size", 110.0, 120.0),
	("just outside cap's east edge", 25.0, 30.000001),
	("outside every grid", 50.0, 50.0),
]
print(json.dumps({"depth": depth_digest, "day": day_digest,
	"samples": [[label, n, e] + list(sample(n, e)) for label, n, e in points]}, indent=1))
