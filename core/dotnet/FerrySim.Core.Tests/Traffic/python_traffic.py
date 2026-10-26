"""An independent Python implementation of the ferrysim-traffic/1 layout and of the replay rule the core follows
(core/FerrySim.Core/Traffic/README.md, The replay), used to make golden vectors for the C# reader and replay.

    python3 python_traffic.py <this folder>

writes PythonTrafficFixture.cs (the pack's files, embedded) and PythonTrafficVectors.csv (the replay at chosen times) beside
this script. The pack is synthetic: made-up vessels and reports, not AIS data. It is written with struct, json and hashlib as the
pipeline writes packs, and the replay reads the float32 fields back from the bytes, so both sides see the same values."""
import hashlib
import json
import math
import os
import struct
import sys

DATE = "2025-07-14"
EPOCH = "2025-07-14T04:00:00Z"
KNOT = 1852.0 / 3600.0
SLACK = 30.0
TANGENT_MIN = 0.1
CHORD_MIN = 1.0
SLOW = 0.5
NAN = float("nan")
TWO_PI = 2.0 * math.pi


def wrap_two_pi(angle):
	wrapped = math.fmod(angle, TWO_PI)
	if wrapped < 0.0:
		wrapped += TWO_PI
	return 0.0 if wrapped >= TWO_PI else wrapped


def wrap_pi(angle):
	return wrap_two_pi(angle + math.pi) - math.pi


def deg(value):
	return math.radians(value)


# Each vessel: (mmsi, name, ais_type, class, length, beam, draft, moving, ferry, reports), a report (t, north, east, sog, cog, heading)
# with SI values and NaN for what AIS did not give. Segments follow from the 300 s gap rule.
def straight(t0, n0, e0, speed, course, heading, count, step=60.0):
	return [(t0 + k * step, n0 + speed * math.cos(course) * k * step, e0 + speed * math.sin(course) * k * step, speed, course, heading) for k in range(count)]


VESSELS = [
	# 0: straight and steady, every field valid: the Hermite curve is the line.
	(100000001, "STEADY ONE", 70, "cargo", 120.0, 18.0, 6.5, True, False, straight(3600.0, 100.0, 200.0, 5.0, deg(30.0), deg(32.0), 5)),
	# 1: a 90 degree turn to port through north at 5 m/s, consistent SOG and COG: Hermite; AIS headings wrap through north.
	(100000002, "TURNER", 31, "tug-tow", 28.0, 9.0, 3.2, True, False, [
		(3600.0, 0.0, 0.0, 5.0, deg(45.0), deg(45.0)),
		(3660.0, 270.0, 0.0, 5.0, deg(315.0), deg(350.0)),
		(3720.0, 270.0 + 191.0, -191.0, 5.0, deg(315.0), deg(20.0)),
	]),
	# 2: stale SOG and COG (the vessel stopped but still reports 5 m/s): inconsistent, so linear; headings valid.
	(100000003, "STALE", 60, "ferry-passenger", 26.0, 8.0, NAN, True, False, [
		(4000.0, -500.0, 400.0, 5.0, deg(90.0), deg(91.0)),
		(4060.0, -500.0, 420.0, 5.0, deg(90.0), deg(92.0)),
		(4120.0, -500.0, 425.0, 0.1, deg(90.0), deg(93.0)),
	]),
	# 3: SOG missing at one end: linear; no heading anywhere: the tangent.
	(100000004, 'NO "SOG", TEST', 37, "pleasure", 9.0, NAN, NAN, True, False, [
		(5000.0, 1000.0, -1000.0, 3.0, deg(180.0), NAN),
		(5060.0, 820.0, -1000.0, NAN, deg(180.0), NAN),
		(5120.0, 640.0, -990.0, 3.0, deg(177.0), NAN),
	]),
	# 4: COG missing at one end, heading at the other only: linear, tangent heading.
	(100000005, "HALF HEADING", 36, "sailing", NAN, 3.0, NAN, True, False, [
		(6000.0, 2000.0, 2000.0, 2.0, NAN, deg(10.0)),
		(6060.0, 2120.0, 2000.0, 2.0, deg(0.0), NAN),
	]),
	# 5: two segments, a one-report segment between them, and a gap over 300 s before each.
	(100000006, "GAPPY", 52, "tug-tow", 25.0, 9.0, 3.0, True, False, [
		(7000.0, -2000.0, 0.0, 4.0, deg(90.0), deg(90.0)),
		(7060.0, -2000.0, 240.0, 4.0, deg(90.0), deg(90.0)),
		(7400.0, -2000.0, 1600.0, 4.0, deg(90.0), deg(90.0)),
		(7800.0, -2000.0, 3200.0, 4.0, deg(90.0), deg(90.0)),
		(7860.0, -2000.0, 3440.0, 4.0, deg(90.0), deg(90.0)),
	]),
	# 6: static (moving False), jittering within 10 m, some headings: the median position, the circular mean heading.
	(100000007, "MOORED BARGE", 89, "tanker", 90.0, 20.0, 4.0, False, False, [
		(0.0, 3000.0, 3000.0, 0.0, NAN, deg(355.0)),
		(180.0, 3003.0, 2998.0, 0.1, deg(200.0), NAN),
		(360.0, 2997.0, 3004.0, 0.0, NAN, deg(5.0)),
		(540.0, 3001.0, 3001.0, 0.0, NAN, deg(15.0)),
		(1200.0, 3002.0, 2999.0, 0.0, NAN, deg(2.0)),
		(1260.0, 2999.0, 3002.0, 0.0, NAN, NAN),
	]),
	# 7: static, no heading at all: unknown heading.
	(100000008, "", 0, "other", NAN, NAN, NAN, False, False, [
		(100.0, -3000.0, -3000.0, NAN, NAN, NAN),
		(160.0, -3002.0, -2999.0, NAN, NAN, NAN),
		(220.0, -3001.0, -3001.0, NAN, NAN, NAN),
	]),
	# 8: crawling at 0.03 m/s: SOG and COG valid and consistent, no heading, no COG at speed: below the slow limit nothing
	# turns it, so north.
	(100000009, "CRAWLER", 30, "fishing", 14.0, 5.0, 2.0, True, False, [
		(8000.0, 500.0, 500.0, 0.03, deg(80.0), NAN),
		(8060.0, 500.5, 503.0, 0.03, deg(80.0), NAN),
	]),
	# 9: a ferry candidate at a landing at 08:30 local (30600 s), moving in.
	(100000010, "FERRY NEAR", 60, "ferry-passenger", 26.0, 8.0, 2.0, True, True, straight(30480.0, 1000.0, 1100.0, 1.0, deg(270.0), deg(270.0), 5)),
	# 10: another ferry candidate 120 m from the same landing, not present at 08:30 but reporting 90 s later.
	(100000011, "FERRY LATE", 60, "ferry-passenger", 26.0, 8.0, 2.0, True, True, straight(30690.0, 1120.0, 1000.0, 0.5, deg(0.0), deg(0.0), 3)),
	# 11: a ferry candidate far away.
	(100000012, "FERRY FAR", 60, "ferry-passenger", 26.0, 8.0, 2.0, True, True, straight(30000.0, 5000.0, 5000.0, 3.0, deg(0.0), deg(0.0), 30)),
	# 12: stopped in place with a heading at one end only: zero chord, so the valid AIS heading is held.
	(100000013, "IDLE", 31, "tug-tow", 1.0, NAN, NAN, True, False, [
		(9000.0, -100.0, -100.0, 0.0, NAN, NAN),
		(9060.0, -100.0, -100.0, 0.0, NAN, deg(123.0)),
	]),
	# 13: a consistent 60 degree turn to starboard with no AIS heading: Hermite, tangent heading.
	(100000014, "NO HEADING TURN", 50, "pilot", 16.0, 5.0, 1.5, True, False, [
		(10000.0, 0.0, -4000.0, 4.0, deg(0.0), NAN),
		(10060.0, 229.0, -3868.0, 4.0, deg(60.0), NAN),
	]),
	# 14: one-report segments: SOG and COG but no heading (the tangent), then nothing at all (unknown), then 0.3 m/s with a COG
	# but no heading: below the slow limit, so no tangent (unknown).
	(100000015, "LONE REPORTS", 51, "search-rescue", 12.0, 4.0, 1.0, True, False, [
		(11000.0, 4000.0, -4000.0, 2.0, deg(135.0), NAN),
		(11500.0, 4100.0, -4000.0, NAN, NAN, NAN),
		(12100.0, 4200.0, -4000.0, 0.3, deg(45.0), NAN),
	]),
	# 15: arrives at 3 m/s with AIS headings, then sits at a berth under 0.5 m/s with no heading while its COG jitters: the
	# stretch holds the last valid heading (89); the slow interval into a report with a heading holds that one (91).
	(100000016, "BERTHING", 60, "ferry-passenger", 26.0, 8.0, 2.0, True, False, [
		(12000.0, 0.0, 6000.0, 3.0, deg(90.0), deg(88.0)),
		(12060.0, 0.0, 6180.0, 3.0, deg(90.0), deg(89.0)),
		(12120.0, 2.0, 6300.0, 0.3, deg(85.0), NAN),
		(12180.0, 3.0, 6301.0, 0.1, deg(300.0), NAN),
		(12240.0, 2.0, 6302.0, 0.0, NAN, NAN),
		(12300.0, 2.5, 6300.5, 0.1, deg(170.0), NAN),
		(12360.0, 1.0, 6301.0, 0.2, deg(45.0), deg(91.0)),
		(12420.0, 1.0, 6420.0, 2.0, deg(90.0), deg(90.0)),
	]),
	# 16: never an AIS heading; at speed its COGs were 350, 10 and 20 (either side of north), then it sits with jittering COGs:
	# the stretch takes the median COG at speed, 10 (not the 20 of a plain sort).
	(100000017, "NO HEADING MOORED", 60, "ferry-passenger", 25.0, 8.0, 2.0, True, False, [
		(13000.0, 0.0, 7000.0, 3.0, deg(350.0), NAN),
		(13060.0, 177.27, 6968.74, 3.0, deg(10.0), NAN),
		(13120.0, 354.54, 7000.0, 3.0, deg(20.0), NAN),
		(13180.0, 400.0, 7010.0, 0.2, deg(250.0), NAN),
		(13240.0, 401.0, 7011.0, 0.1, deg(120.0), NAN),
		(13300.0, 400.5, 7010.5, 0.0, NAN, NAN),
		(13360.0, 400.0, 7011.0, 0.1, deg(300.0), NAN),
	]),
	# 17: slowing from 1 m/s to 0.05 m/s with no heading: not slow at both ends, so the old order; near the end the curve is
	# too slow for a tangent, so the chord.
	(100000018, "SLOWING", 37, "pleasure", 10.0, 3.5, 1.0, True, False, [
		(14000.0, -6000.0, 0.0, 1.0, deg(0.0), NAN),
		(14060.0, -5969.0, 0.0, 0.05, deg(0.0), NAN),
	]),
]

QUERY_OFFSETS = (0.0, 0.25, 0.5, 0.75)


def segments_of(reports):
	segments, segment = [], 0
	for k, report in enumerate(reports):
		if k > 0 and report[0] - reports[k - 1][0] > 300.0:
			segment += 1
		segments.append(segment)
	return segments


def f32(value):
	return struct.unpack("<f", struct.pack("<f", value))[0]


def g17(value):
	return "" if math.isnan(value) else "%.17g" % value


def write_pack():
	records, roster = b"", []
	for index, (mmsi, name, ais_type, cls, length, beam, draft, moving, ferry, reports) in enumerate(VESSELS):
		segments = segments_of(reports)
		for (t, n, e, sog, cog, heading), segment in zip(reports, segments):
			records += struct.pack("<iiddd", index, segment, t, n, e) + struct.pack("<fff", sog, cog, heading)
		quoted = '"' + name.replace('"', '""') + '"' if ("," in name or '"' in name) else name
		roster.append(",".join([str(index), str(mmsi), quoted, str(ais_type), cls, g17(length), g17(beam), g17(draft), g17(reports[0][0]), g17(reports[-1][0]),
			str(len(reports)), str(segments[-1] + 1), "True" if moving else "False", "True" if ferry else "False"]))
	vessels = ("index,mmsi,name,ais_type,class,length_m,beam_m,draft_m,first_t_s,last_t_s,reports,segments,moving,ferry_candidate\n" + "".join(r + "\n" for r in roster)).encode()
	layout = {"byte_order": "little-endian", "record_size_bytes": 44, "fields": [
		{"name": n, "type": t, "offset_bytes": o} for n, t, o in [("vessel_index", "int32", 0), ("segment", "int32", 4), ("t_s", "float64", 8),
			("north_m", "float64", 16), ("east_m", "float64", 24), ("sog_mps", "float32", 32), ("cog_rad", "float32", 36), ("heading_rad", "float32", 40)]],
		"missing_float": "IEEE 754 quiet NaN", "angles": "radians clockwise from true north",
		"positions": "NED horizontal metres via ferrydata.geodesy.to_local at 40.7000 N, -74.0150 E"}
	layout_bytes = (json.dumps(layout, indent="\t") + "\n").encode()
	files = [("vessels.csv", vessels), ("reports.bin", records), ("reports.json", layout_bytes)]
	digest = hashlib.sha256()
	for name, data in files:
		digest.update(name.encode() + b"\0" + data + b"\0")
	digest.update(EPOCH.encode() + b"\0")
	by_class = {}
	for vessel in VESSELS:
		by_class[vessel[3]] = by_class.get(vessel[3], 0) + 1
	count = len(records) // 44
	manifest = {"format": "ferrysim-traffic/1", "date": DATE, "epoch_utc": EPOCH,
		"bbox": {"south": 40.5, "north": 40.9, "west": -74.1, "east": -73.75},
		"source": {"name": "python-golden-vector", "sha256": None},
		"source_cadence": "synthetic golden vectors, not AIS data",
		"sources": [],
		"time_coverage": {"requested_from_s": -3600.0, "requested_to_s": 90000.0, "covered_from_s": -3600.0, "covered_to_s": 90000.0, "missing_intervals": []},
		"drop_accounting": "synthetic: nothing dropped",
		"counts": {"vessels": len(VESSELS), "reports": count, "source_reports": count + 3,
			"dropped_reports": {"impossible_position": 0, "outside_bbox": 1, "invalid_mmsi": 0, "invalid_time": 0, "duplicate": 2, "position_jump": 0, "outside_time_window": 0},
			"vessels_by_class": dict(sorted(by_class.items()))},
		"cleaning": {"segment_gap_s": 300.0}, "class_mapping": {"60-69": "ferry-passenger"},
		"ferry_candidates": {"source": "synthetic", "sha256": None, "subsets": ["T", "K"]},
		"files": [{"name": name, "sha256": hashlib.sha256(data).hexdigest(), "size_bytes": len(data)} for name, data in files],
		"digest": digest.hexdigest()}
	manifest_bytes = (json.dumps(manifest, indent="\t", ensure_ascii=False, allow_nan=False) + "\n").encode()
	return files, manifest_bytes, digest.hexdigest(), records


def read_back(records):
	"""The reports as the bytes hold them: float32 fields rounded, grouped by vessel."""
	by_vessel = {}
	for k in range(len(records) // 44):
		vessel, segment, t, n, e = struct.unpack_from("<iiddd", records, 44 * k)
		sog, cog, heading = struct.unpack_from("<fff", records, 44 * k + 32)
		by_vessel.setdefault(vessel, []).append((segment, t, n, e, sog, cog, heading))
	return by_vessel


def static_pose(reports):
	def median(values):
		values = sorted(values)
		m = len(values) // 2
		return values[m] if len(values) % 2 else (values[m - 1] + values[m]) / 2.0
	north = median([r[2] for r in reports])
	east = median([r[3] for r in reports])
	s = c = 0.0
	valid = 0
	for r in reports:
		if not math.isnan(r[6]):
			s += math.sin(r[6])
			c += math.cos(r[6])
			valid += 1
	heading = wrap_two_pi(math.atan2(s, c)) if valid and (s != 0.0 or c != 0.0) else NAN
	return north, east, heading


def end_velocity(report, dt, dn, de):
	sog, cog = report[4], report[5]
	if math.isnan(sog) or math.isnan(cog):
		return None
	v = (sog * math.cos(cog), sog * math.sin(cog))
	miss = math.hypot(v[0] * dt - dn, v[1] * dt - de)
	return v if miss <= math.hypot(dn, de) + SLACK else None


def circular_median(angles):
	"""The median of angles on the circle: cut the circle at the widest gap between neighbours (the first of equal widest,
	counting the gap through north last), lay the rest out in a line from there, take the middle (the mean of the two middle
	ones for an even count) and wrap it back."""
	ring = sorted(wrap_two_pi(a) for a in angles)
	count = len(ring)
	widest, cut = -1.0, 0
	for k in range(count):
		gap = (ring[k + 1] - ring[k]) if k + 1 < count else (ring[0] + TWO_PI - ring[-1])
		if gap > widest:
			widest, cut = gap, k
	line = ring[cut + 1:] + [a + TWO_PI for a in ring[:cut + 1]]
	middle = line[count // 2] if count % 2 else 0.5 * (line[count // 2 - 1] + line[count // 2])
	return wrap_two_pi(middle)


def is_slow(a, b):
	"""Both ends' SOG (where given) and the step's own speed under the slow limit."""
	dt = b[1] - a[1]
	if math.hypot(b[2] - a[2], b[3] - a[3]) / dt >= SLOW:
		return False
	return all(math.isnan(r[4]) or r[4] < SLOW for r in (a, b))


def held_heading(segment_reports, k):
	"""The heading a slow stretch without AIS headings holds, for the stretch holding the interval from report k: the last valid
	AIS heading before the stretch, else the median of the segment's COGs reported at or above the slow limit, else north."""
	start = k
	while start > 0 and is_slow(segment_reports[start - 1], segment_reports[start]) and math.isnan(segment_reports[start - 1][6]) and math.isnan(segment_reports[start][6]):
		start -= 1
	for r in reversed(segment_reports[:start]):
		if not math.isnan(r[6]):
			return r[6], "ais-last"
	at_speed = [r[5] for r in segment_reports if not math.isnan(r[4]) and r[4] >= SLOW and not math.isnan(r[5])]
	if at_speed:
		return circular_median(at_speed), "cog-median"
	return 0.0, "unknown"


def interval(segment_reports, k, t):
	a, b = segment_reports[k], segment_reports[k + 1]
	dt = b[1] - a[1]
	s = (t - a[1]) / dt
	dn, de = b[2] - a[2], b[3] - a[3]
	v0, v1 = end_velocity(a, dt, dn, de), end_velocity(b, dt, dn, de)
	if v0 is not None and v1 is not None:
		# Hermite basis and its derivative, written out separately from the C#.
		h = (2 * s ** 3 - 3 * s ** 2 + 1, s ** 3 - 2 * s ** 2 + s, -2 * s ** 3 + 3 * s ** 2, s ** 3 - s ** 2)
		d = (6 * s ** 2 - 6 * s, 3 * s ** 2 - 4 * s + 1, -6 * s ** 2 + 6 * s, 3 * s ** 2 - 2 * s)
		pos = [h[0] * a[2 + k] + h[1] * dt * v0[k] + h[2] * b[2 + k] + h[3] * dt * v1[k] for k in (0, 1)]
		vel = [(d[0] * a[2 + k] + d[2] * b[2 + k]) / dt + d[1] * v0[k] + d[3] * v1[k] for k in (0, 1)]
		motion = "hermite"
	else:
		# (1 - s) p0 + s p1, so a report's own time gives its position exactly.
		pos = [(1 - s) * a[2] + s * b[2], (1 - s) * a[3] + s * b[3]]
		vel = [dn / dt, de / dt]
		motion = "linear"
	h0, h1 = a[6], b[6]
	if not math.isnan(h0) and not math.isnan(h1):
		return motion, pos, vel, wrap_two_pi(h0 + s * wrap_pi(h1 - h0)), "ais"
	if is_slow(a, b):
		# Below the slow limit motion says nothing about the heading: one valid AIS heading is held, else the stretch's.
		if not math.isnan(h0) or not math.isnan(h1):
			return motion, pos, vel, h1 if math.isnan(h0) else h0, "ais-held"
		heading, source = held_heading(segment_reports, k)
		return motion, pos, vel, heading, source
	if math.hypot(vel[0], vel[1]) >= TANGENT_MIN:
		return motion, pos, vel, wrap_two_pi(math.atan2(vel[1], vel[0])), "tangent"
	if not math.isnan(h0) or not math.isnan(h1):
		return motion, pos, vel, h1 if math.isnan(h0) else h0, "ais-held"
	if math.hypot(dn, de) >= CHORD_MIN:
		return motion, pos, vel, wrap_two_pi(math.atan2(de, dn)), "chord"
	return motion, pos, vel, 0.0, "unknown"


def sample(vessel, reports, t):
	"""None when absent, else (motion, (north, east), (vn, ve), heading, source)."""
	moving = VESSELS[vessel][7]
	# The segment whose span holds t, if any.
	spans = {}
	for k, r in enumerate(reports):
		spans.setdefault(r[0], []).append(k)
	for segment, members in spans.items():
		first, last = reports[members[0]], reports[members[-1]]
		if not (first[1] <= t <= last[1]):
			continue
		if not moving:
			north, east, heading = static_pose(reports)
			return ("static", (north, east), (0.0, 0.0), 0.0, "unknown") if math.isnan(heading) else ("static", (north, east), (0.0, 0.0), heading, "ais")
		if len(members) == 1:
			r = first
			v = (0.0, 0.0) if math.isnan(r[4]) or math.isnan(r[5]) else (r[4] * math.cos(r[5]), r[4] * math.sin(r[5]))
			if not math.isnan(r[6]):
				return "report", (r[2], r[3]), v, r[6], "ais"
			if not math.isnan(r[4]) and r[4] >= SLOW and math.hypot(*v) >= TANGENT_MIN:
				return "report", (r[2], r[3]), v, wrap_two_pi(math.atan2(v[1], v[0])), "tangent"
			return "report", (r[2], r[3]), v, 0.0, "unknown"
		# The interval starting at the last report at or before t; at the segment's last report, the one ending there.
		k = max(m for m in members if reports[m][1] <= t)
		if k == members[-1]:
			k = members[-2]
		return interval([reports[m] for m in members], members.index(k), t)
	return None


def query_times(reports):
	times = set()
	for k, r in enumerate(reports):
		times.add(r[1])
		times.add(r[1] - 1e-6)
		times.add(r[1] + 1e-6)
		if k + 1 < len(reports):
			for offset in QUERY_OFFSETS:
				times.add(r[1] + offset * (reports[k + 1][1] - r[1]))
			times.add(0.5 * (r[1] + reports[k + 1][1]) + 0.123)
	times.add(reports[0][1] - 100.0)
	times.add(reports[-1][1] + 100.0)
	return sorted(times)


def main():
	folder = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
	files, manifest, digest, records = write_pack()
	by_vessel = read_back(records)
	rows = ["vessel,mmsi,t_s,present,motion,heading_source,north_m,east_m,velocity_north_mps,velocity_east_mps,heading_rad"]
	for vessel in sorted(by_vessel):
		reports = by_vessel[vessel]
		for t in query_times(reports):
			result = sample(vessel, reports, t)
			if result is None:
				rows.append(f"{vessel},{VESSELS[vessel][0]},{t!r},False,,,,,,,")
			else:
				motion, pos, vel, heading, source = result
				rows.append(f"{vessel},{VESSELS[vessel][0]},{t!r},True,{motion},{source},{pos[0]!r},{pos[1]!r},{vel[0]!r},{vel[1]!r},{heading!r}")
	with open(os.path.join(folder, "PythonTrafficVectors.csv"), "w", encoding="utf-8", newline="\n") as handle:
		handle.write("\n".join(rows) + "\n")
	vessels_text = files[0][1].decode().rstrip("\n").split("\n")
	layout_text = files[2][1].decode()
	manifest_text = manifest.decode().rstrip("\n").split("\n")
	def cs_lines(lines):
		return "\n".join("\t\t\t" + json.dumps(line) + "," for line in lines)
	fixture = f"""using System;
using System.IO;
using System.Text;

namespace FerrySim.Core.Tests.Traffic
{{
	/// <summary>
	/// A small traffic pack written by an independent Python implementation of the ferrysim-traffic/1 layout (struct records,
	/// json.dumps manifests, pandas-style %.17g roster numbers, hashlib digests), with the replay at chosen times beside it in
	/// PythonTrafficVectors.csv. Made-up vessels, not AIS data. Generated by python_traffic.py beside this file
	/// (python3 python_traffic.py &lt;this folder&gt;); do not edit by hand.
	/// </summary>
	public static class PythonTrafficFixture
	{{
		/// <summary>Python's pack digest.</summary>
		public const string Digest = "{digest}";

		private static readonly string[] Vessels =
		{{
{cs_lines(vessels_text)}
		}};

		private static readonly string[] Manifest =
		{{
{cs_lines(manifest_text)}
		}};

		private const string Layout = {json.dumps(layout_text)};

		private const string Reports = "{files[1][1].hex()}";

		/// <summary>Writes the pack as &lt;root&gt;/harbor/traffic/{DATE} and returns the day folder.</summary>
		public static string Write(string root)
		{{
			var day = Path.Combine(root, "harbor", "traffic", "{DATE}");
			Directory.CreateDirectory(day);
			File.WriteAllBytes(Path.Combine(day, "vessels.csv"), Text(Vessels));
			File.WriteAllBytes(Path.Combine(day, "manifest.json"), Text(Manifest));
			File.WriteAllBytes(Path.Combine(day, "reports.json"), Encoding.UTF8.GetBytes(Layout));
			File.WriteAllBytes(Path.Combine(day, "reports.bin"), Hex(Reports));
			return day;
		}}

		private static byte[] Text(string[] lines) => Encoding.UTF8.GetBytes(string.Join("\\n", lines) + "\\n");

		private static byte[] Hex(string hex)
		{{
			var bytes = new byte[hex.Length / 2];
			for (var i = 0; i < bytes.Length; i++)
			{{
				bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
			}}

			return bytes;
		}}
	}}
}}
"""
	with open(os.path.join(folder, "PythonTrafficFixture.cs"), "w", encoding="utf-8", newline="\n") as handle:
		handle.write(fixture)
	print(digest)
	print(f"{len(rows) - 1} vectors")


if __name__ == "__main__":
	main()
