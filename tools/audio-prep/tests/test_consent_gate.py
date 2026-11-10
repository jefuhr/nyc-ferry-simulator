"""Consent preflight and lineage use only synthetic sheets and manifests."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

from test_audio_prep import prep, write_wave, MODULE


def consent_manifest(root, names):
	rows = []
	for i, name in enumerate(names):
		digest = hashlib.sha256((root / name).read_bytes()).hexdigest()
		rows.append(dict(path=name, sha256=digest, kind='audio',
			sources=[dict(source_id='synthetic-' + str(i), sha256=digest, contributor='owner',
				consent=dict(scope='processing', version='processing-v2'), withdrawn=False)],
			screening=dict(decision='approved', by='coordinator', date='2026-11-10')))
	return dict(schema_version=1, consent_policy='processing-v2', sheets=rows)


class ConsentGateTests(unittest.TestCase):
	def setUp(self):
		self.temp = tempfile.TemporaryDirectory()
		self.root = Path(self.temp.name)
		self.sheets = self.root / 'sheets'
		(self.sheets / 'audio').mkdir(parents=True)
		for name in ('private-first', 'private-second'):
			write_wave(self.sheets / 'audio' / (name + '.wav'), [.05] * 48000)
		self.consent = consent_manifest(self.sheets, ['audio/private-first.wav', 'audio/private-second.wav'])
		self.reference = self.sheets / 'manifest.json'
		self.reference.write_text(json.dumps(self.consent))
		self.recipes = self.root / 'recipes.json'
		self.recipes.write_text(json.dumps(dict(version=1, date='2026-11-10', clips=[dict(
			id=slot, source=dict(pattern='audio/' + name + '.wav', sha256=row['sha256']),
			source_rate=48000, sample_rate=22050, start_frame=0, end_frame=48000,
			loop=True, crossfade_frames=2205, fade_frames=0, target_lufs=-26)
			for slot, name, row in zip(('first', 'second'), ('private-first', 'private-second'), self.consent['sheets'])])))
		self.catalog = self.root / 'clips.json'
		self.catalog.write_text(json.dumps(dict(version=1, slots=[dict(id=id, file=id+'.wav', loop=True) for id in ('first', 'second')])))
		self.before = self.catalog.read_bytes()
		self.output = self.root / 'Clips'
		self.report = self.root / 'report.json'
		self.derived = self.root / 'derived.json'

	def tearDown(self):
		self.temp.cleanup()

	def build(self):
		return prep.build(self.sheets, self.recipes, self.catalog, self.output, self.report,
			derived_out=self.derived)

	def test_entire_batch_preflights_consent_before_reading_any_source_or_writing(self):
		for reason in ('unconsented', 'withdrawn', 'unscreened', 'missing-entry', 'missing-manifest', 'measurement-only', 'old-policy'):
			with self.subTest(reason=reason):
				data = json.loads(json.dumps(self.consent))
				row = data['sheets'][1]
				if reason == 'unconsented': row['sources'][0]['consent']['version'] = 'processing-v1'
				if reason == 'withdrawn': row['sources'][0]['withdrawn'] = True
				if reason == 'unscreened': row['screening']['decision'] = 'excluded'
				if reason == 'missing-entry': data['sheets'].pop()
				if reason == 'measurement-only': row['allowed_use'] = 'measurement-only'
				if reason == 'old-policy': data['consent_policy'] = 'processing-v1'
				self.reference.write_text(json.dumps(data))
				if reason == 'missing-manifest': self.reference.unlink()
				with mock.patch.object(prep, 'prepare') as prepare, mock.patch('hashlib.file_digest', side_effect=AssertionError('source read before consent preflight')):
					with self.assertRaises(ValueError) as error:
						self.build()
					prepare.assert_not_called()
				self.assertIn('first' if reason in ('missing-manifest', 'old-policy') else 'second', str(error.exception))
				self.assertNotIn('private-', str(error.exception))
				self.assertNotIn('synthetic-', str(error.exception))
				self.assertEqual(self.catalog.read_bytes(), self.before)
				for file in (self.output, self.report, self.derived): self.assertFalse(file.exists())

	def test_consented_batch_records_validated_derived_files_without_modifying_private_manifest(self):
		import math
		for name in ('private-first', 'private-second'):
			write_wave(self.sheets / 'audio' / (name + '.wav'), (.05 * math.sin(2*math.pi*440*i/48000) for i in range(48000)))
		self.consent = consent_manifest(self.sheets, ['audio/private-first.wav', 'audio/private-second.wav'])
		self.reference.write_text(json.dumps(self.consent))
		recipes = json.loads(self.recipes.read_text())
		for recipe, row in zip(recipes['clips'], self.consent['sheets']): recipe['source']['sha256'] = row['sha256']
		self.recipes.write_text(json.dumps(recipes))
		before = self.reference.read_bytes()
		self.build()
		self.assertEqual(self.reference.read_bytes(), before)
		entries = json.loads(self.derived.read_text())
		self.assertEqual(len(entries), 2)
		for entry, slot in zip(entries, ('first', 'second')):
			self.assertTrue(entry['path'].startswith('repo:'))
			self.assertEqual(entry['sha256'], hashlib.sha256((self.output / (slot+'.wav')).read_bytes()).hexdigest())
			self.assertEqual(entry['method'], 'audio-prep')
			self.assertTrue(entry['from'][0].startswith('synthetic-'))
		self.assertNotIn('synthetic-', self.catalog.read_text())
		self.assertEqual(json.loads(self.report.read_text())['consent_gate'], 'processing-v2; no legacy bypass')

	def test_no_manifest_switch_is_explicitly_refused_without_outputs(self):
		result = subprocess.run([sys.executable, str(MODULE), '--sheets', str(self.sheets),
			'--legacy-no-manifest', '--report', str(self.report), '--derived-out', str(self.derived)], capture_output=True, text=True)
		self.assertNotEqual(result.returncode, 0)
		self.assertIn('--legacy-no-manifest', result.stderr)
		self.assertFalse(self.report.exists())
		self.assertFalse(self.derived.exists())


if __name__ == '__main__': unittest.main()
