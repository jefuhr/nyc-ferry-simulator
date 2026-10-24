import numpy as np
import pandas as pd
import pytest

from calibration.analysis import correct_current, extract_events, fit_grid, interpolate_current


def track(speeds, dt=5, headings=None):
	speeds = np.asarray(speeds, dtype=float)
	n = len(speeds)
	return pd.DataFrame({
		'mmsi': 1, 'time_utc': pd.date_range('2025-01-01', periods=n, freq=f'{dt}s', tz='UTC'),
		'north_m': np.r_[0, np.cumsum((speeds[:-1] + speeds[1:]) / 2 * dt)],
		'east_m': 0., 'sog_mps': speeds, 'cog_deg': 0.,
		'heading_deg': np.zeros(n) if headings is None else headings,
		'current_north_mps': 0., 'current_east_mps': 0., 'current_covered': True,
		'landing_distance_m': np.r_[0, np.cumsum((speeds[:-1] + speeds[1:]) / 2 * dt)],
		'landing_id': 'dock',
	})


def test_toward_current_is_subtracted_as_vector_and_surge_is_projected():
	points = track([5, 5, 5], headings=[0, 90, np.nan])
	points['current_north_mps'] = 2
	points['current_east_mps'] = 4
	corrected = correct_current(points)
	assert corrected.stw_mps.tolist() == pytest.approx([5, 5, 5])
	assert corrected.surge_mps.iloc[:2].tolist() == pytest.approx([3, -4])
	assert np.isnan(corrected.surge_mps.iloc[2])
	points['current_north_mps'] = -2
	points['current_east_mps'] = 0
	assert correct_current(points).stw_mps.iloc[0] == 7


def test_current_interpolation_never_extrapolates_or_bridges_missing_data():
	north, east = interpolate_current(np.array([-1, 0, 180, 360, 361, 800, 1000]),
		np.array([0, 360, 1000]), np.array([1, -1, 2]), np.zeros(3))
	assert north[:4] == pytest.approx([np.nan, 1, 0, -1], nan_ok=True)
	assert np.isnan(north[4:6]).all()
	assert north[-1] == 2
	assert east[2] == 0


def test_known_departure_and_arrival_profiles():
	departure = track([0, 2, 4, 6, 8, 10, 12])
	events, profiles = extract_events(correct_current(departure))
	kept = events.query("kind == 'departure' and accepted")
	assert len(kept) == 1
	assert kept.iloc[0].duration_s == 25
	assert kept.iloc[0].mean_acceleration_mps2 == pytest.approx(.4)
	arrival = track([12, 10, 8, 6, 4, 2, 0])
	arrival['landing_distance_m'] = arrival.north_m.iloc[-1] - arrival.north_m
	events, profiles = extract_events(correct_current(arrival))
	assert len(events.query("kind == 'arrival' and accepted")) == 1
	assert profiles.iloc[-1].stw_mps == 0
	assert profiles.iloc[-1].landing_distance_m == 0


def test_sparse_departure_rejected_and_position_jump_rejected():
	points = track([0, 6, 12], dt=60)
	events, _ = extract_events(correct_current(points))
	assert 'cadence' in events.query("kind == 'departure'").iloc[0].reason
	points = track([0, 2, 4, 6, 8, 10, 12])
	points.loc[2, 'north_m'] += 1000
	events, _ = extract_events(correct_current(points))
	assert 'position' in events.query("kind == 'departure'").iloc[0].reason


def test_steady_sampling_and_turn_heading_wrap():
	points = track([12] * 4, dt=60)
	points['landing_distance_m'] = 2000
	events, _ = extract_events(correct_current(points))
	assert len(events.query("kind == 'steady' and accepted")) == 1
	turn = track([12] * 5, headings=[350, 355, 0, 5, 10])
	turn['landing_distance_m'] = 2000
	events, _ = extract_events(correct_current(turn))
	assert events.query("kind == 'turn' and accepted").iloc[0].yaw_deg_s == pytest.approx(1.)
	turn['heading_deg'] = np.nan
	events, _ = extract_events(correct_current(turn))
	assert not len(events.query("kind == 'turn' and accepted"))


def test_current_missing_and_duplicate_timestamp_fail_closed():
	points = track([0, 2, 4, 6, 8, 10, 12])
	points.loc[2, 'current_covered'] = False
	events, _ = extract_events(correct_current(points))
	assert 'current' in events.query("kind == 'departure'").iloc[0].reason
	points['current_covered'] = True
	points.loc[2, 'time_utc'] = points.loc[1, 'time_utc']
	events, _ = extract_events(correct_current(points))
	assert 'time' in events.query("kind == 'departure'").iloc[0].reason


@pytest.mark.parametrize('dt', [1, 5])
@pytest.mark.parametrize('yaw_deg_s', [1, 4])
def test_resolved_circular_turns_accept_dense_trials(dt, yaw_deg_s):
	t = np.arange(0, 61, dt)
	heading = yaw_deg_s * t
	points = track([10] * len(t), dt=dt, headings=heading)
	radius = 10 / np.radians(yaw_deg_s)
	points['north_m'] = radius * np.sin(np.radians(heading))
	points['east_m'] = radius * (1 - np.cos(np.radians(heading)))
	points['cog_deg'] = heading
	points['landing_distance_m'] = 2000
	events, _ = extract_events(correct_current(points))
	turns = events.query("kind == 'turn'")
	assert len(turns) > 0
	assert turns.accepted.all(), turns.reason.tolist()
	assert (turns.duration_s >= 15).all()
	assert turns.yaw_deg_s.tolist() == pytest.approx([yaw_deg_s] * len(turns))
	assert turns.mean_stw_mps.tolist() == pytest.approx([10] * len(turns))
	assert turns.speed_loss_fraction.tolist() == pytest.approx([0] * len(turns))
	# Adjacent windows share their endpoint, not an overlapping interval.
	assert turns.end_utc.iloc[:-1].tolist() == turns.start_utc.iloc[1:].tolist()


def test_sparse_turn_still_fails_cadence():
	points = track([10] * 5, dt=60, headings=[0, 20, 40, 60, 80])
	points['landing_distance_m'] = 2000
	events, _ = extract_events(correct_current(points))
	turns = events.query("kind == 'turn'")
	assert len(turns) == 1
	assert not turns.accepted.any()
	assert turns.reason.tolist() == ['cadence']


def test_seeded_fit_reproducible_known_answer_and_unknown_controls_refused():
	grid = np.array([.5, 1., 1.5])
	predictions = grid[:, None] * np.arange(1, 9)[None, :]
	observations = np.arange(1, 9)
	a = fit_grid(grid, predictions, observations, controls_known=True, seed=42)
	b = fit_grid(grid, predictions, observations, controls_known=True, seed=42)
	assert a == b
	assert a['value'] == 1.
	assert a['bootstrap_interval'] == [1., 1.]
	with pytest.raises(ValueError, match='controls'):
		fit_grid(grid, predictions, observations, controls_known=False, seed=42)


def test_identity_requires_exact_name_and_consistent_dimensions():
	from calibration.inputs import classify
	roster = pd.DataFrame({'mmsi': [1, 2, 3, 4], 'name': [' McShiny ', 'Bay Hopper', 'Time Traveler', 'Purpose'],
		'length_m': [26, 29, 26, 30], 'beam_m': [8, 8, 8, 9], 'report_count': [1] * 4})
	result = classify(roster)
	assert result['class'].tolist() == ['T', 'unclassified', 'unclassified', 'K-IC18130']
	assert result.identity_reason.iloc[1] == 'dimension_conflict'


def test_noaa_ebb_points_toward_and_round_trip_current():
	from ferrydata.coops import components, current_vector
	speed, direction, phase = current_vector(-100, 30, 210)
	assert (speed, direction, phase) == (1, 210, 'ebb')
	north, east = components(speed, direction)
	points = track([5])
	points['current_north_mps'], points['current_east_mps'] = north, east
	water = correct_current(points)
	assert water.water_north_mps.iloc[0] == pytest.approx(5 + np.sqrt(3)/2)
	assert water.water_east_mps.iloc[0] == pytest.approx(.5)


def test_timestamp_resolution_does_not_change_current_lookup():
	from calibration.inputs import epoch_seconds
	times = pd.Series(pd.to_datetime(['2024-10-07T00:00:00Z', '2024-10-07T00:06:00Z']))
	for unit in ['s', 'ms', 'us', 'ns']:
		assert epoch_seconds(times.dt.as_unit(unit)).tolist() == [1728259200., 1728259560.]
