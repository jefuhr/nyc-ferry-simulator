"""Patch generation, provenance and the vessel JSON formatter.

Failure modes guarded: a unit converted wrongly (rpm to rad/s, degrees to radians, percent to a
fraction), the old value or its source lost from the note, a derived parameter computed from the
unpatched value when the patch changes its input, a source key that collides with an existing one,
a patch applied under vessels/ without approval, and a formatter that rewrites lines it did not change.
"""

import json
import math

import pytest

from crew_intake import answers as answers_module
from crew_intake import patch as patch_module
from tests.conftest import GOOD_DIRECT, GOOD_SESSION, VESSELS, make_answers


def change_for(patch, path):
	matches = [change for change in patch["changes"] if change["path"] == path]
	assert len(matches) == 1, f"expected one change at {path}, got {[c['path'] for c in patch['changes']]}"
	return matches[0]


@pytest.fixture
def patch(good_answers, vessel_t):
	loaded = answers_module.load_answers(good_answers)
	assert loaded.ok
	return patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")


def test_source_entry_follows_the_agreed_wording(patch):
	assert patch["sources"] == {
		"crew-synthetic-2026-01-15-captain-a": "Synthetic crew interview (example only, not real data), Captain A, 2026-01-15, T-class",
	}
	for change in patch["changes"]:
		assert change["proposed"]["source"] == "crew-synthetic-2026-01-15-captain-a"
		assert change["proposed"]["confidence"] == "crew-reported"


def test_real_session_source_wording(good_answers, vessel_t):
	session = json.loads(json.dumps(GOOD_SESSION))
	session["sessionId"] = "2026-11-03-captain-a"
	session["synthetic"] = False
	session["date"] = "2026-11-03"
	loaded = answers_module.load_answers(make_answers(good_answers.parent / "real", session=session))
	assert loaded.ok
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["sources"] == {"crew-2026-11-03-captain-a": "Crew interview (anonymised), Captain A, 2026-11-03, T-class"}


def test_idle_rpm_converts_to_rad_per_second_and_keeps_provenance(patch):
	change = change_for(patch, "engines.idleSpeed")
	proposed = change["proposed"]
	assert proposed["unit"] == "rad/s"
	assert math.isclose(proposed["value"], 670 * 2 * math.pi / 60, rel_tol=1e-6)
	assert proposed["asPublished"] == "670 rpm"
	assert "650 rpm" in proposed["note"]
	assert "Baudouin 6M26.3 datasheet idle speed" in proposed["note"]
	assert "Ask crew: engine rpm at idle in gear." in proposed["note"]
	assert "estimated" in proposed["note"]
	assert change["previous"]["value"] == 68.06784


def test_hard_over_angle_converts_degrees(patch):
	proposed = change_for(patch, "controls.hardOverAngle")["proposed"]
	assert proposed["unit"] == "rad"
	assert math.isclose(proposed["value"], math.radians(32), rel_tol=1e-6)
	assert proposed["asPublished"] == "32 deg"


def test_steering_rate_is_derived_from_the_patched_hard_over(patch):
	proposed = change_for(patch, "controls.steeringRate")["proposed"]
	assert proposed["unit"] == "rad/s"
	assert math.isclose(proposed["value"], 2 * math.radians(32) / 8, rel_tol=1e-6)
	assert "8 s" in proposed["asPublished"] and "32" in proposed["asPublished"]
	assert "7 deg/s" in proposed["note"]


def test_detent_percent_becomes_a_fraction(patch):
	proposed = change_for(patch, "controls.clutchInLever")["proposed"]
	assert proposed["unit"] == "1" and math.isclose(proposed["value"], 0.15)


def test_text_parameters_are_patched_as_text(patch):
	proposed = change_for(patch, "controls.steeringMode")["proposed"]
	assert proposed == {
		"value": "follow-up",
		"source": "crew-synthetic-2026-01-15-captain-a",
		"confidence": "crew-reported",
		"note": proposed["note"],
	}
	assert "Previously follow-up" in proposed["note"] and "estimated" in proposed["note"]
	assert change_for(patch, "propellers.rotation")["proposed"]["value"] == "outward-turning"


def test_shift_timing_maps_to_fill_time_and_neutral_dwell(patch):
	fill = change_for(patch, "transmission.clutchFillTime")["proposed"]
	assert fill["unit"] == "s" and math.isclose(fill["value"], 0.5)
	dwell = change_for(patch, "controls.neutralDwell")["proposed"]
	# ahead to astern 3.0 s straight through from idle = release 0.2 (unchanged) + dwell + fill 0.5 (patched)
	assert math.isclose(dwell["value"], 3.0 - 0.2 - 0.5, rel_tol=1e-6)
	assert "3.0 s" in dwell["asPublished"] or "3 s" in dwell["asPublished"]
	assert "2.0 s" in dwell["note"] or "2 s" in dwell["note"]


def test_negative_derived_dwell_is_refused(tmp_path, vessel_t):
	shifts = [{"from_gear": "ahead", "to_gear": "astern", "from_rpm": "650", "seconds": "0.3", "lever_technique": "straight-through", "note": ""}]
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", shifts=shifts))
	assert loaded.ok
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert not any(change["path"] == "controls.neutralDwell" for change in patch["changes"])
	assert any("neutralDwell" in item for item in patch["refused"])


def test_targets_are_reported_but_not_patched(patch):
	assert not any(change["path"].startswith("engines.turbocharger") for change in patch["changes"])
	assert any(target["questionId"] == "E3" for target in patch["targets"])


def test_apply_patch_changes_only_the_patched_paths(patch, vessel_t):
	patched = patch_module.apply_patch(vessel_t, patch)
	assert patched["engines"]["idleSpeed"]["confidence"] == "crew-reported"
	assert patched["sources"]["crew-synthetic-2026-01-15-captain-a"].startswith("Synthetic")
	assert vessel_t["engines"]["idleSpeed"]["confidence"] == "estimated", "apply must not mutate its input"
	before = patch_module.format_vessel_json(vessel_t).splitlines()
	after = patch_module.format_vessel_json(patched).splitlines()
	changed = [line for line in after if line not in before]
	# One line per change, the new source entry, and the previous last source entry gaining a comma.
	assert len(changed) == len(patch["changes"]) + 2, changed
	assert sum(1 for line in changed if "crew-synthetic-2026-01-15-captain-a" in line) == len(patch["changes"]) + 1


def test_apply_refuses_a_colliding_source_key(patch, vessel_t):
	vessel_t["sources"]["crew-synthetic-2026-01-15-captain-a"] = "something else"
	with pytest.raises(patch_module.PatchError):
		patch_module.apply_patch(vessel_t, patch)


def test_apply_refuses_a_stale_previous_value(patch, vessel_t):
	vessel_t["engines"]["idleSpeed"]["value"] = 70.0
	with pytest.raises(patch_module.PatchError):
		patch_module.apply_patch(vessel_t, patch)


@pytest.mark.parametrize("name", ["t-class.json", "k-class.json"])
def test_formatter_round_trips_the_vessel_files_byte_for_byte(name):
	text = (VESSELS / name).read_text()
	assert patch_module.format_vessel_json(patch_module.parse_vessel(text)) == text


def test_write_vessel_refuses_vessels_folder_without_approval(tmp_path, vessel_t):
	with pytest.raises(patch_module.PatchError):
		patch_module.write_vessel(vessel_t, VESSELS / "t-class.json", approved=False)
	out = tmp_path / "t-class.json"
	patch_module.write_vessel(vessel_t, out, approved=False)
	assert json.loads(out.read_text())["id"] == "t-class"


def test_build_patch_without_direct_answers_has_no_changes(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=[], shifts=[]))
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert patch["changes"] == []
