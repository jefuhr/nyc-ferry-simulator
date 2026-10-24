"""Reproducible local evidence generation: python -m calibration [--fetch-currents]."""

import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

from ferrydata import paths
from calibration.analysis import KNOT, correct_current, extract_events
from calibration.inputs import attach_currents, attach_landings, classify, digest, fetch_currents


def plot_cadence(ax, intervals, model_summary_path):
	try:
		model = json.loads(model_summary_path.read_text())
	except FileNotFoundError as exc:
		raise FileNotFoundError(f'{model_summary_path} is required; run python -m calibration.simulator first') from exc
	baseline, = [row for row in model['sensitivity'] if row['factor'] == 1.]
	crossing_s = baseline['time_to_25_kn_s']
	if crossing_s is None or not np.isfinite(crossing_s) or crossing_s < 0:
		raise ValueError('baseline model summary must contain a finite time to 25 kn')
	ax.hist(intervals[(intervals > 0) & (intervals < 300)], bins=np.arange(0,305,5))
	ax.axvline(crossing_s, color='red', label=f'Model 0–25 kn: {crossing_s:.1f} s')
	ax.set(xlabel='Report interval (s)', ylabel='Adjacent report pairs', title='Source cadence, all candidate vessels')
	ax.legend()


def main():
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument('--fetch-currents', action='store_true')
	parser.add_argument('--radius-m', type=float, default=750)
	parser.add_argument('--out', type=Path, default=paths.REPO_ROOT / 'artifacts/calibration')
	parser.add_argument('--model-summary', type=Path, default=paths.REPO_ROOT / 'artifacts/calibration/model-summary.json',
		help='Simulator summary for cadence comparison; generate with python -m calibration.simulator first')
	args = parser.parse_args()
	args.out.mkdir(parents=True, exist_ok=True)
	if args.fetch_currents:
		fetch_currents()
	roster_path = paths.ais_processed_dir() / 'candidate_roster.csv'
	tracks_path = paths.ais_processed_dir() / 'nycferry_tracks.parquet'
	identity = classify(pd.read_csv(roster_path))
	identity.to_csv(args.out / 'identities.csv', index=False)
	tracks = pd.read_parquet(tracks_path)
	tracks['class'] = tracks.mmsi.map(identity.set_index('mmsi')['class'])
	all_dt = tracks.groupby('mmsi').time_utc.diff().dt.total_seconds().dropna()
	summary = dict(input_reports=len(tracks), input_vessels=tracks.mmsi.nunique(),
		inputs_sha256={str(p.relative_to(paths.REPO_ROOT)): digest(p) for p in [roster_path, tracks_path]},
		all_interval_quantiles_s=all_dt.quantile([0,.01,.05,.5,.95,.99,1]).to_dict(),
		duplicate_intervals=int((all_dt == 0).sum()), intervals_le_10_s=int((all_dt <= 10).sum()),
		heading_available=int(tracks.heading_deg.between(0,359).sum()),
		identity_counts=identity['class'].value_counts().to_dict(),
		radius_m=args.radius_m, physical_fit_status='not_identifiable_without_controls', parameters_changed=[])
	classified = tracks[tracks['class'] != 'unclassified'].copy()
	print(f'{len(classified)} classified reports; attaching local current proxies', flush=True)
	points = correct_current(attach_currents(attach_landings(classified), args.radius_m))
	points.to_parquet(args.out / 'corrected-tracks.parquet', index=False)
	summary['by_class'] = {}
	all_events, all_profiles = [], []
	for class_, group in points.groupby('class'):
		print(f'extracting {class_}: {len(group)} reports', flush=True)
		events, profiles = extract_events(group)
		events['class'] = class_
		profiles['class'] = class_
		all_events.append(events)
		all_profiles.append(profiles)
		counts = events.groupby(['kind', 'accepted']).size()
		steady = events.query("kind == 'steady' and accepted").mean_stw_mps / KNOT
		dt = group.groupby('mmsi').time_utc.diff().dt.total_seconds().dropna()
		summary['by_class'][class_] = dict(reports=len(group), vessels=group.mmsi.nunique(),
			current_covered=int(group.current_covered.sum()), heading_available=int(group.heading_deg.between(0,359).sum()),
			interval_quantiles_s=dt.quantile([.01,.5,.99]).to_dict(),
			events={f'{kind}:{accepted}': int(count) for (kind, accepted), count in counts.items()},
			rejections=events.loc[~events.accepted, 'reason'].value_counts().to_dict(),
			steady_speed_kn_quantiles=steady.quantile([.05,.5,.95]).dropna().to_dict())
		print(summary['by_class'][class_], flush=True)
	events = pd.concat(all_events, ignore_index=True)
	profiles = pd.concat(all_profiles, ignore_index=True)
	events.to_csv(args.out / 'events.csv', index=False)
	profiles.to_csv(args.out / 'arrival-samples.csv', index=False)
	(args.out / 'summary.json').write_text(json.dumps(summary, indent='\t') + '\n')
	fig, axes = plt.subplots(1, 2, figsize=(12, 4), layout='constrained')
	plot_cadence(axes[0], all_dt, args.model_summary)
	for class_, group in events.query("kind == 'steady' and accepted").groupby('class'):
		axes[1].hist(group.mean_stw_mps / KNOT, bins=np.arange(14,32,.5), histtype='step', label=class_)
	axes[1].axvline(26, color='red', label='T model full ahead, before = after')
	axes[1].set(xlabel='Speed through water proxy (kn)', ylabel='Accepted steady windows', title='Operational envelopes; controls unknown')
	axes[1].legend()
	fig.savefig(args.out / 'cadence-and-speed.png', dpi=160)
	plt.close(fig)
	fig, ax = plt.subplots(figsize=(7, 4), layout='constrained')
	for class_, group in profiles.groupby('class'):
		usable = group[np.isfinite(group.stw_mps) & group.landing_distance_m.le(1200)]
		ax.scatter(usable.landing_distance_m, usable.stw_mps / KNOT, s=2, alpha=.08, label=class_)
	ax.set(xlabel='Distance to arrival landing (m)', ylabel='Sampled water-speed proxy (kn)',
		title='Arrival samples: cadence-rejected, not resolved deceleration trials')
	ax.legend()
	fig.savefig(args.out / 'arrival-samples.png', dpi=160)
	plt.close(fig)
	print(f'Wrote evidence to {args.out}', flush=True)


if __name__ == '__main__':
	main()
