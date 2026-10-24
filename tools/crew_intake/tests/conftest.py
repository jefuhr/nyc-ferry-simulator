"""Shared fixtures: synthetic answer sets, repo paths and a guard that nothing writes to vessels/."""

import csv
import hashlib
import json
import shutil
from pathlib import Path

import pytest

from crew_intake import ferrysim
from crew_intake import patch as patch_module

REPO_ROOT = Path(__file__).resolve().parents[3]
VESSELS = REPO_ROOT / "vessels"
EXAMPLE = REPO_ROOT / "docs" / "crew" / "example-synthetic"
TEMPLATE = REPO_ROOT / "docs" / "crew" / "answers-template"

GOOD_SESSION = {
	"sessionId": "synthetic-2026-01-15-captain-a",
	"synthetic": True,
	"date": "2026-01-15",
	"respondentRole": "captain",
	"respondentCode": "A",
	"respondentExperience": "",
	"vesselClass": "t-class",
	"variant": "river",
	"hull": "",
	"method": "interview",
	"conditions": {"load": "half", "windKn": 8, "windFrom": "SW", "current": "slack", "currentKn": 0, "tide": "high water", "depth": "deep", "notes": ""},
	"consent": {"respondentAgreed": True, "operatorApproved": True, "approvedByRole": "operations-manager", "approvalDate": "2026-01-10", "notes": "synthetic"},
	"photos": "outside the repo",
	"notes": "SYNTHETIC test data",
}

GOOD_DIRECT = [
	{"question_id": "E1", "value": "670", "unit": "rpm", "note": "synthetic"},
	{"question_id": "S2", "value": "32", "unit": "deg", "note": "synthetic"},
	{"question_id": "S3", "value": "8", "unit": "s", "note": "synthetic"},
	{"question_id": "C3", "value": "15", "unit": "%", "note": "synthetic"},
	{"question_id": "S1", "value": "follow-up", "unit": "text", "note": "synthetic"},
	{"question_id": "E8", "value": "outward-turning", "unit": "text", "note": "synthetic"},
	{"question_id": "E3", "value": "6", "unit": "s", "note": "synthetic target, not a parameter"},
]

GOOD_SHIFTS = [
	{"from_gear": "neutral", "to_gear": "ahead", "from_rpm": "650", "seconds": "0.5", "lever_technique": "straight-through", "note": "synthetic"},
	{"from_gear": "ahead", "to_gear": "astern", "from_rpm": "650", "seconds": "3.0", "lever_technique": "straight-through", "note": "synthetic"},
]

GOOD_RPM_SPEED = [
	{"port_rpm": "650", "starboard_rpm": "650", "gear": "ahead", "speed_kn": "7.5", "speed_tolerance_kn": "0.5", "speed_reference": "through-water", "load": "half", "wind_kn": "5", "current": "slack", "depth": "deep", "note": "synthetic"},
	{"port_rpm": "1200", "starboard_rpm": "1200", "gear": "ahead", "speed_kn": "13.0", "speed_tolerance_kn": "0.5", "speed_reference": "through-water", "load": "half", "wind_kn": "5", "current": "slack", "depth": "deep", "note": "synthetic"},
	{"port_rpm": "1750", "starboard_rpm": "1750", "gear": "ahead", "speed_kn": "20.5", "speed_tolerance_kn": "0.5", "speed_reference": "over-ground", "load": "half", "wind_kn": "5", "current": "slack", "depth": "deep", "note": "synthetic"},
]

GOOD_STOPPING = [
	{"initial_speed_kn": "25", "method": "full-astern", "distance_m": "100", "distance_boat_lengths": "", "time_s": "15", "bow_swing": "starboard", "note": "synthetic"},
]
GOOD_TURNING = [
	{"speed_kn": "25", "helm": "hard-over", "rudder_deg": "", "diameter_m": "140", "time_to_180_s": "25", "speed_in_turn_kn": "18", "note": "synthetic"},
]
GOOD_TWISTING = [
	{"ahead_engine": "port", "ahead_rpm": "650", "astern_rpm": "650", "yaw_rate_deg_s": "2.5", "time_to_180_s": "", "walk": "forward and slightly to starboard", "note": "synthetic"},
]


def write_csv(path: Path, rows, columns):
	with path.open("w", newline="") as handle:
		writer = csv.DictWriter(handle, fieldnames=columns)
		writer.writeheader()
		for row in rows:
			writer.writerow({column: row.get(column, "") for column in columns})


COLUMNS = {
	"rpm_speed.csv": ["port_rpm", "starboard_rpm", "gear", "speed_kn", "speed_tolerance_kn", "speed_reference", "load", "wind_kn", "current", "depth", "note"],
	"shift_timing.csv": ["from_gear", "to_gear", "from_rpm", "seconds", "lever_technique", "note"],
	"stopping.csv": ["initial_speed_kn", "method", "distance_m", "distance_boat_lengths", "time_s", "bow_swing", "note"],
	"turning.csv": ["speed_kn", "helm", "rudder_deg", "diameter_m", "time_to_180_s", "speed_in_turn_kn", "note"],
	"twisting.csv": ["ahead_engine", "ahead_rpm", "astern_rpm", "yaw_rate_deg_s", "time_to_180_s", "walk", "note"],
	"direct_answers.csv": ["question_id", "value", "unit", "note"],
}


def make_answers(folder: Path, session=None, direct=None, shifts=None, rpm_speed=None, stopping=None, turning=None, twisting=None, columns=None, control_layout=None, landing_procedures=None):
	"""Writes a complete synthetic answer folder. Pass a list to replace a table, or None for the good default."""
	folder.mkdir(parents=True, exist_ok=True)
	(folder / "session.json").write_text(json.dumps(GOOD_SESSION if session is None else session, indent="\t") + "\n")
	tables = {
		"rpm_speed.csv": GOOD_RPM_SPEED if rpm_speed is None else rpm_speed,
		"shift_timing.csv": GOOD_SHIFTS if shifts is None else shifts,
		"stopping.csv": GOOD_STOPPING if stopping is None else stopping,
		"turning.csv": GOOD_TURNING if turning is None else turning,
		"twisting.csv": GOOD_TWISTING if twisting is None else twisting,
		"direct_answers.csv": GOOD_DIRECT if direct is None else direct,
	}
	for name, rows in tables.items():
		write_csv(folder / name, rows, (columns or {}).get(name, COLUMNS[name]))
	(folder / "control_layout.md").write_text(control_layout if control_layout is not None else "# Control layout (synthetic)\n\nNo photos; synthetic.\n")
	(folder / "landing_procedures.md").write_text(landing_procedures if landing_procedures is not None else "# Landing procedures (synthetic)\n\nSynthetic.\n")
	return folder


def session_with(**changes) -> dict:
	"""A deep copy of GOOD_SESSION with top-level keys replaced (a value of ... deletes the key)."""
	session = json.loads(json.dumps(GOOD_SESSION))
	for key, value in changes.items():
		if value is ...:
			session.pop(key, None)
		else:
			session[key] = value
	return session


def messages(answer_set, level="error"):
	return [str(problem) for problem in answer_set.problems if problem.level == level]


def vessel_hashes():
	return {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(VESSELS.glob("*.json"))}


@pytest.fixture(scope="session", autouse=True)
def vessels_untouched():
	"""Nothing in this suite may write to vessels/. Checked once at the end of the session."""
	before = vessel_hashes()
	listing = sorted(path.name for path in VESSELS.iterdir())
	yield
	assert vessel_hashes() == before, "a test modified a file under vessels/"
	assert sorted(path.name for path in VESSELS.iterdir()) == listing, "a test added or removed a file under vessels/"


@pytest.fixture
def vessel_t():
	return patch_module.load_vessel(VESSELS / "t-class.json")


@pytest.fixture
def good_answers(tmp_path):
	return make_answers(tmp_path / "answers")


@pytest.fixture(scope="session")
def sim():
	"""The real ferrysim CLI, built fresh once per session. Skips (loudly) when dotnet is missing."""
	if shutil.which("dotnet") is None:
		pytest.skip("dotnet is not installed, so the ferrysim CLI cannot run")
	runner = ferrysim.FerrySim(REPO_ROOT)
	try:
		runner.ensure_built()
	except ferrysim.FerrySimError as error:
		pytest.fail(f"the ferrysim CLI could not be built or verified: {error}")
	return runner
