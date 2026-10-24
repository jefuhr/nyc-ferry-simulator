"""M4: a patch is bound to the vessel it was computed against and the answers it came from (review finding M4).

Failure modes guarded: a derived steering rate applied after the hard-over angle changed, a resistance
fit applied after the propeller changed, a fit made on top of the direct patch applied to the stock
file, an old patch format without binding, and the fingerprint not following the content.
"""

import copy
import json

import pytest

from crew_intake import answers as answers_module
from crew_intake import fit as fit_module
from crew_intake import patch as patch_module
from tests.conftest import GOOD_DIRECT, make_answers
from tests.test_fit import rows_from_states


@pytest.fixture
def steering_patch(tmp_path, vessel_t):
	loaded = answers_module.load_answers(make_answers(tmp_path / "a", direct=[{"question_id": "S3", "value": "10", "unit": "s", "note": "synthetic"}]))
	assert loaded.ok
	return loaded, patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")


def test_patch_records_the_base_fingerprints(steering_patch, vessel_t):
	loaded, patch = steering_patch
	assert patch["base"]["vesselSha256"] == patch_module.vessel_fingerprint(vessel_t)
	assert patch["base"]["answersSha256"] == loaded.fingerprint
	assert len(patch["base"]["vesselSha256"]) == 64 and len(patch["base"]["answersSha256"]) == 64


def test_apply_accepts_the_matching_base(steering_patch, vessel_t):
	patched = patch_module.apply_patch(vessel_t, steering_patch[1])
	assert patched["controls"]["steeringRate"]["confidence"] == "crew-reported"


def test_derived_steering_rate_is_refused_after_the_hard_over_angle_changed(steering_patch, vessel_t):
	"""The reviewer's derived-stale probe: S3 = 10 s at 35 deg would become a 5 s sweep at 17.5 deg."""
	changed = copy.deepcopy(vessel_t)
	changed["controls"]["hardOverAngle"]["value"] = 0.5 * changed["controls"]["hardOverAngle"]["value"]
	with pytest.raises(patch_module.PatchError, match="different version"):
		patch_module.apply_patch(changed, steering_patch[1])


def test_fit_is_refused_after_the_propeller_changed(tmp_path, vessel_t, sim):
	stock = tmp_path / "stock.json"
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states)))
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out")
	assert result.status == "fitted", result.reasons
	assert result.patch["base"]["vesselSha256"] == patch_module.vessel_fingerprint(vessel_t)
	patch_module.apply_patch(vessel_t, result.patch)
	changed = copy.deepcopy(vessel_t)
	changed["propellers"]["pitchRatio"]["value"] = 1.3
	with pytest.raises(patch_module.PatchError, match="different version"):
		patch_module.apply_patch(changed, result.patch)


def test_with_patch_fit_is_bound_to_the_prepatched_vessel(tmp_path, vessel_t, sim):
	"""A fit made on top of the direct patch applies after that patch, never to the stock file."""
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers"))
	direct = patch_module.build_patch(loaded, vessel_t, "vessels/t-class.json")
	prepatched = patch_module.apply_patch(vessel_t, direct)
	stock = tmp_path / "prepatched.json"
	stock.write_text(patch_module.format_vessel_json(prepatched))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers2", rpm_speed=rows_from_states(states)))
	result = fit_module.fit_resistance(loaded, prepatched, "vessels/t-class.json", sim, tmp_path / "out")
	assert result.status == "fitted", result.reasons
	with pytest.raises(patch_module.PatchError, match="different version"):
		patch_module.apply_patch(vessel_t, result.patch)
	assert patch_module.apply_patch(prepatched, result.patch)["hull"]["residuaryResistanceScale"]["confidence"] == "crew-reported"


def test_old_patch_format_is_refused(steering_patch, vessel_t):
	patch = json.loads(json.dumps(steering_patch[1]))
	patch["patchFormat"] = 1
	del patch["base"]
	with pytest.raises(patch_module.PatchError, match="format"):
		patch_module.apply_patch(vessel_t, patch)


def test_missing_or_tampered_base_is_refused(steering_patch, vessel_t):
	patch = json.loads(json.dumps(steering_patch[1]))
	patch["base"]["vesselSha256"] = "0" * 64
	with pytest.raises(patch_module.PatchError):
		patch_module.apply_patch(vessel_t, patch)
	del patch["base"]
	with pytest.raises(patch_module.PatchError):
		patch_module.apply_patch(vessel_t, patch)


def test_answers_fingerprint_follows_the_content(tmp_path):
	first = answers_module.load_answers(make_answers(tmp_path / "a"))
	same = answers_module.load_answers(make_answers(tmp_path / "b"))
	direct = [dict(row) for row in GOOD_DIRECT]
	direct[0]["note"] = "synthetic, but different"
	different = answers_module.load_answers(make_answers(tmp_path / "c", direct=direct))
	assert first.fingerprint == same.fingerprint
	assert first.fingerprint != different.fingerprint


def test_vessel_fingerprint_matches_the_file_bytes(vessel_t):
	import hashlib
	from tests.conftest import VESSELS
	assert patch_module.vessel_fingerprint(vessel_t) == hashlib.sha256((VESSELS / "t-class.json").read_bytes()).hexdigest()
