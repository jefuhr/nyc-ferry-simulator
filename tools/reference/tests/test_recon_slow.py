"""Explicit synthetic RealityKit acceptance. Never runs in normal discovery."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import reference
import recon


@unittest.skipUnless(os.environ.get('REFERENCE_RECON_SLOW')=='1','slow reconstruction only on explicit request and escalation')
class ReconstructionAcceptance(unittest.TestCase):
	def test_textured_box_forty_views_medium_detail(self):
		# Keep synthetic frames/mesh/poses privately; only numeric reports enter Git.
		root=recon.new_output(os.environ['REFERENCE_RECON_OUTPUT'],directory=True)
		frames=root/'frames'; frames.mkdir()
		blender=os.environ.get('BLENDER',recon.BLENDER)
		def render(script,*args):
			result=subprocess.run(['lockf','-k',str(Path.home()/'FerrySim-cache/heavy.lock'),blender,'--background','--factory-startup','-noaudio','--python-exit-code','1','--python',str(recon.HERE/'recon'/script),'--',*map(str,args)],capture_output=True,text=True)
			(root/(script+'.log')).write_text(result.stdout+result.stderr)
			self.assertEqual(result.returncode,0,'Blender failed; see private synthetic log')
		render('render_box.py',frames)
		self.assertEqual(len(list(frames.glob('*.jpg'))),40)
		mesh=root/'box.usdz'; recon.build(frames,mesh,'medium','sequential')
		poses=reference.load_json(mesh.with_suffix('.poses.json'))
		self.assertTrue(poses['poses'], 'No camera poses were reconstructed')
		render('measure_box.py',mesh,root/'metrics.json',root/'geometry.obj')
		metrics=reference.load_json(root/'metrics.json')
		# The fixture analyzer picks a top edge in the arbitrary reconstruction frame.
		result=recon.scale_obj(root/'geometry.obj',root/'scaled.obj',[dict(points=metrics['known_edge_points'],length_m=2)])
		self.assertAlmostEqual(result['factor'],metrics['scale_factor'],places=5)
		print('synthetic reconstruction metrics: '+json.dumps(metrics,sort_keys=True))
		for axis,error in zip(('length','width','height'),metrics['error_percent']):
			self.assertLess(abs(error),2,f'{axis} reconstruction error {error:+.4f}% exceeds 2%')
