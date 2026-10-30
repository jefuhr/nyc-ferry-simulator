"""Reproducible test-route candidates on the adopted floor A, never navigation advice.
The existing harbor_transit planner proposes deep-water legs only. Berth approach,
contacts, actual driven trajectories and eligibility are independently tested in Unity.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import numpy as np
from shapely.geometry import LineString, shape
from ferrydata.environment.pack import DepthSet, load_depth
from ferrydata.environment.harbor_transit import plan


def face(contacts, stop):
	segments = [s for s in contacts if s['stop_id'] == stop and s.get('primary_face')]
	if not segments:
		raise ValueError('missing primary face ' + stop)
	a, b = np.array(segments[0]['start_ne_m']), np.array(segments[-1]['end_ne_m'])
	return (a + b) / 2, np.array(segments[0]['water_normal_ne'])


def corridor(depth, points, half_width=15, spacing=2.5):
	missing, maximum, count = [], -math.inf, 0
	for leg, (a, b) in enumerate(zip(points, points[1:])):
		a, b = np.array(a), np.array(b)
		length = np.linalg.norm(b - a)
		if length == 0:
			continue
		normal = np.array([b[1] - a[1], a[0] - b[0]]) / length
		for f in np.linspace(0, 1, max(2, math.ceil(length / spacing) + 1)):
			for offset in np.linspace(-half_width, half_width, int(2 * half_width / spacing) + 1):
				n, e = a + (b - a) * f + normal * offset
				sample = depth.sample(float(n), float(e))
				count += 1
				if sample.kind != 'known':
					if len(missing) < 100: missing.append([leg, float(n), float(e), sample.kind])
				else:
					maximum = max(maximum, sample.bed)
	return {'samples': count, 'missing_examples': missing, 'zero_missing': not missing, 'highest_bed_navd88_m': maximum}


def seams(depth, epsilon=.001):
	pairs, corners = [], []
	for i, a in enumerate(depth.grids):
		for b in depth.grids[i + 1:]:
			n0, n1 = max(a['origin_north_m'], b['origin_north_m']), min(a['end_north_m'], b['end_north_m'])
			e0, e1 = max(a['origin_east_m'], b['origin_east_m']), min(a['end_east_m'], b['end_east_m'])
			if n0 > n1 or e0 > e1: continue
			if n0 != n1 and e0 != e1: raise ValueError('unexpected overlap')
			A, B = DepthSet('', {}, [a]), DepthSet('', {}, [b])
			points = [(n0, e0)] if n0 == n1 and e0 == e1 else ([(n0, e) for e in np.arange(e0, e1 + .01, 5)] if n0 == n1 else [(n, e0) for n in np.arange(n0, n1 + .01, 5)])
			mismatch, known, delta = 0, 0, 0.0
			for n, e in points:
				x, y = A.sample(n, e), B.sample(n, e)
				mismatch += int(x.kind != y.kind or (x.kind == 'known' and x.bed != y.bed))
				if x.kind == y.kind == 'known': known += 1
				# The public selector on the seam and all 8 epsilon neighbours, including corner quadrants.
				centre = depth.sample(n, e)
				for dn in (-epsilon, 0, epsilon):
					for de in (-epsilon, 0, epsilon):
						s = depth.sample(n + dn, e + de)
						if centre.kind == s.kind == 'known': delta = max(delta, abs(s.bed - centre.bed))
			row = {'a': a['id'], 'b': b['id'], 'nodes': len(points), 'known_nodes': known, 'mismatches': mismatch, 'max_epsilon_delta_m': delta, 'epsilon_m': epsilon}
			(corners if len(points) == 1 else pairs).append(row)
	return {'depth_digest': depth.digest, 'edge_pairs': pairs, 'corner_pairs': corners,
		'verdict': 'pass' if all(r['mismatches'] == 0 for r in pairs + corners) else 'fail'}


def main():
	p = argparse.ArgumentParser()
	p.add_argument('--harbor', type=Path, default=Path('data/processed/harbor'))
	p.add_argument('--out', type=Path, default=Path('artifacts/harbor-e2e/prepared'))
	a = p.parse_args()
	a.out.mkdir(parents=True, exist_ok=True)
	depth = load_depth(a.harbor / 'env/depth')
	contacts = json.loads((a.harbor / 'unity/contacts.json').read_text())
	bridges = json.loads((a.harbor / 'runtime/bridges.json').read_text())['bridges']
	by_span = {s['id']: s for b in bridges for s in b['spans']}
	ids = ['enc:02263858DA411C82', 'enc:0226250074081C82', 'enc:022631AF1AA81C82', 'enc:02260CA8F7FC1C82']
	centres = {s: list(shape(by_span[s]['geometry_ne_m']).centroid.coords)[0] for s in ids + ['enc:02261F20513B1C82']}
	# Fixed NED authoring estimates: channel bends selected from the adopted depth raster.
	# West Roosevelt Island channel uses the ENC's western Queensboro span; the east lift channel is not used.
	east = [(390, 980), centres[ids[0]], centres[ids[1]], (1050, 3000), centres[ids[2]], (3100, 4250), (4500, 3920), (5400, 4330), centres[ids[3]], (7900, 6000), (8450, 6510)]
	hg = 'enc:02261F20513B1C82'
	requests = [
		('st-george', '137', [(390, 980), (-800, 700), (-2200, -1200), (-4000, -3200), (-5850, -4700)], []),
		('astoria', '89', east + [(8200, 6440)], ids),
		('soundview', '112', east + [(8780, 7240), centres[hg], (10300, 8760), (11400, 9900), (11050, 10800), (10650, 12200), (11150, 13400)], ids + [hg]),
	]
	routes = []
	for name, stop, anchors, required in requests:
		if stop != '137':
			middle, normal = face(contacts, stop)
			anchors.append(tuple(middle + normal * 160))
		points = [tuple(anchors[0])]
		for end in anchors[1:]:
			points.extend(plan(depth, points[-1], end, deep=-4., clear=25.))
		points = [list(map(float, q)) for q in points]
		line = LineString(points)
		crossings = []
		for sid in required:
			s = by_span[sid]
			g = shape(s['geometry_ne_m'])
			if not line.intersects(g): raise ValueError(name + ': misses required span ' + sid)
			crossings.append({'id': sid, 'clearances': s['clearances'], 'geometry_ne_m': s['geometry_ne_m']})
		r = {'name': name, 'departure_stop': '87', 'arrival_stop': stop, 'arrival_blocked': stop == '137',
			'date': '2025-07-16', 'minute': {'astoria': 513, 'soundview': 864}.get(name, 738), 'vessel': 't-class', 'points': points, 'required_spans': crossings,
			'rationale': 'Estimated simulation test route only, not navigation advice. Deep-water candidate legs from harbor_transit.plan on adopted floor A, independently corridor-checked. West Roosevelt Island channel for East River journeys; St. George holds off the crew-refused face.',
			'waypoint_provenance': {'bridges_file_sha256': hashlib.sha256((a.harbor / 'runtime/bridges.json').read_bytes()).hexdigest(),
			'contacts_sha256': hashlib.sha256((a.harbor / 'unity/contacts.json').read_bytes()).hexdigest(),
			'depth_digest': depth.digest, 'candidate_bed_below_navd88_m': -4, 'candidate_clearance_m': 25,
			'bends': 'authoring estimates from adopted depth raster; no GTFS route used', 'start_time': 'Astoria 08:33 and Soundview 14:24 local selected to reach low-current arrival windows from the shipped 2025-07-16 station blend; these are test conditions, not navigation advice'},
			'corridor': corridor(depth, points), 'length_m': line.length}
		if stop != '137':
			r['arrival_face'] = {'middle': list(middle), 'normal': list(normal), 'segment_ids': [s['id'] for s in contacts if s['stop_id'] == stop and s.get('primary_face')]}
		routes.append(r)
		print(name, 'waypoints', len(points), 'length_m', round(line.length), r['corridor'], flush=True)
	outbound = next(r for r in routes if r['name'] == 'astoria')
	middle, normal = face(contacts, '87')
	return_points = list(reversed(outbound['points'])) + [list(middle + normal * 160)]
	return_route = dict(outbound, name='astoria-return', departure_stop='89', arrival_stop='87', minute=864,
		arrival_hold=True, points=return_points, required_spans=list(reversed(outbound['required_spans'])),
		arrival_face={'middle': list(middle), 'normal': list(normal), 'segment_ids': [s['id'] for s in contacts if s['stop_id'] == '87' and s.get('primary_face')]},
		corridor=corridor(depth, return_points), length_m=LineString(return_points).length)
	routes.append(return_route)
	(a.out / 'routes.json').write_text(json.dumps({'schema': 'ferrysim-harbor-e2e/1', 'routes': routes}, indent=2) + '\n')
	(a.out / 'seams.json').write_text(json.dumps(seams(depth), indent=2) + '\n')


if __name__ == '__main__':
	main()
