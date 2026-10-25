"""Checksum-first raw cache access; only new CO-OPS files enter our own cache."""
import json
import os
import re
from datetime import datetime, timedelta, timezone
from urllib.parse import urlencode

import pandas as pd

from ferrydata import coops, fetcher, geodesy, manifest, paths
from ferrydata.environment import series

SOURCE = 'coops-environment'


def checked_bytes(m, name, path):
	if not m.verify(name, path):
		raise fetcher.ChecksumError(f'{path}: missing or checksum differs from {m.path}; refusing raw input')
	return path.read_bytes()


def _restore_pinned_file(m, name, destination, validate):
	"""Restore evicted bytes from their recorded URL without touching provenance."""
	entry = m.files[name]
	if (type(entry.get('size_bytes')) is not int or entry['size_bytes'] < 0
		or not isinstance(entry.get('sha256'), str) or re.fullmatch('[0-9a-f]{64}', entry['sha256']) is None
		or not isinstance(entry.get('url'), str) or not entry['url']):
		raise fetcher.ChecksumError(f'{name}: invalid pinned size, SHA-256 or URL')
	destination.parent.mkdir(parents=True, exist_ok=True)
	part = destination.with_name(destination.name + '.part')
	try:
		fetcher._download(entry['url'], part)
		if not m.verify(name, part):
			raise fetcher.ChecksumError(f'{name}: restored bytes differ from pinned size or SHA-256')
		validate(part)
		os.replace(part, destination)
	finally:
		if part.exists():
			part.unlink()
	return 'downloaded'


def timestamp(value):
	return datetime.fromisoformat(value.replace('Z', '+00:00')).replace(tzinfo=timezone.utc)


def location(station):
	lat, lon = float(station['lat']), float(station.get('lon', station.get('lng')))
	north, east = geodesy.to_local(lat, lon)
	return dict(id=station['id'], name=station['name'], lat=lat, lon=lon, north_m=float(north), east_m=float(east))


def numeric_rows(payload, key='data'):
	return {timestamp(r['t']): value for r in payload.get(key, []) if (value := series.finite(r.get('v'))) is not None}


class Inputs:
	def __init__(self, log=print):
		self.manifest = manifest.Manifest.load(SOURCE)
		self.manifest.describe(description='Scenario-day CO-OPS observations and prediction gap fills for ferrysim-env/1',
			homepage='https://tidesandcurrents.noaa.gov/', license=coops.LICENSE,
			terms_url='https://tidesandcurrents.noaa.gov/disclaimers.html',
			notes=['UTC, metric; water levels and predictions use NAVD (NAVD88). Raw API no-data JSON is preserved, never converted to zero observations.'])
		self.cache = paths.raw_dir(SOURCE)
		self.calibration = manifest.Manifest.load('coops-calibration')
		self.loaded = {}
		self.current_frames = {}
		self.downloads = 0
		self.log = log

	def get(self, name, url):
		if name not in self.loaded:
			p = self.cache / name
			# Even a cache file without provenance is refused. Fetcher's ordinary untracked
			# cache adoption path is deliberately unavailable at this boundary.
			if p.exists():
				checked_bytes(self.manifest, name, p)
			validate = lambda p: coops.parse_currents_csv(p.read_text()) if name.endswith('.csv') else json.loads(p.read_text())
			if name in self.manifest.files and not p.exists():
				status = _restore_pinned_file(self.manifest, name, p, validate)
			else:
				status = fetcher.fetch_file(self.manifest, name, url, p, validate=validate)
			self.downloads += status == 'downloaded'
			self.log(f'  {name}: {status}', flush=True)
			self.loaded[name] = checked_bytes(self.manifest, name, p)
		return self.loaded[name]

	def json(self, name, url):
		return json.loads(self.get(name, url))

	def station(self, station):
		name = f'station_{station}.json'
		return self.json(name, f'{coops.MDAPI}/stations/{station}.json')['stations'][0]

	def datums(self, station):
		return self.json(f'datums_{station}.json', f'{coops.MDAPI}/stations/{station}/datums.json?units=metric')

	def window(self, product, station, week):
		start = series.epoch(week) + timedelta(seconds=series.COVERAGE[0])
		end = series.epoch(week) + timedelta(days=6, seconds=series.COVERAGE[1])
		params = dict(product=product, station=station, begin_date=start.strftime('%Y%m%d %H:%M'),
			end_date=end.strftime('%Y%m%d %H:%M'), time_zone='gmt', units='metric', format='json')
		if product in ('water_level', 'predictions'):
			params['datum'] = 'NAVD'
		if product == 'predictions':
			params['interval'] = '6'
		name = f'{product}_{station}_{week}.json'
		return self.json(name, f'{coops.API}?{urlencode(params)}'), name

	def reference(self, name):
		return dict(source=name, source_sha256=self.manifest.files[name]['sha256'])

	def current(self, station, day):
		week = next(w for w in series.WEEKS if 0 <= (datetime.fromisoformat(day)-datetime.fromisoformat(w)).days < 7)
		name = f'{station["id"]}_{week}.csv'
		cache_path = paths.raw_dir('coops-calibration') / name
		frame, reference = pd.DataFrame(), {}
		if cache_path.exists():
			raw = checked_bytes(self.calibration, name, cache_path)
			if name not in self.current_frames:
				self.current_frames[name] = coops.parse_currents_csv(raw.decode('utf-8'))
			frame = self.current_frames[name]
			seconds = (pd.to_datetime(frame['time_utc'], utc=True) - pd.Timestamp(series.epoch(day))).dt.total_seconds()
			frame = frame.assign(t_s=seconds)
			frame = frame[(frame.t_s >= series.COVERAGE[0]) & (frame.t_s <= series.COVERAGE[1])]
			reference = dict(source=name, source_sha256=self.calibration.files[name]['sha256'])
		if frame.empty or frame.t_s.min() > series.COVERAGE[0] or frame.t_s.max() < series.COVERAGE[1]:
			# Missing cache coverage is fetched for this entire day, preserving a single
			# provenance file per station in the v1 manifest.
			params = dict(product='currents_predictions', station=station['id'],
				begin_date=(series.epoch(day)+timedelta(seconds=series.COVERAGE[0])).strftime('%Y%m%d %H:%M'),
				end_date=(series.epoch(day)+timedelta(seconds=series.COVERAGE[1])).strftime('%Y%m%d %H:%M'),
				interval='6', time_zone='gmt', units='metric', format='csv')
			name = f'currents_{station["id"]}_{day}.csv'
			frame = coops.parse_currents_csv(self.get(name, f'{coops.API}?{urlencode(params)}').decode('utf-8'))
			frame['t_s'] = (pd.to_datetime(frame['time_utc'], utc=True)-pd.Timestamp(series.epoch(day))).dt.total_seconds()
			reference = self.reference(name)
		if frame.empty or not all(frame[['t_s', 'north_mps', 'east_mps', 'depth_m']].notna().all()):
			raise ValueError(f'{name}: missing current values')
		depths, bins = sorted(set(frame.depth_m)), sorted(set(frame['bin']))
		if len(depths) != 1 or len(bins) != 1:
			raise ValueError(f'{name}: bin or depth changes within day')
		metadata = dict(**location(station), kind='harmonic-prediction', bin_depth_m=float(depths[0]), **reference)
		notes = [f'{station["id"]}: API-default bin {bins[0]}, depth {depths[0]} m; bin selection is not independently verified as shallowest.']
		notes += series.gap_notes(frame.t_s, f'current {station["id"]}')
		peaks = {phase: series.phase_peak(frame.loc[frame.phase == phase, 'speed_mps']) for phase in ('flood', 'ebb')}
		for phase, peak in peaks.items():
			if peak is None:
				notes.append(f'{station["id"]}: no {phase} phase occurs in this day’s covered predictions; peak is not present.')
		rows = [(station['id'], float(r.t_s), float(r.north_mps), float(r.east_mps)) for r in frame.itertuples()]
		return metadata, rows, notes, dict(bin=int(bins[0]), depth_m=float(depths[0]), **peaks)

	def water_levels(self, station, day, week):
		observed_payload, name = self.window('water_level', station['id'], week)
		observed = numeric_rows(observed_payload)
		epoch = series.epoch(day)
		times = list(range(series.COVERAGE[0], series.COVERAGE[1]+1, series.CADENCE))
		observed = {t: observed.get(epoch+timedelta(seconds=t)) for t in times}
		predicted, prediction_name = {}, None
		if any(value is None for value in observed.values()):
			payload, prediction_name = self.window('predictions', station['id'], week)
			predicted = numeric_rows(payload, 'predictions')
			predicted = {t: predicted.get(epoch+timedelta(seconds=t)) for t in times}
		rows, notes = series.fill_water_levels(observed, predicted, times)
		if not rows:
			return None, [], [f'Water gauge {station["id"]} ({station["name"]}) unavailable: no NAVD88 observations or predictions for {day}; omitted.'], {}
		metadata = dict(**location(station), **self.reference(name))
		if prediction_name:
			# Keep the primary source contract while documenting the second raw input
			# without inventing a synthetic raw-file checksum.
			notes.append(f'Water {station["id"]} prediction source {prediction_name}, SHA-256 {self.manifest.files[prediction_name]["sha256"]}.')
		rows = [(station['id'], float(t), value, source) for t, value, source in rows]
		stats = dict(rows=len(rows), observed=sum(r[3] == 'observed' for r in rows),
			predicted=sum(r[3] == 'predicted' for r in rows), min_m=min(r[2] for r in rows), max_m=max(r[2] for r in rows),
			first_s=rows[0][1], last_s=rows[-1][1])
		return metadata, rows, [f'Water {station["id"]}: {note}' for note in notes], stats

	def wind(self, station, day, week):
		payload, name = self.window('wind', station['id'], week)
		epoch = series.epoch(day)
		rows = []
		for row in payload.get('data', []):
			t = (timestamp(row['t']) - epoch).total_seconds()
			if not series.COVERAGE[0] <= t <= series.COVERAGE[1]:
				continue
			vector = series.wind_components(row.get('s'), row.get('d'))
			if vector is None:
				continue
			gust = series.finite(row.get('g'))
			rows.append((station['id'], float(t), float(vector[0]), float(vector[1]), gust if gust is not None and gust >= 0 else None))
		if not rows:
			return None, [], [f'Wind {station["id"]} ({station["name"]}): no valid observations for {day}; raw response {name} SHA-256 {self.manifest.files[name]["sha256"]}.'], {}
		metadata = dict(**location(station), sensor_height_m=None, **self.reference(name))
		notes = series.gap_notes([r[1] for r in rows], f'Wind {station["id"]}')
		speeds = [float((r[2]**2+r[3]**2)**.5) for r in rows]
		gusts = [r[4] for r in rows if r[4] is not None]
		stats = dict(rows=len(rows), min_mps=min(speeds), max_mps=max(speeds), max_gust_mps=max(gusts) if gusts else None,
			first_s=rows[0][1], last_s=rows[-1][1])
		return metadata, sorted(rows), notes, stats
