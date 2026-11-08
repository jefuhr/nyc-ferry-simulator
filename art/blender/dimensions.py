"""Visual layout in Unity metres: x starboard, y up, z forward from design waterline.

Physics particulars are read only. The sole legacy proportion is the Eye fallback,
which belongs to the interior worker and remains until the interior provides eye_seated plus floor_origin (or absolute eye_position).
"""
import csv
import json
import math
from pathlib import Path

DATA = Path(__file__).resolve().parent / 'data'
SCALARS = ('length', 'beam', 'draft', 'freeboard', 'upper_deck_height', 'wheelhouse_roof_height',
	'bow_height', 'underwing_height', 'separation', 'demihull_beam', 'wheelhouse_forward',
	'wheelhouse_aft', 'wheelhouse_half_beam', 'wheelhouse_roof_inset', 'wheelhouse_sill_height',
	'deck_thickness', 'sponson_depth', 'fender_radius', 'rail_height', 'rail_spacing',
	'window_bottom', 'window_top', 'window_bays', 'exhaust_height', 'exhaust_width',
	'exhaust_base_length', 'exhaust_top_length', 'exhaust_rake', 'bow_gate_half_width', 'sponson_projection', 'fender_outboard_offset', 'stair_clear_width')
GROUPS = ('main_outline', 'upper_outline', 'cabin_outline', 'sheer_profile', 'hull_stations',
	'hull_section', 'sponson_path', 'stair_start', 'stair_end', 'light_mounts', 'exhaust_stacks')
ALIASES = dict(length='length_overall', freeboard='main_deck_height', draft='baseline_height',
	separation='hull_separation', demihull_beam='demihull_width', wheelhouse_forward='wheelhouse_fore_z',
	wheelhouse_aft='wheelhouse_aft_z', wheelhouse_half_beam='wheelhouse_width')
LIGHTS = ('Masthead', 'PortSidelight', 'StarboardSidelight', 'Sternlight', 'ForwardAnchor', 'AfterAnchor', 'AfterMasthead')


def measured(item, unit='m'):
	if not isinstance(item, dict) or not item.get('source') or item.get('unit') != unit:
		raise ValueError('Layout value requires source and correct unit: ' + str(item))
	if item.get('method') not in ('drawing-scale', 'known-dimension', 'photo-estimate', 'video-estimate', 'crew-confirmed', 'published', 'simulator-estimate'):
		raise ValueError('Unknown measurement method')
	if item.get('confidence') not in ('high', 'medium', 'low'):
		raise ValueError('Unknown confidence')
	value = item['value']
	for n in value if isinstance(value, list) else [value]:
		if isinstance(n, bool) or not isinstance(n, (int, float)) or not math.isfinite(n):
			raise ValueError('Layout numbers must be finite')
	return value


def load_layout(name):
	if name not in ('t-class', 'k-class'):
		raise ValueError('Unsupported vessel layout: ' + name)
	return json.loads((DATA / (name + '-hull.json')).read_text())


def legacy_eye(v):
	p, w = v['particulars'], v['windage']
	length, beam, depth, draft = (p[k]['value'] for k in ('lengthOverall', 'beam', 'depth', 'draft'))
	freeboard = depth - draft
	height = min(5., max(2., w['frontalArea']['value'] / beam - max(.5, freeboard)))
	cabin = min(.85 * length, max(.3 * length, (w['lateralArea']['value'] - length * max(.5, freeboard)) / height))
	return [0, freeboard + height + 1.7, .5 * cabin - 2.]


def dimensions(v, layout=None, wheelhouse=None):
	layout = load_layout(v['id']) if layout is None else layout
	if layout['schema_version'] != 1 or layout['kind'] != 'hull' or layout['subject'] != v['id']:
		raise ValueError('Layout identity or schema mismatch')
	values = layout['values']
	d = {key: measured(values[ALIASES.get(key, key)], 'count' if key == 'window_bays' else 'm') for key in SCALARS}
	d['draft'] = -d['draft']
	d['wheelhouse_half_beam'] /= 2
	d['visual_beam'] = d['beam'] + 2*d['sponson_projection']
	d['boarding_threshold_size'] = measured(values['boarding_threshold_size'])
	for group in GROUPS:
		d[group] = {key: measured(value, 'ratio' if group == 'hull_section' else 'm') for key, value in sorted(values[group].items())}
		if any(not isinstance(value, list) or len(value) != (3 if group in ('hull_stations', 'sponson_path', 'stair_start', 'stair_end', 'light_mounts', 'exhaust_stacks') else 2) for value in d[group].values()):
			raise ValueError('Wrong point size: ' + group)
	for group in ('main_outline', 'upper_outline', 'cabin_outline'):
		points = list(d[group].values())
		if len(points) < 2 or any(a[1] >= b[1] for a, b in zip(points, points[1:])) or any(p[0] <= 0 for p in points):
			raise ValueError('Outline must have positive half-widths in increasing z: ' + group)
	for group in ('hull_stations', 'sheer_profile'):
		points = list(d[group].values())
		if len(points) < 2 or any(a[0] >= b[0] for a, b in zip(points, points[1:])):
			raise ValueError('Stations must be in increasing z: ' + group)
	if not (0 < d['freeboard'] < d['upper_deck_height'] < d['wheelhouse_roof_height']):
		raise ValueError('Deck heights must increase above waterline')
	if not (0 < d['draft'] and 0 < d['beam'] < d['length'] and d['wheelhouse_aft'] < d['wheelhouse_forward']):
		raise ValueError('Invalid vessel envelope')
	if set(d['light_mounts']) != set(LIGHTS):
		raise ValueError('Missing navigation-light mount')
	d['materials'] = layout['materials']
	if len(d['materials']) != 7 or len({m['name'] for m in d['materials']}) != 7:
		raise ValueError('Exterior requires seven distinct neutral surfaces')
	for material in d['materials']:
		if len(material['colour']) != 3 or any(not math.isfinite(n) or not 0 <= n <= 1 for n in material['colour'] + [material['metal'], material['smoothness']]):
			raise ValueError('Invalid material properties')
	if wheelhouse is None:
		path = DATA / (v['id'] + '-wheelhouse.json')
		wheelhouse = json.loads(path.read_text()) if path.exists() else {'values': {}}
	eye = wheelhouse['values'].get('eye_position')
	if eye:
		d['eye_position'] = measured(eye)
	elif 'eye_seated' in wheelhouse['values']:
		floor = measured(wheelhouse['values']['floor_origin'])
		seated = measured(wheelhouse['values']['eye_seated'])
		footprint = measured(wheelhouse['values']['footprint'])
		if len(floor) != 3 or len(seated) != 3 or len(footprint) != 3 or not math.isclose(floor[1], d['upper_deck_height'], abs_tol=.001):
			raise ValueError('Interior floor must match the hull upper-deck datum')
		# Same contract as interior.py: y above the floor, z offset from the wheelhouse front (floor centre plus half its length).
		d['eye_position'] = [seated[0], floor[1]+seated[1], floor[2]+footprint[2]/2+seated[2]]
	else:
		d['eye_position'] = legacy_eye(v)
	if not isinstance(d['eye_position'], list) or len(d['eye_position']) != 3:
		raise ValueError('eye_position must be a Unity-frame vector')
	d['eye_height'], d['eye_forward'] = d['eye_position'][1:]
	return d


def mounts(d):
	return dict(Eye=list(d['eye_position']), Bow=[0, d['bow_height'], .5 * d['length']],
		**{name: list(value) for name, value in d['light_mounts'].items()})


def audited_classes(root):
	with (root / 'data-pipeline/calibration/identity-audit.csv').open() as file:
		return {int(r['mmsi']): 't-class' if r['class'] == 'T' else 'k-class'
			for r in csv.DictReader(file) if r['class'] == 'T' or r['class'].startswith('K-')}
