"""Reproducible, deliberately low-confidence GPS-to-barge fits (C8).

Points are vessel GPS fixes, never face positions. Geometry uses east/north metres;
C8 alone uses latitude/longitude and degrees. No landed heading is inferred.
"""
import argparse
import html
import json
import math
from pathlib import Path

import numpy as np
from shapely.geometry import LineString, Point, Polygon, shape
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from ferrydata import approach, geodesy, geojsonio, landings, paths, survey

SOURCE = 'crew-reported: project crew reviewer, slip positions 2026-11'
NOISE_M = 5.0


def dump(value):
	return json.dumps(value, indent='\t', ensure_ascii=False, allow_nan=False) + '\n'


def pose(polygon):
	p = survey._pose(polygon, [])
	return dict(centre_en=[p['centre_ne_m'][1], p['centre_ne_m'][0]],
		heading_deg=math.degrees(p['heading_rad']), length_m=p['length_m'], width_m=p['width_m'])


def edges(polygon):
	result = []
	for i, edge in enumerate(landings._straight_edges(orient(polygon, sign=-1))):
		a, b = np.array(edge.coords[0]), np.array(edge.coords[-1])
		tangent = (b-a) / np.linalg.norm(b-a)
		normal = np.array([-tangent[1], tangent[0]])
		result.append(dict(index=i, edge=edge, normal=normal,
			bearing=math.degrees(math.atan2(normal[0], normal[1])) % 360))
	return result


def observations(polygon, slips, normals=None):
	"""Signed off-face and un-clamped along-face distances, from clockwise start."""
	result = []
	for i, slip in enumerate(slips):
		point = Point(slip['point_en'])
		face = (max(edges(polygon), key=lambda f: float(np.dot(f['normal'], normals[i]))) if normals is not None else
			min(edges(polygon), key=lambda f: (f['edge'].distance(point), f['index'])))
		a, b = np.array(face['edge'].coords[0]), np.array(face['edge'].coords[-1])
		delta = np.array(slip['point_en']) - a
		result.append(dict(name=slip['name'], point_en=list(slip['point_en']), face_index=face['index'],
			face_bearing_deg=face['bearing'], face_start_en=list(a), face_end_en=list(b),
			along_m=float(np.dot(delta, (b-a)/np.linalg.norm(b-a))),
			off_m=float(np.dot(delta, face['normal'])), edge_distance_m=face['edge'].distance(point)))
	return result


def group_faces(rows):
	groups = {}
	for row in rows:
		groups.setdefault(row['face_index'], []).append(row)
	return [dict(name=' / '.join(r['name'] for r in sorted(group, key=lambda r: (r['along_m'], r['name']))),
		face_bearing_deg=group[0]['face_bearing_deg']) for _, group in sorted(groups.items())]


def rectangle(centre, heading, length, width):
	h = math.radians(heading); a = np.array([math.sin(h), math.cos(h)]); b = np.array([math.cos(h), -math.sin(h)])
	return Polygon([np.array(centre)+x*a+y*b for x,y in [(-length/2,-width/2),(length/2,-width/2),(length/2,width/2),(-length/2,width/2)]])


def fit(slips, baseline, anchor_en, offset, length=landings.BARGE_LENGTH_M, width=landings.BARGE_WIDTH_M):
	"""Compare same-side and opposite-end hypotheses, retaining ambiguity.

	Two GPS fixes cannot identify the layout by themselves. Retain feasible models
	within a 5 m per-fix residual allowance; select the least displacement from the
	baseline (centre shift plus equivalent corner rotation). Never label this high.
	Dimensions never expand; an incompatible set of slips is left unresolved.
	"""
	points = np.array([s['point_en'] for s in slips], dtype=float)
	if len(points) == 0 or not np.isfinite(points).all() or offset <= 0:
		raise ValueError('finite slips and positive calibrated distance required')
	old = pose(baseline); mean = points.mean(axis=0)
	if len(points) == 1:
		h = old['heading_deg']
		axes = [h]; determined = False
	else:
		_, _, directions = np.linalg.svd(points-mean, full_matrices=False)
		h = math.degrees(math.atan2(directions[0,0], directions[0,1])) % 180
		axes = [h, (h+90)%180]; determined = True
	candidates = []
	for heading in axes:
		h = math.radians(heading); axis = np.array([math.sin(h),math.cos(h)]); side = np.array([math.cos(h),-math.sin(h)])
		x = points@axis; y = points@side
		# Same-face observations lie offset + half-width seaward of the centre.
		outward = side if np.dot(mean-np.array(anchor_en),side) >= 0 else -side
		l = length
		centre = mean-outward*(offset+width/2)
		poly = rectangle(centre,heading,l,width)
		residual = max(float(np.max(np.abs(y-y.mean()))), max(0,float(np.ptp(x))-l)/2)
		candidates.append(dict(layout='same face',centre_en=centre.tolist(),heading_deg=heading,length_m=l,width_m=width,
			residual_m=residual,polygon=poly))
		if len(points)>1:
			low = x <= (float(x.min())+float(x.max()))/2
			if low.all() or (~low).all():
				continue
			lo, hi = float(x[low].mean()),float(x[~low].mean())
			l = length
			w = width
			centre = axis*((lo+hi)/2)+side*float(y.mean())
			expected = np.where(low,(lo+hi)/2-l/2-offset,(lo+hi)/2+l/2+offset)
			residual = max(float(np.max(np.abs(x-expected))), max(0,float(np.ptp(y))-w)/2)
			candidates.append(dict(layout='opposite ends',centre_en=centre.tolist(),heading_deg=heading,length_m=l,width_m=w,
				residual_m=residual,polygon=rectangle(centre,heading,l,w)))
	feasible = [c for c in candidates if c['residual_m'] <= NOISE_M+1e-8 and c['length_m']>=c['width_m']]
	if not feasible:
		raise ValueError('slips cannot fit published barge dimensions within 5 m per fix')
	for c in candidates:
		turn = abs((c['heading_deg']-old['heading_deg']+90)%180-90)
		c['baseline_distance_m'] = float(np.linalg.norm(np.array(c['centre_en'])-old['centre_en']))+length/2*math.sin(math.radians(turn))
	chosen = min(feasible,key=lambda c:(c['baseline_distance_m'],c['residual_m'],c['heading_deg'],c['layout']))
	h = math.radians(chosen['heading_deg']); axis = np.array([math.sin(h), math.cos(h)])
	if chosen['layout'] == 'same face':
		normal = mean-np.array(chosen['centre_en']); normal /= np.linalg.norm(normal)
		normals = [normal]*len(slips)
	else:
		normals = [axis if np.dot(p-np.array(chosen['centre_en']),axis)>0 else -axis for p in points]
	rows = observations(chosen['polygon'],slips,normals)
	result = {k:v for k,v in chosen.items() if k!='polygon'}
	result.update(slips=rows,berths=group_faces(rows),heading_determined=determined,confidence='low',
		reason=('GPS pair direction; layout chosen nearest baseline among feasible hypotheses; landed heading unknown.' if determined else
			'Single slip cannot determine heading; shoreline-derived baseline heading retained.'),
		alternatives=[{k:v for k,v in c.items() if k!='polygon'} | dict(feasible=c in feasible) for c in candidates])
	return result


def inputs(harbor=None):
	"""Read pinned chart products; recover original outlines after a survey rebuild.

	No fitted outlines feed calibration. C8 stores the original outline explicitly;
	charted candidates always come from shoreline_constructions, never survey output.
	"""
	harbor = Path(harbor or paths.PROCESSED_ROOT/'harbor'); result = {}
	for file in sorted((harbor/'tiles').glob('*/landing_faces.geojson')):
		faces = geojsonio.read_collection(file)['features']
		if not faces:
			continue
		def read(name):
			return [(landings.to_local(shape(f['geometry'])),f['properties']) for f in geojsonio.read_collection(file.parent/(name+'.geojson'))['features']]
		shore = read('shoreline_constructions'); lands = read('land_areas')
		water,land = landings.chart_masks(file.parent)
		gtfs = {p['stop_id']:(g,p) for g,p in read('landings')}
		for feature in faces:
			p = feature['properties']; sid=p['stop_id']; ident=p['outline_id']
			previous = {v['berth_id']:v['geometry'] for v in p.get('survey_previous_outlines',[])}
			baseline = landings.to_local(shape(previous.get(ident,feature['geometry'])))
			candidate_ids = {v['enc_id'] for v in p['candidate_evidence']}
			charted = {f"enc:{props['id']}":g for g,props in shore if props['id'] in candidate_ids and g.geom_type=='Polygon'}
			all_pontoons = {f"enc:{props['id']}":g for g,props in shore if g.geom_type=='Polygon' and (props['feature_class'] in ('PONTON','FLODOC') or props.get('water_level_effect_code')==7)}
			point, gp = gtfs[sid]
			boundaries = [g.boundary for g,_ in lands if g.geom_type in ('Polygon','MultiPolygon')]
			nearest = min(boundaries,key=lambda g:g.distance(point))
			anchor,tangent = approach.face_geometry((point.x,point.y),[nearest])
			vector=np.array([point.x-anchor[0],point.y-anchor[1]])
			if np.linalg.norm(vector)<.01:
				vector=np.array(baseline.centroid.coords[0])-anchor
			outward=vector/np.linalg.norm(vector)
			derived=landings.derive_barge(anchor,tangent,outward[::-1],point)
			result[sid]=dict(name=p['stop_name'],id=ident,baseline=baseline,charted=charted,all_pontoons=all_pontoons,
				anchor=p['bulkhead_anchor_en_m'],shore=shore,lands=lands,water=water,land=land,
				validation_baseline=derived,validation_anchor=anchor,properties=p)
	return result


def local_slips(stop):
	return [dict(name=s['name'],point_en=survey.local_position(s['position'])[::-1]) for s in stop['slips']]


def associate(context, slips, all_pontoons=False):
	polygons=context['all_pontoons'] if all_pontoons else context['charted']
	result=[]
	for slip in slips:
		point=Point(slip['point_en'])
		if not polygons:
			continue
		identifier,polygon=min(polygons.items(),key=lambda p:(p[1].distance(point),p[0]))
		row=observations(polygon,[slip])[0]
		row.update(berth_id=identifier,accepted=identifier in context['charted'] and row['off_m']>0 and row['edge_distance_m']<=20,
			pontoon_distances_m={key:polygon.distance(point) for key,polygon in sorted(polygons.items()) if polygon.distance(point)<200})
		result.append(row)
	return result


def calibrate(contexts, payload):
	rows=[]
	for stop in payload['stops']:
		context=contexts[stop['stop_id']]
		if not context['charted']:
			continue
		for row in associate(context,local_slips(stop),all_pontoons=True):
			# Pier 11 B/E remain unresolved under the coordinator's explicit decision.
			if stop['stop_id']=='87' and row['name'] in ('Slip B1','Slip B2','Slip E1'):
				row['accepted']=False
			rows.append(dict(stop_id=stop['stop_id'],**row))
	values=[r['edge_distance_m'] for r in rows if r['accepted']]
	return dict(mean_m=float(np.mean(values)),median_m=float(np.median(values)),std_m=float(np.std(values)),
		min_m=min(values),max_m=max(values),quartiles_m=np.quantile(values,[.25,.75]).tolist(),
		sample_count=len(values),samples=rows,
		note='Mean nearest-edge distance is a proxy, not a measured antenna-to-bow length; headings and receiver offsets are unknown. 20 m association ceiling excludes remote/missing polygons; Pier 11 B/E are unresolved.')


def measurement(value,unit='m',confidence='low',source=SOURCE,method='simulator-estimate',note=None):
	return dict(value=value,unit=unit,source=source,method=method,confidence=confidence,**(dict(note=note) if note else {}))


def generate(harbor=None,payload=None,holds=None):
	contexts=inputs(harbor)
	payload=payload or json.loads((paths.MANIFEST_DIR/'crew-slip-positions.json').read_text())
	if holds is None:
		holds=json.loads((paths.MANIFEST_DIR/'landing-survey-holds.json').read_text())
	held={row['stop_id']:row for row in holds['stops']}
	calibration=calibrate(contexts,payload); offset=calibration['mean_m']
	document=dict(schema_version=1,purpose='Crew GPS constrained landing estimates; chart outlines retained. Not a navigation aid or a hydrographic survey.',
		notes='Generated by python -m ferrydata.survey_fit. GPS fixes are on vessels; mean chart-edge offset is estimated. One berth per face; names joined along clockwise edge. No landed headings supplied. Governors Island unchanged.',stops={})
	report=dict(calibration=calibration,stops={},cross_validation=[])
	for stop in payload['stops']:
		sid=stop['stop_id']; context=contexts[sid]; points=local_slips(stop)
		if sid in held:
			# A reviewed hold removes the survey override, preserving the derived
			# landing and all existing physics gates. Keep the GPS observations.
			report['stops'][sid]=dict(name=stop['name'],hold=held[sid],note=held[sid]['reason'],
				slips=[dict(row,accepted=False) for row in observations(context['baseline'],points)],
				barges=[dict(berth_id=context['id'],before=pose(context['baseline']),
					after=pose(context['baseline']),confidence='unchanged')])
			continue
		barges=[]; diagnostics=[]
		if context['charted']:
			associations=associate(context,points,all_pontoons=True)
			for r in associations:
				if sid=='87' and r['name'] in ('Slip B1','Slip B2','Slip E1'):
					r['accepted']=False
			for identifier,polygon in sorted(context['charted'].items()):
				rows=[r for r in associations if r['berth_id']==identifier and r['accepted']]
				if not rows:
					continue
				groups=group_faces(rows)
				if sid=='87':
					# Keep exactly the existing three selectable faces, including the
					# south pontoon whose two nearest edges disagree. No new candidate.
					p=context['properties']; original=next(b for b in p['berths'] if b['id']==identifier)
					bearing=landings.heading_deg(original['outward_normal_ne'])
					groups=[dict(name=' / '.join(r['name'] for r in sorted(rows,key=lambda r:(r['face_index'],r['along_m']))),face_bearing_deg=bearing)]
				barges.append(dict(berth_id=identifier,berths=[dict(name=g['name'],face_bearing=measurement(g['face_bearing_deg'],'deg','medium','NOAA ENC; '+SOURCE,
					note='Chart edge outward normal; vessel GPS association. Pier 11 retains the existing selected face.' if sid=='87' else 'Nearest chart edge to vessel GPS fixes; chart outline retained.')) for g in groups],
					notes='Outline retained exactly; pose available in the fit report, not repeated as a rectangle override.'))
				diagnostics.append(dict(berth_id=identifier,before=pose(polygon),after=pose(polygon),confidence='medium',slips=rows))
				# Leave this entire stop out of calibration, and fit without its polygon.
				training=dict(payload,stops=[s for s in payload['stops'] if s['stop_id']!=sid])
				loo=calibrate(contexts,training)['mean_m']
				fitted=fit([dict(name=r['name'],point_en=r['point_en']) for r in rows],context['validation_baseline'],context['validation_anchor'],loo)
				truth=pose(polygon)
				report['cross_validation'].append(dict(stop_id=sid,berth_id=identifier,calibration_m=loo,
					centre_error_m=float(np.linalg.norm(np.array(fitted['centre_en'])-truth['centre_en'])),
					turn_error_deg=abs((fitted['heading_deg']-truth['heading_deg']+90)%180-90),fit=fitted))
		else:
			try:
				fitted=fit(points,context['baseline'],context['anchor'],offset)
			except ValueError as exc:
				if 'cannot fit' not in str(exc):
					raise
				# No source identifies a unique single-pontoon subset. Keep every
				# fix as evidence instead of enlarging a barge or choosing a subset.
				rows=[dict(r,accepted=False) for r in observations(context['baseline'],points)]
				report['stops'][sid]=dict(name=stop['name'],slips=rows,
					note='Published size cannot fit all slips; no defensible unique single-face subset. Stop omitted and unchanged pending LND-03 to LND-05 layout/pontoon mapping.',
					barges=[dict(berth_id=context['id'],before=pose(context['baseline']),after=pose(context['baseline']),confidence='unchanged')])
				continue
			n,e=fitted['centre_en'][1],fitted['centre_en'][0]
			lat,lon=geodesy.from_local(n,e)
			barge=dict(berth_id=context['id'],centre=measurement([round(float(lat),9),round(float(lon),9)],'deg'),
				heading=measurement(round(fitted['heading_deg'],6),'deg'),
				length=measurement(round(fitted['length_m'],6),source='Published landing engineering L04, docs/research/routes-and-landings.md',method='known-dimension'),
				width=measurement(round(fitted['width_m'],6),source='Published landing engineering L04, docs/research/routes-and-landings.md',method='known-dimension'),
				berths=[dict(name=g['name'],face_bearing=measurement(round(g['face_bearing_deg'],6),'deg')) for g in fitted['berths']],
				notes=fitted['reason']+f' Calibrated mean antenna-to-face proxy {offset:.6f} m; GPS allowance 5 m. '+'Published 90 by 35 ft size assumed.')
			barges.append(barge)
			diagnostics.append(dict(berth_id=context['id'],before=pose(context['baseline']),after={k:fitted[k] for k in ('centre_en','heading_deg','length_m','width_m')},**{k:v for k,v in fitted.items() if k not in ('centre_en','heading_deg','length_m','width_m')}))
			associations=fitted['slips']
		document['stops'][sid]=dict(barges=barges,notes='LND-01 landed headings and antenna offsets; LND-08 face/slip names; LND-03 to LND-05 layout photos and dimensions remain needed.')
		report['stops'][sid]=dict(name=stop['name'],barges=diagnostics,slips=associations)
	for sid,context in contexts.items():
		if sid not in report['stops']:
			report['stops'][sid]=dict(name=context['name'],barges=[dict(berth_id=context['id'],before=pose(context['baseline']),after=pose(context['baseline']),confidence='unchanged')],slips=[],note='No crew slips; unchanged.')
	return document,report


def svg(context,stop,path):
	polygons=[context['baseline']]+[rectangle(b['after']['centre_en'],b['after']['heading_deg'],b['after']['length_m'],b['after']['width_m']) if b['berth_id'].startswith('derived-') else context['charted'].get(b['berth_id'],context['baseline']) for b in stop['barges']]
	bounds=unary_union(polygons+[Point(s['point_en']) for s in stop['slips']]).bounds
	x0,y0,x1,y1=bounds; pad=30; x0-=pad;y0-=pad;x1+=pad;y1+=pad
	w,h=x1-x0,y1-y0
	parts=[f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h+18}"><rect width="100%" height="100%" fill="#eef5fa"/>',f'<text x="2" y="6" font-size="4">{html.escape(stop["name"])} · metres · chart grey / before red / fit blue</text>',f'<g transform="translate({-x0} {y1+12}) scale(1 -1)" fill="none" stroke-width="0.6">']
	def draw(g,color,width=.6):
		if g.geom_type in ('MultiPolygon','GeometryCollection','MultiLineString'):
			for child in g.geoms:draw(child,color,width)
		elif g.geom_type in ('Polygon','LineString'):
			coords=g.exterior.coords if g.geom_type=='Polygon' else g.coords
			parts.append(f'<polyline stroke="{color}" stroke-width="{width}" points="'+ ' '.join(f'{x},{y}' for x,y in coords)+'"/>')
	from shapely.geometry import box
	clip=box(x0,y0,x1,y1)
	for g,_ in context['shore']+context['lands']:
		if g.intersects(clip):draw(g.intersection(clip),'#929da6')
	draw(context['baseline'],'#d34c4c',1)
	for polygon in polygons[1:]:draw(polygon,'#147ad6',.8)
	for s in stop['slips']:
		x,y=s['point_en'];parts.append(f'<circle cx="{x}" cy="{y}" r="1" fill="#151515"/>')
		if 'face_start_en' in s:draw(LineString([s['face_start_en'],s['face_end_en']]),'#00a67e',1)
	parts.append('</g>')
	for s in stop['slips']:
		x,y=s['point_en'];parts.append(f'<text x="{x-x0+2}" y="{y1-y+12}" font-size="3">{html.escape(s["name"])}</text>')
	parts.append(f'<path d="M {w-9},18 v -10 l -2,4 m 2,-4 l 2,4" stroke="black" fill="none"/><text x="{w-11}" y="6" font-size="4">N</text><path d="M 4,{h+12} h 20" stroke="black"/><text x="4" y="{h+9}" font-size="3">20 m</text></svg>')
	path.write_text(''.join(parts))



def tables(report, out):
	rows = ['# Before and After', '', '| Stop | Barge | Before centre E,N (m) | After centre E,N (m) | Before / after heading (deg true) | Shift (m) | Turn (deg) | After length / width (m) | Confidence | Next evidence |', '|---|---|---|---|---|---:|---:|---|---|---|']
	slips = ['# Per-slip Evidence', '', 'Along-face distances are unclamped, from the displayed clockwise edge start. Off-face distances are signed positive toward water. Nearest chart edges are associations; fitted edges are layout hypotheses.', '', '| Stop | Slip | Barge | Face outward (deg) | Along (m) | Off (m) | Edge distance (m) | Accepted |', '|---|---|---|---:|---:|---:|---:|---|']
	for sid, stop in sorted(report['stops'].items(), key=lambda x:int(x[0])):
		for b in stop['barges']:
			a,z = b['before'],b['after']
			shift = math.dist(a['centre_en'], z['centre_en']); turn = abs((z['heading_deg']-a['heading_deg']+90)%180-90)
			need = ('LND-01 first slip fix and landed heading' if sid=='111' else 'LND-03 to LND-05 terminal dimensions and number of pontoons' if sid in ('120','136') else 'LND-08 A–E pontoon/face mapping' if sid=='87' else 'LND-01 landed headings and antenna-to-bow distances')
			rows.append(f"| {sid} {stop['name']} | {b['berth_id']} | {a['centre_en'][0]:.3f}, {a['centre_en'][1]:.3f} | {z['centre_en'][0]:.3f}, {z['centre_en'][1]:.3f} | {a['heading_deg']:.3f} / {z['heading_deg']:.3f} | {shift:.3f} | {turn:.3f} | {z['length_m']:.3f} / {z['width_m']:.3f} | {b['confidence']} | {need} |")
		for r in stop['slips']:
			slips.append(f"| {sid} | {r['name']} | {r.get('berth_id', stop['barges'][0]['berth_id'])} | {r['face_bearing_deg']:.3f} | {r['along_m']:.3f} | {r['off_m']:.3f} | {r['edge_distance_m']:.3f} | {r.get('accepted', True)} |")
	(out/'before-after.md').write_text('\n'.join(rows)+'\n')
	(out/'slip-evidence.md').write_text('\n'.join(slips)+'\n')
	checks = ['# Leave-one-stop-out Chart Checks', '', 'Fit against a shoreline/GTFS-derived rectangle, using calibration that excludes this stop. The chart outline is used only to evaluate the result and to associate slips to pontoons; this is not blind validation of the association step.', '', '| Stop | Barge | Centre error (m) | Axis error (deg) | Calibration (m) |', '|---|---|---:|---:|---:|']
	for r in report['cross_validation']:
		checks.append(f"| {r['stop_id']} | {r['berth_id']} | {r['centre_error_m']:.3f} | {r['turn_error_deg']:.3f} | {r['calibration_m']:.6f} |")
	(out/'cross-validation.md').write_text('\n'.join(checks)+'\n')


def main():
	parser=argparse.ArgumentParser(description=__doc__)
	parser.add_argument('--out',type=Path,default=paths.REPO_ROOT/'artifacts/landing-angles')
	parser.add_argument('--write',action='store_true',help='Write the reviewed candidate manifest')
	args=parser.parse_args();args.out.mkdir(parents=True,exist_ok=True)
	document,report=generate();(args.out/'fit-report.json').write_text(dump(report));(args.out/'landing-survey.json').write_text(dump(document))
	if args.write:(paths.MANIFEST_DIR/'landing-survey.json').write_text(dump(document))
	tables(report,args.out)
	contexts=inputs()
	for sid,stop in report['stops'].items():svg(contexts[sid],stop,args.out/(sid+'.svg'))
	print(dump({k:v for k,v in report['calibration'].items() if k!='samples'}))


if __name__=='__main__':
	main()
