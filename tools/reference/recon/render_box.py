# Synthetic acceptance fixture adapted from the coordinator spike.
# Run: Blender --background --factory-startup -noaudio --python render_box.py -- <out-folder>
import math
import random
import sys

import bpy

out = sys.argv[sys.argv.index("--") + 1]
SIZE = (2.0, 1.0, 0.5)  # metres: x, y, z

bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

# Distinct deterministic textures on each face and the ground avoid false
# correspondences from reusing the same image on geometrically different surfaces.
# Only appearance is changed; the hidden check dimensions remain 2 x 1 x 0.5 m.
W = 512

def material(name, seed):
	rng = random.Random(seed)
	image = bpy.data.images.new(name + " texture", W, W)
	cell = 8
	grid = [[(rng.random(), rng.random(), rng.random()) for _ in range(W // cell)] for _ in range(W // cell)]
	pixels = []
	for y in range(W):
		for x in range(W):
			r, g, b = grid[y // cell][x // cell]
			pixels += [r, g, b, 1.0]
	image.pixels = pixels
	image.pack()
	m = bpy.data.materials.new(name)
	m.use_nodes = True
	tex = m.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = image
	tex.interpolation = "Closest"
	bsdf = m.node_tree.nodes["Principled BSDF"]
	bsdf.inputs["Roughness"].default_value = 1.0
	m.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
	return m


bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, SIZE[2] / 2))
box = bpy.context.object
box.scale = SIZE
bpy.ops.object.transform_apply(scale=True)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.uv.cube_project(cube_size=0.6)
bpy.ops.object.mode_set(mode="OBJECT")
for i, polygon in enumerate(box.data.polygons):
	box.data.materials.append(material(f"face-{i}", 7 + i))
	polygon.material_index = i

bpy.ops.mesh.primitive_plane_add(size=12.0, location=(0, 0, 0))
ground = bpy.context.object
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.uv.cube_project(cube_size=2.5)
bpy.ops.object.mode_set(mode="OBJECT")
ground.data.materials.append(material("ground", 1007))

sun = bpy.data.lights.new("sun", "SUN")
sun.energy = 3.0
sun_object = bpy.data.objects.new("sun", sun)
sun_object.rotation_euler = (math.radians(50), 0, math.radians(30))
scene.collection.objects.link(sun_object)
world = bpy.data.worlds.new("world")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.8, 0.85, 0.9, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.7
scene.world = world

camera_data = bpy.data.cameras.new("camera")
camera_data.lens = 28
camera = bpy.data.objects.new("camera", camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
target = bpy.data.objects.new("target", None)
target.location = (0, 0, SIZE[2] / 2)
scene.collection.objects.link(target)
track = camera.constraints.new("TRACK_TO")
track.target = target
track.track_axis = "TRACK_NEGATIVE_Z"
track.up_axis = "UP_Y"

scene.render.engine = "CYCLES"
scene.cycles.samples = 12
scene.cycles.use_denoising = False
scene.render.resolution_x = 1280
scene.render.resolution_y = 960
scene.render.image_settings.file_format = "JPEG"
scene.render.image_settings.quality = 95

index = 0
for height, radius, count in ((0.6, 4.0, 16), (1.6, 4.2, 12), (3.0, 3.6, 12)):
	for i in range(count):
		angle = 2 * math.pi * i / count + height
		camera.location = (radius * math.cos(angle), radius * math.sin(angle), height)
		scene.render.filepath = f"{out}/f_{index:04d}.jpg"
		bpy.ops.render.render(write_still=True)
		index += 1
print(f"rendered {index} views of a {SIZE} m box")
