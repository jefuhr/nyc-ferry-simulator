"""The residuary resistance fit against the real ferrysim CLI.

Failure modes guarded: the lever chosen for a reported rpm not reproducing that rpm, an unsettled run
read as steady, conflicting (non-monotone) data fitted anyway, a fit accepted although a row sits far
outside its tolerance, the fit writing under vessels/, and the bisection not recovering a known scale.
"""

import json
import math

import pytest

from crew_intake import answers as answers_module
from crew_intake import fit as fit_module
from crew_intake import patch as patch_module
from tests.conftest import GOOD_RPM_SPEED, make_answers


class NoSim:
	def steady_states(self, *args, **kwargs):
		raise AssertionError("the CLI must not run for data that conflicts before fitting")


def rows_from_states(states, offsets=None):
	rows = []
	for index, state in enumerate(states):
		rows.append({
			"port_rpm": f"{state.rpm:.0f}", "starboard_rpm": f"{state.rpm:.0f}", "gear": "ahead",
			"speed_kn": f"{state.stw_kn + (offsets or {}).get(index, 0.0):.2f}", "speed_tolerance_kn": "0.3",
			"speed_reference": "through-water", "load": "half", "wind_kn": "0", "current": "slack", "depth": "deep", "note": "synthetic",
		})
	return rows


def test_lever_for_rpm_is_analytic_and_clamped(vessel_t):
	assert math.isclose(fit_module.lever_for_rpm(vessel_t, 1193.75), 0.5, abs_tol=1e-6)
	assert fit_module.lever_for_rpm(vessel_t, 650) == pytest.approx(0.16)
	assert fit_module.lever_for_rpm(vessel_t, 2100) == pytest.approx(1.0, abs=1e-6)


def test_non_monotone_data_is_refused_before_running(tmp_path, vessel_t):
	rows = [dict(row) for row in GOOD_RPM_SPEED]
	rows[1]["speed_kn"] = "22.0"
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=rows))
	assert loaded.ok
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", NoSim(), tmp_path / "out")
	assert result.status == "refused"
	assert any("monoton" in reason for reason in result.reasons)
	assert result.patch is None


def test_fit_recovers_a_known_scale(tmp_path, vessel_t, sim):
	variant = json.loads(json.dumps(vessel_t))
	variant["hull"]["residuaryResistanceScale"]["value"] = 0.9
	variant_file = tmp_path / "t-class-scale-0.9.json"
	variant_file.write_text(patch_module.format_vessel_json(variant))
	states = sim.steady_states(variant_file, [0.16, 0.5, 0.8, 1.0], tmp_path / "truth")
	assert all(state.settled for state in states)
	assert states[0].rpm == pytest.approx(650, abs=1)
	assert states[1].rpm == pytest.approx(1193.75, abs=1)

	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states)))
	assert loaded.ok
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out")
	assert result.status == "fitted", result.reasons
	assert result.scale == pytest.approx(0.9, abs=0.01)
	assert all(abs(row.residual_kn) < 0.15 for row in result.rows), [row.residual_kn for row in result.rows]
	assert all(abs(row.rpm_sim - row.rpm_reported) <= 5 for row in result.rows)
	change = result.patch["changes"][0]
	assert change["path"] == "hull.residuaryResistanceScale"
	assert change["proposed"]["confidence"] == "crew-reported"
	assert "0.7" in change["proposed"]["note"]
	assert change["previous"]["value"] == 0.7
	assert result.patch["base"]["vesselSha256"] == patch_module.vessel_fingerprint(vessel_t)
	report = fit_module.write_fit_report(result, tmp_path / "out" / "resistance-fit.md")
	assert "SYNTHETIC" in report
	assert "residual" in report.lower()
	assert "## Conditions" in report


def test_fit_refuses_when_a_row_cannot_be_explained(tmp_path, vessel_t, sim):
	stock_file = tmp_path / "t-class-stock.json"
	stock_file.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock_file, [0.5, 1.0], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states, offsets={0: 4.0})))
	assert loaded.ok
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out", max_residual_kn=1.5)
	assert result.status == "refused"
	assert any("residual" in reason for reason in result.reasons)
	assert result.patch is None
	assert result.rows, "residuals are still reported so the user can see the conflict"
