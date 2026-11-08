"""Failure modes: missing provenance, invented precision, blocked bow, drift and over-budget geometry."""
import importlib.util
import json
import math
import tempfile
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]

class InteriorTests(unittest.TestCase):
	def module(self):
		spec = importlib.util.spec_from_file_location('interior', ROOT / 'art/blender/interior.py')
		module = importlib.util.module_from_spec(spec)
		spec.loader.exec_module(module)
		return module

	def test_layouts_have_provenance_and_distinct_footprints(self):
		m = self.module()
		layouts = [m.load_layout(ROOT / f'art/blender/data/{c}-wheelhouse.json') for c in ('k-class', 't-class')]
		self.assertNotEqual(layouts[0]['footprint'], layouts[1]['footprint'])
		for c in ('k-class', 't-class'):
			doc = json.loads((ROOT / f'art/blender/data/{c}-wheelhouse.json').read_text())
			self.assertEqual(doc['subject'], c)
			self.assertEqual(doc['kind'], 'wheelhouse')
			for key, v in doc['values'].items():
				self.assertTrue(v['source'], key)
				self.assertIn(v['confidence'], ('low', 'medium', 'high'))
				if c == 't-class' and v['source'].startswith('wheelhouse/'):
					self.assertEqual((v['method'], v['confidence']), ('photo-estimate', 'low'))

	def test_geometry_is_deterministic_bounded_and_mounts_are_separate(self):
		m = self.module()
		for c in ('k-class', 't-class'):
			layout = m.load_layout(ROOT / f'art/blender/data/{c}-wheelhouse.json')
			a, b = m.scene_spec(layout), m.scene_spec(layout)
			self.assertEqual(a, b)
			self.assertLessEqual(sum(len(p['triangles']) for p in a['meshes']), 60000)
			self.assertGreater(sum(len(p['triangles']) for p in a['meshes']), 1000)
			for name in ('Eye', 'EyeStanding', 'PortLever', 'StarboardLever', 'HelmFFU', 'PortNFU', 'StarboardNFU', 'RadarScreen', 'PlotterScreen'):
				self.assertIn(name, a['mounts'])
			for part in a['meshes']:
				for vertex in part['vertices']:
					self.assertTrue(all(math.isfinite(n) for n in vertex))
			self.assertTrue(any(p['material'] == 'RadarScreen' for p in a['meshes']))
			self.assertTrue(any(p['material'] == 'PlotterScreen' for p in a['meshes']))

	def test_seated_and_standing_bow_rays_clear_every_mesh(self):
		m = self.module()
		for c in ('k-class', 't-class'):
			layout = m.load_layout(ROOT / f'art/blender/data/{c}-wheelhouse.json')
			scene = m.scene_spec(layout)
			vessel = json.loads((ROOT / f'vessels/{c}.json').read_text())['particulars']
			# Include the actual automatic-bow camera target at 45% LOA as well as the stem.
			for fraction in (.45, .5):
				bow = (0, vessel['depth']['value'] - vessel['draft']['value'], vessel['lengthOverall']['value'] * fraction)
				for eye in ('Eye', 'EyeStanding'):
					self.assertEqual(m.occluders(scene, scene['mounts'][eye], bow), [], f'{c}/{eye}/bow-{fraction}')

	def test_hull_datums_override_snapshot_and_move_eye_with_enclosure(self):
		m = self.module()
		with tempfile.TemporaryDirectory() as folder:
			hull = Path(folder) / 'k-class-hull.json'
			values = {'upper_deck_height': 5.2, 'wheelhouse_roof_height': 7.8,
				'wheelhouse_fore_z': 9.1, 'wheelhouse_aft_z': 4.5, 'wheelhouse_width': 2.9}
			hull.write_text(json.dumps({'schema_version': 1, 'kind': 'hull', 'subject': 'k-class',
				'values': {k: {'value': v} for k,v in values.items()}}))
			d = m.load_layout(ROOT / 'art/blender/data/k-class-wheelhouse.json', hull_path=hull)
			self.assertEqual(d['floor_origin'], [0, 5.2, 6.8])
			for actual, expected in zip(d['footprint'], [2.9, 2.6, 4.6]): self.assertAlmostEqual(actual, expected)
			scene = m.scene_spec(d)
			self.assertAlmostEqual(scene['mounts']['Eye'][1], 7.0)
			self.assertAlmostEqual(scene['mounts']['Eye'][2], 8.4)

	def test_reference_import_datum_snapshots_are_retained_until_hull_sync(self):
		m = self.module()
		for cls, floor, front, aft, width in [('k-class',4.9,8.591858,4.63646,2.55), ('t-class',4.639,6.781395,2.210233,2.6)]:
			d = m.load_layout(ROOT / f'art/blender/data/{cls}-wheelhouse.json')
			self.assertEqual(d['floor_origin'][1], floor)
			self.assertAlmostEqual(d['floor_origin'][2] + d['footprint'][2]/2, front)
			self.assertAlmostEqual(d['floor_origin'][2] - d['footprint'][2]/2, aft)
			self.assertEqual(d['footprint'][0], width)

	def test_bow_occlusion_checker_detects_both_triangle_windings(self):
		m = self.module()
		for tri in ([0,1,2], [2,1,0]):
			scene = {'mounts': {}, 'meshes': [{'name': 'PositiveControl', 'parent': None,
				'vertices': [[-1,-1,1],[1,-1,1],[0,1,1]], 'triangles': [tri]}]}
			self.assertEqual(m.occluders(scene, [0,0,0], [0,0,2]), ['PositiveControl'])
			self.assertEqual(m.occluders(scene, [3,0,0], [3,0,2]), [])

	def test_neutral_control_grips_are_ahead_of_eye_and_inside_downward_view(self):
		m = self.module()
		for cls in ('k-class','t-class'):
			scene = m.scene_spec(m.load_layout(ROOT / f'art/blender/data/{cls}-wheelhouse.json'))
			for eye_name in ('Eye', 'EyeStanding'):
				eye = scene['mounts'][eye_name]
				for part in scene['meshes']:
					if part['parent'] not in ('HelmFFU','PortNFU','StarboardNFU','PortLever','StarboardLever') or part['material'] != 'Seat': continue
					for local in part['vertices']:
						grip = m.add(local, scene['mounts'][part['parent']])
						self.assertGreater(grip[2] - eye[2], .10, f'{cls}/{eye_name}/{part["parent"]} is behind the helm eye')
						down = math.degrees(math.atan2(eye[1]-grip[1], math.hypot(grip[0]-eye[0],grip[2]-eye[2])))
						self.assertLess(down, 80, 'Fits 8-degree mount tilt + 45-degree head pitch + half 65-degree FOV, with margin')

	def test_invalid_layout_fails_closed(self):
		m = self.module()
		with self.assertRaises(ValueError):
			m.validate_layout({'footprint': [0, 2, 3]})

if __name__ == '__main__': unittest.main()
