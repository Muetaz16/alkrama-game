# Renders the kit pieces side by side (workbench, material colours) to check them by eye.
#   blender -b --factory-startup -P Tools/blender/preview_kit.py -- <kit.glb> <out.png>
import bpy, sys, math
from mathutils import Vector
a = sys.argv[sys.argv.index("--") + 1:]
for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)
bpy.ops.import_scene.gltf(filepath=a[0])
order = ["win_italian", "win_closed", "win_modern", "win_broken", "balcony_rail", "balcony_solid", "plain",
         "shop", "shop_open", "door", "arch", "dmg_hole0", "dmg_hole1", "dmg_hole2", "dmg_top0", "dmg_top1"]
objs = {o.name.split(".")[0]: o for o in bpy.data.objects if o.type == "MESH"}
for i, n in enumerate(order):
    o = objs.get(n)
    if not o: continue
    o.location = Vector(((i % 8) * 4.6, 0, -(i // 8) * 4.2))
for n in ("parapet", "band", "base", "corner"):
    if n in objs: objs[n].location = Vector((0, 50, 0))
cam = bpy.data.cameras.new("c"); co = bpy.data.objects.new("c", cam); bpy.context.collection.objects.link(co)
cam.type = "ORTHO"; cam.ortho_scale = 38
co.location = Vector((16.1, -40, -0.6)); co.rotation_euler = (math.radians(84), 0, math.radians(-4))
bpy.context.scene.camera = co
sun = bpy.data.lights.new("s", "SUN"); so = bpy.data.objects.new("s", sun); bpy.context.collection.objects.link(so)
so.rotation_euler = (math.radians(50), math.radians(-20), math.radians(-30))
s = bpy.context.scene
s.render.engine = "BLENDER_WORKBENCH"
s.display.shading.light = "STUDIO"; s.display.shading.color_type = "MATERIAL"
s.display.shading.show_shadows = True; s.display.shading.show_cavity = True
s.render.resolution_x, s.render.resolution_y = 1800, 1000
s.render.filepath = a[1]
bpy.ops.render.render(write_still=True)
print("[preview] saved", a[1])
