"""NOAA's subordinate current method. Failure modes guarded:

- an offset applied to the wrong phase (slack before flood and slack before ebb swapped, max flood and max ebb swapped),
  or with the wrong sign or unit (minutes taken as seconds);
- a speed ratio applied to the wrong phase, or to slack;
- offsets read from the wrong MDAPI fields, or a station without offsets accepted;
- slack classified by the previous phase instead of the next;
- a series that misses the shifted events: not the scaled maximum at a maximum, not zero at slack;
- extrapolation beyond the bracketing events or the reference series, or shifted events that fall out of order;
- directions: flood and ebb swapped, or "from" instead of "toward".
"""
from datetime import datetime, timezone

import numpy as np
import pytest

from ferrydata.environment import subordinate

UTC = timezone.utc
EPOCH = datetime(2025, 7, 14, 4, tzinfo=UTC)

# The MDAPI entry for ACT3406 (Roosevelt Is., west of, off 75th Street), as currentpredictionoffsets returns it.
ACT3406 = {
	'id': 'ACT3406', 'name': 'Roosevelt Is., west of, off 75th Street', 'lat': 40.766700744628906, 'lng': -73.94999694824219,
	'type': 'S', 'currbin': 1, 'depth': None, 'depthType': 'U',
	'currentpredictionoffsets': {
		'id': 'ACT3406', 'refStationId': 'NYH1924', 'refStationBin': 6, 'meanFloodDir': 37.0, 'meanEbbDir': 215.0,
		'mfcTimeAdjMin': -6, 'sbeTimeAdjMin': -2, 'mecTimeAdjMin': 8, 'sbfTimeAdjMin': 1, 'mfcAmpAdj': 0.6, 'mecAmpAdj': 1.12,
	},
}

# NYH1924 bin 6 MAX_SLACK on 2025-07-14 (UTC, cm/s) and what the API publishes for ACT3406 bin 1 the same day.
REFERENCE = [
	('2025-07-14 01:26', 'flood', 209.4), ('2025-07-14 04:48', 'slack', -0.7), ('2025-07-14 07:44', 'ebb', -248.9),
	('2025-07-14 11:19', 'slack', 0.4), ('2025-07-14 13:54', 'flood', 199.0), ('2025-07-14 17:11', 'slack', 0.3),
	('2025-07-14 20:05', 'ebb', -252.9), ('2025-07-14 23:36', 'slack', 1.0),
]
PUBLISHED = [
	('2025-07-14 01:20', 'flood', 125.6), ('2025-07-14 04:46', 'slack', 0.0), ('2025-07-14 07:52', 'ebb', -278.7),
	('2025-07-14 11:20', 'slack', 0.0), ('2025-07-14 13:48', 'flood', 119.4), ('2025-07-14 17:09', 'slack', 0.0),
	('2025-07-14 20:13', 'ebb', -283.2), ('2025-07-14 23:37', 'slack', 0.0),
]


def seconds(text):
	return (datetime.strptime(text, '%Y-%m-%d %H:%M').replace(tzinfo=UTC) - EPOCH).total_seconds()


def events(rows):
	return [subordinate.Event(seconds(t), kind, v / 100.) for t, kind, v in rows]


def test_offsets_come_from_their_own_mdapi_fields():
	o = subordinate.offsets(ACT3406)
	assert (o.station, o.reference, o.reference_bin) == ('ACT3406', 'NYH1924', 6)
	assert (o.slack_before_flood_s, o.max_flood_s, o.slack_before_ebb_s, o.max_ebb_s) == (60., -360., -120., 480.)
	assert (o.flood_ratio, o.ebb_ratio, o.flood_dir, o.ebb_dir) == (0.6, 1.12, 37.0, 215.0)


@pytest.mark.parametrize('field', ['refStationId', 'mfcTimeAdjMin', 'mecAmpAdj', 'meanEbbDir'])
def test_a_station_without_complete_offsets_is_refused(field):
	payload = {**ACT3406, 'currentpredictionoffsets': {**ACT3406['currentpredictionoffsets'], field: None}}
	with pytest.raises(ValueError, match='ACT3406'):
		subordinate.offsets(payload)


def test_a_harmonic_station_is_not_a_subordinate():
	with pytest.raises(ValueError, match='not a subordinate'):
		subordinate.offsets({**ACT3406, 'type': 'H'})


def test_slack_takes_the_phase_that_follows_it():
	kinds = [e.kind for e in subordinate.classify(events(REFERENCE))]
	assert kinds == ['flood', 'slack before ebb', 'ebb', 'slack before flood', 'flood', 'slack before ebb', 'ebb', 'slack before flood']
	# The last slack has no following maximum: it is the one after the previous phase.
	assert subordinate.classify(events(REFERENCE[:2]))[1].kind == 'slack before ebb'


def test_shifted_reference_events_are_the_published_subordinate_events():
	shifted = subordinate.shift(events(REFERENCE), subordinate.offsets(ACT3406))
	published = events(PUBLISHED)
	assert [e.t for e in shifted] == [e.t for e in published]
	assert [e.kind.split()[0] for e in shifted] == [e.kind for e in published]
	# NOAA scales its unrounded reference speeds; ours are rounded to 0.1 cm/s, so allow that times the ratio.
	assert [e.velocity for e in shifted] == pytest.approx([e.velocity for e in published], abs=0.0012)
	assert all(e.velocity == 0.0 for e in shifted if e.kind.startswith('slack'))


def test_each_phase_gets_its_own_time_offset_and_ratio():
	o = subordinate.Offsets('S', 'R', 1, slack_before_flood_s=100., max_flood_s=200., slack_before_ebb_s=300., max_ebb_s=400.,
		flood_ratio=2., ebb_ratio=3., flood_dir=0., ebb_dir=180.)
	reference = [subordinate.Event(0., 'slack', 0.), subordinate.Event(1000., 'flood', 1.), subordinate.Event(2000., 'slack', 0.),
		subordinate.Event(3000., 'ebb', -1.), subordinate.Event(4000., 'slack', 0.)]
	shifted = subordinate.shift(reference, o)
	assert [(e.t, e.kind, e.velocity) for e in shifted] == [
		(100., 'slack before flood', 0.), (1200., 'flood', 2.), (2300., 'slack before ebb', 0.), (3400., 'ebb', -3.), (4100., 'slack before flood', 0.)]


def test_shifted_events_out_of_order_are_refused():
	o = subordinate.Offsets('S', 'R', 1, 0., 4000., 0., 0., 1., 1., 0., 180.)
	reference = [subordinate.Event(0., 'slack', 0.), subordinate.Event(1000., 'flood', 1.), subordinate.Event(2000., 'slack', 0.)]
	with pytest.raises(ValueError, match='order'):
		subordinate.shift(reference, o)


def sine_reference(o, period=44712., amplitude=2.):
	"""A pure semidiurnal reference: flood maxima at quarter periods, slack at whole and half periods."""
	t = np.arange(-3 * 3600., 30 * 3600. + 1, 360.)
	v = amplitude * np.sin(2 * np.pi * t / period)
	ref_events = []
	for k in range(-1, 4):
		base = k * period
		ref_events += [subordinate.Event(base, 'slack', 0.), subordinate.Event(base + period / 4, 'flood', amplitude),
			subordinate.Event(base + period / 2, 'slack', 0.), subordinate.Event(base + 3 * period / 4, 'ebb', -amplitude)]
	ref_events = [e for e in ref_events if t[0] <= e.t <= t[-1]]
	return t, v, ref_events


def test_series_passes_through_the_scaled_maxima_and_slack_of_the_shifted_events():
	o = subordinate.offsets(ACT3406)
	t, v, ref_events = sine_reference(o)
	shifted = subordinate.shift(ref_events, o)
	inside = [e for e in shifted if shifted[0].t < e.t < shifted[-1].t]
	values = subordinate.series(np.array([e.t for e in inside]), t, v, ref_events, o)
	expected = [e.velocity for e in inside]
	# The reference is sampled every 6 minutes and read linearly: within 1 mm/s of its true maximum.
	assert values == pytest.approx(expected, abs=1e-3)
	flood = [x for x, e in zip(values, inside) if e.kind == 'flood']
	ebb = [x for x, e in zip(values, inside) if e.kind == 'ebb']
	assert flood == pytest.approx([2. * 0.6] * len(flood), abs=1e-3)
	assert ebb == pytest.approx([-2. * 1.12] * len(ebb), abs=1e-3)


def test_between_events_the_reference_is_warped_linearly_in_time():
	o = subordinate.Offsets('S', 'R', 1, slack_before_flood_s=600., max_flood_s=0., slack_before_ebb_s=0., max_ebb_s=0.,
		flood_ratio=1., ebb_ratio=1., flood_dir=0., ebb_dir=180.)
	t, v, ref_events = sine_reference(o)
	period = 44712.
	# Between a slack before flood (moved 600 s later) and the flood maximum (not moved), the quarter period is
	# compressed by 600 s: halfway along it reads the reference halfway along its own quarter.
	start, end = period + 600., period + period / 4
	value = subordinate.series(np.array([(start + end) / 2]), t, v, ref_events, o)[0]
	assert value == pytest.approx(2. * np.sin(np.pi / 4), abs=1e-3)


def test_series_never_extrapolates():
	o = subordinate.offsets(ACT3406)
	t, v, ref_events = sine_reference(o)
	shifted = subordinate.shift(ref_events, o)
	with pytest.raises(ValueError, match='bracket'):
		subordinate.series(np.array([shifted[0].t - 1.]), t, v, ref_events, o)
	with pytest.raises(ValueError, match='reference series'):
		subordinate.series(np.array([shifted[1].t]), t[t > ref_events[2].t], v[t > ref_events[2].t], ref_events, o)


def test_flood_points_along_the_flood_direction_and_ebb_along_the_ebb_direction():
	o = subordinate.offsets(ACT3406)
	north, east = subordinate.components(np.array([1., -2., 0.]), o)
	assert north[0] == pytest.approx(np.cos(np.radians(37.)))
	assert east[0] == pytest.approx(np.sin(np.radians(37.)))
	assert north[1] == pytest.approx(2. * np.cos(np.radians(215.)))
	assert east[1] == pytest.approx(2. * np.sin(np.radians(215.)))
	assert (north[2], east[2]) == (0., 0.)
