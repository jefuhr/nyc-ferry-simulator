"""The command line: validate, patch and apply on the committed synthetic example.

Failure modes guarded: the shipped example failing validation, the blank template validating as if it
were a session (review finding B1), a patch run writing anywhere but the requested output folder, and
apply touching vessels/ without --approved.
"""

import json
import subprocess
import sys

import pytest

from tests.conftest import EXAMPLE, REPO_ROOT, TEMPLATE, VESSELS

INTAKE = REPO_ROOT / "tools" / "crew_intake" / "intake.py"


def run(*arguments, cwd=REPO_ROOT):
	return subprocess.run([sys.executable, str(INTAKE), *arguments], cwd=cwd, text=True, capture_output=True)


def test_help_lists_the_commands():
	result = run("--help")
	assert result.returncode == 0
	for command in ["validate", "patch", "fit-resistance", "apply"]:
		assert command in result.stdout


def test_validate_the_synthetic_example():
	result = run("validate", str(EXAMPLE))
	assert result.returncode == 0, result.stdout + result.stderr
	assert "0 error" in result.stdout


def test_validate_the_blank_template_is_rejected_until_filled_in():
	"""A template is not a session: its consent block must fail validation until a person fills it in."""
	result = run("validate", str(TEMPLATE))
	assert result.returncode == 1, result.stdout + result.stderr
	for field in ("operatorApproved", "respondentAgreed", "approvedByRole", "approvalDate"):
		assert field in result.stdout, field
	assert "placeholder" in result.stdout


def test_validate_reports_errors_with_exit_code_1(tmp_path):
	folder = tmp_path / "bad"
	folder.mkdir()
	(folder / "session.json").write_text("{}\n")
	result = run("validate", str(folder))
	assert result.returncode == 1
	assert "error" in result.stdout


def test_patch_writes_proposal_and_report_to_the_output_folder(tmp_path, sim):
	out = tmp_path / "out"
	result = run("patch", str(EXAMPLE), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build")
	assert result.returncode == 0, result.stdout + result.stderr
	patch = json.loads((out / "proposed-patch.json").read_text())
	assert patch["vessel"] == "vessels/t-class.json"
	assert patch["changes"]
	assert (out / "patch-report.md").exists()
	assert all(change["proposed"]["confidence"] == "crew-reported" for change in patch["changes"])


def test_apply_needs_approval_for_vessels_folder(tmp_path, sim):
	out = tmp_path / "out"
	assert run("patch", str(EXAMPLE), "--vessel", "vessels/t-class.json", "--out", str(out), "--no-build").returncode == 0
	refused = run("apply", "--patch", str(out / "proposed-patch.json"), "--vessel", "vessels/t-class.json", "--out", str(VESSELS / "t-class.json"), "--no-build")
	assert refused.returncode == 1
	assert "--approved" in refused.stdout + refused.stderr
	applied = run("apply", "--patch", str(out / "proposed-patch.json"), "--vessel", "vessels/t-class.json", "--out", str(tmp_path / "t-class.patched.json"), "--no-build")
	assert applied.returncode == 0, applied.stdout + applied.stderr
	patched = json.loads((tmp_path / "t-class.patched.json").read_text())
	assert patched["engines"]["idleSpeed"]["confidence"] == "crew-reported"


def test_patch_refuses_an_output_folder_under_vessels():
	result = run("patch", str(EXAMPLE), "--vessel", "vessels/t-class.json", "--out", str(VESSELS / "proposals"), "--no-build")
	assert result.returncode == 1
	assert not (VESSELS / "proposals").exists()
