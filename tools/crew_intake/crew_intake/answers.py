"""Reads an answers folder (session.json plus CSV tables and two Markdown files) and validates units,
ranges, metadata, consent and the publication boundary on free text. Problems are collected, never raised."""

import csv
import datetime
import hashlib
import json
import re
from dataclasses import dataclass, field
from pathlib import Path

from crew_intake import privacy
from crew_intake.mapping import QUESTIONS, UNIT_TABLE, QuestionSpec, to_si

KNOWN_CLASSES = ("t-class", "k-class")
VARIANTS = ("river", "rockaway-deep-draft", "k-class", "unknown")
LOADS = ("light", "half", "full", "unknown")
CURRENTS = ("slack", "flood", "ebb", "unknown")
DEPTHS = ("deep", "shallow", "unknown")
GEARS = ("neutral", "ahead", "astern")
SPEED_REFERENCES = ("through-water", "over-ground")
STOPPING_METHODS = ("neutral", "astern-idle", "half-astern", "full-astern", "normal")
HELMS = ("hard-over", "half", "quarter")
SIDES = ("port", "starboard")
BOW_SWINGS = ("port", "starboard", "none", "unknown", "")
METHODS = ("interview", "ride-along", "both")
TECHNIQUES = ("straight-through", "paused-in-neutral", "unknown")
RESPONDENT_ROLES = ("captain", "mate", "deckhand", "chief-engineer", "engineer", "port-captain", "training-captain", "other-crew")
ROLE_LABELS = {
	"captain": "Captain", "mate": "Mate", "deckhand": "Deckhand", "chief-engineer": "Chief Engineer", "engineer": "Engineer",
	"port-captain": "Port Captain", "training-captain": "Training Captain", "other-crew": "Crew member",
}
APPROVER_ROLES = ("operations-manager", "port-captain", "marine-superintendent", "safety-manager", "training-manager", "fleet-manager", "general-manager", "operator-representative")
RESPONDENT_CODE_PATTERN = re.compile(r"^[A-Z][A-Z0-9]{0,2}$")
PLACEHOLDER_PATTERN = re.compile(r"^\s*<.*>\s*$")
PLACEHOLDERS = frozenset({"who gave the OK (by role) and when", "years on this class, if they want to say", "<sessionId>"})
SLACK_CURRENT_KN = 0.3
DEFAULT_SPEED_TOLERANCE_KN = 0.5
CALM_WIND_KN = 10.0

NUMBER = "number"
SESSION_KEYS = {
	"sessionId": str, "synthetic": bool, "date": str, "respondentRole": str, "respondentCode": str, "respondentExperience": str,
	"vesselClass": str, "variant": str, "hull": str, "method": str, "conditions": dict, "consent": dict, "textReview": dict,
	"photos": str, "notes": str,
}
SESSION_REQUIRED = ("sessionId", "date", "respondentRole", "respondentCode", "vesselClass", "conditions", "consent")
CONDITION_KEYS = {"load": str, "windKn": NUMBER, "windFrom": str, "current": str, "currentKn": NUMBER, "tide": str, "depth": str, "notes": str}
CONDITION_REQUIRED = ("load", "current", "depth")
CONSENT_KEYS = {"respondentAgreed": bool, "operatorApproved": bool, "approvedByRole": str, "approvalDate": str, "notes": str}
CONSENT_REQUIRED = ("respondentAgreed", "operatorApproved", "approvedByRole", "approvalDate")
REVIEW_KEYS = {"reviewedByRole": str, "date": str, "acceptedPhrases": list}
SESSION_TEXT_FIELDS = ("respondentExperience", "hull", "photos", "notes")
CONDITION_TEXT_FIELDS = ("windFrom", "tide", "notes")

COLUMNS = {
	"rpm_speed.csv": ("port_rpm", "starboard_rpm", "gear", "speed_kn", "speed_tolerance_kn", "speed_reference", "load", "wind_kn", "current", "depth", "note"),
	"shift_timing.csv": ("from_gear", "to_gear", "from_rpm", "seconds", "lever_technique", "note"),
	"stopping.csv": ("initial_speed_kn", "method", "distance_m", "distance_boat_lengths", "time_s", "bow_swing", "note"),
	"turning.csv": ("speed_kn", "helm", "rudder_deg", "diameter_m", "time_to_180_s", "speed_in_turn_kn", "note"),
	"twisting.csv": ("ahead_engine", "ahead_rpm", "astern_rpm", "yaw_rate_deg_s", "time_to_180_s", "walk", "note"),
	"direct_answers.csv": ("question_id", "value", "unit", "note"),
}
TEXT_FILES = ("control_layout.md", "landing_procedures.md")
ALL_FILES = ("session.json", *COLUMNS, *TEXT_FILES)


@dataclass
class Problem:
	level: str
	where: str
	message: str

	def __str__(self):
		return f"{self.level}: {self.where}: {self.message}"


@dataclass
class DirectAnswer:
	question_id: str
	spec: QuestionSpec
	value: float | str
	unit: str
	note: str
	si_value: float | None = None
	si_unit: str | None = None
	line: int = 0


@dataclass
class RpmSpeedRow:
	line: int
	port_rpm: float
	starboard_rpm: float
	gear: str
	speed_kn: float
	tolerance_kn: float
	speed_reference: str
	load: str
	wind_kn: float | None
	current: str
	depth: str
	note: str
	fit_usable: bool = True
	fit_exclusion: str = ""
	fit_hard_exclusion: bool = False

	@property
	def rpm(self) -> float:
		return 0.5 * (self.port_rpm + self.starboard_rpm)


@dataclass
class ShiftRow:
	line: int
	from_gear: str
	to_gear: str
	from_rpm: float | None
	seconds: float
	lever_technique: str
	note: str


@dataclass
class StoppingRow:
	line: int
	initial_speed_kn: float
	method: str
	distance_m: float | None
	distance_boat_lengths: float | None
	time_s: float | None
	bow_swing: str
	note: str


@dataclass
class TurningRow:
	line: int
	speed_kn: float
	helm: str
	rudder_deg: float | None
	diameter_m: float | None
	time_to_180_s: float | None
	speed_in_turn_kn: float | None
	note: str


@dataclass
class TwistingRow:
	line: int
	ahead_engine: str
	ahead_rpm: float
	astern_rpm: float
	yaw_rate_deg_s: float | None
	time_to_180_s: float | None
	walk: str
	note: str


@dataclass
class AnswerSet:
	folder: Path
	session: dict = field(default_factory=dict)
	rpm_speed: list[RpmSpeedRow] = field(default_factory=list)
	shift_timing: list[ShiftRow] = field(default_factory=list)
	stopping: list[StoppingRow] = field(default_factory=list)
	turning: list[TurningRow] = field(default_factory=list)
	twisting: list[TwistingRow] = field(default_factory=list)
	direct: list[DirectAnswer] = field(default_factory=list)
	texts: dict[str, str] = field(default_factory=dict)
	problems: list[Problem] = field(default_factory=list)
	accepted_phrases: set[str] = field(default_factory=set)
	fingerprint: str = ""

	@property
	def errors(self) -> list[Problem]:
		return [problem for problem in self.problems if problem.level == "error"]

	@property
	def warnings(self) -> list[Problem]:
		return [problem for problem in self.problems if problem.level == "warning"]

	@property
	def ok(self) -> bool:
		return not self.errors

	@property
	def synthetic(self) -> bool:
		return self.session.get("synthetic") is True

	@property
	def role_label(self) -> str:
		"""The anonymous label used in provenance text, such as 'Captain A'."""
		role = ROLE_LABELS.get(self.session.get("respondentRole"), "Crew member")
		return f"{role} {self.session.get('respondentCode', '?')}"

	def error(self, where, message):
		self.problems.append(Problem("error", where, message))

	def warning(self, where, message):
		self.problems.append(Problem("warning", where, message))

	def scan(self, where, text):
		"""Applies the publication boundary to one piece of exported free text."""
		for finding in privacy.scan(text, self.accepted_phrases):
			self.error(where, finding.message)


def load_answers(folder: Path) -> AnswerSet:
	"""Loads and validates everything in the folder. Problems are collected, never raised."""
	folder = Path(folder)
	answers = AnswerSet(folder)
	if not folder.is_dir():
		answers.error(str(folder), "answers folder not found")
		return answers
	_load_session(answers)
	_load_rpm_speed(answers)
	_load_shift_timing(answers)
	_load_stopping(answers)
	_load_turning(answers)
	_load_twisting(answers)
	_load_direct(answers)
	for name in TEXT_FILES:
		path = folder / name
		if path.is_file():
			text = path.read_text(encoding="utf-8")
			answers.texts[name] = text
			for number, finding in privacy.scan_lines(text, answers.accepted_phrases):
				answers.error(f"{name} line {number}", finding.message)
		else:
			answers.warning(name, "missing (text answers go here)")
	answers.fingerprint = answers_fingerprint(folder)
	return answers


def answers_fingerprint(folder: Path) -> str:
	"""SHA-256 over the answer files' names and bytes, so a patch can say which answer set it came from."""
	digest = hashlib.sha256()
	for name in ALL_FILES:
		path = Path(folder) / name
		if path.is_file():
			digest.update(name.encode("utf-8") + b"\0" + path.read_bytes() + b"\0")
	return digest.hexdigest()


# --- session.json ---------------------------------------------------------------------------------

def _is_placeholder(value: str) -> bool:
	return bool(PLACEHOLDER_PATTERN.match(value)) or value.strip() in PLACEHOLDERS


def _field(answers: AnswerSet, where: str, container: dict, key: str, kind, required: bool, label: str | None = None):
	"""Returns the value when present, non-null and of the right type; records the problem otherwise.
	A missing or null optional field returns None without a problem."""
	label = label or key
	if key not in container:
		if required:
			answers.error(where, f"required field '{label}' is missing")
		return None
	value = container[key]
	if value is None:
		if required:
			answers.error(where, f"required field '{label}' must not be null")
		return None
	if kind is bool:
		if not isinstance(value, bool):
			answers.error(where, f"'{label}' must be true or false, found {json.dumps(value)}")
			return None
	elif kind == NUMBER:
		if isinstance(value, bool) or not isinstance(value, (int, float)):
			answers.error(where, f"'{label}' must be a number or null, found {json.dumps(value)}")
			return None
	elif kind is str:
		if not isinstance(value, str):
			answers.error(where, f"'{label}' must be a string, found {json.dumps(value)}")
			return None
		if _is_placeholder(value):
			answers.error(where, f"'{label}' is a template placeholder ({value.strip()}); fill it in or leave it empty")
			return None
	elif kind is dict:
		if not isinstance(value, dict):
			answers.error(where, f"'{label}' must be an object, found {json.dumps(value)}")
			return None
	elif kind is list:
		if not isinstance(value, list):
			answers.error(where, f"'{label}' must be a list, found {json.dumps(value)}")
			return None
	return value


def _unknown_keys(answers: AnswerSet, where: str, container: dict, known: dict, prefix: str = ""):
	for key in container:
		if key not in known:
			answers.error(where, f"unknown field '{prefix}{key}' (a typo hides a required field; see answers-template/README.md)")


def _date(answers: AnswerSet, where: str, label: str, value) -> datetime.date | None:
	if value is None:
		return None
	try:
		return datetime.date.fromisoformat(value)
	except (TypeError, ValueError):
		answers.error(where, f"'{label}' '{value}' is not a date in the form YYYY-MM-DD")
		return None


def _load_session(answers: AnswerSet):
	path = answers.folder / "session.json"
	where = "session.json"
	if not path.is_file():
		answers.error(where, "session.json is missing; it holds the date, respondent role and code, vessel class, conditions and consent")
		return
	try:
		session = json.loads(path.read_text(encoding="utf-8"))
	except json.JSONDecodeError as error:
		answers.error(where, f"not valid JSON: {error}")
		return
	if not isinstance(session, dict):
		answers.error(where, "must be a JSON object")
		return
	answers.session = session
	_unknown_keys(answers, where, session, SESSION_KEYS)
	values = {key: _field(answers, where, session, key, kind, key in SESSION_REQUIRED) for key, kind in SESSION_KEYS.items()}

	synthetic = values["synthetic"] is True
	date = _date(answers, where, "date", values["date"])
	role = values["respondentRole"]
	if role is not None and role not in RESPONDENT_ROLES:
		answers.error(where, f"respondentRole '{role}' must be one of {', '.join(RESPONDENT_ROLES)}: a role from the list, never a name")
		role = None
	code = values["respondentCode"]
	if code is not None and not RESPONDENT_CODE_PATTERN.match(code):
		answers.error(where, f"respondentCode '{code}' must be an anonymous code of one capital letter optionally followed by up to two capitals or digits (A, B2); never initials or a name")
		code = None
	session_id = values["sessionId"]
	if session_id is not None and date is not None and role is not None and code is not None:
		expected = f"{'synthetic-' if synthetic else ''}{date.isoformat()}-{role}-{code.lower()}"
		if session_id != expected:
			answers.error(where, f"sessionId must be derived from the synthetic flag, date, role and code: expected '{expected}', found '{session_id}'")
	vessel_class = values["vesselClass"]
	if vessel_class is not None and vessel_class not in KNOWN_CLASSES:
		answers.error(where, f"vesselClass '{vessel_class}' is not one of {', '.join(KNOWN_CLASSES)}")
	variant = values["variant"]
	if variant is not None and variant not in VARIANTS:
		answers.error(where, f"variant '{variant}' is not one of {', '.join(VARIANTS)}")
	method = values["method"]
	if method is not None and method not in METHODS:
		answers.error(where, f"method '{method}' is not one of {', '.join(METHODS)}")

	conditions = values["conditions"]
	if conditions is not None:
		_unknown_keys(answers, where, conditions, CONDITION_KEYS, "conditions.")
		for key, kind in CONDITION_KEYS.items():
			value = _field(answers, where, conditions, key, kind, key in CONDITION_REQUIRED, f"conditions.{key}")
			options = {"load": LOADS, "current": CURRENTS, "depth": DEPTHS}.get(key)
			if options and value is not None and value not in options:
				answers.error(where, f"conditions.{key} '{value}' must be one of {', '.join(options)}")

	consent = values["consent"]
	approval_date = None
	if consent is not None:
		_unknown_keys(answers, where, consent, CONSENT_KEYS, "consent.")
		for key in ("respondentAgreed", "operatorApproved"):
			flag = _field(answers, where, consent, key, bool, True, f"consent.{key}")
			if flag is not None and flag is not True:
				answers.error(where, f"consent.{key} must be true before any answer is used (false means no agreement was recorded)")
		approver = _field(answers, where, consent, "approvedByRole", str, True, "consent.approvedByRole")
		if approver is not None and approver not in APPROVER_ROLES:
			answers.error(where, f"consent.approvedByRole '{approver}' must be one of {', '.join(APPROVER_ROLES)}: the operator's approving role, never a name")
		approval_date = _date(answers, where, "consent.approvalDate", _field(answers, where, consent, "approvalDate", str, True, "consent.approvalDate"))
		_field(answers, where, consent, "notes", str, False, "consent.notes")
	if approval_date is not None and date is not None and approval_date > date:
		answers.warning(where, f"consent.approvalDate {approval_date} is after the session date {date}; the operator's OK should come before the interview (see docs/crew/questionnaire.md)")

	review = values["textReview"]
	if review is not None:
		_unknown_keys(answers, where, review, REVIEW_KEYS, "textReview.")
		reviewer = _field(answers, where, review, "reviewedByRole", str, True, "textReview.reviewedByRole")
		if reviewer is not None and reviewer not in APPROVER_ROLES:
			answers.error(where, f"textReview.reviewedByRole '{reviewer}' must be one of {', '.join(APPROVER_ROLES)}")
			reviewer = None
		review_date = _date(answers, where, "textReview.date", _field(answers, where, review, "date", str, True, "textReview.date"))
		phrases = _field(answers, where, review, "acceptedPhrases", list, True, "textReview.acceptedPhrases")
		if phrases is not None and not all(isinstance(phrase, str) and phrase.strip() for phrase in phrases):
			answers.error(where, "textReview.acceptedPhrases must be a list of non-empty strings")
			phrases = None
		if reviewer is not None and review_date is not None and phrases is not None:
			answers.accepted_phrases = set(phrases)

	# The publication boundary on the session's own free text, with the review in force.
	for key in SESSION_TEXT_FIELDS:
		if isinstance(values.get(key), str):
			answers.scan(f"{where} {key}", values[key])
	if isinstance(conditions, dict):
		for key in CONDITION_TEXT_FIELDS:
			if isinstance(conditions.get(key), str):
				answers.scan(f"{where} conditions.{key}", conditions[key])
	if isinstance(consent, dict) and isinstance(consent.get("notes"), str):
		answers.scan(f"{where} consent.notes", consent["notes"])


# --- CSV helpers ----------------------------------------------------------------------------------

def _read_table(answers: AnswerSet, name: str) -> list[tuple[int, dict]]:
	"""Returns (physical line number, row) pairs for the populated rows, or an empty list after recording
	the problem. Strict: duplicate, unknown or missing columns, rows of the wrong width and bad quoting
	are all errors that name the file and line."""
	path = answers.folder / name
	if not path.is_file():
		answers.error(name, "file is missing (keep the header row even when there are no answers)")
		return []
	expected = COLUMNS[name]
	rows: list[tuple[int, dict]] = []
	with path.open(newline="", encoding="utf-8-sig") as handle:
		reader = csv.reader(handle, strict=True)
		try:
			header = next(reader, None)
			if header is None:
				answers.error(name, f"file is empty; keep the header row: {','.join(expected)}")
				return []
			columns = [column.strip() for column in header]
			duplicates = sorted({column for column in columns if columns.count(column) > 1})
			if duplicates:
				answers.error(name, f"duplicate column(s): {', '.join(duplicates)}; each column may appear once, or a value would be silently overwritten")
				return []
			missing = [column for column in expected if column not in columns]
			unknown = [column for column in columns if column not in expected]
			if missing:
				answers.error(name, f"missing column(s): {', '.join(missing)}; expected {', '.join(expected)}")
			if unknown:
				answers.error(name, f"unknown column(s): {', '.join(unknown)}; expected {', '.join(expected)} (see answers-template/README.md)")
			if missing or unknown:
				return []
			previous_line = reader.line_num
			for cells in reader:
				line = previous_line + 1
				previous_line = reader.line_num
				if not any(cell.strip() for cell in cells):
					continue
				if len(cells) != len(columns):
					answers.error(f"{name} line {line}", f"has {len(cells)} cells but the header has {len(columns)} columns (an extra comma, or a missing cell?)")
					continue
				rows.append((line, dict(zip(columns, cells))))
		except csv.Error as error:
			answers.error(f"{name} line {reader.line_num}", f"malformed CSV ({error}); check the quoting on that line")
			return []
	if not rows:
		answers.warning(name, "no rows")
	return rows


def _number(answers: AnswerSet, name: str, line: int, row: dict, column: str, low: float, high: float, required=True, default=None):
	text = (row.get(column) or "").strip()
	where = f"{name} line {line}"
	if text == "":
		if required:
			answers.error(where, f"{column} is required")
		return default
	try:
		value = float(text)
	except ValueError:
		answers.error(where, f"{column} '{text}' is not a number (write plain numbers; the unit is fixed by the column)")
		return None
	if not (low <= value <= high):
		answers.error(where, f"{column} {value:g} is outside the plausible range {low:g} to {high:g}")
		return None
	return value


def _choice(answers: AnswerSet, name: str, line: int, row: dict, column: str, options, required=True):
	text = (row.get(column) or "").strip().lower()
	where = f"{name} line {line}"
	if text == "" and not required:
		return ""
	if text not in options:
		answers.error(where, f"{column} '{row.get(column)}' must be one of {', '.join(option for option in options if option)}")
		return None
	return text


def _text(answers: AnswerSet, name: str, line: int, row: dict, column: str) -> str:
	"""A free-text cell, passed through the publication boundary."""
	text = (row.get(column) or "").strip()
	if text:
		answers.scan(f"{name} line {line}", text)
	return text


# --- tables ---------------------------------------------------------------------------------------

def _session_conditions(answers: AnswerSet) -> dict:
	conditions = answers.session.get("conditions")
	return conditions if isinstance(conditions, dict) else {}


def _load_rpm_speed(answers: AnswerSet):
	name = "rpm_speed.csv"
	session = _session_conditions(answers)
	session_wind = session.get("windKn") if isinstance(session.get("windKn"), (int, float)) and not isinstance(session.get("windKn"), bool) else None
	session_current = session.get("current") if session.get("current") in CURRENTS else "unknown"
	session_current_kn = session.get("currentKn") if isinstance(session.get("currentKn"), (int, float)) and not isinstance(session.get("currentKn"), bool) else None
	session_depth = session.get("depth") if session.get("depth") in DEPTHS else "unknown"
	for line, row in _read_table(answers, name):
		port = _number(answers, name, line, row, "port_rpm", 300, 2500)
		starboard = _number(answers, name, line, row, "starboard_rpm", 300, 2500)
		gear = _choice(answers, name, line, row, "gear", ("ahead", "astern"))
		speed = _number(answers, name, line, row, "speed_kn", 0, 40)
		tolerance = _number(answers, name, line, row, "speed_tolerance_kn", 0, 5, required=False, default=DEFAULT_SPEED_TOLERANCE_KN)
		reference = _choice(answers, name, line, row, "speed_reference", SPEED_REFERENCES)
		load = _choice(answers, name, line, row, "load", LOADS)
		wind = _number(answers, name, line, row, "wind_kn", 0, 100, required=False)
		current = _choice(answers, name, line, row, "current", CURRENTS, required=False)
		depth = _choice(answers, name, line, row, "depth", DEPTHS, required=False)
		note = _text(answers, name, line, row, "note")
		if None in (port, starboard, gear, speed, tolerance, reference, load, current, depth):
			continue
		inherited_current = current == ""
		if wind is None and (row.get("wind_kn") or "").strip() == "":
			wind = session_wind
		current = current or session_current
		depth = depth or session_depth
		entry = RpmSpeedRow(line, port, starboard, gear, speed, tolerance, reference, load, wind, current, depth, note)
		hard, soft = [], []
		if gear == "astern":
			hard.append("astern rows inform hull.asternResistanceFactor, not the resistance fit")
		if abs(port - starboard) > 0.05 * entry.rpm:
			hard.append("port and starboard rpm differ by more than 5%")
		slack = current == "slack" and (not inherited_current or session_current_kn is None or abs(session_current_kn) <= SLACK_CURRENT_KN)
		if reference == "over-ground" and not slack:
			hard.append(f"over-ground speed with a current that is not slack ({current})")
		if load == "unknown":
			soft.append("load unknown; the fit needs a known load")
		if wind is None:
			soft.append(f"wind not recorded (the fit assumes calm water, under {CALM_WIND_KN:g} kn)")
		elif wind > CALM_WIND_KN:
			soft.append(f"wind {wind:g} kn is above the calm limit {CALM_WIND_KN:g} kn")
		if depth != "deep":
			soft.append(f"depth '{depth}'; the fit runs in deep water")
		if hard or soft:
			entry.fit_usable = False
			entry.fit_hard_exclusion = bool(hard)
			entry.fit_exclusion = "; ".join(hard + soft)
			if gear != "astern":
				answers.warning(f"{name} line {line}", f"excluded from the resistance fit: {entry.fit_exclusion}")
		answers.rpm_speed.append(entry)


def _load_shift_timing(answers: AnswerSet):
	name = "shift_timing.csv"
	for line, row in _read_table(answers, name):
		from_gear = _choice(answers, name, line, row, "from_gear", GEARS)
		to_gear = _choice(answers, name, line, row, "to_gear", GEARS)
		from_rpm = _number(answers, name, line, row, "from_rpm", 300, 2500, required=False)
		seconds = _number(answers, name, line, row, "seconds", 0, 30)
		technique = _choice(answers, name, line, row, "lever_technique", TECHNIQUES)
		note = _text(answers, name, line, row, "note")
		if None in (from_gear, to_gear, seconds, technique):
			continue
		if from_gear == to_gear:
			answers.error(f"{name} line {line}", f"from_gear and to_gear are both '{from_gear}'")
			continue
		answers.shift_timing.append(ShiftRow(line, from_gear, to_gear, from_rpm, seconds, technique, note))


def _load_stopping(answers: AnswerSet):
	name = "stopping.csv"
	for line, row in _read_table(answers, name):
		speed = _number(answers, name, line, row, "initial_speed_kn", 0, 40)
		method = _choice(answers, name, line, row, "method", STOPPING_METHODS)
		distance = _number(answers, name, line, row, "distance_m", 0, 3000, required=False)
		lengths = _number(answers, name, line, row, "distance_boat_lengths", 0, 100, required=False)
		time = _number(answers, name, line, row, "time_s", 0, 600, required=False)
		swing = _choice(answers, name, line, row, "bow_swing", BOW_SWINGS, required=False)
		note = _text(answers, name, line, row, "note")
		if None in (speed, method, swing):
			continue
		if distance is None and lengths is None and time is None:
			answers.error(f"{name} line {line}", "give at least one of distance_m, distance_boat_lengths or time_s")
			continue
		answers.stopping.append(StoppingRow(line, speed, method, distance, lengths, time, swing, note))


def _load_turning(answers: AnswerSet):
	name = "turning.csv"
	for line, row in _read_table(answers, name):
		speed = _number(answers, name, line, row, "speed_kn", 0, 40)
		helm = _choice(answers, name, line, row, "helm", HELMS)
		rudder = _number(answers, name, line, row, "rudder_deg", 0, 60, required=False)
		diameter = _number(answers, name, line, row, "diameter_m", 0, 2000, required=False)
		time = _number(answers, name, line, row, "time_to_180_s", 0, 600, required=False)
		speed_in_turn = _number(answers, name, line, row, "speed_in_turn_kn", 0, 40, required=False)
		note = _text(answers, name, line, row, "note")
		if None in (speed, helm):
			continue
		if diameter is None and time is None:
			answers.error(f"{name} line {line}", "give at least one of diameter_m or time_to_180_s")
			continue
		answers.turning.append(TurningRow(line, speed, helm, rudder, diameter, time, speed_in_turn, note))


def _load_twisting(answers: AnswerSet):
	name = "twisting.csv"
	for line, row in _read_table(answers, name):
		engine = _choice(answers, name, line, row, "ahead_engine", SIDES)
		ahead = _number(answers, name, line, row, "ahead_rpm", 300, 2500)
		astern = _number(answers, name, line, row, "astern_rpm", 300, 2500)
		yaw = _number(answers, name, line, row, "yaw_rate_deg_s", 0, 30, required=False)
		time = _number(answers, name, line, row, "time_to_180_s", 0, 600, required=False)
		walk = _text(answers, name, line, row, "walk")
		note = _text(answers, name, line, row, "note")
		if None in (engine, ahead, astern):
			continue
		if yaw is None and time is None:
			answers.error(f"{name} line {line}", "give at least one of yaw_rate_deg_s or time_to_180_s")
			continue
		answers.twisting.append(TwistingRow(line, engine, ahead, astern, yaw, time, walk, note))


def _load_direct(answers: AnswerSet):
	name = "direct_answers.csv"
	seen = set()
	for line, row in _read_table(answers, name):
		where = f"{name} line {line}"
		question_id = (row.get("question_id") or "").strip().upper()
		spec = QUESTIONS.get(question_id)
		if spec is None:
			answers.error(where, f"unknown question id '{row.get('question_id')}' (see docs/crew/questionnaire.md)")
			continue
		if spec.table:
			answers.error(where, f"{question_id} ({spec.label}) is answered with rows in {spec.table}, not in direct_answers.csv; move it there")
			continue
		if question_id in seen:
			answers.error(where, f"{question_id} is answered twice; keep one row and put alternatives in the note")
			continue
		seen.add(question_id)
		unit = (row.get("unit") or "").strip() or ("text" if not spec.units else "")
		raw = (row.get("value") or "").strip()
		note = _text(answers, name, line, row, "note")
		if not spec.units:
			if unit != "text":
				answers.error(where, f"{question_id} is a text answer; unit must be 'text'")
				continue
			value = raw.lower() if spec.options else raw
			if spec.options and value not in spec.options:
				answers.error(where, f"{question_id} value '{raw}' must be one of {', '.join(spec.options)}")
				continue
			if not spec.options:
				answers.scan(where, value)
			answers.direct.append(DirectAnswer(question_id, spec, value, unit, note, line=line))
			continue
		if unit not in spec.units:
			accepted = ", ".join(spec.units)
			known = "missing unit" if unit == "" else ("unknown unit" if unit not in UNIT_TABLE else "wrong unit")
			answers.error(where, f"{question_id} ({spec.label}): {known} '{unit}'; accepted: {accepted}")
			continue
		try:
			value = float(raw)
		except ValueError:
			answers.error(where, f"{question_id} value '{raw}' is not a number (write plain numbers; the unit goes in the unit column)")
			continue
		si_value, si_unit = to_si(spec, value, unit)
		low, high = spec.si_range
		if not (low <= si_value <= high):
			answers.error(where, f"{question_id} ({spec.label}): {value:g} {unit} is outside the plausible range {_display(spec, low, unit):g} to {_display(spec, high, unit):g} {unit}")
			continue
		answers.direct.append(DirectAnswer(question_id, spec, value, unit, note, si_value, si_unit, line))


def _display(spec: QuestionSpec, si_value: float, unit: str) -> float:
	return si_value / UNIT_TABLE[unit][1]
