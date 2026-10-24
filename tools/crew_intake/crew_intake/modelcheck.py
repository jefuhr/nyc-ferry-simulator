"""Checks that a complete vessel parameter set is usable before it is proposed or written: first the
cross-field invariants the core enforces when it constructs the model (so the explanation names the
answers that clash), then a short neutral-then-ahead run through the real ferrysim CLI (so a file that
loads but cannot shift or move is caught too)."""

import tempfile
from pathlib import Path

from crew_intake.ferrysim import FerrySimError
from crew_intake.mapping import RPM
from crew_intake.vesseljson import PatchError, format_vessel_json, get_path

NEUTRAL_UNTIL_S = 10.0
IDLE_UNTIL_S = 40.0
FULL_UNTIL_S = 70.0


def _value(vessel: dict, path: str):
	try:
		return get_path(vessel, path)["value"]
	except (PatchError, KeyError, TypeError):
		return None


def check_invariants(vessel: dict) -> list[str]:
	"""The relationships between parameters that the core requires (ControlModel, EngineModel, SteeringGear,
	PropellerModel), each explained in terms of the answers that set them."""
	reasons: list[str] = []
	clutch_in = _value(vessel, "controls.clutchInLever")
	throttle_start = _value(vessel, "controls.throttleStartLever")
	hysteresis = _value(vessel, "controls.detentHysteresis")
	if clutch_in is not None and not 0.01 <= clutch_in <= 0.5:
		reasons.append(f"controls.clutchInLever {clutch_in:g} must be between 0.01 and 0.5")
	if clutch_in is not None and throttle_start is not None and not clutch_in <= throttle_start <= 0.9:
		reasons.append(f"controls.throttleStartLever {throttle_start:g} must be between controls.clutchInLever {clutch_in:g} and 0.9: the throttle cannot start opening before the clutch engages (C3 and C4 together)")
	if clutch_in is not None and hysteresis is not None and not 0.0 <= hysteresis <= clutch_in:
		reasons.append(f"controls.detentHysteresis {hysteresis:g} must be between 0 and controls.clutchInLever {clutch_in:g}")
	idle = _value(vessel, "engines.idleSpeed")
	rated = _value(vessel, "machinery.ratedEngineSpeed")
	interlock = _value(vessel, "controls.shiftInterlockSpeed")
	if idle is not None and rated is not None and not idle < rated:
		reasons.append(f"engines.idleSpeed {idle / RPM:.0f} rpm must be below machinery.ratedEngineSpeed {rated / RPM:.0f} rpm")
	if idle is not None and interlock is not None and not interlock > idle:
		reasons.append(f"controls.shiftInterlockSpeed {interlock / RPM:.0f} rpm must be above engines.idleSpeed {idle / RPM:.0f} rpm: the engine never drops below the interlock, so a shift would wait for ever (C9 against E1)")
	for path, low, high in (("controls.hardOverAngle", 0.1, 1.2), ("propellers.pitchRatio", 0.5, 1.4), ("propellers.expandedAreaRatio", 0.3, 1.05)):
		value = _value(vessel, path)
		if value is not None and not low <= value <= high:
			reasons.append(f"{path} {value:g} must be between {low:g} and {high:g}")
	for path in ("controls.steeringRate", "controls.shiftInterlockSpeed", "transmission.clutchEngagementTime", "transmission.clutchReleaseTime", "machinery.propellerDiameter", "machinery.propellerBlades", "machinery.gearRatio", "hull.demihullSeparation", "particulars.displacement", "particulars.maxDraft"):
		value = _value(vessel, path)
		if value is not None and not value > 0:
			reasons.append(f"{path} {value:g} must be positive")
	for path in ("controls.neutralDwell", "controls.throttlePause", "transmission.clutchFillTime"):
		value = _value(vessel, path)
		if value is not None and value < 0:
			reasons.append(f"{path} {value:g} must not be negative")
	mode = _value(vessel, "controls.steeringMode")
	if mode is not None and mode not in ("follow-up", "non-follow-up"):
		reasons.append(f"controls.steeringMode '{mode}' must be follow-up or non-follow-up")
	return reasons


def sanity_run(sim, vessel: dict, work_dir: Path, label: str = "sanity") -> list[str]:
	"""Runs the candidate vessel through `ferrysim run`: levers at zero, then at the idle detent, then at full.
	Returns the reasons the vessel is unusable, or an empty list. The candidate file is temporary; the trace,
	telemetry and commands.jsonl stay in work_dir."""
	reasons = check_invariants(vessel)
	if reasons:
		return reasons
	work_dir = Path(work_dir)
	work_dir.mkdir(parents=True, exist_ok=True)
	clutch_in = _value(vessel, "controls.clutchInLever")
	throttle_start = _value(vessel, "controls.throttleStartLever")
	idle_lever = 0.5 * (clutch_in + throttle_start)
	idle_rpm = _value(vessel, "engines.idleSpeed") / RPM
	interlock_rpm = _value(vessel, "controls.shiftInterlockSpeed") / RPM
	trace = work_dir / f"{label}-trace.csv"
	telemetry = work_dir / f"{label}.csv"
	trace.write_text(f"time_s,port_lever,starboard_lever,helm\n0,0,0,0\n{NEUTRAL_UNTIL_S:g},{idle_lever:.6f},{idle_lever:.6f},0\n{IDLE_UNTIL_S:g},1,1,0\n")
	with tempfile.TemporaryDirectory(dir=work_dir, prefix="candidate-") as temp:
		candidate = Path(temp) / "candidate.json"
		candidate.write_text(format_vessel_json(vessel))
		try:
			sim.run(["run", "--vessel", str(candidate), "--inputs", str(trace), "--out", str(telemetry), "--duration-s", f"{FULL_UNTIL_S:g}", "--sample-hz", "1", "--env", "calm"], work_dir, label)
		except FerrySimError as error:
			return [f"the ferrysim CLI refused the candidate vessel: {_core_message(str(error))}"]
	rows = sim.read_csv(telemetry)
	by_time = {round(float(row["time_s"])): row for row in rows}

	def at(time_s: float) -> dict | None:
		return by_time.get(round(time_s))

	neutral = [row for time, row in by_time.items() if 1 <= time <= NEUTRAL_UNTIL_S - 1]
	if any(row["port_gear"] != "neutral" or row["stbd_gear"] != "neutral" or abs(float(row["port_shaft_rpm"])) > 1.0 for row in neutral):
		reasons.append("with both levers at zero the gear is not neutral or the shaft turns; the neutral band is broken")
	engine_rpm = [0.5 * (float(row["port_engine_rpm"]) + float(row["stbd_engine_rpm"])) for row in neutral]
	if engine_rpm and abs(engine_rpm[-1] - idle_rpm) > 0.1 * idle_rpm:
		reasons.append(f"the engine idles at {engine_rpm[-1]:.0f} rpm, not engines.idleSpeed {idle_rpm:.0f} rpm")
	idle_end = at(IDLE_UNTIL_S)
	full_end = at(FULL_UNTIL_S)
	if idle_end is None or full_end is None:
		return reasons + [f"telemetry has no rows at {IDLE_UNTIL_S:g} s and {FULL_UNTIL_S:g} s in {telemetry}"]
	gears = (idle_end["port_gear"], idle_end["stbd_gear"])
	if gears != ("ahead", "ahead"):
		reasons.append(f"{IDLE_UNTIL_S - NEUTRAL_UNTIL_S:g} s after moving both levers to the idle detent ({idle_lever:.2f}) the gear is still '{gears[0]}': the shift never completes. Check controls.shiftInterlockSpeed ({interlock_rpm:.0f} rpm) against engines.idleSpeed ({idle_rpm:.0f} rpm) and the clutch times")
	else:
		if abs(float(idle_end["port_shaft_rpm"])) <= 1.0 or float(idle_end["stw_kn"]) <= 0.1:
			reasons.append(f"in gear at the idle detent the shaft turns at {float(idle_end['port_shaft_rpm']):.0f} rpm and the boat makes {float(idle_end['stw_kn']):.2f} kn: the clutch does not drive the propeller")
	full_rpm = 0.5 * (float(full_end["port_engine_rpm"]) + float(full_end["stbd_engine_rpm"]))
	if gears == ("ahead", "ahead") and full_rpm < idle_rpm + 100:
		reasons.append(f"{FULL_UNTIL_S - IDLE_UNTIL_S:g} s at full lever the engines make {full_rpm:.0f} rpm, barely above idle {idle_rpm:.0f} rpm: the throttle does not open (check controls.throttleStartLever)")
	if gears == ("ahead", "ahead") and float(full_end["stw_kn"]) < float(idle_end["stw_kn"]) + 0.5:
		reasons.append(f"the boat does not speed up at full lever ({float(idle_end['stw_kn']):.2f} kn at the detent, {float(full_end['stw_kn']):.2f} kn at full)")
	return reasons


def _core_message(text: str) -> str:
	"""The core's own one-line explanation out of a stack trace."""
	for line in text.splitlines():
		stripped = line.strip()
		if "Exception" in stripped and ":" in stripped:
			return stripped.split(":", 1)[1].strip() if stripped.startswith(("Unhandled exception", "System.")) else stripped
		if stripped.startswith("ferrysim: error:"):
			return stripped[len("ferrysim: error:"):].strip()
	return text.strip().splitlines()[0] if text.strip() else "no message"
