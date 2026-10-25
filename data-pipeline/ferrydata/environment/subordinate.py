"""NOAA's subordinate current method: a subordinate station's predictions from its reference station's.

NOAA CO-OPS, "Current Predictions User Guide: Understanding NOAA Current Predictions" (v5), page 3: "Predictions at
subordinate stations are obtained by taking the predictions calculated at nearby reference stations and applying time
and speed corrections. These include four corrections for time (slack before flood, max flood, slack before ebb, and max
ebb) and two corrections for speed (max flood and max ebb)." The corrections are the MDAPI's currentpredictionoffsets:
sbfTimeAdjMin, mfcTimeAdjMin, sbeTimeAdjMin, mecTimeAdjMin, mfcAmpAdj and mecAmpAdj, with meanFloodDir and meanEbbDir.

CO-OPS publishes only the shifted events for a subordinate station (MAX_SLACK); its 6-minute interval is for harmonic
stations only, and its FAQ (https://tidesandcurrents.noaa.gov/noaacurrents/faq.html) calls the curve between the events
"a mathematical interpolation between the known maximum and slack values". Here that interpolation keeps the reference's
own curve: between two consecutive shifted events the reference is read at the linearly corresponding time between its
two events, and multiplied by the flood ratio where it floods and the ebb ratio where it ebbs. So the series passes
through every published event, slack stays slack, and the shape between events is the reference's harmonic prediction.

Times are seconds on any common time base; velocities are signed major-axis m/s, positive on the flood.
"""
import math
from dataclasses import dataclass

import numpy as np

MAXIMA = ('flood', 'ebb')


@dataclass(frozen=True)
class Offsets:
	station: str
	reference: str
	reference_bin: int
	slack_before_flood_s: float
	max_flood_s: float
	slack_before_ebb_s: float
	max_ebb_s: float
	flood_ratio: float
	ebb_ratio: float
	flood_dir: float
	ebb_dir: float


@dataclass(frozen=True)
class Event:
	t: float
	kind: str
	velocity: float


def offsets(station):
	"""Offsets of an MDAPI station record expanded with currentpredictionoffsets (stations/<id>.json)."""
	sid = station.get('id')
	if station.get('type') != 'S':
		raise ValueError(f'{sid}: type {station.get("type")!r} is not a subordinate current station')
	o = station.get('currentpredictionoffsets') or {}
	fields = ('refStationId', 'refStationBin', 'sbfTimeAdjMin', 'mfcTimeAdjMin', 'sbeTimeAdjMin', 'mecTimeAdjMin',
		'mfcAmpAdj', 'mecAmpAdj', 'meanFloodDir', 'meanEbbDir')
	missing = [f for f in fields if o.get(f) is None or o.get(f) == '']
	if missing:
		raise ValueError(f'{sid}: currentpredictionoffsets lacks {", ".join(missing)}')
	numbers = {f: float(o[f]) for f in fields[1:]}
	if not all(math.isfinite(v) for v in numbers.values()) or numbers['mfcAmpAdj'] <= 0 or numbers['mecAmpAdj'] <= 0:
		raise ValueError(f'{sid}: offsets must be finite with positive speed ratios')
	return Offsets(station=sid, reference=str(o['refStationId']), reference_bin=int(numbers['refStationBin']),
		slack_before_flood_s=60. * numbers['sbfTimeAdjMin'], max_flood_s=60. * numbers['mfcTimeAdjMin'],
		slack_before_ebb_s=60. * numbers['sbeTimeAdjMin'], max_ebb_s=60. * numbers['mecTimeAdjMin'],
		flood_ratio=numbers['mfcAmpAdj'], ebb_ratio=numbers['mecAmpAdj'],
		flood_dir=numbers['meanFloodDir'], ebb_dir=numbers['meanEbbDir'])


def classify(events):
	"""Names each slack by the maximum that follows it (or, for a last slack, the one that precedes it)."""
	events = sorted(events, key=lambda e: e.t)
	result = []
	for i, e in enumerate(events):
		kind = e.kind
		if kind == 'slack':
			following = next((x.kind for x in events[i + 1:] if x.kind in MAXIMA), None)
			preceding = next((x.kind for x in reversed(events[:i]) if x.kind in MAXIMA), None)
			if following is None and preceding is None:
				raise ValueError('a slack needs a maximum before or after it to know its phase')
			kind = 'slack before ' + (following or ('ebb' if preceding == 'flood' else 'flood'))
		elif kind not in MAXIMA and not kind.startswith('slack before '):
			raise ValueError(f'unknown current event type {e.kind!r}')
		result.append(Event(e.t, kind, e.velocity))
	return result


def shift(reference_events, o):
	"""The subordinate events: each reference event moved by its phase's time offset; maxima scaled, slack zero."""
	shifted = []
	for e in classify(reference_events):
		if e.kind == 'flood':
			shifted.append(Event(e.t + o.max_flood_s, e.kind, e.velocity * o.flood_ratio))
		elif e.kind == 'ebb':
			shifted.append(Event(e.t + o.max_ebb_s, e.kind, e.velocity * o.ebb_ratio))
		elif e.kind == 'slack before flood':
			shifted.append(Event(e.t + o.slack_before_flood_s, e.kind, 0.0))
		else:
			shifted.append(Event(e.t + o.slack_before_ebb_s, e.kind, 0.0))
	if any(b.t <= a.t for a, b in zip(shifted, shifted[1:])):
		raise ValueError(f'{o.station}: offsets move the reference events out of order')
	return shifted


def series(times, reference_t, reference_v, reference_events, o):
	"""Signed major-axis velocity (m/s) of the subordinate station at `times`."""
	times = np.asarray(times, dtype=float)
	reference_t = np.asarray(reference_t, dtype=float)
	reference_v = np.asarray(reference_v, dtype=float)
	if reference_t.size < 2 or np.any(np.diff(reference_t) <= 0) or not np.all(np.isfinite(reference_v)):
		raise ValueError(f'{o.reference}: reference series must be finite with increasing times')
	ref = sorted(reference_events, key=lambda e: e.t)
	sub = shift(ref, o)
	sub_t = np.array([e.t for e in sub])
	ref_t = np.array([e.t for e in ref])
	if times.size and (times.min() < sub_t[0] or times.max() > sub_t[-1]):
		raise ValueError(f'{o.station}: times {times.min():g} to {times.max():g} s are not bracketed by shifted events '
			f'{sub_t[0]:g} to {sub_t[-1]:g} s')
	k = np.clip(np.searchsorted(sub_t, times, side='right') - 1, 0, len(sub_t) - 2)
	fraction = (times - sub_t[k]) / (sub_t[k + 1] - sub_t[k])
	tau = ref_t[k] + fraction * (ref_t[k + 1] - ref_t[k])
	if times.size and (tau.min() < reference_t[0] or tau.max() > reference_t[-1]):
		raise ValueError(f'{o.station}: warped times {tau.min():g} to {tau.max():g} s fall outside the reference series '
			f'{reference_t[0]:g} to {reference_t[-1]:g} s')
	v = np.interp(tau, reference_t, reference_v)
	return np.where(v > 0, v * o.flood_ratio, v * o.ebb_ratio)


def components(velocity, o):
	"""(north, east) m/s of signed major-axis velocities: toward the flood direction when positive, the ebb's when negative."""
	velocity = np.asarray(velocity, dtype=float)
	direction = np.radians(np.where(velocity > 0, o.flood_dir, o.ebb_dir))
	speed = np.abs(velocity)
	return speed * np.cos(direction), speed * np.sin(direction)
