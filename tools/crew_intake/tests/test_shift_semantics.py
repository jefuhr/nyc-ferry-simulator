"""M5: a captain's deliberate pause or an interlock wait is not the automatic neutral dwell (review finding M5).

Failure modes guarded: a reversal with a recorded pause asserted as controls.neutralDwell, a reversal from
high rpm (interlock wait) asserted as dwell, a reversal without the starting rpm or with an unknown
technique asserted as dwell, inconsistent reversal timings averaged into one value, and free-text
technique silently accepted.
"""

from crew_intake import answers as answers_module
from crew_intake import patch as patch_module
from tests.conftest import make_answers, messages


def shift(from_gear, to_gear, seconds, technique="straight-through", from_rpm="650", note="synthetic"):
	return {"from_gear": from_gear, "to_gear": to_gear, "from_rpm": from_rpm, "seconds": seconds, "lever_technique": technique, "note": note}


def dwell_changes(patch):
	return [change for change in patch["changes"] if change["path"] == "controls.neutralDwell"]


def dwell_targets(patch):
	return [target for target in patch["targets"] if target["questionId"] == "C11"]


def build(tmp_path, vessel_t, shifts):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", shifts=shifts))
	assert loaded.ok, messages(loaded)
	return patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")


def test_straight_through_reversal_from_idle_derives_the_dwell(tmp_path, vessel_t):
	patch = build(tmp_path, vessel_t, [shift("neutral", "ahead", "0.4"), shift("ahead", "astern", "3.0")])
	assert len(dwell_changes(patch)) == 1
	assert abs(dwell_changes(patch)[0]["proposed"]["value"] - (3.0 - 0.2 - 0.4)) < 1e-9


def test_paused_reversal_is_reported_not_derived(tmp_path, vessel_t):
	"""The reviewer's manual-pause probe: 7.6 s including a 5 s pause produced neutralDwell = 7.0 s."""
	patch = build(tmp_path, vessel_t, [shift("neutral", "ahead", "0.4"), shift("ahead", "astern", "7.6", technique="paused-in-neutral", note="held neutral for about 5 seconds")])
	assert dwell_changes(patch) == []
	targets = dwell_targets(patch)
	assert len(targets) == 1 and "pause" in targets[0]["informs"].lower()
	assert "7.6" in targets[0]["value"]


def test_reversal_from_high_rpm_is_reported_as_an_interlock_wait(tmp_path, vessel_t):
	patch = build(tmp_path, vessel_t, [shift("ahead", "astern", "6.0", from_rpm="1500")])
	assert dwell_changes(patch) == []
	assert any("interlock" in target["informs"].lower() for target in dwell_targets(patch)), patch["targets"]


def test_reversal_without_a_starting_rpm_is_not_derived(tmp_path, vessel_t):
	patch = build(tmp_path, vessel_t, [shift("ahead", "astern", "3.0", from_rpm="")])
	assert dwell_changes(patch) == []
	assert any("from_rpm" in target["informs"] for target in dwell_targets(patch)), patch["targets"]


def test_unknown_technique_is_not_derived(tmp_path, vessel_t):
	patch = build(tmp_path, vessel_t, [shift("ahead", "astern", "3.0", technique="unknown")])
	assert dwell_changes(patch) == []
	assert dwell_targets(patch)


def test_inconsistent_reversals_are_refused_instead_of_averaged(tmp_path, vessel_t):
	patch = build(tmp_path, vessel_t, [shift("ahead", "astern", "2.8"), shift("astern", "ahead", "4.5")])
	assert dwell_changes(patch) == []
	assert any("neutralDwell" in item and "2.8" in item and "4.5" in item for item in patch["refused"]), patch["refused"]


def test_inconsistent_fills_are_refused_instead_of_averaged(tmp_path, vessel_t):
	patch = build(tmp_path, vessel_t, [shift("neutral", "ahead", "0.3"), shift("neutral", "astern", "1.4")])
	assert not any(change["path"] == "transmission.clutchFillTime" for change in patch["changes"])
	assert any("clutchFillTime" in item for item in patch["refused"]), patch["refused"]


def test_free_text_technique_is_rejected(tmp_path):
	result = answers_module.load_answers(make_answers(tmp_path / "a", shifts=[shift("ahead", "astern", "7.6", technique="held neutral for 5 seconds")]))
	assert not result.ok
	assert any("lever_technique" in message and "straight-through" in message for message in messages(result)), messages(result)


def test_interlock_from_the_same_patch_is_used(tmp_path, vessel_t):
	"""With C9 lowered to 700 rpm in the same answer set, a reversal from 800 rpm waits for the interlock."""
	direct = [{"question_id": "C9", "value": "700", "unit": "rpm", "note": "synthetic"}]
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=direct, shifts=[shift("ahead", "astern", "3.0", from_rpm="800")]))
	assert loaded.ok, messages(loaded)
	patch = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	assert dwell_changes(patch) == []
	assert any("interlock" in target["informs"].lower() for target in dwell_targets(patch))
