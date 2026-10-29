"""The production C# MinimumStartDepth sampler, vectorized without changing its arithmetic.

The minimum uses the contact hull, a world-aligned 0.25 m lattice, every depth-grid node
inside it and its edges at <=0.25 m. Unknown depth propagates; the outside fallback is
never evidence. Water is sampled at every point, not just at the reference position.
"""
import csv
import json
import math
from pathlib import Path

import numpy as np
import pandas as pd

from ferrydata import paths, operational_depth as od
from ferrydata.environment import pack

SPACING_M = .25
CONTACT_CLEARANCE_M = 1.  # FaceClearance.MinimumMetres


def vessel(hull):
	return json.loads((paths.REPO_ROOT / 'vessels' / (hull + '.json')).read_text(encoding='utf-8'))


def maximum_draft(value):
	p = value['particulars']
	key = 'maxDraft' if p.get('maxDraft') is not None else 'draft'
	return float(p[key]['value']), key if key == 'maxDraft' else 'draft (maxDraft missing)'


def world_outline(value, north, east, heading_deg):
	ring = []
	for name, reverse in (('starboardDemihull', False), ('bowFenderLine', True), ('portDemihull', True), ('sternFenderLine', False)):
		points = value['contact'][name]
		for p in points[::-1] if reverse else points:
			vertex = (float(p['x']['value']), float(p['y']['value']))
			if not ring or math.hypot(vertex[0] - ring[-1][0], vertex[1] - ring[-1][1]) >= 1e-9:
				ring.append(vertex)
	if math.hypot(ring[0][0] - ring[-1][0], ring[0][1] - ring[-1][1]) < 1e-9:
		ring.pop()
	cos, sin = math.cos(math.radians(heading_deg)), math.sin(math.radians(heading_deg))
	return np.array([(north + x * cos - y * sin, east + x * sin + y * cos) for x, y in ring])


def inside(outline, north, east):
	result = np.zeros(np.shape(north), bool)
	for i in range(len(outline)):
		a, b = outline[i], outline[i - 1]
		if b[1] != a[1]:
			result ^= ((a[1] > east) != (b[1] > east)) & (north < (b[0] - a[0]) * (east - a[1]) / (b[1] - a[1]) + a[0])
	return result


def footprint_points(outline, depth):
	lo, hi = outline.min(axis=0), outline.max(axis=0)
	norths, easts = [], []

	def add(n, e):
		nn, ee = np.meshgrid(n, e, indexing='ij')
		mask = inside(outline, nn, ee)
		norths.append(nn[mask]); easts.append(ee[mask])

	add(*(np.arange(math.ceil(lo[i] / SPACING_M) * SPACING_M, hi[i] + 1e-12, SPACING_M) for i in (0, 1)))
	for g in depth.grids:
		axes = []
		for i, (origin, count) in enumerate(((g['origin_north_m'], g['rows']), (g['origin_east_m'], g['cols']))):
			first, last = max(0, math.ceil((lo[i] - origin) / g['cell_m'])), min(count - 1, math.floor((hi[i] - origin) / g['cell_m']))
			axes.append(origin + np.arange(first, last + 1) * g['cell_m'])
		add(*axes)
	for i, a in enumerate(outline):
		delta = outline[(i + 1) % len(outline)] - a
		steps = max(1, math.ceil(math.hypot(*delta) / SPACING_M))
		j = np.arange(steps + 1)
		norths.append(a[0] + delta[0] * j / steps)
		easts.append(a[1] + delta[1] * j / steps)
	return np.concatenate(norths), np.concatenate(easts)


def beds(depth, north, east):
	result = np.full(np.shape(north), np.nan)
	claimed = np.zeros(np.shape(north), bool)
	for g in depth.grids:
		row, col = (north - g['origin_north_m']) / g['cell_m'], (east - g['origin_east_m']) / g['cell_m']
		mask = ~claimed & (row >= 0.) & (row <= g['rows'] - 1) & (col >= 0.) & (col <= g['cols'] - 1)
		claimed |= mask
		r, c = np.minimum(row[mask].astype(int), g['rows'] - 2), np.minimum(col[mask].astype(int), g['cols'] - 2)
		v = g['values']
		sw, se, nw, ne = (v[rr, cc].astype(float) for rr, cc in ((r, c), (r, c + 1), (r + 1, c), (r + 1, c + 1)))
		across, up = col[mask] - c, row[mask] - r
		lower, upper = sw + across * (se - sw), nw + across * (ne - nw)
		result[mask] = lower + up * (upper - lower)
	return result


def load_levels(day_folder):
	folder = Path(day_folder)
	metadata = json.loads((folder / 'manifest.json').read_text(encoding='utf-8'))
	rows = list(csv.DictReader((folder / 'water_levels.csv').open(encoding='utf-8')))
	gauges = []
	for s in metadata['water_level_stations']:
		own = [r for r in rows if r['station_id'] == s['id']]
		gauges.append(od.Gauge(s['id'], s['north_m'], s['east_m'], [float(r['t_s']) for r in own],
			[float(r['level_navd88_m']) for r in own], s['source'], s['source_sha256']))
	return od.DayLevels(gauges, metadata['time_coverage_s'])


class PreparedFootprint:
	"""Immutable pose samples, reused across times; no day-dependent values are cached."""
	def __init__(self, depth, outline, levels):
		self.north, self.east = footprint_points(outline, depth)
		self.bed = beds(depth, self.north, self.east)
		self.gauge_positions = [(g.north_m, g.east_m) for g in levels.gauges]

	def minimum(self, levels, time):
		if self.gauge_positions != [(g.north_m, g.east_m) for g in levels.gauges]:
			raise ValueError('gauge positions changed')
		held = min(max(time, levels.coverage[0]), levels.coverage[1])
		if len(levels.gauges) == 1:
			g = levels.gauges[0]
			return float(np.min(od.series_value(g.times, g.values, held) - self.bed))
		weights, total = np.zeros(self.bed.shape), np.zeros(self.bed.shape)
		at = np.full(self.bed.shape, np.nan)
		for g in levels.gauges:
			value = od.series_value(g.times, g.values, held)
			dn, de = self.north - g.north_m, self.east - g.east_m
			squared = dn * dn + de * de
			near = squared <= od.AT_STATION_M2
			at[near & np.isnan(at)] = value
			weight = 1. / np.where(near, 1., squared)
			weights += weight
			total += weight * value
		water = np.where(np.isfinite(at), at, total / weights)
		return float(np.min(water - self.bed))


def status(ukc, eligible, clearance=4., overlap=False):
	if not eligible:
		return 'excluded_face'
	if overlap or not clearance >= CONTACT_CLEARANCE_M:
		return 'contact_refusal'
	if not math.isfinite(ukc):
		return 'unknown_depth'
	if ukc < 0.:
		return 'aground_refusal'
	return 'warning' if ukc < .3 else 'startable'


def recompute(rows, depth, days):
	"""Recompute physical start depth for frozen, source-hashed poses; retain independent contact evidence."""
	levels, prepared, vessels = {}, {}, {}
	results = []
	for row in rows.to_dict('records'):
		hull, day = row['hull'], row['date']
		v = vessels.setdefault(hull, vessel(hull))
		draft, basis = maximum_draft(v)
		eligible = math.isfinite(row['north_m'])
		minimum = water = float('nan')
		if eligible:
			if day not in levels:
				levels[day] = load_levels(Path(days) / day)
			l = levels[day]
			key = (hull, row['north_m'], row['east_m'], row['heading_deg'])
			if key not in prepared:
				prepared[key] = PreparedFootprint(depth, world_outline(v, *key[1:]), l)
			minimum = prepared[key].minimum(l, row['time_s'])
			water = l.level(row['north_m'], row['east_m'], row['time_s'])
		ukc = minimum - draft
		row.update(min_depth_m=minimum, draft_m=draft, draft_basis=basis, under_keel_m=ukc,
			water_level_navd88_m=water, status=status(ukc, eligible, row['clearance_m'], row['overlap']))
		results.append(row)
	return pd.DataFrame(results)


def parity(s1, env, destination, per_stratum=7):
	"""Seven spread rows per assessed berth/hull (322 rows, all 23 berths and both hulls). Fail closed on drift."""
	pack.verify(env)
	rows = pd.read_csv(s1, dtype={'stop_id': str})
	rows = rows[np.isfinite(rows.north_m)]
	groups = rows.groupby(['stop_id', 'berth_id', 'hull'], sort=True)
	selected = pd.concat([g.iloc[np.linspace(0, len(g) - 1, per_stratum, dtype=int)] for _, g in groups])
	actual = recompute(selected, pack.load_depth(Path(env) / 'depth'), Path(env) / 'days')
	actual['expected_min_depth_m'] = selected.min_depth_m.to_numpy()
	actual['expected_under_keel_m'] = selected.under_keel_m.to_numpy()
	actual['expected_status'] = selected.status.to_numpy()
	actual['depth_error_m'] = abs(actual.min_depth_m - actual.expected_min_depth_m)
	actual['ukc_error_m'] = abs(actual.under_keel_m - actual.expected_under_keel_m)
	actual['matches'] = (actual.depth_error_m <= .001) & (actual.ukc_error_m <= .001) & (actual.status == actual.expected_status)
	Path(destination).mkdir(parents=True, exist_ok=True)
	actual.to_csv(Path(destination) / 'parity.csv', index=False, lineterminator='\n')
	if not actual.matches.all() or len(actual) < 300 or len(groups) != 46:
		raise ValueError(f'parity failed: {int(actual.matches.sum())}/{len(actual)} rows; {len(groups)} strata')
	return dict(rows=len(actual), strata=len(groups), max_depth_error_m=float(actual.depth_error_m.max()),
		max_ukc_error_m=float(actual.ukc_error_m.max()), statuses_match=True)
