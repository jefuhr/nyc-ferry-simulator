"""Assemble independently reproducible scenario days from verified source bytes."""
from pathlib import Path

from ferrydata import paths, slices
from ferrydata.environment import depth, pack, report, series
from ferrydata.environment.sources import Inputs

WIND_ORDER = ('8518750', '8516945', '8530973', '8519483')


def build(region, days, log=print):
	if region != 'lower-east-river':
		raise ValueError('environment v1 currently supports lower-east-river only')
	if not days or any(day not in series.DAYS for day in days):
		raise ValueError('choose one or more of the 28 AIS scenario days')
	slice_ = slices.get_slice(region)
	source_dir = paths.processed_dir(region)
	root = source_dir / 'env'
	if not (source_dir / 'depth_merged_navd88.tif').exists():
		raise ValueError('missing slice depth; run python data-pipeline/ferrydata build --slice lower-east-river --skip ais first')
	inputs = Inputs(log)
	water_stations, skipped_datums = [], []
	for station_id in slice_.water_level_stations:
		station, datums = inputs.station(station_id), inputs.datums(station_id)
		if any(d['name'] == 'NAVD88' for d in datums.get('datums', [])):
			water_stations.append(station)
		else:
			skipped_datums.append(f'Water gauge {station_id} ({station["name"]}) omitted: no NAVD88 datum; no datum substitution.')
	depth_manifest, depth_stats = depth.build(slice_, source_dir, root / 'depth')
	depth_hash = pack.read_digest(root / 'depth')
	daily = {}
	for day in sorted(set(days)):
		log(f'[build] {region}/{day}', flush=True)
		week = next(w for w in series.WEEKS if 0 <= (series.epoch(day)-series.epoch(w)).days < 7)
		notes = list(skipped_datums) + [
			'Dimond Reef NYH1919 uses the API-default 9.4 m bin (Bin 9), not a verified near-surface current; this deep bin is a known weakness.',
			'Current stations are harmonic predictions and spatial proxies, not local observed velocity or a resolved cross-channel/shear field.',
			'Wind sensor height is unknown; gusts are preserved but not applied by the v1 core.',
		]
		station_metadata = dict(current_stations=[], water_level_stations=[], wind_stations=[])
		rows = dict(currents=[], water_levels=[], wind=[])
		stats = dict(currents={}, water_levels={}, wind={})
		for station in sorted(inputs.calibration.header['stations'], key=lambda s: s['id']):
			metadata, current, station_notes, peaks = inputs.current(station, day)
			station_metadata['current_stations'].append(metadata)
			rows['currents'].extend(current)
			notes.extend(station_notes)
			stats['currents'][station['id']] = dict(rows=len(current), first_s=current[0][1], last_s=current[-1][1], **peaks)
		for station in water_stations:
			metadata, levels, station_notes, water_stats = inputs.water_levels(station, day, week)
			notes.extend(station_notes)
			if metadata:
				station_metadata['water_level_stations'].append(metadata)
				rows['water_levels'].extend(levels)
				stats['water_levels'][station['id']] = water_stats
		if not station_metadata['water_level_stations'] or station_metadata['water_level_stations'][0]['id'] != '8518750':
			raise ValueError(f'{day}: required reference water gauge The Battery is unavailable')
		for station_id in WIND_ORDER:
			station = inputs.station(station_id)
			metadata, wind, station_notes, wind_stats = inputs.wind(station, day, week)
			notes.extend(station_notes)
			if metadata:
				station_metadata['wind_stations'].append(metadata)
				rows['wind'] = wind
				stats['wind'][station_id] = wind_stats
				notes.append(f'Uniform v1 wind uses {station_id} ({station["name"]}), the first available station in Battery, Kings Point, Robbins Reef, Bergen Point order.')
				if station_id == '8516945':
					notes.append('Kings Point is on Long Island Sound and more exposed than the East River; its uniform wind is a spatial proxy.')
				break
		if not station_metadata['wind_stations']:
			notes.append('No wind station has valid observations for this day; empty wind.csv represents still air in the core coverage report.')
		metadata = pack.write_day(root=root, region=region, day=day, depth_digest=depth_hash,
			**station_metadata, **rows, notes=notes)
		day_hash = pack.read_digest(root / 'days' / day)
		daily[day] = dict(digest=day_hash, **stats, notes=metadata['notes'])
		log(f'  day digest {day_hash}', flush=True)
	digests = pack.verify(root)
	result = dict(region=region, days=daily, depth=depth_manifest, depth_digest=depth_hash, depth_stats=depth_stats,
		new_fetch_count=inputs.downloads, total_raw_files=len(inputs.manifest.files),
		total_pack_bytes=sum(p.stat().st_size for p in root.rglob('*') if p.is_file()))
	report.write(result, root, paths.REPO_ROOT / 'artifacts' / 'environment-pack')
	log(f'[verify] {len(digests)} packs; {result["total_pack_bytes"]} bytes; {inputs.downloads} new fetches', flush=True)
	return result
