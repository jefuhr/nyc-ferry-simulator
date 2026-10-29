"""Full 26-berth x 2-hull x 28-day x 25-time start assessment from current harbor contacts.

Poses use LandingApproach.BowOn's 4 m bow clearance; contacts use the production
Python landing gate. The low-water time is the exact minimum of the piecewise-linear
spatial tide at the pose reference point over [0, 86340] s, including every gauge breakpoint.
"""
import json
import math
from pathlib import Path

import numpy as np
import pandas as pd
from shapely.geometry import LineString, Polygon

from ferrydata import export_unity, geodesy, manifest
from ferrydata.environment import pack, series
from ferrydata.harbor_landings import assessment as a, faces


def catalogue(contacts, notes=None):
	notes = faces.load_notes() if notes is None else notes
	groups = {}
	for r in contacts:
		if r.get('berth_face'):
			groups.setdefault((r['stop_id'], r['berth_id']), []).append(r)
	result = []
	for (stop, berth), records in groups.items():
		ids = records[0].get('berth_face_segment_ids', [r['id'] for r in records])
		lookup = {r['id']: r for r in records}
		ordered = [lookup[i] for i in ids]
		production = all(export_unity.landing_gate(r)[0] == 'kept' for r in ordered)
		eligible = faces.face_eligible(stop, berth, ordered, notes, production)
		# Crew confirmation never permits malformed or reversed contact segments.
		if eligible and not production:
			eligible = all(export_unity.landing_gate(dict(r, water_side_crew_reported=True))[0] == 'kept' for r in ordered)
		start, end = np.array(ordered[0]['start_ne_m']), np.array(ordered[-1]['end_ne_m'])
		delta = end - start
		along = delta / math.hypot(*delta)
		normal = np.array([along[1], -along[0]])
		straight = math.cos(math.radians(1.))
		if any(np.dot((np.array(r['end_ne_m']) - r['start_ne_m']) / math.dist(r['start_ne_m'], r['end_ne_m']), along) < straight for r in ordered):
			eligible = False
		result.append(dict(stop_id=stop, stop_name=records[0]['stop_name'], berth_id=berth,
			berth_name=records[0].get('berth_name', 'Main berth'), eligible=eligible,
			mid=(start + end) * .5, normal=normal, selected_segment_ids=ids,
			geometry_sha256=faces.geometry_digest(ordered)))
	return result


def pose(berth, vessel):
	bow = a.world_outline(vessel, 0., 0., 0.)[:, 0].max()
	n, e = np.asarray(berth['mid']) + np.asarray(berth['normal']) * (bow + 4.)
	# ScenarioSettings stores geodetic coordinates, then resolves them back through the tangent plane.
	lat, lon = geodesy.from_local(n, e)
	n, e = geodesy.to_local(lat, lon)
	heading = math.degrees(math.atan2(-berth['normal'][1], -berth['normal'][0])) % 360.
	return float(n), float(e), heading


def low_time(levels, north, east):
	times = sorted({0., 86340., *(t for g in levels.gauges for t in g.times if 0. <= t <= 86340.)})
	return min(times, key=lambda t: (levels.level(north, east, t), t))


def build(contacts_path, env, destination, depth_path=None):
	env, destination = Path(env), Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	# Verification checks the actual schema, raw CSV hashes and all 28 day/depth digests before assessment.
	pack.verify(env)
	depth_path = Path(depth_path or env / 'depth')
	depth = pack.load_depth(depth_path)
	contacts = json.loads(Path(contacts_path).read_text(encoding='utf-8'))
	berths = catalogue(contacts)
	if len(berths) != 26:
		raise ValueError(f'expected 26 declared berths, found {len(berths)}')
	confirmed_ids = {i for berth in berths if berth['eligible'] for i in berth['selected_segment_ids']}
	retained = [r for r in contacts if export_unity.landing_gate(r)[0] == 'kept'
		or (r['id'] in confirmed_ids and export_unity.landing_gate(dict(r, water_side_crew_reported=True))[0] == 'kept')]
	lines = [LineString([r['start_ne_m'], r['end_ne_m']]) for r in retained]
	levels = {day: a.load_levels(env / 'days' / day) for day in series.DAYS}
	rows = []
	for berth in berths:
		for hull in ('t-class', 'k-class'):
			v = a.vessel(hull)
			draft, basis = a.maximum_draft(v)
			north = east = heading = clearance = float('nan')
			overlap, nearest, prepared = False, '', None
			if berth['eligible']:
				north, east, heading = pose(berth, v)
				outline = a.world_outline(v, north, east, heading)
				polygon = Polygon(outline)
				distances = [polygon.distance(line) for line in lines]
				k = int(np.argmin(distances))
				clearance, nearest = distances[k], retained[k]['id']
				overlap = clearance == 0.
				prepared = a.PreparedFootprint(depth, outline, next(iter(levels.values())))
			for day, level in levels.items():
				low = low_time(level, north, east) if prepared else low_time(level, *berth['mid'])
				for label, time in [('hourly', float(h * 3600)) for h in range(24)] + [('local_low_water', low)]:
					minimum = prepared.minimum(level, time) if prepared else float('nan')
					ukc = minimum - draft
					rows.append(dict(stop_id=berth['stop_id'], stop_name=berth['stop_name'], berth_id=berth['berth_id'],
						berth_name=berth['berth_name'], hull=hull, date=day, sample=label, time_s=time,
						north_m=north, east_m=east, heading_deg=heading, clearance_m=clearance, nearest_segment=nearest,
						overlap=overlap, min_depth_m=minimum, draft_m=draft, draft_basis=basis, under_keel_m=ukc,
						water_level_navd88_m=level.level(north, east, time) if prepared else float('nan'),
						status=a.status(ukc, berth['eligible'], clearance, overlap)))
			print(f'{berth["stop_id"]} {berth["berth_name"]} {hull}', flush=True)
	frame = pd.DataFrame(rows)
	frame.to_csv(destination / 'matrix.csv', index=False, lineterminator='\n')
	summaries = []
	for keys, group in frame.groupby(['stop_id', 'stop_name', 'berth_id', 'berth_name', 'hull'], sort=False):
		accepted = group[group.status.isin(('startable', 'warning'))]
		summaries.append(dict(zip(('stop_id', 'stop_name', 'berth_id', 'berth_name', 'hull'), keys), rows=len(group),
			**{s: int((group.status == s).sum()) for s in ('startable', 'warning', 'aground_refusal', 'excluded_face', 'unknown_depth', 'contact_refusal')},
			accepted=len(accepted), worst_ukc_m=group.under_keel_m.min(),
			lowest_accepted_water_navd88_m=accepted.water_level_navd88_m.min()))
	pd.DataFrame(summaries).to_csv(destination / 'summary.csv', index=False, lineterminator='\n')
	pack.write_json(destination / 'berths.json', [dict(
		**{k: v for k, v in berth.items() if k not in ('mid', 'normal')},
		face_midpoint_ne_m=berth['mid'].tolist(), outward_normal_ne=berth['normal'].tolist(),
		hulls=[{k: (None if isinstance(v, float) and not math.isfinite(v) else v) for k, v in s.items()} for s in summaries if s['stop_id'] == berth['stop_id'] and s['berth_id'] == berth['berth_id']])
		for berth in berths])
	pack.write_json(destination / 'provenance.json', dict(contacts_sha256=manifest.sha256_file(contacts_path),
		depth_digest=depth.digest, day_digests={day: pack.read_digest(env / 'days' / day) for day in series.DAYS},
		crew_face_notes_sha256=manifest.sha256_file(Path(faces.__file__).with_name('crew-face-notes.json')),
		rows=len(frame), berths=len(berths), times='24 hourly plus local low water from all gauge breakpoints and day endpoints'))
	return frame
