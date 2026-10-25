"""Leave-one-out check of the channel field against the station blend, at every current station on every pack day.

Each station in turn is left out: the field is rebuilt from the others on the same geometry, and both the field and the
inverse-distance-squared blend of the others (StationBlendCurrentField) predict it at its own position. The station's
own series is the truth.

Leave-one-out and the parameter sweep run on a LOO_CELL_M lattice: each left-out station needs its own weight solve,
which takes under a minute at 20 m and about fifteen at 10 m, and interpolation between stations works at the scale of
kilometres. The maps and the landings use the region's own lattice, the one the packs store.
"""
import csv
import json
from dataclasses import replace
from pathlib import Path

import numpy as np

from ferrydata.environment import field, series as env_series, subordinate

PALETTE = dict(field='#2a78d6', baseline='#eb6834', truth='#0b0b0b', secondary='#52514e', grid='#e4e3df', surface='#fcfcfb')
DIRECTION_FLOOR_MPS = 0.25
LOO_CELL_M = 20.


def station_series(root):
	"""Stations (from the first day's manifest), days, sample times and each station's (days, times, 2) velocities."""
	days = sorted(p.name for p in (Path(root) / 'days').iterdir() if p.is_dir())
	manifest = json.loads((Path(root) / 'days' / days[0] / 'manifest.json').read_text())
	stations = sorted((field.Station(s['id'], s['north_m'], s['east_m']) for s in manifest['current_stations']), key=lambda s: s.id)
	series, times = {s.id: [] for s in stations}, None
	for day in days:
		rows = {s.id: [] for s in stations}
		with (Path(root) / 'days' / day / 'currents.csv').open(newline='') as stream:
			for row in csv.DictReader(stream):
				rows[row['station_id']].append((float(row['t_s']), float(row['north_mps']), float(row['east_mps'])))
		for sid, values in rows.items():
			values = np.array(values)
			if times is None:
				times = values[:, 0]
			if not np.array_equal(values[:, 0], times):
				raise ValueError(f'{day} {sid}: sample times differ between stations or days')
			series[sid].append(values[:, 1:])
	return stations, days, times, {sid: np.array(v) for sid, v in series.items()}


def blend(stations, series, north, east):
	"""StationBlendCurrentField at one point: inverse distance squared, exact within 1e-6 m of a station."""
	d2 = np.array([(s.north - north) ** 2 + (s.east - east) ** 2 for s in stations])
	if np.any(d2 <= 1e-12):
		return series[stations[int(np.argmin(d2))].id]
	w = (1. / d2) / np.sum(1. / d2)
	return sum(wk * series[s.id] for wk, s in zip(w, stations))


def predict(basis, series, station):
	"""The field from `basis` at a station's position, for every sample of `series` (id -> (days, times, 2))."""
	shape = next(iter(series.values())).shape
	flat = {sid: v.reshape(-1, 2) for sid, v in series.items()}
	count = flat[next(iter(flat))].shape[0]
	return field.evaluate(basis, flat, np.full(count, station.north), np.full(count, station.east)).reshape(shape)


def metrics(prediction, truth):
	error = prediction - truth
	vector = np.sqrt(np.sum(error ** 2, axis=-1))
	true_speed = np.hypot(truth[..., 0], truth[..., 1])
	speed = np.hypot(prediction[..., 0], prediction[..., 1])
	moving = true_speed >= DIRECTION_FLOOR_MPS
	angle = np.degrees(np.abs(np.angle(np.exp(1j * (np.arctan2(prediction[..., 1], prediction[..., 0])
		- np.arctan2(truth[..., 1], truth[..., 0]))))))
	return dict(samples=int(truth[..., 0].size), rms_vector_error_mps=float(np.sqrt(np.mean(vector ** 2))),
		speed_mae_mps=float(np.mean(np.abs(speed - true_speed))), speed_bias_mps=float(np.mean(speed - true_speed)),
		direction_median_deg=float(np.median(angle[moving])), direction_p90_deg=float(np.percentile(angle[moving], 90)),
		true_rms_speed_mps=float(np.sqrt(np.mean(true_speed ** 2))))


# Parameters varied one at a time around the chosen ones, on the same leave-one-out weights.
SENSITIVITY = (
	('chosen', {}),
	('no depth factor (exponent 0)', dict(depth_exponent=0.)),
	('depth exponent 1/3', dict(depth_exponent=1. / 3.)),
	('depth exponent 2/3 (Manning)', dict(depth_exponent=2. / 3.)),
	('depth averaged over 100 m', dict(depth_smoothing_m=100.)),
	('depth averaged over 400 m', dict(depth_smoothing_m=400.)),
	('no sheltering', dict(shelter_threshold=1e-12)),
	('sheltering threshold 0.85 (first proposed)', dict(shelter_threshold=.85)),
	('sheltering threshold 1.1', dict(shelter_threshold=1.1)),
	('sheltering smoothed over 400 m (first proposed)', dict(smoothing_m=400.)),
	('sheltering smoothed over 1600 m', dict(smoothing_m=1600.)),
	('pattern conveyance exponent 1 (depth-weighted flux)', dict(conveyance_exponent=1.)),
	('pattern conveyance exponent 5/3 (Manning)', dict(conveyance_exponent=5. / 3.)),
	('cross taper 20 m (none beyond the cells next to the shore)', dict(cross_taper_m=20.)),
	('cross taper 120 m', dict(cross_taper_m=120.)),
	('no station reach beyond the disk', dict(station_reach_m=41.)),
	('station reach 240 m', dict(station_reach_m=240.)),
	('across reach 120 m', dict(across_reach_m=120.)),
	('across reach 1000 m', dict(across_reach_m=1000.)),
	('across reach 2000 m', dict(across_reach_m=2000.)),
	('across part everywhere, shore taper only (as first proposed)', dict(across_reach_m=1e9)),
)


def leave_one_out(geometry, stations, series, log=print):
	"""Per station: its truth, the field and baseline predictions without it, each (days, times, 2), and the basis."""
	results = {}
	for held in stations:
		others = [s for s in stations if s.id != held.id]
		basis = field.with_stations(geometry, others)
		subset = {s.id: series[s.id] for s in others}
		results[held.id] = dict(truth=series[held.id], field=predict(basis, subset, held),
			baseline=blend(others, subset, held.north, held.east), basis=basis,
			weights={basis.stations[k].id: float(w) for k, w in enumerate(station_weights(basis, held)) if w > 0})
		log(f'  left out {held.id}: weight iterations {basis.iterations["weights"]}', flush=True)
	return results


def sensitivity(lattice, water, bed, parameters, stations, series, results, log=print):
	"""Mean and pooled leave-one-out errors of the field with one parameter changed at a time."""
	rows = []
	for name, change in SENSITIVITY:
		varied = replace(parameters, **change)
		geometry = field.geometry(lattice, water, bed, varied)
		predictions, truths, per_station = [], [], []
		for held in stations:
			others = [s for s in stations if s.id != held.id]
			basis = field.with_stations(geometry, others, solved=results[held.id]['basis'])
			prediction = predict(basis, {s.id: series[s.id] for s in others}, held)
			predictions.append(prediction)
			truths.append(series[held.id])
			per_station.append(metrics(prediction, series[held.id]))
		pooled = metrics(np.concatenate(predictions), np.concatenate(truths))
		rows.append(dict(name=name, change=change, pooled=pooled,
			mean={k: float(np.mean([m[k] for m in per_station])) for k in per_station[0] if k != 'samples'}))
		log(f'  {name}: pooled RMS {pooled["rms_vector_error_mps"]:.4f} m/s', flush=True)
	return rows


def station_weights(basis, station):
	"""The truncated weights the field gives each of its stations at a point (bilinear over the four cells)."""
	totals = np.zeros(len(basis.stations))
	for r, c, w in field.corners(basis.lattice, np.array([station.north]), np.array([station.east])):
		for slot, weight in zip(basis.slots[r[0], c[0]], basis.slot_weights[r[0], c[0]]):
			if slot != field.EMPTY:
				totals[slot] += w[0] * weight
	return totals


def write(results, days, times, destination, frames):
	"""loo.json, loo.md and the plots under destination; frames maps station id to its flood-oriented frame."""
	destination = Path(destination)
	destination.mkdir(parents=True, exist_ok=True)
	table = {sid: {m: metrics(r[m], r['truth']) for m in ('field', 'baseline')} for sid, r in results.items()}
	ids = sorted(table)
	pooled = {m: metrics(np.concatenate([results[s][m] for s in ids]), np.concatenate([results[s]['truth'] for s in ids])) for m in ('field', 'baseline')}
	summary = dict(stations=table, pooled=pooled, weights={s: results[s]['weights'] for s in ids}, days=days,
		samples_per_station=int(len(days) * len(times)), direction_floor_mps=DIRECTION_FLOOR_MPS)
	(destination / 'loo.json').write_text(json.dumps(summary, indent='\t', sort_keys=True) + '\n', encoding='utf-8')
	lines = ['| Station | True RMS speed m/s | RMS vector error m/s, field / baseline | Speed MAE m/s, field / baseline | Speed bias m/s, field / baseline | Direction error median °, field / baseline | Direction error 90th percentile °, field / baseline | Field weights of the others |',
		'|---|---:|---:|---:|---:|---:|---:|---|']
	for sid in ids + ['pooled']:
		f, b = (pooled['field'], pooled['baseline']) if sid == 'pooled' else (table[sid]['field'], table[sid]['baseline'])
		w = '' if sid == 'pooled' else ', '.join(f'{k} {v:.2f}' for k, v in sorted(results[sid]['weights'].items(), key=lambda kv: -kv[1]) if v >= 0.01)
		lines.append(f'| {sid} | {f["true_rms_speed_mps"]:.3f} | {f["rms_vector_error_mps"]:.3f} / {b["rms_vector_error_mps"]:.3f} | '
			f'{f["speed_mae_mps"]:.3f} / {b["speed_mae_mps"]:.3f} | {f["speed_bias_mps"]:+.3f} / {b["speed_bias_mps"]:+.3f} | '
			f'{f["direction_median_deg"]:.1f} / {b["direction_median_deg"]:.1f} | {f["direction_p90_deg"]:.1f} / {b["direction_p90_deg"]:.1f} | {w} |')
	(destination / 'loo.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
	_bars(table, ids, destination)
	_series(results, days, times, destination, frames)
	return summary


def _style(ax):
	ax.set_facecolor(PALETTE['surface'])
	ax.grid(axis='x', color=PALETTE['grid'], linewidth=.8)
	ax.set_axisbelow(True)
	for side in ('top', 'right'):
		ax.spines[side].set_visible(False)
	for side in ('left', 'bottom'):
		ax.spines[side].set_color(PALETTE['secondary'])
	ax.tick_params(colors=PALETTE['secondary'], labelcolor=PALETTE['truth'])


def _bars(table, ids, destination):
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	panels = [('rms_vector_error_mps', 'RMS vector error (m/s)'), ('direction_p90_deg', 'Direction error, 90th percentile (°)')]
	fig, axes = plt.subplots(1, 2, figsize=(12, 6.5), layout='constrained', facecolor=PALETTE['surface'])
	y = np.arange(len(ids))
	for ax, (key, label) in zip(axes, panels):
		for offset, method, name in ((-.2, 'field', 'Channel field'), (.2, 'baseline', 'Inverse-distance baseline')):
			values = [table[s][method][key] for s in ids]
			ax.barh(y + offset, values, height=.36, color=PALETTE[method], label=name, edgecolor=PALETTE['surface'], linewidth=2)
		ax.set_yticks(y, ids)
		ax.invert_yaxis()
		ax.set_xlabel(label, color=PALETTE['truth'])
		_style(ax)
	axes[0].legend(frameon=False, loc='lower right')
	fig.suptitle('Leave-one-out at each station, 28 scenario days', color=PALETTE['truth'])
	fig.savefig(destination / 'loo-errors.png', dpi=130, metadata={'Software': 'ferrydata.environment'})
	plt.close(fig)


def _series(results, days, times, destination, frames, day='2025-07-14', show=('NYH1920', 'NYH1921', 'ACT3426', 'NYH1927')):
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	if day not in days:
		day = days[0]
	d = days.index(day)
	show = [s for s in show if s in results]
	fig, axes = plt.subplots(len(show), 1, figsize=(11, 2.6 * len(show)), sharex=True, sharey=True, layout='constrained', facecolor=PALETTE['surface'])
	for ax, sid in zip(np.atleast_1d(axes), show):
		truth = results[sid]['truth'][d]
		# Signed speed along the station's frame: positive on the flood.
		major = np.asarray(frames[sid], dtype=float) / np.hypot(*frames[sid])
		for method, name, colour, width in (('truth', 'NOAA prediction', PALETTE['truth'], 2.), ('field', 'Channel field', PALETTE['field'], 2.), ('baseline', 'Inverse-distance baseline', PALETTE['baseline'], 2.)):
			ax.plot(times / 3600., results[sid][method][d] @ major, color=colour, linewidth=width, label=name)
		ax.axhline(0., color=PALETTE['secondary'], linewidth=.8)
		ax.set_ylabel(f'{sid}\nm/s, + flood', color=PALETTE['truth'])
		_style(ax)
		ax.grid(axis='y', color=PALETTE['grid'], linewidth=.8)
	np.atleast_1d(axes)[0].legend(frameon=False, ncol=3, loc='upper right')
	np.atleast_1d(axes)[-1].set_xlabel(f'Hours since local midnight, {day}', color=PALETTE['truth'])
	fig.suptitle('Left-out station: its own prediction against the field and the baseline built without it', color=PALETTE['truth'])
	fig.savefig(destination / 'loo-series.png', dpi=130, metadata={'Software': 'ferrydata.environment'})
	plt.close(fig)


def subordinate_check(inputs, station_ids, days, destination):
	"""Each subordinate series against what CO-OPS publishes for the station itself (MAX_SLACK events): event times and
	speeds from the shifted reference events, the series at the published event times, and its zero crossings against
	the published slack times. Writes subordinates.md and returns the statistics."""
	low, high = env_series.COVERAGE
	sample_times = np.arange(low, high + 1, env_series.CADENCE, dtype=float)
	table = {}
	for sid in station_ids:
		dt, dv, at_slack, at_max, crossings, events = [], [], [], [], [], 0
		flood_dv, ebb_dv = [], []
		for day in days:
			_, derivation, _, velocity, x = inputs.subordinate_current(sid, day)
			mine = [e for e in subordinate.shift(x['reference_events'], x['offsets'], source=x['reference_source']) if low <= e.t <= high]
			published = [e for e in x['published'] if low <= e.t <= high]
			if len(mine) != len(published) or any(a.kind.split()[0] != b.kind for a, b in zip(mine, published)):
				raise ValueError(f'{sid} {day}: shifted events do not pair with the published ones')
			events += len(published)
			dt += [a.t - b.t for a, b in zip(mine, published)]
			dv += [a.velocity - b.velocity for a, b in zip(mine, published)]
			flood_dv += [a.velocity - b.velocity for a, b in zip(mine, published) if b.kind == 'flood']
			ebb_dv += [a.velocity - b.velocity for a, b in zip(mine, published) if b.kind == 'ebb']
			values = subordinate.series(np.array([e.t for e in published]), x['reference_t'], x['reference_v'], x['reference_events'], x['offsets'],
				source=x['reference_source'])
			for v, e in zip(values, published):
				(at_slack if e.kind == 'slack' else at_max).append(v - e.velocity)
			slacks = np.array([e.t for e in published if e.kind == 'slack'])
			sign = np.sign(velocity)
			for i in np.nonzero(sign[:-1] * sign[1:] < 0)[0]:
				t0 = sample_times[i] - velocity[i] * (sample_times[i + 1] - sample_times[i]) / (velocity[i + 1] - velocity[i])
				if slacks.size:
					crossings.append(t0 - slacks[np.argmin(np.abs(slacks - t0))])
		dt, dv, at_slack, at_max, crossings, flood_dv, ebb_dv = map(np.array, (dt, dv, at_slack, at_max, crossings, flood_dv, ebb_dv))
		table[sid] = dict(days=len(days), events=events, reference=derivation['reference_station'], reference_bin=derivation['reference_bin'],
			event_time_max_abs_s=float(np.abs(dt).max()), event_speed_max_abs_mps=float(np.abs(dv).max()),
			max_flood_speed_max_abs_mps=float(np.abs(flood_dv).max()), max_ebb_speed_max_abs_mps=float(np.abs(ebb_dv).max()),
			max_floods=int(flood_dv.size), max_ebbs=int(ebb_dv.size),
			series_at_slack_max_abs_mps=float(np.abs(at_slack).max()), series_at_maxima_max_abs_mps=float(np.abs(at_max).max()),
			series_at_maxima_rms_mps=float(np.sqrt(np.mean(at_max ** 2))), crossings=int(crossings.size),
			crossing_max_abs_s=float(np.abs(crossings).max()), crossing_mean_s=float(crossings.mean()))
	lines = ['| Station | Reference | Published events | Event time, max \\|Δ\\| s | Max flood speed, max \\|Δ\\| m/s (events) | Max ebb speed, max \\|Δ\\| m/s (events) | 6-min series at published slack, max \\|v\\| m/s | Series at published maxima, max \\|Δ\\| / RMS m/s | 6-min zero crossings vs published slack, max \\|Δ\\| / mean s |',
		'|---|---|---:|---:|---:|---:|---:|---:|---:|']
	for sid, r in table.items():
		lines.append(f'| {sid} | {r["reference"]} bin {r["reference_bin"]} | {r["events"]} | {r["event_time_max_abs_s"]:.0f} | '
			f'{r["max_flood_speed_max_abs_mps"]:.4f} ({r["max_floods"]}) | {r["max_ebb_speed_max_abs_mps"]:.4f} ({r["max_ebbs"]}) | '
			f'{r["series_at_slack_max_abs_mps"]:.4f} | {r["series_at_maxima_max_abs_mps"]:.4f} / {r["series_at_maxima_rms_mps"]:.4f} | '
			f'{r["crossing_max_abs_s"]:.0f} / {r["crossing_mean_s"]:+.0f} |')
	Path(destination).mkdir(parents=True, exist_ok=True)
	(Path(destination) / 'subordinates.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
	return table


def open_cell(basis, north, east):
	"""The nearest cell centre where nothing slows the flow (along 1) and beyond the shore taper: open channel."""
	lat = basis.lattice
	r, c = np.nonzero(basis.wet & (basis.along >= 1.) & (basis.shore_m >= basis.parameters.cross_taper_m))
	n, e = lat.origin_north + r * lat.cell, lat.origin_east + c * lat.cell
	k = int(np.argmin((n - north) ** 2 + (e - east) ** 2))
	return float(n[k]), float(e[k])


def face_ratios(basis, series, faces, offsets=(0., 5., 15.), wet_floor=field.WET_FLOOR):
	"""For each landing: RMS field speed over every sample of `series` at its primary-face midpoint and at `offsets` m
	along its outward normal, as fractions of the RMS speed at the nearest open-channel cell."""
	flat = {sid: v.reshape(-1, 2) for sid, v in series.items()}
	count = next(iter(flat.values())).shape[0]
	rows = {}
	for feature in sorted(faces['features'], key=lambda f: f['properties']['stop_name']):
		p = feature['properties']
		middle, normal = p['face_midpoint'], p['outward_normal_ne']
		points = [(middle['north_m'] + o * normal[0], middle['east_m'] + o * normal[1]) for o in offsets]
		reference = open_cell(basis, middle['north_m'], middle['east_m'])
		rms = []
		for n, e in points + [reference]:
			u = field.evaluate(basis, flat, np.full(count, n), np.full(count, e), wet_floor=wet_floor)
			rms.append(float(np.sqrt(np.mean(np.sum(u ** 2, axis=-1)))))
		rows[p['stop_name']] = dict(ratios=[x / rms[-1] for x in rms[:-1]], open_rms_mps=rms[-1],
			open_distance_m=float(np.hypot(reference[0] - middle['north_m'], reference[1] - middle['east_m'])))
	return rows


def landings(basis, series, days, times, destination, faces):
	"""landings.md: each landing's field speed as a fraction of the nearest open channel, and at the strongest flood and
	ebb of 2025-07-14 at NYH1920."""
	rows = face_ratios(basis, series, faces)
	lines = ['RMS field speed over all 28 days at each landing\'s primary-face midpoint, 5 m and 15 m off along its outward normal, '
		'as a fraction of the RMS speed at the nearest open-channel cell (where neither factor slows the flow).', '',
		'| Landing | Face midpoint | 5 m off | 15 m off | Nearest open channel: distance m, RMS speed m/s |', '|---|---:|---:|---:|---|']
	for name, r in rows.items():
		lines.append(f'| {name} | {r["ratios"][0]:.2f} | {r["ratios"][1]:.2f} | {r["ratios"][2]:.2f} | {r["open_distance_m"]:.0f}, {r["open_rms_mps"]:.2f} |')
	(Path(destination) / 'landings.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
	return rows


def maps(basis, stations, series, days, times, destination, extent, day='2025-07-14', reference='NYH1920'):
	"""The field and the baseline over `extent` (north0, north1, east0, east1) at the reference station's strongest flood
	and ebb of a day: speed shading, arrows every 160 m."""
	import matplotlib
	matplotlib.use('Agg')
	import matplotlib.pyplot as plt
	from matplotlib.colors import LinearSegmentedColormap
	d = days.index(day) if day in days else 0
	frame = dict(zip([s.id for s in basis.stations], basis.frames))[reference]
	along = series[reference][d] @ (np.asarray(frame) / np.hypot(*frame))
	moments = [('strongest flood', int(np.argmax(along))), ('strongest ebb', int(np.argmin(along)))]
	lat = basis.lattice
	r0, r1 = [int(round((x - lat.origin_north) / lat.cell)) for x in extent[:2]]
	c0, c1 = [int(round((x - lat.origin_east) / lat.cell)) for x in extent[2:]]
	rows, cols = np.arange(r0, r1 + 1), np.arange(c0, c1 + 1)
	north = lat.origin_north + rows * lat.cell
	east = lat.origin_east + cols * lat.cell
	n, e = np.meshgrid(north, east, indexing='ij')
	wet = basis.wet[r0:r1 + 1, c0:c1 + 1]
	ramp = LinearSegmentedColormap.from_list('speed', ['#cde2fb', '#86b6ef', '#3987e5', '#256abf', '#184f95', '#0d366b'])
	fig, axes = plt.subplots(2, 2, figsize=(13, 12.5), layout='constrained', facecolor=PALETTE['surface'])
	top = 0.
	panels = []
	for label, k in moments:
		at = {sid: np.repeat(v[d, k][None, :], n.size, axis=0) for sid, v in series.items()}
		channel = field.evaluate(basis, at, n.ravel(), e.ravel()).reshape(n.shape + (2,))
		baseline = np.zeros(n.shape + (2,))
		d2 = np.stack([(s.north - n) ** 2 + (s.east - e) ** 2 for s in stations])
		w = (1. / np.maximum(d2, 1e-12)) / np.sum(1. / np.maximum(d2, 1e-12), axis=0)
		for ws, st in zip(w, stations):
			baseline += ws[..., None] * series[st.id][d, k][None, None, :]
		for values in (channel, baseline):
			values[~wet] = np.nan
		top = max(top, float(np.nanmax(np.hypot(channel[..., 0], channel[..., 1]))), float(np.nanmax(np.hypot(baseline[..., 0], baseline[..., 1]))))
		panels.append((label, k, channel, baseline))
	for i, (label, k, channel, baseline) in enumerate(panels):
		for j, (name, values) in enumerate((('Channel field', channel), ('Inverse-distance baseline', baseline))):
			ax = axes[i, j]
			ax.set_facecolor('#e4e3df')
			speed = np.hypot(values[..., 0], values[..., 1])
			image = ax.imshow(speed, origin='lower', cmap=ramp, vmin=0., vmax=top,
				extent=(east[0] - lat.cell / 2, east[-1] + lat.cell / 2, north[0] - lat.cell / 2, north[-1] + lat.cell / 2))
			step = 8
			q = (slice(None, None, step), slice(None, None, step))
			ok = wet[q] & (speed[q] > 0.02)
			ax.quiver(e[q][ok], n[q][ok], values[..., 1][q][ok], values[..., 0][q][ok], color=PALETTE['truth'], scale=40., width=.0018)
			for st in stations:
				if extent[0] <= st.north <= extent[1] and extent[2] <= st.east <= extent[3]:
					ax.plot(st.east, st.north, marker='^', markersize=8, color='#eb6834', markeredgecolor=PALETTE['surface'])
					ax.annotate(st.id, (st.east, st.north), xytext=(5, 5), textcoords='offset points', fontsize=8, color=PALETTE['truth'])
			ax.set_title(f'{name}: {reference} {label}, {day} {times[k] / 3600.:.1f} h local', color=PALETTE['truth'], fontsize=10)
			ax.set_aspect('equal')
			ax.tick_params(colors=PALETTE['secondary'], labelcolor=PALETTE['truth'], labelsize=8)
	fig.colorbar(image, ax=axes, shrink=.6, label='current speed (m/s)')
	fig.savefig(Path(destination) / 'field-maps.png', dpi=110, metadata={'Software': 'ferrydata.environment'})
	plt.close(fig)


def run(shipped, loo, parameters, root, destination, log=print, extent=None, faces=None):
	"""Maps and landings on the shipped (lattice, water, bed); leave-one-out over every station and day of the packs
	under root, and the parameter sweep, on the loo (lattice, water, bed). Writes the report files."""
	stations, days, times, series = station_series(root)
	full = field.with_stations(field.geometry(*shipped, parameters), stations)
	frames = {s.id: tuple(f) for s, f in zip(full.stations, full.frames)}
	if extent is not None:
		maps(full, stations, series, days, times, destination, extent)
	if faces is not None:
		landings(full, series, days, times, destination, faces)
	del full
	geometry = field.geometry(*loo, parameters)
	results = leave_one_out(geometry, stations, series, log)
	summary = write(results, days, times, destination, frames)
	sweep = sensitivity(*loo, parameters, stations, series, results, log)
	summary['sensitivity'] = sweep
	(Path(destination) / 'loo.json').write_text(json.dumps(summary, indent='\t', sort_keys=True) + '\n', encoding='utf-8')
	lines = ['| Variant | Pooled RMS vector error m/s | Pooled speed bias m/s | Mean speed MAE m/s | Mean direction error, 90th percentile ° |',
		'|---|---:|---:|---:|---:|']
	for row in sweep:
		lines.append(f'| {row["name"]} | {row["pooled"]["rms_vector_error_mps"]:.4f} | {row["pooled"]["speed_bias_mps"]:+.4f} | '
			f'{row["mean"]["speed_mae_mps"]:.4f} | {row["mean"]["direction_p90_deg"]:.2f} |')
	(Path(destination) / 'sensitivity.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
	return summary
