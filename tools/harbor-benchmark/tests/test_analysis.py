import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('analysis', Path(__file__).parents[1] / 'analyze.py')
a = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(a)


class AnalysisTests(unittest.TestCase):
	def test_one_bad_event_fails_despite_good_percentiles_and_a_control_stall(self):
		frames = [{'frame': i, 'wall_ms': 21.0 if i == 50 else 10.0, 'phase': 'drive'} for i in range(100)]
		result = a.streaming(frames, [{'frame': 50, 'kind': 'tile_created'}], stress=False)
		self.assertEqual(result['verdict'], 'fail')
		self.assertEqual(result['windows'][0]['frames'], [48, 49, 50, 51, 52])
		self.assertEqual(result['max_event_ms'], 21.0)

	def test_missing_timing_or_event_instrumentation_never_passes(self):
		self.assertEqual(a.streaming([], [], False)['verdict'], 'unverified')
		self.assertEqual(a.streaming([{'frame': 2, 'wall_ms': 10, 'phase': 'drive'}], [{'frame': 1, 'kind': 'tile_created'}], False)['verdict'], 'unverified')

	def test_preparation_is_reported_and_the_gate_starts_at_first_full_view_including_the_berth(self):
		frames=[{'frame':i,'wall_ms':1000 if i==3 else (21 if i==10 else 10),'phase':'ready'} for i in range(15)]
		events=[{'frame':1,'kind':'start_accepted'},{'frame':3,'kind':'tile_created'},
			{'frame':10,'kind':'view_ready','related_frame':9},{'frame':10,'kind':'tile_created'}]
		r=a.streaming(frames,events,False)
		self.assertEqual(r['verdict'],'fail')
		self.assertEqual(r['preparation_event_max_ms'],1000)
		self.assertEqual(r['max_event_ms'],21)

	def test_stress_never_claims_a_1x_pass(self):
		r = a.streaming([{'frame': i, 'wall_ms': 100, 'phase': 'drive'} for i in range(5)], [{'frame': 2, 'kind': 'tile_created'}], True)
		self.assertEqual(r['verdict'], 'unverified')
		self.assertEqual(r['stress_over_20ms'], 1)

	def test_known_over_budget_evidence_still_fails_when_another_source_is_missing(self):
		self.assertEqual(a.memory([], 2100000000)['verdict'], 'fail')
		frames=[{'frame': 50, 'wall_ms': 25, 'phase': 'drive'}]
		self.assertEqual(a.streaming(frames,[{'frame': 50, 'kind': 'tile_created'},{'frame': 49,'kind':'tile_created'}],False)['verdict'],'fail')

	def test_zeros_are_not_memory_evidence(self):
		self.assertEqual(a.memory([{'system_used_bytes': 0}], 500000000)['verdict'], 'unverified')
		self.assertEqual(a.memory([{'system_used_bytes': 1900000000}], 2000000000)['verdict'], 'fail')

	def test_required_artifacts_cannot_be_replaced_by_a_passing_claim(self):
		with tempfile.TemporaryDirectory() as folder:
			self.assertEqual(a.analyze(Path(folder))['overall'], 'unverified')


class RecordingArtifactTests(unittest.TestCase):
	def test_zero_step_replay_is_not_journey_evidence(self):
		with tempfile.TemporaryDirectory() as folder:
			r=a.recording_evidence(Path(folder), {'duration_s':0, 'replay_rows':1, 'recording':'C:/old/run.csv'})
			self.assertEqual(r['verdict'],'fail')

	def test_a_claimed_replay_with_missing_tapes_is_unverified(self):
		with tempfile.TemporaryDirectory() as folder:
			r=a.recording_evidence(Path(folder), {'duration_s':60, 'replay_rows':6001, 'recording':'C:/old/run.csv','replay_identical':True})
			self.assertEqual(r['verdict'],'unverified')
			self.assertIn('run.csv',str(r['missing']))


class SwitchMemoryTests(unittest.TestCase):
	def test_kernel_sample_is_joined_by_utc_and_stale_samples_stay_explicit(self):
		with tempfile.TemporaryDirectory() as temp:
			folder=Path(temp)
			(folder/'identity.json').write_text(json.dumps({'utc_zero_unix_s':1000}))
			(folder/'starts.json').write_text(json.dumps([{'sequence':1,'stop_id':'87','memory_first_view':{'wall_s':2,'system_used_bytes':20,'mono_used_bytes':4},'memory_after_10s':{'wall_s':12,'system_used_bytes':22}}]))
			(folder/'os-memory.csv').write_text('unix_s,rss_bytes\n1002.04,15\n1003,16\n')
			table=a.switch_memory_table(folder)
			self.assertEqual(table[0]['kernel_rss_bytes'],15)
			self.assertAlmostEqual(table[0]['kernel_sample_delta_s'],.04)
			self.assertIsNone(table[1]['kernel_rss_bytes'])


class JourneyVerdictTests(unittest.TestCase):
	def test_a_native_pass_does_not_hide_a_traffic_collision(self):
		self.assertEqual(a.journey_verdict({'functional_verdict':'pass','completed_journey':True,'traffic_collisions':1,'traffic':True})['verdict'],'fail')

	def test_an_incomplete_clean_run_is_not_a_passed_journey(self):
		self.assertEqual(a.journey_verdict({'functional_verdict':'pass','completed_journey':False})['verdict'],'unverified')


class StartReadinessTests(unittest.TestCase):
	def test_a_refused_face_does_not_become_a_fast_ready_start(self):
		r=a.start_readiness([{'accepted':True,'ready':True,'start_to_ready_s':1.2},{'accepted':False,'reason':'blocked by the crew question'}])
		self.assertEqual(r['verdict'],'unverified')
		self.assertEqual(r['accepted_verdict'],'pass')
		self.assertEqual(r['blocked_or_refused'],1)

	def test_one_slow_accepted_start_fails_the_gate(self):
		r=a.start_readiness([{'accepted':True,'ready':True,'start_to_ready_s':3.001}])
		self.assertEqual(r['verdict'],'fail')


class TimingSummaryTests(unittest.TestCase):
	def test_missing_gpu_timing_is_not_zero_cost_and_no_stall_is_removed(self):
		r=a.frame_cost([{'wall_ms':10,'gpu_ms':0},{'wall_ms':100,'gpu_ms':0}])
		self.assertEqual(r['wall_ms']['mean'],55)
		self.assertEqual(r['wall_ms']['max'],100)
		self.assertEqual(r['gpu_ms']['verdict'],'unverified')


class MissingJourneyFieldTests(unittest.TestCase):
	def test_missing_physics_fields_cannot_support_a_completed_claim(self):
		self.assertEqual(a.journey_verdict({'functional_verdict':'pass','completed_journey':True})['verdict'],'unverified')

	def test_unknown_traffic_monitor_is_not_invented_as_a_collision(self):
		r=a.journey_verdict({'functional_verdict':'pass','completed_journey':True,'traffic':True,'traffic_collisions':None})
		self.assertEqual(r['verdict'],'unverified')
		self.assertNotIn('unintended AIS traffic collision',r['failures'])


class UniqueEventFrameTests(unittest.TestCase):
	def test_multiple_events_on_one_slow_frame_are_one_violating_frame(self):
		frames=[{'frame':i,'wall_ms':25 if i==4 else 10,'phase':'drive'} for i in range(8)]
		events=[{'frame':4,'kind':'tile_created'},{'frame':4,'kind':'load_progress'}]
		r=a.streaming(frames,events,False)
		self.assertEqual(r['over_20ms'],1)
		self.assertEqual(r['over_20_event_records'],2)
		self.assertEqual(r['event_frame_count'],1)
		self.assertEqual(r['violating_frames'],[4])


class CameraMotionTests(unittest.TestCase):
	def test_a_camera_sweep_without_post_render_positions_is_unverified(self):
		with tempfile.TemporaryDirectory() as temp:
			result=a.camera_evidence(Path(temp),{})
			self.assertEqual(result['verdict'],'unverified')
			self.assertEqual(result['evidence_validity'],'invalid')

	def test_a_stationary_camera_cannot_claim_a_traversed_route(self):
		with tempfile.TemporaryDirectory() as temp:
			folder=Path(temp)
			(folder/'camera.csv').write_text('frame,wall_s,expected_x,expected_y,expected_z,actual_x,actual_y,actual_z\n1,0,0,0,0,0,0,0\n2,1,1000,0,0,0,0,0\n3,2,2000,0,0,0,0,0\n')
			result=a.camera_evidence(folder,{'camera_expected_distance_m':2000,'camera_route_verified':True})
			self.assertEqual(result['verdict'],'fail')
			self.assertEqual(result['observed_distance_m'],0)

	def test_every_rendered_camera_sample_matches_the_complete_requested_path(self):
		with tempfile.TemporaryDirectory() as temp:
			folder=Path(temp)
			(folder/'camera.csv').write_text('frame,wall_s,expected_x,expected_y,expected_z,actual_x,actual_y,actual_z\n1,0,0,0,0,0,0,0\n2,1,1000,0,0,1000,0,0\n3,2,2000,0,0,2000,0,0\n')
			result=a.camera_evidence(folder,{'camera_expected_distance_m':2000,'camera_route_verified':True})
			self.assertEqual(result['verdict'],'pass')
			self.assertEqual(result['observed_distance_m'],2000)
			self.assertEqual(result['max_error_m'],0)
