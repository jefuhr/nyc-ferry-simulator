"""An independent Python implementation of the ferrysim-env/1 layout and the revised (2026-10-25) digests,
used to make golden vectors for the C# reader. Positions use the same ECEF maths as ferrydata.geodesy."""
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

def station(id, name, lat, lon, **extra):
	n, e = to_local(lat, lon)
	s = {"id": id, "name": name}
	s.update({k: v for k, v in extra.items() if k == "kind"})
	s.update({"lat": lat, "lon": lon, "north_m": n, "east_m": e})
	s.update({k: v for k, v in extra.items() if k != "kind"})
	return s

def write(path, data):
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "wb") as f:
		f.write(data)

def sha(b):
	return hashlib.sha256(b).hexdigest()

root = sys.argv[1]
env = os.path.join(root, "lower-east-river", "env")

# Depth set: one 3 x 2 grid of 10 m cells whose last centre is the world origin.
values = [-10.0, -9.5, float("nan"), 1.25, -3.0, 2.0]
f32 = b"".join(struct.pack("<f", v) for v in values)
depth_manifest = {
	"format": "ferrysim-env-depth/1",
	"region": "lower-east-river",
	"datum": "NAVD88",
	"value": "bed elevation, metres, positive up (water is negative)",
	"grids": [{"id": "slice", "file": "slice.f32", "origin_north_m": -20.0, "origin_east_m": -10.0, "cell_m": 10.0,
		"rows": 3, "cols": 2, "nodata": "NaN", "sha256": sha(f32)}],
	"outside_depth_m": 20.0,
	"sources": [{"name": "depth_merged_navd88.tif", "sha256": "0" * 64, "note": "synthetic golden vector"}],
}
depth_bytes = (json.dumps(depth_manifest, indent=2) + "\n").encode()
h = hashlib.sha256()
h.update(b"manifest.json\0" + depth_bytes + b"\0")
for grid in depth_manifest["grids"]:
	h.update(grid["file"].encode() + b"\0" + f32 + b"\0")
depth_digest = h.hexdigest()
write(os.path.join(env, "depth", "manifest.json"), depth_bytes)
write(os.path.join(env, "depth", "slice.f32"), f32)
write(os.path.join(env, "depth", "DIGEST"), (depth_digest + "\n").encode())

# Day: 2025-07-14, EDT, so local midnight is 04:00Z.
currents = "station_id,t_s,north_mps,east_mps\n" + "".join(f"NYH1920,{t},{n!r},{e!r}\n" for t, n, e in [(-3600, 0.5, -0.25), (0, 0.75, -0.125), (360, 1e-05, 0.3), (90000, -0.5, 0.25)])
levels = "station_id,t_s,level_navd88_m,source\n" + "".join(f"8518750,{t},{v!r},{s}\n" for t, v, s in [(-3600.0, 0.1, "observed"), (1800.0, 0.4, "predicted"), (90000.0, -0.3, "observed")])
wind = "station_id,t_s,north_mps,east_mps,gust_mps\n" + "".join(f"8518750,{t},{n!r},{e!r},{g}\n" for t, n, e, g in [(-3600, -3.0, 4.0, ""), (90000, 1.0, 2.0, repr(7.5))])
series = {"currents.csv": currents.encode(), "water_levels.csv": levels.encode(), "wind.csv": wind.encode()}
day_manifest = {
	"format": "ferrysim-env/1",
	"region": "lower-east-river",
	"date": "2025-07-14",
	"epoch_utc": "2025-07-14T04:00:00Z",
	"time_coverage_s": [-3600, 90000],
	"depth_digest": depth_digest,
	"current_stations": [station("NYH1920", "Brooklyn Bridge", 40.7063, -73.9971, kind="harmonic-prediction", bin_depth_m=1.5,
		source="NYH1920_2025-07-14.json", source_sha256="1" * 64)],
	"water_level_stations": [station("8518750", "The Battery", 40.7006, -74.0142, source="8518750_water_level.json", source_sha256="2" * 64)],
	"wind_stations": [station("8518750", "The Battery", 40.7006, -74.0142, sensor_height_m=None, source="8518750_wind.json", source_sha256="3" * 64)],
	"files": {name: sha(b) for name, b in series.items()},
	"notes": ["water level 8518750: observations missing 00:30-01:00 local, filled with predictions"],
}
day_bytes = (json.dumps(day_manifest, indent=2) + "\n").encode()
d = hashlib.sha256()
d.update(b"manifest.json\0" + day_bytes + b"\0")
for name in ["currents.csv", "water_levels.csv", "wind.csv"]:
	d.update(name.encode() + b"\0" + series[name] + b"\0")
d.update(depth_digest.encode() + b"\0")
day_digest = d.hexdigest()
day = os.path.join(env, "days", "2025-07-14")
write(os.path.join(day, "manifest.json"), day_bytes)
for name, b in series.items():
	write(os.path.join(day, name), b)
write(os.path.join(day, "DIGEST"), (day_digest + "\n").encode())
print("depth", depth_digest)
print("day", day_digest)
