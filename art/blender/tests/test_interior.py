"""Failure modes: missing provenance, invented precision, blocked bow, drift and over-budget geometry."""
import importlib.util
import copy
import hashlib
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

	def test_v2_layout_and_nested_hull_groups_load(self):
		m = self.module()
		for cls in ('k-class', 't-class'):
			path = ROOT / f'art/blender/data/{cls}-wheelhouse.json'
			doc = json.loads(path.read_text())
			self.assertEqual(doc['schema_version'], 2)
			d = m.load_layout(path)
			self.assertEqual(len(d['screens']), 4)
			self.assertGreater(len(d['controls']), 40)
			self.assertEqual(d['upper_deck_height'], doc['values']['upper_deck_height']['value'])
			for item in doc['screens'] + doc['controls']:
				for value in item.values():
					if isinstance(value, dict):
						self.assertTrue(all(k in value for k in ('value', 'unit', 'source', 'method', 'confidence')))

	def test_control_outside_footprint_and_duplicate_lamp_fail_closed(self):
		m = self.module()
		d = m.load_layout(ROOT / 'art/blender/data/t-class-wheelhouse.json')
		for bad in ([10, 1, -.3], [0, -1, -.3], [0, 1, 2]):
			changed = copy.deepcopy(d)
			changed['controls'][0]['pivot'] = bad
			with self.assertRaisesRegex(ValueError, 'pivot'):
				m.validate_layout(changed)
		lamps = [c for c in d['controls'] if c['kind'] == 'lamp']
		lamps[1]['lamp'] = lamps[0]['lamp']
		with self.assertRaisesRegex(ValueError, 'lamp'):
			m.validate_layout(d)

	def test_positive_lever_travel_moves_actual_grip_forward(self):
		m = self.module()
		d = m.load_layout(ROOT / 'art/blender/data/t-class-wheelhouse.json')
		scene = m.scene_spec(d)
		for name in ('PortLever', 'StarboardLever'):
			control = next(c for c in d['controls'] if c['id'] == name)
			grip = next(p for p in scene['meshes'] if p['parent'] == name and p['material'] == 'Seat')
			centre = [sum(v[i] for v in grip['vertices']) / len(grip['vertices']) for i in range(3)]
			self.assertGreater(m.rotate(centre, control['axis'], control['travel'][1])[2], centre[2])
			self.assertEqual(scene['rotations'].get(name, [0, 0, 0]), [0, 0, 0])

	def test_lamps_share_mesh_and_every_vertex_has_unique_bank_index(self):
		m = self.module()
		for cls in ('t-class', 'k-class'):
			d = m.load_layout(ROOT / f'art/blender/data/{cls}-wheelhouse.json')
			scene = m.scene_spec(d)
			lamps = [c for c in d['controls'] if c['kind'] == 'lamp']
			indices = [c['lamp'] for c in lamps]
			self.assertEqual(len(indices), len(set(indices)))
			bank = [p for p in scene['meshes'] if p['material'] == 'LampsSurface']
			self.assertEqual(len(bank), 1)
			self.assertEqual(bank[0]['name'], 'LampsSurface')
			self.assertEqual(len(bank[0]['uv1']), len(bank[0]['vertices']))
			self.assertEqual({uv[0] for uv in bank[0]['uv1']}, set(indices))
			for tri in bank[0]['triangles']:
				self.assertEqual(len({bank[0]['uv1'][i][0] for i in tri}), 1)
			self.assertFalse(any(p['parent'] in {c['id'] for c in lamps} for p in scene['meshes']))

	def test_screen_yaw_sign_and_focus_visibility(self):
		m = self.module()
		# Positive yaw turns the outward (-z) normal toward port (-x).
		self.assertLess(m.rotate([0, 0, -1], [0, 1, 0], 30)[0], 0)
		for cls in ('t-class', 'k-class'):
			d = m.load_layout(ROOT / f'art/blender/data/{cls}-wheelhouse.json')
			scene = m.scene_spec(d)
			for screen in d['screens']:
				name = screen['id']; centre = scene['mounts'][name]
				normal = m.screen_vector([0, 0, -1], screen)
				to_eye = m.sub(scene['mounts']['Eye'], centre)
				self.assertGreaterEqual(m.dot(normal, to_eye) / math.sqrt(m.dot(to_eye, to_eye)), math.cos(math.radians(45)), name)
				focus = scene['mounts'][name + 'FocusEye']
				delta = m.sub(focus, centre); distance = math.sqrt(m.dot(delta, delta))
				self.assertGreaterEqual(distance, .45)
				self.assertGreater(m.dot(normal, delta) / distance, .99999)
				# Check the whole lit rectangle, not just the centre hidden behind a throttle.
				for x in (-.48, -.32, -.16, 0, .16, .32, .48):
					for y in (-.48, -.32, -.16, 0, .16, .32, .48):
						target = m.add(centre, m.screen_vector([screen['size'][0]*x, screen['size'][1]*y, -.003], screen))
						self.assertEqual(m.occluders(scene, focus, target), [], f'{cls}/{name}/{x}/{y}')

	def test_two_build_geometry_hashes_match(self):
		m = self.module()
		for cls in ('t-class', 'k-class'):
			d = m.load_layout(ROOT / f'art/blender/data/{cls}-wheelhouse.json')
			hashes = [hashlib.sha256(json.dumps(m.scene_spec(d), sort_keys=True).encode()).hexdigest() for _ in range(2)]
			self.assertEqual(hashes[0], hashes[1])

	def test_blender_export_has_uv1_and_byte_identical_repeat(self):
		try:
			import bpy
		except ImportError:
			self.skipTest('Run inside Blender under heavy.lock')
		m = self.module()
		with tempfile.TemporaryDirectory() as folder:
			outputs = []
			for index in range(2):
				bpy.ops.wm.read_factory_settings(use_empty=True)
				scene = m.scene_spec(m.load_layout(ROOT / 'art/blender/data/t-class-wheelhouse.json'))
				file = Path(folder) / f'{index}.fbx'
				m.export(scene, file)
				bank = next(o for o in bpy.context.scene.objects if o.type == 'MESH' and o.data.materials[0].name == 'LampsSurface')
				self.assertEqual(len(bank.data.uv_layers), 2)
				self.assertEqual({round(v.uv.x) for v in bank.data.uv_layers[1].data}, {c['lamp'] for c in scene['controls'] if c['kind'] == 'lamp'})
				outputs.append(file.read_bytes())
			self.assertEqual(outputs[0], outputs[1])

if __name__ == '__main__': unittest.main()
