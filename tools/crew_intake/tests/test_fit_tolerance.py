"""M3: contradictory observations outside their own tolerances must refuse the fit (review finding M3).

Failure modes guarded: two rows at the same rpm with disjoint speed intervals being averaged, the
undocumented 0.5 kn allowance hiding that, and a fitted curve accepted although a row's residual exceeds
the row's stated tolerance while staying under the global cap.
"""

from crew_intake import answers as answers_module
from crew_intake import fit as fit_module
from crew_intake import patch as patch_module
from tests.conftest import make_answers, messages
from tests.test_fit import rows_from_states


class NoSim:
	def steady_states(self, *args, **kwargs):
		raise AssertionError("the CLI must not run for data that conflicts before fitting")


def same_rpm_rows(first_kn, second_kn, tolerance):
	return [
		{"port_rpm": "1100", "starboard_rpm": "1100", "gear": "ahead", "speed_kn": str(first_kn), "speed_tolerance_kn": str(tolerance), "speed_reference": "through-water", "load": "half", "wind_kn": "0", "current": "slack", "depth": "deep", "note": "synthetic"},
		{"port_rpm": "1100", "starboard_rpm": "1100", "gear": "ahead", "speed_kn": str(second_kn), "speed_tolerance_kn": str(tolerance), "speed_reference": "through-water", "load": "half", "wind_kn": "0", "current": "slack", "depth": "deep", "note": "synthetic"},
	]


def test_disjoint_intervals_at_the_same_rpm_are_refused_before_running(tmp_path, vessel_t):
	"""The reviewer's tight-conflict probe: 12.0 ±0.1 and 12.6 ±0.1 kn at 1100 rpm cannot both hold."""
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", rpm_speed=same_rpm_rows(12.0, 12.6, 0.1)))
	assert loaded.ok, messages(loaded)
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", NoSim(), tmp_path / "out")
	assert result.status == "refused" and result.patch is None
	assert any("line 2" in reason and "line 3" in reason for reason in result.reasons), result.reasons


def test_overlapping_intervals_at_the_same_rpm_are_consistent():
	rows = [fit_module.FitRow(2, 1100, 12.0, 0.5, 0.45), fit_module.FitRow(3, 1100, 12.6, 0.5, 0.45)]
	assert fit_module.check_consistency(rows) == []
	rows = [fit_module.FitRow(2, 1100, 12.0, 0.1, 0.45), fit_module.FitRow(3, 1100, 12.6, 0.1, 0.45)]
	assert fit_module.check_consistency(rows)


def test_residual_outside_the_stated_tolerance_refuses_the_fit(tmp_path, vessel_t, sim):
	"""Two rows the stock curve cannot both match within ±0.3 kn, although both stay under the 1.5 kn cap."""
	stock = tmp_path / "stock.json"
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states, offsets={0: 0.5, 1: -0.5})))
	assert loaded.ok, messages(loaded)
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out", max_residual_kn=1.5)
	assert result.status == "refused", result.scale
	assert result.patch is None
	assert any("tolerance" in reason for reason in result.reasons), result.reasons
	assert all(abs(row.residual_kn) < 1.5 for row in result.rows), "the global cap alone would have accepted this"
	report = fit_module.write_fit_report(result, tmp_path / "out" / "resistance-fit.md")
	assert "refused" in report.lower() and "tolerance" in report.lower()
