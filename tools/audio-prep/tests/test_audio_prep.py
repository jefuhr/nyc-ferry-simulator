"""Synthetic regressions: no private recordings are needed to run these tests."""
import array
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import random
import subprocess
import sys
import tempfile
import unittest
import wave

MODULE = Path(__file__).resolve().parents[1] / 'audio_prep.py'
spec = importlib.util.spec_from_file_location('audio_prep', MODULE)
prep = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prep)


def write_wave(path, samples, rate=48000, channels=1):
	pcm = array.array('h', (round(max(-1, min(1, s)) * 32767) for s in samples))
	if sys.byteorder != 'little':
		pcm.byteswap()
	with wave.open(str(path), 'wb') as stream:
		stream.setparams((channels, 2, rate, 0, 'NONE', 'not compressed'))
		stream.writeframes(pcm.tobytes())


def read_wave(path):
	with wave.open(str(path), 'rb') as stream:
		info = (stream.getframerate(), stream.getnchannels(), stream.getnframes())
		samples = array.array('h', stream.readframes(stream.getnframes()))
	if sys.byteorder != 'little':
		samples.byteswap()
	return info, [x / 32768 for x in samples]


class AudioPrepTests(unittest.TestCase):
	def setUp(self):
		self.temp = tempfile.TemporaryDirectory()
		self.root = Path(self.temp.name)
		self.source = self.root / 'source.wav'
		n = 48000 * 3
		write_wave(self.source, (.08 * math.sin(2 * math.pi * 173.7 * i / 48000) for i in range(n)))
		self.recipe = dict(id='fixture', source_rate=48000, start_frame=2400, end_frame=132000,
			sample_rate=22050, target_lufs=-26, loop=True, crossfade_frames=2205, fade_frames=0,
			highpass_hz=0, lowpass_hz=0)

	def tearDown(self):
		self.temp.cleanup()

	def test_tone_loop_has_sample_exact_duration_low_seam_and_target_loudness(self):
		out = self.root / 'tone.wav'
		report = prep.prepare(self.source, self.recipe, out)
		info, samples = read_wave(out)
		self.assertEqual(info, (22050, 1, 59535 - 2205))
		self.assertLess(abs(samples[0] - samples[-1]), .002)
		self.assertAlmostEqual(prep.measure_lufs(out), -26, delta=.5)
		self.assertEqual(report['loop_start_sample'], 0)
		self.assertEqual(report['loop_end_sample'], info[2])

	def test_seeded_noise_loop_is_seamless_and_normalized(self):
		rng = random.Random(1847)
		write_wave(self.source, (rng.uniform(-.18, .18) for _ in range(144000)))
		out = self.root / 'noise.wav'
		prep.prepare(self.source, self.recipe, out)
		_, samples = read_wave(out)
		self.assertLess(abs(samples[0] - samples[-1]), .002)
		self.assertAlmostEqual(prep.measure_lufs(out), -26, delta=.5)

	def test_rerun_is_byte_identical_including_report(self):
		a, b = self.root / 'a.wav', self.root / 'b.wav'
		self.assertEqual(prep.prepare(self.source, self.recipe, a), prep.prepare(self.source, self.recipe, b))
		self.assertEqual(a.read_bytes(), b.read_bytes())

	def test_stereo_downmix_and_input_frame_trim(self):
		# Different channels and loud material outside the crop detect channel/frame confusion.
		values = []
		for i in range(144000):
			value = .04 * math.sin(2 * math.pi * 440 * i / 48000) if 24000 <= i < 120000 else .8
			values.extend((value, value * .5))
		write_wave(self.source, values, channels=2)
		r = dict(self.recipe, start_frame=24000, end_frame=120000, loop=False, crossfade_frames=0, fade_frames=441)
		out = self.root / 'stereo.wav'
		prep.prepare(self.source, r, out)
		info, samples = read_wave(out)
		self.assertEqual(info, (22050, 1, 44100))
		self.assertEqual((samples[0], samples[-1]), (0, 0))
		self.assertAlmostEqual(prep.measure_lufs(out), -26, delta=.5)

	def test_equal_power_overlap_does_not_silence_the_join(self):
		# Uncorrelated noise should retain roughly the same RMS through the overlap.
		rng = random.Random(33)
		values = array.array('f', (rng.uniform(-.2, .2) for _ in range(60000)))
		loop, _ = prep.make_loop(values, 10000)
		energy = sum(x*x for x in loop) / len(loop)
		self.assertAlmostEqual(energy, sum(x*x for x in values) / len(values), delta=.001)

	def test_invalid_trim_crossfade_and_rate_rejected_without_output(self):
		for overrides in [dict(start_frame=-1), dict(end_frame=999999), dict(source_rate=44100),
			dict(crossfade_frames=90000), dict(crossfade_frames=0), dict(sample_rate=0),
			dict(target_lufs=float('nan')), dict(loop=False, crossfade_frames=0, fade_frames=0)]:
			with self.subTest(overrides=overrides):
				out = self.root / 'invalid.wav'
				with self.assertRaises(ValueError):
					prep.prepare(self.source, dict(self.recipe, **overrides), out)
				self.assertFalse(out.exists())

	def test_silence_is_rejected_instead_of_being_claimed_normalized(self):
		write_wave(self.source, [0] * 144000)
		with self.assertRaises(ValueError):
			prep.prepare(self.source, self.recipe, self.root / 'silence.wav')

	def test_peak_overflow_is_rejected_without_clipping(self):
		with self.assertRaises(ValueError):
			prep.prepare(self.source, dict(self.recipe, target_lufs=0), self.root / 'hot.wav')

	def test_source_pattern_requires_unique_content_verified_local_file(self):
		audio = self.root / 'audio'
		audio.mkdir()
		file = audio / 'anonymous.wav'
		file.write_bytes(self.source.read_bytes())
		source = dict(pattern='audio/*.wav', sha256=hashlib.sha256(file.read_bytes()).hexdigest())
		self.assertEqual(prep.resolve_source(self.root, source), file.resolve())
		with self.assertRaises(ValueError):
			prep.resolve_source(self.root, dict(source, sha256='0' * 64))
		(audio / 'second.wav').write_bytes(file.read_bytes())
		with self.assertRaises(ValueError):
			prep.resolve_source(self.root, source)
		for pattern in ['../*.wav', '/tmp/*.wav', 'audio/../../*.wav']:
			with self.assertRaises(ValueError):
				prep.resolve_source(self.root, dict(source, pattern=pattern))

	def test_symlink_outside_sheets_is_refused(self):
		root = self.root / 'sheets'
		(root / 'audio').mkdir(parents=True)
		(root / 'audio' / 'escape.wav').symlink_to(self.source)
		with self.assertRaises(ValueError):
			prep.resolve_source(root, dict(pattern='audio/*.wav', sha256=hashlib.sha256(self.source.read_bytes()).hexdigest()))

	def test_cli_updates_provenance_and_preserves_placeholder(self):
		(root_audio := self.root / 'audio').mkdir()
		(root_audio / 'anonymous.wav').write_bytes(self.source.read_bytes())
		manifest = self.root / 'clips.json'
		placeholder = dict(id='unavailable', file='unavailable.wav', source='placeholder', edits='synthesis')
		manifest.write_text(json.dumps(dict(version=1, slots=[dict(id='fixture', file='fixture.wav', loop=True), placeholder])))
		recipes = self.root / 'recipes.json'
		recipe = dict(self.recipe, source=dict(pattern='audio/*.wav', sha256=hashlib.sha256(self.source.read_bytes()).hexdigest()))
		recipes.write_text(json.dumps(dict(version=1, date='2026-11-08', clips=[recipe])))
		command = [sys.executable, str(MODULE), '--sheets', str(self.root), '--recipes', str(recipes), '--manifest', str(manifest), '--output', str(self.root/'Clips'), '--report', str(self.root/'report.json')]
		result = subprocess.run(command, text=True, capture_output=True)
		self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
		updated = json.loads(manifest.read_text())['slots']
		self.assertEqual(updated[1], placeholder)
		self.assertEqual(updated[0]['source'], 'crew recording (private), 2026-11')
		self.assertGreater(updated[0]['loopEndSample'], 0)
		self.assertEqual(updated[0]['date'], '2026-11-08')
		self.assertNotIn('anonymous', manifest.read_text())


if __name__ == '__main__':
	unittest.main()
