"""m3 and m4: the simulator the fit runs is the one the source describes, and temporary variants go away.

Failure modes guarded: a stale ferrysim.dll silently used as the forward model, no record of which binary
produced a fit, and parameter variants left behind after success or refusal.
"""

import os
import time

import pytest

from crew_intake import answers as answers_module
from crew_intake import ferrysim
from crew_intake import fit as fit_module
from crew_intake import patch as patch_module
from tests.conftest import REPO_ROOT, make_answers
from tests.test_fit import rows_from_states


def fake_repo(tmp_path, dll_age_s, source_age_s):
	root = tmp_path / "repo"
	dll = root / ferrysim.CLI_PROJECT / "bin" / "Debug" / "net10.0" / "ferrysim.dll"
	source = root / "core" / "FerrySim.Core" / "Dynamics" / "HullModel.cs"
	dll.parent.mkdir(parents=True)
	source.parent.mkdir(parents=True)
	dll.write_bytes(b"not a real dll")
	source.write_text("// source\n")
	now = time.time()
	os.utime(dll, (now - dll_age_s, now - dll_age_s))
	os.utime(source, (now - source_age_s, now - source_age_s))
	return root, dll, source


def test_a_dll_older_than_the_source_is_refused(tmp_path):
	root, dll, source = fake_repo(tmp_path, dll_age_s=600, source_age_s=60)
	with pytest.raises(ferrysim.FerrySimError, match="older"):
		ferrysim.FerrySim(root).ensure_built(build=False)


def test_a_dll_newer_than_every_source_is_accepted_and_identified(tmp_path):
	root, dll, source = fake_repo(tmp_path, dll_age_s=60, source_age_s=600)
	sim = ferrysim.FerrySim(root)
	assert sim.ensure_built(build=False) == dll
	assert sim.identity["sha256"] and len(sim.identity["sha256"]) == 64
	assert sim.identity["dll"] == str(dll.relative_to(root))


def test_a_missing_dll_without_building_is_an_error(tmp_path):
	root, dll, source = fake_repo(tmp_path, dll_age_s=60, source_age_s=600)
	dll.unlink()
	with pytest.raises(ferrysim.FerrySimError):
		ferrysim.FerrySim(root).ensure_built(build=False)


def test_the_session_simulator_is_fresh_against_the_real_source(sim):
	newest = max(path.stat().st_mtime for path in (REPO_ROOT / "core").rglob("*.cs") if "/bin/" not in str(path) and "/obj/" not in str(path))
	assert sim.dll.stat().st_mtime >= newest
	assert sim.identity["sha256"]


def test_fit_records_the_simulator_and_cleans_up_variants(tmp_path, vessel_t, sim):
	stock = tmp_path / "stock.json"
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states)))
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out")
	assert result.status == "fitted", result.reasons
	assert result.patch["fit"]["simulator"]["sha256"] == sim.identity["sha256"]
	report = fit_module.write_fit_report(result, tmp_path / "out" / "resistance-fit.md")
	assert sim.identity["sha256"][:12] in report
	work = tmp_path / "out" / "work"
	assert not list(work.glob("*.json")), list(work.glob("*.json"))
	assert not (work / "variants").exists()
	assert (work / "commands.jsonl").exists()
	assert list(work.glob("scale-*.csv")), "telemetry is kept"


def test_refused_fit_also_cleans_up_variants(tmp_path, vessel_t, sim):
	stock = tmp_path / "stock.json"
	stock.write_text(patch_module.format_vessel_json(vessel_t))
	states = sim.steady_states(stock, [0.5, 0.9], tmp_path / "truth")
	loaded = answers_module.load_answers(make_answers(tmp_path / "answers", rpm_speed=rows_from_states(states, offsets={0: 4.0})))
	result = fit_module.fit_resistance(loaded, vessel_t, "vessels/t-class.json", sim, tmp_path / "out")
	assert result.status == "refused"
	assert not list((tmp_path / "out" / "work").glob("*.json"))
