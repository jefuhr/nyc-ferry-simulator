"""Proposed parameter patches: building them from answers, checking the combined result, applying them,
and reporting them. Vessel JSON reading and writing lives in vesseljson.py and is re-exported here."""

import copy
import json
import math
from pathlib import Path

from crew_intake import __version__, modelcheck, privacy
from crew_intake.answers import AnswerSet
from crew_intake.mapping import RPM
from crew_intake.vesseljson import PatchError, SourceFloat, format_vessel_json, get_path, load_vessel, parse_vessel, set_path, vessel_fingerprint

__all__ = [
	"PATCH_FORMAT", "PatchError", "SourceFloat", "apply_changes", "apply_patch", "build_patch", "class_label", "exported_text",
	"find_repo_root", "format_vessel_json", "get_path", "is_under_vessels", "load_vessel", "parse_vessel", "patch_report",
	"previous_note", "round_si", "set_path", "source_key", "source_text", "vessel_fingerprint", "write_json", "write_vessel",
]

PATCH_FORMAT = 2
FILL_SPREAD_S = 0.5
REVERSAL_SPREAD_S = 1.0
PITCH_RATIO_BOUNDS = (0.5, 1.4)


# --- provenance -----------------------------------------------------------------------------------

def class_label(vessel: dict) -> str:
	identifier = vessel.get("id", "")
	return identifier[:1].upper() + identifier[1:] if identifier else "unknown class"


def source_key(session: dict) -> str:
	return f"crew-{session['sessionId']}"


def source_text(answers: AnswerSet, vessel: dict) -> str:
	session = answers.session
	method = {"interview": "interview", "ride-along": "ride-along", "both": "interview and ride-along"}[session.get("method") or "interview"]
	if answers.synthetic:
		prefix = f"Synthetic crew {method} (example only, not real data)"
	else:
		prefix = f"Crew {method} (anonymised)"
	return f"{prefix}, {answers.role_label}, {session['date']}, {class_label(vessel)}"


def exported_text(text: str | None, answers: AnswerSet) -> str:
	"""The publication boundary at the point where free text leaves the tool. Validation already scanned
	every field; this refuses anything that reached a patch or report by another route."""
	text = text or ""
	findings = privacy.scan(text, answers.accepted_phrases)
	if findings:
		raise PatchError(f"text cannot be published: {findings[0].message}")
	return text


def _format_number(value) -> str:
	if isinstance(value, float) and value.is_integer() and abs(value) < 1e15:
		return f"{value:.1f}"
	return f"{value:g}" if isinstance(value, float) else str(value)


def previous_note(previous: dict) -> str:
	"""One sentence that keeps everything the old entry knew."""
	if "unit" in previous:
		unit = "" if previous["unit"] == "1" else f" {previous['unit']}"
		text = f"Previously {_format_number(previous['value'])}{unit}"
		if previous.get("asPublished"):
			text += f" ({previous['asPublished']})"
	else:
		text = f"Previously {previous['value']}"
	text += f", confidence {previous.get('confidence', 'unknown')}, source: {previous.get('source', 'unknown')}."
	if previous.get("note"):
		text += f" Old note: {previous['note']}"
	return text


def round_si(value: float) -> float:
	"""Seven significant digits, the precision the vessel files use."""
	return float(f"{value:.7g}")


def _proposed_value(previous: dict, si_value: float, si_unit: str, as_published: str, key: str, crew_note: str, extra: str = "") -> dict:
	si_value = int(round(si_value)) if si_unit == "count" else round_si(si_value)
	note = previous_note(previous)
	if extra:
		note += f" {extra}"
	if crew_note:
		note += f" Crew note: {crew_note}"
	return {"value": si_value, "unit": si_unit, "asPublished": as_published, "source": key, "confidence": "crew-reported", "note": note}


def _proposed_text(previous: dict, value: str, key: str, crew_note: str) -> dict:
	note = previous_note(previous)
	if crew_note:
		note += f" Crew note: {crew_note}"
	return {"value": value, "source": key, "confidence": "crew-reported", "note": note}


# --- building -------------------------------------------------------------------------------------

def build_patch(answers: AnswerSet, vessel: dict, vessel_path: str) -> dict:
	"""Turns direct answers and shift timings into a proposal bound to this vessel and answer set, then
	checks the combined result against the core's parameter invariants. Nothing is written here."""
	if not answers.ok:
		raise PatchError("answers have validation errors; run validate first")
	session = answers.session
	key = source_key(session)
	patch = {
		"patchFormat": PATCH_FORMAT,
		"generatedBy": f"crew_intake {__version__} patch",
		"vessel": str(vessel_path),
		"vesselId": vessel.get("id"),
		"base": {"vesselSha256": vessel_fingerprint(vessel), "answersSha256": answers.fingerprint},
		"session": {
			"sessionId": session["sessionId"], "date": session["date"], "respondentRole": session["respondentRole"],
			"respondentCode": session["respondentCode"], "respondentLabel": answers.role_label, "synthetic": answers.synthetic,
		},
		"sources": {key: source_text(answers, vessel)},
		"changes": [],
		"targets": [],
		"refused": [],
		"warnings": [],
		"inventory": [],
		"texts": {name: exported_text(text, answers) for name, text in answers.texts.items()},
		"modelCheck": {"ok": True, "reasons": [], "simulated": False},
	}
	proposed_by_path: dict[str, dict] = {}

	def add_change(question_id: str, path: str, proposed: dict, previous: dict, how: str):
		if previous.get("confidence") == "published":
			patch["warnings"].append(f"{path}: the crew answer would replace a published value ({previous.get('asPublished') or previous.get('value')}); check which is right before approving")
		patch["changes"].append({"questionId": question_id, "path": path, "how": how, "previous": copy.deepcopy(previous), "proposed": proposed})
		proposed_by_path[path] = proposed

	def current(path: str) -> dict:
		return proposed_by_path.get(path) or get_path(vessel, path)

	def inventory(source: str, line: int, question_id: str, label: str, answer: str, disposition: str, note: str = ""):
		patch["inventory"].append({"source": source, "line": line, "questionId": question_id, "label": label, "answer": answer, "disposition": disposition, "note": note})

	def target(question_id: str, label: str, value: str, informs: str, note: str):
		patch["targets"].append({"questionId": question_id, "label": label, "value": value, "unit": "", "informs": informs, "note": note})

	answered = {answer.question_id: answer for answer in answers.direct}
	notes = {answer.question_id: exported_text(answer.note, answers) for answer in answers.direct}

	for answer in answers.direct:
		spec = answer.spec
		note = notes[answer.question_id]
		shown = f"{answer.value:g} {answer.unit}" if isinstance(answer.value, float) else str(answer.value)
		if spec.kind == "parameter":
			previous = get_path(vessel, spec.path)
			add_change(answer.question_id, spec.path, _proposed_value(previous, answer.si_value, answer.si_unit, shown, key, note), previous, "direct")
			inventory("direct_answers.csv", answer.line, answer.question_id, spec.label, shown, f"patched {spec.path}", note)
		elif spec.kind == "text" and spec.path:
			previous = get_path(vessel, spec.path)
			add_change(answer.question_id, spec.path, _proposed_text(previous, answer.value, key, note), previous, "direct")
			inventory("direct_answers.csv", answer.line, answer.question_id, spec.label, shown, f"patched {spec.path}", note)
		elif spec.kind == "derived":
			continue  # S3 and E7 below, once their dependencies in the same patch are known
		else:
			target(answer.question_id, spec.label, shown, spec.path or spec.informs, note)
			inventory("direct_answers.csv", answer.line, answer.question_id, spec.label, shown, "reported as a calibration target" if spec.kind == "target" else "reported as text", note)

	# S3: steering rate from hard-over-to-hard-over time, using the patched hard-over angle when there is one.
	if "S3" in answered:
		answer = answered["S3"]
		hard_over = current("controls.hardOverAngle")
		rate = 2 * hard_over["value"] / answer.si_value
		previous = get_path(vessel, "controls.steeringRate")
		as_published = f"{answer.value:g} s hard over to hard over at {math.degrees(hard_over['value']):g} deg"
		extra = f"Derived: 2 x hardOverAngle {hard_over['value']:.7g} rad / {answer.si_value:g} s ({math.degrees(rate):.3g} deg/s)."
		add_change("S3", "controls.steeringRate", _proposed_value(previous, rate, "rad/s", as_published, key, notes["S3"], extra), previous, "derived")
		inventory("direct_answers.csv", answer.line, "S3", answer.spec.label, f"{answer.value:g} s", "derived controls.steeringRate", notes["S3"])

	# E7: pitch ratio from pitch and diameter, within the bounds the simulator's propeller model accepts.
	if "E7" in answered:
		answer = answered["E7"]
		diameter = current("machinery.propellerDiameter")
		ratio = answer.si_value / diameter["value"]
		previous = get_path(vessel, "propellers.pitchRatio")
		if PITCH_RATIO_BOUNDS[0] <= ratio <= PITCH_RATIO_BOUNDS[1]:
			as_published = f"pitch {answer.value:g} {answer.unit} on {diameter['value']:.4g} m diameter"
			extra = f"Derived: pitch {answer.si_value:.4g} m / diameter {diameter['value']:.4g} m."
			add_change("E7", "propellers.pitchRatio", _proposed_value(previous, ratio, "1", as_published, key, notes["E7"], extra), previous, "derived")
			disposition = "derived propellers.pitchRatio"
		else:
			reason = f"propellers.pitchRatio: pitch {answer.si_value:.4g} m over diameter {diameter['value']:.4g} m gives P/D {ratio:.3g}, outside {PITCH_RATIO_BOUNDS[0]:g} to {PITCH_RATIO_BOUNDS[1]:g} (the simulator's propeller model refuses it); check the units and the diameter"
			patch["refused"].append(reason)
			disposition = f"refused: {reason}"
		inventory("direct_answers.csv", answer.line, "E7", answer.spec.label, f"{answer.value:g} {answer.unit}", disposition, notes["E7"])

	_derive_shift_timings(answers, vessel, patch, key, add_change, current, inventory, target)

	# Behaviour tables are targets for the calibration step, not direct patches.
	for row in answers.rpm_speed:
		note = exported_text(row.note, answers)
		wind = "wind not recorded" if row.wind_kn is None else f"wind {row.wind_kn:g} kn"
		value = f"{row.rpm:g} rpm {row.gear}: {row.speed_kn:g} kn {row.speed_reference} (±{row.tolerance_kn:g}), load {row.load}, {wind}, {row.current} current, {row.depth} water"
		question_id = "E2" if row.gear == "ahead" else "E4"
		disposition = "fit input (fit-resistance)" if row.fit_usable else f"excluded from the fit: {row.fit_exclusion}"
		target(question_id, "rpm and speed", value, "hull.residuaryResistanceScale (fit-resistance)" if row.fit_usable else f"excluded from the fit: {row.fit_exclusion}", note)
		inventory("rpm_speed.csv", row.line, question_id, "rpm and speed", value, disposition, note)
	for row in answers.stopping:
		note = exported_text(row.note, answers)
		question_id = {"neutral": "A4", "full-astern": "A3"}.get(row.method, "A2")
		value = f"from {row.initial_speed_kn:g} kn: " + ", ".join(filter(None, (f"{row.distance_m:g} m" if row.distance_m is not None else "", f"{row.distance_boat_lengths:g} lengths" if row.distance_boat_lengths is not None else "", f"{row.time_s:g} s" if row.time_s is not None else "", f"bow to {row.bow_swing}" if row.bow_swing not in ("", "none", "unknown") else "")))
		target(question_id, f"stopping, {row.method}", value, "hull.asternResistanceFactor, propellers.reversedFlowLiftFactor, propellers.propWalkAstern", note)
		inventory("stopping.csv", row.line, question_id, f"stopping, {row.method}", value, "reported as a calibration target", note)
	for row in answers.turning:
		note = exported_text(row.note, answers)
		value = f"at {row.speed_kn:g} kn: " + ", ".join(filter(None, (f"{row.rudder_deg:g} deg rudder" if row.rudder_deg is not None else "", f"diameter {row.diameter_m:g} m" if row.diameter_m is not None else "", f"{row.time_to_180_s:g} s to 180" if row.time_to_180_s is not None else "", f"{row.speed_in_turn_kn:g} kn in the turn" if row.speed_in_turn_kn is not None else "")))
		target("T1", f"turning, {row.helm}", value, "hull.derivativeNr, hull.derivativeYv, rudders.area", note)
		inventory("turning.csv", row.line, "T1", f"turning, {row.helm}", value, "reported as a calibration target", note)
	for row in answers.twisting:
		note = exported_text(row.note, answers)
		walk = exported_text(row.walk, answers)
		value = f"{row.ahead_engine} ahead {row.ahead_rpm:g} rpm, other astern {row.astern_rpm:g} rpm: " + ", ".join(filter(None, (f"{row.yaw_rate_deg_s:g} deg/s" if row.yaw_rate_deg_s is not None else "", f"{row.time_to_180_s:g} s to 180" if row.time_to_180_s is not None else "", f"walk {walk}" if walk else "")))
		target("T4", "twisting", value, "propellers.reversedFlowLiftFactor, propellers.asternThrustDeduction, hull.crossFlowDragCoefficient", note)
		inventory("twisting.csv", row.line, "T4", "twisting", value, "reported as a calibration target", note)
	for name, text in patch["texts"].items():
		inventory(name, 1, "text", "text answers", f"{len(text.splitlines())} lines", "reported in full (see Text answers)", "")

	# The combined result must still be a vessel the core can build.
	reasons = modelcheck.check_invariants(apply_changes(vessel, patch))
	patch["modelCheck"] = {"ok": not reasons, "reasons": reasons, "simulated": False}
	return patch


def _derive_shift_timings(answers: AnswerSet, vessel: dict, patch: dict, key: str, add_change, current, inventory, target):
	"""Clutch fill time from neutral-to-gear rows and neutral dwell from reversals, but only from rows that
	measure the system: a straight-through lever movement that started below the shift interlock speed.
	A captain's pause in neutral and an interlock wait are reported as calibration targets instead."""
	interlock_rpm = current("controls.shiftInterlockSpeed")["value"] / RPM

	def shown(row) -> str:
		text = f"{row.from_gear} to {row.to_gear} {row.seconds:g} s"
		if row.from_rpm is not None:
			text += f" from {row.from_rpm:g} rpm"
		return text + f", {row.lever_technique}"

	fills, reversals = [], []
	for row in answers.shift_timing:
		note = exported_text(row.note, answers)
		if row.from_gear == "neutral":
			if row.from_rpm is not None and row.from_rpm > interlock_rpm + 1.0:
				why = f"started at {row.from_rpm:g} rpm, above the shift interlock {interlock_rpm:.0f} rpm, so the time includes the interlock wait"
				target("C10", "neutral to gear timing", shown(row), f"transmission.clutchFillTime, controls.shiftInterlockSpeed (compare with a ferrysim run; not derived: {why})", note)
				inventory("shift_timing.csv", row.line, "C10", "neutral to gear timing", shown(row), f"reported as a calibration target: {why}", note)
			else:
				fills.append((row, note))
		elif row.to_gear == "neutral":
			target("C11", "gear to neutral timing", shown(row), "transmission.clutchReleaseTime (compare with a ferrysim run; not derived)", note)
			inventory("shift_timing.csv", row.line, "C11", "gear to neutral timing", shown(row), "reported as a calibration target", note)
		else:
			why = None
			if row.lever_technique == "paused-in-neutral":
				why = "the captain paused in neutral, so the time includes technique, not only the system's automatic neutral dwell"
			elif row.lever_technique == "unknown":
				why = "lever_technique is unknown; ask whether the lever went straight through or paused in neutral"
			elif row.from_rpm is None:
				why = "from_rpm was not recorded; a reversal from above the shift interlock includes the engine's deceleration"
			elif row.from_rpm > interlock_rpm + 1.0:
				why = f"started at {row.from_rpm:g} rpm, above the shift interlock {interlock_rpm:.0f} rpm, so the time includes the interlock wait"
			if why:
				target("C11", "reversal timing", shown(row), f"controls.neutralDwell, controls.shiftInterlockSpeed (compare with a ferrysim run; not derived: {why})", note)
				inventory("shift_timing.csv", row.line, "C11", "reversal timing", shown(row), f"reported as a calibration target: {why}", note)
			else:
				reversals.append((row, note))

	if fills:
		seconds = [row.seconds for row, _ in fills]
		detail = "; ".join(f"{row.from_gear} to {row.to_gear} {row.seconds:g} s (line {row.line})" for row, _ in fills)
		previous = get_path(vessel, "transmission.clutchFillTime")
		if max(seconds) - min(seconds) > FILL_SPREAD_S:
			patch["refused"].append(f"transmission.clutchFillTime: the neutral-to-gear timings disagree by more than {FILL_SPREAD_S:g} s ({detail}); they are not averaged. Re-ask, or keep one row and put the others in its note")
			disposition = "refused: inconsistent timings"
		else:
			mean = sum(seconds) / len(seconds)
			if 0.1 <= mean <= 5.0:
				extra = f"Derived from shift_timing.csv ({detail}): mean time from the lever reaching the detent to the clutch biting."
				add_change("C10", "transmission.clutchFillTime", _proposed_value(previous, mean, "s", f"{mean:g} s", key, "; ".join(filter(None, (note for _, note in fills))), extra), previous, "derived")
				disposition = "derived transmission.clutchFillTime"
			else:
				patch["refused"].append(f"transmission.clutchFillTime: mean neutral-to-gear time {mean:g} s is outside 0.1 to 5 s")
				disposition = "refused: outside 0.1 to 5 s"
		for row, note in fills:
			inventory("shift_timing.csv", row.line, "C10", "neutral to gear timing", shown(row), disposition, note)

	if reversals:
		seconds = [row.seconds for row, _ in reversals]
		detail = "; ".join(f"{row.from_gear} to {row.to_gear} {row.seconds:g} s from {row.from_rpm:g} rpm (line {row.line})" for row, _ in reversals)
		previous = get_path(vessel, "controls.neutralDwell")
		if max(seconds) - min(seconds) > REVERSAL_SPREAD_S:
			patch["refused"].append(f"controls.neutralDwell: the reversal timings disagree by more than {REVERSAL_SPREAD_S:g} s ({detail}); they are not averaged. Re-ask, or keep one row and put the others in its note")
			disposition = "refused: inconsistent timings"
		else:
			mean = sum(seconds) / len(seconds)
			release = current("transmission.clutchReleaseTime")["value"]
			fill = current("transmission.clutchFillTime")["value"]
			dwell = mean - release - fill
			if 0.0 <= dwell <= 10.0:
				extra = f"Derived from shift_timing.csv ({detail}): {mean:g} s straight-through reversal from below the shift interlock, minus clutchReleaseTime {release:g} s minus clutchFillTime {fill:g} s."
				add_change("C11", "controls.neutralDwell", _proposed_value(previous, dwell, "s", f"{mean:g} s ahead to astern", key, "; ".join(filter(None, (note for _, note in reversals))), extra), previous, "derived")
				disposition = "derived controls.neutralDwell"
			else:
				patch["refused"].append(f"controls.neutralDwell: reversal {mean:g} s minus clutchReleaseTime {release:g} s minus clutchFillTime {fill:g} s gives {dwell:g} s, outside 0 to 10 s; the reported reversal is faster than the clutch alone")
				disposition = "refused: outside 0 to 10 s"
		for row, note in reversals:
			inventory("shift_timing.csv", row.line, "C11", "reversal timing", shown(row), disposition, note)


# --- applying -------------------------------------------------------------------------------------

def apply_changes(vessel: dict, patch: dict) -> dict:
	"""Returns a patched copy: merges the source entries and replaces each changed leaf after checking that
	the leaf still holds the value the patch was computed from. No binding checks; see apply_patch."""
	result = copy.deepcopy(vessel)
	sources = result.setdefault("sources", {})
	for key, text in patch.get("sources", {}).items():
		if key in sources and sources[key] != text:
			raise PatchError(f"source key '{key}' already exists with different text")
		sources[key] = text
	for change in patch.get("changes", []):
		current = get_path(result, change["path"])
		if current != change["previous"]:
			raise PatchError(f"{change['path']} has changed since the patch was generated; regenerate the patch")
		set_path(result, change["path"], copy.deepcopy(change["proposed"]))
	return result


def apply_patch(vessel: dict, patch: dict) -> dict:
	"""Returns a patched copy. Refuses old patch formats, another vessel, a vessel that differs anywhere from
	the one the patch was computed against, a patch that failed its model check, stale leaves and colliding
	source keys."""
	if patch.get("patchFormat") != PATCH_FORMAT:
		raise PatchError(f"unsupported patch format {patch.get('patchFormat')}; this tool writes format {PATCH_FORMAT} patches, which carry the base fingerprints. Regenerate the patch")
	if patch.get("vesselId") != vessel.get("id"):
		raise PatchError(f"patch is for vessel '{patch.get('vesselId')}', file is '{vessel.get('id')}'")
	base = patch.get("base")
	if not isinstance(base, dict) or not isinstance(base.get("vesselSha256"), str):
		raise PatchError("the patch has no base vessel fingerprint; regenerate it")
	actual = vessel_fingerprint(vessel)
	if base["vesselSha256"] != actual:
		raise PatchError(f"the patch was computed against a different version of the vessel file (base {base['vesselSha256'][:12]}, this file {actual[:12]}); its derived values and fit evidence no longer describe this vessel. Regenerate the patch against the current file")
	check = patch.get("modelCheck")
	if not isinstance(check, dict) or check.get("ok") is not True:
		reasons = check.get("reasons") if isinstance(check, dict) else None
		raise PatchError("the patch failed its model check: " + ("; ".join(reasons) if reasons else "no model check recorded"))
	return apply_changes(vessel, patch)


# --- writing --------------------------------------------------------------------------------------

def find_repo_root(start: Path | None = None) -> Path | None:
	node = (start or Path(__file__)).resolve()
	for candidate in [node, *node.parents]:
		if (candidate / "vessels").is_dir() and (candidate / "core").is_dir() and (candidate / "docs").is_dir():
			return candidate
	return None


def is_under_vessels(path: Path) -> bool:
	resolved = Path(path).resolve()
	root = find_repo_root()
	if root is not None and (root / "vessels") in [resolved, *resolved.parents]:
		return True
	return any(part == "vessels" for part in resolved.parts)


def write_vessel(vessel: dict, out_path: Path, approved: bool = False):
	out_path = Path(out_path)
	if is_under_vessels(out_path) and not approved:
		raise PatchError(f"refusing to write {out_path}: it is under vessels/. Review the patch and pass --approved to apply it there")
	out_path.parent.mkdir(parents=True, exist_ok=True)
	out_path.write_text(format_vessel_json(vessel))


def write_json(data, out_path: Path):
	out_path = Path(out_path)
	if is_under_vessels(out_path):
		raise PatchError(f"refusing to write {out_path}: outputs never go under vessels/")
	out_path.parent.mkdir(parents=True, exist_ok=True)
	out_path.write_text(json.dumps(data, indent="\t", ensure_ascii=False) + "\n")


def _display_value(entry: dict) -> str:
	unit = entry.get("unit", "")
	text = f"{_format_number(entry['value'])} {'' if unit == '1' else unit}".strip()
	if entry.get("asPublished"):
		text += f" ({entry['asPublished']})"
	return text


def _cell(text) -> str:
	return str(text).replace("|", "\\|").replace("\n", " ")


def patch_report(patch: dict) -> str:
	session = patch["session"]
	lines = []
	if session.get("synthetic"):
		lines += ["> **SYNTHETIC DATA.** Every number below comes from a made-up example answer set, not from crew.", ""]
	lines += [f"# Proposed patch for `{patch['vessel']}`", ""]
	lines += [f"Session `{session['sessionId']}`, {session.get('respondentLabel', session.get('respondentRole'))}, {session['date']}.", ""]
	lines += [f"Computed against vessel file `{patch['base']['vesselSha256']}` (SHA-256 of `{patch['vessel']}`) from answer set `{patch['base']['answersSha256']}`. `apply` refuses the patch if either has changed.", ""]
	lines += ["New source entry:", ""]
	for key, text in patch["sources"].items():
		lines.append(f"- `{key}`: {text}")
	check = patch.get("modelCheck", {})
	lines += ["", "## Model check", ""]
	if check.get("ok"):
		lines.append("Passed: the combined parameters satisfy the simulator's constraints" + (", and a neutral-then-ahead run through the real ferrysim CLI shifted and moved as expected." if check.get("simulated") else " (parameter invariants; the CLI run happens in the `patch` command)."))
	else:
		lines += ["**Refused.** The combined answers do not make a usable vessel, so no patch file was written:", ""] + [f"- {reason}" for reason in check.get("reasons", [])]
	lines += ["", "## Proposed changes", ""]
	if not patch["changes"]:
		lines.append("None: no direct answers or derivable shift timings in this answer set.")
	else:
		lines.append("| Question | Parameter | Previous | Proposed | How |")
		lines.append("|---|---|---|---|---|")
		for change in patch["changes"]:
			previous, proposed = change["previous"], change["proposed"]
			old = _display_value(previous) + f", {previous.get('confidence')}"
			new = _display_value(proposed)
			lines.append(f"| {change['questionId']} | `{change['path']}` | {_cell(old)} | {_cell(new)} | {change['how']} |")
		lines += ["", "Each proposed `note` starts with the previous value, confidence and source, so nothing is lost when the patch is applied."]
	if patch["warnings"]:
		lines += ["", "## Warnings", ""] + [f"- {item}" for item in patch["warnings"]]
	if patch["refused"]:
		lines += ["", "## Refused derivations", ""] + [f"- {item}" for item in patch["refused"]]
	if patch["targets"]:
		lines += ["", "## Calibration targets (reported, not patched)", "", "| Question | What | Answer | Informs | Note |", "|---|---|---|---|---|"]
		for target in patch["targets"]:
			value = f"{target['value']} {target['unit']}".strip()
			lines.append(f"| {target['questionId']} | {_cell(target['label'])} | {_cell(value)} | {_cell(target['informs'])} | {_cell(target.get('note', ''))} |")
	if patch.get("inventory"):
		lines += ["", "## Answer inventory", "", "Every accepted answer and what became of it.", "", "| Source | Line | Question | Answer | Disposition | Note |", "|---|---|---|---|---|---|"]
		for item in patch["inventory"]:
			lines.append(f"| {item['source']} | {item['line']} | {item['questionId']} | {_cell(item['answer'])} | {_cell(item['disposition'])} | {_cell(item.get('note', ''))} |")
	if patch.get("texts"):
		lines += ["", "## Text answers", "", "Reproduced in full from the answer folder; they passed the publication boundary (no contact details, titled names or unreviewed capitalised phrases)."]
		for name, text in patch["texts"].items():
			lines += ["", f"### {name}", ""] + [f"> {line}" if line.strip() else ">" for line in text.splitlines()]
	lines += ["", "## Applying", "", "The patch is a proposal. Review it, then:", "", "```", f"uv run tools/crew_intake/intake.py apply --patch <this folder>/proposed-patch.json --vessel {patch['vessel']} --out {patch['vessel']} --approved", "```", "", "`apply` refuses the patch when the vessel file no longer matches the fingerprint above, when the manifest beside the patch records a refused rerun, or when the result fails the model check. A resistance fit made with `--with-patch` on top of this patch applies only after this patch.", ""]
	return "\n".join(lines)
