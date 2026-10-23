import numpy as np
import pandas as pd
import pytest

from ferrydata import ais, geodesy

# Two fixture landings, 2 km apart.
LANDINGS = [
	{"stop_id": "A", "stop_name": "Fixture A", "lat": 40.7000, "lon": -74.0000},
	{"stop_id": "B", "stop_name": "Fixture B", "lat": 40.7180, "lon": -74.0000},
]


def _points(rows, mmsi=111111111):
	df = pd.DataFrame(rows, columns=["minute", "lat", "lon", "sog"])
	df["mmsi"] = mmsi
	df["time_utc"] = pd.Timestamp("2025-07-14T12:00:00Z") + pd.to_timedelta(df.minute, unit="m")
	df["north_m"], df["east_m"] = geodesy.to_local(df.lat.to_numpy(), df.lon.to_numpy())
	return df


def test_clean_replaces_ais_sentinels():
	df = pd.DataFrame({"sog": [5.0, 102.3, 0.0], "cog": [90.0, 360.0, 3600.0], "heading": [180, 511, 0]})
	out = ais.clean_ais(df)
	assert np.isnan(out.sog.iloc[1]) and out.sog.iloc[0] == 5.0
	assert np.isnan(out.cog.iloc[1]) and np.isnan(out.cog.iloc[2])
	assert np.isnan(out.heading.iloc[1]) and out.heading.iloc[2] == 0


def test_static_filter_passenger_vessels_25_to_31_m():
	df = pd.DataFrame({"vessel_type": [60, 69, 52, 60, 60, 70], "length": [26, 31, 28, 24, 32, 28]})
	assert ais.static_match(df).tolist() == [True, True, False, False, False, False]


def test_a_dwell_counts_as_one_visit_even_with_many_points():
	rows = [(m, 40.7000, -74.0000, 0.2) for m in range(0, 6)]  # six minutes alongside A
	visits = ais.count_visits(_points(rows), LANDINGS)
	assert visits == {"A": 1}


def test_separate_dwells_are_separate_visits():
	rows = [(0, 40.7000, -74.0, 0.1), (1, 40.7000, -74.0, 0.1), (60, 40.7000, -74.0, 0.1), (61, 40.7000, -74.0, 0.1)]
	assert ais.count_visits(_points(rows), LANDINGS) == {"A": 2}


def test_passing_at_speed_is_not_a_visit():
	rows = [(0, 40.7000, -74.0, 12.0), (1, 40.7001, -74.0, 12.0)]
	assert ais.count_visits(_points(rows), LANDINGS) == {}


def test_far_from_any_landing_is_not_a_visit():
	rows = [(0, 40.7100, -74.0, 0.0), (1, 40.7100, -74.0, 0.0)]  # 1.1 km from both, stopped
	assert ais.count_visits(_points(rows), LANDINGS) == {}


def test_two_landings_counted_independently():
	rows = [(0, 40.7000, -74.0, 0.1), (20, 40.7180, -74.0, 0.1), (60, 40.7000, -74.0, 0.1)]
	assert ais.count_visits(_points(rows), LANDINGS) == {"A": 2, "B": 1}


def test_roster_requires_repeat_visits_and_several_landings():
	frames = []
	# boat 1: a real shuttle, 3 visits to A and 3 to B
	rows = []
	for k in range(3):
		rows += [(k * 60, 40.7000, -74.0, 0.1), (k * 60 + 30, 40.7180, -74.0, 0.1)]
	frames.append(_points(rows, mmsi=1))
	# boat 2: sits at A all day (one visit) -> rejected
	frames.append(_points([(m, 40.7000, -74.0, 0.0) for m in range(30)], mmsi=2))
	# boat 3: only ever visits A, three times -> rejected by the two-landing rule
	frames.append(_points([(0, 40.7, -74.0, 0.0), (60, 40.7, -74.0, 0.0), (120, 40.7, -74.0, 0.0)], mmsi=3))
	points = pd.concat(frames)
	points["vessel_name"] = "BOAT"
	points["length"] = 28
	points["width"] = 9
	points["vessel_type"] = 60
	roster = ais.build_roster(points, LANDINGS, min_visits=5, min_landings=2)
	assert roster.mmsi.tolist() == [1]
	row = roster.iloc[0]
	assert row.visits_total == 6 and row.landings_visited == 2
	assert row.visits_A == 3 and row.visits_B == 3
	assert row.length_m == 28 and row.beam_m == 9
	assert str(row.first_seen).startswith("2025-07-14T12:00")


def test_roster_picks_the_modal_name_and_dimensions():
	rows = [(m * 60, 40.7000 if m % 2 == 0 else 40.7180, -74.0, 0.0) for m in range(8)]
	points = _points(rows)
	points["vessel_name"] = ["GOOD", "GOOD", "GOOD", "GOOD", "GOOD", "TYPO", "GOOD", "GOOD"]
	points["length"] = 28
	points["width"] = 9
	points["vessel_type"] = 60
	roster = ais.build_roster(points, LANDINGS, min_visits=2, min_landings=2)
	assert roster.name.iloc[0] == "GOOD"


def test_week_days():
	days = ais.week_days("2025-07-14")
	assert days[0] == "2025-07-14" and days[-1] == "2025-07-20" and len(days) == 7


def test_url_for_day_uses_the_year_folder():
	assert ais.day_url("2024-10-07").endswith("/csv2024/ais-2024-10-07.csv.zst")
