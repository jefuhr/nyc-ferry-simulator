import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from profile_analysis import summarize


class ProfileAnalysisTests(unittest.TestCase):
	def fixture(self, directory):
		folder = Path(directory)
		(folder / 'profile.json').write_text(json.dumps({'schema': 'ferrysim-frame-profile/1',
			'development_build': True, 'overflow': False, 'sampled_frames': 1, 'sampling_method': 'continuous per-frame sums',
			'markers': [{'id': 0, 'name': 'GC.Collect', 'available': False, 'unit': None, 'bound_at_frame': None},
				{'id': 1, 'name': 'WaitForTargetFPS', 'available': True, 'unit': 'TimeNanoseconds', 'bound_at_frame': 1}]}))
		(folder / 'profile-frames.csv').write_text('frame,phase,wall_ms,main_thread_cpu_ms\n7,2,280,2\n')
		(folder / 'profile-markers.csv').write_text('frame,marker,value,count\n7,1,275000000,1\n')
		(folder / 'frames.csv').write_text('frame,phase,wall_ms,frame_timing_timestamp,gpu_ms\n7,drive,280,12345,8\n')
		(folder / 'events.csv').write_text('frame,kind\n8,frame_dropped\n')
		return folder

	def test_missing_marker_stays_unavailable_and_native_gpu_is_not_joined(self):
		with tempfile.TemporaryDirectory() as directory:
			result = summarize(self.fixture(directory))
			frame = result['slow_frames'][0]
			self.assertIsNone(frame['markers']['GC.Collect'])
			self.assertEqual(frame['markers']['WaitForTargetFPS']['milliseconds'], 275)
			self.assertEqual(frame['main_thread_cpu_ms'], 2)
			self.assertNotIn('gpu_ms', frame)
			self.assertIn('independent', result['gpu_timing_note'])

	def test_overflow_or_bad_join_prevents_complete_attribution(self):
		with tempfile.TemporaryDirectory() as directory:
			folder = self.fixture(directory)
			profile = json.loads((folder / 'profile.json').read_text())
			profile['overflow'] = True
			(folder / 'profile.json').write_text(json.dumps(profile))
			self.assertEqual(summarize(folder)['coverage'], 'incomplete')
			(folder / 'frames.csv').write_text('frame,phase,wall_ms\n7,drive,4\n')
			self.assertEqual(summarize(folder)['unmatched_profile_frames'], [7])

	def test_markers_bound_after_a_stall_cannot_exonerate_it(self):
		with tempfile.TemporaryDirectory() as directory:
			folder = self.fixture(directory)
			profile = json.loads((folder / 'profile.json').read_text())
			profile['markers'][1]['bound_at_frame'] = 8
			(folder / 'profile.json').write_text(json.dumps(profile))
			self.assertIsNone(summarize(folder)['slow_frames'][0]['markers']['WaitForTargetFPS'])

	def test_the_superseded_reset_collector_is_not_valid_attribution(self):
		with tempfile.TemporaryDirectory() as directory:
			folder = self.fixture(directory)
			profile = json.loads((folder / 'profile.json').read_text())
			profile.pop('sampling_method')
			(folder / 'profile.json').write_text(json.dumps(profile))
			self.assertEqual(summarize(folder)['coverage'], 'incomplete')

	def test_drop_callback_joins_the_preceding_completed_frame(self):
		with tempfile.TemporaryDirectory() as directory:
			result = summarize(self.fixture(directory))
			self.assertEqual(result['drop_frame_profiles'][0]['callback_frame'], 8)
			self.assertEqual(result['drop_frame_profiles'][0]['elapsed_frame'], 7)
			self.assertEqual(result['drop_frame_profiles'][0]['profile']['wall_ms'], 280)

	def test_native_cpu_timing_counters_are_not_attributed_to_the_unity_frame(self):
		with tempfile.TemporaryDirectory() as directory:
			folder = self.fixture(directory)
			profile = json.loads((folder / 'profile.json').read_text())
			profile['markers'].append({'id': 2, 'name': 'CPU Render Thread Frame Time',
				'available': True, 'unit': 'TimeNanoseconds', 'bound_at_frame': 1})
			(folder / 'profile.json').write_text(json.dumps(profile))
			with (folder / 'profile-markers.csv').open('a') as stream:
				stream.write('7,2,1000000,1\n')
			frame = summarize(folder)['slow_frames'][0]
			self.assertNotIn('CPU Render Thread Frame Time', frame['markers'])
			self.assertEqual(frame['unjoined_native_counters']['CPU Render Thread Frame Time']['milliseconds'], 1)


if __name__ == '__main__':
	unittest.main()
