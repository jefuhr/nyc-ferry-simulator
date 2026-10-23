import math

import pytest

from ferrydata import lights


def test_simple_flashing_green():
	light = lights.parse_light({"LITCHR": 2, "COLOUR": ["4"], "SIGGRP": "(1)", "SIGPER": 4.0})
	assert light["characteristic"] == "Fl"
	assert light["colours"] == ["G"]
	assert light["group"] is None
	assert light["period_s"] == 4.0
	assert light["notation"] == "Fl G 4s"


def test_composite_group_flashing():
	light = lights.parse_light({"LITCHR": 2, "COLOUR": [1], "SIGGRP": "(2+1)", "SIGPER": 6.0})
	assert light["group"] == [2, 1]
	assert light["notation"] == "Fl(2+1) W 6s"


def test_grouped_occulting_red():
	light = lights.parse_light({"LITCHR": 8, "COLOUR": [3], "SIGGRP": "(3)", "SIGPER": 10})
	assert light["notation"] == "Oc(3) R 10s"


@pytest.mark.parametrize(
	"code,abbr",
	[(1, "F"), (2, "Fl"), (3, "LFl"), (4, "Q"), (5, "VQ"), (6, "UQ"), (7, "Iso"), (8, "Oc"), (12, "Mo"), (25, "Q+LFl")],
)
def test_characteristic_codes(code, abbr):
	assert lights.parse_light({"LITCHR": code})["characteristic"] == abbr


def test_colour_codes_and_multi_colour_order():
	light = lights.parse_light({"LITCHR": 2, "COLOUR": ["1", "3"], "SIGPER": 5.0})
	assert light["colours"] == ["W", "R"]
	assert lights.parse_light({"LITCHR": 1, "COLOUR": [6]})["colours"] == ["Y"]
	assert lights.parse_light({"LITCHR": 1, "COLOUR": [5]})["colours"] == ["Bu"]


def test_range_height_and_sector_are_carried_with_units():
	light = lights.parse_light({"LITCHR": 2, "COLOUR": [4], "SIGPER": 4.0, "VALNMR": 3.0, "HEIGHT": 12.0, "SECTR1": 10.0, "SECTR2": 190.0})
	assert light["range_nm"] == 3.0
	assert light["range_m"] == pytest.approx(3.0 * 1852.0)
	assert light["height_m"] == 12.0
	assert (light["sector_start_deg"], light["sector_end_deg"]) == (10.0, 190.0)
	assert light["full_notation"] == "Fl G 4s 12m 3M"


def test_nan_and_missing_attributes_become_none():
	light = lights.parse_light({"LITCHR": 2, "COLOUR": [4], "SIGPER": math.nan, "VALNMR": math.nan, "HEIGHT": None})
	assert light["period_s"] is None and light["range_nm"] is None and light["height_m"] is None
	assert light["notation"] == "Fl G"


def test_no_characteristic_gives_none_not_crash():
	light = lights.parse_light({"COLOUR": [1]})
	assert light["characteristic"] is None
	assert light["parsed"] is False


def test_signal_sequence_parsing():
	assert lights.parse_sequence("00.3+(02.2)") == [("light", 0.3), ("dark", 2.2)]
	seq = lights.parse_sequence("00.3+(00.4)+00.3+(01.2)+00.3+(03.5)")
	assert [s for s, _ in seq] == ["light", "dark"] * 3
	assert sum(d for _, d in seq) == pytest.approx(6.0)
	assert lights.parse_sequence(None) is None
	assert lights.parse_sequence("garbage") is None


def test_parse_notation_round_trip():
	parsed = lights.parse_notation("Fl G 4s")
	assert parsed == {"characteristic": "Fl", "group": None, "colours": ["G"], "period_s": 4.0}
	parsed = lights.parse_notation("Fl(2+1) R 6s")
	assert parsed["group"] == [2, 1] and parsed["colours"] == ["R"] and parsed["period_s"] == 6.0
	assert lights.parse_notation("Q W") == {"characteristic": "Q", "group": None, "colours": ["W"], "period_s": None}
	assert lights.parse_notation("not a light") is None


def test_numpy_array_values_are_accepted():
	import numpy as np

	light = lights.parse_light({"LITCHR": np.int32(2), "COLOUR": np.array(["4"]), "SIGGRP": "(1)", "SIGPER": np.float64(4.0)})
	assert light["notation"] == "Fl G 4s"
