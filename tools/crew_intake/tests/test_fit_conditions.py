"""M2: the resistance fit must only use observations its calm, deep-water forward model can explain (review finding M2).

Failure modes guarded: windy, shallow or unknown-condition rows fitted as if calm, rows at different
loads averaged into one scale, over-ground speeds in a current, the reviewer's bad-conditions probe
yielding the calm result, and the override being silent.
"""

import json

import pytest

from crew_intake import answers as answers_module
from crew_intake import fit as fit_module
from tests.conftest import GOOD_RPM_SPEED, make_answers, messages, session_with


class NoSim:
	def steady_states(self, *args, **kwargs):
		raise AssertionError("the CLI must not run for data that is refused before fitting")


def rows(**overrides):
	result = [dict(row) for row in GOOD_RPM_SPEED]
	for index, changes in overrides.items():
		result[int(index)].update(changes)
	return result


def test_wind_above_the_calm_limit_excludes_the_row(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows(**{"1": {"wind_kn": "25"}})))
	assert result.ok, messages(result)
	assert [row.fit_usable for row in result.rpm_speed] == [True, False, True]
	assert "wind" in result.rpm_speed[1].fit_exclusion
	assert any("rpm_speed.csv line 3" in message and "wind" in message for message in messages(result, "warning"))


@pytest.mark.parametrize("depth", ["shallow", "unknown"])
def test_shallow_or_unknown_depth_excludes_the_row(tmp_path, depth):
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows(**{"0": {"depth": depth}})))
	assert result.ok, messages(result)
	assert not result.rpm_speed[0].fit_usable and "depth" in result.rpm_speed[0].fit_exclusion


def test_unknown_load_excludes_the_row(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows(**{"0": {"load": "unknown"}})))
	assert result.ok, messages(result)
	assert not result.rpm_speed[0].fit_usable and "load" in result.rpm_speed[0].fit_exclusion


def test_empty_row_conditions_inherit_from_the_session(tmp_path):
	blank = rows(**{"0": {"wind_kn": "", "current": "", "depth": ""}})
	result = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=blank))
	assert result.ok, messages(result)
	assert result.rpm_speed[0].fit_usable and result.rpm_speed[0].wind_kn == 8 and result.rpm_speed[0].depth == "deep"
	session = session_with()
	session["conditions"]["windKn"] = None
	result = answers_module.load_answers(make_answers(tmp_path / "b", session=session, rpm_speed=blank))
	assert result.ok, messages(result)
	assert not result.rpm_speed[0].fit_usable and "wind" in result.rpm_speed[0].fit_exclusion


def test_the_reviewers_bad_conditions_probe_is_refused(tmp_path, vessel_t):
	"""60 kn of wind, shallow water and full load in the session; rows inherit and must not be fitted as calm."""
	session = session_with(conditions={"load": "full", "windKn": 60, "windFrom": "N", "current": "slack", "currentKn": 0, "tide": "", "depth": "shallow", "notes": ""})
	blank = rows(**{str(index): {"wind_kn": "", "current": "", "depth": ""} for index in range(3)})
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", session=session, rpm_speed=blank))
	assert loaded.ok, messages(loaded)
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", NoSim(), tmp_path / "out")
	assert result.status == "refused" and result.scale is None
	assert any("wind" in reason or "depth" in reason or "usable" in reason for reason in result.reasons), result.reasons
	assert len(result.excluded) == 3


def test_free_text_depth_in_the_session_is_rejected(tmp_path):
	session = session_with()
	session["conditions"]["depth"] = "shallow 1.5 m"
	result = answers_module.load_answers(make_answers(tmp_path / "a", session=session))
	assert any("conditions.depth" in message for message in messages(result)), messages(result)


def test_mixed_loads_are_refused_without_a_flag(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows(**{"2": {"load": "full"}})))
	assert loaded.ok, messages(loaded)
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", NoSim(), tmp_path / "out")
	assert result.status == "refused"
	assert any("load" in reason and "--load" in reason for reason in result.reasons), result.reasons


def test_load_selection_groups_the_rows(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows(**{"2": {"load": "full"}})))
	selected, excluded, reasons, conditions = fit_module.select_fit_rows(loaded, vessel_t, load="half")
	assert reasons == []
	assert [row.line for row in selected] == [2, 3]
	assert [(row.line, reason) for row, reason in excluded] == [(4, "load full is not the selected load half")]
	assert conditions["load"] == "half" and conditions["override"] is False


def test_override_fits_mixed_rows_and_says_so(tmp_path, vessel_t):
	mixed = rows(**{"1": {"wind_kn": "25"}, "2": {"load": "full", "depth": "unknown"}})
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=mixed))
	selected, excluded, reasons, conditions = fit_module.select_fit_rows(loaded, vessel_t, allow_mixed_conditions=True)
	assert reasons == [] and excluded == []
	assert [row.line for row in selected] == [2, 3, 4]
	assert conditions["override"] is True and sorted(conditions["loads"]) == ["full", "half"]
	assert conditions["maxWindKn"] == 25


def test_override_never_admits_astern_or_current_affected_rows(tmp_path, vessel_t):
	session = session_with()
	session["conditions"]["current"] = "ebb"
	session["conditions"]["currentKn"] = 1.5
	flowing = rows(**{"2": {"current": "ebb"}}) + [{"port_rpm": "900", "starboard_rpm": "900", "gear": "astern", "speed_kn": "4", "speed_tolerance_kn": "1", "speed_reference": "through-water", "load": "half", "wind_kn": "5", "current": "slack", "depth": "deep", "note": ""}]
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", session=session, rpm_speed=flowing))
	assert loaded.ok, messages(loaded)
	selected, excluded, reasons, conditions = fit_module.select_fit_rows(loaded, vessel_t, allow_mixed_conditions=True)
	assert [row.line for row in selected] == [2, 3]
	assert sorted(row.line for row, _ in excluded) == [4, 5]


def test_fit_patch_and_report_record_the_conditions_used(tmp_path, vessel_t, sim):
	"""One real fit on two synthetic rows; the conditions block must name load, wind limit and depth."""
	from tests.test_fit import rows_from_states
	stock = tmp_path / "stock.json"
	from crew_intake import patch as patch_module
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states)))
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out")
	assert result.status == "fitted", result.reasons
	conditions = result.patch["fit"]["conditions"]
	assert conditions["load"] == "half" and conditions["depth"] == "deep" and conditions["calmWindLimitKn"] == 10
	assert conditions["override"] is False
	report = fit_module.write_fit_report(result, tmp_path / "out" / "resistance-fit.md")
	assert "## Conditions" in report and "half" in report
