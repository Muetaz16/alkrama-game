# Tripo building -> game-ready landmark: decimate to a triangle budget, sit it on the ground (min y = 0) centred on
# x/z, keep its textures, export GLB, and render a front + side preview to check it.
#   blender -b --factory-startup -P Tools/blender/prep_hero.py -- <in.glb> <out.glb> <preview.png> [tris=40000]
import bpy, sys, math
from mathutils import Vector
a = sys.argv[sys.argv.index("--") + 1:]
src, dst, png = a[0], a[1], a[2]; budget = int(a[3]) if len(a) > 3 else 40000
for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
bpy.ops.object.select_all(action="DESELECT")
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1: bpy.ops.object.join()
ob = bpy.context.view_layer.objects.active
bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
tris0 = sum(len(p.vertices) - 2 for p in ob.data.polygons)
if tris0 > budget:
    m = ob.modifiers.new("dec", "DECIMATE"); m.ratio = budget / tris0; m.use_collapse_triangulate = True
    bpy.ops.object.modifier_apply(modifier=m.name)
tris1 = sum(len(p.vertices) - 2 for p in ob.data.polygons)
# on the ground, centred (Blender z up)
vs = [ob.matrix_world @ v.co for v in ob.data.vertices]
mn = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs))); mx = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
ob.location -= Vector(((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, mn.z))
bpy.ops.object.transform_apply(location=True)
# Tripo puts the long, photographed facade facing +x; turn it to face -y, the same way as the kit pieces
# (Blender -y ends up as +z in Unity), so the game can face it at the street
from mathutils import Matrix
ob.data.transform(Matrix.Rotation(math.radians(-90), 4, "Z")); ob.data.update()
size = Vector((mx.y - mn.y, mx.x - mn.x, mx.z - mn.z))
for o in bpy.data.objects:
    if o != ob: bpy.data.objects.remove(o, do_unlink=True)
ob.name = "hero"; ob.data.name = "hero"
bpy.ops.object.select_all(action="DESELECT"); ob.select_set(True)
bpy.ops.export_scene.gltf(filepath=dst, export_format="GLB", use_selection=True, export_yup=True, export_image_format="JPEG", export_jpeg_quality=90)
print(f"[hero] {src}: {tris0} -> {tris1} tris, size x {size.x:.2f} (width) y {size.y:.2f} (depth) z {size.z:.2f} (height) -> {dst}")

# preview: front (looking along +y) and side (along -x), material colours
s = bpy.context.scene
s.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE"
world = bpy.data.worlds.new("w"); s.world = world; world.color = (0.6, 0.62, 0.66)
sun = bpy.data.lights.new("s", "SUN"); sun.energy = 3; so = bpy.data.objects.new("s", sun); s.collection.objects.link(so)
so.rotation_euler = (math.radians(55), 0, math.radians(30))
cam = bpy.data.cameras.new("c"); cam.type = "ORTHO"; co = bpy.data.objects.new("c", cam); s.collection.objects.link(co); s.camera = co
big = max(size.x, size.y, size.z)
cam.ortho_scale = big * 1.15
s.render.resolution_x, s.render.resolution_y = 900, 900
for name, loc, rot in [("front", (0, -big * 3, size.z / 2), (math.radians(90), 0, 0)), ("side", (big * 3, 0, size.z / 2), (math.radians(90), 0, math.radians(90))), ("persp", (-big * 1.8, -big * 2.2, size.z * 0.9), (math.radians(75), 0, math.radians(-40)))]:
    co.location = loc; co.rotation_euler = rot
    s.render.filepath = png.replace(".png", f"_{name}.png")
    bpy.ops.render.render(write_still=True)
print("[hero] previews", png)
