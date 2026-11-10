"""Receipts must describe actual files, all four LODs and their source/material contract."""
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / 'verify_exports.py'


class ExportVerificationTests(unittest.TestCase):
	def setUp(self):
		self.temp = tempfile.TemporaryDirectory()
		self.addCleanup(self.temp.cleanup)
		self.paths = [Path(self.temp.name) / n for n in ('first','second')]
		for path in self.paths:
			path.mkdir()
			models = {}
			for name in ('t-class','k-class'):
				(path/name).mkdir()
				for lod in (0,1):
					file = path/name/f'LOD{lod}.fbx'
					file.write_bytes(b'fixture-fbx')
					models[f'{name}/LOD{lod}'] = dict(geometry_sha256='same',triangles=60000 if lod==0 else 4000,
						vertices=10,dimensions={},mounts={},bounds_blender=[],layout_sha256='same',materials=[],
						fbx_sha256=hashlib.sha256(file.read_bytes()).hexdigest())
			(path/'manifest.json').write_text(json.dumps(dict(schema='ferrysim-models/2',models=models)))
			(path/'NeutralPalette.png').write_bytes(b'palette')
			(path/'audited-classes.json').write_text('{}')

	def verify(self):
		return subprocess.run([sys.executable,str(SCRIPT),*[str(p) for p in self.paths]],capture_output=True,text=True)

	def edit(self, callback, path=None):
		path = path or self.paths[1]
		manifest=json.loads((path/'manifest.json').read_text());callback(manifest)
		(path/'manifest.json').write_text(json.dumps(manifest))

	def test_accepts_new_lod0_budget(self):
		result=self.verify()
		self.assertEqual(result.returncode,0,result.stderr)

	def test_tampered_actual_fbx_is_rejected(self):
		for path in self.paths:
			self.edit(lambda m: [v.update(triangles=4000) for v in m['models'].values()],path)
		(self.paths[1]/'t-class/LOD0.fbx').write_bytes(b'tampered')
		self.assertNotEqual(self.verify().returncode,0)

	def test_missing_pair_layout_material_drift_and_budget_are_rejected(self):
		original=(self.paths[1]/'manifest.json').read_text()
		for mutate in (
			lambda m:m['models'].pop('k-class/LOD1'),
			lambda m:m['models']['t-class/LOD0'].update(layout_sha256='other'),
			lambda m:m['models']['t-class/LOD0'].update(materials=[{'name':'BrandedPaint'}]),
			lambda m:m['models']['t-class/LOD1'].update(triangles=5001),
		):
			(self.paths[1]/'manifest.json').write_text(original)
			self.edit(mutate)
			self.assertNotEqual(self.verify().returncode,0)


class InteriorExportVerificationTests(ExportVerificationTests):
	def setUp(self):
		super().setUp()
		for path in self.paths:
			models = {}
			for cls in ('t-class', 'k-class'):
				file = path / cls / 'Interior.fbx'; file.write_bytes(b'interior-fixture')
				models[cls + '/Interior'] = dict(triangles=6000, vertices=4000, renderers=75,
					geometry_sha256='same', layout_sha256='same', mounts={}, rotations={}, controls=[], materials=['LampsSurface'],
					fbx_sha256=hashlib.sha256(file.read_bytes()).hexdigest())
			(path / 'interior-manifest.json').write_text(json.dumps(dict(schema='ferrysim-interiors/2', models=models)))

	def verify(self):
		return subprocess.run([sys.executable, str(SCRIPT), '--interiors', *[str(p) for p in self.paths]], capture_output=True, text=True)

	# The exterior cases remain unedited above; these independently exercise the additive mode.
	def test_accepts_new_lod0_budget(self):
		result = self.verify()
		self.assertEqual(result.returncode, 0, result.stderr)

	def test_tampered_actual_fbx_is_rejected(self):
		(self.paths[1] / 't-class/Interior.fbx').write_bytes(b'tampered')
		self.assertNotEqual(self.verify().returncode, 0)

	def test_missing_pair_layout_material_drift_and_budget_are_rejected(self):
		file = self.paths[1] / 'interior-manifest.json'; original = file.read_text()
		for mutation in (
			lambda m: m['models'].pop('k-class/Interior'),
			lambda m: m['models']['t-class/Interior'].update(layout_sha256='changed'),
			lambda m: m['models']['t-class/Interior'].update(materials=[]),
			lambda m: m['models']['t-class/Interior'].update(triangles=150001),
			lambda m: m['models']['t-class/Interior'].update(rotations={'RadarScreen':[0,0,0]}),
		):
			doc = json.loads(original); mutation(doc); file.write_text(json.dumps(doc))
			self.assertNotEqual(self.verify().returncode, 0)

	def test_container_byte_drift_is_rejected_even_with_matching_receipts(self):
		file = self.paths[1] / 't-class/Interior.fbx'; file.write_bytes(b'timestamp-drift')
		receipt = self.paths[1] / 'interior-manifest.json'; doc = json.loads(receipt.read_text())
		doc['models']['t-class/Interior']['fbx_sha256'] = hashlib.sha256(file.read_bytes()).hexdigest()
		receipt.write_text(json.dumps(doc))
		self.assertNotEqual(self.verify().returncode, 0)


if __name__ == '__main__':
	unittest.main()
