"""Build layout-driven passenger catamarans, export FBX and geometry receipts.
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
from dimensions import dimensions, mounts, audited_classes, load_layout

ROOT = Path(__file__).resolve().parents[2]


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

	def rail(self, a, b, base, lod, d):
		length = (Vector(b)-Vector(a)).length
		posts = max(1, math.ceil(length / (d['rail_spacing'] * (2 if lod else 1))))
		for i in range(posts+1):
			x, z = (a[j]+(b[j]-a[j])*i/posts for j in range(2))
			self.rod((x,base,z),(x,base+d['rail_height'],z),.035,5,4 if lod else 12)
		for h in ([d['rail_height']] if lod else [d['rail_height']/2,d['rail_height']]):
			self.rod((a[0],base+h,a[1]),(b[0],base+h,b[1]),.035,5,4 if lod else 12)

	def prism(self, outline, lower, upper, colour):
		# An outline is ordered around a horizontal footprint; elevations may follow sheer.
		n = len(outline)
		verts = [(x, y(z) if callable(y) else y, z) for y in (lower, upper) for x, z in outline]
		faces = [tuple(range(n-1,-1,-1)), tuple(range(n,2*n))]
		faces += [(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
		self.part(verts, faces, colour)

	def upper_deck(self, d):
		# Shared-vertex slab cells leave real companionway openings, without overlapping faces.
		outline=list(d['upper_outline'].values())
		holes=[(start[0]-d['stair_clear_width']/2,start[0]+d['stair_clear_width']/2,
			min(start[2],end[2])-.15,max(start[2],end[2])+.15)
			for start,end in zip(d['stair_start'].values(),d['stair_end'].values())]
		cuts=sorted({p[1] for p in outline} | {z for hole in holes for z in hole[2:] if outline[0][1]<z<outline[-1][1]})
		xcuts=sorted({x for hole in holes for x in hole[:2]})
		def width(z):
			for (wa,za),(wb,zb) in zip(outline,outline[1:]):
				if z<=zb:return wa+(wb-wa)*(z-za)/(zb-za)
			return outline[-1][0]
		verts=[];indices={};tops=[];edges={}
		def vertex(x,z):
			key=(round(x,7),round(z,7))
			if key not in indices:
				indices[key]=len(verts);verts.append((x,d['upper_deck_height'],z))
			return indices[key]
		for za,zb in zip(cuts,cuts[1:]):
			wa,wb=width(za),width(zb)
			fixed=[x for x in xcuts if -min(wa,wb)<x<min(wa,wb)]
			xa,xb=[-wa]+fixed+[wa],[-wb]+fixed+[wb]
			for i in range(len(xa)-1):
				x=(xa[i]+xa[i+1]+xb[i]+xb[i+1])/4;z=(za+zb)/2
				if any(x0<x<x1 and z0<z<z1 for x0,x1,z0,z1 in holes):continue
				face=(vertex(xa[i],za),vertex(xb[i],zb),vertex(xb[i+1],zb),vertex(xa[i+1],za))
				tops.append(face)
				for a,b in zip(face,face[1:]+face[:1]):
					key=tuple(sorted((a,b)));edges[key]=edges.get(key,[])+[(a,b)]
		n=len(verts)
		verts += [(x,y-d['deck_thickness'],z) for x,y,z in verts[:]]
		faces=tops+[tuple(i+n for i in reversed(face)) for face in tops]
		faces += [(a,a+n,b+n,b) for uses in edges.values() if len(uses)==1 for a,b in uses]
		self.part(verts,faces,6)

	def hull(self, x, d, lod):
		stations = list(d['hull_stations'].values())
		section = list(d['hull_section'].values())
		n = len(section)
		verts = [(x+dx*width, keel+(sheer(d,z)-d['deck_thickness']-keel)*fraction, z)
			for z,width,keel in stations for dx,fraction in section]
		faces = [tuple(range(n-1,-1,-1)), tuple(range((len(stations)-1)*n,len(stations)*n))]
		faces += [(n*j+i,n*j+(i+1)%n,n*(j+1)+(i+1)%n,n*(j+1)+i)
			for j in range(len(stations)-1) for i in range(n)]
		self.part(verts, faces, 0)

	def object(self, material, lod):
		mesh=bpy.data.meshes.new('PassengerCatamaranGeometry')
		mesh.from_pydata(self.v,[],self.f)
		mesh.update()
		uv=mesh.uv_layers.new(name='PaletteUV')
		for p,c in zip(mesh.polygons,self.colours):
			p.material_index=0 if lod else min(c,6)
			for i in p.loop_indices:
				uv.data[i].uv=((c+.5)/7,.5)
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
					uv.data[i].uv=((polygon.material_index+.5)/7,.5)
		return obj


def mirrored(points):
	return list(points) + [(-x,z) for x,z in reversed(points)]


def sheer(d, z):
	points = list(d['sheer_profile'].values())
	for (za,ya),(zb,yb) in zip(points,points[1:]):
		if z <= zb:
			return d['freeboard']+ya+(yb-ya)*max(0,(z-za)/(zb-za))
	return d['freeboard']+points[-1][1]


def build(d,lod,material):
	g = Geometry()
	F,U,R = d['freeboard'],d['upper_deck_height'],d['wheelhouse_roof_height']
	thick = d['deck_thickness']
	main,upper,cabin = (mirrored(list(d[key].values())) for key in ('main_outline','upper_outline','cabin_outline'))
	for sign in (-1,1):
		g.hull(sign*d['separation']/2,d,lod)
	# Measured underwing, shaped deck edges and explicit sheer; no windage-driven boxes.
	g.prism(main,d['underwing_height'],lambda z:sheer(d,z)-thick,0)
	g.prism(main,lambda z:sheer(d,z)-thick,lambda z:sheer(d,z),1)
	g.prism(cabin,F,U-thick,6)
	g.upper_deck(d)
	# Saloon windows follow each side-wall segment, with white mullions.
	wb,wt = d['window_bottom'],d['window_top']
	for sign in (-1,1):
		points=list(d['cabin_outline'].values())
		for a,b in zip(points,points[1:]):
			x0,z0=sign*(a[0]+.012),a[1];x1,z1=sign*(b[0]+.012),b[1]
			g.part([(x0,wb,z0),(x1,wb,z1),(x1,wt,z1),(x0,wt,z0)],
				[(0,3,2,1) if sign==1 else (0,1,2,3)],2,preserve_winding=True)
			count=max(1,round((z1-z0)/(points[-1][1]-points[0][1])*d['window_bays']))
			for i in range(count+1):
				t=i/count;x=x0+(x1-x0)*t;z=z0+(z1-z0)*t
				g.rod((x,wb,z),(x,wt,z),.035,6,4)
	# Wheelhouse block is independent of the eye. Interior controls are owned elsewhere.
	w,z0,z1=d['wheelhouse_half_beam'],d['wheelhouse_aft'],d['wheelhouse_forward']
	outline=mirrored([(w,z0),(w,z1-.65),(w-.30,z1)])
	g.prism(outline,U,U+d['wheelhouse_sill_height'],6)
	g.prism(outline,R-.10,R,6)
	inset=d['wheelhouse_roof_inset'];sill=U+d['wheelhouse_sill_height']
	for a,b in zip(outline,outline[1:]+outline[:1]):
		def top(point):
			x,z=point
			return (x-math.copysign(inset,x),R-.1,z+inset if z==z0 else z-inset)
		va=(a[0],sill,a[1]);vb=(b[0],sill,b[1]);ta,tb=top(a),top(b)
		g.part([va,vb,tb,ta],[(0,3,2,1)],2,preserve_winding=True)
		g.rod(va,ta,.045,6,4)
		# Subdivide the forward windscreen without an opaque plane across the eye.
		if a[1]==z1 and b[1]==z1:
			for ratio in (.33,.67):
				g.rod(tuple(va[j]+(vb[j]-va[j])*ratio for j in range(3)),
					tuple(ta[j]+(tb[j]-ta[j])*ratio for j in range(3)),.03,6,4)
	# Sponson volume and rubber strip follow the explicit centreline control points.
	for sign in (-1,1):
		points=list(d['sponson_path'].values())
		for a,b in zip(points,points[1:]):
			a=(sign*a[0],a[1],a[2]);b=(sign*b[0],b[1],b[2])
			g.rod(a,b,d['sponson_depth']/2,0,4)
			g.rod((a[0]+sign*d['fender_outboard_offset'],a[1],a[2]),(b[0]+sign*d['fender_outboard_offset'],b[1],b[2]),d['fender_radius'],4,4 if lod else 12)
	# Deck rails follow polygon edges; bow gate opening is left in the centre.
	for points,base in ((main,F),(upper,U)):
		for a,b in zip(points,points[1:]+points[:1]):
			if a[1]==b[1] and a[1]==d['length']/2:
				for end in (a,b):
					g.rail((end[0]-math.copysign(.12,end[0]),end[1]-.1),(math.copysign(d['bow_gate_half_width'],end[0]),end[1]-.1),base,lod,d)
				continue
			pa=(a[0]-math.copysign(.12,a[0]),a[1]);pb=(b[0]-math.copysign(.12,b[0]),b[1])
			# No aft/fore rail outside the LOA envelope.
			pa=(pa[0],min(d['length']/2-.10,max(-d['length']/2+.10,pa[1])))
			pb=(pb[0],min(d['length']/2-.10,max(-d['length']/2+.10,pb[1])))
			if points is main and pa[1] != pb[1]:
				# Saloon walls bound the passenger space; side rails belong only to exposed aft/fore decks.
				aft,fore=cabin[0][1],max(point[1] for point in cabin)
				cuts=sorted([0.,1.]+[(z-pa[1])/(pb[1]-pa[1]) for z in (aft,fore) if 0<(z-pa[1])/(pb[1]-pa[1])<1])
				for t0,t1 in zip(cuts,cuts[1:]):
					mid=pa[1]+(pb[1]-pa[1])*(t0+t1)/2
					if aft<mid<fore:continue
					g.rail(tuple(pa[j]+(pb[j]-pa[j])*t0 for j in range(2)),tuple(pa[j]+(pb[j]-pa[j])*t1 for j in range(2)),base,lod,d)
			else:g.rail(pa,pb,base,lod,d)
	# Two aft stairs are explicit endpoints, never derived from windage area.
	for start,end in zip(d['stair_start'].values(),d['stair_end'].values()):
		steps=12 if not lod else 6
		for i in range(steps+1):
			pos=[start[j]+(end[j]-start[j])*i/steps for j in range(3)]
			g.box(pos,(.85,.10,.25),5)
		for side in (-.47,.47):
			g.rod((start[0]+side,start[1],start[2]),(end[0]+side,end[1],end[2]),.055,5,4)
			g.rod((start[0]+side,start[1]+1,start[2]),(end[0]+side,end[1]+1,end[2]),.03,5,4)
	# Raked exhaust enclosures are part of the observed aft superstructure silhouette.
	for x,y,z in d['exhaust_stacks'].values():
		w,h,base,top,rake=(d[key] for key in ('exhaust_width','exhaust_height','exhaust_base_length','exhaust_top_length','exhaust_rake'))
		profile=[(y,z-base/2),(y,z+base/2),(y+h,z+rake+top/2),(y+h,z+rake-top/2)]
		g.part([(x+side*w/2,py,pz) for side in (-1,1) for py,pz in profile],
			[(0,3,2,1),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],6)
	# Neutral ancillary fittings; dimensions remain explicitly visual detail estimates.
	if not lod:
		for z in (-7,-4,-1):
			for x in (-2.2,2.2):
				g.box((x,U+.48,z),(1.5,.12,.45),5)
				g.box((x,U+.78,z-.23),(1.5,.55,.08),5)
		for sign in (-1,1):
			for z in (-d['length']/2+1.3,d['length']/2-2):
				x=sign*(d['beam']/2-.65)
				g.rod((x,F,z),(x,F+.30,z),.08,3,8)
				g.rod((x-.15,F+.30,z),(x+.15,F+.30,z),.05,3,8)
	w,h,run=d['boarding_threshold_size']
	g.box((0,F+h/2,d['length']/2-run/2),(w,h,run),3)
	# Navigation mounts remain explicit estimates, constrained by placement tests.
	m=mounts(d);mast=m['Masthead'];anchor=m['ForwardAnchor']
	g.rod((mast[0],R,mast[2]),(anchor[0],anchor[1]-.12,anchor[2]),.06,5,6 if lod else 12)
	for name in ('Masthead','PortSidelight','StarboardSidelight','Sternlight'):
		x,y,z=m[name]
		g.rod((x,y-.5,z),(x,y-.14,z),.045,3,6)
		g.box((x,y-.12,z),(.12,.1,.12),5)
	return g.object(material,lod)


def palette(out, surfaces_data):
	colours=[tuple(m['colour'])+(1.,) for m in surfaces_data]
	img=bpy.data.images.new('NeutralPalette',width=len(colours),height=1,alpha=True)
	img.pixels=[c for rgba in colours for c in rgba]
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
	for i,source in enumerate(surfaces_data):
		name=source['name']
		surface=bpy.data.materials.new(name);surface.use_nodes=True
		shader=surface.node_tree.nodes.get('Principled BSDF')
		shader.inputs['Base Color'].default_value=colours[i]
		shader.inputs['Roughness'].default_value=1-source['smoothness']
		shader.inputs['Metallic'].default_value=source['metal']
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


def renders(obj,name,out,d,views=None):
	scene=bpy.context.scene
	scene.render.engine='CYCLES';scene.cycles.device='CPU';scene.cycles.samples=24
	scene.render.resolution_x=1200;scene.render.resolution_y=450 if name=='comparison-profile' else 900;scene.render.resolution_percentage=100
	scene.world.color=(.2,.2,.2)
	scene.view_settings.view_transform='AgX'
	bpy.ops.object.light_add(type='AREA',location=(10,-6,24));key=bpy.context.object;key.data.energy=10000;key.data.shape='DISK';key.data.size=15
	bpy.ops.object.light_add(type='AREA',location=(-12,8,12));fill=bpy.context.object;fill.data.energy=5000;fill.data.size=12
	bpy.ops.object.camera_add();cam=bpy.context.object;scene.camera=cam
	cam.data.type='ORTHO'
	target=blender((0,3,0))
	for view,pos,scale in views or [('front',(0,7,d['length']*1.5),d['beam']*1.8),('side',(40,3,0),d['length']*1.2),('top',(0,60,0),d['length']*1.2),('three-quarter',(30,24,38),d['length']*1.32)]:
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
	surfaces=load_layout('t-class')['materials']
	assert surfaces==load_layout('k-class')['materials'], 'Shared neutral palette must match'
	mat=palette(a.out,surfaces)
	manifest={'schema':'ferrysim-models/2','frame':'Unity x starboard, y up, z forward; Blender x starboard, y forward, z up','blender':bpy.app.version_string,'models':{}}
	for name in ('t-class','k-class'):
		d=dimensions(json.loads((ROOT/'vessels'/f'{name}.json').read_text()));m=mounts(d)
		folder=a.out/name;folder.mkdir(exist_ok=True)
		for lod in (0,1):
			obj=build(d,lod,mat)
			data=receipt(obj,d,m,folder/f'LOD{lod}-geometry.json' if a.receipt_details else None)
			assert data['triangles']<=(5000 if lod else 80000),data
			empty=[]
			for mount,pos in m.items():
				node=bpy.data.objects.new(mount,None);bpy.context.collection.objects.link(node);node.location=blender(pos);empty.append(node)
			bpy.ops.object.select_all(action='DESELECT');obj.select_set(True)
			for node in empty:node.select_set(True)
			bpy.context.view_layer.objects.active=obj
			fbx=folder/f'LOD{lod}.fbx'
			bpy.ops.export_scene.fbx(filepath=str(fbx),use_selection=True,object_types={'MESH','EMPTY'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=True,use_mesh_modifiers=True,mesh_smooth_type='FACE',use_tspace=False,bake_anim=False,add_leaf_bones=False,path_mode='STRIP')
			data['layout_sha256']=hashlib.sha256((ROOT/'art/blender/data'/f'{name}-hull.json').read_bytes()).hexdigest()
			data['materials']=surfaces
			data['fbx_sha256']=hashlib.sha256(fbx.read_bytes()).hexdigest();manifest['models'][f'{name}/LOD{lod}']=data
			print(name,lod,'triangles',data['triangles'],'geometry',data['geometry_sha256'])
			if a.renders and lod==0:renders(obj,f'{name}-LOD{lod}',a.renders,d)
			for item in [obj]+empty:bpy.data.objects.remove(item,do_unlink=True)
	if a.renders:
		# Render the two original models together; no reference images or compositing.
		for view,offset,position in (('profile',(0,0,18),(80,3,0)),('plan',(5.5,0,0),(0,90,0))):
			objects=[]
			for sign,name in ((-1,'t-class'),(1,'k-class')):
				d=dimensions(json.loads((ROOT/'vessels'/f'{name}.json').read_text()))
				obj=build(d,0,mat);obj.location=blender(tuple(sign*x for x in offset));objects.append(obj)
			bpy.ops.object.select_all(action='DESELECT')
			for obj in objects:obj.select_set(True)
			bpy.context.view_layer.objects.active=objects[0]
			bpy.ops.object.join();obj=bpy.context.object
			renders(obj,'comparison-'+view,a.renders,d,[(view,position,1)])
			bpy.data.objects.remove(obj,do_unlink=True)
	(a.out/'manifest.json').write_text(json.dumps(manifest,indent=2,sort_keys=True)+'\n')
	(a.out/'audited-classes.json').write_text(json.dumps(audited_classes(ROOT),indent=2,sort_keys=True)+'\n')


if __name__=='__main__':main()
