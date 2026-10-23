"""Parse S-57 light attributes (LIGHTS objects) into light characteristics.

Reference: IHO S-57 Appendix A, attributes LITCHR, COLOUR, SIGGRP, SIGPER, SIGSEQ, VALNMR, HEIGHT, SECTR1/2.
"""

import math
import re

NM_TO_M = 1852.0

LITCHR = {
	1: "F", 2: "Fl", 3: "LFl", 4: "Q", 5: "VQ", 6: "UQ", 7: "Iso", 8: "Oc", 9: "IQ", 10: "IVQ", 11: "IUQ",
	12: "Mo", 13: "FFl", 14: "Fl+LFl", 15: "OcFl", 16: "FLFl", 17: "AlOc", 18: "AlLFl", 19: "AlFl", 20: "Al",
	25: "Q+LFl", 26: "VQ+LFl", 27: "UQ+LFl", 28: "Al", 29: "AlFFl",
}

COLOUR = {1: "W", 2: "B", 3: "R", 4: "G", 5: "Bu", 6: "Y", 7: "Gy", 8: "Br", 9: "Am", 10: "Vi", 11: "Or", 12: "Mg", 13: "Pk"}

_ABBR_TO_CODE = {abbr: code for code, abbr in sorted(LITCHR.items(), reverse=True)}
_NOTATION = re.compile(r"^(?P<char>[A-Za-z+]+?)(?:\((?P<group>[\d+]+)\))?(?:\s+(?P<colours>[A-Za-z]+(?:\s+[A-Z][a-z]?)*?))?(?:\s+(?P<period>\d+(?:\.\d+)?)s)?$")


def _number(value):
	if value is None:
		return None
	try:
		number = float(value)
	except (TypeError, ValueError):
		return None
	return None if math.isnan(number) else number


def _int_list(value):
	"""S-57 list attributes arrive as arrays, lists, strings like '[4]' or scalars."""
	if value is None:
		return []
	if isinstance(value, str):
		return [int(token) for token in re.findall(r"-?\d+", value)]
	try:
		items = list(value)
	except TypeError:
		items = [value]
	out = []
	for item in items:
		number = _number(item)
		if number is not None:
			out.append(int(number))
	return out


def parse_group(text):
	"""'(1)' -> None (single flash is not written), '(2+1)' -> [2, 1], '(3)' -> [3]."""
	if text is None or (isinstance(text, float) and math.isnan(text)):
		return None
	groups = [int(token) for token in re.findall(r"\d+", str(text))]
	if not groups or groups == [1]:
		return None
	return groups


def parse_sequence(text):
	"""'00.3+(02.2)' -> [('light', 0.3), ('dark', 2.2)]; parenthesised values are eclipses."""
	if not text or not isinstance(text, str):
		return None
	parts = re.findall(r"\(?\d+(?:\.\d+)?\)?", text)
	if not parts or "".join(parts) != re.sub(r"\+", "", text):
		return None
	sequence = []
	for part in parts:
		state = "dark" if part.startswith("(") else "light"
		sequence.append((state, float(part.strip("()"))))
	return sequence


def _fmt_number(value):
	return f"{value:g}"


def format_notation(characteristic, group, colours, period_s):
	"""Chart-style notation, e.g. 'Fl(2+1) W 6s'."""
	if characteristic is None:
		return None
	text = characteristic
	if group:
		text += "(" + "+".join(str(g) for g in group) + ")"
	if colours:
		text += " " + " ".join(colours)
	if period_s is not None:
		text += f" {_fmt_number(period_s)}s"
	return text


def parse_light(attributes):
	"""Turn the S-57 attributes of one LIGHTS object (a mapping) into a characteristic dict."""
	get = attributes.get
	codes = _int_list(get("LITCHR"))
	characteristic = LITCHR.get(codes[0]) if codes and codes[0] in LITCHR else None
	colours = [COLOUR[c] for c in _int_list(get("COLOUR")) if c in COLOUR]
	group = parse_group(get("SIGGRP"))
	period = _number(get("SIGPER"))
	range_nm = _number(get("VALNMR"))
	height = _number(get("HEIGHT"))
	sector_start = _number(get("SECTR1"))
	sector_end = _number(get("SECTR2"))
	notation = format_notation(characteristic, group, colours, period)
	full = notation
	if full is not None:
		if height is not None:
			full += f" {_fmt_number(height)}m"
		if range_nm is not None:
			full += f" {_fmt_number(range_nm)}M"
	sequence = parse_sequence(get("SIGSEQ"))
	return {
		"characteristic": characteristic,
		"group": group,
		"colours": colours,
		"period_s": period,
		"range_nm": range_nm,
		"range_m": None if range_nm is None else range_nm * NM_TO_M,
		"height_m": height,
		"sector_start_deg": sector_start,
		"sector_end_deg": sector_end,
		"sequence": None if sequence is None else [{"state": s, "seconds": d} for s, d in sequence],
		"category": _int_list(get("CATLIT")) or None,
		"visibility": _int_list(get("LITVIS")) or None,
		"status": _int_list(get("STATUS")) or None,
		"notation": notation,
		"full_notation": full,
		"parsed": characteristic is not None,
	}


def parse_notation(text):
	"""Parse chart notation such as 'Fl G 4s' or 'Fl(2+1) R 6s'. Returns None if it is not a light."""
	match = _NOTATION.match(text.strip())
	if not match or match["char"] not in _ABBR_TO_CODE:
		return None
	colours = match["colours"].split() if match["colours"] else []
	if any(c not in COLOUR.values() for c in colours):
		return None
	period = float(match["period"]) if match["period"] else None
	return {
		"characteristic": match["char"],
		"group": parse_group(f"({match['group']})") if match["group"] else None,
		"colours": colours,
		"period_s": period,
	}
