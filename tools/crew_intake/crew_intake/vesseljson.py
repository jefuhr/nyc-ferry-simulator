"""Vessel parameter JSON: loading it with the file's own number spelling, addressing leaves by dotted
path, writing it back in the repository's style, and fingerprinting it."""

import hashlib
import json
from pathlib import Path


class PatchError(Exception):
	pass


class SourceFloat(float):
	"""A float that remembers how the vessel file spelled it (8.0010 stays 8.0010), so unchanged lines
	round-trip byte for byte. Values the tool computes are plain floats."""

	def __new__(cls, text: str):
		instance = super().__new__(cls, text)
		instance.text = text
		return instance


def parse_vessel(text: str) -> dict:
	return json.loads(text, parse_float=SourceFloat)


def load_vessel(path: Path) -> dict:
	return parse_vessel(Path(path).read_text())


# --- paths into the vessel JSON -------------------------------------------------------------------

def get_path(vessel: dict, path: str):
	node = vessel
	for key in path.split("."):
		if not isinstance(node, dict) or key not in node:
			raise PatchError(f"vessel file has no '{path}'")
		node = node[key]
	return node


def set_path(vessel: dict, path: str, value):
	keys = path.split(".")
	node = vessel
	for key in keys[:-1]:
		if key not in node or not isinstance(node[key], dict):
			raise PatchError(f"vessel file has no '{path}'")
		node = node[key]
	if keys[-1] not in node:
		raise PatchError(f"vessel file has no '{path}'")
	node[keys[-1]] = value


# --- writing --------------------------------------------------------------------------------------

def _is_leaf(value: dict) -> bool:
	return "value" in value and not any(isinstance(item, (dict, list)) for item in value.values())


def _scalar(value) -> str:
	if isinstance(value, SourceFloat):
		return value.text
	return json.dumps(value, ensure_ascii=False)


def _format(value, depth: int) -> str:
	indent = "\t" * depth
	if isinstance(value, dict):
		if not value:
			return "{}"
		if _is_leaf(value):
			return "{ " + ", ".join(f"{json.dumps(key, ensure_ascii=False)}: {_scalar(item)}" for key, item in value.items()) + " }"
		inner = ",\n".join(f"{indent}\t{json.dumps(key, ensure_ascii=False)}: {_format(item, depth + 1)}" for key, item in value.items())
		return "{\n" + inner + "\n" + indent + "}"
	if isinstance(value, list):
		if not value:
			return "[]"
		inner = ",\n".join(f"{indent}\t{_format(item, depth + 1)}" for item in value)
		return "[\n" + inner + "\n" + indent + "]"
	return _scalar(value)


def format_vessel_json(vessel: dict) -> str:
	"""Tabs, one ParameterValue per line, expanded sections: the style of vessels/*.json."""
	return _format(vessel, 0) + "\n"


def vessel_fingerprint(vessel: dict) -> str:
	"""SHA-256 of the vessel in its canonical file form. For a file already in the repository's style this
	equals the hash of the file's bytes, so a patch can name exactly the version it was computed against."""
	return hashlib.sha256(format_vessel_json(vessel).encode("utf-8")).hexdigest()
