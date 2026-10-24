"""The questionnaire items a structured answer may carry, and what each one maps to.

Every item here has an id used in docs/crew/questionnaire.md and in direct_answers.csv. Kinds:

- parameter: the answer becomes a proposed value at `path` after unit conversion.
- derived: the answer feeds a formula in patch.py that produces the value at `path`.
- target: a behaviour the simulator should reproduce; reported for manual calibration, never patched.
- text: free text or a choice; reported, and patched only when `path` names a ParameterText.

An item with `table` set is answered in that CSV table (one row per observation) and is refused in
direct_answers.csv, so an answer never lands where nothing reads it.
"""

import math
from dataclasses import dataclass

RPM = 2 * math.pi / 60
DEG = math.pi / 180
KNOT = 1852 / 3600
FOOT = 0.3048
INCH = 0.0254

# unit symbol -> (SI unit, factor to SI)
UNIT_TABLE = {
	"rpm": ("rad/s", RPM),
	"deg": ("rad", DEG),
	"deg/s": ("rad/s", DEG),
	"s": ("s", 1.0),
	"%": ("1", 0.01),
	"fraction": ("1", 1.0),
	"1": ("1", 1.0),
	"ratio": ("1", 1.0),
	"m": ("m", 1.0),
	"ft": ("m", FOOT),
	"in": ("m", INCH),
	"mm": ("m", 0.001),
	"kg": ("kg", 1.0),
	"t": ("kg", 1000.0),
	"lb": ("kg", 0.45359237),
	"kn": ("m/s", KNOT),
	"m/s": ("m/s", 1.0),
	"count": ("count", 1.0),
	"text": ("text", 1.0),
}


@dataclass(frozen=True)
class QuestionSpec:
	id: str
	label: str
	kind: str
	path: str | None = None
	units: tuple[str, ...] = ()
	si_range: tuple[float, float] | None = None
	options: tuple[str, ...] | None = None
	informs: str = ""
	formula: str = ""
	table: str = ""

	@property
	def si_unit(self) -> str:
		if not self.units:
			return "text"
		return UNIT_TABLE[self.units[0]][0]


def _q(*args, **kwargs):
	spec = QuestionSpec(*args, **kwargs)
	for unit in spec.units:
		if UNIT_TABLE[unit][0] != spec.si_unit:
			raise ValueError(f"{spec.id}: units {spec.units} do not share one SI unit")
	return spec


QUESTIONS: dict[str, QuestionSpec] = {spec.id: spec for spec in [
	# Vessel and session
	_q("V1", "Class and variant", "text", options=("river", "rockaway-deep-draft", "k-class", "unknown"), informs="which vessels/*.json the answers belong to"),
	_q("V2", "Hull number (only if shared)", "text", informs="session metadata"),
	_q("V3", "Operating displacement", "parameter", "particulars.displacement", ("t", "kg", "lb"), (30_000.0, 200_000.0), informs="mass in the equations of motion; acceleration and stopping"),
	_q("V4", "Maximum draft including propellers", "parameter", "particulars.maxDraft", ("ft", "m", "in"), (0.8, 3.0), informs="depth under keel and grounding"),
	_q("V5", "Known differences between sister boats", "text", informs="whether one parameter set covers the class"),
	_q("V6", "Shareable source for this boat's particulars", "text", informs="provenance of published values"),
	_q("V7", "Repower or conversion history", "text", informs="which machinery configuration the parameters describe"),
	# Controls
	_q("C1", "Control head make and model", "text", informs="control_layout.md; the control model's shift logic"),
	_q("C2", "Stations and how control transfers", "text", informs="control_layout.md; station-transfer behaviour (not modelled yet)"),
	_q("C3", "Ahead and astern idle detent position", "parameter", "controls.clutchInLever", ("%", "fraction"), (0.02, 0.5), informs="where the clutch engages on the lever"),
	_q("C4", "Where the throttle starts to open", "parameter", "controls.throttleStartLever", ("%", "fraction"), (0.03, 0.7), informs="where rpm starts rising past the detent"),
	_q("C5", "Warm-up mode (throttle in neutral)", "text", options=("yes", "no", "unknown"), informs="control model feature list"),
	_q("C6", "Single-lever or sync mode", "text", informs="control model feature list"),
	_q("C7", "Docking joystick", "text", informs="control model feature list"),
	_q("C8", "Throttle pause after the clutch engages", "parameter", "controls.throttlePause", ("s",), (0.0, 5.0), informs="delay before rpm follows the lever after a shift"),
	_q("C9", "Engine rpm below which a shift is allowed", "parameter", "controls.shiftInterlockSpeed", ("rpm",), (300 * RPM, 1800 * RPM), informs="shift interlock in the control model"),
	_q("C10", "Neutral to ahead or astern: seconds until the clutch bites", "derived", "transmission.clutchFillTime", ("s",), (0.1, 5.0), informs="clutch fill delay", formula="shift_timing.csv rows from neutral: mean seconds", table="shift_timing.csv"),
	_q("C11", "Ahead to astern (or back): seconds until the opposite clutch bites", "derived", "controls.neutralDwell", ("s",), (0.0, 10.0), informs="neutral dwell when reversing", formula="seconds - clutchReleaseTime - clutchFillTime, for a straight-through reversal from below the interlock speed", table="shift_timing.csv"),
	_q("C12", "Lever technique when reversing", "text", options=("straight-through", "paused-in-neutral", "unknown"), informs="whether a reversal timing measures the system or the captain", table="shift_timing.csv"),
	# Steering
	_q("S1", "Steering mode", "text", "controls.steeringMode", options=("follow-up", "non-follow-up"), informs="whether helm position or helm rate orders the rudder"),
	_q("S2", "Rudder angle at hard over", "parameter", "controls.hardOverAngle", ("deg",), (15 * DEG, 60 * DEG), informs="rudder limit"),
	_q("S3", "Hard over to hard over time", "derived", "controls.steeringRate", ("s",), (2.0, 60.0), informs="steering gear slew rate", formula="2 x hardOverAngle / seconds"),
	_q("S4", "Rudder angle indicator shows one rudder or both", "text", informs="instrument rendering"),
	_q("S5", "Autopilot fitted and when used", "text", informs="feature list"),
	_q("S6", "Interceptors or trim tabs fitted and how used", "text", informs="whether a trim device model is needed"),
	# Engine and speed
	_q("E1", "Engine rpm at idle in gear", "parameter", "engines.idleSpeed", ("rpm",), (400 * RPM, 1200 * RPM), informs="idle speed; idle-in-gear boat speed"),
	_q("E2", "Rpm and speed table", "target", "hull.residuaryResistanceScale", informs="rpm_speed.csv; fitted by fit-resistance", table="rpm_speed.csv"),
	_q("E3", "Seconds from idle to full rpm in gear", "target", "engines.turbochargerTimeConstant", ("s",), (0.5, 60.0), informs="turbocharger lag; compare with an acceleration run"),
	_q("E4", "Boat speed going astern at a given rpm", "target", "hull.asternResistanceFactor", informs="rpm_speed.csv rows with gear astern", table="rpm_speed.csv"),
	_q("E5", "Gear ratio", "parameter", "machinery.gearRatio", ("ratio", "1"), (1.0, 4.0), informs="propeller rpm per engine rpm"),
	_q("E6", "Propeller diameter", "parameter", "machinery.propellerDiameter", ("in", "m", "mm"), (0.5, 1.5), informs="propeller thrust and torque"),
	_q("E7", "Propeller pitch", "derived", "propellers.pitchRatio", ("in", "m", "mm"), (0.4, 2.5), informs="pitch ratio P/D", formula="pitch / propellerDiameter"),
	_q("E8", "Propeller rotation seen from astern, going ahead", "text", "propellers.rotation", options=("outward-turning", "inward-turning"), informs="prop walk direction"),
	_q("E9", "Propeller blade area ratio", "parameter", "propellers.expandedAreaRatio", ("fraction", "%"), (0.3, 1.05), informs="open-water propeller curves"),
	_q("E10", "Distance between the two propeller shafts", "parameter", "hull.demihullSeparation", ("m", "ft", "in"), (3.0, 8.0), informs="twisting moment from split engines"),
	_q("E11", "Propeller blade count", "parameter", "machinery.propellerBlades", ("count",), (2, 7), informs="open-water propeller curves"),
	_q("E12", "Gearbox make and model", "text", "machinery.gearboxModel", informs="which gearbox the ratio and clutch timings describe"),
	# Acceleration and stopping
	_q("A1", "Seconds from standstill to service speed", "target", "particulars.displacement", ("s",), (5.0, 120.0), informs="acceleration run; mass and turbo lag"),
	_q("A2", "Normal slowing from service speed to manoeuvring speed", "target", "hull.asternResistanceFactor", informs="stopping.csv rows with method normal", table="stopping.csv"),
	_q("A3", "Crash stop from service speed", "target", "propellers.reversedFlowLiftFactor", informs="stopping.csv rows with method full-astern", table="stopping.csv"),
	_q("A4", "Coasting in neutral", "target", "hull.residuaryResistanceScale", informs="stopping.csv rows with method neutral", table="stopping.csv"),
	_q("A5", "Which way the bow swings in a crash stop", "target", "propellers.propWalkAstern", informs="stopping.csv bow_swing column", table="stopping.csv"),
	_q("A6", "Is full power applied at once or ramped", "text", options=("at-once", "ramped", "depends"), informs="how departures are scripted in scenarios"),
	_q("A7", "Shareable trials data", "text", informs="replaces crew estimates with measured curves"),
	# Turning and twisting
	_q("T1", "Hard-over turn at service speed", "target", "hull.derivativeNr", informs="turning.csv", table="turning.csv"),
	_q("T2", "Turn at manoeuvring speed", "target", "rudders.area", informs="turning.csv", table="turning.csv"),
	_q("T3", "Speed lost in a hard turn", "target", "hull.derivativeYv", ("%",), (0.0, 0.8), informs="steady-turn speed loss"),
	_q("T4", "Twisting at rest with split engines", "target", "hull.crossFlowDragCoefficient", informs="twisting.csv", table="twisting.csv"),
	_q("T5", "Prop walk when backing on one engine", "text", informs="propellers.propWalkAstern sign and rotation"),
	_q("T6", "Is power held constant through a turn", "text", options=("yes", "no", "depends"), informs="how turning observations are compared"),
	# Landings
	_q("L1", "Which side or end touches first, per landing type", "text", informs="landing_procedures.md; contact geometry"),
	_q("L2", "Approach speed", "target", "", ("kn",), (0.0, 15 * KNOT), informs="landing scenarios"),
	_q("L3", "Where slowing starts", "text", informs="landing scenarios"),
	_q("L4", "Final approach angle", "target", "", ("deg",), (0.0, 90 * DEG), informs="landing scenarios"),
	_q("L5", "How the boat is held on the landing", "text", informs="holding thrust; landing_procedures.md"),
	_q("L6", "Lines used and when", "text", informs="landing_procedures.md"),
	_q("L7", "Deckhand role and signals", "text", informs="landing_procedures.md"),
	_q("L8", "Ebb versus flood at each landing", "text", informs="landing scenarios and current model checks"),
	_q("L9", "Tricky landings and why", "text", informs="scenario priorities"),
	_q("L10", "Fenders and boarding connection on the boat and the landing", "text", informs="contact surface stiffness and damping; where the boarding connection sits"),
	_q("L11", "Shareable landing drawings or dimensions", "text", informs="landing contact geometry"),
	# Environment rules of thumb
	_q("W1", "Wind limit for normal operations", "target", "", ("kn",), (0.0, 80 * KNOT), informs="windage check scenarios"),
	_q("W2", "Where the current sets you on each route", "text", informs="current model checks"),
	_q("W3", "Where the real current differs from the NOAA station", "text", informs="current model checks"),
	_q("W4", "Weather or sea limits per route", "text", informs="scenario limits"),
	_q("W5", "Radio and VTS practice", "text", informs="scenario scripting only"),
	_q("W6", "Normal tracks, turning points and slow zones per route", "text", informs="route scenarios"),
]}


def to_si(spec: QuestionSpec, value: float, unit: str) -> tuple[float, str]:
	"""Converts an answer to the SI unit of its parameter."""
	if unit not in spec.units:
		raise ValueError(f"{spec.id} does not accept unit '{unit}' (accepted: {', '.join(spec.units)})")
	si_unit, factor = UNIT_TABLE[unit]
	return value * factor, si_unit


def from_si(spec: QuestionSpec, si_value: float, unit: str) -> float:
	si_unit, factor = UNIT_TABLE[unit]
	return si_value / factor
