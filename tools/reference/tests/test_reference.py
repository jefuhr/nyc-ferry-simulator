"""Synthetic fixtures only: no private reference media is needed by these tests."""
import copy
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import reference

REPO = Path(__file__).resolve().parents[3]
SCHEMA = REPO / "art/blender/data/layout.schema.json"


def png(path, width=400, height=200):
	"""Generate a drawing with black axes on white, without image dependencies."""
	def chunk(kind, data):
		return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
	rows = []
	for y in range(height):
		rows.append(b"\0" + bytes(0 if x == 10 or y == 10 else 255 for x in range(width)))
	path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 0, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"".join(rows))) + chunk(b"IEND", b""))


def value(number, confidence="high"):
	return dict(value=number, unit="m", source="drawings/synthetic.png", method="known-dimension", confidence=confidence)


def layout():
	return dict(schema_version=1, subject="t-class", kind="hull", values={
		"length_overall": value(26), "length_waterline": value(24.5), "beam": value(8.03),
		"baseline_height": value(-1.2), "waterline_height": value(0), "underwing_height": value(1.4),
		"main_deck_height": value(2), "upper_deck_height": value(4.4), "wheelhouse_roof_height": value(6.8),
	})


def picks():
	return dict(schema_version=1, subject="t-class", kind="hull", image="drawings/synthetic.png",
		calibration=dict(method="drawing-scale", scale="1:75", pixels_per_printed_mm=3),
		measurements={"length": dict(points=[[10, 10], [210, 10]], confidence="medium")})


class MeasureTests(unittest.TestCase):
	def test_synthetic_drawing_scale_and_dpi_within_one_percent(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); (root / "drawings").mkdir(); png(root / "drawings/synthetic.png")
			p = picks()
			for calibration in (p["calibration"], dict(method="drawing-scale", scale="1:75", dpi=76.2)):
				p["calibration"] = calibration
				result = reference.measure(p, root, legacy_no_manifest=True)
				self.assertAlmostEqual(result["values"]["length"]["value"], 5, delta=.05)
				self.assertEqual(result["values"]["length"]["source"], p["image"])
	def test_rotated_axis_signed_coordinates_and_polyline(self):
		p = picks(); angle = math.radians(27)
		def point(x, y):
			return [50 + x * math.cos(angle) + y * math.sin(angle), 50 + x * math.sin(angle) - y * math.cos(angle)]
		p["axis"] = [point(0, 0), point(100, 0)]
		p["origin"] = point(0, 0)
		p["calibration"] = dict(method="known-dimension", points=[point(0, 0), point(200, 0)], length_m=5)
		p["measurements"] = {
			"fore": dict(points=[point(-20, 30)], operation="point"),
			"path": dict(points=[point(0, 0), point(200, 0), point(200, 80)], operation="polyline"),
			"height": dict(points=[point(0, 0), point(0, 80)], operation="y"),
		}
		d = reference.measure(p, legacy_no_manifest=True)["values"]
		self.assertAlmostEqual(d["fore"]["value"][0], -.5)
		self.assertAlmostEqual(d["fore"]["value"][1], .75)
		self.assertAlmostEqual(d["path"]["value"], 7, delta=.07)
		self.assertAlmostEqual(d["height"]["value"], 2, delta=.02)
	def test_shear_and_independent_transverse_scale(self):
		p = picks(); p["axis"] = [[0, 0], [100, 0]]; p["transverse_axis"] = [[0, 0], [60, -80]]
		p["calibration"] = dict(method="known-dimension", points=[[0, 0], [200, 0]], length_m=5,
			cross_dimension=dict(points=[[0, 0], [60, -80]], length_m=4))
		p["measurements"] = {"diagonal": dict(points=[[0, 0], [260, -80]])}
		self.assertAlmostEqual(reference.measure(p, legacy_no_manifest=True)["values"]["diagonal"]["value"], math.sqrt(41), delta=.06)
	def test_printed_values_keep_independent_pixel_check(self):
		p = picks(); p["measurements"]["length"].update(printed_m=5.02, confidence="high")
		self.assertEqual(reference.measure(p, legacy_no_manifest=True)["values"]["length"]["value"], 5.02)
		row = reference.compare(p)[0]
		self.assertEqual(row["measured_m"], 5)
		self.assertAlmostEqual(row["error_percent"], -0.3984063745)
	def test_reject_invalid_calibration_points_paths_and_nonfinite(self):
		bad = [dict(method="drawing-scale", scale="1:75", dpi=0),
			dict(method="drawing-scale", scale="bad", dpi=72),
			dict(method="drawing-scale", scale="1:75", dpi=72, pixels_per_printed_mm=2),
			dict(method="known-dimension", points=[[2, 2], [2, 2]], length_m=1),
			dict(method="known-dimension", points=[[0, 0], [1, 0]], length_m=float("nan"))]
		for calibration in bad:
			with self.subTest(calibration=calibration), self.assertRaises(ValueError):
				p = picks(); p["calibration"] = calibration; reference.measure(p, legacy_no_manifest=True)
		for path in ("../original.jpg", "/absolute.jpg", "drawings/../../secret.jpg"):
			with self.subTest(path=path), self.assertRaises(ValueError):
				p = picks(); p["image"] = path; reference.measure(p, legacy_no_manifest=True)
		p = picks(); p["axis"] = [[0, 0], [0, 0]]
		with self.assertRaises(ValueError): reference.measure(p, legacy_no_manifest=True)
		p = picks(); p["transverse_axis"] = [[0, 0], [100, 0]]
		with self.assertRaises(ValueError): reference.measure(p, legacy_no_manifest=True)
	def test_image_binding_rejects_resized_or_changed_image(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); (root / "drawings").mkdir(); png(root / "drawings/synthetic.png")
			p = picks(); p["image_size"] = [800, 400]
			with self.assertRaises(ValueError): reference.measure(p, root, legacy_no_manifest=True)
			p["image_size"] = [400, 200]; p["image_sha256"] = "0" * 64
			with self.assertRaises(ValueError): reference.measure(p, root, legacy_no_manifest=True)


class PickIntegrityTests(unittest.TestCase):
	def test_printed_measurements_still_reject_invalid_pixel_picks(self):
		for points in ([[0, 0], [float("nan"), 0]], [[0, 0]], [[0, 0], [True, 1]]):
			with self.subTest(points=points), self.assertRaises(ValueError):
				p = picks(); p["measurements"]["length"].update(printed_m=5, points=points)
				reference.measure(p, legacy_no_manifest=True)
	def test_image_bounds_are_checked_without_private_image(self):
		p = picks(); p["image_size"] = [100, 100]
		with self.assertRaises(ValueError): reference.measure(p, legacy_no_manifest=True)
	def test_seed_values_survive_in_hull_files_without_private_media(self):
		# The exterior owns the hull files after seeding and may add values; every seeded measurement must stay as measured.
		for name in ("t-class", "k-class"):
			with self.subTest(name=name):
				pick = reference.load_json(REPO / f"tools/reference/picks/{name}.json")
				hull = reference.load_json(REPO / f"art/blender/data/{name}-hull.json")
				seeded = reference.measure(pick, legacy_no_manifest=True)
				self.assertEqual({k: hull[k] for k in ("schema_version", "subject", "kind")}, {k: seeded[k] for k in ("schema_version", "subject", "kind")})
				for key, value in seeded["values"].items():
					self.assertEqual(hull["values"].get(key), value, key)
				self.assertEqual(reference.validate(hull, SCHEMA, REPO / "vessels"), [])
	def test_strict_json_rejects_duplicates_and_nan(self):
		with tempfile.TemporaryDirectory() as tmp:
			p = Path(tmp).resolve() / "bad.json"
			for text in ('{"value": 1, "value": 2}', '{"value": NaN}'):
				p.write_text(text)
				with self.assertRaises(ValueError): reference.load_json(p)


class CatalogTests(unittest.TestCase):
	def test_deterministic_private_catalog(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); sheets = root / "sheets"; sheets.mkdir()
			png(sheets / "z.png", 60, 30); (sheets / "a.txt").write_text("private")
			a = reference.catalog(sheets, legacy_no_manifest=True); b = reference.catalog(sheets, legacy_no_manifest=True)
			self.assertEqual(reference.json_text(a), reference.json_text(b))
			self.assertEqual([e["path"] for e in a["files"]], ["a.txt", "z.png"])
			self.assertEqual(a["files"][1]["image_size"], [60, 30])
			self.assertEqual(a["files"][0]["sha256"], hashlib.sha256(b"private").hexdigest())
			self.assertNotIn(str(root), reference.json_text(a))
	def test_symlinked_parent_of_sheets_is_refused(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); real = root / "real"; real.mkdir(); (real / "sheets").mkdir()
			(root / "alias").symlink_to(real, target_is_directory=True)
			with self.assertRaises(ValueError): reference.catalog(root / "alias/sheets", legacy_no_manifest=True)
	def test_ffprobe_optional_and_duration_is_finite(self):
		with tempfile.TemporaryDirectory() as tmp:
			sheets = Path(tmp).resolve(); (sheets / "clip.mp4").write_bytes(b"synthetic")
			with patch.object(reference.shutil, "which", return_value=None):
				self.assertIsNone(reference.catalog(sheets, legacy_no_manifest=True)["files"][0]["duration_seconds"])
			with patch.object(reference.shutil, "which", return_value="/usr/bin/ffprobe"), patch.object(reference.subprocess, "run") as run:
				run.return_value = subprocess.CompletedProcess([], 0, '{"format":{"duration":"2.5"}}', '')
				self.assertEqual(reference.catalog(sheets, legacy_no_manifest=True)["files"][0]["duration_seconds"], 2.5)
	def test_no_symlink_escape_and_no_repo_catalog(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); sheets = root / "sheets"; sheets.mkdir(); outside = root / "secret.txt"; outside.write_text("never read")
			(sheets / "escape").symlink_to(outside)
			with self.assertRaises(ValueError): reference.catalog(sheets, legacy_no_manifest=True)
		with self.assertRaises(ValueError): reference.write_catalog({"files": []}, REPO / "artifacts/reference-import/catalog.json")


class ValidationTests(unittest.TestCase):
	def test_good_layout_and_nested_values(self):
		d = layout(); d["values"]["wheelhouse"] = {"point": value([1, 2, 3])}
		self.assertEqual(reference.validate(d, SCHEMA, REPO / "vessels"), [])
	def test_value_named_group_member_is_legal(self):
		d = layout(); d["values"]["group"] = {"value": value(2)}
		self.assertEqual(reference.validate(d, SCHEMA, REPO / "vessels"), [])
		other = copy.deepcopy(d); other["values"]["group"]["value"] = value(3, "low")
		merged, skipped = reference.merge(d, other)
		self.assertEqual(merged["values"]["group"]["value"]["value"], 2)
		self.assertIn("group.value", skipped)
	def test_broken_schema_and_semantics(self):
		for field, bad in (("source", ""), ("method", "guess"), ("unit", "feet"), ("confidence", "certain"), ("value", float("nan")), ("value", True)):
			with self.subTest(field=field, bad=bad):
				d = layout(); d["values"]["beam"][field] = bad
				self.assertTrue(reference.validate(d, SCHEMA, REPO / "vessels"))
		for key, number in (("length_overall", 30), ("upper_deck_height", -2), ("length_waterline", 30), ("beam", -1)):
			with self.subTest(key=key):
				d = layout(); d["values"][key] = value(number)
				self.assertTrue(reference.validate(d, SCHEMA, REPO / "vessels"))
		d = layout(); del d["values"]["beam"]["source"]
		self.assertTrue(reference.validate(d, SCHEMA, REPO / "vessels"))
		d = layout(); d["values"]["junk"] = {"nested": {"too_deep": value(1)}}
		self.assertTrue(reference.validate(d, SCHEMA, REPO / "vessels"))
	def test_schema_is_read_not_just_hardcoded(self):
		with tempfile.TemporaryDirectory() as tmp:
			path = Path(tmp).resolve() / "schema.json"; schema = json.loads(SCHEMA.read_text())
			schema["properties"]["subject"] = {"const": "k-class"}; path.write_text(json.dumps(schema))
			self.assertTrue(reference.validate(layout(), path, REPO / "vessels"))


class ExportTests(unittest.TestCase):
	def test_confidence_merge_preserves_nested_siblings_and_input(self):
		old = layout(); old["values"]["nested"] = {"a": value(1), "b": value(2)}
		new = copy.deepcopy(old); new["values"] = {"length_overall": value(26.02, "low"), "nested": {"a": value(3, "low"), "c": value(4)}}
		before = copy.deepcopy(old); merged, skipped = reference.merge(old, new)
		self.assertEqual(old, before); self.assertEqual(merged["values"]["length_overall"]["value"], 26)
		self.assertEqual(merged["values"]["nested"]["b"]["value"], 2)
		self.assertEqual(merged["values"]["nested"]["c"]["value"], 4)
		self.assertEqual(set(skipped), {"length_overall", "nested.a"})
		forced, _ = reference.merge(old, new, force=True)
		self.assertEqual(forced["values"]["length_overall"]["value"], 26.02)
	def test_cross_subject_and_shape_collision_refused(self):
		a = layout(); b = layout(); b["subject"] = "k-class"
		with self.assertRaises(ValueError): reference.merge(a, b)
		b = layout(); b["values"]["beam"] = {"x": value(1)}
		with self.assertRaises(ValueError): reference.merge(a, b)
	def test_cli_measure_export_validate_and_invalid_export_is_atomic(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); pick = root / "picks.json"; patch_file = root / "patch.json"; target = root / "layout.json"
			p = picks(); p["measurements"] = {"length_overall": dict(printed_m=26, confidence="high")}
			pick.write_text(json.dumps(p)); target.write_text(json.dumps(layout()))
			def run(*args):
				return subprocess.run([sys.executable, str(REPO / "tools/reference/reference.py"), *map(str, args)], capture_output=True, text=True)
			self.assertEqual(run("measure", pick, "--legacy-no-manifest", "--output", patch_file).returncode, 0)
			p = json.loads(patch_file.read_text()); p["values"]["length_overall"]["value"] = 26.01; patch_file.write_text(json.dumps(p))
			result = run("export", patch_file, "--layout", target)
			self.assertEqual(result.returncode, 0, result.stderr); self.assertIn("26.01", result.stdout)
			self.assertEqual(run("validate", target).returncode, 0)
			before = target.read_bytes(); p["values"]["length_overall"]["value"] = 30; patch_file.write_text(json.dumps(p))
			self.assertNotEqual(run("export", patch_file, "--layout", target).returncode, 0)
			self.assertEqual(target.read_bytes(), before)


if __name__ == "__main__":
	unittest.main()
