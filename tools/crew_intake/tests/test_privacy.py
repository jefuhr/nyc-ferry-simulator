"""B2: anonymisation is a controlled enumeration plus a publication boundary on free text (review finding B2).

Failure modes guarded: a name accepted as a respondent role, a free-form code that could carry a name,
a session id carrying free text, an email, phone number, handle or title-plus-name in any exported text
(notes, technique, walk, session fields, Markdown), ordinary marine terminology being rejected, ambiguous
capitalised phrases exported without a recorded review, and the docs promising detection the code
does not do.
"""

import json

import pytest

from crew_intake import answers as answers_module
from crew_intake import patch as patch_module
from crew_intake import privacy
from tests.conftest import GOOD_DIRECT, GOOD_SHIFTS, GOOD_TWISTING, REPO_ROOT, make_answers, messages, session_with


@pytest.mark.parametrize("role", ["John Smith A", "Captain John Smith", "Captain A", "Captain", "CAPTAIN", "", "pilot"])
def test_respondent_role_outside_the_list_is_rejected(tmp_path, role):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(respondentRole=role)))
	assert not result.ok
	assert any("respondentRole" in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("code", ["Smith", "a", "", "A-1", "ABCD", "J.S.", 1])
def test_respondent_code_is_a_short_anonymous_code(tmp_path, code):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(respondentCode=code)))
	assert not result.ok
	assert any("respondentCode" in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("code", ["A", "B2", "AC", "Z9"])
def test_short_codes_are_accepted(tmp_path, code):
	session = session_with(respondentCode=code, sessionId=f"synthetic-2026-01-15-captain-{code.lower()}")
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert result.ok, messages(result)


def test_session_id_is_derived_from_date_role_and_code(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(sessionId="synthetic-2026-01-15-john-smith")))
	assert not result.ok
	assert any("sessionId" in message and "synthetic-2026-01-15-captain-a" in message for message in messages(result)), messages(result)


def test_role_label_is_built_from_the_enumeration(tmp_path, vessel_t):
	session = session_with(respondentRole="chief-engineer", respondentCode="B", sessionId="synthetic-2026-01-15-chief-engineer-b")
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert loaded.ok, messages(loaded)
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["sources"] == {"crew-synthetic-2026-01-15-chief-engineer-b": "Synthetic crew interview (example only, not real data), Chief Engineer B, 2026-01-15, T-class"}
	assert patch["session"]["respondentRole"] == "chief-engineer"
	assert patch["session"]["respondentCode"] == "B"


# --- the publication boundary -----------------------------------------------------------------------

@pytest.mark.parametrize("text", [
	"Contact John Smith at john.smith@example.invalid",
	"call 212-555-0100 for the schedule",
	"call (212) 555-0100",
	"+1 212 555 0100",
	"Captain Smith said so",
	"Capt. J. Smith prefers the port engine",
	"ask Mate Jones",
	"Mr. Smith approved",
	"ask @skipperjoe",
])
def test_obvious_identifiers_are_rejected(text):
	findings = privacy.scan(text)
	assert findings and any(finding.level == "reject" for finding in findings), findings


@pytest.mark.parametrize("text", [
	"ZF 2050 gearbox, Baudouin 6M26.3 engines",
	"Michigan Wheel 38 in props, NiBrAl",
	"VHF 13 and 16; Pier 11 on the ebb",
	"D-rubber on the hull, foam fenders on the barge",
	"Captain A and Chief Engineer B agree",
	"about 650 rpm, 7.5 kn through the water",
	"East River flood sets you onto the barge at Wall St",
	"Hunters Point South on the ebb; Rockaway run cancelled above 25 kn",
	"Coast Guard VTS on channel 12",
	"Deckhand uses hand signals; all fast by voice",
	"SYNTHETIC example electronic control head",
])
def test_marine_and_equipment_terminology_passes(text):
	assert privacy.scan(text) == [], privacy.scan(text)


@pytest.mark.parametrize("text", ["Observed head model: Example Controls 123", "John Smith", "Spoke with Jane Doe about it"])
def test_ambiguous_capitalised_phrases_need_review(text):
	findings = privacy.scan(text)
	assert findings, text
	assert all(finding.level == "review" for finding in findings), findings


def test_accepted_phrases_clear_the_review_requirement():
	assert privacy.scan("Observed head model: Example Controls 123", accepted={"Example Controls"}) == []


def test_accepted_phrases_never_clear_a_rejection():
	findings = privacy.scan("john.smith@example.invalid", accepted={"john.smith@example.invalid"})
	assert findings and findings[0].level == "reject"


# --- the boundary is enforced at load time on every exported field ----------------------------------

def test_direct_answer_note_with_an_email_fails_validation(tmp_path):
	direct = [dict(row) for row in GOOD_DIRECT]
	direct[0]["note"] = "Contact John Smith at john.smith@example.invalid"
	result = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert not result.ok
	assert any("direct_answers.csv line 2" in message and "email" in message for message in messages(result)), messages(result)


def test_markdown_with_a_titled_name_fails_validation(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", control_layout="# C1\nCaptain John Smith showed the head\n"))
	assert not result.ok
	assert any("control_layout.md line 2" in message for message in messages(result)), messages(result)


def test_twisting_walk_text_is_scanned(tmp_path):
	rows = [dict(row) for row in GOOD_TWISTING]
	rows[0]["walk"] = "ask Captain Smith"
	result = answers_module.load_answers(make_answers(tmp_path / "a", twisting=rows))
	assert any("twisting.csv line 2" in message for message in messages(result)), messages(result)


def test_shift_note_is_scanned(tmp_path):
	rows = [dict(row) for row in GOOD_SHIFTS]
	rows[1]["note"] = "per Mate Jones"
	result = answers_module.load_answers(make_answers(tmp_path / "a", shifts=rows))
	assert any("shift_timing.csv line 3" in message for message in messages(result)), messages(result)


@pytest.mark.parametrize("field", ["notes", "respondentExperience", "photos", "hull"])
def test_session_text_fields_are_scanned(tmp_path, field):
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(**{field: "call 212-555-0100"})))
	assert any(f"session.json {field}" in message for message in messages(result)), messages(result)


def test_consent_and_conditions_notes_are_scanned(tmp_path):
	session = session_with()
	session["consent"]["notes"] = "approved by Mr. Smith"
	session["conditions"]["notes"] = "john.smith@example.invalid"
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	errors = messages(result)
	assert any("consent.notes" in message for message in errors), errors
	assert any("conditions.notes" in message for message in errors), errors


def test_ambiguous_markdown_requires_a_recorded_review(tmp_path, vessel_t):
	text = "# C1\nObserved head model: Example Controls 123\n# S6\nInterceptors absent\n"
	result = answers_module.load_answers(make_answers(tmp_path / "a", control_layout=text))
	assert not result.ok
	assert any("control_layout.md line 2" in message and "textReview" in message for message in messages(result)), messages(result)
	session = session_with(textReview={"reviewedByRole": "operations-manager", "date": "2026-01-16", "acceptedPhrases": ["Example Controls"]})
	reviewed = answers_module.load_answers(make_answers(tmp_path / "b", session=session, control_layout=text))
	assert reviewed.ok, messages(reviewed)
	patch = patch_module.build_patch(reviewed, vessel_t, "vessels/t-class.json")
	assert "Example Controls 123" in patch_module.patch_report(patch)


@pytest.mark.parametrize("review", [
	{"reviewedByRole": "John", "date": "2026-01-16", "acceptedPhrases": ["Example Controls"]},
	{"reviewedByRole": "operations-manager", "acceptedPhrases": ["Example Controls"]},
	{"reviewedByRole": "operations-manager", "date": "2026-01-16", "acceptedPhrases": "Example Controls"},
	{"reviewedByRole": "operations-manager", "date": "2026-01-16"},
])
def test_text_review_needs_an_approver_role_date_and_phrase_list(tmp_path, review):
	text = "# C1\nObserved head model: Example Controls 123\n"
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session_with(textReview=review), control_layout=text))
	assert not result.ok
	assert any("textReview" in message for message in messages(result)), messages(result)


# --- the boundary is enforced again where text leaves the tool ---------------------------------------

def test_build_patch_refuses_private_text_that_bypassed_validation(good_answers, vessel_t):
	loaded = answers_module.load_answers(good_answers)
	assert loaded.ok
	loaded.direct[0].note = "john.smith@example.invalid"
	with pytest.raises(patch_module.PatchError):
		patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")


def test_fit_patch_refuses_private_text_that_bypassed_validation(good_answers, vessel_t):
	loaded = answers_module.load_answers(good_answers)
	loaded.rpm_speed[0].note = "call 212-555-0100"
	with pytest.raises(patch_module.PatchError):
		patch_module.exported_text(loaded.rpm_speed[0].note, loaded)


def test_docs_do_not_claim_general_name_detection():
	for name in ("docs/crew/README.md", "docs/crew/questionnaire.md", "docs/crew/answers-template/README.md"):
		text = (REPO_ROOT / name).read_text().lower()
		assert "looks like a name" not in text, name
		assert "rejects anything that looks like" not in text, name
