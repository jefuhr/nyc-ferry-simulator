"""Pure time, vector and missing-observation boundary conversions."""
import math
from datetime import date, datetime, timedelta, timezone
from zoneinfo import ZoneInfo

from ferrydata import coops

WEEKS = ('2024-10-07', '2025-01-13', '2025-04-14', '2025-07-14')
DAYS = tuple((date.fromisoformat(w) + timedelta(days=d)).isoformat() for w in WEEKS for d in range(7))
COVERAGE = (-3600, 90000)
CADENCE = 360


def epoch_utc(day):
	return datetime.combine(date.fromisoformat(day), datetime.min.time(), ZoneInfo('America/New_York')).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')


def epoch(day):
	return datetime.fromisoformat(epoch_utc(day).replace('Z', '+00:00'))


def current_components(velocity_major_cm_s, flood_deg, ebb_deg):
	speed, direction, _ = coops.current_vector(velocity_major_cm_s, flood_deg, ebb_deg)
	return coops.components(speed, direction)


def finite(value):
	try:
		value = float(value)
	except (TypeError, ValueError):
		return None
	return value if math.isfinite(value) else None


def wind_components(speed, from_deg):
	speed, direction = finite(speed), finite(from_deg)
	if speed is None or speed < 0:
		return None
	if speed == 0:
		return (0., 0.)
	if direction is None or not 0 <= direction <= 360:
		return None
	return coops.components(speed, direction + 180.)


def gap_notes(times, label='series', coverage=COVERAGE):
	times = sorted(set(float(t) for t in times))
	if not times:
		return [f'{label}: no valid samples.']
	notes = []
	for a, b in zip(times, times[1:]):
		if b - a > CADENCE:
			policy = 'exceeds 1800 s; reader holds nearest value' if b - a > 1800 else 'reader interpolates linearly'
			notes.append(f'{label}: gap {a:g} to {b:g} s ({b-a:g} s); {policy}.')
	if times[0] > coverage[0] or times[-1] < coverage[1]:
		notes.append(f'{label}: valid coverage [{times[0]:g}, {times[-1]:g}] s does not span [{coverage[0]}, {coverage[1]}]; reader holds nearest endpoint.')
	return notes


def fill_water_levels(observed, predicted, times):
	rows = []
	for t in times:
		value = finite(observed.get(t))
		source = 'observed'
		if value is None:
			value = finite(predicted.get(t))
			source = 'predicted'
		if value is not None:
			rows.append((t, value, source))
	count = sum(row[2] == 'predicted' for row in rows)
	times = list(times)
	notes = [f'{count} missing observed water levels filled with predicted NAVD88 values.'] if count else []
	return rows, notes + gap_notes([r[0] for r in rows], coverage=(min(times), max(times)))


def phase_peak(values):
	values = [v for value in values if (v := finite(value)) is not None]
	return max(values) if values else None
