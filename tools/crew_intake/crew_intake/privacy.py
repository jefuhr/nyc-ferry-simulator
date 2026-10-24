"""The publication boundary for free text that leaves the tool: notes, lever technique, walk, session
fields, the Markdown answer files and everything a patch or report repeats from them.

Two levels. A **reject** finding is an obvious personal or contact identifier (an email address, a phone
number, a social-media handle, a title followed by a name) and can never be published. A **review**
finding is a capitalised phrase that could be a person's name or could be a place, maker or product; it
is published only after a person records it in session.json `textReview.acceptedPhrases`.

This is a net, not a guarantee. It cannot recognise every name; the interviewer keeps names out of the
answers, and ordinary marine and equipment terminology passes untouched.
"""

import re
from dataclasses import dataclass


@dataclass(frozen=True)
class Finding:
	level: str
	kind: str
	match: str
	message: str


EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")
PHONE = re.compile(r"(?<![\w.])(?:\+?\d{1,2}[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?![\w.])")
HANDLE = re.compile(r"(?<![\w.])@[A-Za-z][A-Za-z0-9_]{2,}")
TITLES = r"(?:Captain|Capt\.?|Cpt\.?|Skipper|Master|Mate|Engineer|Deckhand|Officer|Pilot|Mr\.?|Mrs\.?|Ms\.?|Mx\.?|Dr\.?)"
TITLED_NAME = re.compile(r"\b" + TITLES + r"\s+(?:[A-Z]\.\s*)*[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*")
CAPITALISED_PHRASE = re.compile(r"\b[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,})+\b")

# Marine, equipment, place and institution terms that are capitalised phrases but never a person.
KNOWN_TERMS = frozenset({
	# roles as the tool labels them
	"Chief Engineer", "Port Captain", "Training Captain", "Crew Member", "Other Crew",
	# makers and products
	"Michigan Wheel", "Incat Crowther", "Metal Shark", "Horizon Shipbuilding", "Twin Disc", "Volvo Penta", "Hamilton Jet",
	"Standard Horizon", "Rolls Royce", "Furuno", "Garmin", "Raymarine", "Simrad", "Icom", "Kobelt", "Glendinning", "Humphree",
	"Zipwake", "Caterpillar", "Cummins", "Scania", "Yanmar", "Baudouin", "Jastram", "Kongsberg", "Veth", "Schottel", "Hundested",
	"Example Electronic Control Head",
	# NYC Ferry landings and local waters
	"East River", "Hudson River", "Harlem River", "Upper Bay", "Lower Bay", "New York Harbor", "New York", "Jamaica Bay",
	"Rockaway Inlet", "Buttermilk Channel", "Hell Gate", "Governors Island", "Staten Island", "Roosevelt Island", "Randalls Island",
	"Wall Street", "Pier Eleven", "Hunters Point South", "Hunters Point", "Long Island City", "Red Hook", "Bay Ridge", "Sunset Park",
	"Brooklyn Army Terminal", "Brooklyn Bridge Park", "Brooklyn Bridge", "Brooklyn Navy Yard", "Battery Park", "Battery Park City",
	"Corlears Hook", "Stuyvesant Cove", "Throgs Neck", "Soundview", "Clason Point", "Ferry Point", "Astoria", "Greenpoint",
	"North Williamsburg", "South Williamsburg", "Williamsburg", "Dumbo", "Midtown West", "Midtown East", "Saint George", "St George",
	"Coney Island", "Sheepshead Bay", "Verrazzano Narrows", "Verrazano Narrows", "Kill Van Kull", "Newtown Creek", "Gowanus Bay",
	"Erie Basin", "Atlantic Basin", "Liberty Island", "Ellis Island", "Sandy Hook", "Ambrose Channel", "Gravesend Bay",
	# institutions and systems
	"Coast Guard", "Vessel Traffic Service", "Army Corps", "Port Authority", "Sandy Hook Pilots", "Harbor Operations",
	"Automatic Identification System", "Global Positioning System", "Electronic Chart System",
	# generic marine phrases that sometimes get title case
	"Floating Barge", "Fixed Pier", "Hard Over", "Full Astern", "Full Ahead", "Dead Slow", "Slack Water", "High Water", "Low Water",
	"Spring Tide", "Neap Tide", "Rules Of The Road", "Rudder Angle Indicator", "Control Head", "Wing Station", "Main Helm",
})


def _known_pattern(accepted) -> re.Pattern:
	terms = sorted(set(KNOWN_TERMS) | {phrase for phrase in accepted if isinstance(phrase, str) and phrase.strip()}, key=len, reverse=True)
	return re.compile(r"\b(?:" + "|".join(re.escape(term) for term in terms) + r")\b", re.IGNORECASE)


def scan(text: str, accepted=frozenset()) -> list[Finding]:
	"""Findings for one piece of text. Reject findings come first; accepted phrases only clear review findings."""
	if not text:
		return []
	findings: list[Finding] = []
	for match in EMAIL.finditer(text):
		findings.append(Finding("reject", "email", match.group(0), f"contains an email address ('{match.group(0)}'); remove contact details"))
	for match in PHONE.finditer(text):
		findings.append(Finding("reject", "phone", match.group(0), f"contains a phone number ('{match.group(0).strip()}'); remove contact details"))
	for match in HANDLE.finditer(text):
		findings.append(Finding("reject", "handle", match.group(0), f"contains a social-media handle ('{match.group(0)}'); remove it"))
	cleared = _known_pattern(accepted).sub("x", text)
	for match in TITLED_NAME.finditer(cleared):
		findings.append(Finding("reject", "titled-name", match.group(0), f"'{match.group(0)}' looks like a title followed by a name; use the role and code (for example Captain A), or write the word in lower case if it is not a name"))
	if not findings:
		for match in CAPITALISED_PHRASE.finditer(cleared):
			findings.append(Finding("review", "capitalised-phrase", match.group(0), f"'{match.group(0)}' is a capitalised phrase that could be a person's name; if it is a place, maker or product, list it in session.json textReview.acceptedPhrases after checking"))
	return findings


def scan_lines(text: str, accepted=frozenset()) -> list[tuple[int, Finding]]:
	"""Findings for a multi-line document, with 1-based line numbers."""
	results = []
	for number, line in enumerate(text.splitlines(), 1):
		for finding in scan(line, accepted):
			results.append((number, finding))
	return results
