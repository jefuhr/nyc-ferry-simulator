"""Original neutral wheelhouse geometry from provenance-bearing layout values.

Unity metres throughout scene_spec: x starboard, y up, z forward from midships/DWL.
Blender is a build dependency only. No private image is loaded or embedded by this script.
Screen mount/slot names: RadarScreen, PlotterScreen, PortEngineScreen, StarboardEngineScreen.
Eye is the elevated seated helm eye; EyeStanding is a second inspection pose.
HelmFFU and twin engine handles animate. NFU jog levers stay neutral because the core
has no independent jog demand or selected steering mode. Never infer that from rudder state.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
COLOURS = {'Console':(.045,.052,.058,1), 'Trim':(.43,.47,.49,1), 'Lining':(.40,.42,.40,1),
	'Floor':(.075,.085,.09,1), 'Seat':(.027,.032,.036,1), 'Panel':(.13,.16,.18,1),
	'Red':(.48,.025,.012,1), 'Amber':(.66,.40,.035,1), 'Indicator':(.12,.36,.25,1),
	'LampsSurface':(.12,.18,.12,1), 'Dial':(.72,.75,.69,1),
	'RadarScreen':(.018,.045,.04,1), 'PlotterScreen':(.018,.04,.055,1),
	'PortEngineScreen':(.018,.035,.04,1), 'StarboardEngineScreen':(.018,.035,.04,1)}


def load_layout(path, hull_path=None):
	path = Path(path)
	doc = json.loads(path.read_text())
	if doc['schema_version'] not in (1,2) or doc['kind'] != 'wheelhouse': raise ValueError('Unsupported wheelhouse layout')
	layout = {k:v['value'] for k,v in doc['values'].items() if 'value' in v}
	layout['schema_version'] = doc['schema_version']
	for collection in ('screens','controls'):
		layout[collection] = [{k:(v['value'] if isinstance(v,dict) else v) for k,v in item.items()} for item in doc.get(collection,[])]
	canonical = Path(hull_path) if hull_path is not None else path.with_name(doc['subject']+'-hull.json')
	if hull_path is not None or canonical.exists():
		hull = json.loads(canonical.read_text())
		if hull['schema_version'] != 1 or hull['kind'] != 'hull' or hull['subject'] != doc['subject']:
			raise ValueError('Hull layout does not match wheelhouse')
		# Hull values also contain named groups (outlines, light mounts); read only scalar datums.
		datums = {k:v['value'] for k,v in hull['values'].items() if 'value' in v}
		layout['_hull_source'] = str(canonical)
	else:
		# The coordinator permits reference-import snapshots until the exterior hull files are synced.
		# They retain the canonical source path/confidence; do not silently invent replacement datums.
		datums = layout
		layout['_hull_source'] = 'wheelhouse snapshot of '+str(canonical.name)+' (live hull file unavailable)'
	keys = ('upper_deck_height','wheelhouse_roof_height','wheelhouse_fore_z','wheelhouse_aft_z','wheelhouse_width')
	if any(k not in datums or not isinstance(datums[k],(int,float)) or not math.isfinite(datums[k]) for k in keys):
		raise ValueError('Missing/nonfinite canonical hull datums')
	floor,roof,front,aft,width = [datums[k] for k in keys]
	if roof <= floor or front <= aft or width <= 0: raise ValueError('Invalid hull enclosure')
	layout.update({k:datums[k] for k in keys})
	layout['floor_origin'] = [0,floor,(front+aft)/2]
	layout['footprint'] = [width,roof-floor,front-aft]
	layout['console_size'][0] = width-2*layout['console_side_clearance']
	validate_layout(layout)
	return layout


def validate_layout(d):
	required = ('footprint','floor_origin','console_size','panel_height','eye_seated','eye_standing','window_sill','engine_display_size','navigation_display_size','lever_height','lever_travel','helm_travel','seat_size','port_engine_screen','starboard_engine_screen','plotter_screen','radar_screen','port_lever','starboard_lever','helm_ffu','port_nfu','starboard_nfu','helm_seat','companion_seat','console_side_clearance')
	if any(k not in d for k in required): raise ValueError('Incomplete wheelhouse layout')
	for k in required:
		values = d[k] if isinstance(d[k],list) else [d[k]]
		if not all(isinstance(v,(int,float)) and math.isfinite(v) for v in values): raise ValueError('Nonfinite layout: '+k)
	for k in ('footprint','console_size','engine_display_size','navigation_display_size','seat_size'):
		if len(d[k]) != 3 or min(d[k]) <= 0: raise ValueError('Invalid size: '+k)
	for k in ('floor_origin','eye_seated','eye_standing'):
		if len(d[k]) != 3: raise ValueError('Invalid position: '+k)
	if not 0 < d['console_size'][1] < d['eye_seated'][1] < d['footprint'][1]: raise ValueError('Eye/console must fit wheelhouse')

	ids = set(); lamps = set()
	def vector(v, size, label):
		if not isinstance(v,list) or len(v) != size or not all(isinstance(n,(int,float)) and math.isfinite(n) for n in v):
			raise ValueError('Invalid '+label)
	def inside(p,label):
		vector(p,3,label)
		if not (-d['footprint'][0]/2 <= p[0] <= d['footprint'][0]/2 and 0 <= p[1] <= d['footprint'][1] and -d['footprint'][2] <= p[2] <= 0):
			raise ValueError('Outside wheelhouse '+label)
	for c in d.get('controls',[]):
		name=c['id']
		if not name or name in ids: raise ValueError('Duplicate control id')
		ids.add(name);inside(c['pivot'],name+' pivot')
		if c['kind'] not in ('lever','jog','wheel','button','switch','lamp','needle','card'): raise ValueError('Unknown control kind')
		for field in ('axis','normal'):
			if field in c:
				vector(c[field],3,field)
				if abs(dot(c[field],c[field])-1) > 1e-5: raise ValueError('Non-unit '+field)
		if 'travel' in c:
			vector(c['travel'],2,'travel')
			if not c['travel'][0] <= 0 <= c['travel'][1]: raise ValueError('Invalid travel')
		if c['kind'] == 'lamp':
			index=c.get('lamp')
			if not isinstance(index,int) or index < 0 or index in lamps: raise ValueError('Invalid/duplicate lamp index')
			lamps.add(index)
	for screen in d.get('screens',[]):
		name=screen['id']
		if not name or name in ids: raise ValueError('Duplicate screen id')
		ids.add(name);inside(screen['centre'],name+' centre');inside(screen['focus_eye'],name+' focus_eye')
		vector(screen['size'],2,'screen size')
		if min(screen['size']) <= 0: raise ValueError('Invalid screen size')
		for field in ('tilt','yaw'):
			if not isinstance(screen.get(field,0),(int,float)) or not math.isfinite(screen.get(field,0)): raise ValueError('Invalid screen '+field)
		if math.sqrt(dot(sub(screen['focus_eye'],screen['centre']),sub(screen['focus_eye'],screen['centre']))) < .45:
			raise ValueError('Screen focus eye too close')


def scene_spec(d):
	"""Stable low-poly triangle soup grouped by surface/moving mount, independent of bpy."""
	validate_layout(d)
	meshes=[]; mounts={}; groups={}; rotations={}
	active_lamp=None; shape_transform=None
	def part(name, vertices, faces, mat, parent=None):
		key=(parent,mat)
		if key not in groups:
			groups[key]={'name':name if parent else ('LampsSurface' if mat=='LampsSurface' else mat+'Geometry'),'vertices':[],'triangles':[],'material':mat,'parent':parent}
			meshes.append(groups[key])
		if shape_transform: vertices=[shape_transform(v) for v in vertices]
		p=groups[key]; offset=len(p['vertices']); p['vertices'].extend(vertices)
		if active_lamp is not None: p.setdefault('uv1',[]).extend([[active_lamp,0] for _ in vertices])
		for f in faces:
			for i in range(1,len(f)-1): p['triangles'].append([offset+f[0],offset+f[i],offset+f[i+1]])
	def box(name,c,s,mat='Console',parent=None):
		x,y,z=c; a,b,e=[v/2 for v in s]
		v=[[x+dx*a,y+dy*b,z+dz*e] for dx,dy,dz in [(-1,-1,-1),(1,-1,-1),(1,-1,1),(-1,-1,1),(-1,1,-1),(1,1,-1),(1,1,1),(-1,1,1)]]
		part(name,v,[(0,3,2,1),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7),(4,5,6,7)],mat,parent)
	def rod(name,a,b,r,mat='Trim',parent=None,sides=16):
		axis=sub(b,a); axis=mul(axis,1/math.sqrt(dot(axis,axis)))
		u=cross(axis,(0,1,0))
		if dot(u,u)<.001: u=cross(axis,(1,0,0))
		u=mul(u,1/math.sqrt(dot(u,u))); v=cross(axis,u)
		vertices=[add(p,mul(add(mul(u,math.cos(i*2*math.pi/sides)),mul(v,math.sin(i*2*math.pi/sides))),r)) for p in (a,b) for i in range(sides)]
		faces=[tuple(range(sides-1,-1,-1)),tuple(range(sides,2*sides))]+[(i,(i+1)%sides,(i+1)%sides+sides,i+sides) for i in range(sides)]
		part(name,vertices,faces,mat,parent)
	w,h,l=d['footprint']; _,floor,centre=d['floor_origin']; front=centre+l/2; aft=centre-l/2
	def position(key):
		x,y,z=d[key];return [x,floor+y,front+z]
	cw,ch,cd=d['console_size']; top=floor+ch; z=front-cd/2-.08; upper=top+d['panel_height']/2
	for name,key in [('Eye','eye_seated'),('EyeStanding','eye_standing')]:
		mounts[name]=position(key)
	box('Floor',(0,floor-.04,centre),(w,.08,l),'Floor')
	box('Ceiling',(0,floor+h+.035,centre),(w,.07,l),'Lining')
	box('Aft bulkhead',(0,floor+h/2,aft),(w,h,.06),'Lining')
	# Open forward and side windows; no opaque fake glazing that hides the outside world.
	for x in (-w/2,w/2):
		box('Side lining',(x,floor+.48,centre),(.055,.96,l),'Lining')
		for pz in (aft+.6,centre+.3,front): box('Window post',(x,floor+h/2,pz),(.055,h,.055),'Console')
		box('Window sill',(x,floor+d['window_sill'],centre),(.09,.045,l),'Console')
	for x in (-w/2,-w/3,w/3,w/2): box('Forward mullion',(x,floor+h/2,front),(.045,h,.045),'Console')
	box('Window header',(0,floor+h-.1,front),(w,.12,.08),'Console')
	box('Console cabinet',(0,floor+ch/2,z),(cw,ch,cd),'Console')
	box('Console top',(0,top,z-.04),(cw+.03,.045,cd+.12),'Console')
	box('Front edge trim',(0,top-.025,z-cd/2-.09),(cw+.04,.035,.035),'Trim')
	if not d.get('screens'): box('Upper panel',(0,upper,front-.12),(cw,d['panel_height'],.13),'Console')
	if not d.get('screens'):
		# Paired engine displays; separate screen slots with UVs suitable for future RenderTextures.
		for key,name in [('port_engine_screen','PortEngineScreen'),('starboard_engine_screen','StarboardEngineScreen')]:
			x,ey,ez=position(key);s=d['engine_display_size']; pos=(x,ey,ez+.026)
			box(name+' bezel',pos,s,'Panel')
			mounts[name]=[x,ey,ez]
			box(name,(x,ey,ez-.003),(s[0]*.72,s[1]*.72,.004),name,name)
			rod('E-stop guard',(x+.20,upper-.07,front-.21),(x+.20,upper-.07,front-.238),.041,'Amber')
			rod('E-stop',(x+.20,upper-.07,front-.239),(x+.20,upper-.07,front-.265),.027,'Red')
		for key,name in [('plotter_screen','PlotterScreen'),('radar_screen','RadarScreen')]:
			x,ey,ez=position(key);s=d['navigation_display_size']; pos=[x,ey,ez+.035]
			box(name+' bezel',pos,(s[0]+.035,s[1],s[2]),'Console')
			mounts[name]=[x,ey,ez]
			box(name,(x,ey,ez-.003),(s[0],s[1]-.035,.004),name,name)
		# Steering alarm panel at port, lower alarms/breakers and blank generic indicator rows.
		for x in (-cw*.30,-cw*.21):
			box('Steering alarm',(x,top+.025,z-.10),(.19,.025,.17),'Panel')
			for i in range(4): box('Alarm light',(x-.06+i*.04,top+.04,z-.12),(.012,.007,.014),'Indicator')
		for x in (-.68,-.23,.23,.68):
			box('Lower panel',(x,floor+.52,z-cd/2-.008),(.32,.58,.018),'Panel')
			for row in range(8):
				box('Breaker label',(x,floor+.28+row*.066,z-cd/2-.022),(.22,.014,.005),'Trim')
				rod('Breaker',(x+.105,floor+.28+row*.066,z-cd/2-.026),(x+.105,floor+.28+row*.066,z-cd/2-.04),.009,'Console',sides=8)
		# Compass dome approximated by stacked turned profiles, deliberately without logos or fake readings.
		for radius,dy in [(.105,0),(.09,.035),(.07,.065),(.04,.09)]:
			rod('Compass',(0,top+d['panel_height']+dy,front-.14),(0,top+d['panel_height']+dy+.03,front-.14),radius,'Console')
		# Engine control head pivots are local rotations about +x; positive pitch moves the grip forward.
		pa=position('port_lever');sa=position('starboard_lever');throttle_x=(pa[0]+sa[0])/2;control_z=pa[2]
		box('Twin head',(throttle_x,top+.065,control_z),(.24,.13,.19),'Trim')
		for key,name in [('port_lever','PortLever'),('starboard_lever','StarboardLever')]:
			x,ly,lz=position(key);mounts[name]=[x,ly,lz]
			rod(name,(x,ly,lz),(x,ly+d['lever_height'],lz),.014,'Trim',name)
			rod(name+' grip',(x-.04,ly+d['lever_height'],lz),(x+.04,ly+d['lever_height'],lz),.025,'Seat',name)
		for key,name,size in [('helm_ffu','HelmFFU',.23),('port_nfu','PortNFU',.13),('starboard_nfu','StarboardNFU',.13)]:
			x,ly,lz=position(key)
			rod(name+' base',(x,ly-.055,lz),(x,ly-.005,lz),.065,'Console')
			mounts[name]=[x,ly,lz]
			rod(name,(x,ly,lz),(x,ly+.02,lz-size),.014,'Trim',name)
			rod(name+' grip',(x,ly+.02,lz-size+.025),(x,ly+.02,lz-size-.025),.025,'Seat',name)
	else:
		# Individual screen housings replace the opaque common upper panel, which otherwise
		# intersects the tilted faces. Static housings remain merged by material.
		for screen in d['screens']:
			name=screen['id']; c=screen['centre']; pos=[c[0],floor+c[1],front+c[2]]
			mounts[name]=pos; rotations[name]=[screen['tilt'],screen.get('yaw',0),0]
			f=screen['focus_eye'];mounts[name+'FocusEye']=[f[0],floor+f[1],front+f[2]]
			sw,sh=screen['size']; border=.025
			shape_transform=lambda p: add(pos,screen_vector(p,screen))
			for x in (-sw/2-border/2,sw/2+border/2): box(name+' bezel',(x,0,.012),(border,sh+2*border,.025),'Panel')
			for y in (-sh/2-border/2,sh/2+border/2): box(name+' bezel',(0,y,.012),(sw,border,.025),'Panel')
			box(name+' back',(0,0,.04),(sw+2*border,sh+2*border,.025),'Console')
			part(name,[[-sw/2,-sh/2,0],[-sw/2,sh/2,0],[sw/2,sh/2,0],[sw/2,-sh/2,0]],[(0,1,2,3)],name,name)
			shape_transform=None
		# Keep the existing lower panel and breaker positions; each breaker is now a control.
		for x in (-.68,-.23,.23,.68):
			box('Lower panel',(x,floor+.52,z-cd/2-.008),(.32,.58,.018),'Panel')
			for row in range(8): box('Breaker label',(x,floor+.28+row*.066,z-cd/2-.022),(.22,.014,.005),'Trim')
		for x in (-cw*.30,-cw*.21): box('Steering alarm',(x,top+.025,z-.10),(.19,.025,.17),'Panel')
		pa=position('port_lever');sa=position('starboard_lever')
		box('Twin head',((pa[0]+sa[0])/2,top+.065,pa[2]),(.24,.13,.19),'Trim')
		for control in d['controls']:
			name=control['id'];kind=control['kind'];c=control['pivot'];p=[c[0],floor+c[1],front+c[2]];mounts[name]=p
			n=control.get('normal',[0,1,0]);x,ly,lz=p
			# A solid mount under raised components replaces their former support in the
			# common panel. It does not move the provenance-bearing control pivot.
			if kind=='card':
				rod('Compass pedestal',(x,top,lz),(x,ly-.013,lz),.075,'Console',sides=24)
			elif (name.endswith('EngineAlarmLamp') or 'EmergencyStop' in name or name.endswith('EngineKeySwitch')) and ly>top+.025:
				height=ly-top
				box('Control support',(x,top+height/2,lz+.025),(.06,height,.04),'Console')
			if name in ('PortLever','StarboardLever'):
				rod(name,p,(x,ly+d['lever_height'],lz),.014,'Trim',name)
				rod(name+' grip',(x-.04,ly+d['lever_height'],lz),(x+.04,ly+d['lever_height'],lz),.025,'Seat',name)
			elif kind in ('lever','jog'):
				size=.23 if name=='HelmFFU' else .13
				rod(name+' base',(x,ly-.055,lz),(x,ly-.005,lz),.065,'Console')
				rod(name,p,(x,ly+.02,lz-size),.014,'Trim',name)
				rod(name+' grip',(x,ly+.02,lz-size+.025),(x,ly+.02,lz-size-.025),.025,'Seat',name)
			elif kind=='lamp':
				active_lamp=control['lamp']
				rod('Lamps',p,add(p,mul(n,.006)),.009,'LampsSurface',sides=12)
				active_lamp=None
			elif kind in ('needle','card'):
				radius=.066 if kind=='needle' else .09
				rod(name+' rim',sub(p,mul(n,.012)),sub(p,mul(n,.003)),radius+.008,'Trim',sides=32)
				rod(name+' dial',sub(p,mul(n,.002)),p,radius,'Dial',name if kind=='card' else None,sides=32)
				# A simple generic dial, with marks and an independent needle/card. No claimed calibration.
				for tick in range(12):
					angle=math.radians(tick*30);u=[math.sin(angle),0,math.cos(angle)]
					rod('Dial tick',add(p,add(mul(u,radius*.82),mul(n,.002))),add(p,add(mul(u,radius*.94),mul(n,.002))),.0015,'Console',name if kind=='card' else None,sides=4)
				if kind=='needle':
					part(name,[[x-.003,ly+.004,lz-.012],[x+.003,ly+.004,lz-.012],[x,ly+.004,lz+radius*.77]],[(0,2,1)],'Red',name)
				else: rod('Compass index',(x,ly+.005,lz+radius),(x,ly+.005,lz+radius+.018),.003,'Red')
			else:
				radius=.027 if 'EmergencyStop' in name else (.009 if name.startswith('Breaker') else .014)
				if 'EmergencyStop' in name: rod(name+' guard',sub(p,mul(n,.015)),p,.041,'Amber')
				rod(name+' base',sub(p,mul(n,.008)),p,radius*1.3,'Panel')
				rod(name,p,add(p,mul(n,.025 if kind=='switch' else .012)),radius,'Red' if control.get('colour')=='red' else 'Trim',name,sides=12)
	# Starboard VHFs with handset/speaker detail, on the wing return.
	for dz in (0,.24):
		x=cw/2-.12; rz=z-.45-dz
		box('VHF',(x,top+.045,rz),(.20,.07,.16),'Panel')
		box('VHF display',(x-.035,top+.083,rz),(.09,.004,.075),'Indicator')
		for i in range(5): box('Speaker slot',(x+.05+i*.008,top+.084,rz),(.003,.004,.075),'Console')
		box('Handset',(x-.14,top+.06,rz),(.065,.055,.18),'Seat')
	for x in (-cw/2+.12,cw/2-.12): box('Wing return',(x,top-.04,z-.5),(.28,.06,.6),'Console')
	# Raised pilot chair, plus an aft companion chair. Neither puts a headrest through an eye mount.
	for key in ('helm_seat','companion_seat'):
		x,seat_y,sz=position(key);sx,sy,sd=d['seat_size']
		rod('Seat pedestal',(x,floor,sz),(x,seat_y,sz),.055,'Trim')
		box('Seat cushion',(x,seat_y,sz),(sx,sy,sd),'Seat')
		box('Seat back',(x,seat_y+.3,sz-.26),(sx,.6,.09),'Seat')
		for side in (-1,1):
			rod('Arm support',(x+side*.29,seat_y,sz-.15),(x+side*.29,seat_y+.24,sz-.15),.018,'Trim')
			box('Arm pad',(x+side*.29,seat_y+.25,sz),(.085,.06,.38),'Seat')
	# Mesh vertices become relative to their mount, keeping animation pivots correct through FBX import.
	for p in meshes:
		if p['parent']:
			angles=rotations.get(p['parent'],[0,0,0])
			p['vertices']=[rotate(rotate(sub(v,mounts[p['parent']]),[0,1,0],-angles[1]),[1,0,0],-angles[0]) for v in p['vertices']]
	return {'meshes':meshes,'mounts':mounts,'rotations':rotations,'controls':d.get('controls',[]),'screens':d.get('screens',[]),'animation_degrees':{'engine':d['lever_travel'],'helm':d['helm_travel']}}


def add(a,b): return [a[i]+b[i] for i in range(3)]
def sub(a,b): return [a[i]-b[i] for i in range(3)]
def mul(a,s): return [v*s for v in a]
def dot(a,b): return sum(a[i]*b[i] for i in range(3))
def cross(a,b): return [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]]


def rotate(v,axis,degrees):
	"""Unity positive axis-angle convention; +x rotates an up-pointing grip forward."""
	a=math.radians(degrees);c=math.cos(a);s=math.sin(a)
	return add(add(mul(v,c),mul(cross(axis,v),s)),mul(axis,dot(axis,v)*(1-c)))


def screen_vector(v,screen):
	return rotate(rotate(v,[1,0,0],screen['tilt']),[0,1,0],screen.get('yaw',0))


def world_vertices(scene,part):
	angles=scene.get('rotations',{}).get(part['parent'],[0,0,0])
	offset=scene['mounts'].get(part['parent'],[0,0,0])
	return [add(rotate(rotate(v,[1,0,0],angles[0]),[0,1,0],angles[1]),offset) for v in part['vertices']]


def occluders(scene,eye,target):
	"""Two-sided segment/triangle test (Moller-Trumbore), in vessel metres."""
	blocked=[]; direction=sub(target,eye)
	for p in scene['meshes']:
		vertices=world_vertices(scene,p)
		for tri in p['triangles']:
			a,b,c=[vertices[i] for i in tri]; e1=sub(b,a); e2=sub(c,a); h=cross(direction,e2); det=dot(e1,h)
			if abs(det)<1e-10: continue
			s=sub(eye,a); u=dot(s,h)/det
			if u<0 or u>1: continue
			q=cross(s,e1); v=dot(direction,q)/det; t=dot(e2,q)/det
			if v>=0 and u+v<=1 and 1e-5<t<1-1e-5:
				blocked.append(p['name']);break
	return blocked


def export(scene,path):
	import bpy
	import bmesh
	from mathutils import Matrix
	bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
	materials={}
	for name,rgba in COLOURS.items():
		mat=bpy.data.materials.new(name);mat.diffuse_color=rgba;mat.use_nodes=True
		bsdf=mat.node_tree.nodes.get('Principled BSDF');bsdf.inputs['Base Color'].default_value=rgba
		bsdf.inputs['Roughness'].default_value=.26 if name=='Trim' else .62
		bsdf.inputs['Metallic'].default_value=.75 if name=='Trim' else 0
		if 'Screen' in name:
			bsdf.inputs['Emission Color'].default_value=rgba;bsdf.inputs['Emission Strength'].default_value=.18
		materials[name]=mat
	mounts={}
	for name,pos in scene['mounts'].items():
		node=bpy.data.objects.new(name,None);bpy.context.collection.objects.link(node);node.location=(pos[0],pos[2],pos[1]);mounts[name]=node
		if name in scene['rotations']:
			a=scene['rotations'][name]; screen={'tilt':a[0],'yaw':a[1]}
			columns=[screen_vector(v,screen) for v in ([1,0,0],[0,0,1],[0,1,0])]
			node.rotation_euler=Matrix([[v[i] for v in columns] for i in (0,2,1)]).to_euler()
	for p in scene['meshes']:
		mesh=bpy.data.meshes.new(p['name']);mesh.from_pydata([(v[0],v[2],v[1]) for v in p['vertices']],[],[list(reversed(f)) for f in p['triangles']]);mesh.update()
		mesh.materials.append(materials[p['material']]);uv=mesh.uv_layers.new(name='SurfaceUV')
		# All display fronts point aft. Map their x/y extent to [0,1] without a baked screenshot.
		mins=[min(v[i] for v in p['vertices']) for i in (0,1)]; spans=[max(v[i] for v in p['vertices'])-mins[i] for i in (0,1)]
		for poly in mesh.polygons:
			for idx in poly.loop_indices:
				v=p['vertices'][mesh.loops[idx].vertex_index]
				uv.data[idx].uv=tuple((v[i]-mins[i])/max(spans[i],.001) for i in (0,1))
		if 'uv1' in p:
			indices=mesh.uv_layers.new(name='LampIndex')
			for idx,loop in enumerate(mesh.loops): indices.data[idx].uv=p['uv1'][loop.vertex_index]
			mesh.uv_layers.active_index=0
		bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
		obj=bpy.data.objects.new('LampsSurface' if p['material']=='LampsSurface' else p['name']+'Surface',mesh);bpy.context.collection.objects.link(obj)
		if p['parent']:obj.parent=mounts[p['parent']]
	bpy.ops.object.select_all(action='SELECT')
	# FBX otherwise embeds wall-clock time in its header. Fix only that export field,
	# restoring the exporter immediately; never change the installed Blender package.
	from io_scene_fbx import export_fbx_bin, fbx_utils
	from datetime import datetime
	original_header=export_fbx_bin.fbx_header_elements
	original_uuid=fbx_utils._key_to_uuid
	old_keys=fbx_utils._keys_to_uuids.copy();old_uuids=fbx_utils._uuids_to_keys.copy()
	fbx_utils._keys_to_uuids.clear();fbx_utils._uuids_to_keys.clear()
	def stable_uuid(existing,key):
		# Blender uses process-randomized hash(str) here. Content-based IDs also keep
		# exports identical across fresh Blender processes, not just repeat builds.
		value=int.from_bytes(hashlib.sha256(str(key).encode()).digest()[:8],'big') & ((1<<63)-1)
		if value in existing and existing[value] != key: raise ValueError('FBX ID collision')
		return fbx_utils.UUID(value)
	fbx_utils._key_to_uuid=stable_uuid
	export_fbx_bin.fbx_header_elements=lambda root,data,time=None: original_header(root,data,time=datetime(2000,1,1))
	try:
		bpy.ops.export_scene.fbx(filepath=str(path),use_selection=True,object_types={'MESH','EMPTY'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_space_transform=True,use_mesh_modifiers=True,mesh_smooth_type='FACE',use_tspace=False,bake_anim=False,add_leaf_bones=False,path_mode='STRIP')
	finally:
		export_fbx_bin.fbx_header_elements=original_header
		fbx_utils._key_to_uuid=original_uuid
		fbx_utils._keys_to_uuids.clear();fbx_utils._keys_to_uuids.update(old_keys)
		fbx_utils._uuids_to_keys.clear();fbx_utils._uuids_to_keys.update(old_uuids)

	# Hash the actual evaluated vertices, normals, UVs, transform hierarchy and material assignments.
	actual=[]
	for obj in sorted(bpy.context.scene.objects,key=lambda o:o.name):
		record={'name':obj.name,'parent':obj.parent.name if obj.parent else None,'matrix':[round(v,7) for row in obj.matrix_local for v in row]}
		if obj.type=='MESH':
			mesh=obj.to_mesh();mesh.calc_loop_triangles()
			record.update(vertices=[[round(c,7) for c in v.co] for v in mesh.vertices],triangles=[list(t.vertices) for t in mesh.loop_triangles],normals=[[round(c,7) for c in n.vector] for n in mesh.corner_normals],uv=[[[round(c,7) for c in v.uv] for v in layer.data] for layer in mesh.uv_layers],materials=[m.name for m in mesh.materials],slots=[p.material_index for p in mesh.polygons])
			obj.to_mesh_clear()
		actual.append(record)
	return {'triangles':sum(len(p['triangles']) for p in scene['meshes']),'vertices':sum(len(p['vertices']) for p in scene['meshes']),'renderers':len(scene['meshes']),'materials':sorted({p['material'] for p in scene['meshes']}),'mounts':scene['mounts'],'rotations':scene['rotations'],'controls':scene['controls'],'animation_degrees':scene['animation_degrees'], 'geometry_sha256':hashlib.sha256(json.dumps(actual,sort_keys=True).encode()).hexdigest(),'fbx_sha256':hashlib.sha256(path.read_bytes()).hexdigest()}


def render_views(scene,out,cls):
	import bpy
	from mathutils import Vector
	s=bpy.context.scene;s.render.engine='CYCLES';s.cycles.device='CPU';s.cycles.samples=16
	s.render.resolution_x=1280;s.render.resolution_y=800;s.render.resolution_percentage=100
	s.world.use_nodes=True;s.view_settings.view_transform='AgX'
	bpy.ops.object.camera_add();cam=bpy.context.object;s.camera=cam;cam.data.lens=18;cam.data.clip_start=.03
	bpy.ops.object.light_add(type='AREA');light=bpy.context.object;light.data.size=4
	out.mkdir(parents=True,exist_ok=True)
	views=[('Eye','Eye',(0,1,-1.20)),('EyeStanding','EyeStanding',(0,1,-1.20))]
	for screen in scene.get('screens',[]):
		name=screen['id'];direction=sub(scene['mounts'][name],scene['mounts'][name+'FocusEye'])
		views.append((name+'FocusEye',name+'FocusEye',(direction[0],direction[2],direction[1])))
	for name,mount,direction in views:
		cam.data.lens=32 if name.endswith('FocusEye') else 18
		p=scene['mounts'][mount];cam.location=(p[0],p[2],p[1]);cam.rotation_euler=Vector(direction).to_track_quat('-Z','Y').to_euler()
		light.location=(0,p[2]+3,p[1]+1.5);light.rotation_euler=Vector((0,-1,-.5)).to_track_quat('-Z','Y').to_euler()
		for night in (False,True):
			s.world.node_tree.nodes['Background'].inputs[0].default_value=(.012,.018,.035,1) if night else (.45,.56,.68,1)
			s.world.node_tree.nodes['Background'].inputs[1].default_value=.15 if night else .6
			light.data.energy=35 if night else 600
			s.render.filepath=str(out/f'{cls}-{name}-{"night" if night else "day"}.png')
			bpy.ops.render.render(write_still=True)


def main():
	p=argparse.ArgumentParser();p.add_argument('--out',type=Path,required=True);p.add_argument('--receipt',type=Path,required=True);p.add_argument('--renders',type=Path);p.add_argument('--hull-data',type=Path,help='Optional canonical hull-layout directory; defaults to the wheelhouse layout directory')
	a=p.parse_args(sys.argv[sys.argv.index('--')+1:]);receipts={}
	for cls in ('k-class','t-class'):
		# Clear datablocks between classes so material names do not acquire .001 suffixes.
		import bpy
		bpy.ops.wm.read_factory_settings(use_empty=True)
		layout=load_layout(ROOT/f'art/blender/data/{cls}-wheelhouse.json',a.hull_data/f'{cls}-hull.json' if a.hull_data else None)
		scene=scene_spec(layout);folder=a.out/cls;folder.mkdir(parents=True,exist_ok=True)
		assert sum(len(x['triangles']) for x in scene['meshes'])<=150000
		receipts[cls]=export(scene,folder/'Interior.fbx')
		receipts[cls]['layout_sha256']=hashlib.sha256(json.dumps(layout,sort_keys=True).encode()).hexdigest()
		receipts[cls]['hull_source']=layout['_hull_source']
		receipts[cls]['hull_datums']={k:layout[k] for k in ('upper_deck_height','wheelhouse_roof_height','wheelhouse_fore_z','wheelhouse_aft_z','wheelhouse_width')}
		if a.renders:
			if bpy.context.scene.world is None:bpy.context.scene.world=bpy.data.worlds.new('World')
			render_views(scene,a.renders,cls)
	manifest={'schema':'ferrysim-interiors/2','models':{cls+'/Interior':r for cls,r in receipts.items()}}
	a.receipt.parent.mkdir(parents=True,exist_ok=True);a.receipt.write_text(json.dumps(manifest,indent=2,sort_keys=True)+'\n')
	print(json.dumps(receipts,indent=2))

if __name__=='__main__':main()
