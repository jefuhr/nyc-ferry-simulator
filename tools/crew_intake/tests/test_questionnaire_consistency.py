"""m2: the questionnaire and its coverage table describe only what the tool captures (review finding m2).

Failure modes guarded: a question id in the docs that the importer does not know (or the reverse), blade
count asked without a field that accepts it, the gearbox model lost behind the ratio, and the coverage
table claiming complete capture for rows the reviewer showed are partial.
"""

import re

from crew_intake import answers as answers_module
from crew_intake import patch as patch_module
from crew_intake.mapping import QUESTIONS
from tests.conftest import REPO_ROOT, make_answers, messages

QUESTIONNAIRE = (REPO_ROOT / "docs" / "crew" / "questionnaire.md").read_text()
TEMPLATE_README = (REPO_ROOT / "docs" / "crew" / "answers-template" / "README.md").read_text()


def test_every_question_in_the_docs_is_known_and_vice_versa():
	documented = set(re.findall(r"^\*\*([A-Z]\d{1,2})\b", QUESTIONNAIRE, flags=re.MULTILINE))
	assert documented == set(QUESTIONS), (documented ^ set(QUESTIONS))


def test_blade_count_has_its_own_field(tmp_path, vessel_t):
	assert QUESTIONS["E11"].path == "machinery.propellerBlades"
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=[{"question_id": "E11", "value": "5", "unit": "count", "note": "synthetic"}]))
	assert loaded.ok, messages(loaded)
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	change = [change for change in patch["changes"] if change["path"] == "machinery.propellerBlades"]
	assert len(change) == 1 and change[0]["proposed"]["value"] == 5 and change[0]["proposed"]["unit"] == "count"
	assert "E11" in TEMPLATE_README


def test_gearbox_model_has_its_own_text_field(tmp_path, vessel_t):
	assert QUESTIONS["E12"].path == "machinery.gearboxModel"
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=[{"question_id": "E12", "value": "ZF 2050", "unit": "text", "note": "synthetic"}]))
	assert loaded.ok, messages(loaded)
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert any(change["path"] == "machinery.gearboxModel" and change["proposed"]["value"] == "ZF 2050" for change in patch["changes"])


def test_e9_no_longer_offers_blade_count_as_an_alternative():
	e9 = re.search(r"^\*\*E9 .*?(?=^\*\*E10)", QUESTIONNAIRE, flags=re.MULTILINE | re.DOTALL).group(0)
	assert "blade count" not in e9.lower() or "E11" in e9


def test_coverage_table_marks_partial_rows():
	coverage = QUESTIONNAIRE[QUESTIONNAIRE.index("## Coverage"):]
	for fragment in ("Instruments", "trim", "Rudder geometry", "synchroni", "underwater", "current measurement"):
		row = next((line for line in coverage.splitlines() if fragment.lower() in line.lower()), None)
		assert row is not None, fragment
		assert "partial" in row.lower(), row
	assert "Partial" in coverage and "Deferred" in coverage


def test_questionnaire_does_not_ask_for_a_manoeuvre():
	assert "Do not ask anyone to perform manoeuvres" in QUESTIONNAIRE
