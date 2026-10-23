import math

import pandas as pd
import pytest

from ferrydata import coops

CSV = """Time, Depth, Velocity_Major, meanFloodDir, meanEbbDir, Bin
2026-06-14 00:00,2.4,67.1,49.0,225.0,12
2026-06-14 00:06,2.4,0.0,49.0,225.0,12
2026-06-14 00:12,2.4,-50.0,49.0,225.0,12
"""


def test_flood_points_toward_mean_flood_direction():
	speed, direction, phase = coops.current_vector(67.1, 49.0, 225.0)
	assert speed == pytest.approx(0.671)  # cm/s -> m/s
	assert direction == 49.0
	assert phase == "flood"


def test_ebb_points_toward_mean_ebb_direction_not_away_from_it():
	speed, direction, phase = coops.current_vector(-50.0, 49.0, 225.0)
	assert speed == pytest.approx(0.5)  # speed is a magnitude
	assert direction == 225.0  # toward, as published; never flipped by 180
	assert phase == "ebb"


def test_slack_has_zero_speed():
	speed, _, phase = coops.current_vector(0.0, 49.0, 225.0)
	assert speed == 0.0 and phase == "slack"


def test_direction_is_normalised_to_0_360():
	assert coops.current_vector(10.0, 360.0, 180.0)[1] == 0.0
	assert coops.current_vector(10.0, -10.0, 180.0)[1] == 350.0


def test_components_follow_toward_convention():
	# Flowing toward 90 degrees (east) must have positive east, zero north.
	n, e = coops.components(1.0, 90.0)
	assert n == pytest.approx(0.0, abs=1e-12) and e == pytest.approx(1.0)
	n, e = coops.components(1.0, 225.0)  # toward south-west
	assert n < 0 and e < 0


def test_parse_currents_csv_to_utc_rows():
	df = coops.parse_currents_csv(CSV)
	assert list(df.columns) == ["time_utc", "speed_mps", "direction_toward_deg", "phase", "north_mps", "east_mps", "velocity_major_mps", "depth_m", "bin"]
	assert df.time_utc.iloc[0] == "2026-06-14T00:00:00Z"
	assert df.direction_toward_deg.tolist() == [49.0, 49.0, 225.0]
	assert df.speed_mps.tolist() == pytest.approx([0.671, 0.0, 0.5])
	assert df.velocity_major_mps.iloc[2] == pytest.approx(-0.5)


def test_api_error_text_raises():
	with pytest.raises(ValueError):
		coops.parse_currents_csv("Error: No Predictions data was found. Please make sure the Station ID and Date are valid.")


def test_max_gap_hours():
	times = pd.to_datetime(["2026-06-14T00:00:00Z", "2026-06-14T00:06:00Z", "2026-06-14T02:06:00Z"], utc=True)
	assert coops.max_gap_hours(times) == pytest.approx(2.0)
	assert coops.max_gap_hours(times[:1]) == math.inf


def test_parse_water_level_csv():
	text = "Date Time, Water Level, Sigma, O, F, R, L, Quality\n2026-06-14 00:00,0.512,0.003,0,0,0,0,v\n2026-06-14 00:06,0.540,0.003,0,0,0,0,v\n"
	df = coops.parse_water_level_csv(text)
	assert list(df.columns) == ["time_utc", "water_level_navd88_m", "quality"]
	assert df.water_level_navd88_m.tolist() == [0.512, 0.540]
	assert df.time_utc.iloc[0] == "2026-06-14T00:00:00Z"


def test_water_level_header_with_trailing_space_and_extra_columns():
	text = "Date Time, Water Level, Sigma, O or I (for verified), F, R, L, Quality \n2026-06-14 00:00,1.087,0.074,0,0,0,0,v\n"
	df = coops.parse_water_level_csv(text)
	assert df.water_level_navd88_m.tolist() == [1.087]


def test_error_line_after_header_is_an_api_error():
	text = "Date Time, Water Level, Sigma, O or I (for verified), F, R, L, Quality \nError: No data was found. This product may not be offered at this station at the requested time."
	with pytest.raises(ValueError):
		coops.parse_water_level_csv(text)
