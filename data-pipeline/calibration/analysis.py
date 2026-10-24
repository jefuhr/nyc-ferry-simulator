"""SI/NED event extraction. Current predictions are local proxies, not measured flow."""

import numpy as np
import pandas as pd

KNOT = 1852 / 3600
DYNAMIC_MIN_DURATION_S = 15
EVENT_COLUMNS = ['kind', 'mmsi', 'start_utc', 'end_utc', 'duration_s', 'samples',
	'max_gap_s', 'accepted', 'reason', 'mean_stw_mps', 'start_stw_mps', 'end_stw_mps',
	'mean_acceleration_mps2', 'yaw_deg_s', 'speed_loss_fraction']


def angle_delta(degrees):
	return (np.diff(degrees) + 180) % 360 - 180


def interpolate_current(times, source_times, north, east, max_gap_s=360):
	"""Linear component interpolation, exact samples allowed, no extrapolation/gap fill."""
	times, source_times = np.asarray(times), np.asarray(source_times)
	if len(source_times) < 2 or np.any(np.diff(source_times) <= 0):
		raise ValueError('current source must have at least two strictly increasing times')
	right = np.searchsorted(source_times, times, side='left')
	i = np.clip(right, 1, len(source_times) - 1)
	valid = (times >= source_times[0]) & (times <= source_times[-1])
	exact = source_times[np.clip(right, 0, len(source_times) - 1)] == times
	valid &= exact | ((source_times[i] - source_times[i - 1]) <= max_gap_s)
	return tuple(np.where(valid, np.interp(times, source_times, values), np.nan) for values in (north, east))


def correct_current(points):
	out = points.copy()
	cog = np.radians(out.cog_deg)
	valid = (out.current_covered & out.sog_mps.between(0, 102.2 * KNOT)
		& out.cog_deg.between(0, 359.9999))
	out['water_north_mps'] = (out.sog_mps * np.cos(cog) - out.current_north_mps).where(valid)
	out['water_east_mps'] = (out.sog_mps * np.sin(cog) - out.current_east_mps).where(valid)
	out['stw_mps'] = np.hypot(out.water_north_mps, out.water_east_mps)
	heading = np.radians(out.heading_deg.where(out.heading_deg.between(0, 359)))
	out['surge_mps'] = out.water_north_mps * np.cos(heading) + out.water_east_mps * np.sin(heading)
	return out


def _event(kind, frame):
	dt = frame.time_utc.diff().dt.total_seconds().iloc[1:].to_numpy()
	duration = (frame.time_utc.iloc[-1] - frame.time_utc.iloc[0]).total_seconds()
	reasons = []
	if np.any(dt <= 0):
		reasons.append('time')
	gap_limit = 90 if kind == 'steady' else 10
	if len(frame) < (3 if kind == 'steady' else 4) or np.any(dt > gap_limit):
		reasons.append('cadence')
	if duration < (120 if kind == 'steady' else DYNAMIC_MIN_DURATION_S):
		reasons.append('duration')
	if not frame.current_covered.all() or not np.isfinite(frame.stw_mps).all():
		reasons.append('current_or_velocity')
	positions = frame[['north_m', 'east_m']].to_numpy()
	# Displacement consistency rejects gross jumps; 30 m is a tolerance, not a claimed AIS accuracy.
	displacement = np.linalg.norm(np.diff(positions, axis=0), axis=1)
	max_travel = np.maximum(frame.sog_mps.to_numpy()[:-1], frame.sog_mps.to_numpy()[1:]) * dt + 30
	if not np.isfinite(positions).all() or np.any(displacement > max_travel):
		reasons.append('position')
	yaw = np.nan
	if kind == 'turn':
		if not frame.heading_deg.between(0, 359).all():
			reasons.append('heading')
		else:
			delta = angle_delta(frame.heading_deg.to_numpy())
			yaw = np.sum(delta) / duration if duration > 0 else np.nan
			if np.any(np.abs(delta) > 90) or abs(np.sum(delta)) < 10:
				reasons.append('heading_change')
	start, end = frame.stw_mps.iloc[0], frame.stw_mps.iloc[-1]
	return dict(kind=kind, mmsi=int(frame.mmsi.iloc[0]), start_utc=str(frame.time_utc.iloc[0]),
		end_utc=str(frame.time_utc.iloc[-1]), duration_s=duration, samples=len(frame),
		max_gap_s=float(np.max(dt)), accepted=not reasons, reason=','.join(reasons),
		mean_stw_mps=float(frame.stw_mps.mean()), start_stw_mps=start, end_stw_mps=end,
		mean_acceleration_mps2=(end - start) / duration if duration > 0 else np.nan,
		yaw_deg_s=yaw, speed_loss_fraction=1 - end / start if start > 0 else np.nan)


def extract_events(points):
	"""Disjoint steady windows and short turn windows; dock-to-transit episode audit.

	Departure/arrival detection uses SOG thresholds (2 kn dock, 10 m/s transit), so
	missing currents do not silently remove candidate events from rejection counts.
	No lever, rudder or vessel-class inference is made. Arrival profiles use distance
	to the same terminal identified at the final low-speed report (caller supplies it).
	"""
	events, profiles = [], []
	for _, vessel in points.groupby('mmsi', sort=True):
		vessel = vessel.sort_values('time_utc', kind='stable').reset_index(drop=True)
		# Long gaps split voyages. Duplicate times stay visible to the rejection gate.
		segments = (vessel.time_utc.diff().dt.total_seconds().fillna(0) > 300).cumsum()
		for _, frame in vessel.groupby(segments, sort=True):
			frame = frame.reset_index(drop=True)
			slow = (frame.sog_mps <= 2 * KNOT) & (frame.landing_distance_m <= 75)
			fast = frame.sog_mps >= 10
			for kind, reverse in [('departure', False), ('arrival', True)]:
				view = frame.iloc[::-1].reset_index(drop=True) if reverse else frame
				dock = slow.iloc[::-1].to_numpy() if reverse else slow.to_numpy()
				transit = fast.iloc[::-1].to_numpy() if reverse else fast.to_numpy()
				anchor = None
				for i in range(len(view)):
					if dock[i]:
						anchor = i
					elif transit[i] and anchor is not None:
						episode = view.iloc[anchor:i + 1]
						if reverse:
							episode = episode.iloc[::-1]
						if abs((view.time_utc.iloc[i] - view.time_utc.iloc[anchor]).total_seconds()) <= 300:
							entry = _event(kind, episode)
							events.append(entry)
							if reverse:
								profile = episode[['mmsi', 'time_utc', 'stw_mps', 'landing_distance_m']].copy()
								profile['arrival_utc'] = entry['end_utc']
								profile['accepted'] = entry['accepted']
								profile['reason'] = entry['reason']
								if 'landing_north_m' in episode:
									profile['landing_distance_m'] = np.hypot(episode.north_m - episode.landing_north_m.iloc[-1], episode.east_m - episode.landing_east_m.iloc[-1])
								profiles.append(profile)
						anchor = None
			# Non-overlapping windows avoid counting each sample as an independent event.
			i = 0
			while i + 2 < len(frame):
				end = i + 2
				while end + 1 < len(frame) and (frame.time_utc.iloc[end] - frame.time_utc.iloc[i]).total_seconds() < 120:
					end += 1
				window = frame.iloc[i:end + 1]
				if ((window.sog_mps >= 8).all() and (window.landing_distance_m > 250).all()
					and window.sog_mps.max() - window.sog_mps.min() <= .5
					and window.cog_deg.between(0, 359.9999).all()
					and np.abs(angle_delta(window.cog_deg.to_numpy())).sum() <= 10):
					entry = _event('steady', window)
					if np.isfinite(window.stw_mps).all() and window.stw_mps.max() - window.stw_mps.min() > .7:
						entry['accepted'] = False
						entry['reason'] = ','.join(filter(None, [entry['reason'], 'water_speed_variation']))
					events.append(entry)
				i = end + 1
			i = 0
			while i + 3 < len(frame):
				# Keep four intervals where available, extending dense samples to the
				# duration gate. Adjacent windows share only one endpoint.
				end = min(i + 4, len(frame) - 1)
				while end + 1 < len(frame) and (frame.time_utc.iloc[end] - frame.time_utc.iloc[i]).total_seconds() < DYNAMIC_MIN_DURATION_S:
					end += 1
				window = frame.iloc[i:end + 1]
				if (window.sog_mps >= 5).all():
					# Course can nominate a candidate, but never substitutes for heading as yaw.
					course_change = np.abs(angle_delta(window.cog_deg.to_numpy())).sum()
					heading_change = np.abs(angle_delta(window.heading_deg.to_numpy())).sum()
					if course_change >= 10 or heading_change >= 10:
						events.append(_event('turn', window))
				i = end
	return pd.DataFrame(events, columns=EVENT_COLUMNS), (pd.concat(profiles, ignore_index=True) if profiles else
		pd.DataFrame(columns=['mmsi', 'time_utc', 'stw_mps', 'landing_distance_m', 'arrival_utc']))


def fit_grid(values, predictions, observations, *, controls_known, seed=42, resamples=200):
	"""Fit a one-parameter simulator grid only to observations with known controls.

	Bootstrap is a repeatability diagnostic, not proof of parameter identifiability.
	Real AIS rows are correlated; callers must aggregate independent trials first.
	"""
	if not controls_known:
		raise ValueError('physical fit requires known controls; AIS speed alone is insufficient')
	values, predictions, observations = map(np.asarray, (values, predictions, observations))
	if (values.ndim != 1 or observations.ndim != 1 or len(observations) < 2
		or predictions.shape != (len(values), len(observations))
		or not all(np.isfinite(x).all() for x in (values, predictions, observations))):
		raise ValueError('finite grid predictions and at least two observations required')
	loss = (predictions - observations[None, :]) ** 2
	best = int(np.argmin(loss.mean(axis=1)))
	rng = np.random.default_rng(seed)
	selected = []
	for _ in range(resamples):
		indices = rng.integers(0, len(observations), len(observations))
		selected.append(float(values[np.argmin(loss[:, indices].mean(axis=1))]))
	return {'value': float(values[best]), 'rmse': float(np.sqrt(loss[best].mean())),
		'bootstrap_interval': np.quantile(selected, [.025, .975]).tolist(), 'seed': seed}
