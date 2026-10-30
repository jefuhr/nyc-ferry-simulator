"""Release verifier/recorder failure shields, using tiny independent fixtures."""
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
ROOT = pathlib.Path(__file__).resolve().parents[5]
TOOLS = ROOT / 'unity/Assets/FerrySim/Build/Editor'
spec = importlib.util.spec_from_file_location('harbor_verify', TOOLS / 'verify_harbor_payload.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)


class PayloadVerifierTests(unittest.TestCase):
	def setUp(self):
		self.temp = tempfile.TemporaryDirectory()
		self.addCleanup(self.temp.cleanup)
		base = pathlib.Path(self.temp.name)
		self.source, self.copy = base / 'source', base / 'copy'
		self.source.mkdir(); self.copy.mkdir()
		data = b'harbor bytes'
		self.entry = {'path': 'data.bin', 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
		for root in (self.source, self.copy): (root / 'data.bin').write_bytes(data)
		self.inventory = {'schema': 'fixture', 'file_count': 1, 'bytes': len(data), 'files': [self.entry]}
		self.record = {'payload': [self.entry], 'version': '0.2.0', 'commit': 'abc', 'dirty': True, 'target': 'StandaloneOSX',
			'harbor': {key: value for key, value in self.inventory.items() if key != 'files'}, 'scenes': ['Assets/FerrySim/Harbor/Scenes/Harbor.unity']}
		self.report = {'scenes': self.record['scenes'], 'packed_assets': []}
		self.inventory_path, self.report_path = base / 'inventory.json', base / 'report.json'

	def run_verify(self):
		(self.copy / 'build-info.json').write_text(json.dumps(self.record))
		self.inventory_path.write_text(json.dumps(self.inventory)); self.report_path.write_text(json.dumps(self.report))
		verify.verify(str(self.copy), str(self.source), str(self.inventory_path), '0.2.0', 'abc', '1', 'mac', str(self.report_path))

	def test_exact_inventory_passes(self): self.run_verify()
	def test_digest_streams_sha256_without_file_digest(self):
		# Review item 8: the verifier must run on the macOS system Python 3.9, which has no hashlib.file_digest.
		path = self.source / 'big.bin'
		data = bytes(range(256)) * 8192 + b'tail'
		path.write_bytes(data)
		self.assertEqual(verify.digest(path), hashlib.sha256(data).hexdigest())
		self.assertNotIn('hashlib.file_digest(', (TOOLS / 'verify_harbor_payload.py').read_text())
	def test_unrecorded_copy_is_refused(self):
		(self.copy / 'raw.bin').write_bytes(b'raw')
		with self.assertRaisesRegex(ValueError, 'actual StreamingAssets'): self.run_verify()
	def test_changed_source_is_refused(self):
		(self.source / 'data.bin').write_bytes(b'changed')
		with self.assertRaisesRegex(ValueError, 'source does not match'): self.run_verify()
	def test_changed_copy_is_refused(self):
		(self.copy / 'data.bin').write_bytes(b'changed')
		with self.assertRaisesRegex(ValueError, 'copy does not match'): self.run_verify()
	def test_duplicate_record_is_refused(self):
		self.record['payload'].append(self.entry)
		with self.assertRaisesRegex(ValueError, 'duplicate paths'): self.run_verify()
	def test_slice_assets_are_refused(self):
		self.report['packed_assets'] = ['Assets/FerrySim/World/Generated/terrain.asset']
		with self.assertRaisesRegex(ValueError, 'slice scene/generated'): self.run_verify()
	def test_wrong_scene_is_refused(self):
		self.report['scenes'] = ['Assets/FerrySim/Slice/Scenes/LowerEastRiverSlice.unity']
		with self.assertRaisesRegex(ValueError, 'exactly Harbor'): self.run_verify()
	def test_symlink_is_refused(self):
		(self.copy / 'data.bin').unlink(); (self.copy / 'data.bin').symlink_to(self.source / 'data.bin')
		with self.assertRaisesRegex(ValueError, 'symlink'): self.run_verify()


class WindowsRecorderTests(unittest.TestCase):
	def setUp(self):
		self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
		base = pathlib.Path(self.temp.name)
		self.out, self.source = base / 'release', base / 'pc-result'
		self.out.mkdir(); self.source.mkdir()
		self.manifest = {'version': '0.2.0', 'commit': 'abcdef0123456', 'dirty': True, 'harbor_payload': {'source_digests': {'runtime': 'r', 'depth': 'd', 'days': [{'date': '2025-07-16', 'environment_digest': 'e', 'traffic_digest': 't'}]}}}
		self.smoke = {'platform': 'WindowsPlayer', 'build': '0.2.0 (abcdef0-dirty)', 'passed': True, 'failures': [], 'scene': 'Harbor', 'repository_fallback_available': False, 'environment_packaged': True, 'vessel_file_packaged': True, 'environment_digest': 'e', 'min_screenshot_colours': 100, 'harbor': {'tiles_ready': True, 'landing_stop_id': '89', 'traffic_enabled': True, 'traffic_active': 10, 'runtime_digest': 'r', 'depth_digest': 'd', 'date': '2025-07-16', 'traffic_digest': 't'}}
		for name in ('smoke-wheelhouse.png', 'smoke-chase.png'): (self.source / name).write_bytes(b'\x89PNG\r\n\x1a\nfixture')
		text = (ROOT / 'scripts/record-windows-smoke.sh').read_text()
		self.code = text.split("<<'PY'\n", 1)[1].rsplit('\nPY', 1)[0]

	def record(self, expected=0):
		(self.out / 'release-manifest.json').write_text(json.dumps(self.manifest)); (self.source / 'smoke.json').write_text(json.dumps(self.smoke))
		result = subprocess.run([sys.executable, '-I', '-', str(self.out), str(self.source)], input=self.code, text=True, capture_output=True)
		self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
		return result

	def test_actual_harbor_receipt_records(self):
		self.record(); self.assertTrue((self.out / 'windows-smoke/smoke.json').is_file())
	def test_missing_screenshot_cannot_pass(self):
		(self.source / 'smoke-chase.png').unlink(); self.record(1)
	def test_stale_traffic_cannot_pass(self):
		self.smoke['harbor']['traffic_digest'] = 'wrong'; self.record(1)
	def test_wrong_build_is_refused(self):
		self.smoke['build'] = 'other'; self.record(1)
	def test_failed_run_is_preserved_and_returns_failure(self):
		self.smoke['passed'] = False; self.smoke['failures'] = ['tiles failed']; self.record(1)
		self.assertTrue((self.out / 'windows-smoke/smoke.json').is_file())
	def test_source_parent_of_destination_is_refused(self):
		self.source = self.out; self.smoke['passed'] = False; self.record(1)


if __name__ == '__main__': unittest.main(verbosity=2)
