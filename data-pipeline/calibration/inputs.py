"""Read existing pipeline data; fetch only calibration current predictions on request."""

from datetime import date, datetime, timedelta, timezone
import hashlib
import json
from types import SimpleNamespace
from urllib.request import urlopen

import numpy as np
import pandas as pd

from ferrydata import ais, coops, geodesy, paths
from calibration.analysis import interpolate_current

MANIFEST = paths.MANIFEST_DIR / 'coops-calibration.json'
CACHE = paths.raw_dir('coops-calibration')
# Exact designer names, case/whitespace normalization only. No fuzzy spelling matches.
DESIGNS = {
	'IC16019': ('T', 26., 8., 'Lunchbox,Sunset Crossing,Urban Journey,Friendship Express,Waves of Wonder,Happy Hauler,Great Eagle,Owls Head,Munsea,McShiny,Starlight,Spring Mallard,Atlantic Compass,Ferry Godmother,River Sprinter,Cyclone Shark,Bay Hopper,Tooth Ferry,Time Traveller,White Sands'.split(',')),
	'IC18130': ('K-IC18130', 29.6, 8.5, ['Purpose', 'Curiosity']),
}


def classify(roster):
	"""Designer-name association, not an authoritative MMSI registry.

	Require dimensions within 1 m to allow integer AIS encoding. Keep name matches
	with conflicting dimensions in the audit, but exclude them from class envelopes.
	"""
	out = roster[['mmsi', 'name', 'length_m', 'beam_m', 'report_count']].copy()
	out['class'] = 'unclassified'
	out['design'] = ''
	out['source'] = ''
	out['identity_reason'] = 'no_exact_designer_name'
	for design, (class_, length, beam, names) in DESIGNS.items():
		matched = out.name.fillna('').str.strip().str.upper().isin([n.upper() for n in names])
		consistent = (out.length_m - length).abs().le(1) & (out.beam_m - beam).abs().le(1)
		out.loc[matched, 'design'] = design
		out.loc[matched, 'source'] = f'https://www.incatcrowther.com/ships/ferries/commuter/{design.lower()}/'
		out.loc[matched, 'identity_reason'] = 'dimension_conflict'
		out.loc[matched & consistent, 'class'] = class_
		out.loc[matched & consistent, 'identity_reason'] = 'exact_designer_name_and_integer_dimensions'
	return out


def stations():
	meta = paths.processed_dir('lower-east-river') / 'coops_stations.json'
	return [s for s in json.loads(meta.read_text())['stations'] if s.get('station_type') == 'harmonic']


def digest(path):
	return hashlib.sha256(path.read_bytes()).hexdigest()


def fetch_currents():
	CACHE.mkdir(parents=True, exist_ok=True)
	manifest = json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {'files': []}
	known = {f['name']: f for f in manifest['files']}
	manifest.update(source='NOAA CO-OPS harmonic current predictions', units='metric: cm/s converted to m/s',
		direction='toward', station_metadata_source='data/manifests/coops.json', stations=stations(),
		bin_selection='api_default',
		limitations=['Nearest harmonic station is a spatial proxy, not a local measured current.',
			'No subordinate-event interpolation; API-default prediction bins; shallowest selection not verified; no cross-channel/shear model.',
			'Dimond Reef metadata depths are 3.35/5.49/9.45 m; recorded API-default responses are 9.4 m, Bin 9. The bin/depth mapping is unresolved.'])
	for station in stations():
		for week in ais.WEEKS:
			window = SimpleNamespace(window_start=week, window_end=str(date.fromisoformat(week) + timedelta(days=7)))
			url = coops.currents_url(station['id'], window)
			name = f"{station['id']}_{week}.csv"
			path = CACHE / name
			if name in known:
				if not path.exists() or digest(path) != known[name]['sha256']:
					raise ValueError(f'cached checksum mismatch: {path}')
				frame = coops.parse_currents_csv(path.read_text())
			else:
				with urlopen(url, timeout=60) as response:
					data = response.read()
				frame = coops.parse_currents_csv(data.decode('utf-8'))
				if len(frame) < 2:
					raise ValueError(f'empty current response: {url}')
				path.write_bytes(data)
				known[name] = dict(name=name, url=url, station=station['id'], week=week,
					retrieved_utc=datetime.now(timezone.utc).isoformat(), sha256=digest(path), size_bytes=len(data),
					rows=len(frame), depth_m=sorted(frame.depth_m.unique().tolist()))
				print(f'fetched {name}: {len(frame)} rows', flush=True)
			# Refresh response identifiers even for verified older cache entries;
			# preserve request/provenance fields and never relabel the returned depth.
			known[name]['bin'] = sorted(frame['bin'].unique().tolist())
			manifest['files'] = sorted(known.values(), key=lambda f: f['name'])
			MANIFEST.write_text(json.dumps(manifest, indent='\t') + '\n')


def epoch_seconds(times):
	"""Normalize parquet millisecond and CSV microsecond timestamps explicitly."""
	return times.dt.as_unit('ns').astype('int64').to_numpy() / 1e9


def attach_currents(points, radius_m=750):
	manifest = json.loads(MANIFEST.read_text())
	metadata = manifest['stations']
	out = points.copy()
	distance = np.hypot(out.north_m.to_numpy()[:, None] - np.array([s['north_m'] for s in metadata]),
		out.east_m.to_numpy()[:, None] - np.array([s['east_m'] for s in metadata]))
	nearest = distance.argmin(axis=1)
	out['current_station'] = np.array([s['id'] for s in metadata])[nearest]
	out['current_distance_m'] = distance[np.arange(len(out)), nearest]
	out['current_north_mps'] = np.nan
	out['current_east_mps'] = np.nan
	for station in metadata:
		series = []
		for file in manifest['files']:
			if file['station'] == station['id']:
				path = CACHE / file['name']
				if digest(path) != file['sha256']:
					raise ValueError(f'current checksum mismatch: {path}')
				series.append(coops.parse_currents_csv(path.read_text()))
		frame = pd.concat(series).sort_values('time_utc').drop_duplicates('time_utc')
		mask = (out.current_station == station['id']) & (out.current_distance_m <= radius_m)
		north, east = interpolate_current(epoch_seconds(out.loc[mask, 'time_utc']),
			epoch_seconds(pd.to_datetime(frame.time_utc, utc=True)),
			frame.north_mps.to_numpy(), frame.east_mps.to_numpy())
		out.loc[mask, 'current_north_mps'] = north
		out.loc[mask, 'current_east_mps'] = east
	out['current_covered'] = np.isfinite(out.current_north_mps) & np.isfinite(out.current_east_mps)
	return out


def attach_landings(points):
	landings = ais.harbor_landings()
	north, east = geodesy.to_local(np.array([s['lat'] for s in landings]), np.array([s['lon'] for s in landings]))
	out = points.copy()
	distance = np.hypot(out.north_m.to_numpy()[:, None] - north, out.east_m.to_numpy()[:, None] - east)
	nearest = distance.argmin(axis=1)
	out['landing_distance_m'] = distance[np.arange(len(out)), nearest]
	out['landing_id'] = np.array([s['stop_id'] for s in landings])[nearest]
	out['landing_north_m'] = north[nearest]
	out['landing_east_m'] = east[nearest]
	return out
