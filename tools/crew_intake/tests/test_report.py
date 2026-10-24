"""m1: every accepted answer reaches the report (review finding m1).

Failure modes guarded: Markdown text answers loaded but absent from the patch and report, direct answers
missing from the report, row notes dropped, and direct C10/C11 (or other table-only questions) accepted
without producing anything.
"""

import json

from crew_intake import answers as answers_module
from crew_intake import patch as patch_module
from tests.conftest import GOOD_DIRECT, GOOD_STOPPING, make_answers, messages


def test_text_answers_appear_in_the_patch_and_the_report(tmp_path, vessel_t):
	"""The reviewer's text-drop probe."""
	text = "# C1\nObserved approved head model: one lever per engine\n# S6\nInterceptors absent\n"
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", control_layout=text))
	assert loaded.ok, messages(loaded)
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["texts"]["control_layout.md"] == text
	report = patch_module.patch_report(patch)
	assert "Interceptors absent" in report and "one lever per engine" in report


def test_every_direct_answer_appears_in_the_report_inventory(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a"))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	report = patch_module.patch_report(patch)
	inventory = {item["questionId"]: item for item in patch["inventory"]}
	for row in GOOD_DIRECT:
		assert row["question_id"] in inventory, row
		assert f"| {row['question_id']} |" in report
	assert inventory["E1"]["disposition"].startswith("patched")
	assert inventory["S3"]["disposition"].startswith("derived")
	assert "target" in inventory["E3"]["disposition"]
	assert inventory["S1"]["disposition"].startswith("patched")


def test_row_notes_appear_in_the_report(tmp_path, vessel_t):
	stopping = [dict(row) for row in GOOD_STOPPING]
	stopping[0]["note"] = "synthetic distinctive stopping note"
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", stopping=stopping))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert "synthetic distinctive stopping note" in patch_module.patch_report(patch)


def test_table_rows_are_inventoried(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a"))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	tables = {item["source"] for item in patch["inventory"]}
	assert {"rpm_speed.csv", "shift_timing.csv", "stopping.csv", "turning.csv", "twisting.csv", "direct_answers.csv"} <= tables


def test_direct_c10_c11_are_rejected_with_an_instruction(tmp_path):
	"""The reviewer's direct-shift probe: C10 = 0.8 s and C11 = 3 s validated but produced nothing."""
	direct = [{"question_id": "C10", "value": "0.8", "unit": "s", "note": ""}, {"question_id": "C11", "value": "3", "unit": "s", "note": ""}]
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert not result.ok
	errors = messages(result)
	assert any("C10" in message and "shift_timing.csv" in message for message in errors), errors
	assert any("C11" in message and "shift_timing.csv" in message for message in errors), errors


def test_other_table_questions_are_rejected_as_direct_answers(tmp_path):
	direct = [{"question_id": "E2", "value": "12", "unit": "text", "note": ""}, {"question_id": "T4", "value": "fast", "unit": "text", "note": ""}]
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	errors = messages(result)
	assert any("E2" in message and "rpm_speed.csv" in message for message in errors), errors
	assert any("T4" in message and "twisting.csv" in message for message in errors), errors


def test_report_names_the_answer_set_and_vessel_fingerprints(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a"))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	report = patch_module.patch_report(patch)
	assert patch["base"]["vesselSha256"][:12] in report
	assert patch["base"]["answersSha256"][:12] in report
