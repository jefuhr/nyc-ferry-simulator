"""Fits hull.residuaryResistanceScale to crew rpm-speed figures by running the real simulator."""

import math
import shutil
from dataclasses import dataclass, field
from pathlib import Path

from crew_intake import __version__, modelcheck
from crew_intake.answers import CALM_WIND_KN, AnswerSet, RpmSpeedRow
from crew_intake.mapping import RPM
from crew_intake.patch import PATCH_FORMAT, _proposed_value, exported_text, is_under_vessels, source_key, source_text
from crew_intake.vesseljson import format_vessel_json, get_path, vessel_fingerprint

SCALE_PATH = "hull.residuaryResistanceScale"
RPM_TOLERANCE = 5.0
SCALE_BOUNDS = (0.02, 10.0)
SAME_RPM_BAND = 20.0


@dataclass
class FitRow:
	line: int
	rpm_reported: float
	speed_reported: float
	tolerance_kn: float
	lever: float
	rpm_sim: float = math.nan
	speed_sim: float = math.nan
	settled: bool = False
	load: str = ""
	wind_kn: float | None = None
	current: str = ""
	depth: str = ""
	speed_reference: str = ""
	note: str = ""
	caveat: str = ""

	@property
	def residual_kn(self) -> float:
		return self.speed_sim - self.speed_reported


@dataclass
class FitResult:
	status: str
	reasons: list[str]
	vessel_id: str
	vessel_path: str
	session: dict
	synthetic: bool
	initial_scale: float
	scale: float | None = None
	rows: list[FitRow] = field(default_factory=list)
	excluded: list[tuple[RpmSpeedRow, str]] = field(default_factory=list)
	iterations: list[tuple[float, float]] = field(default_factory=list)
	patch: dict | None = None
	max_residual_kn: float = 1.5
	rms_kn: float | None = None
	runs: int = 0
	work_dir: Path | None = None
	conditions: dict = field(default_factory=dict)
	simulator: dict = field(default_factory=dict)
	role_label: str = ""


def lever_for_rpm(vessel: dict, rpm: float) -> float:
	"""The lever that orders this engine speed: inside the idle band when the rpm is idle, else linear
	from throttleStartLever to full lever (the governor holds the set point unless torque limited)."""
	idle = get_path(vessel, "engines.idleSpeed")["value"] / RPM
	rated = get_path(vessel, "machinery.ratedEngineSpeed")["value"] / RPM
	clutch_in = get_path(vessel, "controls.clutchInLever")["value"]
	throttle_start = get_path(vessel, "controls.throttleStartLever")["value"]
	if rpm <= idle + 1.0:
		return 0.5 * (clutch_in + throttle_start)
	lever = throttle_start + (rpm - idle) / (rated - idle) * (1.0 - throttle_start)
	return min(1.0, max(throttle_start, lever))


def check_consistency(rows: list[FitRow]) -> list[str]:
	"""Rows at the same rpm must have overlapping speed intervals (each row's own tolerance, no extra
	allowance), and speed must not fall as rpm rises beyond what the tolerances allow."""
	reasons = []
	ordered = sorted(rows, key=lambda row: row.rpm_reported)
	for earlier, later in zip(ordered, ordered[1:]):
		if abs(later.rpm_reported - earlier.rpm_reported) <= SAME_RPM_BAND:
			if abs(later.speed_reported - earlier.speed_reported) > earlier.tolerance_kn + later.tolerance_kn:
				reasons.append(f"conflicting rows at the same rpm: line {earlier.line} says {earlier.speed_reported:g} ±{earlier.tolerance_kn:g} kn and line {later.line} says {later.speed_reported:g} ±{later.tolerance_kn:g} kn near {later.rpm_reported:g} rpm; the intervals do not overlap, so no speed satisfies both")
		elif later.speed_reported + later.tolerance_kn < earlier.speed_reported - earlier.tolerance_kn:
			reasons.append(f"speeds are not monotone in rpm: line {later.line} ({later.rpm_reported:g} rpm, {later.speed_reported:g} kn) is slower than line {earlier.line} ({earlier.rpm_reported:g} rpm, {earlier.speed_reported:g} kn)")
	return reasons


def select_fit_rows(answers: AnswerSet, vessel: dict, load: str | None = None, allow_mixed_conditions: bool = False):
	"""Which rpm-speed rows the calm, deep-water forward model may be fitted to.

	Returns (rows, excluded, reasons, conditions). Astern rows, unbalanced engines and over-ground speeds in
	a current are always excluded. Rows with unknown load, wind above the calm limit or unknown, or water
	that is not deep are excluded unless allow_mixed_conditions is set, in which case they are fitted with
	their caveat recorded. Rows at more than one load are refused unless one load is selected or the
	override is set."""
	rows: list[FitRow] = []
	excluded: list[tuple[RpmSpeedRow, str]] = []
	reasons: list[str] = []
	for row in answers.rpm_speed:
		if row.fit_hard_exclusion or (not row.fit_usable and not allow_mixed_conditions):
			excluded.append((row, row.fit_exclusion))
			continue
		if load is not None and row.load != load:
			excluded.append((row, f"load {row.load} is not the selected load {load}"))
			continue
		rows.append(FitRow(row.line, row.rpm, row.speed_kn, row.tolerance_kn, lever_for_rpm(vessel, row.rpm), load=row.load, wind_kn=row.wind_kn, current=row.current, depth=row.depth, speed_reference=row.speed_reference, note=exported_text(row.note, answers), caveat="" if row.fit_usable else row.fit_exclusion))
	loads = sorted({row.load for row in rows})
	if len(loads) > 1 and not allow_mixed_conditions:
		reasons.append(f"the usable rows mix loads ({', '.join(loads)}); one resistance scale describes one loading. Pass --load <{'|'.join(loads)}> to fit one group, or --allow-mixed-conditions to fit them together knowingly")
	winds = [row.wind_kn for row in rows if row.wind_kn is not None]
	conditions = {
		"load": loads[0] if len(loads) == 1 else None,
		"loads": loads,
		"selectedLoad": load,
		"calmWindLimitKn": CALM_WIND_KN,
		"maxWindKn": max(winds) if winds else None,
		"windUnknownRows": [row.line for row in rows if row.wind_kn is None],
		"depth": "deep" if rows and all(row.depth == "deep" for row in rows) else ("mixed" if rows else None),
		"depths": sorted({row.depth for row in rows}),
		"speedReferences": sorted({row.speed_reference for row in rows}),
		"override": allow_mixed_conditions,
		"overriddenRows": [{"line": row.line, "caveat": row.caveat} for row in rows if row.caveat],
	}
	return rows, excluded, reasons, conditions


def fit_resistance(answers: AnswerSet, vessel: dict, vessel_path: str, sim, out_dir: Path, max_residual_kn: float = 1.5, scale_tolerance: float = 0.002, hold_s: float = 150.0, load: str | None = None, allow_mixed_conditions: bool = False) -> FitResult:
	out_dir = Path(out_dir)
	if is_under_vessels(out_dir):
		raise ValueError("the fit's output folder must not be under vessels/")
	work = out_dir / "work"
	initial = get_path(vessel, SCALE_PATH)["value"]
	result = FitResult("refused", [], vessel.get("id", ""), str(vessel_path), answers.session, answers.synthetic, initial, max_residual_kn=max_residual_kn, work_dir=work)
	if not answers.ok:
		result.reasons.append("answers have validation errors; run validate first")
		return result
	result.role_label = answers.role_label
	rows, excluded, reasons, conditions = select_fit_rows(answers, vessel, load, allow_mixed_conditions)
	result.rows, result.excluded, result.conditions = rows, excluded, conditions
	result.reasons.extend(reasons)
	if not rows:
		result.reasons.append(f"no usable ahead rpm-speed rows: the fit needs through-water speed (or over-ground at slack), a known load, wind under {CALM_WIND_KN:g} kn and deep water; see the excluded rows")
	if result.reasons:
		return result
	result.reasons.extend(check_consistency(rows))
	if result.reasons:
		return result
	result.simulator = dict(getattr(sim, "identity", None) or {})

	weights = [1.0 / max(row.tolerance_kn, 0.1) for row in rows]
	variants = work / "variants"

	def evaluate(scale: float) -> float:
		variant = _variant(vessel, scale)
		variants.mkdir(parents=True, exist_ok=True)
		variant_file = variants / f"{vessel.get('id', 'vessel')}-scale-{scale:.5f}.json"
		variant_file.write_text(format_vessel_json(variant))
		_simulate(result, sim, variant_file, work, hold_s, f"scale-{scale:.5f}")
		mean = sum(weight * row.residual_kn for weight, row in zip(weights, rows)) / sum(weights)
		result.iterations.append((scale, mean))
		return mean

	try:
		# Bracket: the simulated speed falls as the scale rises, so the weighted residual is decreasing in scale.
		low, high = initial, initial
		value_low = value_high = evaluate(initial)
		expansions = 0
		while value_low < 0 and low > SCALE_BOUNDS[0] and expansions < 12:
			low = max(SCALE_BOUNDS[0], low / 1.5)
			value_low = evaluate(low)
			expansions += 1
		while value_high > 0 and high < SCALE_BOUNDS[1] and expansions < 12:
			high = min(SCALE_BOUNDS[1], high * 1.5)
			value_high = evaluate(high)
			expansions += 1
		if value_low < 0 or value_high > 0:
			result.reasons.append(f"could not bracket a scale in {SCALE_BOUNDS[0]} to {SCALE_BOUNDS[1]} that matches the reported speeds (residual {value_low:+.2f} kn at {low:.3g}, {value_high:+.2f} kn at {high:.3g}); the speeds are not explainable by the resistance scale alone")
			return result
		best_scale, best_value = (low, value_low) if abs(value_low) < abs(value_high) else (high, value_high)
		while high - low > scale_tolerance * max(high, 1e-9) and abs(best_value) > 0.02:
			middle = 0.5 * (low + high)
			value = evaluate(middle)
			if abs(value) < abs(best_value):
				best_scale, best_value = middle, value
			if value > 0:
				low, value_low = middle, value
			else:
				high, value_high = middle, value
		if result.iterations[-1][0] != best_scale:
			evaluate(best_scale)
	finally:
		# Disposable parameter variants go; telemetry and commands.jsonl stay as the record of what ran.
		shutil.rmtree(variants, ignore_errors=True)
	result.scale = best_scale
	result.rms_kn = math.sqrt(sum(row.residual_kn ** 2 for row in rows) / len(rows))

	unsettled = [row for row in rows if not row.settled]
	if unsettled:
		result.reasons.append("the simulator did not settle for rows " + ", ".join(str(row.line) for row in unsettled))
	rpm_off = [row for row in rows if abs(row.rpm_sim - row.rpm_reported) > RPM_TOLERANCE]
	if rpm_off:
		result.reasons.append("the simulator could not reproduce the reported rpm for rows " + ", ".join(f"{row.line} ({row.rpm_reported:g} reported, {row.rpm_sim:.0f} simulated)" for row in rpm_off) + "; check engines.idleSpeed, machinery.ratedEngineSpeed and the torque curve")
	outside = [row for row in rows if abs(row.residual_kn) > row.tolerance_kn + 1e-9]
	if outside:
		text = "the fitted curve misses the stated tolerance of rows " + ", ".join(f"{row.line} ({row.speed_sim:.2f} kn simulated against {row.speed_reported:g} ±{row.tolerance_kn:g} kn, residual {row.residual_kn:+.2f})" for row in outside) + "; one resistance scale cannot satisfy every row within its own tolerance"
		for first, second in _conflicting_pairs(outside):
			text += f". Lines {first.line} and {second.line} conflict: both near {first.rpm_reported:g} rpm, one above and one below the curve"
		result.reasons.append(text)
	too_far = [row for row in rows if abs(row.residual_kn) > max_residual_kn]
	if too_far:
		result.reasons.append(f"residual above {max_residual_kn:g} kn for rows " + ", ".join(f"{row.line} ({row.residual_kn:+.2f} kn)" for row in too_far) + "; one resistance scale cannot explain these points. Check the rows, or the curve shape (hull.residuaryResistance) and propeller pitch need attention")
	if result.rms_kn > max_residual_kn * 0.67:
		result.reasons.append(f"RMS residual {result.rms_kn:.2f} kn exceeds {max_residual_kn * 0.67:.2f} kn")
	if result.reasons:
		return result

	fitted = _variant(vessel, round(best_scale, 4))
	invariants = modelcheck.check_invariants(fitted)
	if invariants:
		result.reasons.extend(invariants)
		return result

	result.status = "fitted"
	key = source_key(answers.session)
	previous = get_path(vessel, SCALE_PATH)
	as_published = f"fit to {len(rows)} crew rpm-speed points"
	extra = f"Fitted by crew_intake {__version__} fit-resistance against the ferrysim CLI ({result.simulator.get('sha256', 'unknown build')[:12]}): RMS residual {result.rms_kn:.2f} kn over {len(rows)} points, max |residual| {max(abs(row.residual_kn) for row in rows):.2f} kn, every row within its stated tolerance; load {conditions['load'] or '/'.join(conditions['loads'])}, calm, deep water; see resistance-fit.md."
	result.patch = {
		"patchFormat": PATCH_FORMAT,
		"generatedBy": f"crew_intake {__version__} fit-resistance",
		"vessel": str(vessel_path),
		"vesselId": vessel.get("id"),
		"base": {"vesselSha256": vessel_fingerprint(vessel), "answersSha256": answers.fingerprint},
		"session": {
			"sessionId": answers.session["sessionId"], "date": answers.session["date"], "respondentRole": answers.session["respondentRole"],
			"respondentCode": answers.session["respondentCode"], "respondentLabel": answers.role_label, "synthetic": answers.synthetic,
		},
		"sources": {key: source_text(answers, vessel)},
		"changes": [{"questionId": "E2", "path": SCALE_PATH, "how": "fit", "previous": previous, "proposed": _proposed_value(previous, round(best_scale, 4), "1", as_published, key, "", extra)}],
		"targets": [],
		"refused": [],
		"warnings": [],
		"inventory": [{"source": "rpm_speed.csv", "line": row.line, "questionId": "E2", "label": "rpm and speed", "answer": f"{row.rpm_reported:g} rpm: {row.speed_reported:g} kn ±{row.tolerance_kn:g}", "disposition": "fit input" + (f" (override: {row.caveat})" if row.caveat else ""), "note": row.note} for row in rows],
		"texts": {},
		"modelCheck": {"ok": True, "reasons": [], "simulated": True},
		"fit": {
			"initialScale": initial,
			"scale": round(best_scale, 4),
			"rmsResidualKn": round(result.rms_kn, 3),
			"conditions": conditions,
			"simulator": result.simulator,
			"rows": [{"line": row.line, "rpmReported": row.rpm_reported, "lever": round(row.lever, 4), "rpmSimulated": round(row.rpm_sim, 1), "speedReportedKn": row.speed_reported, "toleranceKn": row.tolerance_kn, "speedSimulatedKn": round(row.speed_sim, 3), "residualKn": round(row.residual_kn, 3), "load": row.load, "windKn": row.wind_kn, "current": row.current, "depth": row.depth, "speedReference": row.speed_reference, "caveat": row.caveat} for row in rows],
			"excluded": [{"line": row.line, "reason": reason} for row, reason in excluded],
			"iterations": [{"scale": round(scale, 5), "weightedMeanResidualKn": round(value, 4)} for scale, value in result.iterations],
			"simulatorRuns": result.runs,
		},
	}
	return result


def _conflicting_pairs(rows: list[FitRow]) -> list[tuple[FitRow, FitRow]]:
	pairs = []
	ordered = sorted(rows, key=lambda row: row.rpm_reported)
	for index, first in enumerate(ordered):
		for second in ordered[index + 1:]:
			if abs(second.rpm_reported - first.rpm_reported) <= SAME_RPM_BAND and first.residual_kn * second.residual_kn < 0:
				pairs.append((first, second))
	return pairs


def _variant(vessel: dict, scale: float) -> dict:
	import copy
	variant = copy.deepcopy(vessel)
	entry = get_path(variant, SCALE_PATH)
	entry["value"] = scale
	entry["note"] = f"Temporary fit variant written by crew_intake; not a vessel file. {entry.get('note', '')}".strip()
	return variant


def _simulate(result: FitResult, sim, variant_file: Path, work: Path, hold_s: float, label: str):
	"""Runs every row's lever in one trace, then refines any lever whose rpm missed its target."""
	states = sim.steady_states(variant_file, [row.lever for row in result.rows], work, hold_s=hold_s, label=label)
	result.runs += 1
	for row, state in zip(result.rows, states):
		row.rpm_sim, row.speed_sim, row.settled = state.rpm, state.stw_kn, state.settled
	for index, row in enumerate(result.rows):
		if abs(row.rpm_sim - row.rpm_reported) <= RPM_TOLERANCE or row.rpm_reported <= result.rows[index].rpm_sim and row.lever < 0.2:
			continue
		low, high = (row.lever, 1.0) if row.rpm_sim < row.rpm_reported else (0.0, row.lever)
		for attempt in range(8):
			middle = 0.5 * (low + high)
			state = sim.steady_states(variant_file, [middle], work, hold_s=hold_s, label=f"{label}-row{row.line}-lever{attempt}")[0]
			result.runs += 1
			if abs(state.rpm - row.rpm_reported) <= RPM_TOLERANCE:
				row.lever, row.rpm_sim, row.speed_sim, row.settled = middle, state.rpm, state.stw_kn, state.settled
				break
			if state.rpm < row.rpm_reported:
				low = middle
			else:
				high = middle
			row.lever, row.rpm_sim, row.speed_sim, row.settled = middle, state.rpm, state.stw_kn, state.settled


def write_fit_report(result: FitResult, path: Path) -> str:
	conditions = result.conditions or {}
	lines = []
	if result.synthetic:
		lines += ["> **SYNTHETIC DATA.** Every number below comes from a made-up example answer set, not from crew.", ""]
	lines += [f"# Residuary resistance fit for `{result.vessel_path}`", ""]
	lines += [f"Session `{result.session.get('sessionId')}`, {result.role_label or result.session.get('respondentRole')}, {result.session.get('date')}. Parameter: `{SCALE_PATH}`, starting value {result.initial_scale:g}.", ""]
	if result.status == "fitted":
		lines += [f"**Result: fitted.** Scale {result.scale:.4f} (was {result.initial_scale:g}); RMS residual {result.rms_kn:.2f} kn over {len(result.rows)} points after {result.runs} simulator runs; every row within its stated tolerance.", ""]
	else:
		lines += ["**Result: refused.** No patch was written. Reasons:", ""] + [f"- {reason}" for reason in result.reasons] + [""]
	if conditions:
		lines += ["## Conditions", ""]
		load = conditions.get("load") or (", ".join(conditions.get("loads", [])) or "none")
		wind = conditions.get("maxWindKn")
		lines.append(f"- Load: {load}" + (f" (selected with --load)" if conditions.get("selectedLoad") else ""))
		lines.append(f"- Wind: calm limit {conditions.get('calmWindLimitKn', CALM_WIND_KN):g} kn; strongest fitted row " + (f"{wind:g} kn" if wind is not None else "not recorded") + (f"; wind unknown on lines {', '.join(map(str, conditions['windUnknownRows']))}" if conditions.get("windUnknownRows") else ""))
		lines.append(f"- Water: {conditions.get('depth') or 'none'} ({', '.join(conditions.get('depths', [])) or 'no rows'}); the forward model runs in calm, deep water (50 m) with no current")
		lines.append(f"- Speed references: {', '.join(conditions.get('speedReferences', [])) or 'none'} (over-ground rows only at slack current)")
		if conditions.get("override"):
			lines.append("- **--allow-mixed-conditions was set.** Rows whose conditions the forward model does not represent were fitted anyway; their caveats are listed in the Rows table. Treat the scale as indicative, not calibrated.")
		lines.append("")
	if result.rows and not math.isnan(result.rows[0].speed_sim):
		lines += ["## Rows", "", "| Line | Reported rpm | Lever | Simulated rpm | Reported kn (± tol) | Simulated kn | Residual kn | Settled | Load | Wind kn | Depth | Caveat |", "|---|---|---|---|---|---|---|---|---|---|---|---|"]
		for row in result.rows:
			wind = f"{row.wind_kn:g}" if row.wind_kn is not None else "?"
			lines.append(f"| {row.line} | {row.rpm_reported:g} | {row.lever:.4f} | {row.rpm_sim:.0f} | {row.speed_reported:g} (±{row.tolerance_kn:g}) | {row.speed_sim:.2f} | {row.residual_kn:+.2f} | {'yes' if row.settled else 'NO'} | {row.load} | {wind} | {row.depth} | {row.caveat} |")
		lines.append("")
		lines.append("Residual = simulated minus reported, at the lever that reproduces the reported rpm. The weighted mean residual (weights 1/tolerance) is driven to zero by bisection on the scale; the fit is then accepted only if every row's residual is within that row's stated tolerance.")
		lines.append("")
	elif result.rows:
		lines += ["## Rows", "", "| Line | Reported rpm | Reported kn (± tol) | Load | Wind kn | Depth |", "|---|---|---|---|---|---|"] + [f"| {row.line} | {row.rpm_reported:g} | {row.speed_reported:g} (±{row.tolerance_kn:g}) | {row.load} | {row.wind_kn if row.wind_kn is not None else '?'} | {row.depth} |" for row in result.rows] + [""]
	if result.excluded:
		lines += ["## Excluded rows", ""] + [f"- line {row.line}: {row.rpm:g} rpm {row.gear}, {row.speed_kn:g} kn {row.speed_reference}, load {row.load}: {reason}" for row, reason in result.excluded] + [""]
	if result.iterations:
		lines += ["## Iterations", "", "| Scale | Weighted mean residual kn |", "|---|---|"] + [f"| {scale:.5f} | {value:+.3f} |" for scale, value in result.iterations] + [""]
	if result.simulator:
		lines += ["## Simulator", "", f"- `{result.simulator.get('dll')}`, SHA-256 `{result.simulator.get('sha256')}`, built {result.simulator.get('builtAt')}", f"- newest core source: `{result.simulator.get('newestSource')}` at {result.simulator.get('newestSourceAt')} (the build was checked to be no older)", ""]
	lines += ["## Method", "", "1. Rows are admitted only when the forward model can represent them: both engines ahead and balanced, through-water speed (or over-ground at slack current), a known load shared by every fitted row, wind under the calm limit and deep water. Everything else is listed under Excluded rows.", "2. Each reported rpm is mapped to the lever that orders it (idle band for idle rpm, else linear from `controls.throttleStartLever`), and checked against the simulated rpm; a miss is refined by bisection on the lever.", "3. The real `ferrysim run` holds both levers at each position in turn in calm deep water until speed and rpm are steady (checked over the last 20 s; unsettled holds are doubled).", "4. The scale is bisected until the weighted mean residual is zero.", "5. The fit is refused when rows at the same rpm have disjoint tolerance intervals, when the data is not monotone, when any row's residual exceeds its own stated tolerance or the global limit, when the rpm cannot be reproduced, or when the scale leaves its bounds.", "", f"Telemetry and `commands.jsonl` (every CLI invocation) are under `{result.work_dir}`; the temporary parameter variants are deleted after the run.", ""]
	text = "\n".join(lines)
	path = Path(path)
	path.parent.mkdir(parents=True, exist_ok=True)
	path.write_text(text)
	return text
