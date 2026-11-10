"""Offline reference measurements. Standard library only; never copies source media."""
from __future__ import annotations

import argparse
import copy
import difflib
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import shutil
import struct
import subprocess
import sys
import tempfile

REPO = Path(__file__).resolve().parents[2]
SCHEMA = REPO / "art/blender/data/layout.schema.json"
CONFIDENCE = {"low": 0, "medium": 1, "high": 2}
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"}
VIDEO_EXTENSIONS = {".mp4", ".mov", ".m4v", ".webm", ".mkv"}
AUDIO_EXTENSIONS = {".wav", ".mp3", ".m4a", ".flac", ".aiff", ".ogg"}


def json_text(data):
	return json.dumps(data, ensure_ascii=False, allow_nan=False, sort_keys=True, indent="\t") + "\n"


def load_json(path):
	def constant(text):
		raise ValueError(f"Nonfinite JSON number: {text}")
	def unique(pairs):
		result = {}
		for key, value in pairs:
			if key in result:
				raise ValueError(f"Duplicate JSON key: {key}")
			result[key] = value
		return result
	return json.loads(Path(path).read_text(), parse_constant=constant, object_pairs_hook=unique)


def finite(number, label="number", positive=False):
	if isinstance(number, bool) or not isinstance(number, (int, float)) or not math.isfinite(number):
		raise ValueError(f"{label} must be a finite number")
	if positive and number <= 0:
		raise ValueError(f"{label} must be positive")
	return float(number)


def relative_path(text):
	if not isinstance(text, str) or not text or "\\" in text or ":" in text:
		raise ValueError("Image must be a relative sheet path")
	path = PurePosixPath(text)
	if path.is_absolute() or ".." in path.parts or str(path) != text or text == ".":
		raise ValueError("Image must be a normalized relative sheet path without traversal")
	return path


def sheet_root(root):
	root = Path(root).expanduser().absolute()
	if any(p.is_symlink() for p in (root, *root.parents)):
		raise ValueError("Symlinks are not permitted in sheets paths; supply the real path")
	if not root.is_dir():
		raise ValueError("Sheets must be an existing directory")
	return root.resolve(strict=True)


def sheet_path(root, relative):
	root = sheet_root(root)
	path = root.joinpath(relative_path(relative))
	# Do not resolve symlinks first: even links back inside sheets are disallowed.
	for parent in [path, *path.parents]:
		if parent == root:
			break
		if parent.is_symlink():
			raise ValueError("Symlink in sheet path is not permitted")
	if not path.is_file():
		raise ValueError(f"Missing sheet: {relative}")
	return path


def sha256(path):
	with Path(path).open("rb") as stream:
		return hashlib.file_digest(stream, "sha256").hexdigest()


def image_size(path):
	"""Read PNG/JPEG/GIF/BMP headers, without decoding or rewriting private pixels."""
	with Path(path).open("rb") as stream:
		header = stream.read(32)
		if header.startswith(b"\x89PNG\r\n\x1a\n") and len(header) >= 24:
			return list(struct.unpack(">II", header[16:24]))
		if header[:6] in (b"GIF87a", b"GIF89a"):
			return list(struct.unpack("<HH", header[6:10]))
		if header[:2] == b"BM" and len(header) >= 26:
			width, height = struct.unpack("<ii", header[18:26]); return [abs(width), abs(height)]
		if header[:2] == b"\xff\xd8":
			stream.seek(2)
			while True:
				prefix = stream.read(1)
				if not prefix:
					break
				if prefix != b"\xff":
					continue
				marker = stream.read(1)
				while marker == b"\xff":
					marker = stream.read(1)
				if marker in (b"\xd9", b"\xda", b""):
					break
				if marker == b"\x00" or marker == b"\x01" or 0xD0 <= marker[0] <= 0xD8:
					continue
				length_bytes = stream.read(2)
				if len(length_bytes) != 2:
					break
				length = struct.unpack(">H", length_bytes)[0]
				if length < 2:
					break
				if marker[0] in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
					data = stream.read(5)
					if len(data) == 5:
						height, width = struct.unpack(">HH", data[1:]); return [width, height]
					break
				stream.seek(length - 2, 1)
	# Optional installed ffprobe supports TIFF/WebP and additional image formats.
	probe = shutil.which("ffprobe")
	if probe:
		data = probe_media(path, probe, "stream=width,height")
		for stream in data.get("streams", []):
			if stream.get("width", 0) > 0 and stream.get("height", 0) > 0:
				return [stream["width"], stream["height"]]
	raise ValueError("Unsupported or unreadable image header (TIFF/WebP require ffprobe)")


def probe_media(path, probe, entries):
	try:
		result = subprocess.run([probe, "-v", "error", "-show_entries", entries, "-of", "json", str(path)],
			capture_output=True, text=True, check=True, timeout=30)
		return json.loads(result.stdout)
	except (subprocess.SubprocessError, json.JSONDecodeError) as exc:
		# ffprobe stderr can contain private metadata or filenames; do not echo it.
		raise ValueError("ffprobe could not read a media file") from exc


def catalog(sheets, manifest=None, legacy_no_manifest=False):
	import intake
	root = sheet_root(sheets)
	if not legacy_no_manifest:
		intake.load_manifest(manifest or root / "manifest.json")
	files = []
	# Reject links before opening any file. Never follow symlinked directories.
	for parent, dirs, names in os.walk(root, followlinks=False):
		for name in dirs + names:
			if (Path(parent) / name).is_symlink():
				raise ValueError("Symlinks are not permitted in sheets")
		files.extend(Path(parent) / name for name in names)
	probe = shutil.which("ffprobe")
	entries = []
	for path in sorted(files, key=lambda p: p.relative_to(root).as_posix()):
		if not path.is_file():
			raise ValueError("Sheets must contain regular files only")
		if path.name in ("manifest.json", "requests.json", "README.md"):
			continue
		if not legacy_no_manifest:
			intake.gate(root, path.relative_to(root).as_posix(), manifest)
		ext = path.suffix.lower()
		kind = "image" if ext in IMAGE_EXTENSIONS else "video" if ext in VIDEO_EXTENSIONS else "audio" if ext in AUDIO_EXTENSIONS else "document"
		entry = dict(path=path.relative_to(root).as_posix(), kind=kind, size_bytes=path.stat().st_size, sha256=sha256(path))
		if kind == "image":
			entry["image_size"] = image_size(path)
		if kind in ("video", "audio"):
			entry["duration_seconds"] = None
			entry["duration_status"] = "ffprobe-unavailable"
			if probe:
				data = probe_media(path, probe, "format=duration")
				try:
					duration = float(data["format"]["duration"])
				except (KeyError, TypeError, ValueError) as exc:
					raise ValueError("Media has no numeric duration") from exc
				entry["duration_seconds"] = finite(duration, "duration", positive=True)
				entry["duration_status"] = "ffprobe"
		entries.append(entry)
	return dict(schema_version=1, files=entries)


def write_json(data, output, private=False):
	output = Path(output).expanduser()
	if output.is_symlink():
		raise ValueError("Output must not be a symlink")
	output.parent.mkdir(parents=True, exist_ok=True)
	payload = json_text(data)
	# Atomic replacement: a validation/write error cannot truncate an existing layout.
	mode = output.stat().st_mode & 0o777 if output.exists() and not private else 0o600
	fd, temporary = tempfile.mkstemp(prefix=".reference-", dir=output.parent)
	try:
		with os.fdopen(fd, "w") as stream:
			stream.write(payload)
		os.chmod(temporary, mode)
		os.replace(temporary, output)
	finally:
		if os.path.exists(temporary):
			os.unlink(temporary)


def write_catalog(data, output):
	path = Path(output).expanduser().resolve()
	# Catalog paths may contain identities. Even ignored artifacts inside a repo
	# are refused; default output is in the private cache, outside the checkout.
	if path.is_relative_to(REPO) or any((p / ".git").exists() for p in path.parents):
		raise ValueError("Private catalogs must be outside every Git repository")
	write_json(data, output, private=True)


def point(raw):
	if not isinstance(raw, list) or len(raw) != 2:
		raise ValueError("A picked point must have exactly two pixel coordinates")
	return tuple(finite(n, "pixel coordinate") for n in raw)


def direction(raw):
	if not isinstance(raw, list) or len(raw) != 2:
		raise ValueError("An axis requires two points")
	a, b = map(point, raw); dx, dy = b[0] - a[0], b[1] - a[1]
	length = math.hypot(dx, dy)
	if length < 1e-9:
		raise ValueError("Coincident axis points")
	return dx / length, dy / length


def transform(pick):
	if pick["calibration"].get("method") == "plane-homography":
		from homography import fit
		return fit(pick["calibration"])
	ux, uy = direction(pick.get("axis", [[0, 0], [1, 0]]))
	vx, vy = direction(pick["transverse_axis"]) if "transverse_axis" in pick else (uy, -ux)
	determinant = ux * vy - uy * vx
	if abs(determinant) < 1e-6:
		raise ValueError("Axes must not be parallel")
	def project(raw):
		x, y = point(raw)
		return ((x * vy - y * vx) / determinant, (ux * y - uy * x) / determinant)
	c = pick["calibration"]
	method = c.get("method")
	if method == "drawing-scale":
		try:
			left, right = c["scale"].split(":")
			ratio = finite(float(right), "scale denominator", True) / finite(float(left), "scale numerator", True)
		except (AttributeError, KeyError, ValueError) as exc:
			raise ValueError("Drawing scale must be positive numerator:denominator, e.g. 1:75") from exc
		if ("dpi" in c) == ("pixels_per_printed_mm" in c):
			raise ValueError("Provide exactly one of dpi and pixels_per_printed_mm")
		ppm = finite(c["dpi"], "DPI", True) / 25.4 if "dpi" in c else finite(c["pixels_per_printed_mm"], "pixels per printed mm", True)
		sx = ratio / (1000 * ppm)
		sy = ratio / (1000 * finite(c.get("transverse_pixels_per_printed_mm", ppm), "transverse pixels per printed mm", True))
	elif method == "known-dimension":
		def scale_for(dimension, component=None):
			points = dimension["points"]
			if len(points) != 2:
				raise ValueError("Known dimension requires two points")
			a, b = map(project, points); dx, dy = b[0] - a[0], b[1] - a[1]
			pixels = math.hypot(dx, dy) if component is None else abs((dx, dy)[component])
			return finite(dimension["length_m"], "known length", True) / finite(pixels, "calibration span", True)
		if "cross_dimension" in c:
			sx, sy = scale_for(c, 0), scale_for(c["cross_dimension"], 1)
		else:
			sx = sy = scale_for(c)
	else:
		raise ValueError("Calibration method must be drawing-scale or known-dimension")
	origin = project(pick.get("origin", [0, 0]))
	def metres(raw):
		x, y = project(raw)
		return (finite((x - origin[0]) * sx), finite((y - origin[1]) * sy))
	return metres


def pixel_value(spec, convert):
	points = list(map(convert, spec.get("points", [])))
	op = spec.get("operation", "distance")
	if op == "point" and len(points) == 1:
		return [round(v, 6) for v in points[0]]
	if op == "polyline" and len(points) >= 2:
		return sum(math.dist(a, b) for a, b in zip(points, points[1:]))
	if op in ("distance", "x", "y") and len(points) == 2:
		a, b = points
		return math.dist(a, b) if op == "distance" else b[0] - a[0] if op == "x" else b[1] - a[1]
	raise ValueError("Pick operation requires one point (point), two (distance/x/y), or at least two (polyline)")


def measure(pick, sheets=None, manifest=None, legacy_no_manifest=False):
	if pick.get("schema_version") != 1:
		raise ValueError("Unsupported pick schema_version")
	source = str(relative_path(pick["image"]))
	source_entry = {}
	if not legacy_no_manifest:
		if sheets is None:
			raise ValueError(f"{source}: manifest verification requires --sheets")
		import intake
		source_manifest, source_entry = intake.gate(sheets, source, manifest)
		source_entry = {**source_entry}
		derived = next((e for e in source_manifest.get("derived", []) if e["path"] == source), None)
		if derived is not None: source_entry["method"] = derived["method"]
	if sheets is not None:
		path = sheet_path(sheets, source)
		if "image_size" in pick and image_size(path) != pick["image_size"]:
			raise ValueError("Sheet dimensions changed; repick or verify calibration")
		if "image_sha256" in pick and sha256(path) != pick["image_sha256"]:
			raise ValueError("Sheet hash changed; repick or verify calibration")
	convert = transform(pick)
	if "image_size" in pick:
		size = pick["image_size"]
		if not isinstance(size, list) or len(size) != 2 or any(type(n) is not int or n <= 0 for n in size):
			raise ValueError("image_size must contain two positive integer pixel counts")
		def check_points(container):
			if isinstance(container, dict):
				for key, item in container.items():
					if key in ("points", "axis", "transverse_axis", "origin"):
						for raw in ([item] if key == "origin" else item):
							x, y = point(raw)
							if not (0 <= x < size[0] and 0 <= y < size[1]):
								raise ValueError("Picked point lies outside image_size")
					else:
						check_points(item)
		check_points(pick)
	values = {}
	for name, spec in pick["measurements"].items():
		measured = pixel_value(spec, convert) if "points" in spec else None
		if "printed_m" not in spec and measured is None:
			raise ValueError("Measurement requires pixel points or printed_m")
		number = finite(spec["printed_m"], name) if "printed_m" in spec else measured
		offset = finite(spec.get("offset_m", 0), "offset")
		if isinstance(number, list):
			if offset:
				raise ValueError("offset_m applies to scalar measurements only")
		else:
			number = round(finite(number + offset), 6)
		method = pick["calibration"]["method"]
		if method == "plane-homography": method = "known-dimension"
		if source_entry.get("method") == "photogrammetry": method = "photogrammetry"
		item = dict(value=number, unit="m", source=source, method=method, confidence=spec.get("confidence", "medium"))
		notes = []
		if source_entry.get("allowed_use") == "measurement-only" or any(s["consent"]["scope"] == "measurement-only" for s in source_entry.get("sources", [])):
			notes.append("Measurement-only reference (private); numbers only, no tracing or reproduction.")
		if hasattr(convert, "report"):
			r = convert.report
			notes.append(f"Plane homography reprojection RMS {r['rms_px']:.6f} px, max {r['max_px']:.6f} px; tolerance {r['tolerance_px']:g} px. Valid only on the calibrated plane.")
		if "printed_m" in spec:
			notes.append(f"Printed dimension: {spec['printed_m']:g} m; not a pixel-derived value.")
		if "note" in spec:
			notes.append(spec["note"])
		if notes:
			item["note"] = " ".join(notes)
		values[name] = item
	for name, item in pick.get("estimates", {}).items():
		if name in values:
			raise ValueError("Estimate collides with a measurement")
		if item.get("confidence") != "low" or item.get("method") not in ("simulator-estimate", "photo-estimate", "video-estimate"):
			raise ValueError("Unmeasured estimates must have low confidence and an estimate method")
		values[name] = copy.deepcopy(item)
	result = dict(schema_version=1, subject=pick["subject"], kind=pick["kind"], values=values)
	if "notes" in pick:
		result["notes"] = pick["notes"]
	errors = validate(result, SCHEMA)
	if errors:
		raise ValueError("; ".join(errors))
	return result


def compare(pick):
	convert = transform(pick); rows = []
	for name, spec in pick["measurements"].items():
		if "printed_m" not in spec or "points" not in spec:
			continue
		printed = finite(spec["printed_m"]); measured = pixel_value(spec, convert)
		if isinstance(measured, list):
			raise ValueError("Printed comparison must be scalar")
		rows.append(dict(name=name, measured_m=measured, printed_m=printed,
			error_percent=None if printed == 0 else 100 * (measured - printed) / printed,
			calibration_anchor=spec.get("calibration_anchor", False)))
	return rows


# A deliberately small JSON Schema evaluator, covering every keyword in the
# repository schema. Unsupported assertion keywords fail closed, never silently pass.
SCHEMA_KEYS = {"$schema", "$id", "$defs", "$ref", "title", "description", "type", "required", "properties", "additionalProperties", "oneOf", "enum", "const", "items", "minItems", "maxItems", "minLength"}


def schema_errors(data, schema, root=None, path="$"):
	root = root or schema
	errors = []
	unknown = set(schema) - SCHEMA_KEYS
	if unknown:
		return [f"{path}: unsupported schema keywords {sorted(unknown)}"]
	if "$ref" in schema:
		ref = schema["$ref"]
		if not ref.startswith("#/"):
			return [f"{path}: only local schema references are supported"]
		target = root
		for key in ref[2:].split("/"):
			target = target[key.replace("~1", "/").replace("~0", "~")]
		errors += schema_errors(data, target, root, path)
	if "oneOf" in schema:
		matches = sum(not schema_errors(data, candidate, root, path) for candidate in schema["oneOf"])
		if matches != 1:
			errors.append(f"{path}: must match exactly one allowed shape")
	if "const" in schema and (data != schema["const"] or type(data) is bool and type(schema["const"]) is not bool):
		errors.append(f"{path}: expected constant {schema['const']}")
	if "enum" in schema and not any(data == v and isinstance(data, bool) == isinstance(v, bool) for v in schema["enum"]):
		errors.append(f"{path}: unknown value {data!r}")
	type_name = schema.get("type")
	checks = {"object": isinstance(data, dict), "array": isinstance(data, list), "string": isinstance(data, str),
		"number": isinstance(data, (int, float)) and not isinstance(data, bool) and math.isfinite(data)}
	if type_name is not None and not checks.get(type_name, False):
		return errors + [f"{path}: expected {type_name}"]
	if isinstance(data, dict):
		for key in schema.get("required", []):
			if key not in data:
				errors.append(f"{path}.{key}: required")
		props = schema.get("properties", {})
		for key, item in data.items():
			if key in props:
				errors += schema_errors(item, props[key], root, f"{path}.{key}")
			elif schema.get("additionalProperties") is False:
				errors.append(f"{path}.{key}: unknown property")
			elif isinstance(schema.get("additionalProperties"), dict):
				errors += schema_errors(item, schema["additionalProperties"], root, f"{path}.{key}")
	if isinstance(data, list):
		if len(data) < schema.get("minItems", 0) or len(data) > schema.get("maxItems", math.inf):
			errors.append(f"{path}: array length out of bounds")
		if "items" in schema:
			for i, item in enumerate(data):
				errors += schema_errors(item, schema["items"], root, f"{path}[{i}]")
	if isinstance(data, str) and len(data) < schema.get("minLength", 0):
		errors.append(f"{path}: string is empty")
	return errors


def is_value(item):
	# A group may legitimately have a member named "value". Check its type,
	# after schema validation, rather than treating that name alone as a leaf.
	return isinstance(item.get("value"), (int, float, list))


def validate(data, schema_path=SCHEMA, vessels=None):
	errors = schema_errors(data, load_json(schema_path))
	if errors:
		return errors
	# Whitespace-only provenance is not a meaningful source.
	def leaves(values):
		for name, item in values.items():
			if is_value(item):
				yield name, item
			else:
				for subname, subitem in item.items():
					yield f"{name}.{subname}", subitem
	for name, item in leaves(data["values"]):
		if not item["source"].strip():
			errors.append(f"{name}: source must not be blank")
	if data["kind"] != "hull":
		return errors
	if data["subject"] not in ("t-class", "k-class"):
		return errors + ["hull subject must be t-class or k-class"]
	values = data["values"]
	def number(name):
		if name not in values:
			return None
		item = values[name]
		if not isinstance(item.get("value"), (int, float)) or item.get("unit") != "m":
			errors.append(f"{name}: hull length/height must be a scalar in metres")
			return None
		return item["value"]
	loa = number("length_overall")
	for name in ("length_overall", "length_waterline", "beam", "frame_spacing", "demihull_width", "sponson_width"):
		n = number(name)
		if n is not None and n <= 0:
			errors.append(f"{name}: must be positive")
	if vessels is not None:
		if loa is None:
			errors.append("length_overall: required for full hull validation")
		else:
			vessel = load_json(Path(vessels) / f"{data['subject']}.json")
			if abs(loa - vessel["particulars"]["lengthOverall"]["value"]) > .05 + 1e-9:
				errors.append("length_overall differs from vessel by more than 0.05 m")
	lwl, beam = number("length_waterline"), number("beam")
	if loa is not None and lwl is not None and lwl > loa:
		errors.append("length_waterline exceeds length_overall")
	if loa is not None and beam is not None and beam >= loa:
		errors.append("beam must be less than length_overall")
	order = ("baseline_height", "waterline_height", "underwing_height", "main_deck_height", "upper_deck_height", "wheelhouse_roof_height")
	last = None
	for name in order:
		n = number(name)
		if n is not None:
			if last is not None and n <= last[1]:
				errors.append(f"{name} must be above {last[0]}")
			last = (name, n)
	waterline = number("waterline_height")
	if waterline is not None and abs(waterline) > 1e-6:
		errors.append("waterline_height must be zero in the layout coordinate frame")
	aft, fore = number("wheelhouse_aft_z"), number("wheelhouse_fore_z")
	if aft is not None and fore is not None and aft >= fore:
		errors.append("wheelhouse_aft_z must be aft of wheelhouse_fore_z")
	if loa is not None and any(n is not None and abs(n) > loa / 2 for n in (aft, fore)):
		errors.append("wheelhouse extents lie outside length_overall")
	return errors


def merge(old, incoming, force=False):
	for field in ("schema_version", "subject", "kind"):
		if old.get(field) != incoming.get(field):
			raise ValueError(f"Cannot merge different {field}")
	for data in (old, incoming):
		errors = validate(data)
		if errors:
			raise ValueError("; ".join(errors))
	result = copy.deepcopy(old); skipped = []
	def walk(target, source, prefix=""):
		for key, item in source.items():
			name = prefix + key
			if key not in target:
				target[key] = copy.deepcopy(item)
			elif is_value(target[key]) != is_value(item):
				raise ValueError(f"Cannot replace group/value shape: {name}")
			elif not is_value(item):
				walk(target[key], item, name + ".")
			elif force or CONFIDENCE[item["confidence"]] >= CONFIDENCE[target[key]["confidence"]]:
				target[key] = copy.deepcopy(item)
			else:
				skipped.append(name)
	walk(result["values"], incoming["values"])
	return result, skipped


def main(argv=None):
	parser = argparse.ArgumentParser(description=__doc__)
	sub = parser.add_subparsers(dest="command", required=True)
	p = sub.add_parser("catalog", help="Private deterministic catalog, outside any Git repository")
	p.add_argument("--sheets", type=Path, default=Path.home() / "FerrySim-reference/v04/sheets")
	p.add_argument("--output", type=Path, default=Path.home() / "FerrySim-cache/reference-import/catalog.json")
	p = sub.add_parser("measure", help="Convert a pick file into layout values")
	p.add_argument("picks", type=Path); p.add_argument("--output", type=Path, required=True)
	p.add_argument("--sheets", type=Path, help="Verify private image size/hash when provided; no media needed otherwise")
	p.add_argument("--comparisons", type=Path, help="Write independent measured-vs-printed checks")
	p = sub.add_parser("validate", help="Validate schema and class-specific geometry")
	p.add_argument("layouts", type=Path, nargs="+"); p.add_argument("--schema", type=Path, default=SCHEMA)
	p.add_argument("--vessels", type=Path, default=REPO / "vessels")
	p = sub.add_parser("export", help="Merge measured values, print diff, validate, then atomically write")
	p.add_argument("values", type=Path); p.add_argument("--layout", type=Path, required=True)
	p.add_argument("--force", action="store_true"); p.add_argument("--dry-run", action="store_true")
	for name in ("catalog", "measure"):
		command_parser = sub.choices[name]
		command_parser.add_argument("--manifest", type=Path, help="Defaults to manifest.json beside the sheets")
		command_parser.add_argument("--legacy-no-manifest", action="store_true", help="Explicit legacy offline reproduction without consent evidence")
	p = sub.add_parser("audit", help="List picks and values affected by missing/excluded/withdrawn sheets")
	p.add_argument("--manifest", type=Path, required=True); p.add_argument("files", type=Path, nargs="+")
	p = sub.add_parser("requests", help="Private shot-list request register")
	r = p.add_subparsers(dest="action", required=True)
	for action in ("status", "mark"):
		p = r.add_parser(action); p.add_argument("--manifest", type=Path, required=True)
		if action == "mark":
			p.add_argument("request"); p.add_argument("--status", choices=("open","received","screened","used","needs-reshoot"), required=True)
	import recon
	recon.add_parser(sub)
	args = parser.parse_args(argv)
	try:
		if getattr(args,"legacy_no_manifest",False):
			print("WARNING: --legacy-no-manifest bypasses consent/screening verification; legacy reproduction only", file=sys.stderr)
		if args.command == "recon":
			return recon.run(args)
		if args.command == "audit":
			import intake
			findings = intake.audit(args.manifest, args.files)
			for finding in findings: print(finding)
			return 1 if findings else 0
		if args.command == "requests":
			import intake
			intake.requests(args.manifest, args.action, getattr(args,"request",None), getattr(args,"status",None))
			return 0
		if args.command == "catalog":
			output = args.output.expanduser().resolve()
			if output.is_relative_to(args.sheets.expanduser().resolve()):
				raise ValueError("Catalog must not be written inside the sheets input")
			data = catalog(args.sheets, args.manifest, args.legacy_no_manifest); write_catalog(data, args.output)
			print(f"Catalogued {len(data['files'])} files; sha256 {sha256(args.output)}")
		elif args.command == "measure":
			picks = load_json(args.picks); data = measure(picks, args.sheets, args.manifest, args.legacy_no_manifest)
			if picks["calibration"]["method"] == "plane-homography":
				print("Homography reprojection: " + json_text(transform(picks).report).strip())
			comparisons = compare(picks) if args.comparisons else None
			write_json(data, args.output)
			if args.comparisons:
				write_json(comparisons, args.comparisons)
			print(f"Measured {len(data['values'])} values for {data['subject']}")
		elif args.command == "validate":
			failed = False
			for path in args.layouts:
				errors = validate(load_json(path), args.schema, args.vessels)
				print(f"{path}: {'INVALID' if errors else 'valid'}")
				for error in errors:
					print(f"  {error}")
				failed |= bool(errors)
			return 1 if failed else 0
		elif args.command == "export":
			incoming = load_json(args.values)
			old = load_json(args.layout) if args.layout.exists() else {**incoming, "values": {}}
			result, skipped = merge(old, incoming, args.force)
			errors = validate(result, SCHEMA, REPO / "vessels")
			if errors:
				raise ValueError("; ".join(errors))
			print("".join(difflib.unified_diff(json_text(old).splitlines(True), json_text(result).splitlines(True), fromfile=str(args.layout), tofile=str(args.layout) + " (merged)")), end="")
			for name in skipped:
				print(f"Kept higher-confidence value: {name}")
			if not args.dry_run:
				write_json(result, args.layout)
		return 0
	except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as exc:
		print(f"error: {exc}", file=sys.stderr)
		return 2


if __name__ == "__main__":
	raise SystemExit(main())
