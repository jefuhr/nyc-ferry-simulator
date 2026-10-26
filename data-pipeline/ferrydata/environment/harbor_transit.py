"""Harbor transits run by the CLI (ferrysim maneuver transit with a harbor pack), checked against the depth set: the
run arrived with no depth misses and no time holds, and the depth under the keel is continuous at every tile seam the
track crosses. Writes a table and a plot per transit."""
import heapq
import math
from pathlib import Path

import numpy as np

from ferrydata import tiles
from ferrydata.environment import harbor, pack

# Route planning: a lattice this fine, over this much margin around the start and the end.
ROUTE_CELL_M = 25.
ROUTE_MARGIN_M = 600.


def plan(depth, start, end, deep=-6., clear=25.):
	"""A transit route through the depth set's water: the shortest path over a 25 m lattice of points whose bed is below
	`deep` m NAVD88 and at least `clear` m from shallower water (8-connected), from the start to the end, then legs as
	long as they can be while every point along them stays where the bed is below `deep`. Returns the waypoints after the
	start, the end last. Deterministic: the same depth set and points give the same route."""
	n0, n1 = min(start[0], end[0]) - ROUTE_MARGIN_M, max(start[0], end[0]) + ROUTE_MARGIN_M
	e0, e1 = min(start[1], end[1]) - ROUTE_MARGIN_M, max(start[1], end[1]) + ROUTE_MARGIN_M
	rows, cols = int((n1 - n0) / ROUTE_CELL_M) + 1, int((e1 - e0) / ROUTE_CELL_M) + 1
	def bed(r, c):
		sample = depth.sample(n0 + r * ROUTE_CELL_M, e0 + c * ROUTE_CELL_M)
		return sample.bed if sample.kind == 'known' else np.nan
	beds = np.array([[bed(r, c) for c in range(cols)] for r in range(rows)])
	water = np.isfinite(beds) & (beds < deep)
	reach = int(math.ceil(clear / ROUTE_CELL_M))
	open_ = water.copy()
	for dr in range(-reach, reach + 1):
		for dc in range(-reach, reach + 1):
			if dr * dr + dc * dc > reach * reach:
				continue
			shifted = np.zeros_like(water)
			shifted[max(0, -dr):rows - max(0, dr), max(0, -dc):cols - max(0, dc)] = water[max(0, dr):rows + min(0, dr), max(0, dc):cols + min(0, dc)]
			open_ &= shifted
	cells = np.argwhere(open_)
	if not cells.size:
		raise ValueError(f'no water below {deep} m with {clear} m clearance between {start} and {end}')
	def nearest(point):
		return tuple(int(x) for x in cells[np.argmin(((cells - [(point[0] - n0) / ROUTE_CELL_M, (point[1] - e0) / ROUTE_CELL_M]) ** 2).sum(axis=1))])
	source, target = nearest(start), nearest(end)
	distance, previous, heap = {source: 0.}, {}, [(0., source)]
	while heap:
		d, cell = heapq.heappop(heap)
		if cell == target:
			break
		if d > distance[cell]:
			continue
		for dr, dc in ((-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)):
			r, c = cell[0] + dr, cell[1] + dc
			if 0 <= r < rows and 0 <= c < cols and open_[r, c] and d + math.hypot(dr, dc) < distance.get((r, c), math.inf):
				distance[(r, c)] = d + math.hypot(dr, dc)
				previous[(r, c)] = cell
				heapq.heappush(heap, (distance[(r, c)], (r, c)))
	if target not in distance:
		raise ValueError(f'no route through water below {deep} m with {clear} m clearance from {start} to {end}')
	path = [target]
	while path[-1] != source:
		path.append(previous[path[-1]])
	points = [tuple(start)] + [(n0 + r * ROUTE_CELL_M, e0 + c * ROUTE_CELL_M) for r, c in path[::-1]] + [tuple(end)]
	def in_water(a, b):
		for f in np.linspace(0., 1., max(2, int(math.hypot(b[0] - a[0], b[1] - a[1]) / (ROUTE_CELL_M / 2)))):
			r, c = round((a[0] + f * (b[0] - a[0]) - n0) / ROUTE_CELL_M), round((a[1] + f * (b[1] - a[1]) - e0) / ROUTE_CELL_M)
			if not (0 <= r < rows and 0 <= c < cols and water[r, c]):
				return False
		return True
	route, i = [], 0
	while i < len(points) - 1:
		j = len(points) - 1
		while j > i + 1 and not in_water(points[i], points[j]):
			j -= 1
		route.append(points[j])
		i = j
	route = [p for k, p in enumerate(route) if k == 0 or p != route[k - 1]]
	for a, b in zip([tuple(start)] + route, route):
		if not in_water(a, b):
			raise ValueError(f'no route through water below {deep} m from {start} to {end}: the leg {a} to {b} leaves it')
	return route


def write_route(path, waypoints):
	Path(path).write_text('north_m,east_m\n' + ''.join(f'{n:.1f},{e:.1f}\n' for n, e in waypoints), encoding='utf-8')


def read_csv(path):
	"""A ferrysim telemetry CSV: its '# key: value' header and its columns as float arrays (text columns skipped)."""
	header, names, rows = {}, None, []
	for line in Path(path).read_text(encoding='utf-8').splitlines():
		if line.startswith('#'):
			key, _, value = line[2:].partition(': ')
			header[key] = value
		elif names is None:
			names = line.split(',')
		elif line:
			rows.append(line.split(','))
	columns = {}
	for k, name in enumerate(names):
		try:
			columns[name] = np.array([float(r[k]) for r in rows])
		except ValueError:
			continue
	return header, columns


def crossings(north, east):
	"""Rows k where the reference point passes from one tile square to another between rows k and k + 1, with the
	fraction of the way at which it crosses each seam line."""
	result = []
	for k in range(len(north) - 1):
		for axis, a, b in (('north', north[k], north[k + 1]), ('east', east[k], east[k + 1])):
			line_a, line_b = math.floor(a / tiles.SIZE_M), math.floor(b / tiles.SIZE_M)
			if line_a != line_b:
				line = tiles.SIZE_M * max(line_a, line_b)
				result.append((k, axis, line, (line - a) / (b - a)))
	return result


def check(csv_path, depth, destination):
	header, c = read_csv(csv_path)
	north, east, keel, t = c['north_m'], c['east_m'], c['depth_under_keel_m'], c['time_s']
	distance = np.concatenate([[0.], np.cumsum(np.hypot(np.diff(north), np.diff(east)))])
	steps = np.abs(np.diff(keel))
	rows = []
	for k, axis, line, fraction in crossings(north, east):
		n = north[k] + fraction * (north[k + 1] - north[k])
		e = east[k] + fraction * (east[k + 1] - east[k])
		direction = np.array([north[k + 1] - north[k], east[k + 1] - east[k]])
		direction /= np.linalg.norm(direction)
		before = depth.sample(n - 1e-3 * direction[0], e - 1e-3 * direction[1])
		after = depth.sample(n + 1e-3 * direction[0], e + 1e-3 * direction[1])
		near = steps[max(0, k - 50):k].tolist() + steps[k + 1:k + 51].tolist()
		rows.append(dict(row=int(k), time_s=float(t[k]), axis=axis, line_m=float(line), north_m=float(n), east_m=float(e),
			distance_m=float(distance[k] + fraction * (distance[k + 1] - distance[k])),
			grid_before=depth.grids[before.grid]['id'] if before.grid >= 0 else None, grid_after=depth.grids[after.grid]['id'] if after.grid >= 0 else None,
			bed_step_2mm_m=float(abs(after.bed - before.bed)), keel_step_m=float(steps[k]),
			keel_steps_nearby_median_m=float(np.median(near)), keel_steps_nearby_max_m=float(np.max(near))))
	summary = dict(csv=Path(csv_path).name, vessel=header.get('vessel'), pack=header.get('environment_pack'),
		scenario_start_utc=header.get('scenario_start_utc'), current_field=header.get('environment_current_field'),
		depth_misses=int(header.get('environment_depth_misses', '-1')), time_holds=int(header.get('environment_time_holds', '-1')),
		arrived=header.get('result.arrived'), arrived_s=header.get('result.arrived_s'), duration_s=float(t[-1] - t[0]),
		distance_m=float(distance[-1]), min_depth_under_keel_m=float(keel.min()), seams=rows,
		passed=bool(header.get('result.arrived') == 'yes' and header.get('environment_depth_misses') == '0'
			and header.get('environment_time_holds') == '0' and rows and all(r['grid_before'] != r['grid_after'] or r['grid_before'] is None for r in rows)
			and all(r['keel_step_m'] <= 2 * r['keel_steps_nearby_max_m'] + 1e-3 for r in rows)))
	plot(summary, north, east, distance, keel, Path(destination) / (Path(csv_path).stem + '.png'))
	return summary


def plot(summary, north, east, distance, keel, target):
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	figure, (track, profile) = plt.subplots(1, 2, figsize=(14, 6), layout='constrained', gridspec_kw=dict(width_ratios=(1, 1.6)))
	track.plot(east, north, color='#1f6fb4', linewidth=1.5)
	track.plot(east[:1], north[:1], 'o', color='#1f6fb4')
	low_n, high_n = math.floor(north.min() / tiles.SIZE_M), math.floor(north.max() / tiles.SIZE_M) + 1
	low_e, high_e = math.floor(east.min() / tiles.SIZE_M), math.floor(east.max() / tiles.SIZE_M) + 1
	for line in range(low_n, high_n + 1):
		track.axhline(line * tiles.SIZE_M, color='#999999', linewidth=.8, linestyle='--')
	for line in range(low_e, high_e + 1):
		track.axvline(line * tiles.SIZE_M, color='#999999', linewidth=.8, linestyle='--')
	for i in range(low_n, high_n):
		for j in range(low_e, high_e):
			track.text((j + .5) * tiles.SIZE_M, (i + .5) * tiles.SIZE_M, harbor.tile_id(i, j), ha='center', va='center', fontsize=8, color='#777777')
	for s in summary['seams']:
		track.plot(s['east_m'], s['north_m'], 'x', color='#c0392b')
	track.set_aspect('equal')
	track.set_xlabel('East of the world origin (m)')
	track.set_ylabel('North of the world origin (m)')
	track.set_title('Track and tile seams (crossings marked x)')
	profile.plot(distance / 1000., keel, color='#1f6fb4', linewidth=1.)
	for s in summary['seams']:
		profile.axvline(s['distance_m'] / 1000., color='#c0392b', linewidth=.8, linestyle=':')
	profile.set_xlabel('Distance run (km)')
	profile.set_ylabel('Depth under keel (m)')
	profile.set_title('Depth under keel; seam crossings dotted')
	profile.grid(alpha=.25)
	figure.suptitle(f'{summary["vessel"]} transit, {summary["pack"]}, start {summary["scenario_start_utc"]}: arrived {summary["arrived"]}, '
		f'{summary["depth_misses"]} depth misses, {summary["time_holds"]} time holds')
	figure.savefig(target, dpi=130, metadata={'Software': 'ferrydata.environment'})
	plt.close(figure)


def run(csv_paths, destination):
	destination = Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	depth = pack.load_depth(harbor.tiles_root() / 'env' / 'depth')
	summaries = [check(path, depth, destination) for path in csv_paths]
	pack.write_json(destination / 'transit-check.json', summaries)
	lines = ['# Harbor transits', '']
	for s in summaries:
		lines += [f'## {s["csv"]}', '',
			f'{s["vessel"]} on `{s["pack"]}` from {s["scenario_start_utc"]}, current field {s["current_field"]}: arrived {s["arrived"]} after '
			f'{s["arrived_s"]} s, {s["distance_m"] / 1000:.2f} km; {s["depth_misses"]} depth misses, {s["time_holds"]} time holds; least depth '
			f'under keel {s["min_depth_under_keel_m"]:.2f} m. Verdict: {"PASS" if s["passed"] else "FAIL"}.', '',
			'At each seam the track crosses: the depth set\'s bed 1 mm either side along the track, and the depth under keel '
			'between the two CSV rows around the crossing, against the row-to-row changes within 50 rows of it.', '',
			'| Seam line | Crossed at (north, east) m | Grids | Bed step over 2 mm m | Keel step at crossing m | Nearby keel steps median / largest m |',
			'|---|---|---|---:|---:|---|']
		for r in s['seams']:
			lines.append(f'| {r["axis"]} {r["line_m"]:g} | {r["north_m"]:.1f}, {r["east_m"]:.1f} | {r["grid_before"]} to {r["grid_after"]} | '
				f'{r["bed_step_2mm_m"]:.2e} | {r["keel_step_m"]:.3f} | {r["keel_steps_nearby_median_m"]:.3f} / {r["keel_steps_nearby_max_m"]:.3f} |')
		lines += ['', f'![{s["csv"]}]({Path(s["csv"]).stem}.png)', '']
	(destination / 'transit-check.md').write_text('\n'.join(lines), encoding='utf-8')
	return all(s['passed'] for s in summaries)
