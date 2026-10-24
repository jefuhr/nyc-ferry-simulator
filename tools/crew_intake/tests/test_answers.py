"""Parsing and validation of an answers folder.

Failure modes guarded: missing or malformed session metadata, consent not recorded, a respondent
role outside the controlled list, unknown vessel class, missing or extra CSV columns, non-numeric cells,
unknown enumerations, units a question does not accept, values outside physical ranges, unknown or
duplicated question ids, and over-ground speeds silently treated as through-water.
"""

import json

import pytest

from crew_intake import answers as answers_module
from tests.conftest import GOOD_DIRECT, GOOD_RPM_SPEED, GOOD_SESSION, GOOD_SHIFTS, COLUMNS, make_answers, messages


def test_good_set_parses_without_errors(good_answers):
	result = answers_module.load_answers(good_answers)
	assert result.ok, messages(result)
	assert result.session["sessionId"] == "synthetic-2026-01-15-captain-a"
	assert len(result.rpm_speed) == 3
	assert len(result.direct) == len(GOOD_DIRECT)
	assert result.direct[0].question_id == "E1"
	assert result.direct[0].value == 670.0
	assert result.direct[0].unit == "rpm"
	assert len(result.shift_timing) == 2
	assert len(result.stopping) == 1 and len(result.turning) == 1 and len(result.twisting) == 1


def test_missing_session_file(tmp_path):
	folder = make_answers(tmp_path / "a")
	(folder / "session.json").unlink()
	result = answers_module.load_answers(folder)
	assert not result.ok
	assert any("session.json" in message for message in messages(result))


@pytest.mark.parametrize("field", ["sessionId", "date", "respondentRole", "respondentCode", "vesselClass", "conditions", "consent"])
def test_missing_required_metadata_is_named(tmp_path, field):
	session = json.loads(json.dumps(GOOD_SESSION))
	del session[field]
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert not result.ok
	assert any(field in message for message in messages(result)), messages(result)


def test_consent_must_be_recorded(tmp_path):
	session = json.loads(json.dumps(GOOD_SESSION))
	session["consent"]["operatorApproved"] = False
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert any("operatorApproved" in message for message in messages(result))


def test_respondent_must_be_a_role_not_a_name(tmp_path):
	session = json.loads(json.dumps(GOOD_SESSION))
	session["respondentRole"] = "Captain John Smith"
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert any("respondentRole" in message for message in messages(result))


def test_synthetic_sessions_must_say_so_in_the_id(tmp_path):
	session = json.loads(json.dumps(GOOD_SESSION))
	session["sessionId"] = "2026-01-15-captain-a"
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert any("synthetic" in message for message in messages(result))


def test_unknown_vessel_class(tmp_path):
	session = json.loads(json.dumps(GOOD_SESSION))
	session["vesselClass"] = "z-class"
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert any("vesselClass" in message for message in messages(result))


def test_wrong_unit_for_a_question(tmp_path):
	direct = [dict(row) for row in GOOD_DIRECT]
	direct[0]["unit"] = "deg"
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert any("E1" in message and "deg" in message for message in messages(result))


def test_value_outside_physical_range(tmp_path):
	direct = [dict(row) for row in GOOD_DIRECT]
	direct[0]["value"] = "6500"
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert any("E1" in message and "range" in message for message in messages(result))


def test_unknown_and_duplicate_question_ids(tmp_path):
	direct = [dict(row) for row in GOOD_DIRECT] + [{"question_id": "Q99", "value": "1", "unit": "s", "note": ""}, dict(GOOD_DIRECT[0])]
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert any("Q99" in message for message in messages(result))
	assert any("E1" in message and "twice" in message for message in messages(result))


def test_text_answer_must_be_one_of_the_options(tmp_path):
	direct = [dict(row) for row in GOOD_DIRECT]
	direct[4]["value"] = "wheel"
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert any("S1" in message for message in messages(result))


def test_missing_csv_column_is_an_error(tmp_path):
	columns = {"rpm_speed.csv": [column for column in COLUMNS["rpm_speed.csv"] if column != "speed_reference"]}
	result = answers_module.load_answers(make_answers(tmp_path / "a", columns=columns))
	assert any("rpm_speed.csv" in message and "speed_reference" in message for message in messages(result))


def test_non_numeric_cell_is_an_error(tmp_path):
	rows = [dict(row) for row in GOOD_RPM_SPEED]
	rows[1]["speed_kn"] = "about thirteen"
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows))
	assert any("rpm_speed.csv" in message and "speed_kn" in message for message in messages(result))


def test_unknown_enumeration_is_an_error(tmp_path):
	rows = [dict(row) for row in GOOD_RPM_SPEED]
	rows[1]["speed_reference"] = "gps"
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows))
	assert any("speed_reference" in message for message in messages(result))


def test_shift_gears_must_differ(tmp_path):
	rows = [dict(row) for row in GOOD_SHIFTS] + [{"from_gear": "ahead", "to_gear": "ahead", "from_rpm": "650", "seconds": "1", "lever_technique": "straight-through", "note": ""}]
	result = answers_module.load_answers(make_answers(tmp_path / "a", shifts=rows))
	assert any("shift_timing.csv" in message for message in messages(result))


def test_over_ground_speed_is_fit_usable_only_at_slack(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a"))
	assert result.ok
	assert [row.fit_usable for row in result.rpm_speed] == [True, True, True]
	session = json.loads(json.dumps(GOOD_SESSION))
	session["conditions"]["current"] = "ebb"
	session["conditions"]["currentKn"] = 1.5
	rows = [dict(row) for row in GOOD_RPM_SPEED]
	for row in rows:
		row["current"] = ""
	result = answers_module.load_answers(make_answers(tmp_path / "b", session=session, rpm_speed=rows))
	assert result.ok, messages(result)
	assert [row.fit_usable for row in result.rpm_speed] == [True, True, False]
	assert any("over-ground" in message for message in messages(result, "warning"))
	rows[2]["current"] = "ebb"
	result = answers_module.load_answers(make_answers(tmp_path / "c", rpm_speed=rows))
	assert result.ok, messages(result)
	assert [row.fit_usable for row in result.rpm_speed] == [True, True, False]


def test_astern_rows_are_kept_but_not_fit_usable(tmp_path):
	rows = [dict(row) for row in GOOD_RPM_SPEED] + [{"port_rpm": "900", "starboard_rpm": "900", "gear": "astern", "speed_kn": "4", "speed_tolerance_kn": "1", "speed_reference": "through-water", "load": "half", "wind_kn": "5", "current": "slack", "depth": "deep", "note": ""}]
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows))
	assert result.ok, messages(result)
	assert result.rpm_speed[-1].gear == "astern" and not result.rpm_speed[-1].fit_usable


def test_empty_tables_are_allowed_with_warnings(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=[], rpm_speed=[], shifts=[], stopping=[], turning=[], twisting=[]))
	assert result.ok, messages(result)
	assert any("rpm_speed.csv" in message for message in messages(result, "warning"))
