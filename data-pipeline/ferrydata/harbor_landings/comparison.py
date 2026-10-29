"""Human review table alongside complete per-berth/hull comparison CSV."""
from pathlib import Path

import pandas as pd


def write(before, after, destination):
	destination = Path(destination)
	keys = ['stop_id', 'stop_name', 'berth_id', 'berth_name', 'hull']
	before, after = [pd.read_csv(p, dtype={'stop_id': str}) for p in (before, after)]
	joined = before.merge(after, on=keys, suffixes=('_before', '_after'), validate='one_to_one')
	if len(joined) != 52:
		raise ValueError('comparison requires all 26 berths and both hulls')
	joined.to_csv(destination / 'before-after.csv', index=False, lineterminator='\n')
	lines = ['| landing / berth | accepted T before → after | accepted K before → after | worst UKC T before → after (m) | worst UKC K before → after (m) | lowest accepted level T / K after (m NAVD88) |',
		'|---|---:|---:|---:|---:|---:|']

	def number(x):
		return 'unknown' if pd.isna(x) else f'{x:.3f}'

	for key, group in joined.groupby(keys[:-1], sort=False):
		t, k = [group[group.hull == hull].iloc[0] for hull in ('t-class', 'k-class')]
		lines.append(f'| {key[1]} / {key[3]} (`{key[2]}`) | {t.accepted_before} → {t.accepted_after} | '
			f'{k.accepted_before} → {k.accepted_after} | {number(t.worst_ukc_m_before)} → {number(t.worst_ukc_m_after)} | '
			f'{number(k.worst_ukc_m_before)} → {number(k.worst_ukc_m_after)} | '
			f'{number(t.lowest_accepted_water_navd88_m_after)} / {number(k.lowest_accepted_water_navd88_m_after)} |')
	body = '\n'.join(lines) + '\n'
	(destination / 'before-after.md').write_text(body, encoding='utf-8')
	return body
