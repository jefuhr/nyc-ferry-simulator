"""B1: consent and approval must be recorded in substance, not just as keys (review finding B1).

Failure modes guarded: a required field present but null, a wrong type, a missing key, consent flags
left false or unset, approval without a role or a date, an approving role outside the controlled list,
a template placeholder left in place, an unknown key hiding a typo, and a complete session being accepted.
"""

import json
import shutil

import pytest

from crew_intake import answers as answers_module
from crew_intake import fit as fit_module
from crew_intake import patch as patch_module
from tests.conftest import GOOD_SESSION, TEMPLATE, make_answers, messages, session_with

REQUIRED = ["sessionId", "date", "respondentRole", "respondentCode", "vesselClass", "conditions", "consent"]


def consent_with(**changes) -> dict:
	consent = json.loads(json.dumps(GOOD_SESSION["consent"]))
	for key, value in changes.items():
		if value is ...:
			consent.pop(key, None)
		else:
			consent[key] = value
	return session_with(consent=consent)


def test_valid_session_is_accepted(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a"))
	assert result.ok, messages(result)
	assert result.session["consent"]["approvedByRole"] == "operations-manager"


@pytest.mark.parametrize("field", REQUIRED)
def test_null_required_field_is_an_error(tmp_path, field):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(**{field: None})))
	assert not result.ok
	assert any(field in message and "null" in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("field", REQUIRED)
def test_missing_required_field_is_an_error(tmp_path, field):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(**{field: ...})))
	assert not result.ok
	assert any(field in message and "missing" in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("field,value", [
	("sessionId", 5), ("date", 20260115), ("respondentRole", ["captain"]), ("respondentCode", 1),
	("vesselClass", True), ("conditions", "half"), ("consent", True), ("synthetic", "yes"), ("notes", 3),
])
def test_wrong_type_is_an_error(tmp_path, field, value):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(**{field: value})))
	assert not result.ok
	assert any(field in message for message in messages(result)), messages(result)


def test_null_consent_blocks_patch_and_fit(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(consent=None)))
	assert not loaded.ok
	with pytest.raises(patch_module.PatchError):
		patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", None, tmp_path / "out")
	assert result.status == "refused" and result.patch is None


@pytest.mark.parametrize("key", ["respondentAgreed", "operatorApproved"])
@pytest.mark.parametrize("value", [False, None, "yes", 1, ...])
def test_consent_flags_must_be_literally_true(tmp_path, key, value):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=consent_with(**{key: value})))
	assert not result.ok
	assert any(key in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("value", [None, "", "John Smith", "who gave the OK (by role) and when", "manager", "Operations Manager", 3, ...])
def test_approving_role_must_come_from_the_controlled_list(tmp_path, value):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=consent_with(approvedByRole=value)))
	assert not result.ok
	assert any("approvedByRole" in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("value", [None, "", "2026", "soon", "<yyyy-mm-dd>", 20260110, ...])
def test_approval_date_must_be_a_date(tmp_path, value):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=consent_with(approvalDate=value)))
	assert not result.ok
	assert any("approvalDate" in message for message in messages(result)), messages(result)


def test_approval_after_the_session_is_flagged(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=consent_with(approvalDate="2026-02-01")))
	assert result.ok, messages(result)
	assert any("approvalDate" in message for message in messages(result, "warning")), messages(result, "warning")


def test_unknown_session_key_is_an_error(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(respondantCode="A")))
	assert any("respondantCode" in message for message in messages(result)), messages(result)


def test_unknown_consent_key_is_an_error(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=consent_with(approvedBy="operations-manager")))
	assert any("approvedBy" in message for message in messages(result)), messages(result)


def test_template_consent_defaults_are_not_true():
	session = json.loads((TEMPLATE / "session.json").read_text())
	assert session["consent"]["operatorApproved"] is False
	assert session["consent"]["respondentAgreed"] is False
	assert session["consent"]["approvedByRole"] is None
	assert session["consent"]["approvalDate"] is None


def test_the_untouched_template_does_not_validate():
	result = answers_module.load_answers(TEMPLATE)
	assert not result.ok
	errors = messages(result)
	for field in ("operatorApproved", "respondentAgreed", "approvedByRole", "approvalDate"):
		assert any(field in message for message in errors), (field, errors)
	assert any("placeholder" in message for message in errors), errors


def test_template_copied_with_numbers_but_no_consent_is_rejected(tmp_path):
	"""The reviewer's scenario: copy the template, enter numbers, leave the consent block alone."""
	folder = tmp_path / "session"
	shutil.copytree(TEMPLATE, folder)
	(folder / "direct_answers.csv").write_text("question_id,value,unit,note\nE1,650,rpm,\n")
	result = answers_module.load_answers(folder)
	assert not result.ok
	assert any("consent" in message for message in messages(result)), messages(result)


def test_placeholder_values_are_rejected_even_when_well_formed(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(respondentExperience="<years on this class, if they want to say>")))
	assert any("placeholder" in message and "respondentExperience" in message for message in messages(result)), messages(result)
