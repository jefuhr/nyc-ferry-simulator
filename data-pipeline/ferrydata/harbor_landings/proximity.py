"""Classify lowered near-hazard source cells against actual starts and a straight 100 m hull extrusion.

The extrusion runs astern from the start along the existing outward normal with fixed
heading. It is a spatial classification only, never a safe route or a changed corridor
gate. A source cell outside a hull can still affect its depth through raster/depth-grid
interpolation, so the recomputed matrix remains the authoritative start comparison.
"""
import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd
import rasterio
from shapely import union_all
from shapely.affinity import translate
from shapely.geometry import Point, Polygon
from shapely.strtree import STRtree

from ferrydata import geodesy, landings, manifest
from ferrydata.environment import pack
from ferrydata.harbor_landings import assessment, matrix
from ferrydata.harbor_landings.candidate import cell_key


def extrude(outline_en, delta_en):
	outline = np.asarray(outline_en, float)
	delta = np.asarray(delta_en, float)
	parts = [Polygon(outline), Polygon(outline + delta)]
	for i, a in enumerate(outline):
		b = outline[(i + 1) % len(outline)]
		quad = Polygon([a, b, b + delta, a + delta])
		if quad.area > 0.:
			parts.append(quad)
	return union_all(parts)


def run(candidate, contacts_path, destination):
	candidate, destination = Path(candidate), Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	input_file = candidate / 'lowered-within-50m-hazard.csv'
	frame = pd.read_csv(input_file)
	lat, lon = geodesy.from_local(frame.north_m.to_numpy(), frame.east_m.to_numpy())
	keys = [cell_key(a, b) for a, b in zip(lat, lon)]
	frame['global_lat_cell'] = [k[0] for k in keys]
	frame['global_lon_cell'] = [k[1] for k in keys]
	cell_columns = ['global_lat_cell', 'global_lon_cell']
	cells = frame.drop_duplicates(cell_columns).copy()
	berths = matrix.catalogue(json.loads(Path(contacts_path).read_text(encoding='utf-8')))
	starts, approaches, labels = [], [], []
	for berth in berths:
		if not berth['eligible']:
			continue
		for hull in ('t-class', 'k-class'):
			v = assessment.vessel(hull)
			n, e, heading = matrix.pose(berth, v)
			outline = assessment.world_outline(v, n, e, heading)[:, [1, 0]]
			starts.append(Polygon(outline))
			approaches.append(extrude(outline, berth['normal'][[1, 0]] * 100.))
			labels.append(f'{berth["stop_id"]}/{berth["berth_id"]}/{hull}')
	start_tree, approach_tree = STRtree(starts), STRtree(approaches)
	transforms, details = {}, []
	for row in cells.to_dict('records'):
		tile = row['tile']
		if tile not in transforms:
			with rasterio.open(candidate / 'tiles' / tile / 'depth_merged_navd88.tif') as ds:
				transforms[tile] = ds.transform
		t, r, c = transforms[tile], row['row'], row['col']
		corners = [(t.c + cc * t.a, t.f + rr * t.e) for rr, cc in ((r, c), (r, c + 1), (r + 1, c + 1), (r + 1, c))]
		polygon = landings.to_local(Polygon(corners))
		point = Point(row['east_m'], row['north_m'])
		start_indices = sorted(start_tree.query(polygon, predicate='intersects'))
		approach_indices = sorted(approach_tree.query(polygon, predicate='intersects'))
		nearest = min(berths, key=lambda b: (float(np.hypot(b['mid'][0] - row['north_m'], b['mid'][1] - row['east_m'])), b['stop_id']))
		details.append(dict(global_lat_cell=row['global_lat_cell'], global_lon_cell=row['global_lon_cell'],
			nearest_stop_id=nearest['stop_id'], nearest_stop_name=nearest['stop_name'],
			start_berths=';'.join(labels[i] for i in start_indices),
			approach_berths=';'.join(labels[i] for i in approach_indices),
			in_start_footprint=bool(start_indices), in_first_100m_approach=bool(approach_indices),
			centre_in_start_footprint=any(starts[i].covers(point) for i in start_indices),
			region='start footprint' if start_indices else 'first 100 m approach only' if approach_indices else 'elsewhere'))
	classification = pd.DataFrame(details, columns=[*cell_columns, 'nearest_stop_id', 'nearest_stop_name', 'start_berths', 'approach_berths',
		'in_start_footprint', 'in_first_100m_approach', 'centre_in_start_footprint', 'region'])
	classified = frame.merge(classification, on=cell_columns, validate='many_to_one')
	classified['hazard_type'] = classified.hazard_layer.map(lambda x: x if x in ('OBSTRN', 'WRECKS') else 'WATLEV 3 submerged structure')
	classified.to_csv(destination / 'cell-hazard-classification.csv', index=False, lineterminator='\n')
	# A cell may occur against several hazards or tile margins: each grouping counts its global identity once.
	group_columns = ['nearest_stop_id', 'nearest_stop_name', 'hazard_type']
	grouped = []
	for key, own in classified.groupby(group_columns, sort=True):
		unique = own.drop_duplicates(cell_columns)
		grouped.append(dict(zip(group_columns, key), unique_cells=len(unique), min_distance_m=float(own.distance_m.min()),
			max_lowering_m=float(own.lowering_m.max()), start_cells=int(unique.in_start_footprint.sum()),
			approach_only_cells=int((unique.region == 'first 100 m approach only').sum()),
			elsewhere_cells=int((unique.region == 'elsewhere').sum())))
	pd.DataFrame(grouped, columns=[*group_columns, 'unique_cells', 'min_distance_m', 'max_lowering_m', 'start_cells', 'approach_only_cells', 'elsewhere_cells']).to_csv(
		destination / 'by-landing-hazard-type.csv', index=False, lineterminator='\n')
	cells_out = cells.merge(classification, on=cell_columns, validate='one_to_one')
	cells_out.to_csv(destination / 'unique-cells.csv', index=False, lineterminator='\n')
	summary = dict(records=len(frame), unique_cells=len(cells), grouped=grouped,
		start_cells=int(classification.in_start_footprint.sum()),
		approach_only_cells=int((classification.region == 'first 100 m approach only').sum()),
		elsewhere_cells=int((classification.region == 'elsewhere').sum()),
		min_distance_m=float(frame.distance_m.min()) if len(frame) else None,
		max_lowering_m=float(frame.lowering_m.max()) if len(frame) else None,
		sources=dict(proximity_csv_sha256=manifest.sha256_file(input_file), contacts_sha256=manifest.sha256_file(contacts_path)),
		classification='Cell footprints intersect hull/straight extrusion; centres also flagged. Straight extrusion is 100 m astern from the actual start, fixed heading; not a driven approach or route proof. Interpolation can transmit source-cell changes from outside these regions; compare the actual matrix.')
	pack.write_json(destination / 'summary.json', summary)
	return summary


if __name__ == '__main__':
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument('--candidate', required=True)
	parser.add_argument('--contacts', required=True)
	parser.add_argument('--out', required=True)
	args = parser.parse_args()
	print(json.dumps(run(args.candidate, args.contacts, args.out), indent=2))
