"""An independent Python implementation of the ferrysim-env/1 current-field extension (approved 2026-10-25), used to make
golden vectors for the C# reader and ChannelCurrentField: a pack whose day names a field set, the field digest and the
extended day digest, and the sampling rule. Positions use the same ECEF maths as ferrydata.geodesy. Made-up values.
python3 python_field_pack.py <out-dir> writes the pack and prints its digests and golden samples as JSON."""
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
env = os.path.join(root, "lower-east-river", "env")

# Depth set: one 2 x 2 grid of 50 m cells around the origin.
depth_f32 = b"".join(struct.pack("<f", v) for v in [-12.0, -11.0, -10.0, -9.0])
depth_manifest = {
	"format": "ferrysim-env-depth/1", "region": "lower-east-river", "datum": "NAVD88",
	"value": "bed elevation, metres, positive up (water is negative)",
	"grids": [{"id": "slice", "file": "slice.f32", "origin_north_m": -50.0, "origin_east_m": -50.0, "cell_m": 50.0,
		"rows": 2, "cols": 2, "nodata": "NaN", "sha256": sha(depth_f32)}],
	"outside_depth_m": 20.0,
	"sources": [{"name": "depth_merged_navd88.tif", "sha256": "0" * 64, "note": "synthetic golden vector"}],
}
depth_bytes = (json.dumps(depth_manifest, indent=2) + "\n").encode()
depth_digest = digest([("manifest.json", depth_bytes), ("slice.f32", depth_f32)])
write(os.path.join(env, "depth", "manifest.json"), depth_bytes)
write(os.path.join(env, "depth", "slice.f32"), depth_f32)
write(os.path.join(env, "depth", "DIGEST"), (depth_digest + "\n").encode())

# Three current stations: A and B inside the field's grid, C outside it.
POSITIONS = {"A": (40.6999, -74.0150), "B": (40.70025, -74.01475), "C": (40.7012, -74.0130)}
LOCAL = {k: to_local(*v) for k, v in POSITIONS.items()}
SERIES = {
	"A": [(-3600, 0.5, 0.2), (0, 1.0, 0.4), (360, 1.25, 0.5), (720, -0.75, -0.3), (90000, 0.1, 0.0)],
	"B": [(-3600, 0.3, 0.6), (0, 0.4, 0.9), (360, 0.2, 1.1), (720, -0.3, -0.8), (90000, 0.0, 0.1)],
	"C": [(-3600, -0.2, 0.1), (0, 0.6, -0.2), (360, 0.7, 0.1), (720, 0.1, 0.4), (90000, 0.0, 0.0)],
}

# The field: a 4 x 5 grid of 20 m cells from (-20, -20), 3 slots; column 4 above row 0 is land. A owns rows 0-1, columns
# 0-2; B owns rows 2-3, columns 2-3; the rest blend. The axis turns across the grid; the cells next to the land have no
# cross factor; two cells are sheltered. Several cells keep their stations behind or between empty slots (None): the
# format allows it, and a cell is water when any of its slots names a station.
ROWS, COLS, SLOTS, CELL, N0, E0 = 4, 5, 3, 20.0, -20.0, -20.0
STATIONS = ["A", "B", "C"]
DRY = {(1, 4), (2, 4), (3, 4)}

def cell(r, c):
	if (r, c) in DRY:
		return None
	angle = math.radians(20.0 + 8.0 * c + 5.0 * r)
	along = {(0, 3): 0.5, (1, 3): 0.75}.get((r, c), 1.0)
	cross = 0.0 if c == 3 and r >= 1 else along
	if r <= 1 and c <= 2:
		slots = [None, (0, 1.0)] if (r, c) in ((0, 1), (1, 2)) else [(0, 1.0)]
	elif r >= 2 and 2 <= c <= 3:
		slots = [None, None, (1, 1.0)] if (r, c) == (2, 3) else [(1, 1.0)]
	elif r <= 1 and c == 3:
		slots = [(0, 0.6), None, (1, 0.4)]
	elif c == 4:
		slots = [None, (0, 0.5), (2, 0.5)]
	else:
		slots = [(1, 0.7), None, (2, 0.3)]
	return (math.cos(angle), math.sin(angle), along, cross, slots)

values, indices, cells = b"", b"", {}
for r in range(ROWS):
	for c in range(COLS):
		x = cell(r, c)
		if x is None:
			numbers, slot_bytes = [0.0] * (4 + SLOTS), [255] * SLOTS
		else:
			weights = [0.0 if s is None else s[1] for s in x[4]] + [0.0] * (SLOTS - len(x[4]))
			numbers = list(x[:4]) + weights
			slot_bytes = [255 if s is None else s[0] for s in x[4]] + [255] * (SLOTS - len(x[4]))
		values += b"".join(struct.pack("<f", v) for v in numbers)
		indices += bytes(slot_bytes)
		# What a reader has: float32 values widened to double.
		cells[(r, c)] = None if x is None else ([f32(v) for v in numbers], slot_bytes)

def corners(n, e):
	row, col = (n - N0) / CELL, (e - E0) / CELL
	r, c = min(int(row), ROWS - 2), min(int(col), COLS - 2)
	up, across = row - r, col - c
	return [(r, c, (1.0 - up) * (1.0 - across)), (r, c + 1, (1.0 - up) * across), (r + 1, c, up * (1.0 - across)), (r + 1, c + 1, up * across)]

def frame_at(n, e):
	fn = fe = wet = 0.0
	for r, c, w in corners(n, e):
		if cells[(r, c)] is not None:
			numbers = cells[(r, c)][0]
			fn += w * numbers[0]
			fe += w * numbers[1]
			wet += w
	share = max(wet, 0.5)
	return (fn / share, fe / share)

FRAMES = {"A": frame_at(*LOCAL["A"]), "B": frame_at(*LOCAL["B"]), "C": (0.6, 0.8)}
field_manifest = {
	"format": "ferrysim-env-field/1", "region": "lower-east-river", "method": "channel-harmonic/1",
	"stations": [{"id": k, "north_m": LOCAL[k][0], "east_m": LOCAL[k][1], "frame_north": FRAMES[k][0], "frame_east": FRAMES[k][1]} for k in STATIONS],
	"grids": [{"id": "slice", "values_file": "slice.f32", "stations_file": "slice.u8", "origin_north_m": N0, "origin_east_m": E0,
		"cell_m": CELL, "rows": ROWS, "cols": COLS, "slots": SLOTS, "values_sha256": sha(values), "stations_sha256": sha(indices)}],
	"parameters": {"cell_m": 20.0, "computation_north_m": [-20.0, 40.0], "computation_east_m": [-20.0, 60.0], "inflow": "south",
		"outflow": "north", "conveyance_exponent": 0.0, "min_depth_m": 1.0, "smoothing_m": 400.0, "shelter_threshold": 0.85,
		"depth_exponent": 1.0 / 6.0, "depth_smoothing_m": 200.0, "cross_taper_m": 60.0, "station_disk_m": 40.0, "station_reach_m": 120.0,
		"across_reach_m": 500.0, "tolerance": 1e-10},
	"sources": [{"name": "depth/DIGEST", "sha256": depth_digest, "note": "synthetic golden vector"}],
	"notes": ["synthetic golden vector"],
}
field_bytes = (json.dumps(field_manifest, indent=2) + "\n").encode()
field_digest = digest([("manifest.json", field_bytes), ("slice.f32", values), ("slice.u8", indices)])
write(os.path.join(env, "field", "manifest.json"), field_bytes)
write(os.path.join(env, "field", "slice.f32"), values)
write(os.path.join(env, "field", "slice.u8"), indices)
write(os.path.join(env, "field", "DIGEST"), (field_digest + "\n").encode())

# Day: 2025-07-14 (EDT), naming the field; B is a subordinate station with its derivation; no wind station.
def station(id, kind, extra):
	lat, lon = POSITIONS[id]
	n, e = LOCAL[id]
	s = {"id": id, "name": "Station " + id, "kind": kind, "lat": lat, "lon": lon, "north_m": n, "east_m": e,
		"bin_depth_m": None, "source": id + "_currents.csv", "source_sha256": "1" * 64}
	s.update(extra)
	return s

derivation = {"method": "noaa-subordinate", "reference_station": "NYH1924", "reference_bin": 6, "slack_before_flood_min": 1.0,
	"max_flood_min": -6.0, "slack_before_ebb_min": -2.0, "max_ebb_min": 8.0, "flood_ratio": 0.6, "ebb_ratio": 1.12,
	"flood_dir_deg": 37.0, "ebb_dir_deg": 215.0, "sources": [{"name": "offsets_B.json", "sha256": "4" * 64}]}
currents = "station_id,t_s,north_mps,east_mps\n" + "".join(f"{k},{t},{n!r},{e!r}\n" for k in STATIONS for t, n, e in SERIES[k])
battery = to_local(40.7006, -74.0142)
levels = "station_id,t_s,level_navd88_m,source\n8518750,-3600,0.1,observed\n8518750,90000,-0.3,observed\n"
wind = "station_id,t_s,north_mps,east_mps,gust_mps\n"
series = {"currents.csv": currents.encode(), "water_levels.csv": levels.encode(), "wind.csv": wind.encode()}
day_manifest = {
	"format": "ferrysim-env/1", "region": "lower-east-river", "date": "2025-07-14", "epoch_utc": "2025-07-14T04:00:00Z",
	"time_coverage_s": [-3600, 90000], "depth_digest": depth_digest, "field_digest": field_digest,
	"current_stations": [station("A", "harmonic-prediction", {}), station("B", "subordinate-prediction", {"derivation": derivation}),
		station("C", "harmonic-prediction", {})],
	"water_level_stations": [{"id": "8518750", "name": "The Battery", "lat": 40.7006, "lon": -74.0142, "north_m": battery[0],
		"east_m": battery[1], "source": "8518750_water_level.json", "source_sha256": "2" * 64}],
	"wind_stations": [],
	"files": {name: sha(b) for name, b in series.items()},
	"notes": ["synthetic golden vector with a current field"],
}
day_bytes = (json.dumps(day_manifest, indent=2) + "\n").encode()
day_digest = digest([("manifest.json", day_bytes)] + [(n, series[n]) for n in ["currents.csv", "water_levels.csv", "wind.csv"]],
	depth_digest.encode() + b"\0" + field_digest.encode() + b"\0")
day = os.path.join(env, "days", "2025-07-14")
write(os.path.join(day, "manifest.json"), day_bytes)
for name, b in series.items():
	write(os.path.join(day, name), b)
write(os.path.join(day, "DIGEST"), (day_digest + "\n").encode())

# The sampling rule, written independently of ferrydata and of the C# reader.
def velocity(k, t):
	rows = SERIES[k]
	for t0, n0, e0 in rows:
		if t == t0:
			return (n0, e0)
	for (t0, n0, e0), (t1, n1, e1) in zip(rows, rows[1:]):
		if t0 <= t <= t1:
			assert t1 - t0 <= 1800, "golden times stay inside interpolated gaps"
			fraction = (t - t0) / (t1 - t0)
			return (n0 + fraction * (n1 - n0), e0 + fraction * (e1 - e0))
	raise ValueError(t)

def sample(n, e, t):
	if not (0.0 <= (n - N0) / CELL <= ROWS - 1 and 0.0 <= (e - E0) / CELL <= COLS - 1):
		# Outside the grid: inverse distance squared over the day's current stations in manifest order.
		weights = sum_n = sum_e = 0.0
		for k in STATIONS:
			dn, de = n - LOCAL[k][0], e - LOCAL[k][1]
			squared = dn * dn + de * de
			v = velocity(k, t)
			if squared <= 1e-12:
				return v
			w = 1.0 / squared
			weights += w
			sum_n += w * v[0]
			sum_e += w * v[1]
		return (sum_n / weights, sum_e / weights)
	parts = []
	for k in STATIONS:
		vn, ve = velocity(k, t)
		fn, fe = FRAMES[k]
		square = fn * fn + fe * fe
		parts.append(((vn * fn + ve * fe) / square, (-vn * fe + ve * fn) / square))
	north = east = wet = 0.0
	for r, c, w in corners(n, e):
		if cells[(r, c)] is None:
			continue
		numbers, slot_bytes = cells[(r, c)]
		along = across = 0.0
		for j in range(SLOTS):
			if slot_bytes[j] != 255:
				along += numbers[4 + j] * parts[slot_bytes[j]][0]
				across += numbers[4 + j] * parts[slot_bytes[j]][1]
		along = numbers[2] * along
		across = numbers[3] * across
		north += w * (along * numbers[0] - across * numbers[1])
		east += w * (along * numbers[1] + across * numbers[0])
		wet += w
	share = max(wet, 0.5)
	return (north / share, east / share)

points = [
	("mid-channel", 5.0, 7.0, 180.0),
	("at station A", LOCAL["A"][0], LOCAL["A"][1], 0.0),
	("next to station A", LOCAL["A"][0] + 3.0, LOCAL["A"][1] - 2.0, 180.0),
	("at station B (cross factor 0 on two corners)", LOCAL["B"][0], LOCAL["B"][1], 360.0),
	("near the shore, wet share 0.65", 27.0, 47.0, 360.0),
	("near the shore, wet share 0.4", 27.0, 52.0, 360.0),
	("on a land centre", 40.0, 60.0, 360.0),
	("just west of a cell boundary", 13.0, 19.999, 540.0),
	("on a cell boundary", 13.0, 20.0, 540.0),
	("just east of a cell boundary", 13.0, 20.001, 540.0),
	("outside the grid, north", 40.001, 10.0, 540.0),
	("outside the grid, far", 100.0, 150.0, 360.0),
	("at station C, outside the grid", LOCAL["C"][0], LOCAL["C"][1], 720.0),
	("on a cell whose first two slots are empty", 20.0, 40.0, 180.0),
	("on a cell with an empty slot between two stations", 20.0, -20.0, 540.0),
]
print(json.dumps({"depth": depth_digest, "field": field_digest, "day": day_digest,
	"samples": [[label, n, e, t] + list(sample(n, e, t)) for label, n, e, t in points]}, indent=1))
