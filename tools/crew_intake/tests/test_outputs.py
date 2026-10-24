"""M7: a refused or failed rerun must not leave an earlier successful patch applicable (review finding M7).

Failure modes guarded: a refused rerun leaving resistance-fit.json or proposed-patch.json from an earlier
answer set beside the refusal report, a stale work/ folder from an earlier run, and apply accepting a
patch whose manifest records a refusal.
"""

import json
import subprocess
import sys

from crew_intake import answers as answers_module
from crew_intake import patch as patch_module
from tests.conftest import GOOD_DIRECT, GOOD_RPM_SPEED, REPO_ROOT, make_answers
from tests.test_fit import rows_from_states

INTAKE = REPO_ROOT / "tools" / "crew_intake" / "intake.py"


def run(*arguments):
	return subprocess.run([sys.executable, str(INTAKE), *arguments], cwd=REPO_ROOT, text=True, capture_output=True)


def test_refused_patch_rerun_removes_the_earlier_proposal(tmp_path, sim):
	folder = make_answers(tmp_path / "answers")
	out = tmp_path / "out"
	first = run("patch", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert first.returncode == 0, first.stdout + first.stderr
	assert (out / "proposed-patch.json").exists()
	assert json.loads((out / "manifest.json").read_text())["runs"]["patch"]["status"] == "proposed"
	direct = [dict(row) for row in GOOD_DIRECT if row["question_id"] != "C3"] + [{"question_id": "C3", "value": "50", "unit": "%", "note": ""}, {"question_id": "C4", "value": "3", "unit": "%", "note": ""}]
	make_answers(folder, direct=direct)
	second = run("patch", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert second.returncode == 1, second.stdout + second.stderr
	assert not (out / "proposed-patch.json").exists(), "the stale success must not survive a refused rerun"
	run_record = json.loads((out / "manifest.json").read_text())["runs"]["patch"]
	assert run_record["status"] == "refused" and run_record["outputs"] == ["patch-report.md"]
	assert "refused" in (out / "patch-report.md").read_text().lower()


def test_refused_fit_rerun_removes_the_earlier_fit_patch(tmp_path, sim, vessel_t):
	"""The reviewer's refusal probe, on two synthetic rows to keep the real fit short."""
	stock = tmp_path / "stock.json"
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	folder = make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states))
	out = tmp_path / "out"
	first = run("fit-resistance", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert first.returncode == 0, first.stdout + first.stderr
	assert (out / "resistance-fit.json").exists() and (out / "work" / "commands.jsonl").exists()
	rows = rows_from_states(states)
	rows[0]["speed_kn"] = "35.0"
	make_answers(folder, rpm_speed=rows)
	second = run("fit-resistance", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert second.returncode == 1, second.stdout + second.stderr
	assert not (out / "resistance-fit.json").exists(), "the stale fit must not survive a refused rerun"
	run_record = json.loads((out / "manifest.json").read_text())["runs"]["fit-resistance"]
	assert run_record["status"] == "refused" and "resistance-fit.json" not in run_record["outputs"]
	assert not (out / "work").exists() or not list((out / "work").glob("scale-*")), "telemetry from the earlier run must not linger"


def test_apply_refuses_a_patch_whose_manifest_records_a_refusal(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers"))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	out = tmp_path / "out"
	out.mkdir()
	(out / "proposed-patch.json").write_text(json.dumps(patch))
	(out / "manifest.json").write_text(json.dumps({"runs": {"patch": {"status": "refused", "outputs": ["patch-report.md"]}}}))
	result = run("apply", "--patch", str(out / "proposed-patch.json"), "--vessel", "vessels/t-class.json", "--out", str(tmp_path / "patched.json"), "--no-build")
	assert result.returncode == 1
	assert "manifest" in result.stdout
	assert not (tmp_path / "patched.json").exists()


def test_patch_and_fit_in_the_same_folder_keep_each_other_applicable(tmp_path, sim, vessel_t):
	"""The default output folder is shared by patch and fit-resistance; a later fit must not mark the proposal stale."""
	stock = tmp_path / "stock.json"
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	folder = make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states))
	out = tmp_path / "out"
	assert run("patch", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build").returncode == 0
	assert run("fit-resistance", str(folder), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build").returncode == 0
	manifest = json.loads((out / "manifest.json").read_text())
	assert set(manifest["runs"]) == {"patch", "fit-resistance"}
	assert (out / "model-check" / "commands.jsonl").exists() and (out / "work" / "commands.jsonl").exists()
	applied = run("apply", "--patch", str(out / "proposed-patch.json"), "--vessel", "vessels/t-class.json", "--out", str(tmp_path / "patched.json"), "--no-build")
	assert applied.returncode == 0, applied.stdout + applied.stderr
	applied = run("apply", "--patch", str(out / "resistance-fit.json"), "--vessel", "vessels/t-class.json", "--out", str(tmp_path / "fitted.json"), "--no-build")
	assert applied.returncode == 0, applied.stdout + applied.stderr
