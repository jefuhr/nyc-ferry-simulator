"""Build the generic passenger catamarans from scratch, export FBX and geometry receipts.
Run with Blender --background --factory-startup --python art/blender/ferries.py -- --out DIR.
Optional --renders DIR creates four studio views of each LOD. No .blend is written.
"""
import argparse
import hashlib
import json
import math
import sys
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from dimensions import dimensions, mounts, audited_classes

ROOT = Path(__file__).resolve().parents[2]
# Neutral, deliberately unbranded paint, deck, glass, metal and rubber.
PALETTE = [(0.82, 0.85, 0.85, 1), (.37, .41, .43, 1), (.025, .055, .065, 1), (.19, .23, .25, 1), (.055, .065, .07, 1), (.65, .69, .70, 1), (.93, .93, .91, 1), (.28, .30, .31, 1)]


def blender(v):
	return Vector((v[0], v[2], v[1]))


class Geometry:
	def __init__(self):
		self.v, self.f, self.colours, self.preserve_winding = [], [], [], []

	def part(self, vertices, faces, colour, preserve_winding=False):
		offset = len(self.v)
		self.v.extend(blender(v) for v in vertices)
		self.f.extend(tuple(i + offset for i in reversed(face)) for face in faces)
		self.colours.extend([colour] * len(faces))
		self.preserve_winding.extend([preserve_winding] * len(faces))

	def box(self, centre, size, colour=0):
		x, y, z = centre
		a, b, c = (n / 2 for n in size)
		self.part([(x+dx*a,y+dy*b,z+dz*c) for dx,dy,dz in [(-1,-1,-1),(1,-1,-1),(1,-1,1),(-1,-1,1),(-1,1,-1),(1,1,-1),(1,1,1),(-1,1,1)]], [(0,3,2,1),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)], colour)

	def rod(self, a, b, radius=.035, colour=5, sides=6):
		a, b = Vector(a), Vector(b)
		axis = (b-a).normalized()
		u = axis.cross(Vector((0,1,0)))
		if u.length < .01:
			u = axis.cross(Vector((1,0,0)))
		u.normalize()
		v = axis.cross(u)
		verts = [tuple(p + radius * (u * math.cos(2*math.pi*i/sides) + v * math.sin(2*math.pi*i/sides))) for p in (a,b) for i in range(sides)]
		faces = [tuple(range(sides-1,-1,-1)),tuple(range(sides,2*sides))]
		faces += [(i,(i+1)%sides,(i+1)%sides+sides,i+sides) for i in range(sides)]
		self.part(verts, faces, colour)

	def rail(self, a, b, base, lod):
		length = (Vector(b)-Vector(a)).length
		posts = max(1, math.ceil(length / (2.4 if lod else 1.4)))
		for i in range(posts+1):
			x, z = (a[j]+(b[j]-a[j])*i/posts for j in range(2))
			self.rod((x,base,z),(x,base+1.05,z),.035,5,4 if lod else 12)
		for h in ([1.05] if lod else [.48,1.05]):
			self.rod((a[0],base+h,a[1]),(b[0],base+h,b[1]),.035,5,4 if lod else 12)

	def hull(self, x, d, lod):
		L, w, f, draft = d['length'], d['demihull_beam']/2, d['freeboard'], d['draft']
		stations = [(-.5, .86, .72),(-.35,1,1),(.15,1,1),(.33,.78,.85),(.44,.35,.5),(.5,.035,.03)]
		if not lod:
			stations=[(-.5,.86,.72),(-.47,.93,.9),(-.4,.98,.98),(-.3,1,1),(-.12,1,1),(.08,1,1),(.2,.96,.98),(.3,.83,.9),(.38,.61,.7),(.44,.35,.5),(.48,.13,.22),(.5,.035,.03)]
		verts=[]
		section=[(-1,f),(1,f),(1,.05),(.68,-.72*draft),(.25,-draft),(-.25,-draft),(-.68,-.72*draft),(-1,.05)]
		if not lod:
			section=[(-1,f),(1,f),(1,.05),(.98,-.25*draft),(.89,-.5*draft),(.68,-.78*draft),(.43,-.94*draft),(.20,-draft),(-.20,-draft),(-.43,-.94*draft),(-.68,-.78*draft),(-.89,-.5*draft),(-.98,-.25*draft),(-1,.05)]
		n=len(section)
		for z, width, depth in stations:
			# Chined demihull with fine forward entrance and transom aft.
			for dx,y in section:
				rake=(-.025*L if z>0 else .012*L)*(1-depth)*max(0,-y/draft)
				verts.append((x+dx*w*width, y if y>=0 else y*depth,z*L+rake))
		faces=[tuple(range(n-1,-1,-1)),tuple(range((len(stations)-1)*n,len(stations)*n))]
		for station in range(len(stations)-1):
			for i in range(n):
				faces.append((n*station+i,n*station+(i+1)%n,n*(station+1)+(i+1)%n,n*(station+1)+i))
		self.part(verts,faces,0)
		# Continuous black rubber fender, kept inside the declared beam.
		self.rod((x-w*.93,f-.18,-.46*L),(x-w*.93,f-.18,.24*L),.065,4,4 if lod else 16)
		self.rod((x+w*.93,f-.18,-.46*L),(x+w*.93,f-.18,.24*L),.065,4,4 if lod else 16)

	def object(self, material, lod):
		mesh=bpy.data.meshes.new('PassengerCatamaranGeometry')
		mesh.from_pydata(self.v,[],self.f)
		mesh.update()
		uv=mesh.uv_layers.new(name='PaletteUV')
		for p,c in zip(mesh.polygons,self.colours):
			p.material_index=0 if lod else min(c,6)
			for i in p.loop_indices:
				uv.data[i].uv=((c+.5)/8,.5)
		bm=bmesh.new()
		bm.from_mesh(mesh)
		bm.faces.index_update()
		# Closed solids can be oriented by volume; independent window panes must keep their authored outside.
		bmesh.ops.recalc_face_normals(bm,faces=[f for f in bm.faces if not self.preserve_winding[f.index]])
		bmesh.ops.triangulate(bm,faces=bm.faces)
		bm.to_mesh(mesh)
		bm.free()
		for slot in ([material[0]] if lod else material[1]):
			mesh.materials.append(slot)
		obj=bpy.data.objects.new('Geometry',mesh)
		bpy.context.collection.objects.link(obj)
		if not lod:
			bpy.context.view_layer.objects.active=obj
			obj.select_set(True)
			bevel=obj.modifiers.new('Manufactured edge radii','BEVEL')
			bevel.width=.035;bevel.segments=2;bevel.limit_method='ANGLE';bevel.angle_limit=.55
			bpy.ops.object.modifier_apply(modifier=bevel.name)
			bm=bmesh.new();bm.from_mesh(obj.data)
			bmesh.ops.triangulate(bm,faces=bm.faces);bm.to_mesh(obj.data);bm.free()
			# Bevel interpolates the constant palette UVs with tiny evaluation-dependent float differences.
			# These faces have separate materials; restore their exact palette centre after evaluation.
			uv=obj.data.uv_layers.active
			for polygon in obj.data.polygons:
				for i in polygon.loop_indices:
					uv.data[i].uv=((polygon.material_index+.5)/8,.5)
		return obj


def build(d,lod,material):
	g=Geometry()
	L,B,F,H,C=d['length'],d['beam'],d['freeboard'],d['cabin_height'],d['cabin_length']
	roof=F+H
	fore, aft=.5*C-.04*L,-.5*C-.04*L
	for sign in (-1,1):
		g.hull(sign*.5*d['separation'],d,lod)
	# Cross-deck terminates exactly at the bow mount. Avoid rails beyond the contact extents.
	g.box((0,F-.16,0),(B-.02,.32,L),0)
	g.box((0,F+.015,0),(B-.22,.03,L-.16),1)
	g.box((0,F+H/2,-.04*L),(B-.65,H,C),6)
	g.box((0,roof+.07,-.04*L),(B-.4,.14,C+.25),6)
	# Cabin windows and vertical mullions, clear neutral glass strips.
	levels = [F+H*.5] if H<4 else [F+1.18,F+3.2]
	for y in levels:
		for sign in (-1,1):
			x=sign*((B-.65)/2+.008)
			g.box((x,y,-.04*L),(.015,1.17,C-.7),2)
			for i in range(1, (7 if lod else 12)):
				z=aft+.35+(C-.7)*i/(7 if lod else 12)
				g.box((x+sign*.008,y,z),(.02,1.23,.065),0)
		g.box((0,y,fore+.01),(B-1.2,1.17,.02),2)
		g.box((0,y,aft-.01),(B-1.3,1.17,.02),2)
	# Hollow wheelhouse. The glazing is single-sided outward: no opaque plane across the camera.
	E=d['eye_forward']; W=.55*B; z0=E-3.25; z1=E+1.25
	g.box((0,roof+.17,E-1),(W,.34,4.5),6)
	g.box((0,roof+2.25,E-1),(W+.35,.1,4.85),6)
	g.box((0,roof+.05,E-1),(W+.35,.1,4.85),1)
	g.part([(-W/2,roof+.36,z1),(W/2,roof+.36,z1),(W/2-.14,roof+2.2,z1-.18),(-W/2+.14,roof+2.2,z1-.18)],[(0,1,2,3)],2,preserve_winding=True)
	g.part([(-W/2,roof+.36,z0),(-W/2+.14,roof+2.2,z0+.18),(W/2-.14,roof+2.2,z0+.18),(W/2,roof+.36,z0)],[(0,1,2,3)],2,preserve_winding=True)
	for sign in (-1,1):
		g.part([(sign*W/2,roof+.36,z0),(sign*W/2,roof+.36,z1),(sign*(W/2-.14),roof+2.2,z1-.18),(sign*(W/2-.14),roof+2.2,z0+.18)],[(3,2,1,0) if sign==1 else (0,1,2,3)],2,preserve_winding=True)
		for z in (z0,z1):
			g.rod((sign*W/2,roof+.33,z),(sign*(W/2-.14),roof+2.2,z+(.18 if z==z0 else -.18)),.065,0,4)
	for x in (-W/6,W/6):
		g.rod((x,roof+.34,z1+.008),(x,roof+2.2,z1-.175),.035,0,4)
	# Upper aft passenger deck, seating, rails, and an open bow loading area.
	for sign in (-1,1):
		x=sign*(B/2-.2)
		g.rail((x,-.48*L),(x,aft-.1),F,lod)
		g.rail((x,fore+.2),(x,.48*L),F,lod)
		g.rail((x,aft),(x,z0-.25),roof+.14,lod)
		g.rail((x,.48*L),(sign*1.2,.48*L),F,lod)
		g.rail((x,-.48*L),(0,-.48*L),F,lod)
		for z in [aft+1,aft+3,aft+5]:
			if z<z0-.8:
				g.box((sign*(B*.27),roof+.55,z),(1.9,.13,.43),5)
				if not lod:
					g.box((sign*(B*.27),roof+.9,z-.23),(1.9,.6,.08),5)
		# Side landing bollards and longitudinal fender rub strips.
		for z in (-.43*L,.4*L):
			g.rod((sign*(B/2-.4),F,z),(sign*(B/2-.4),F+.3,z),.09,3,6)
			g.rod((sign*(B/2-.4)-.18,F+.29,z),(sign*(B/2-.4)+.18,F+.29,z),.065,3,6)
	g.rail((-B/2+.2,aft),(B/2-.2,aft),roof+.14,lod)
	g.box((0,F+.04,.5*L-.8),(2.3,.08,1.6),3)
	if not lod:
		for z in [(.5*L-.1)-i*.18 for i in range(9)]:
			g.box((0,F+.085,z),(2.15,.015,.025),5)
		# Aft companionway/stairs and roof ventilation, visual estimates only.
		stair_run = max(.6, .5*L + aft - .3)
		for i in range(11):
			g.box((-B*.33,F+.15+i*H/11,aft-stair_run+i*(stair_run-.12)/10),(.8,.12,.16),5)
		for x in (-B*.33-.43,-B*.33+.43):
			g.rod((x,F+1,aft-stair_run),(x,roof+1,aft-.12),.035,5,6)
		for z in (aft+1.3,aft+3.2):
			g.box((0,roof+.3,z),(.85,.32,.9),5)
			for i in range(5):
				g.box((-.3+i*.15,roof+.47,z),(.04,.025,.7),3)
	if not lod:
		# Stair stringers, posts, bench supports and passenger doors.
		for x in (-B*.33-.4,-B*.33+.4):
			g.rod((x,F,aft-stair_run),(x,roof,aft-.12),.075,5,12)
			g.rod((x,F,aft-stair_run),(x,F+1,aft-stair_run),.035,5,12)
			g.rod((x,roof,aft-.12),(x,roof+1,aft-.12),.035,5,12)
		for sign in (-1,1):
			for z in (aft+1,aft+3,aft+5):
				for offset in (-.65,.65):
					g.box((sign*B*.27+offset,roof+.3,z),(.12,.5,.35),5)
			# Curved forward rubber fender and its mounting strip.
			points=[(sign*(B/2-.13),F-.2,.23*L),(sign*(B/2-.3),F-.2,.34*L),(sign*(d['separation']/2+.48),F-.2,.43*L),(sign*d['separation']/2,F-.2,.495*L)]
			for a,b in zip(points,points[1:]):g.rod(a,b,.10,4,16)
			for z in (z0+1.3,z0+2.7):
				g.rod((sign*W/2,roof+.36,z),(sign*(W/2-.14),roof+2.2,z),.045,6,8)
			for z in (-.43*L,.4*L):
				g.box((sign*(B/2-.4),F+.055,z),(.5,.11,.4),5)
		# Dark glass door, metal surround and handles on both ends of the main saloon.
		for z in (aft-.025,fore+.025):
			g.box((0,F+1.18,z),(1.15,2.28,.045),3)
			g.box((0,F+1.48,z+(.03 if z>0 else -.03)),(.91,1.35,.025),2)
			for x in (-.57,.57):g.box((x,F+1.18,z),(.06,2.35,.085),5)
			g.rod((.4,F+.95,z+.06),(.4,F+1.25,z+.06),.025,5,12)
		# The ramp has a visible hinge, side plates, tread ribs and a light edge strip.
		g.rod((-1.1,F+.12,.5*L-1.5),(1.1,F+.12,.5*L-1.5),.08,5,16)
		for x in (-1.1,1.1):g.box((x,F+.13,.5*L-.8),(.09,.14,1.55),5)
		g.box((0,F+.11,.5*L-.045),(2.23,.06,.07),6)
		# Wheelhouse instruments are generic nonfunctional props below the downward sight line.
		for x in (-W*.33,W*.33):
			g.box((x,roof+.85,E+.65),(W*.21,.3,.45),3)
			g.box((x,roof+1.04,E+.65),(.65,.08,.3),2)
		for x in (-W/3,W/3):
			g.rod((x,roof+.87,z1+.025),(x+.15,roof+1.75,z1-.09),.018,4,8)
	# All lantern pedestals coincide with the established layout; colour belongs to SignalLight.
	m=mounts(d)
	g.rod((0,roof+2.3,E-1.18),(0,m['ForwardAnchor'][1]-.12,E-1.18),.06,5,6 if lod else 16)
	g.rod((-.7,roof+3,E-1),(.7,roof+3,E-1),.045,5,6)
	for name in ('Masthead','PortSidelight','StarboardSidelight','Sternlight'):
		x,y,z=m[name]
		g.rod((x,y-.5,z),(x,y-.14,z),.045,3,6)
		g.box((x,y-.12,z),(.12,.1,.12),5)
	return g.object(material,lod)


def palette(out):
	img=bpy.data.images.new('NeutralPalette',width=8,height=1,alpha=True)
	img.pixels=[c for rgba in PALETTE for c in rgba]
	img.filepath_raw=str(out/'NeutralPalette.png')
	img.file_format='PNG'
	img.save()
	mat=bpy.data.materials.new('NeutralFerry')
	mat.use_nodes=True
	nodes=mat.node_tree.nodes
	tex=nodes.new('ShaderNodeTexImage');tex.image=img;tex.interpolation='Closest'
	bsdf=nodes.get('Principled BSDF');bsdf.inputs['Roughness'].default_value=.35
	mat.node_tree.links.new(tex.outputs['Color'],bsdf.inputs['Base Color'])
	surfaces=[]
	for i,name in enumerate(('HullWhite','DeckGrey','WindowGlass','DarkMetal','BlackFender','RailMetal','CabinWhite')):
		surface=bpy.data.materials.new(name);surface.use_nodes=True
		shader=surface.node_tree.nodes.get('Principled BSDF')
		shader.inputs['Base Color'].default_value=PALETTE[i]
		shader.inputs['Roughness'].default_value=(.38,.85,.08,.45,.95,.22,.32)[i]
		shader.inputs['Metallic'].default_value=.8 if i==5 else .25 if i==3 else 0
		surfaces.append(surface)
	return (mat,surfaces)


def receipt(obj,d,m,debug=None):
	mesh=obj.data
	mesh.calc_loop_triangles()
	payload=dict(vertices=[[round(x,7) for x in v.co] for v in mesh.vertices],
		triangles=[list(t.vertices) for t in mesh.loop_triangles],
		normals=[[round(x,7) for x in l.vector] for l in mesh.corner_normals],
		uvs=[[round(x,7) for x in u.uv] for u in mesh.uv_layers.active.data],
		material_slots=[s.material.name for s in obj.material_slots],
		material_indices=[p.material_index for p in mesh.polygons])
	if debug:debug.write_text(json.dumps(payload,separators=(',',':'))+'\n')
	return dict(geometry_sha256=hashlib.sha256(json.dumps(payload,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
		triangles=len(mesh.loop_triangles),vertices=len(mesh.vertices),dimensions=d,mounts=m,
		bounds_blender=[list(min(v.co[i] for v in mesh.vertices) for i in range(3)),list(max(v.co[i] for v in mesh.vertices) for i in range(3))])


def renders(obj,name,out,d):
	scene=bpy.context.scene
	scene.render.engine='CYCLES';scene.cycles.samples=24
	scene.render.resolution_x=1200;scene.render.resolution_y=900;scene.render.resolution_percentage=100
	scene.world.color=(.2,.2,.2)
	scene.view_settings.view_transform='AgX'
	bpy.ops.object.light_add(type='AREA',location=(10,-6,24));key=bpy.context.object;key.data.energy=10000;key.data.shape='DISK';key.data.size=15
	bpy.ops.object.light_add(type='AREA',location=(-12,8,12));fill=bpy.context.object;fill.data.energy=5000;fill.data.size=12
	bpy.ops.object.camera_add();cam=bpy.context.object;scene.camera=cam
	cam.data.type='ORTHO'
	target=blender((0,3,0))
	for view,pos,scale in [('front',(0,7,d['length']*1.5),d['beam']*1.8),('side',(40,5,0),d['length']*1.2),('top',(0,60,.01),d['length']*1.2),('three-quarter',(30,24,38),d['length']*1.32)]:
		cam.location=blender(pos);cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
		rotation=cam.rotation_euler.to_matrix()
		projected=[rotation.transposed() @ (obj.matrix_world @ v.co - cam.location) for v in obj.data.vertices]
		x0,x1=min(v.x for v in projected),max(v.x for v in projected)
		y0,y1=min(v.y for v in projected),max(v.y for v in projected)
		cam.location += rotation @ Vector(((x0+x1)/2,(y0+y1)/2,0))
		cam.data.ortho_scale=max(x1-x0,(y1-y0)*scene.render.resolution_x/scene.render.resolution_y)*1.18
		scene.render.filepath=str(out/f'{name}-{view}.png');bpy.ops.render.render(write_still=True)
	for item in (key,fill,cam):
		bpy.data.objects.remove(item,do_unlink=True)


def main():
	p=argparse.ArgumentParser();p.add_argument('--out',type=Path,required=True);p.add_argument('--renders',type=Path);p.add_argument('--receipt-details',action='store_true')
	a=p.parse_args(sys.argv[sys.argv.index('--')+1:]);a.out.mkdir(parents=True,exist_ok=True)
	if a.renders:a.renders.mkdir(parents=True,exist_ok=True)
	bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
	mat=palette(a.out)
	manifest={'schema':'ferrysim-models/1','frame':'Unity x starboard, y up, z forward; Blender x starboard, y forward, z up','blender':bpy.app.version_string,'models':{}}
	for name in ('t-class','k-class'):
		d=dimensions(json.loads((ROOT/'vessels'/f'{name}.json').read_text()));m=mounts(d)
		folder=a.out/name;folder.mkdir(exist_ok=True)
		for lod in (0,1):
			obj=build(d,lod,mat)
			data=receipt(obj,d,m,folder/f'LOD{lod}-geometry.json' if a.receipt_details else None)
			assert data['triangles']<=(5000 if lod else 50000),data
			empty=[]
			for mount,pos in m.items():
				node=bpy.data.objects.new(mount,None);bpy.context.collection.objects.link(node);node.location=blender(pos);empty.append(node)
			bpy.ops.object.select_all(action='DESELECT');obj.select_set(True)
			for node in empty:node.select_set(True)
			bpy.context.view_layer.objects.active=obj
			fbx=folder/f'LOD{lod}.fbx'
			bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'MESH','EMPTY'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=True,use_mesh_modifiers=True,mesh_smooth_type='FACE',use_tspace=False,bake_anim=False,add_leaf_bones=False,path_mode='STRIP')
			data['fbx_sha256']=hashlib.sha256(fbx.read_bytes()).hexdigest();manifest['models'][f'{name}/LOD{lod}']=data
			print(name,lod,'triangles',data['triangles'],'geometry',data['geometry_sha256'])
			if a.renders:renders(obj,f'{name}-LOD{lod}',a.renders,d)
			for item in [obj]+empty:bpy.data.objects.remove(item,do_unlink=True)
	(a.out/'manifest.json').write_text(json.dumps(manifest,indent=2,sort_keys=True)+'\n')
	(a.out/'audited-classes.json').write_text(json.dumps(audited_classes(ROOT),indent=2,sort_keys=True)+'\n')


if __name__=='__main__':main()
