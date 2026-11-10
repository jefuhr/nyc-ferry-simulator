"""Private, consent-bound reconstruction orchestration; no installed Python packages."""
from __future__ import annotations
import json
import math
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import zlib

import reference as ref
from intake import private_path

HERE=Path(__file__).resolve().parent
BLENDER='/Applications/Blender.app/Contents/MacOS/Blender'


def tool(name):
	result=shutil.which(name)
	if result is None: raise ValueError(f'{name} is unavailable; install nothing automatically')
	return result


def invoke(argv, timeout=3600):
	# Tool stderr may include private paths and metadata: retain it privately in the
	# caller's environment, never echo it into a public patch or report.
	try:
		return subprocess.run(list(map(str,argv)),capture_output=True,check=True,timeout=timeout,umask=0o077)
	except subprocess.TimeoutExpired as exc:
		raise ValueError(f'{Path(argv[0]).name} timed out; private command details withheld') from exc
	except subprocess.CalledProcessError as exc:
		raise ValueError(f'{Path(argv[0]).name} failed (exit {exc.returncode}); private tool output withheld') from exc


def new_output(output, directory=False):
	output=private_path(output)
	if output.exists(): raise ValueError('Reconstruction output already exists; choose a new path')
	output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
	if directory: output.mkdir(mode=0o700)
	return output


def read_obj(path):
	vertices=[]; faces=[]
	for line in Path(path).read_text().splitlines():
		parts=line.split()
		if not parts or parts[0].startswith('#'): continue
		if parts[0]=='v':
			if len(parts)!=4: raise ValueError('OBJ vertices must have three coordinates')
			vertices.append(tuple(ref.finite(float(n),'mesh coordinate') for n in parts[1:]))
		elif parts[0]=='f':
			indices=[int(p.split('/')[0]) for p in parts[1:]]
			if len(indices)<3 or any(i==0 or i>len(vertices) or i < -len(vertices) for i in indices): raise ValueError('Invalid OBJ face')
			faces.append(tuple(i-1 if i>0 else len(vertices)+i for i in indices))
	if not vertices or not faces: raise ValueError('Mesh must contain vertices and faces')
	return vertices,faces


def bounds(vertices):
	return [max(v[i] for v in vertices)-min(v[i] for v in vertices) for i in range(3)]


def scale_mesh(mesh, output, dimensions, blender=BLENDER):
	if Path(output).suffix.lower()!='.obj': raise ValueError('Scaled geometry output must be .obj')
	if Path(mesh).suffix.lower()=='.obj': return scale_obj(mesh,output,dimensions)
	private_path(output)
	Path(output).parent.mkdir(parents=True,exist_ok=True,mode=0o700)
	with tempfile.TemporaryDirectory(prefix='.mesh-',dir=Path(output).parent) as temporary:
		converted=Path(temporary)/'geometry.obj'
		invoke(['lockf','-k',Path.home()/'FerrySim-cache/heavy.lock',blender,'--background','--factory-startup','-noaudio','--python-exit-code','1','--python',HERE/'recon/export_geometry.py','--',mesh,converted])
		return scale_obj(converted,output,dimensions)


def scale_obj(mesh, output, dimensions):
	vertices,faces=read_obj(mesh)
	if not isinstance(dimensions,list) or not dimensions: raise ValueError('Provide one or more known-length dimensions')
	spans=[]; lengths=[]
	for dimension in dimensions:
		points=dimension['points']
		if len(points)!=2 or any(not isinstance(p,list) or len(p)!=3 for p in points): raise ValueError('Scale dimension requires two 3D mesh points')
		a,b=[tuple(ref.finite(n,'picked mesh coordinate') for n in p) for p in points]
		spans.append(ref.finite(math.dist(a,b),'picked mesh span',True))
		lengths.append(ref.finite(dimension['length_m'],'known length',True))
	factor=sum(d*l for d,l in zip(spans,lengths))/sum(d*d for d in spans)
	residuals=[d*factor-l for d,l in zip(spans,lengths)]
	out=new_output(output)
	out.write_text(''.join('v ' + ' '.join(format(n*factor,'.17g') for n in vertex)+'\n' for vertex in vertices)
		+ ''.join('f '+' '.join(str(i+1) for i in face)+'\n' for face in faces))
	os.chmod(out,0o600)
	return dict(factor=factor,residuals_m=residuals,rms_residual_m=math.sqrt(sum(r*r for r in residuals)/len(residuals)),dimensions_m=[d*factor for d in bounds(vertices)],scale='known-dimension: '+', '.join(f'{l:g} m' for l in lengths))


def sharpness(pixels,width,height):
	if width<3 or height<3 or len(pixels)!=width*height: raise ValueError('Invalid grayscale sharpness raster')
	lap=[4*pixels[y*width+x]-pixels[y*width+x-1]-pixels[y*width+x+1]-pixels[(y-1)*width+x]-pixels[(y+1)*width+x] for y in range(1,height-1) for x in range(1,width-1)]
	mean=sum(lap)/len(lap)
	return sum((v-mean)**2 for v in lap)/len(lap)


def frames(video,output,rate,threshold):
	output=new_output(output,directory=True)
	rate=ref.finite(rate,'frame rate',True); threshold=ref.finite(threshold,'sharpness threshold')
	if threshold<0: raise ValueError('sharpness threshold must be nonnegative')
	ffmpeg=tool('ffmpeg')
	with tempfile.TemporaryDirectory(prefix='.frames-',dir=output.parent) as temporary:
		invoke([ffmpeg,'-nostdin','-v','error','-i',video,'-an','-vf',f'fps={rate:g}','-map_metadata','-1',Path(temporary)/'frame-%06d.png'])
		scores=[]; kept=[]
		for frame in sorted(Path(temporary).glob('frame-*.png')):
			raw=invoke([ffmpeg,'-nostdin','-v','error','-i',frame,'-vf','scale=128:128,format=gray','-f','rawvideo','-']).stdout
			score=sharpness(raw,128,128); accepted=score>=threshold
			scores.append(dict(frame=frame.name,sharpness=score,accepted=accepted))
			if accepted:
				target=output/frame.name; shutil.move(frame,target); os.chmod(target,0o600); kept.append(target)
	if not kept: raise ValueError('No sharp frames survived; inspect capture and sharpness threshold')
	report=output/'frames.json'; ref.write_json(dict(rate_hz=rate,sharpness_threshold=threshold,frames=scores),report,private=True)
	return kept+[report]


def render_views(mesh,output,ppm,stations,blender=BLENDER):
	ppm=ref.finite(ppm,'pixels per metre',True)
	stations=[ref.finite(s,'section station') for s in stations]
	output=new_output(output,directory=True)
	if not Path(blender).is_file(): raise ValueError('Blender is unavailable; no packages are installed')
	invoke(['lockf','-k',Path.home()/'FerrySim-cache/heavy.lock',blender,'--background','--factory-startup','-noaudio','--python-exit-code','1','--python',HERE/'recon/render_views.py','--',mesh,output,str(ppm),json.dumps(stations)])
	return sorted(output.glob('*.png'))+[output/'views.json']


def png_colours(path):
	"""Inspect the deterministic RGB PNGs emitted by render_views (no dependencies)."""
	raw=Path(path).read_bytes(); offset=8; compressed=b''; width=height=None
	while offset<len(raw):
		n=struct.unpack('>I',raw[offset:offset+4])[0]; kind=raw[offset+4:offset+8]; data=raw[offset+8:offset+8+n]; offset+=12+n
		if kind==b'IHDR': width,height,depth,colour,*_=struct.unpack('>IIBBBBB',data)
		if kind==b'IDAT': compressed+=data
	if depth!=8 or colour!=2: raise ValueError('Expected an 8-bit RGB PNG')
	pixels=zlib.decompress(compressed); result=set(); stride=width*3+1
	for y in range(height):
		row=pixels[y*stride:(y+1)*stride]
		if row[0]!=0: raise ValueError('Expected unfiltered PNG')
		result.update(tuple(row[i:i+3]) for i in range(1,len(row),3))
	return result


def build(frames,output,detail='medium',ordering='sequential'):
	if detail not in ('medium','full','raw'): raise ValueError('Measuring requires medium detail or higher')
	output=private_path(output)
	if output.suffix.lower()!='.usdz': raise ValueError('Build mesh output must be .usdz')
	poses=output.with_suffix('.poses.json')
	if output.exists() or poses.exists(): raise ValueError('Reconstruction output already exists')
	output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
	with tempfile.TemporaryDirectory(prefix='.recon-build-',dir=output.parent) as temporary:
		binary=Path(temporary)/'recon'
		try:
			invoke(['xcrun','swiftc','-module-cache-path',Path(temporary)/'modules',HERE/'recon/recon.swift','-o',binary])
		except (ValueError,OSError) as exc:
			raise ValueError('RealityKit PhotogrammetrySession API or installed Xcode compiler is unavailable; build failed') from exc
		try:
			result=subprocess.run([str(binary),str(frames),str(output),str(poses),detail,ordering],capture_output=True,text=True,timeout=3600,umask=0o077)
		except subprocess.TimeoutExpired as exc:
			raise ValueError('PhotogrammetrySession timed out after 3600 seconds') from exc
		if result.returncode:
			if result.returncode==3: raise ValueError('RealityKit PhotogrammetrySession is unavailable on this machine')
			raise ValueError(f'PhotogrammetrySession failed (exit {result.returncode}); private details withheld')
	if not output.is_file() or not poses.is_file(): raise ValueError('Reconstruction did not produce both mesh and camera poses')
	for p in (output,poses): os.chmod(p,0o600)
	return [output,poses]


def add_parser(sub):
	p=sub.add_parser('recon',help='Consent-bound private video/mesh reconstruction')
	r=p.add_subparsers(dest='action',required=True)
	for action in ('frames','build','scale','views'):
		p=r.add_parser(action); p.add_argument('--manifest',type=Path,required=True); p.add_argument('--input',type=Path,required=True); p.add_argument('--output',type=Path,required=True)
		if action=='frames':
			p.add_argument('--rate',type=float,default=2); p.add_argument('--min-sharpness',type=float,default=100)
		if action=='build':
			p.add_argument('--detail',choices=('medium','full','raw'),default='medium'); p.add_argument('--ordering',choices=('sequential','unordered'),default='sequential')
		if action=='scale':
			p.add_argument('--dimensions',type=Path,required=True,help='JSON array of {points: [[x,y,z],[x,y,z]], length_m: number}'); p.add_argument('--blender',default=BLENDER)
		if action=='views':
			p.add_argument('--pixels-per-metre',type=float,default=200); p.add_argument('--station',type=float,action='append',default=[]); p.add_argument('--blender',default=BLENDER)


def run(args):
	import intake
	manifest=private_path(args.manifest); output=private_path(args.output)
	input_path=args.input.expanduser().absolute()
	if any(p.is_symlink() for p in (input_path,*input_path.parents)): raise ValueError('Symlinks are not permitted in reconstruction input')
	input_path=input_path.resolve(strict=True)
	if input_path==output or input_path.is_dir() and output.is_relative_to(input_path): raise ValueError('Output must not overlap its input')
	if args.action=='build':
		if not input_path.is_dir(): raise ValueError('Build input must be a frame folder')
		frames_input=sorted(p for p in input_path.iterdir() if p.suffix.lower() in ref.IMAGE_EXTENSIONS)
		if not frames_input: raise ValueError('Build input contains no frames')
		ids=sorted(set(s for frame in frames_input for s in intake.derived_input(frame,manifest)))
	else:
		ids=intake.derived_input(input_path,manifest)
	metadata={}
	if args.action=='frames': outputs=frames(input_path,output,args.rate,args.min_sharpness); method='frame-extract'
	elif args.action=='build': outputs=build(input_path,output,args.detail,args.ordering); method='photogrammetry'
	elif args.action=='scale':
		report=output.with_suffix('.scale.json')
		if report.exists(): raise ValueError('Scale report output already exists')
		result=scale_mesh(input_path,output,ref.load_json(args.dimensions),args.blender); metadata['scale']=result['scale']
		report=output.with_suffix('.scale.json'); ref.write_json(result,report,private=True); outputs=[output,report]; method='photogrammetry'
		print(ref.json_text(result),end='')
	else:
		# Views accept geometry-only, explicitly scaled OBJ. An unscaled mesh has no
		# defensible pixels/metre; require the recorded known-length scaling step.
		data=intake.load_manifest(manifest)
		entry=next((e for e in data.get('derived',[]) if (manifest.parent/e['path']).resolve()==input_path),{})
		if not entry.get('scale'): raise ValueError('Render views require a recorded known-length scale')
		metadata=dict(scale=entry['scale'],pixels_per_metre=args.pixels_per_metre)
		outputs=render_views(input_path,output,args.pixels_per_metre,args.station,args.blender); method='photogrammetry'
	intake.record_derived(manifest,outputs,ids,method,**metadata)
	print(f'Recorded {len(outputs)} derived outputs')
	return 0
