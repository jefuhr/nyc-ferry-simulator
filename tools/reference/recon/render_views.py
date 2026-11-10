"""Blender backend: deterministic flat geometry projections in a documented mesh frame.

Input geometry uses x longitudinal, y transverse, z up. Sections are x stations.
Imported materials, image textures, vertex colours and lights are never retained.
"""
import json
import math
from pathlib import Path
import struct
import sys
import zlib

import bpy
from mathutils import Vector


def png(path, width, height, pixels, ppm):
	def chunk(kind,data): return struct.pack('>I',len(data))+kind+data+struct.pack('>I',zlib.crc32(kind+data))
	rows=b''.join(b'\0'+pixels[y*width*3:(y+1)*width*3] for y in range(height))
	path.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',width,height,8,2,0,0,0))+chunk(b'tEXt',f'pixels_per_metre\0{ppm:g}'.encode())+chunk(b'IDAT',zlib.compress(rows,9))+chunk(b'IEND',b''))


FONT={
	'0':['111','101','101','101','111'],'1':['010','110','010','010','111'],
	'2':['111','001','111','100','111'],'3':['111','001','111','001','111'],
	'4':['101','101','111','001','001'],'5':['111','100','111','001','111'],
	'6':['111','100','111','101','111'],'7':['111','001','010','010','010'],
	'8':['111','101','111','101','111'],'9':['111','101','111','001','111'],
	'm':['000','000','111','111','101'],'p':['000','110','101','110','100'],
	'x':['000','101','010','101','000'],'/':['001','001','010','100','100'],
	'.':['000','000','000','000','010'],' ':['000']*5,
}


def text(pixels,width,height,left,top,label):
	for i,char in enumerate(label):
		for y,row in enumerate(FONT.get(char,FONT[' '])):
			for x,value in enumerate(row):
				px=left+i*8+x*2; py=top+y*2
				if value=='1':
					for dy in (0,1):
						for dx in (0,1):
							if px+dx<width and py+dy<height:
								start=((py+dy)*width+px+dx)*3; pixels[start:start+3]=b'\0'*3


def import_geometry(path):
	if path.suffix.lower()=='.obj':
		# Preserve numeric OBJ coordinates exactly: Blender's general OBJ importer
		# otherwise assumes Y-up and silently rotates the view frame.
		vertices=[]; faces=[]
		for line in path.read_text().splitlines():
			parts=line.split()
			if parts and parts[0]=='v': vertices.append(tuple(float(n) for n in parts[1:4]))
			elif parts and parts[0]=='f':
				indices=[int(n.split('/')[0]) for n in parts[1:]]
				faces.append(tuple(i-1 if i>0 else len(vertices)+i for i in indices))
		mesh=bpy.data.meshes.new('input'); mesh.from_pydata(vertices,[],faces); mesh.update()
		obj=bpy.data.objects.new('input',mesh); bpy.context.scene.collection.objects.link(obj)
	elif path.suffix.lower() in ('.usd','.usdz','.usdc','.usda'): bpy.ops.wm.usd_import(filepath=str(path))
	else: raise ValueError('Supported mesh formats: OBJ and USD/USDZ')
	# Bake world transforms, using mesh data only. Material/colour/UV data are
	# discarded by rebuilding every face on a fresh mesh datablock.
	vertices=[]; faces=[]
	for obj in sorted(bpy.context.scene.objects,key=lambda o:o.name):
		if obj.type!='MESH': continue
		offset=len(vertices); vertices.extend(tuple(obj.matrix_world@v.co) for v in obj.data.vertices)
		faces.extend(tuple(offset+i for i in poly.vertices) for poly in obj.data.polygons)
	for obj in list(bpy.data.objects): bpy.data.objects.remove(obj,do_unlink=True)
	if not vertices or not faces: raise ValueError('No mesh geometry')
	mesh=bpy.data.meshes.new('geometry'); mesh.from_pydata(vertices,[],faces); mesh.update()
	obj=bpy.data.objects.new('geometry',mesh); bpy.context.scene.collection.objects.link(obj)
	return obj,vertices,faces


def main():
	args=sys.argv[sys.argv.index('--')+1:]; model,out=Path(args[0]),Path(args[1]); ppm=float(args[2]); stations=json.loads(args[3])
	bpy.ops.wm.read_factory_settings(use_empty=True)
	obj,vertices,faces=import_geometry(model)
	low=Vector(tuple(min(v[i] for v in vertices) for i in range(3))); high=Vector(tuple(max(v[i] for v in vertices) for i in range(3)))
	size=high-low; centre=(low+high)/2
	if min(size)<=0 or ppm<=0: raise ValueError('Mesh extents and pixels per metre must be positive')
	for station in stations:
		if not low.x<=station<=high.x: raise ValueError('Section station lies outside the mesh')
	scene=bpy.context.scene
	clay=bpy.data.materials.new('grey geometry'); clay.use_nodes=True
	clay.node_tree.nodes.clear(); emission=clay.node_tree.nodes.new('ShaderNodeEmission'); emission.inputs['Color'].default_value=(.6,.6,.6,1)
	outnode=clay.node_tree.nodes.new('ShaderNodeOutputMaterial'); clay.node_tree.links.new(emission.outputs[0],outnode.inputs[0]); obj.data.materials.append(clay)
	camera_data=bpy.data.cameras.new('orthographic'); camera_data.type='ORTHO'; camera_data.sensor_fit='HORIZONTAL'
	camera=bpy.data.objects.new('orthographic',camera_data); scene.collection.objects.link(camera); scene.camera=camera
	engines={item.identifier for item in scene.render.bl_rna.properties['engine'].enum_items}
	scene.render.engine='BLENDER_EEVEE_NEXT' if 'BLENDER_EEVEE_NEXT' in engines else 'BLENDER_EEVEE'
	if hasattr(scene, 'eevee'): scene.eevee.taa_render_samples=1
	scene.render.film_transparent=True; scene.render.resolution_percentage=100
	scene.view_settings.view_transform='Standard'; scene.view_settings.look='None'
	views={'side':(Vector((0,-1,0)),(0,2)), 'plan':(Vector((0,0,1)),(0,1)), 'front':(Vector((1,0,0)),(1,2))}
	for station in stations: views[f'section-{station:g}']=(Vector((1,0,0)),(1,2))
	meta=dict(pixels_per_metre=ppm,dimensions_m=[round(v,9) for v in size],frame='x longitudinal, y transverse, z up; sections at x stations',views={})
	for name,(direction,axes) in views.items():
		section=None
		if name.startswith('section-'):
			station=float(name[len('section-'):]); segments=[]
			for face in faces:
				intersections=[]
				for a,b in zip(face,face[1:]+face[:1]):
					pa,pb=Vector(vertices[a]),Vector(vertices[b]); da,db=pa.x-station,pb.x-station
					if abs(da)<1e-9: intersections.append(pa)
					if da*db<0: intersections.append(pa+(pb-pa)*(-da/(db-da)))
				unique=[]
				for p in intersections:
					if not any((p-q).length<1e-8 for q in unique): unique.append(p)
				if len(unique)==2: segments.append(unique)
			curves=bpy.data.curves.new('section','CURVE'); curves.dimensions='3D'; curves.bevel_depth=.75/ppm; curves.bevel_resolution=0
			for a,b in segments:
				spline=curves.splines.new('POLY'); spline.points.add(1); spline.points[0].co=(*a,1); spline.points[1].co=(*b,1)
			section=bpy.data.objects.new('section',curves); scene.collection.objects.link(section); curves.materials.append(clay); obj.hide_render=True
		span_x,span_y=size[axes[0]],size[axes[1]]
		width=max(160,math.ceil(span_x*ppm)+80); height=max(120,math.ceil(span_y*ppm)+100)
		if width*height>100_000_000: raise ValueError('Projection too large; reduce pixels per metre')
		scene.render.resolution_x=width; scene.render.resolution_y=height
		camera_data.ortho_scale=width/ppm; camera_data.clip_start=.001; camera_data.clip_end=max(size)*20+10
		camera.location=centre+direction*(max(size)*4+2); camera.rotation_euler=(-direction).to_track_quat('-Z','Y').to_euler()
		bpy.context.view_layer.update(); bpy.ops.render.render()
		image=bpy.data.images['Render Result']; rgba=list(image.pixels)
		if not rgba:
			# Some headless Blender builds expose Render Result only through a file.
			intermediate=out/'.projection.png'; scene.render.image_settings.file_format='PNG'; scene.render.image_settings.color_mode='RGBA'; image.save_render(str(intermediate)); decoded=bpy.data.images.load(str(intermediate),check_existing=False); rgba=list(decoded.pixels); bpy.data.images.remove(decoded); intermediate.unlink()
		pixels=bytearray(b'\xff'*(width*height*3))
		for y in range(height):
			for x in range(width):
				if rgba[((height-1-y)*width+x)*4+3]>.5:
					start=(y*width+x)*3; pixels[start:start+3]=bytes((153,153,153))
		bar_m=10**math.floor(math.log10(max(.001,(width-80)/ppm)))
		bar_px=max(1,round(bar_m*ppm)); left=20; top=height-25
		for y in range(top,top+3):
			for x in range(left,left+bar_px):
				start=(y*width+x)*3; pixels[start:start+3]=b'\0'*3
		text(pixels,width,height,20,height-45,f'{bar_m:g} m'); text(pixels,width,height,20,10,f'{ppm:g} px/m')
		png(out/f'{name}.png',width,height,pixels,ppm)
		meta['views'][name]=dict(image_size=[width,height],geometry_pixels=[round(span_x*ppm),round(span_y*ppm)],scale_bar_m=bar_m,scale_bar_pixels=bar_px)
		if section: bpy.data.objects.remove(section,do_unlink=True); obj.hide_render=False
	(out/'views.json').write_text(json.dumps(meta,sort_keys=True,indent='\t')+'\n')


if __name__=='__main__': main()
