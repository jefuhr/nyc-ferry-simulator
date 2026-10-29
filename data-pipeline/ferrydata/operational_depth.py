"""Operational depth floor from the fleet's own AIS (method `operational-ais`).

Beside some berths the chart cannot settle the depth: its depth areas there are 0-2 m bands and the water next to
the piers is unsurveyed (UNSARE). The fleet's T- and K-class boats berth there every day, and a boat afloat bounds
the bed under it. For each qualifying AIS report:

- Which reports: a T- or K-class identity (the calibration identity audit), inside the depth grid, within RADIUS_M
  of any selectable berth face's midpoint, SOG at most MAX_SOG_MPS (berthed and manoeuvring; faster reports
  squat), on the 28 scenario days (New York local days, as the environment packs date them).
- Level L: the simulation's water level at the report's position and time, as the environment packs and the core's
  DataEnvironment compute it from the gauges (series_value, blend).
- Required bed: L - D - M, with D the class's particulars.maxDraft (UNKNOWN_CLASS_DRAFT_M for an unknown class) and
  M = KEEL_MARGIN_M, a keel margin that is an estimate.
- Footprint: every water cell whose centre lies within half the class's length overall of the report, because the
  AIS antenna offset is unknown.

A cell's floor is the deepest required bed over every report whose footprint covers it, and its new bed is
min(bed, floor) on water cells only: a bed is never raised, land and fixed structures are never touched and nodata is
never filled. Charted hazards keep their bed too (coordinator, 2026-10-25): every cell an always-submerged structure
(WATLEV 3) or an ENC obstruction or wreck area touches, and the report lists what each would have lost. floor_grid and
apply_floor work on any north-up lon/lat grid. The slice build is unchanged; harbor_landings builds an isolated
candidate with build_harbor_tile, and requires separate review before adoption.
"""
import json
import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import pandas as pd
import rasterio
from rasterio import features, windows
from shapely.geometry import shape

from ferrydata import depth, geodesy, landings, manifest, paths
from ferrydata.environment import series

METHOD = 'operational-ais'
RADIUS_M = 200.
MAX_SOG_MPS = 2.5
KEEL_MARGIN_M = .3
UNKNOWN_CLASS_DRAFT_M = 1.9
VESSEL_FILES = {'T': 't-class.json', 'K': 'k-class.json'}
# The core's reading of a pack's gauges: SeriesTime.MaximumInterpolatedGap and InverseDistanceSquared.AtStation.
MAXIMUM_INTERPOLATED_GAP_S = 1800.
AT_STATION_M2 = 1e-12
LEVEL_MODEL = ("the environment packs' water level: each NAVD88 gauge's series at the pack time (linear across gaps up to "
	"1800 s, the nearer sample across longer gaps, held before the first and after the last sample and at the coverage "
	"edges), blended between the gauges by inverse distance squared, as the core's DataEnvironment reads it")
FLOOR_FILE = 'depth_operational_floor_navd88.tif'
LOWERED_FILE = 'depth_operational_lowered_m.tif'
REPORTS_FILE = 'depth_operational_reports.csv'
REPORT_FILE = 'depth_operational_floor.json'
REPORT_MD = 'depth_operational_floor.md'
DROP_REASONS = ('not_t_or_k', 'no_position', 'no_sog', 'over_sog_limit', 'beyond_radius', 'off_depth_grid', 'off_scenario_days')
HAZARD_LAYERS = {'OBSTRN': 'obstruction area', 'WRECKS': 'wreck area'}
NOTES = [
	'The keel margin M is an estimate, not a crew-reported or surveyed figure.',
	'AIS antenna offsets are unknown, so a report deepens every water cell within half its class length overall.',
	'A floor is evidence that a boat floated there at that level; it is not a survey and never makes a bed shallower.',
	'Charted hazards keep their bed: every cell an always-submerged structure (WATLEV 3) or an ENC obstruction or wreck area touches.',
]


# ---- the classes and identities ------------------------------------------------------------------------------


@dataclass(frozen=True)
class VesselClass:
	name: str
	max_draft_m: float
	length_overall_m: float
	file: str
	sha256: str

	@property
	def footprint_radius_m(self):
		return self.length_overall_m / 2.


def vessel_classes(directory=None):
	"""T- and K-class maxDraft and lengthOverall from vessels/*.json."""
	directory = Path(directory) if directory else paths.REPO_ROOT / 'vessels'
	classes = {}
	for name, file in sorted(VESSEL_FILES.items()):
		path = directory / file
		particulars = json.loads(path.read_text(encoding='utf-8'))['particulars']
		values = {}
		for key in ('maxDraft', 'lengthOverall'):
			if particulars[key]['unit'] != 'm':
				raise ValueError(f'{path}: {key} must be in metres')
			values[key] = float(particulars[key]['value'])
		classes[name] = VesselClass(name, values['maxDraft'], values['lengthOverall'], file, manifest.sha256_file(path))
	return classes


def draft_m(classes, name):
	return classes[name].max_draft_m if name in classes else UNKNOWN_CLASS_DRAFT_M


def identity_audit_path():
	return paths.REPO_ROOT / 'data-pipeline' / 'calibration' / 'identity-audit.csv'


def identity_classes(path=None):
	"""MMSI -> 'T' or 'K' for the identity audit's T and K subsets (the traffic packs' ferry candidates)."""
	frame = pd.read_csv(path or identity_audit_path(), dtype={'class': str})
	classes = {}
	for mmsi, label in zip(frame['mmsi'], frame['class'].fillna('')):
		name = 'T' if label == 'T' else 'K' if label == 'K' or label.startswith('K-') else None
		if name:
			classes[int(mmsi)] = name
	return classes


# ---- the level model: the core's DataEnvironment, gauge by gauge ----------------------------------------------


def series_value(times, values, time):
	"""ScalarSeries.Sample: linear across gaps up to 1800 s, the nearer sample across longer gaps (the earlier one at
	the midpoint), the first or last sample beyond the series. The arithmetic follows the core's order."""
	last = len(times) - 1
	if not time > times[0]:
		return float(values[0])
	if time >= times[last]:
		return float(values[last])
	low, high = 0, last
	while high - low > 1:
		middle = (low + high) >> 1
		if times[middle] <= time:
			low = middle
		else:
			high = middle
	gap = times[high] - times[low]
	if gap <= MAXIMUM_INTERPOLATED_GAP_S:
		fraction = (time - times[low]) / gap
		return float(values[low] + fraction * (values[high] - values[low]))
	return float(values[low] if time - times[low] <= times[high] - time else values[high])


def blend(north, east, gauge_north, gauge_east, values):
	"""InverseDistanceSquared.Blend: one gauge is its value everywhere; at a gauge, that gauge's value."""
	if len(values) == 1:
		return float(values[0])
	weights = total = 0.
	for n, e, value in zip(gauge_north, gauge_east, values):
		dn, de = north - n, east - e
		squared = dn * dn + de * de
		if squared <= AT_STATION_M2:
			return float(value)
		weight = 1. / squared
		weights += weight
		total += weight * value
	return total / weights


@dataclass
class Gauge:
	id: str
	north_m: float
	east_m: float
	times: list
	values: list
	source: str = ''
	source_sha256: str = ''


class DayLevels:
	"""One pack day's water level at a world position and pack time (seconds since the day's local midnight)."""

	def __init__(self, gauges, coverage):
		if not gauges:
			raise ValueError('a day needs at least one water level gauge')
		self.gauges = list(gauges)
		self.coverage = (float(coverage[0]), float(coverage[1]))

	def level(self, north, east, pack_time):
		held = min(max(pack_time, self.coverage[0]), self.coverage[1])
		values = [series_value(g.times, g.values, held) for g in self.gauges]
		return blend(north, east, [g.north_m for g in self.gauges], [g.east_m for g in self.gauges], values)


def day_levels(day, station_ids, inputs):
	"""A day's gauges as the environment build selects them: the slice's NAVD88 gauges, in its order, with rows that day."""
	week = next(w for w in series.WEEKS if 0 <= (series.epoch(day) - series.epoch(w)).days < 7)
	gauges = []
	for station_id in station_ids:
		if not any(d['name'] == 'NAVD88' for d in inputs.datums(station_id).get('datums', [])):
			continue
		metadata, rows, _, _ = inputs.water_levels(inputs.station(station_id), day, week)
		if metadata:
			gauges.append(Gauge(station_id, float(metadata['north_m']), float(metadata['east_m']),
				[float(r[1]) for r in rows], [float(r[2]) for r in rows], metadata.get('source', ''), metadata.get('source_sha256', '')))
	return DayLevels(gauges, series.COVERAGE)


def cached_inputs():
	"""The environment build's sources, read from the pinned cache only: a missing file stops the build, never a download."""
	from ferrydata.environment.sources import Inputs

	class CachedInputs(Inputs):
		def get(self, name, url):
			if name not in self.loaded and not (name in self.manifest.files and (self.cache / name).is_file()):
				raise FileNotFoundError(f'{self.cache / name}: the operational depth floor reads the environment packs\' pinned gauge '
					'files and never downloads them; build the environment packs once to fetch them')
			return super().get(name, url)

	return CachedInputs(lambda *args, **kwargs: None)


# ---- which reports count ----------------------------------------------------------------------------------


def select_reports(tracks, identities, faces, inside, days=series.DAYS):
	"""The qualifying reports, each with its nearest face, scenario day and pack time, sorted; and how many were left
	out for each reason, a report counting once under the first reason in DROP_REASONS it fails."""
	frame = tracks.reset_index(drop=True)
	faces = sorted(faces, key=lambda f: f['stop_id'])
	keep = np.ones(len(frame), bool)
	dropped = {}

	def drop(reason, mask):
		dropped[reason] = int((keep & mask).sum())
		keep[mask] = False

	vessel_class = frame['mmsi'].map(identities)
	drop('not_t_or_k', vessel_class.isna().to_numpy())
	north, east = frame['north_m'].to_numpy(float), frame['east_m'].to_numpy(float)
	lat, lon = frame['lat'].to_numpy(float), frame['lon'].to_numpy(float)
	drop('no_position', ~(np.isfinite(north) & np.isfinite(east) & np.isfinite(lat) & np.isfinite(lon)))
	sog = frame['sog_mps'].to_numpy(float)
	drop('no_sog', ~np.isfinite(sog))
	drop('over_sog_limit', ~(sog <= MAX_SOG_MPS))
	best, nearest = np.full(len(frame), np.inf), np.full(len(frame), -1)
	for index, face in enumerate(faces):
		distance = np.hypot(north - face['north_m'], east - face['east_m'])
		closer = distance < best
		best[closer], nearest[closer] = distance[closer], index
	drop('beyond_radius', ~(best <= RADIUS_M))
	drop('off_depth_grid', ~np.asarray(inside(np.where(keep, lat, 0.), np.where(keep, lon, 0.)), bool))
	local_day = frame['time_utc'].dt.tz_convert('America/New_York').dt.strftime('%Y-%m-%d')
	drop('off_scenario_days', ~local_day.isin(days).to_numpy())
	rows = frame[keep]
	epochs = local_day[keep].map(lambda day: pd.Timestamp(series.epoch(day)))
	result = pd.DataFrame({
		'stop_id': [faces[i]['stop_id'] for i in nearest[keep]], 'stop_name': [faces[i]['stop_name'] for i in nearest[keep]],
		'mmsi': rows['mmsi'].astype('int64').to_numpy(), 'vessel_class': vessel_class[keep].to_numpy(),
		'time_utc': rows['time_utc'].dt.strftime('%Y-%m-%dT%H:%M:%SZ').to_numpy(), 'day': local_day[keep].to_numpy(),
		'pack_time_s': (rows['time_utc'] - epochs).dt.total_seconds().to_numpy(),
		'lat': lat[keep], 'lon': lon[keep], 'north_m': north[keep], 'east_m': east[keep], 'sog_mps': sog[keep],
		'distance_m': best[keep]})
	return result.sort_values(['stop_id', 'mmsi', 'time_utc', 'north_m', 'east_m'], kind='mergesort').reset_index(drop=True), dropped


def required_beds(reports, levels_by_day, classes, margin=KEEL_MARGIN_M):
	"""Each report's level, draft, margin, required bed (L - D - M) and footprint radius."""
	frame = reports.copy()
	frame['level_navd88_m'] = [levels_by_day[day].level(n, e, t) for day, n, e, t in
		zip(frame['day'], frame['north_m'], frame['east_m'], frame['pack_time_s'])]
	frame['max_draft_m'] = [draft_m(classes, name) for name in frame['vessel_class']]
	frame['keel_margin_m'] = float(margin)
	frame['required_bed_navd88_m'] = frame['level_navd88_m'] - frame['max_draft_m'] - frame['keel_margin_m']
	frame['footprint_radius_m'] = [classes[name].footprint_radius_m for name in frame['vessel_class']]
	return frame


# ---- the floor on a grid ----------------------------------------------------------------------------------


def _cell_size_m(transform, grid_shape):
	"""Distance between neighbouring cell centres along a column and along a row, at the grid's middle."""
	lat = transform.f + grid_shape[0] / 2 * transform.e
	lon = transform.c + grid_shape[1] / 2 * transform.a
	n0, e0 = geodesy.to_local(lat, lon)
	n1, e1 = geodesy.to_local(lat + transform.e, lon)
	n2, e2 = geodesy.to_local(lat, lon + transform.a)
	return float(np.hypot(n1 - n0, e1 - e0)), float(np.hypot(n2 - n0, e2 - e0))


def floor_grid(reports, transform, grid_shape, water):
	"""Each cell's floor: the deepest required bed over the reports whose footprint covers its centre, NaN where none.

	Footprints cover water cells only. Distances are in the world tangent plane; the grid is north-up lon/lat."""
	if transform.b != 0 or transform.d != 0:
		raise ValueError('the floor needs a north-up grid')
	floor = np.full(grid_shape, np.inf)
	if len(reports):
		row_m, col_m = _cell_size_m(transform, grid_shape)
		items = sorted(zip(reports['north_m'], reports['east_m'], reports['required_bed_navd88_m'], reports['footprint_radius_m']))
		lat, lon = geodesy.from_local(np.array([i[0] for i in items]), np.array([i[1] for i in items]))
		inverse = ~transform
		for (north, east, required, radius), y, x in zip(items, np.atleast_1d(lat), np.atleast_1d(lon)):
			col, row = inverse.a * x + inverse.b * y + inverse.c, inverse.d * x + inverse.e * y + inverse.f
			reach_row, reach_col = int(np.ceil(radius / row_m)) + 2, int(np.ceil(radius / col_m)) + 2
			r0, r1 = max(int(np.floor(row)) - reach_row, 0), min(int(np.floor(row)) + reach_row + 1, grid_shape[0])
			c0, c1 = max(int(np.floor(col)) - reach_col, 0), min(int(np.floor(col)) + reach_col + 1, grid_shape[1])
			if r0 >= r1 or c0 >= c1:
				continue
			rows, cols = np.mgrid[r0:r1, c0:c1]
			cell_north, cell_east = geodesy.to_local(transform.f + (rows + .5) * transform.e, transform.c + (cols + .5) * transform.a)
			cover = ((cell_north - north) ** 2 + (cell_east - east) ** 2 <= radius * radius) & water[r0:r1, c0:c1]
			window = floor[r0:r1, c0:c1]
			window[cover] = np.minimum(window[cover], required)
	floor[np.isinf(floor)] = np.nan
	return floor


def _at_or_below(values, dtype):
	"""values in dtype, rounded toward deeper water where the cast rounded up."""
	cast = np.asarray(values).astype(dtype)
	above = cast.astype('float64') > values
	cast[above] = np.nextafter(cast[above], np.array(-np.inf, dtype=dtype))
	return cast


def apply_floor(bed, floor, water, nodata):
	"""min(bed, floor) on valid water cells. Returns the new bed, the lowering and the mask of lowered cells.

	The new bed keeps the bed's dtype, rounded toward deeper water, so every lowered cell is at or below its float64
	floor. The lowering is float64 and exact (before = after + lowering), 0 where unchanged and NaN where the bed is
	nodata."""
	bed = np.asarray(bed)
	valid = np.isfinite(bed) if nodata is None else np.isfinite(bed) & (bed != nodata)
	lower = valid & water & np.isfinite(floor) & (floor < bed)
	new = bed.copy()
	new[lower] = _at_or_below(floor[lower], bed.dtype)
	lowered = np.where(valid, bed.astype('float64') - new.astype('float64'), np.nan)
	return new, lowered, lower


def fixed_mask(out, grid_shape, transform):
	"""Cells the floor never touches: ENC LNDARE and dry SLCONS areas (WATLEV other than 3 and 7), the fixed solid the
	landing structures and validate use."""
	parts = _geometries(Path(out) / 'land_areas.geojson')
	parts += [g for g, p in _geometries(Path(out) / 'shoreline_constructions.geojson', properties=True)
		if p['feature_class'] == 'SLCONS' and g.geom_type == 'Polygon' and p.get('water_level_effect_code') not in (3, 7)]
	if not parts:
		return np.zeros(grid_shape, bool)
	return features.geometry_mask(parts, out_shape=grid_shape, transform=transform, invert=True)


def hazards(out, slice_, *, areas_only=True):
	"""Charted hazards the floor never deepens, sorted by layer and id: always-submerged structures (WATLEV 3) in
	shoreline_constructions.geojson, and ENC obstruction and wreck areas (OBSTRN and WRECKS polygons).
	The harbor caller sets areas_only=False to protect point/line hazards too; the slice default is unchanged."""
	from ferrydata import enc
	items = [dict(layer=p['feature_class'], id=str(p['id']), kind='always-submerged structure', category=p.get('category'), geometry=g)
		for g, p in _geometries(Path(out) / 'shoreline_constructions.geojson', properties=True) if p.get('water_level_effect_code') == 3]
	layers, _ = enc.read_layers(slice_, tuple(HAZARD_LAYERS))
	for name, kind in HAZARD_LAYERS.items():
		if name not in layers:
			continue
		for row in layers[name].itertuples():
			if row.geometry is not None and (not areas_only or row.geometry.geom_type in ('Polygon', 'MultiPolygon')):
				category = getattr(row, 'CATOBS' if name == 'OBSTRN' else 'CATWRK', None)
				items.append(dict(layer=name, id=str(row.LNAM), kind=kind, category=None if pd.isna(category) else int(category), geometry=row.geometry))
	return sorted(items, key=lambda h: (h['layer'], h['id']))


def hazard_mask(items, grid_shape, transform):
	"""Every cell a charted hazard touches."""
	if not items:
		return np.zeros(grid_shape, bool)
	return features.geometry_mask([h['geometry'] for h in items], out_shape=grid_shape, transform=transform, invert=True, all_touched=True)


def left_out(items, bed, reports, transform, grid_shape, open_water, nodata, faces):
	"""Per hazard: the valid water cells it touches, how many a report's footprint reaches, and the lowering they would
	have had without the hazard rule (bed less the floor), with the nearest landing face."""
	valid = np.isfinite(bed) & (bed != nodata)
	masks = [features.geometry_mask([h['geometry']], out_shape=grid_shape, transform=transform, invert=True, all_touched=True) & valid & open_water
		for h in items]
	would = floor_grid(reports, transform, grid_shape, np.logical_or.reduce(masks) if masks else np.zeros(grid_shape, bool))
	records = []
	for h, cells in zip(items, masks):
		reached = cells & np.isfinite(would)
		lower = reached & (would < bed)
		amounts = bed[lower].astype('float64') - would[lower]
		centre = h['geometry'].centroid
		north, east = geodesy.to_local(centre.y, centre.x)
		face = min(faces, key=lambda f: (math.hypot(f['anchor_ne'][0] - north, f['anchor_ne'][1] - east), f['stop_id']))
		records.append(dict(layer=h['layer'], id=h['id'], kind=h['kind'], category=h['category'], geometry_type=h['geometry'].geom_type,
			cells=int(cells.sum()), cells_reached=int(reached.sum()), cells_would_lower=int(lower.sum()),
			max_would_lower_m=float(amounts.max()) if amounts.size else 0., mean_would_lower_m=float(amounts.mean()) if amounts.size else 0.,
			nearest_landing=face['stop_name'], distance_to_face_m=float(math.hypot(face['anchor_ne'][0] - north, face['anchor_ne'][1] - east))))
	return records


def left_out_summary(records):
	summary = {}
	for record in records:
		entry = summary.setdefault(record['kind'], dict(features=0, cells=0, cells_reached=0, cells_would_lower=0, max_would_lower_m=0.))
		entry['features'] += 1
		for key in ('cells', 'cells_reached', 'cells_would_lower'):
			entry[key] += record[key]
		entry['max_would_lower_m'] = max(entry['max_would_lower_m'], record['max_would_lower_m'])
	return {kind: summary[kind] for kind in sorted(summary)}


def _geometries(path, properties=False):
	collection = json.loads(Path(path).read_text(encoding='utf-8'))
	return [(shape(f['geometry']), f['properties']) if properties else shape(f['geometry']) for f in collection['features']]


# ---- outputs ----------------------------------------------------------------------------------------------


def _write(target, array, profile, tags):
	with rasterio.open(target, 'w', **profile) as dataset:
		dataset.write(array, 1)
		dataset.update_tags(**tags)


def _counts(values):
	return {key: int(count) for key, count in sorted(pd.Series(values, dtype=object).value_counts().items())}


def write_outputs(out, bed, water, profile, reports, classes, sources):
	"""The floor and lowering rasters beside the merged bed. Returns the new bed and the provenance for its metadata."""
	out = Path(out)
	transform, nodata = profile['transform'], profile.get('nodata')
	floor = floor_grid(reports, transform, bed.shape, water)
	new, lowered, mask = apply_floor(bed, floor, water, nodata)
	tags = {'method': METHOD, 'vertical_datum': 'NAVD88', 'units': 'm'}
	applied = np.full(bed.shape, np.nan, dtype='float32')
	covered = np.isfinite(floor)
	applied[covered] = _at_or_below(floor[covered], np.float32)
	_write(out / FLOOR_FILE, applied, {**profile, 'dtype': 'float32', 'nodata': float('nan')},
		{**tags, 'positive': 'up', 'meaning': 'deepest required bed L - D - M over the T/K reports covering the cell; NaN where none applies'})
	_write(out / LOWERED_FILE, lowered, {**profile, 'dtype': 'float64', 'nodata': float('nan')},
		{**tags, 'positive': 'down', 'meaning': 'metres the operational floor lowered the merged bed; 0 where unchanged, NaN where the bed is nodata'})
	amounts = lowered[mask]
	provenance = {
		'method': METHOD, 'applied': True,
		'parameters': {'radius_m': RADIUS_M, 'max_sog_mps': MAX_SOG_MPS, 'keel_margin_m': KEEL_MARGIN_M,
			'unknown_class_draft_m': UNKNOWN_CLASS_DRAFT_M,
			'max_draft_m': {name: c.max_draft_m for name, c in sorted(classes.items())},
			'footprint_radius_m': {name: c.footprint_radius_m for name, c in sorted(classes.items())},
			'level_model': LEVEL_MODEL},
		'reports': {'total': int(len(reports)), 'by_class': _counts(reports['vessel_class'])},
		'floor_cells': int(covered.sum()), 'cells_lowered': int(mask.sum()),
		'max_lowering_m': float(amounts.max()) if amounts.size else 0., 'mean_lowering_m': float(amounts.mean()) if amounts.size else 0.,
		'floor_file': FLOOR_FILE, 'floor_sha256': manifest.sha256_file(out / FLOOR_FILE),
		'lowered_file': LOWERED_FILE, 'lowered_sha256': manifest.sha256_file(out / LOWERED_FILE),
		'sources': sources, 'notes': NOTES}
	return new, provenance


def face_anchors(faces):
	"""Every selectable berth's midpoint and outward normal; older outputs carry only the primary face."""
	anchors = []
	for feature in faces:
		p = feature['properties']
		for berth in p.get('berths', [p]):
			line = landings.to_local(shape(berth['face_geometry']))
			middle = line.interpolate(.5, normalized=True)
			anchors.append(dict(stop_id=p['stop_id'], stop_name=p['stop_name'],
				berth_id=berth.get('id', p.get('outline_id')), berth_name=berth.get('name', 'Main berth'),
				north_m=float(middle.y), east_m=float(middle.x), anchor_ne=(float(middle.y), float(middle.x)),
				outward_normal_ne=tuple(berth['outward_normal_ne'])))
	return sorted(anchors, key=lambda a: a['stop_id'])


def box_cells(polygon, transform, grid_shape):
	"""Rows and columns of the cells validate samples under a box: depth.read_polygon's rounded window, then the cells
	whose centres the box contains (the cells it touches when it contains none)."""
	window = windows.from_bounds(*polygon.bounds, transform=transform).round_offsets().round_lengths()
	full = windows.Window(0, 0, grid_shape[1], grid_shape[0])
	if (window.col_off >= full.width or window.row_off >= full.height or window.col_off + window.width <= 0
		or window.row_off + window.height <= 0):
		return np.array([], int), np.array([], int)
	window = window.intersection(full)
	if window.width < 1 or window.height < 1:
		return np.array([], int), np.array([], int)
	(r0, r1), (c0, c1) = [(int(a), int(b)) for a, b in window.toranges()]
	sub = windows.transform(window, transform)
	inside = features.geometry_mask([polygon], out_shape=(r1 - r0, c1 - c0), transform=sub, invert=True, all_touched=False)
	if not inside.any():
		inside = features.geometry_mask([polygon], out_shape=(r1 - r0, c1 - c0), transform=sub, invert=True, all_touched=True)
	rows, cols = np.nonzero(inside)
	return rows + r0, cols + c0


def box_result(before, after, floor, water, transform, nodata, face, reports, low, required):
	"""A landing's approach box before and after the floor, sampled as validate samples it, with the floor's coverage of
	the box and its worst cell."""
	from ferrydata import validate
	polygon = depth.landing_box_polygon(face['anchor_ne'], face['outward_normal_ne'], validate.APPROACH_BOX_M)
	box_rows, box_cols = box_cells(polygon, transform, before.shape)
	valid_cells = np.isfinite(before[box_rows, box_cols]) & (before[box_rows, box_cols] != nodata)
	result = {}
	for name, grid in (('before', before), ('after', after)):
		values = grid[box_rows, box_cols][valid_cells].astype(float)
		stats = depth.depth_stats(values, low)
		stats['valid_fraction'] = len(values) / len(box_rows) if len(box_rows) else 0.
		stats['verdict'] = 'PASS' if validate.depth_verdict(stats, required) else 'FAIL'
		result[name] = {key: stats[key] for key in ('min_depth_m', 'p5_depth_m', 'valid_fraction', 'verdict')}
	valid = np.zeros(before.shape, bool)
	valid[box_rows[valid_cells], box_cols[valid_cells]] = True
	covered = valid & water & np.isfinite(floor)
	rows, cols = np.nonzero(valid)
	cell_north, cell_east = geodesy.to_local(transform.f + (rows + .5) * transform.e, transform.c + (cols + .5) * transform.a)
	levels = np.full(len(rows), np.inf)
	counts = np.zeros(len(rows), int)
	for n, e, radius, level in zip(reports['north_m'], reports['east_m'], reports['footprint_radius_m'], reports['level_navd88_m']):
		near = (cell_north - n) ** 2 + (cell_east - e) ** 2 <= radius * radius
		counts[near] += 1
		levels[near] = np.minimum(levels[near], level)
	reached = water[rows, cols] & (counts > 0)
	depth_after = low - after[rows, cols].astype('float64')
	failing = depth_after < required
	result.update(cells=int(valid.sum()), covered_cells=int(covered.sum()), uncovered_cells=int((valid & ~covered).sum()),
		lowered_cells=int((valid & (after < before)).sum()), failing_cells=int(failing.sum()),
		failing_uncovered_cells=int((failing & ~np.isfinite(floor[rows, cols])).sum()),
		lowest_covering_level_navd88_m=float(levels[reached].min()) if reached.any() else None)
	if len(rows):
		normal = np.asarray(face['outward_normal_ne'], float) / np.hypot(*face['outward_normal_ne'])
		k = int(np.argmin(depth_after))
		dn, de = cell_north[k] - face['anchor_ne'][0], cell_east[k] - face['anchor_ne'][1]
		result['worst_cell'] = dict(out_m=float(dn * normal[0] + de * normal[1]), along_m=float(-dn * normal[1] + de * normal[0]),
			depth_before_m=low - float(before[rows[k], cols[k]]), depth_after_m=float(depth_after[k]),
			floor_depth_m=float(low - floor[rows[k], cols[k]]) if np.isfinite(floor[rows[k], cols[k]]) else None,
			covering_reports=int(counts[k]), lowest_covering_level_navd88_m=float(levels[k]) if counts[k] else None)
	return result


def landing_rows(reports, before, after, floor, water, transform, nodata, faces, low, required):
	"""Per landing: the reports used by class and day, the cells its reports lowered, and its approach box."""
	rows = []
	for face in sorted(faces, key=lambda f: f['stop_id']):
		own = reports[reports['stop_id'] == face['stop_id']]
		own_floor = floor_grid(own, transform, before.shape, water)
		valid = np.isfinite(before) & (before != nodata)
		mask = valid & water & np.isfinite(own_floor) & (own_floor < before)
		amounts = before[mask].astype('float64') - after[mask].astype('float64')
		by_day = {day: _counts(group['vessel_class']) for day, group in own.groupby('day', sort=True)}
		rows.append(dict(stop_id=face['stop_id'], stop_name=face['stop_name'], berth_id=face.get('berth_id'), berth_name=face.get('berth_name'),
			reports=dict(total=int(len(own)), by_class=_counts(own['vessel_class']), by_day=by_day),
			lowest_level_navd88_m=float(own['level_navd88_m'].min()) if len(own) else None,
			cells_lowered=int(mask.sum()), max_lowering_m=float(amounts.max()) if amounts.size else 0.,
			mean_lowering_m=float(amounts.mean()) if amounts.size else 0.,
			box=box_result(before, after, floor, water, transform, nodata, face, own, low, required)))
	return rows


# ---- the slice build ----------------------------------------------------------------------------------------


def tracks_path():
	return paths.ais_processed_dir() / 'nycferry_tracks.parquet'


def build_slice(slice_, out, bed, profile, low, required, log=print):
	"""The floor on the slice's merged bed: writes the rasters, the reports used and the per-landing report, and returns
	the new bed and the provenance for depth_merged_navd88.json. low is the window's lowest water and required the
	approach-depth requirement, for the report's before and after."""
	out = Path(out)
	if not tracks_path().is_file():
		raise FileNotFoundError(f'{tracks_path()} is missing: the operational depth floor needs the AIS extract; run the full '
			'build (ferrydata build --slice ' + slice_.name + ', without --skip ais) once')
	transform, nodata = profile['transform'], profile.get('nodata')
	faces = face_anchors(json.loads((out / 'landing_faces.geojson').read_text(encoding='utf-8'))['features'])
	tracks = pd.read_parquet(tracks_path(), columns=['mmsi', 'time_utc', 'lat', 'lon', 'north_m', 'east_m', 'sog_mps'])
	inverse = ~transform

	def inside(lat, lon):
		col, row = inverse.a * lon + inverse.b * lat + inverse.c, inverse.d * lon + inverse.e * lat + inverse.f
		return (col >= 0) & (col < bed.shape[1]) & (row >= 0) & (row < bed.shape[0])

	reports, dropped = select_reports(tracks, identity_classes(), faces, inside)
	inputs = cached_inputs()
	levels = {day: day_levels(day, slice_.water_level_stations, inputs) for day in sorted(set(reports['day']))}
	classes = vessel_classes()
	reports = required_beds(reports, levels, classes)
	valid = np.isfinite(bed) if nodata is None else np.isfinite(bed) & (bed != nodata)
	open_water = valid & ~fixed_mask(out, bed.shape, transform)
	items = hazards(out, slice_)
	water = open_water & ~hazard_mask(items, bed.shape, transform)
	reports.to_csv(out / REPORTS_FILE, index=False, lineterminator='\n')
	sources = {'identity-audit.csv': manifest.sha256_file(identity_audit_path()),
		'marinecadastre-ais.json': manifest.sha256_file(paths.MANIFEST_DIR / 'marinecadastre-ais.json'),
		'coops-environment.json': manifest.sha256_file(paths.MANIFEST_DIR / 'coops-environment.json'),
		**{c.file: c.sha256 for _, c in sorted(classes.items())}, REPORTS_FILE: manifest.sha256_file(out / REPORTS_FILE)}
	new, provenance = write_outputs(out, bed, water, profile, reports, classes, sources)
	with rasterio.open(out / FLOOR_FILE) as dataset:
		floor = dataset.read(1).astype('float64')
	rows = landing_rows(reports, bed, new, floor, water, transform, nodata, faces, low, required)
	hazards_left_out = left_out(items, bed, reports, transform, bed.shape, open_water, nodata, faces)
	provenance['left_out'] = left_out_summary(hazards_left_out)
	gauges = {day: [dict(id=g.id, north_m=g.north_m, east_m=g.east_m, source=g.source, source_sha256=g.source_sha256) for g in levels[day].gauges]
		for day in sorted(levels)}
	report = dict(method=METHOD, region=slice_.name, lowest_water_navd88_m=low, required_depth_m=required,
		dropped_reports=dropped, gauges=gauges, landings=rows, hazards_left_out=hazards_left_out,
		**{k: v for k, v in provenance.items() if k != 'method'})
	(out / REPORT_FILE).write_text(json.dumps(report, indent='\t', ensure_ascii=False, allow_nan=False) + '\n', encoding='utf-8')
	(out / REPORT_MD).write_text(report_markdown(report), encoding='utf-8')
	provenance['report_file'] = REPORT_FILE
	provenance['report_sha256'] = manifest.sha256_file(out / REPORT_FILE)
	log(f"  {FLOOR_FILE}: {provenance['reports']['total']} T/K reports; {provenance['cells_lowered']} cells lowered, "
		f"largest {provenance['max_lowering_m']:.2f} m; {REPORT_FILE}")
	return new, provenance


def _depth(value):
	return '-' if value is None else f'{value:.2f}'


def report_markdown(report):
	"""The per-landing report for people: what each landing's reports did to its bed and its approach box."""
	p = report['parameters']
	lines = ['# Operational depth floor (operational-ais)', '',
		f"Method: for each T- or K-class AIS report within {p['radius_m']:g} m of any selectable berth face's midpoint at SOG at most "
		f"{p['max_sog_mps']:g} m/s on the 28 scenario days, the bed is at most L - D - M: L the simulation's water level there and "
		f"then, D the class's maxDraft (T {p['max_draft_m']['T']:g} m, K {p['max_draft_m']['K']:g} m), M = {p['keel_margin_m']:g} m, "
		f"a keel margin that is an estimate. Each report covers the water cells within half its class length overall "
		f"(T {p['footprint_radius_m']['T']:.2f} m, K {p['footprint_radius_m']['K']:.2f} m); a cell takes the deepest requirement. "
		'No bed is raised, land and fixed structures (ENC LNDARE, dry SLCONS areas) are never touched, charted hazards (always-submerged '
		'structures, ENC obstruction and wreck areas) keep their bed in every cell they touch, and nodata is never filled.', '',
		f"Level model: {p['level_model']}.", '',
		f"{report['reports']['total']} reports ({', '.join(f'{k} {v}' for k, v in report['reports']['by_class'].items())}); "
		f"{report['cells_lowered']} cells lowered, largest {report['max_lowering_m']:.2f} m, mean {report['mean_lowering_m']:.2f} m. "
		'Left out: ' + ', '.join(f"{k.replace('_', ' ')} {v}" for k, v in report['dropped_reports'].items()) + '.', '',
		f"Approach boxes: the validation's 30 m box at its lowest water, {report['lowest_water_navd88_m']:.3f} m NAVD88, "
		f"against the unchanged {report['required_depth_m']:.2f} m.", '',
		'| stop | landing | reports (T/K) | days | lowest level (m) | cells lowered | largest / mean lowering (m) | box min before (m) | box min after (m) | box cells covered | box verdict after |',
		'|---|---|---|---|---|---|---|---|---|---|---|']
	for row in report['landings']:
		box, classes = row['box'], row['reports']['by_class']
		lines.append(f"| {row['stop_id']} | {row['stop_name']} | {row['reports']['total']} ({classes.get('T', 0)}/{classes.get('K', 0)}) | "
			f"{len(row['reports']['by_day'])} | {_depth(row['lowest_level_navd88_m'])} | {row['cells_lowered']} | "
			f"{row['max_lowering_m']:.2f} / {row['mean_lowering_m']:.2f} | {_depth(box['before']['min_depth_m'])} | "
			f"{_depth(box['after']['min_depth_m'])} | {box['covered_cells']} of {box['cells']} | {box['after']['verdict']} |")
	lines += ['', '## Boxes that still fail', '']
	failing = [row for row in report['landings'] if row['box']['after']['verdict'] != 'PASS']
	for row in failing:
		box, worst = row['box'], row['box'].get('worst_cell', {})
		lines.append(f"- **{row['stop_name']}** (stop {row['stop_id']}): {box['failing_cells']} of {box['cells']} cells short of "
			f"{report['required_depth_m']:.2f} m after the floor, {box['failing_uncovered_cells']} of them beyond every report's footprint; "
			f"the lowest level any report covering the box saw is {_depth(box['lowest_covering_level_navd88_m'])} m NAVD88. "
			f"Its worst cell, {worst.get('out_m', 0):.1f} m out and {worst.get('along_m', 0):.1f} m along the face, is "
			f"{_depth(worst.get('depth_after_m'))} m deep at the lowest water ({worst.get('covering_reports', 0)} covering reports"
			+ (f", the lowest of them at {worst['lowest_covering_level_navd88_m']:.2f} m" if worst.get('lowest_covering_level_navd88_m') is not None else '') + ').')
	if not failing:
		lines.append('None.')
	lines += ['', '## Charted hazards left out', '',
		'Every cell an always-submerged structure (WATLEV 3) or an ENC obstruction or wreck area touches keeps its bed. '
		'These are the ones a report\'s footprint reached, with the lowering their cells would have had:', '',
		'| layer | id | kind | nearest landing (m) | cells touched | cells reached | would have lowered | largest / mean (m) |', '|---|---|---|---|---|---|---|---|']
	reached = sorted((h for h in report['hazards_left_out'] if h['cells_reached']), key=lambda h: (-h['cells_would_lower'], h['layer'], h['id']))
	lines += [f"| {h['layer']} | {h['id']} | {h['kind']} | {h['nearest_landing']} ({h['distance_to_face_m']:.0f}) | {h['cells']} | {h['cells_reached']} | "
		f"{h['cells_would_lower']} | {h['max_would_lower_m']:.2f} / {h['mean_would_lower_m']:.2f} |" for h in reached] or ['| - | none reached | | | | | | |']
	untouched = len(report['hazards_left_out']) - len(reached)
	lines += ['', f"{untouched} other charted hazards in the slice lie beyond every footprint."]
	lines += ['', '## Reports by day', '', '| stop | ' + ' | '.join(sorted({d for r in report['landings'] for d in r['reports']['by_day']})) + ' |']
	days = sorted({d for r in report['landings'] for d in r['reports']['by_day']})
	lines.append('|---|' + '---|' * len(days))
	for row in report['landings']:
		cells = [row['reports']['by_day'].get(d, {}) for d in days]
		lines.append(f"| {row['stop_id']} | " + ' | '.join('/'.join(f'{k}{v}' for k, v in c.items()) or '-' for c in cells) + ' |')
	lines += ['', 'Notes: ' + ' '.join(report['notes']), '']
	return '\n'.join(lines)


# ---- opt-in harbor candidate (the slice path above is unchanged) --------------------------------------------


def build_harbor_tile(source, destination, bed, profile, reports, classes, sources, items):
	"""Apply the existing operational-ais rule to one harbor tile in a separate destination.

	The caller supplies the globally selected reports (including reports just across tile boundaries)
	and the tile's charted hazards. This function never writes the source tile or a slice output.
	"""
	destination = Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	valid = np.isfinite(bed) & (bed != profile.get('nodata'))
	water = valid & ~fixed_mask(source, bed.shape, profile['transform']) & ~hazard_mask(items, bed.shape, profile['transform'])
	new, provenance = write_outputs(destination, bed, water, profile, reports, classes, sources)
	_write(destination / 'depth_merged_navd88.tif', new, profile,
		{'vertical_datum': 'NAVD88', 'units': 'm', 'positive': 'up', 'method': METHOD})
	return new, provenance
