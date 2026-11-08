import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from analyze import analyze, detect_machine_stalls, frame_tail, split_machine_stalls


def frame(number, seconds, milliseconds=25, phase='drive'):
	return {'frame': str(number), 'wall_s': str(seconds), 'wall_ms': str(milliseconds), 'phase': phase}


def identity(**changes):
	return dict({'platform': 'OSXPlayer', 'os': 'fixture OS', 'cpu': 'fixture CPU', 'gpu': 'fixture GPU',
		'utc_zero_unix_s': 1800000000}, **changes)


def control(frames, **changes):
	return dict({'source': 'control-none-1', 'frames': frames, 'events': [], 'identity': identity(),
		'scenario': {'platform': 'OSXPlayer', 'render_mode': 'none', 'traffic': False}}, **changes)


class MachineStallTests(unittest.TestCase):
	def test_seconds_are_learned_from_distinct_control_minutes_not_known_clock_values(self):
		model = detect_machine_stalls([control([frame(1, 19.1), frame(2, 19.2), frame(3, 79.7), frame(4, 43.1)])])
		self.assertEqual(model['hosts'][0]['seconds'], [19])
		self.assertEqual(model['hosts'][0]['evidence'][0]['minute_count'], 2)
		self.assertEqual(model['hosts'][0]['evidence'][0]['frames'], [
			{'source': 'control-none-1', 'frame': 1}, {'source': 'control-none-1', 'frame': 2},
			{'source': 'control-none-1', 'frame': 3}])

	def test_one_burst_duplicates_and_unrelated_control_cannot_establish_recurrence(self):
		burst = control([frame(1, 11.1), frame(2, 11.2)])
		self.assertEqual(detect_machine_stalls([burst, burst])['hosts'][0]['seconds'], [])
		for scenario in ({'platform': 'OSXPlayer', 'render_mode': 'both', 'traffic': False},
			{'platform': 'OSXPlayer', 'render_mode': 'none', 'traffic': True}):
			self.assertEqual(detect_machine_stalls([control([frame(1, 11), frame(2, 71)], scenario=scenario)])['hosts'], [])

	def test_diffuse_control_noise_does_not_label_every_second_a_periodic_stall(self):
		frames = [frame(i + 1, i) for i in range(120)]
		self.assertEqual(detect_machine_stalls([control(frames)])['hosts'][0]['seconds'], [])

	def test_split_is_reporting_only_with_exact_seconds_and_no_padding(self):
		model = detect_machine_stalls([control([frame(1, 19.1), frame(2, 79.7)])])
		frames = [frame(10, 139.2), frame(11, 140.0), frame(12, 138.99), frame(13, 159, 20)]
		tail = frame_tail(frames, [])
		original = copy.deepcopy(tail)
		result = split_machine_stalls(frames, tail, identity(), model)
		self.assertEqual(result['machine_stall_frames'], [10])
		self.assertEqual(result['other_frames'], [11, 12])
		self.assertEqual(result['machine_stall_over_20ms'] + result['other_over_20ms'], tail['over_20ms'])
		self.assertEqual(tail, original)
		self.assertEqual(tail['verdict'], 'fail')
		self.assertFalse(result['causal_attribution_proven'])

	def test_readiness_replay_and_invalid_samples_never_train_the_model(self):
		frames = [frame(1, 19), frame(2, 79), frame(3, 139, phase='replay'), frame(4, 199, milliseconds='nan')]
		events = [{'frame': '1', 'kind': 'start_accepted'}, {'frame': '3', 'related_frame': '2', 'kind': 'view_ready'}]
		self.assertEqual(detect_machine_stalls([control(frames, events=events)])['hosts'][0]['seconds'], [])

	def test_missing_or_nonfinite_utc_and_frame_timestamps_stay_other(self):
		model = detect_machine_stalls([control([frame(1, 19), frame(2, 79)])])
		for stamp in [None, 'nan', 'inf', 'bad']:
			frames = [frame(10, stamp)]
			result = split_machine_stalls(frames, frame_tail(frames, []), identity(), model)
			self.assertEqual(result['machine_stall_over_20ms'], 0)
			self.assertEqual(result['other_over_20ms'], 1)
			self.assertEqual(result['unclassified_timestamp_frames'], [10])
			bad_identity = identity(utc_zero_unix_s=stamp)
			self.assertEqual(detect_machine_stalls([control([frame(1, 19), frame(2, 79)], identity=bad_identity)])['hosts'], [])

	def test_missing_control_other_host_and_windows_do_not_borrow_seconds(self):
		model = detect_machine_stalls([control([frame(1, 19), frame(2, 79)])])
		frames = [frame(10, 139)]
		for host, evidence in [(identity(), None), (identity(cpu='other CPU'), model),
			(identity(platform='WindowsPlayer'), model)]:
			result = split_machine_stalls(frames, frame_tail(frames, []), host, evidence)
			self.assertEqual(result['status'], 'unverified')
			self.assertEqual(result['other_frames'], [10])

	def test_read_only_cli_does_not_rewrite_source_evidence(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp)
			source = root/'session'
			source.mkdir()
			(source/'scenario.json').write_text('{}')
			(source/'verdict.json').write_text('original verdict bytes\n')
			before = {p.name: p.read_bytes() for p in source.iterdir()}
			output = root/'analysis.json'
			run = subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1]/'analyze.py'),
				str(source), '--read-only', '--output', str(output)], capture_output=True, text=True)
			self.assertEqual(run.returncode, 0, run.stderr)
			self.assertEqual({p.name: p.read_bytes() for p in source.iterdir()}, before)
			self.assertEqual(json.loads(output.read_text())['.']['overall'], 'unverified')

	def test_full_analysis_keeps_a_correlated_frame_in_the_failed_gate(self):
		with tempfile.TemporaryDirectory() as tmp:
			folder = Path(tmp)
			(folder/'frames.csv').write_text('frame,wall_s,wall_ms,phase,width,height\n10,139,25,drive,1920,1080\n')
			(folder/'events.csv').write_text('frame,kind\n')
			(folder/'memory.csv').write_text('system_used_bytes,phase\n10000000,drive\n')
			(folder/'scenario.json').write_text(json.dumps({'platform': 'OSXPlayer', 'route': 'starts'}))
			(folder/'identity.json').write_text(json.dumps(identity()))
			(folder/'process.json').write_text(json.dumps({'peak_rss_bytes': 10000000}))
			model = detect_machine_stalls([control([frame(1, 19), frame(2, 79)])])
			result = analyze(folder, model, write_tables=False)
			self.assertEqual(result['frame_tail_classification']['machine_stall_frames'], [10])
			self.assertEqual(result['frame_tail']['violating_frames'], [10])
			self.assertEqual(result['frame_tail']['verdict'], 'fail')
			self.assertEqual(result['overall'], 'fail')

	def test_read_only_cli_refuses_an_output_inside_the_source(self):
		with tempfile.TemporaryDirectory() as tmp:
			folder = Path(tmp)
			sentinel = folder/'verdict.json'
			sentinel.write_text('original verdict bytes\n')
			run = subprocess.run([sys.executable, str(Path(__file__).resolve().parents[1]/'analyze.py'),
				str(folder), '--read-only', '--output', str(sentinel)], capture_output=True, text=True)
			self.assertNotEqual(run.returncode, 0)
			self.assertIn('outside the source', run.stderr)
			self.assertEqual(sentinel.read_text(), 'original verdict bytes\n')


if __name__ == '__main__':
	unittest.main()
