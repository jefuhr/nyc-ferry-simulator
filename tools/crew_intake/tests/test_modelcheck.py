"""M1: individually plausible control answers must not combine into an unusable vessel (review finding M1).

Failure modes guarded: a throttle start below the clutch-in detent (the core refuses to construct the
model), a shift interlock below idle (the real CLI loads the file but the shift never completes), a patch
with a failed model check being applied, and the stock vessel wrongly failing the sanity run.
"""

import copy
import json
import subprocess
import sys

import pytest

from crew_intake import answers as answers_module
from crew_intake import modelcheck
from crew_intake import patch as patch_module
from crew_intake.mapping import RPM
from tests.conftest import GOOD_DIRECT, REPO_ROOT, make_answers

INTAKE = REPO_ROOT / "tools" / "crew_intake" / "intake.py"


def run(*arguments):
	return subprocess.run([sys.executable, str(INTAKE), *arguments], cwd=REPO_ROOT, text=True, capture_output=True)


def direct_with(*rows):
	return [dict(row) for row in GOOD_DIRECT if row["question_id"] not in {r["question_id"] for r in rows}] + [dict(row) for row in rows]


def test_throttle_start_below_clutch_in_is_refused(tmp_path, vessel_t):
	direct = direct_with({"question_id": "C3", "value": "50", "unit": "%", "note": ""}, {"question_id": "C4", "value": "3", "unit": "%", "note": ""})
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert loaded.ok
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["modelCheck"]["ok"] is False
	assert any("throttleStartLever" in reason and "clutchInLever" in reason for reason in patch["modelCheck"]["reasons"]), patch["modelCheck"]
	with pytest.raises(patch_module.PatchError):
		patch_module.apply_patch(vessel_t, patch)


def test_interlock_below_idle_is_refused_by_the_invariants(tmp_path, vessel_t):
	direct = direct_with({"question_id": "C9", "value": "300", "unit": "rpm", "note": ""})
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert loaded.ok
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["modelCheck"]["ok"] is False
	assert any("shiftInterlockSpeed" in reason and "idleSpeed" in reason for reason in patch["modelCheck"]["reasons"]), patch["modelCheck"]


def test_good_patch_passes_the_invariants(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a"))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["modelCheck"] == {"ok": True, "reasons": [], "simulated": False}


def test_pitch_ratio_outside_the_core_bounds_is_refused(tmp_path, vessel_t):
	# 60 in pitch on the 38 in propeller is P/D 1.58, which VesselDynamicsSimulation refuses (0.5 to 1.4).
	direct = direct_with({"question_id": "E7", "value": "60", "unit": "in", "note": ""})
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	assert loaded.ok
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert not any(change["path"] == "propellers.pitchRatio" for change in patch["changes"])
	assert any("pitchRatio" in item and "1.4" in item for item in patch["refused"]), patch["refused"]


def test_stock_vessel_passes_the_sanity_run(tmp_path, vessel_t, sim):
	assert modelcheck.check_invariants(vessel_t) == []
	assert modelcheck.sanity_run(sim, vessel_t, tmp_path / "work") == []


def test_interlock_below_idle_immobilises_the_boat_in_the_real_cli(tmp_path, vessel_t, sim):
	"""The reviewer's low-interlock probe: the file loads, exit 0, but the gear stays 'shifting' for ever."""
	vessel = copy.deepcopy(vessel_t)
	vessel["controls"]["shiftInterlockSpeed"]["value"] = 300 * RPM
	reasons = modelcheck.sanity_run(sim, vessel, tmp_path / "work")
	assert reasons, "the sanity run must notice that the shift never completes"
	assert any("shifting" in reason or "never" in reason for reason in reasons), reasons
	assert not list((tmp_path / "work").glob("*.json")), "the candidate vessel file must be cleaned up"


def test_cross_field_failure_in_the_real_cli_is_reported_with_the_core_message(tmp_path, vessel_t, sim):
	"""The reviewer's cross-controls probe: ShiftSettings refuses throttleStartLever below clutchInLever."""
	vessel = copy.deepcopy(vessel_t)
	vessel["controls"]["clutchInLever"]["value"] = 0.5
	vessel["controls"]["throttleStartLever"]["value"] = 0.03
	reasons = modelcheck.sanity_run(sim, vessel, tmp_path / "work")
	assert any("throttleStartLever" in reason for reason in reasons), reasons


def test_cli_patch_refuses_an_unusable_combination(tmp_path, sim):
	direct = direct_with({"question_id": "C3", "value": "50", "unit": "%", "note": ""}, {"question_id": "C4", "value": "3", "unit": "%", "note": ""})
	folder = make_answers(tmp_path / "answers", direct=direct)
	out = tmp_path / "out"
	result = run("patch", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert result.returncode == 1, result.stdout + result.stderr
	assert "throttleStartLever" in result.stdout
	assert not (out / "proposed-patch.json").exists()
	assert "refused" in (out / "patch-report.md").read_text().lower()
	manifest = json.loads((out / "manifest.json").read_text())
	assert manifest["runs"]["patch"]["status"] == "refused"


def test_cli_patch_runs_the_sanity_check_for_a_good_set(tmp_path, sim):
	folder = make_answers(tmp_path / "answers")
	out = tmp_path / "out"
	result = run("patch", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert result.returncode == 0, result.stdout + result.stderr
	patch = json.loads((out / "proposed-patch.json").read_text())
	assert patch["modelCheck"]["ok"] is True and patch["modelCheck"]["simulated"] is True


def test_cli_apply_refuses_a_patch_with_a_failed_model_check(tmp_path, vessel_t):
	direct = direct_with({"question_id": "C9", "value": "300", "unit": "rpm", "note": ""})
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	patch_file = tmp_path / "bad-patch.json"
	patch_file.write_text(json.dumps(patch))
	result = run("apply", "--patch", str(patch_file), "--vessel", "vessels/t-class.json", "--out", str(tmp_path / "out.json"), "--no-build")
	assert result.returncode == 1
	assert "shiftInterlockSpeed" in result.stdout
	assert not (tmp_path / "out.json").exists()
