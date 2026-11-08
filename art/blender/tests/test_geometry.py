"""Blender checks: measured outlines affect meshes; single renderer and finite budgeted solids."""
import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'art/blender'))
try:
	import bpy
	import ferries
	from dimensions import dimensions, load_layout, mounts
except ImportError:
	bpy = None


@unittest.skipIf(bpy is None, 'Run inside the pinned Blender under heavy + unity locks')
class GeometryTests(unittest.TestCase):
	@classmethod
	def setUpClass(cls):
		out = ROOT / 'artifacts/ferry-exterior/test-palette'
		out.mkdir(parents=True, exist_ok=True)
		cls.material = ferries.palette(out, load_layout('t-class')['materials'])

	def model(self, name, lod, layout=None):
		v = json.loads((ROOT / 'vessels' / (name + '.json')).read_text())
		d = dimensions(v, layout or load_layout(name))
		obj = ferries.build(d, lod, self.material)
		self.addCleanup(bpy.data.objects.remove, obj, do_unlink=True)
		return obj, d

	def test_all_lods_respect_geometry_budget_datum_materials_and_dimensions(self):
		for name in ('t-class', 'k-class'):
			for lod in (0, 1):
				obj, d = self.model(name, lod)
				r = ferries.receipt(obj, d, mounts(d))
				self.assertLessEqual(r['triangles'], 5000 if lod else 80000)
				self.assertEqual(len(obj.material_slots), 1 if lod else 7)
				self.assertEqual(obj.type, 'MESH')
				self.assertEqual(len(obj.modifiers), 0)
				lo, hi = r['bounds_blender']
				self.assertAlmostEqual(hi[1] - lo[1], d['length'], places=3)
				self.assertAlmostEqual(lo[2], -d['draft'], places=3)
				self.assertAlmostEqual(hi[0] - lo[0], d['visual_beam'], places=3)

	def test_window_faces_outward_and_bow_sightline_is_clear(self):
		from mathutils import Vector
		for name in ('t-class','k-class'):
			obj,d=self.model(name,0)
			origin=ferries.blender((d['beam'],(d['window_bottom']+d['window_top'])/2,.17))
			hit,point,normal,face=obj.ray_cast(origin,Vector((-1,0,0)))
			self.assertTrue(hit)
			self.assertGreater(normal.x,0, 'Saloon glazing must face the exterior')
			self.assertEqual(obj.data.polygons[face].material_index,2)
			for eye in (mounts(d)['Eye'], [0,d['upper_deck_height']+1.8,(d['wheelhouse_aft']+d['wheelhouse_forward'])/2]):
				origin=ferries.blender(eye);bow=ferries.blender(mounts(d)['Bow'])
				direction=(bow-origin).normalized();distance=(bow-origin).length
				travelled=0;first=None
				for _ in range(50):
					hit,point,normal,face=obj.ray_cast(origin,direction,distance=distance+.1-travelled)
					if not hit:break
					travelled+=(point-origin).length
					if normal.dot(direction)<-1e-6:
						first=travelled;break
					origin=point+direction*.0001;travelled+=.0001
				self.assertIsNotNone(first,f'{name} eye={eye}: bow deck must be visible')
				self.assertGreater(first,distance-.5,'Front-facing solid blocks the bow sightline')

	def test_provisional_rubber_strake_is_visible_outside_the_hull(self):
		from mathutils import Vector
		for name in ('t-class','k-class'):
			obj,d=self.model(name,0)
			origin=ferries.blender((d['beam'],d['freeboard']-.25,0))
			hit,point,normal,face=obj.ray_cast(origin,Vector((-1,0,0)))
			self.assertTrue(hit)
			self.assertEqual(obj.data.polygons[face].material_index,4,'Rubber must not be buried inside the hull/sponson')
			self.assertGreater(point.x,d['beam']/2)

	def test_foredeck_has_one_visible_top_surface_and_stairs_have_openings(self):
		from mathutils import Vector
		from mathutils.geometry import intersect_ray_tri
		for name in ('t-class','k-class'):
			obj,d=self.model(name,0)
			origin=ferries.blender((2.8,d['freeboard']+1,10.07));down=Vector((0,0,-1))
			coincident=0
			for polygon in obj.data.polygons:
				if polygon.normal.z<.9:continue
				a,b,c=(obj.data.vertices[i].co for i in polygon.vertices)
				point=intersect_ray_tri(a,b,c,down,origin,True)
				if point is not None and abs(point.z-d['freeboard'])<.001:coincident+=1
			self.assertEqual(coincident,1,f'{name}: overlapping hull/deck top faces cause black render artifacts')
			for start,end in zip(d['stair_start'].values(),d['stair_end'].values()):
				origin=ferries.blender((start[0],d['upper_deck_height']+1,(start[2]+end[2])/2))
				hit,point,normal,face=obj.ray_cast(origin,down)
				self.assertTrue(hit)
				self.assertLess(point.z,d['upper_deck_height']-.5,f'{name}: upper deck blocks stair opening')

	def test_deck_outline_and_sheer_changes_reach_the_mesh(self):
		for group, component in (('main_outline', 0), ('sheer_profile', 1), ('hull_stations', 1)):
			layout = load_layout('t-class')
			obj, d = self.model('t-class', 1, layout)
			before = ferries.receipt(obj, d, mounts(d))['geometry_sha256']
			changed = copy.deepcopy(layout)
			changed['values'][group]['p02']['value'][component] += .1
			obj, d = self.model('t-class', 1, changed)
			self.assertNotEqual(before, ferries.receipt(obj, d, mounts(d))['geometry_sha256'], group)


if __name__ == '__main__':
	suite = unittest.TestSuite(unittest.defaultTestLoader.discover(str(ROOT / 'art/blender/tests'), pattern=pattern)
		for pattern in ('test_dimensions.py', 'test_geometry.py'))
	if not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful():
		raise SystemExit(1)
