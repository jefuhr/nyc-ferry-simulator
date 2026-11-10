"""Synthetic consent, projection and reconstruction acceptance fixtures."""
import contextlib
import copy
import io
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import struct
import zlib
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import reference
from test_reference import png, picks


def manifest(root, names=('drawings/synthetic.png',)):
	entries = []
	for name in names:
		p = root / name
		p.parent.mkdir(parents=True, exist_ok=True)
		if not p.exists(): png(p)
		entries.append(dict(path=name, sha256=reference.sha256(p), kind='image',
			sources=[dict(source_id='local:' + name, sha256=reference.sha256(p), contributor='owner',
				consent=dict(scope='processing', version='processing-v2'), withdrawn=False)],
			screening=dict(decision='approved', by='coordinator', date='2026-11-10')))
	data = dict(schema_version=1, consent_policy='processing-v2', sheets=entries)
	reference.write_json(data, root / 'manifest.json')
	return data


def box(path, size=(20, 10, 5)):
	vertices = [(x, y, z) for z in (0, size[2]) for y in (0, size[1]) for x in (0, size[0])]
	faces = [(1, 2, 4, 3), (5, 7, 8, 6), (1, 5, 6, 2), (3, 4, 8, 7), (1, 3, 7, 5), (2, 6, 8, 4)]
	path.write_text(''.join('v %g %g %g\n' % p for p in vertices) + ''.join('f ' + ' '.join(map(str, f)) + '\n' for f in faces))


class GateTests(unittest.TestCase):
	def setUp(self):
		self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
		self.root = Path(self.tmp.name).resolve(); self.data = manifest(self.root)
	def save(self): reference.write_json(self.data, self.root / 'manifest.json')
	def refuse(self, text):
		self.save()
		for run in (lambda: reference.catalog(self.root), lambda: reference.measure(picks(), self.root)):
			with self.assertRaisesRegex(ValueError, 'drawings/synthetic.png.*' + text): run()
	def test_missing_entry(self):
		self.data['sheets'] = []; self.refuse('manifest entry')
	def test_hash_mismatch(self):
		self.data['sheets'][0]['sha256'] = '0' * 64; self.refuse('hash')
	def test_old_consent(self):
		self.data['sheets'][0]['sources'][0]['consent']['version'] = 'processing-v1'; self.refuse('consent')
	def test_withdrawn(self):
		self.data['sheets'][0]['sources'][0]['withdrawn'] = True; self.refuse('withdrawn')
	def test_excluded(self):
		self.data['sheets'][0]['screening']['decision'] = 'excluded'; self.refuse('screening')
	def test_good_and_measurement_only(self):
		self.assertEqual(len(reference.catalog(self.root)['files']), 1)
		for scope in ('owner', 'measurement-only'):
			self.data['sheets'][0]['sources'][0]['consent'] = dict(scope=scope, version='old')
			self.data['sheets'][0]['allowed_use'] = 'measurement-only'; self.save()
			self.assertAlmostEqual(reference.measure(picks(), self.root)['values']['length']['value'], 5)
	def test_default_requires_manifest_even_without_sheets(self):
		with self.assertRaisesRegex(ValueError, 'manifest'): reference.measure(picks())
		(self.root / 'manifest.json').unlink()
		with self.assertRaisesRegex(ValueError, 'manifest'): reference.catalog(self.root)
	def test_schema_and_duplicate_paths_fail_closed(self):
		self.data['sheets'][0]['sources'][0]['withdrawn'] = 1; self.save()
		with self.assertRaises(ValueError): reference.catalog(self.root)
		self.data['sheets'][0]['sources'][0]['withdrawn'] = False
		self.data['sheets'].append(copy.deepcopy(self.data['sheets'][0])); self.save()
		with self.assertRaisesRegex(ValueError, 'Duplicate'): reference.catalog(self.root)
	def test_cli_gate_and_legacy_warning(self):
		p = self.root / 'pick.json'; reference.write_json(picks(), p)
		stderr = io.StringIO()
		with contextlib.redirect_stderr(stderr):
			self.assertEqual(reference.main(['measure', str(p), '--output', str(self.root / 'values.json')]), 2)
		self.assertIn('manifest', stderr.getvalue())
		with contextlib.redirect_stderr(stderr):
			self.assertEqual(reference.main(['measure', str(p), '--legacy-no-manifest', '--output', str(self.root / 'values.json')]), 0)
		self.assertIn('WARNING', stderr.getvalue())


class AuditTests(unittest.TestCase):
	def test_prepared_audio_is_a_recordable_derived_method(self):
		# A finished audio clip lives in the repository, not beside the sheets, so its derived entry
		# names it with a repo: path. The manifest must still load so a withdrawal can be traced to it.
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); data = manifest(root)
			data['derived'] = [dict(path='repo:unity/Assets/FerrySim/Audio/Clips/example.wav', sha256='b'*64, method='audio-prep', **{'from': ['local:drawings/synthetic.png']})]
			reference.write_json(data, root / 'manifest.json')
			loaded = intake.load_manifest(root / 'manifest.json')
			self.assertEqual(loaded['derived'][0]['method'], 'audio-prep')
			data['derived'][0]['method'] = 'guesswork'
			reference.write_json(data, root / 'manifest.json')
			with self.assertRaises(ValueError): intake.load_manifest(root / 'manifest.json')

	def test_withdrawal_propagates_and_lists_every_value(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); data = manifest(root)
			data['sheets'][0]['sources'][0]['withdrawn'] = True
			data['derived'] = [dict(path='views/side.png', sha256='a'*64, method='photogrammetry', **{'from': ['local:drawings/synthetic.png']})]
			reference.write_json(data, root / 'manifest.json')
			p = root / 'pick.json'; reference.write_json(picks(), p)
			v = root / 'survey.json'; reference.write_json({'values': {'one': {'value': 1, 'source': 'views/side.png'}, 'two': {'source': 'missing.png'}}}, v)
			out = io.StringIO()
			with contextlib.redirect_stdout(out): result = reference.main(['audit', '--manifest', str(root/'manifest.json'), str(p), str(v)])
			self.assertEqual(result, 1)
			for text in ('pick.json', 'survey.json', 'values.one', 'values.two', 'withdrawn', 'missing'): self.assertIn(text, out.getvalue())
	def test_clean_excluded_and_non_sheet_sources(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); data = manifest(root)
			p = root / 'pick.json'; reference.write_json(picks(), p)
			self.assertEqual(reference.main(['audit', '--manifest', str(root/'manifest.json'), str(p)]), 0)
			data['sheets'][0]['screening']['decision'] = 'excluded'; reference.write_json(data, root/'manifest.json')
			self.assertEqual(reference.main(['audit', '--manifest', str(root/'manifest.json'), str(p)]), 1)


class HomographyTests(unittest.TestCase):
	def test_oblique_rectangle_with_independent_points(self):
		# Project a synthetic 2 m x 1 m planar rectangle with a pinhole homography.
		def project(x, y): return [(140*x + 30*y + 50)/(1+.16*x+.07*y), (10*x+150*y+35)/(1+.16*x+.07*y)]
		p = picks(); p['calibration'] = dict(method='plane-homography', points=[project(x,y) for x,y in ((0,0),(2,0),(2,1),(0,1),(.8,.4))], plane_m=[[0,0],[2,0],[2,1],[0,1],[.8,.4]], tolerance_px=.25)
		p['measurements'] = dict(edge=dict(points=[project(.2,.3),project(1.8,.3)]), position=dict(points=[project(1.1,.6)], operation='point'))
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); image=root/'drawings/synthetic.png'; image.parent.mkdir()
			# Rasterize the same oblique planar panel. Independent picks remain
			# separate from its four corner calibration anchors.
			corners=[project(x,y) for x,y in ((0,0),(2,0),(2,1),(0,1))]
			rows=[]
			for y in range(250):
				row=bytearray()
				for x in range(400):
					cross=[(b[0]-a[0])*(y-a[1])-(b[1]-a[1])*(x-a[0]) for a,b in zip(corners,corners[1:]+corners[:1])]
					row.append(80 if all(v>=0 for v in cross) or all(v<=0 for v in cross) else 255)
				rows.append(b'\0'+row)
			def chunk(kind,data): return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data))
			image.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',400,250,8,0,0,0,0))+chunk(b'IDAT',zlib.compress(b''.join(rows)))+chunk(b'IEND',b''))
			manifest(root)
			d = reference.measure(p, root)
			self.assertAlmostEqual(d['values']['edge']['value'], 1.6, delta=.016)
			self.assertEqual(d['values']['position']['value'], [1.1,.6])
			self.assertIn('reprojection', d['values']['edge']['note'])
	def test_degenerate_and_bad_fit_refused(self):
		for image, plane, tolerance in (([[0,0],[1,0],[2,0],[3,0]], [[0,0],[1,0],[2,0],[3,0]], 1), ([[0,0],[100,0],[100,100],[0,100],[90,30]], [[0,0],[1,0],[1,1],[0,1],[.5,.5]], .1)):
			p = picks(); p['calibration'] = dict(method='plane-homography', points=image, plane_m=plane, tolerance_px=tolerance)
			with self.assertRaisesRegex(ValueError, 'degenerate|reprojection'): reference.transform(p)


class ReconTests(unittest.TestCase):
	def test_scale_known_box_and_residuals(self):
		import recon
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp).resolve(); mesh = root/'box.obj'; box(mesh)
			spec = [dict(points=[[0,0,0],[20,0,0]], length_m=2), dict(points=[[0,0,0],[0,10,0]], length_m=1)]
			result = recon.scale_obj(mesh, root/'scaled.obj', spec)
			self.assertAlmostEqual(result['factor'], .1)
			self.assertLess(result['rms_residual_m'], .002)
			verts, _ = recon.read_obj(root/'scaled.obj')
			for axis, expected in enumerate((2,1,.5)):
				self.assertAlmostEqual(max(p[axis] for p in verts)-min(p[axis] for p in verts), expected, delta=expected*.001)
			spec[1]['length_m']=1.1
			self.assertGreater(recon.scale_obj(mesh, root/'other.obj', spec)['rms_residual_m'], 0)
	def test_private_outputs_refuse_nested_git_and_symlinks(self):
		import recon
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); (root/'.git').write_text('gitdir: somewhere')
			with self.assertRaisesRegex(ValueError, 'Git'): recon.private_path(root/'new/box.obj')
	def test_sharpness_rejects_blur(self):
		import recon
		self.assertEqual(recon.sharpness(bytes([128])*256,16,16), 0)
		self.assertGreater(recon.sharpness(bytes((x+y)%2*255 for y in range(16) for x in range(16)),16,16), 100)
	def test_recon_requires_screened_input_and_records_source_ids(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=manifest(root)
			out=root/'view.obj'; box(out)
			intake.record_derived(root/'manifest.json', [out], ['local:drawings/synthetic.png'], 'photogrammetry')
			updated=reference.load_json(root/'manifest.json')
			self.assertEqual(updated['derived'][0]['from'], ['local:drawings/synthetic.png'])
			self.assertEqual(updated['derived'][0]['sha256'], reference.sha256(out))
	def test_requests_register_and_sheet_links(self):
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=manifest(root)
			data['sheets'][0]['tags']={'requests':['WH-01@t-class']}; reference.write_json(data,root/'manifest.json')
			args=['requests','mark','--manifest',str(root/'manifest.json'),'WH-01@t-class']
			self.assertEqual(reference.main(args+['--status','open']),0)
			self.assertEqual(reference.main(args+['--status','screened']),0)
			register=reference.load_json(root/'requests.json')
			self.assertEqual(register['requests'][0]['sheets'], ['drawings/synthetic.png'])
			self.assertEqual(register['requests'][0]['status'], 'screened')


@unittest.skipUnless(os.environ.get('REFERENCE_BLENDER_TESTS')=='1', 'explicit Blender escalation required')
class BlenderViewTests(unittest.TestCase):
	def test_box_views_are_deterministic_flat_geometry_and_scaled(self):
		import recon
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); mesh=root/'box.obj'; box(mesh,(2,1,.5))
			for name in ('first','second'):
				try:
					recon.render_views(mesh,root/name,100,[1],os.environ.get('BLENDER','/Applications/Blender.app/Contents/MacOS/Blender'))
				except ValueError as exc:
					if isinstance(exc.__cause__,subprocess.CalledProcessError):
						print(exc.__cause__.stdout.decode()+exc.__cause__.stderr.decode())
					raise
			for name in ('side','plan','front','section-1'):
				a=root/'first'/f'{name}.png'; b=root/'second'/f'{name}.png'
				self.assertEqual(a.read_bytes(),b.read_bytes())
				colors=recon.png_colours(a)
				self.assertEqual(colors,{(255,255,255),(153,153,153),(0,0,0)})
			meta=reference.load_json(root/'first/views.json')
			self.assertEqual(meta['pixels_per_metre'],100)
			self.assertEqual(meta['dimensions_m'],[2,1,.5])
			self.assertEqual(meta['views']['plan']['geometry_pixels'],[200,100])
			for name,expected in (('plan',(200,100)),('side',(200,50)),('front',(100,50))):
				raw=(root/'first'/f'{name}.png').read_bytes(); offset=8; compressed=b''
				while offset<len(raw):
					n=struct.unpack('>I',raw[offset:offset+4])[0]; kind=raw[offset+4:offset+8]; payload=raw[offset+8:offset+8+n]; offset+=12+n
					if kind==b'IHDR': width,height=struct.unpack('>II',payload[:8])
					if kind==b'IDAT': compressed+=payload
				pixels=zlib.decompress(compressed); stride=width*3+1
				grey=[(x,y) for y in range(height) for x in range(width) if pixels[y*stride+1+x*3:y*stride+4+x*3]==bytes((153,153,153))]
				actual=(max(x for x,y in grey)-min(x for x,y in grey)+1,max(y for x,y in grey)-min(y for x,y in grey)+1)
				self.assertEqual(actual,expected,f'{name} actual raster scale')

class IntakeBoundaryTests(unittest.TestCase):
	def test_audit_reports_missing_file_even_with_manifest_entry(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); manifest(root)
			p=root/'pick.json'; reference.write_json(picks(),p)
			(root/'drawings/synthetic.png').unlink()
			self.assertTrue(any('missing sheet file' in finding for finding in intake.audit(root/'manifest.json',[p])))
	def test_reconstruction_rejects_withdrawn_or_measurement_only_derived_input(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=manifest(root); mesh=root/'mesh.obj'; box(mesh)
			intake.record_derived(root/'manifest.json',[mesh],['local:drawings/synthetic.png'],'photogrammetry')
			data=reference.load_json(root/'manifest.json'); data['sheets'][0]['allowed_use']='measurement-only'; reference.write_json(data,root/'manifest.json')
			with self.assertRaisesRegex(ValueError,'measurement-only'): intake.derived_input(mesh,root/'manifest.json')
			data['sheets'][0].pop('allowed_use'); data['sheets'][0]['sources'][0]['withdrawn']=True; reference.write_json(data,root/'manifest.json')
			with self.assertRaisesRegex(ValueError,'withdrawn'): intake.derived_input(mesh,root/'manifest.json')
	def test_derived_outputs_refuse_overwrite_and_material_leakage(self):
		import recon
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); mesh=root/'box.obj'; box(mesh)
			with mesh.open('a') as stream: stream.write('vt 1 1\nusemtl private texture\n')
			out=root/'scaled.obj'; dimensions=[dict(points=[[0,0,0],[20,0,0]],length_m=2)]
			recon.scale_obj(mesh,out,dimensions)
			self.assertNotIn('vt ',out.read_text()); self.assertNotIn('usemtl',out.read_text())
			before=out.read_bytes()
			with self.assertRaisesRegex(ValueError,'already exists'): recon.scale_obj(mesh,out,dimensions)
			self.assertEqual(before,out.read_bytes())
	def test_frame_extraction_keeps_scores_and_records_only_sharp_frames(self):
		import recon
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); output=root/'frames'
			def fake_invoke(argv,timeout=3600):
				if '-map_metadata' in argv:
					pattern=Path(argv[-1]); (pattern.parent/'frame-000001.png').write_bytes(b'sharp'); (pattern.parent/'frame-000002.png').write_bytes(b'blur')
					return subprocess.CompletedProcess(argv,0,b'')
				frame=Path(argv[argv.index('-i')+1])
				raw=bytes((x+y)%2*255 for y in range(128) for x in range(128)) if frame.read_bytes()==b'sharp' else bytes([128])*128*128
				return subprocess.CompletedProcess(argv,0,raw)
			with patch.object(recon,'invoke',side_effect=fake_invoke), patch.object(recon,'tool',return_value='ffmpeg'):
				outputs=recon.frames(root/'synthetic.mp4',output,2,100)
			self.assertEqual([p.name for p in outputs],['frame-000001.png','frames.json'])
			self.assertEqual([r['accepted'] for r in reference.load_json(output/'frames.json')['frames']],[True,False])

class CliPipelineTests(unittest.TestCase):
	@unittest.skipUnless(__import__('shutil').which('ffmpeg'),'installed ffmpeg unavailable')
	def test_synthetic_video_to_gated_frames_e2e(self):
		import recon
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); video=root/'walk.mp4'
			result=subprocess.run(['ffmpeg','-nostdin','-v','error','-f','lavfi','-i','testsrc2=size=128x128:rate=2:duration=1','-an','-map_metadata','-1',str(video)],capture_output=True)
			self.assertEqual(result.returncode,0,result.stderr.decode())
			data=manifest(root,('walk.mp4',)); data['sheets'][0]['kind']='video'; reference.write_json(data,root/'manifest.json')
			self.assertEqual(reference.main(['recon','frames','--manifest',str(root/'manifest.json'),'--input',str(video),'--output',str(root/'frames'),'--rate','2','--min-sharpness','1']),0)
			updated=reference.load_json(root/'manifest.json')
			self.assertEqual(len(updated['derived']),3)
			self.assertTrue(all(e['from']==['local:walk.mp4'] and e['method']=='frame-extract' for e in updated['derived']))
	def test_cli_scale_requires_lineage_and_refuses_withdrawal(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=manifest(root); mesh=root/'box.obj'; box(mesh)
			intake.record_derived(root/'manifest.json',[mesh],['local:drawings/synthetic.png'],'photogrammetry')
			dims=root/'dimensions.json'; reference.write_json([dict(points=[[0,0,0],[20,0,0]],length_m=2)],dims)
			args=['recon','scale','--manifest',str(root/'manifest.json'),'--input',str(mesh),'--output',str(root/'scaled.obj'),'--dimensions',str(dims)]
			self.assertEqual(reference.main(args),0)
			updated=reference.load_json(root/'manifest.json')
			self.assertEqual(len(updated['derived']),3)
			self.assertTrue(all(e.get('scale') for e in updated['derived'] if 'scaled' in e['path']))
			updated['sheets'][0]['sources'][0]['withdrawn']=True; reference.write_json(updated,root/'manifest.json')
			with contextlib.redirect_stderr(io.StringIO()): self.assertEqual(reference.main(args),2)
	def test_photogrammetry_measurement_preserves_method_and_withdrawal_link(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); manifest(root)
			png(root/'side.png'); intake.record_derived(root/'manifest.json',[root/'side.png'],['local:drawings/synthetic.png'],'photogrammetry',scale='known-dimension: 2 m',pixels_per_metre=40)
			data=reference.load_json(root/'manifest.json'); view=copy.deepcopy(data['sheets'][0]); view.update(path='side.png',sha256=reference.sha256(root/'side.png')); data['sheets'].append(view); reference.write_json(data,root/'manifest.json')
			p=picks(); p['image']='side.png'; result=reference.measure(p,root)
			self.assertEqual(result['values']['length']['method'],'photogrammetry')
			self.assertEqual(result['values']['length']['source'],'side.png')

class DerivedScreeningTests(unittest.TestCase):
	def fixture(self,root):
		import intake
		manifest(root); png(root/'side.png')
		intake.record_derived(root/'manifest.json',[root/'side.png'],['local:drawings/synthetic.png'],'photogrammetry',scale='known-dimension: 2 m',pixels_per_metre=40)
		return reference.load_json(root/'manifest.json')
	def test_derived_only_is_refused_until_its_own_screening(self):
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=self.fixture(root); p=picks(); p['image']='side.png'
			for call in (lambda:reference.measure(p,root),lambda:reference.catalog(root)):
				with self.assertRaisesRegex(ValueError,'side.png.*approved sheets entry'): call()
			view=copy.deepcopy(data['sheets'][0]); view.update(path='side.png',sha256=reference.sha256(root/'side.png')); data['sheets'].append(view)
			reference.write_json(data,root/'manifest.json')
			self.assertEqual(reference.measure(p,root)['values']['length']['method'],'photogrammetry')
			self.assertEqual(len(reference.catalog(root)['files']),2)
			data['sheets'][-1]['screening']['decision']='excluded'; reference.write_json(data,root/'manifest.json')
			with self.assertRaisesRegex(ValueError,'screening'): reference.measure(p,root)
	def test_paired_hash_and_source_ids_must_match(self):
		for mutation in ('hash','from'):
			with self.subTest(mutation=mutation),tempfile.TemporaryDirectory() as tmp:
				root=Path(tmp).resolve(); data=self.fixture(root)
				view=copy.deepcopy(data['sheets'][0]); view.update(path='side.png',sha256=reference.sha256(root/'side.png')); data['sheets'].append(view)
				if mutation=='hash': data['derived'][0]['sha256']='0'*64
				else: data['derived'][0]['from']=['local:other']
				reference.write_json(data,root/'manifest.json')
				with self.assertRaisesRegex(ValueError,'side.png.*(hash|source ids)'): reference.catalog(root)
	def test_withdrawal_audits_every_approved_view_value(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=self.fixture(root)
			view=copy.deepcopy(data['sheets'][0]); view.update(path='side.png',sha256=reference.sha256(root/'side.png')); data['sheets'].append(view)
			for sheet in data['sheets']: sheet['sources'][0]['withdrawn']=True
			reference.write_json(data,root/'manifest.json')
			values=root/'values.json'; reference.write_json({'values':{'one':{'source':'side.png'},'two':{'source':'side.png'}}},values)
			findings=intake.audit(root/'manifest.json',[values])
			self.assertEqual(len(findings),2)
			self.assertTrue(all('withdrawn' in finding for finding in findings))

class ScreeningScopeTests(unittest.TestCase):
	def test_excluded_sibling_view_does_not_exclude_approved_original(self):
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); data=manifest(root); png(root/'bad-view.png')
			view=copy.deepcopy(data['sheets'][0]); view.update(path='bad-view.png',sha256=reference.sha256(root/'bad-view.png'))
			view['screening']['decision']='excluded'; data['sheets'].append(view); reference.write_json(data,root/'manifest.json')
			self.assertEqual(reference.measure(picks(),root)['values']['length']['value'],5)
			p=picks(); p['image']='bad-view.png'
			with self.assertRaisesRegex(ValueError,'screening'): reference.measure(p,root)

class AuditCitationTests(unittest.TestCase):
	def test_absolute_external_file_is_not_a_repository_citation(self):
		import intake
		with tempfile.TemporaryDirectory() as tmp:
			root=Path(tmp).resolve(); manifest(root); private=root/'private.json'; private.write_text('{}')
			values=root/'values.json'; reference.write_json({'values':{'one':{'source':str(private)}}},values)
			self.assertTrue(intake.audit(root/'manifest.json',[values]))
