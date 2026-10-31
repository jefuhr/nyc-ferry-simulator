import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from analyze import frame_tail


class FrameTailTests(unittest.TestCase):
	def test_slow_frame_without_streaming_event_fails(self):
		frames = [{'frame': '7', 'phase': 'drive', 'wall_ms': '270'}]
		result = frame_tail(frames, [])
		self.assertEqual(result['verdict'], 'fail')
		self.assertEqual(result['violating_frames'], [7])
		self.assertEqual(result['non_event_over_20ms'], 1)

	def test_first_full_view_is_included_and_next_start_closes_gate(self):
		frames = [{'frame': str(i), 'phase': 'ready', 'wall_ms': '21'} for i in range(1, 7)]
		events = [{'frame': '1', 'kind': 'start_accepted'},
			{'frame': '4', 'related_frame': '3', 'kind': 'view_ready'},
			{'frame': '5', 'kind': 'start_accepted'}]
		result = frame_tail(frames, events)
		self.assertEqual(result['violating_frames'], [3, 4])

	def test_duplicate_events_count_once_and_missing_data_cannot_pass(self):
		frames = [{'frame': '7', 'phase': 'drive', 'wall_ms': '21'}]
		events = [{'frame': '7', 'kind': 'tile_created'}, {'frame': '7', 'kind': 'tile_enabled'}]
		self.assertEqual(frame_tail(frames, events)['event_over_20ms'], 1)
		self.assertEqual(frame_tail([], [])['verdict'], 'unverified')
		self.assertEqual(frame_tail([{'frame': '7', 'phase': 'drive', 'wall_ms': ''}], [])['verdict'], 'unverified')

	def test_replay_is_separate_and_stress_does_not_claim_one_x_pass(self):
		frames = [{'frame': '7', 'phase': 'drive', 'wall_ms': '20'},
			{'frame': '8', 'phase': 'replay', 'wall_ms': '1000'}]
		self.assertEqual(frame_tail(frames, [])['verdict'], 'pass')
		self.assertEqual(frame_tail(frames, [], stress=True)['verdict'], 'unverified')

	def test_nonfinite_and_duplicate_frame_intervals_are_unverified(self):
		for value in ['nan', 'inf', '-1']:
			self.assertEqual(frame_tail([{'frame': '7', 'phase': 'drive', 'wall_ms': value}], [])['verdict'], 'unverified')
		frame = {'frame': '7', 'phase': 'drive', 'wall_ms': '5'}
		self.assertEqual(frame_tail([frame, frame], [])['verdict'], 'unverified')


if __name__ == '__main__':
	unittest.main()
