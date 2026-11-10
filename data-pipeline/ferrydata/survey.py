"""Provenance-checked landing surveys and review overlays (C8).

Input coordinates are latitude, longitude degrees. Output positions are north,
east metres in the shared tangent plane; output angles are radians true.
"""
import argparse
import json
import math
from pathlib import Path

from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.polygon import orient

from ferrydata import geodesy, geojsonio, paths

SCHEMA = paths.REPO_ROOT / 'docs/contracts/landing-survey.schema.json'
POSE = ('centre', 'heading', 'length', 'width')
PROVENANCE = ('source', 'method', 'confidence', 'note')


def _read(file):
	def unique(pairs):
		result = {}
		for key, value in pairs:
			if key in result:
				raise ValueError(f'{file}: duplicate JSON key {key}')
			result[key] = value
		return result
	return json.loads(Path(file).read_text(encoding='utf-8'), object_pairs_hook=unique,
		parse_constant=lambda v: (_ for _ in ()).throw(ValueError(f'{file}: nonfinite number {v}')))


def _schema_errors(data, schema, root, location='$'):
	"""Evaluate C8's small JSON Schema vocabulary without a runtime dependency."""
	errors = []
	if '$ref' in schema:
		target = root
		for key in schema['$ref'][2:].split('/'):
			target = target[key.replace('~1', '/').replace('~0', '~')]
		errors += _schema_errors(data, target, root, location)
	if 'oneOf' in schema and sum(not _schema_errors(data, s, root, location) for s in schema['oneOf']) != 1:
		errors.append(f'{location}: expected exactly one allowed shape')
	if 'const' in schema and (type(data) != type(schema['const']) or data != schema['const']):
		errors.append(f'{location}: expected constant {schema["const"]}')
	if 'enum' in schema and not any(type(data) == type(v) and data == v for v in schema['enum']):
		errors.append(f'{location}: unexpected value {data!r}')
	checks = dict(object=isinstance(data, dict), array=isinstance(data, list), string=isinstance(data, str),
		number=type(data) in (int, float) and math.isfinite(data))
	if 'type' in schema and not checks.get(schema['type'], False):
		return errors + [f'{location}: expected {schema["type"]}']
	if isinstance(data, dict):
		for key in schema.get('required', []):
			if key not in data:
				errors.append(f'{location}.{key}: required')
		for key, value in data.items():
			properties = schema.get('properties', {})
			if key in properties:
				errors += _schema_errors(value, properties[key], root, f'{location}.{key}')
			elif schema.get('additionalProperties') is False:
				errors.append(f'{location}.{key}: unknown property')
			elif isinstance(schema.get('additionalProperties'), dict):
				errors += _schema_errors(value, schema['additionalProperties'], root, f'{location}.{key}')
	if isinstance(data, list):
		if not schema.get('minItems', 0) <= len(data) <= schema.get('maxItems', math.inf):
			errors.append(f'{location}: array length out of bounds')
		if 'items' in schema:
			for i, value in enumerate(data):
				errors += _schema_errors(value, schema['items'], root, f'{location}[{i}]')
	if isinstance(data, str) and len(data.strip()) < schema.get('minLength', 0):
		errors.append(f'{location}: empty string')
	return errors


def _measurement(item, name):
	unit = 'deg' if name in ('centre', 'shore_hinge', 'barge_hinge', 'heading', 'face_bearing', 'landed_heading') else 'm'
	if item['unit'] != unit:
		raise ValueError(f'{name}: unit must be {unit}')
	v = item['value']
	if name in ('centre', 'shore_hinge', 'barge_hinge'):
		from ferrydata import tiles
		harbor = tiles.extent()
		if not isinstance(v, list) or len(v) != 2 or not (harbor.south <= v[0] <= harbor.north and harbor.west <= v[1] <= harbor.east):
			raise ValueError(f'{name}: expected [latitude, longitude] inside harbor bounds')
	else:
		if type(v) not in (int, float):
			raise ValueError(f'{name}: expected a scalar')
		if name in ('heading', 'face_bearing', 'landed_heading') and not 0 <= v < (180 if name == 'heading' else 360):
			raise ValueError(f'{name}: outside [0, {180 if name == "heading" else 360})')
		if name in ('length', 'width', 'diameter') and v <= 0:
			raise ValueError(f'{name}: must be positive')


def _semantic(node):
	if isinstance(node, dict):
		for key, item in node.items():
			if isinstance(item, dict) and 'value' in item:
				_measurement(item, key)
			else:
				_semantic(item)
		if 'points' in node:
			for point in node['points']:
				_measurement(dict(value=point, unit='deg'), 'centre')
			if node['points'][0] != node['points'][-1]:
				raise ValueError('outline: expected a closed ring')
			polygon = Polygon([local_position(p)[::-1] for p in node['points']])
			if not polygon.is_valid or polygon.area <= 0:
				raise ValueError('outline: invalid shore polygon')
	elif isinstance(node, list):
		for item in node:
			_semantic(item)


def load(file=None):
	"""Return stops keyed by GTFS id; an absent file and an empty survey agree."""
	file = Path(file) if file is not None else paths.MANIFEST_DIR / 'landing-survey.json'
	if not file.exists():
		return {}
	payload = _read(file); schema = _read(SCHEMA)
	errors = _schema_errors(payload, schema, schema)
	if errors:
		raise ValueError('Invalid landing survey: ' + '; '.join(errors))
	_semantic(payload)
	for stop_id, stop in payload['stops'].items():
		seen = set()
		for b in stop['barges']:
			if b['berth_id'] in seen:
				raise ValueError(f'{stop_id}: duplicate berth_id {b["berth_id"]}')
			seen.add(b['berth_id'])
			if any(k in b for k in POSE) and not all(k in b for k in POSE):
				raise ValueError(f'{stop_id} {b["berth_id"]}: pose needs centre, heading, length and width')
			if 'length' in b and b['length']['value'] < b['width']['value']:
				raise ValueError(f'{stop_id}: length must be at least width (heading is the long axis)')
	return payload['stops']


def local_position(latlon):
	return [float(v) for v in geodesy.to_local(*latlon)]


def rectangle(barge):
	"""A surveyed long-axis bearing, converted once at the input boundary."""
	n, e = local_position(barge['centre']['value'])
	h = math.radians(barge['heading']['value'])
	length, width = barge['length']['value'], barge['width']['value']
	return Polygon([(e + a * math.sin(h) + b * math.cos(h), n + a * math.cos(h) - b * math.sin(h))
		for a, b in ((-length/2, -width/2), (length/2, -width/2), (length/2, width/2), (-length/2, width/2))])


def face(polygon, bearing, anchor, water, land, obstacles):
	"""Select ANY exterior edge by outward normal, including the barge ends.

	The survey selects geometry; it never upgrades independent chart verification.
	"""
	from ferrydata import landings
	h = math.radians(bearing['value']); toward = math.cos(h), math.sin(h)
	choices = []
	for edge in landings._straight_edges(orient(polygon, sign=-1)):
		a, b = edge.coords[0], edge.coords[-1]; length = math.dist(a, b)
		normal = (b[0] - a[0]) / length, (a[1] - b[1]) / length
		choices.append((sum(a*b for a, b in zip(normal, toward)), edge, normal))
	_, edge, normal = max(choices, key=lambda x: x[0])
	chart = landings.water_side_evidence(edge, normal, anchor, water, land, obstacles, polygon)
	proof = dict(chart, verified=True, method='surveyed', chart_verified=chart['verified'], chart_method=chart['method'], survey=bearing) if 'source' in bearing else chart
	return edge, normal, proof


def evidence(item):
	return {k: item[k] for k in PROVENANCE if k in item}


def local_blocks(barge, shore=()):
	"""Optional review/render layout in SI; absent measurements stay absent."""
	def block(item):
		result = {}; provenance = {}
		for key, v in item.items():
			if key in ('notes', 'kind'):
				result[key] = v
			elif key == 'outline':
				result['geometry_ne_m'] = dict(type='Polygon', coordinates=[[local_position(p) for p in v['points']]])
				provenance[key] = evidence(v)
			elif isinstance(v, dict) and 'value' in v:
				if key in ('centre', 'shore_hinge', 'barge_hinge'):
					result[key + '_ne_m'] = local_position(v['value'])
				elif key == 'heading':
					result['heading_rad'] = math.radians(v['value'])
				else:
					result[key + '_m'] = v['value']
				provenance[key] = evidence(v)
		if provenance:
			result['provenance'] = provenance
		return result
	result = {}
	for key in ('gangways', 'piles'):
		if key in barge:
			result[key] = [block(v) for v in barge[key]]
	if 'canopy' in barge:
		result['canopy'] = block(barge['canopy'])
		if 'heading' not in barge['canopy'] and 'heading' in barge:
			result['canopy']['heading_rad'] = math.radians(barge['heading']['value'])
			result['canopy']['provenance']['heading'] = evidence(barge['heading'])
	if shore:
		result['shore'] = [block(v) for v in shore]
	return result


def _pose(polygon, berths):
	from ferrydata import landings
	edges = landings._straight_edges(polygon.minimum_rotated_rectangle)
	longest = max(edges, key=lambda e: e.length)
	a, b = longest.coords[0], longest.coords[-1]
	return dict(centre_ne_m=[polygon.centroid.y, polygon.centroid.x], heading_rad=math.atan2(b[0]-a[0], b[1]-a[1]) % math.pi,
		length_m=longest.length, width_m=min(e.length for e in edges),
		face_bearings_rad=[math.radians(landings.heading_deg(b['outward_normal_ne'])) for b in berths])


def _selectable(measurement, polygon, existing, used_orders, anchor, water, land, obstacles):
	from ferrydata import landings
	if 'berths' not in measurement:
		return existing
	result = []; seen_edges = set()
	for b in measurement['berths']:
		selected, normal, proof = face(polygon, b['face_bearing'], anchor, water, land, obstacles)
		key = tuple(selected.coords)
		if key in seen_edges:
			raise ValueError(f'{measurement["berth_id"]}: multiple survey bearings select the same face')
		seen_edges.add(key)
		if b.get('is_ferry_berth', True) is False:
			continue
		i = len(result)
		order = existing[i]['order'] if i < len(existing) else max(used_orders, default=-1) + 1
		used_orders.add(order)
		previous = existing[i] if i < len(existing) else {}
		record = dict(previous, id=measurement['berth_id'], order=order,
			name=previous.get('name', 'Main berth'), face_geometry=geojsonio.make_feature(landings.to_wgs84(selected), {})['geometry'],
			outward_normal_ne=list(normal), survey_evidence=dict(face_bearing=b['face_bearing']))
		if 'name' in b:
			record['alias'] = b['name']
		if 'landed_heading' in b:
			record['landed_heading'] = math.radians(b['landed_heading']['value'])
			record['survey_evidence']['landed_heading'] = b['landed_heading']
		result.append(record)
	return result


def apply_stop(feature, context, stop, shore, water, land):
	"""Apply a survey to one completed baseline context, never to shared charts."""
	from ferrydata import landings
	p = feature['properties']; primary_id = context['outline_id']
	by_id = {primary_id: dict(polygon=context['barge'], face=context['face'], normal=p['outward_normal_ne'],
		proof=p['water_side_evidence'], anchor=context['anchor'], obstacles=context['structures'])}
	by_id.update({f"enc:{b['props']['id']}": b for b in context['berths']})
	unknown = {b['berth_id'] for b in stop['barges']} - by_id.keys()
	if unknown:
		raise ValueError(f'{context["stop_id"]}: unknown or non-candidate surveyed berth {sorted(unknown)}')
	p['survey_previous_outlines'] = [{'berth_id': b['berth_id'], 'geometry': geojsonio.make_feature(landings.to_wgs84(by_id[b['berth_id']]['polygon']), {})['geometry']} for b in stop['barges']]
	existing = p['berths']; used_orders = {b['order'] for b in existing}
	changes = []; metadata = {}; selections = list(existing)
	# Replace all posed outlines in a stop-local chart copy before any side probes.
	poses = {b['berth_id']: rectangle(b) for b in stop['barges'] if 'centre' in b}
	local_shore = [(poses.get(f"enc:{props['id']}", g), props) for g, props in shore]
	for measurement in stop['barges']:
		identifier = measurement['berth_id']; target = by_id[identifier]
		old_selections = [b for b in existing if b['id'] == identifier]
		before = _pose(target['polygon'], old_selections)
		polygon = poses.get(identifier, target['polygon'])
		obstacles = landings.surface_structures(local_shore, identifier.removeprefix('enc:'))
		selected = _selectable(measurement, polygon, old_selections, used_orders, target['anchor'], water, land, obstacles)
		if 'berths' not in measurement and identifier in poses:
			# Keep the baseline side direction when only the pose was surveyed.
			selected = []
			for old in old_selections:
				bearing = dict(value=landings.heading_deg(old['outward_normal_ne']), unit='deg')
				edge, normal, proof = face(polygon, bearing, target['anchor'], water, land, obstacles)
				selected.append(dict(old, face_geometry=geojsonio.make_feature(landings.to_wgs84(edge), {})['geometry'], outward_normal_ne=list(normal)))
		selections = [b for b in selections if b['id'] != identifier] + selected
		after = _pose(polygon, selected)
		if not target['polygon'].equals_exact(polygon, 1e-8) or [b['face_geometry'] for b in old_selections] != [b['face_geometry'] for b in selected]:
			changes.append(dict(berth_id=identifier, before=before, after=after))
		geometry_override = identifier in poses or 'berths' in measurement
		if not geometry_override:
			edge, normal, proof = target['face'], target['normal'], target['proof']
		elif selected:
			bearing = next((b['face_bearing'] for b in measurement.get('berths', []) if b.get('is_ferry_berth', True)), None)
			if bearing is not None:
				edge, normal, proof = face(polygon, bearing, target['anchor'], water, land, obstacles)
			else:
				edge, normal, proof = face(polygon, dict(value=landings.heading_deg(selected[0]['outward_normal_ne'])), target['anchor'], water, land, obstacles)
		else:
			edge, normal, proof = target['face'], target['normal'], target['proof']
			if identifier in poses:
				edge, normal = landings.berthing_face(polygon, normal)
				proof = landings.water_side_evidence(edge, normal, target['anchor'], water, land, obstacles, polygon)
		target.update(polygon=polygon, face=edge, normal=normal, proof=proof, obstacles=obstacles)
		local = local_blocks(measurement, stop.get('shore', ()))
		if 'canopy' in local and 'heading_rad' not in local['canopy']:
			local['canopy']['heading_rad'] = after['heading_rad']
			local['canopy']['heading_source'] = 'existing outline long axis'
		metadata[identifier] = dict(survey=measurement, survey_local=local,
			survey_berths=[{k: b[k] for k in ('id', 'order', 'alias', 'landed_heading', 'survey_evidence') if k in b} for b in selected],
			survey_no_berths=not selected, pose_surveyed=identifier in poses)
		if identifier == primary_id:
			if not geometry_override:
				p['survey'] = measurement
				continue
			context.update(barge=polygon, face=edge, structures=obstacles)
			# A disabled face leaves the existing exposed run intact unless the pose moved.
			if identifier in poses or selected:
				context['contact'] = landings.water_facing_outline(polygon, edge, normal, land, obstacles)
				contact_geo = geojsonio.make_feature(landings.to_wgs84(context['contact']), {})
				context['contact_lonlat'] = contact_geo['geometry']['coordinates']
				context['records'] = landings.contact_segment_evidence(context['contact'], edge, proof, target['anchor'], water, land, obstacles, polygon)
				for record in context['records']:
					record['verification'] = landings.segment_verification(record, context['note'])
			if not selected:
				for record in context['records']:
					record['on_primary_face'] = False
			face_geo = geojsonio.make_feature(landings.to_wgs84(edge), {})
			mid = edge.interpolate(.5, normalized=True); lat, lon = geodesy.from_local(mid.y, mid.x)
			p.update(face_geometry=face_geo['geometry'], face_local_coordinates=face_geo['local_coordinates'],
				outward_normal_ne=list(normal), face_heading_deg_true=landings.heading_deg(normal), water_side_evidence=proof,
				water_side=proof['verified'], water_side_verified_by_pipeline=proof.get('chart_verified', proof['verified']),
				face_midpoint=dict(lat=float(lat), lon=float(lon), north_m=mid.y, east_m=mid.x), gtfs_to_face_m=edge.distance(context['point']),
				contact_geometry=geojsonio.make_feature(landings.to_wgs84(context['contact']), {})['geometry'],
				contact_local_coordinates=geojsonio.make_feature(landings.to_wgs84(context['contact']), {})['local_coordinates'],
				contact_segment_evidence=context['records'], survey=measurement)
			if identifier in poses:
				confidence = min((measurement[k]['confidence'] for k in POSE), key=lambda c: {'low': 0, 'medium': 1, 'high': 2}[c])
				p.update(source='surveyed', outline_source='surveyed', confidence=confidence, contact_confidence=confidence,
					contact_geometry_estimated=False, derivation=None,
					contact_note='Surveyed rectangle; independent chart water-side and solid-side checks retained. Complete outline in landing_structures.geojson.')
				context.update(source='surveyed', confidence=confidence, contact_confidence=confidence)
			if 'berths' in measurement:
				p['side_chosen_by'] = 'surveyed-face-bearing'
			new_feature = geojsonio.make_feature(landings.to_wgs84(polygon), p)
			feature.clear(); feature.update(new_feature); p = feature['properties']
			context['ring_lonlat'] = feature['geometry']['coordinates'][0]
	if poses:
		# Moving a neighbouring pontoon can invalidate a previously clear corridor.
		# Recheck chart evidence at this stop without choosing any new faces.
		for identifier, target in by_id.items():
			obstacles = landings.surface_structures(local_shore, identifier.removeprefix('enc:'))
			polygon = target['polygon']
			chart_outline = None if identifier == primary_id and context['derived'] and identifier not in poses else polygon
			chart = landings.water_side_evidence(target['face'], target['normal'], target['anchor'], water, land, obstacles, chart_outline)
			old = target['proof']
			proof = dict(old, chart_verified=chart['verified'], chart_method=chart['method']) if old['method'] in ('surveyed', 'crew-reported') else chart
			target.update(proof=proof, obstacles=obstacles)
			if identifier == primary_id:
				context['structures'] = obstacles
				context['records'] = landings.contact_segment_evidence(context['contact'], context['face'], proof, context['anchor'], water, land, obstacles,
					chart_outline, bool(context['note'] and context['note'].get('water_side_crew_reported')))
				for record in context['records']:
					if not any(b['id'] == primary_id for b in selections):
						record['on_primary_face'] = False
					record['verification'] = landings.segment_verification(record, context['note'])
				p.update(water_side_evidence=proof, water_side=proof['verified'],
					water_side_verified_by_pipeline=proof.get('chart_verified', proof['verified']), contact_segment_evidence=context['records'])
	selections.sort(key=lambda b: b['order'])
	p['berths'] = selections
	p['survey_no_berths'] = not selections
	for meta in metadata.values():
		meta['survey_no_berths'] = not selections
	context.update(selectable_berths=selections, survey_metadata=metadata, survey_shore=local_shore)
	return dict(stop_id=context['stop_id'], barges=changes) if changes else None


def annotate_structures(polylines, context):
	"""Attach only this stop's surveyed metadata and reclassify its selected faces."""
	from ferrydata import landings
	for polyline in polylines:
		identifier = polyline['structure_id']
		meta = context.get('survey_metadata', {}).get(identifier)
		if meta is None:
			continue
		polyline.update({k: v for k, v in meta.items() if k != 'pose_surveyed'})
		polygon = context['barge'] if identifier == context['outline_id'] else next(b['polygon'] for b in context['berths'] if f"enc:{b['props']['id']}" == identifier)
		kinds = landings.outline_side_kinds(polygon)
		for segment in polyline['segments']:
			i = segment['index']; a, b = polyline['coords'][i:i+2]
			segment['kind'] = kinds[frozenset((a, b))]
			segment.pop('primary_face', None)
			if meta['pose_surveyed']:
				segment['source'] = 'surveyed'
				segment['solid_side_evidence'].update(method='surveyed-outline', outline_survey={k:evidence(meta['survey'][k]) for k in POSE})
				segment['confidence'] = min((meta['survey'][k]['confidence'] for k in POSE), key=lambda c: {'low': 0, 'medium': 1, 'high': 2}[c])
				segment.pop('geometry_estimated', None)
				polyline['source'] = 'surveyed'


def write_changes(out, changes, stop_ids=()):
	# Harbor tiles merge into one root report outside hashed tile products.
	if out.parent.name == 'tiles':
		out = out.parent.parent
		previous = _read(out / 'landing-survey-changes.json')['stops'] if (out / 'landing-survey-changes.json').exists() else []
		changes = [c for c in previous if c['stop_id'] not in stop_ids] + changes
	(out / 'landing-survey-changes.json').write_text(json.dumps(dict(schema_version=1,
		coordinate_frame='north,east metres; radians true', stops=sorted(changes, key=lambda c: c['stop_id'])),
		ensure_ascii=False, allow_nan=False, indent='\t') + '\n', encoding='utf-8')


def overlay(stop_id, out, processed=None, file=None):
	"""Plot measured plan geometry; a marker with unknown diameter stays a point."""
	from ferrydata import landings
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	from matplotlib.patches import Circle
	stops = load(file)
	if stop_id not in stops:
		raise ValueError(f'{stop_id}: no survey to overlay')
	processed = Path(processed) if processed is not None else paths.PROCESSED_ROOT / 'harbor'
	if (processed / 'index.json').is_file():
		index = _read(processed / 'index.json')
		owners = [t for t in index['tiles'] if stop_id in t['stop_ids']]
		if len(owners) != 1:
			raise ValueError(f'{stop_id}: expected exactly one owning tile')
		processed = processed / 'tiles' / owners[0]['id']
	features = _read(processed / 'landing_faces.geojson')['features']
	landing = next((f for f in features if f['properties']['stop_id'] == stop_id), None)
	if landing is None:
		raise ValueError(f'{stop_id}: missing built landing')
	previous = {p['berth_id']: shape(p['geometry']) for p in landing['properties'].get('survey_previous_outlines', [])}
	previous.setdefault(landing['properties']['outline_id'], shape(landing['geometry']))
	for f in _read(processed / 'shoreline_constructions.geojson')['features']:
		previous.setdefault('enc:' + f['properties']['id'], shape(f['geometry']))
	fig, ax = plt.subplots(figsize=(9, 8), layout='constrained')
	focus = []
	def draw(geometry, **style):
		for g in getattr(geometry, 'geoms', [geometry]):
			line = g.exterior if g.geom_type == 'Polygon' else g
			if hasattr(line, 'xy'):
				ax.plot(*line.xy, **style)
	for f in _read(processed / 'land_areas.geojson')['features']:
		draw(landings.to_local(shape(f['geometry'])), color='0.55', linewidth=1)
	for i, b in enumerate(stops[stop_id]['barges']):
		if b['berth_id'] not in previous:
			raise ValueError(f'{stop_id}: missing old outline {b["berth_id"]}')
		old = landings.to_local(previous[b['berth_id']])
		new = rectangle(b) if 'centre' in b else old
		focus.extend([old, new])
		draw(old, color='tab:orange', linestyle='--', label='Old outline' if i == 0 else None)
		draw(new, color='tab:blue', linewidth=2, label='Surveyed outline' if i == 0 else None)
		for j, g in enumerate(b.get('gangways', [])):
			n1, e1 = local_position(g['shore_hinge']['value']); n2, e2 = local_position(g['barge_hinge']['value'])
			line = LineString([(e1, n1), (e2, n2)]); focus.append(line)
			draw(line, color='tab:green', linewidth=2, label='Gangway' if i == 0 and j == 0 else None)
		for j, pile in enumerate(b.get('piles', [])):
			n, e = local_position(pile['centre']['value']); focus.append(Point(e, n))
			ax.scatter([e], [n], marker='+', color='tab:red', label='Pile centre' if i == 0 and j == 0 else None)
			if 'diameter' in pile:
				ax.add_patch(Circle((e, n), pile['diameter']['value']/2, fill=False, color='tab:red'))
	bounds = [g.bounds for g in focus]
	x0 = min(b[0] for b in bounds)-10; x1 = max(b[2] for b in bounds)+10
	y0 = min(b[1] for b in bounds)-10; y1 = max(b[3] for b in bounds)+10
	ax.set_xlim(x0, x1); ax.set_ylim(y0, y1)
	scale = 10 if x1-x0 < 100 else 25
	x, y = x0+3, y0+3
	ax.plot([x, x+scale], [y, y], color='black', linewidth=3)
	ax.text(x+scale/2, y+1, f'{scale} m', ha='center')
	ax.set_aspect('equal'); ax.grid(alpha=.2); ax.set_xlabel('East (m)'); ax.set_ylabel('North (m)')
	ax.set_title(f'Landing {stop_id} — survey review'); ax.legend(loc='upper left')
	out = Path(out); out.parent.mkdir(parents=True, exist_ok=True)
	fig.savefig(out, dpi=150); plt.close(fig)
	return out


def main(argv=None):
	parser = argparse.ArgumentParser(prog='python -m ferrydata.survey', description=__doc__)
	commands = parser.add_subparsers(dest='command', required=True)
	p = commands.add_parser('overlay', help='Chart shoreline, old/surveyed outlines, gangways and piles')
	p.add_argument('--stop', required=True); p.add_argument('--out', required=True, type=Path)
	p.add_argument('--processed', type=Path, help='Built slice, tile or harbor root (default: harbor)')
	p.add_argument('--survey', type=Path, help='Survey JSON (default: tracked C8 manifest)')
	args = parser.parse_args(argv)
	try:
		overlay(args.stop, args.out, args.processed, args.survey)
	except (ValueError, OSError, KeyError) as error:
		import sys
		print(f'overlay failed: {error}', file=sys.stderr)
		return 1
	return 0


if __name__ == '__main__':
	raise SystemExit(main())
