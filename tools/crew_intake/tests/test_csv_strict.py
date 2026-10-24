"""M6: a malformed CSV must fail loudly with a located diagnostic, never select a wrong value (review finding M6).

Failure modes guarded: a duplicate header silently overwriting a value, an extra cell discarded, an extra
cell on an otherwise empty row crashing the loader, a missing cell, an unknown column, broken quoting, a
unit written into a numeric cell, a missing unit, and a UTF-8 BOM or quoted multi-line note being
treated as malformed.
"""

from crew_intake import answers as answers_module
from tests.conftest import GOOD_RPM_SPEED, make_answers, messages


def load_with_direct(tmp_path, text):
	folder = make_answers(tmp_path / "a")
	(folder / "direct_answers.csv").write_text(text)
	return answers_module.load_answers(folder)


def test_duplicate_header_is_an_error_and_selects_nothing(tmp_path):
	"""The reviewer's duplicate-column probe recorded E1 as 900 rpm."""
	result = load_with_direct(tmp_path, "question_id,value,unit,note,value\nE1,650,rpm,,900\n")
	assert not result.ok
	assert any("direct_answers.csv" in message and "duplicate" in message and "value" in message for message in messages(result)), messages(result)
	assert result.direct == []


def test_extra_cell_on_a_populated_row_is_an_error(tmp_path):
	result = load_with_direct(tmp_path, "question_id,value,unit,note\nE1,650,rpm,,EXTRA\n")
	assert not result.ok
	assert any("direct_answers.csv line 2" in message and "5" in message and "4" in message for message in messages(result)), messages(result)


def test_extra_cell_on_an_otherwise_empty_row_is_a_located_error_not_a_crash(tmp_path):
	result = load_with_direct(tmp_path, "question_id,value,unit,note\n,,,,EXTRA\n")
	assert not result.ok
	assert any("direct_answers.csv line 2" in message for message in messages(result)), messages(result)


def test_missing_cell_is_an_error(tmp_path):
	result = load_with_direct(tmp_path, "question_id,value,unit,note\nE1,650,rpm\n")
	assert not result.ok
	assert any("direct_answers.csv line 2" in message and "3" in message for message in messages(result)), messages(result)


def test_unknown_column_is_an_error(tmp_path):
	result = load_with_direct(tmp_path, "question_id,value,unit,note,comment\nE1,650,rpm,,fine\n")
	assert not result.ok
	assert any("direct_answers.csv" in message and "comment" in message for message in messages(result)), messages(result)


def test_blank_rows_with_stray_commas_are_skipped(tmp_path):
	result = load_with_direct(tmp_path, "question_id,value,unit,note\nE1,650,rpm,\n,,,,\n\n")
	assert result.ok, messages(result)
	assert [answer.question_id for answer in result.direct] == ["E1"]


def test_broken_quoting_is_an_error(tmp_path):
	result = load_with_direct(tmp_path, 'question_id,value,unit,note\nE1,650,rpm,"unterminated\nS2,35,deg,fine\n')
	assert not result.ok
	assert any("direct_answers.csv" in message for message in messages(result)), messages(result)
	assert not any(answer.question_id == "S2" for answer in result.direct)


def test_inch_marks_inside_a_cell_are_kept_verbatim(tmp_path):
	"""A quote inside an unquoted cell is not broken quoting (38" propeller); the parser must not mangle it."""
	result = load_with_direct(tmp_path, 'question_id,value,unit,note\nE1,650,rpm,6"5 head\n')
	assert result.ok, messages(result)
	assert result.direct[0].note == '6"5 head'


def test_quoted_multiline_note_keeps_physical_line_numbers(tmp_path):
	result = load_with_direct(tmp_path, 'question_id,value,unit,note\nE1,650,rpm,"line one\nline two"\nS2,abc,deg,\n')
	assert not result.ok
	assert any("direct_answers.csv line 4" in message and "S2" in message for message in messages(result)), messages(result)
	assert result.direct[0].note == "line one\nline two"


def test_utf8_bom_is_tolerated(tmp_path):
	result = load_with_direct(tmp_path, "﻿question_id,value,unit,note\nE1,650,rpm,\n")
	assert result.ok, messages(result)


def test_unit_written_into_a_numeric_cell_is_an_error(tmp_path):
	rows = [dict(row) for row in GOOD_RPM_SPEED]
	rows[0]["speed_kn"] = "7.5 kn"
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows))
	assert any("rpm_speed.csv line 2" in message and "speed_kn" in message for message in messages(result)), messages(result)


def test_missing_unit_in_a_direct_answer_is_an_error(tmp_path):
	result = load_with_direct(tmp_path, "question_id,value,unit,note\nE1,650,,\n")
	assert not result.ok
	assert any("E1" in message and "unit" in message for message in messages(result)), messages(result)


def test_empty_file_without_a_header_is_an_error(tmp_path):
	result = load_with_direct(tmp_path, "")
	assert any("direct_answers.csv" in message and "header" in message for message in messages(result)), messages(result)
