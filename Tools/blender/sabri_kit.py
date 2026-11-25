# Sabri building kit: real geometry for the facades of Benghazi's old centre, made by script.
#   "C:\Program Files\Blender Foundation\Blender 5.3\blender.exe" -b --factory-startup -P Tools/blender/sabri_kit.py -- <out.glb>
#
# Every piece is one facade bay of the web city's grid: 4 m wide (x -2..2), one floor 3.2 m tall (z 0..3.2).
# The back of a piece (y = 0) sits on the existing building wall, the front face is at y = -T and things that
# stick out (frames, sills, balconies) go further to -y. Unity lays the pieces along every wall of a building
# (CityDetail.Kit.cs), stretching them a little to fit the wall length and the building height.
#
# Materials are named Kit_* and swapped for the project's URP materials in Unity, so only their names matter.
# UVs are a box projection in metres (1 UV unit = 2 m) so every texture keeps one scale across all pieces.
import bpy, bmesh, math, random, sys
from mathutils import Matrix, Vector

OUT = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else "sabri_kit.glb"
T = 0.22          # facade skin thickness
W2 = 2.0          # half bay width
H = 3.2           # floor height

MATS = ["Plaster", "Trim", "Glass", "Frame", "Shutter", "Metal", "Dark", "Block", "ShopShutter", "Sign", "Door", "Rebar", "Stone"]
COLORS = {"Plaster": (0.85, 0.8, 0.7), "Trim": (0.8, 0.76, 0.68), "Glass": (0.08, 0.09, 0.1), "Frame": (0.8, 0.8, 0.78),
          "Shutter": (0.25, 0.4, 0.3), "Metal": (0.12, 0.12, 0.12), "Dark": (0.02, 0.02, 0.02), "Block": (0.55, 0.5, 0.45),
          "ShopShutter": (0.55, 0.55, 0.56), "Sign": (0.6, 0.15, 0.1), "Door": (0.35, 0.25, 0.17), "Rebar": (0.35, 0.2, 0.12),
          "Stone": (0.55, 0.52, 0.47)}

for o in list(bpy.data.objects): bpy.data.objects.remove(o, do_unlink=True)
mats = {}
for n in MATS:
    m = bpy.data.materials.new("Kit_" + n)
    try:
        m.use_nodes = True
        bsdf = m.node_tree.nodes.get("Principled BSDF")
        if bsdf: bsdf.inputs["Base Color"].default_value = (*COLORS[n], 1)
    except Exception:
        pass
    m.diffuse_color = (*COLORS[n], 1)
    mats[n] = m


class Piece:
    def __init__(self, name):
        self.name = name; self.bm = bmesh.new(); self.used = []

    def mi(self, mat):
        if mat not in self.used: self.used.append(mat)
        return self.used.index(mat)

    # an axis-aligned box from (x0, y0, z0) to (x1, y1, z1), optionally turned by matrix m about its own centre
    def box(self, x0, x1, y0, y1, z0, z1, mat, m=None):
        if x1 - x0 < 1e-4 or y1 - y0 < 1e-4 or z1 - z0 < 1e-4: return
        c = Vector(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)); h = Vector(((x1 - x0) / 2, (y1 - y0) / 2, (z1 - z0) / 2))
        vs = []
        for i in range(8):
            p = Vector((h.x * (1 if i & 1 else -1), h.y * (1 if i & 2 else -1), h.z * (1 if i & 4 else -1)))
            if m is not None: p = m @ p
            vs.append(self.bm.verts.new(c + p))
        idx = self.mi(mat)
        for f in [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)]:
            face = self.bm.faces.new([vs[k] for k in f]); face.material_index = idx

    def finish(self):
        bm = self.bm
        bm.normal_update()
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        uv = bm.loops.layers.uv.new("UVMap")
        for f in bm.faces:
            n = f.normal; ax = max(range(3), key=lambda k: abs(n[k]))
            for l in f.loops:
                p = l.vert.co
                u, v = (p.y, p.z) if ax == 0 else (p.x, p.z) if ax == 1 else (p.x, p.y)
                l[uv].uv = (u * 0.5, v * 0.5)
        me = bpy.data.meshes.new(self.name); bm.to_mesh(me); bm.free()
        for n in self.used: me.materials.append(mats[n])
        ob = bpy.data.objects.new(self.name, me); bpy.context.collection.objects.link(ob)
        return ob


# ---------------------------------------------------------------- parts
def wall_with_opening(p, ox0, ox1, oz0, oz1, mat="Plaster"):
    p.box(-W2, ox0, -T, 0, 0, H, mat)
    p.box(ox1, W2, -T, 0, 0, H, mat)
    p.box(ox0, ox1, -T, 0, 0, oz0, mat)
    p.box(ox0, ox1, -T, 0, oz1, H, mat)

def surround(p, ox0, ox1, oz0, oz1, heavy=True):
    d = 0.06 if heavy else 0.03
    p.box(ox0 - 0.16, ox0, -T - d, -T + 0.01, oz0 - 0.05, oz1 + 0.05, "Trim")
    p.box(ox1, ox1 + 0.16, -T - d, -T + 0.01, oz0 - 0.05, oz1 + 0.05, "Trim")
    if heavy:
        p.box(ox0 - 0.26, ox1 + 0.26, -T - 0.1, -T + 0.01, oz1 + 0.05, oz1 + 0.27, "Trim")          # lintel
        p.box(ox0 - 0.32, ox1 + 0.32, -T - 0.16, -T + 0.01, oz1 + 0.27, oz1 + 0.34, "Trim")         # its cap
    else:
        p.box(ox0 - 0.16, ox1 + 0.16, -T - d, -T + 0.01, oz1 + 0.05, oz1 + 0.17, "Trim")

def sill(p, ox0, ox1, z):
    p.box(ox0 - 0.12, ox1 + 0.12, -T - 0.12, -T + 0.04, z - 0.09, z, "Trim")

def casement(p, ox0, ox1, oz0, oz1, transom=True, glass=True, depth=-0.08):
    if glass: p.box(ox0, ox1, depth - 0.01, depth + 0.01, oz0, oz1, "Glass")
    else: p.box(ox0, ox1, -0.03, -0.01, oz0, oz1, "Dark")
    f = 0.05; y0, y1 = depth - 0.03, depth + 0.02
    p.box(ox0, ox0 + f, y0, y1, oz0, oz1, "Frame"); p.box(ox1 - f, ox1, y0, y1, oz0, oz1, "Frame")
    p.box(ox0, ox1, y0, y1, oz0, oz0 + f, "Frame"); p.box(ox0, ox1, y0, y1, oz1 - f, oz1, "Frame")
    if glass: p.box((ox0 + ox1) / 2 - f / 2, (ox0 + ox1) / 2 + f / 2, y0, y1, oz0, oz1, "Frame")
    if transom: p.box(ox0, ox1, y0, y1, oz1 - 0.5, oz1 - 0.45, "Frame")

def shutter_leaf(p, x0, x1, z0, z1, y, m=None, slats=7):
    # louvred wooden leaf: a frame and tilted slats; m turns the whole leaf about its own centre
    cx = (x0 + x1) / 2; cz = (z0 + z1) / 2
    def B(a0, a1, b0, b1, c0, c1, mm=None):
        if m is None: p.box(a0, a1, b0, b1, c0, c1, "Shutter", mm); return
        cc = Vector(((a0 + a1) / 2 - cx, (b0 + b1) / 2 - y, (c0 + c1) / 2 - cz)); nc = m @ cc
        dx = nc - cc
        p.box(a0 + dx.x, a1 + dx.x, b0 + dx.y, b1 + dx.y, c0 + dx.z, c1 + dx.z, "Shutter", m if mm is None else m @ mm)
    f = 0.05
    B(x0, x0 + f, y - 0.02, y + 0.02, z0, z1); B(x1 - f, x1, y - 0.02, y + 0.02, z0, z1)
    B(x0, x1, y - 0.02, y + 0.02, z0, z0 + f); B(x0, x1, y - 0.02, y + 0.02, z1 - f, z1)
    tilt = Matrix.Rotation(math.radians(35), 3, "X")
    for k in range(slats):
        z = z0 + f + (k + 0.5) * (z1 - z0 - 2 * f) / slats
        B(x0 + f, x1 - f, y - 0.012, y + 0.012, z - 0.06, z + 0.06, tilt)

def open_shutters(p, ox0, ox1, oz0, oz1):
    w = (ox1 - ox0) / 2
    y = -T - 0.06
    shutter_leaf(p, ox0 - 0.17 - w, ox0 - 0.17, oz0, oz1, y)
    shutter_leaf(p, ox1 + 0.17, ox1 + 0.17 + w, oz0, oz1, y)

def railing(p, x0, x1, y_front, y_back, z0, h=1.0):
    p.box(x0, x1, y_front - 0.03, y_front + 0.03, z0 + h - 0.05, z0 + h, "Metal")
    p.box(x0, x1, y_front - 0.02, y_front + 0.02, z0 + 0.06, z0 + 0.1, "Metal")
    n = int((x1 - x0) / 0.13)
    for k in range(n + 1):
        x = x0 + k * (x1 - x0) / max(n, 1)
        p.box(x - 0.012, x + 0.012, y_front - 0.012, y_front + 0.012, z0 + 0.06, z0 + h, "Metal")
    for x in (x0, x1):
        p.box(x - 0.025, x + 0.025, y_front, y_back, z0 + h - 0.05, z0 + h, "Metal")
        m = int((y_back - y_front) / 0.13)
        for k in range(1, m):
            y = y_front + k * (y_back - y_front) / m
            p.box(x - 0.012, x + 0.012, y - 0.012, y + 0.012, z0 + 0.06, z0 + h, "Metal")

# a wall bay laid out of rows, with an irregular hole blown through it: the plaster skin is broken back further
# than the block wall behind, so the edge shows the blocks; behind the hole it is dark
def damaged_wall(p, seed, hole_c, hole_r, top_cut=None):
    rnd = random.Random(seed)
    rows = 32; dz = H / rows
    hx, hz = hole_c
    for r in range(rows):
        z0, z1 = r * dz, (r + 1) * dz; zc = (z0 + z1) / 2
        spans = [(-W2, W2)]
        dzr = zc - hz
        rr = hole_r * (1 + 0.25 * math.sin(r * 1.7 + seed)) if hole_r > 0 else 0
        if abs(dzr) < rr:
            half = math.sqrt(rr * rr - dzr * dzr) * (0.85 + 0.3 * rnd.random())
            spans = [(-W2, hx - half), (hx + half, W2)]
        if top_cut is not None:
            lim = top_cut(zc)
            if lim is None: continue
            spans = [(max(a, lim[0]), min(b, lim[1])) for a, b in spans]
        for a, b in spans:
            if b - a < 0.05: continue
            p.box(a, b, -T + 0.05, 0, z0, z1, "Block")
            ea, eb = a, b
            if a > -W2 + 0.01: ea = a + rnd.uniform(0.04, 0.22)
            if b < W2 - 0.01: eb = b - rnd.uniform(0.04, 0.22)
            if eb - ea > 0.03: p.box(ea, eb, -T, -T + 0.05, z0, z1, "Plaster")
    if hole_r > 0:
        p.box(max(-W2, hx - hole_r * 1.4), min(W2, hx + hole_r * 1.4), -0.02, -0.005, max(0, hz - hole_r * 1.4), min(H, hz + hole_r * 1.4), "Dark")
        for k in range(6):                                                  # rebar across the hole
            z = hz + rnd.uniform(-hole_r, hole_r) * 0.8; L = rnd.uniform(0.3, 0.8)
            x0 = hx + rnd.choice([-1, 1]) * hole_r * 0.9
            m = Matrix.Rotation(math.radians(rnd.uniform(-40, 40)), 3, "Y")
            p.box(x0 - L / 2 if x0 < hx else x0, x0 if x0 < hx else x0 + L / 2, -T * 0.5 - 0.006, -T * 0.5 + 0.006, z - 0.006, z + 0.006, "Rebar", m)


# ---------------------------------------------------------------- the pieces
pieces = []
def piece(name):
    p = Piece(name); pieces.append(p); return p

OW, SZ, TZ = 1.3, 0.95, 2.75          # window width, sill, head

p = piece("plain"); p.box(-W2, W2, -T, 0, 0, H, "Plaster")

p = piece("win_italian")                          # tall window, stone frame, louvred shutters folded back
wall_with_opening(p, -OW / 2, OW / 2, SZ, TZ); surround(p, -OW / 2, OW / 2, SZ, TZ); sill(p, -OW / 2, OW / 2, SZ)
casement(p, -OW / 2, OW / 2, SZ, TZ); open_shutters(p, -OW / 2, OW / 2, SZ, TZ)

p = piece("win_closed")                           # shutters shut in the opening
wall_with_opening(p, -OW / 2, OW / 2, SZ, TZ); surround(p, -OW / 2, OW / 2, SZ, TZ); sill(p, -OW / 2, OW / 2, SZ)
p.box(-OW / 2, OW / 2, -0.05, -0.02, SZ, TZ, "Dark")
shutter_leaf(p, -OW / 2, 0, SZ, TZ, -0.09, slats=9); shutter_leaf(p, 0, OW / 2, SZ, TZ, -0.09, slats=9)

p = piece("win_modern")                           # wider aluminium window with an iron grille, plain concrete frame
mw = 1.6
wall_with_opening(p, -mw / 2, mw / 2, 1.0, 2.5); surround(p, -mw / 2, mw / 2, 1.0, 2.5, heavy=False); sill(p, -mw / 2, mw / 2, 1.0)
casement(p, -mw / 2, mw / 2, 1.0, 2.5, transom=False, depth=-0.1)
for k in range(9):
    x = -mw / 2 + 0.1 + k * (mw - 0.2) / 8; p.box(x - 0.012, x + 0.012, -T - 0.05, -T - 0.025, 1.0, 2.5, "Metal")
for z in (1.25, 2.25): p.box(-mw / 2, mw / 2, -T - 0.05, -T - 0.025, z - 0.015, z + 0.015, "Metal")

p = piece("win_broken")                           # glass gone, one shutter hanging, scorched dark inside
wall_with_opening(p, -OW / 2, OW / 2, SZ, TZ); surround(p, -OW / 2, OW / 2, SZ, TZ); sill(p, -OW / 2, OW / 2, SZ)
casement(p, -OW / 2, OW / 2, SZ, TZ, glass=False)
shutter_leaf(p, -OW / 2 - 0.17 - OW / 2, -OW / 2 - 0.17, SZ, TZ, -T - 0.06)
shutter_leaf(p, OW / 2 + 0.1, OW / 2 + 0.1 + OW / 2, SZ - 0.5, TZ - 0.5, -T - 0.2, m=Matrix.Rotation(math.radians(24), 3, "Y") @ Matrix.Rotation(math.radians(-14), 3, "X"))

def balcony(name, solid):
    p = piece(name)
    dw, dh = 1.3, 2.55
    wall_with_opening(p, -dw / 2, dw / 2, 0, dh); surround(p, -dw / 2, dw / 2, 0.1, dh, heavy=not solid)
    casement(p, -dw / 2, dw / 2, 0.02, dh, transom=True)
    if not solid: open_shutters(p, -dw / 2, dw / 2, 0.02, dh)
    D = 1.15
    p.box(-1.7, 1.7, -T - D, -T + 0.01, -0.16, 0.0, "Trim")                     # slab
    p.box(-1.75, 1.75, -T - D - 0.04, -T - D + 0.06, -0.2, 0.02, "Trim")        # its nosing
    for x in (-1.45, -0.6, 0.6, 1.45): p.box(x - 0.08, x + 0.08, -T - 0.25, -T, -0.36, -0.16, "Trim")   # corbels
    if solid:
        p.box(-1.7, 1.7, -T - D, -T - D + 0.12, 0, 1.0, "Plaster")
        for x in (-1.7, 1.58): p.box(x, x + 0.12, -T - D, -T, 0, 1.0, "Plaster")
        p.box(-1.74, 1.74, -T - D - 0.03, -T + 0.01, 1.0, 1.07, "Trim")
    else:
        railing(p, -1.65, 1.65, -T - D + 0.05, -T, 0.0)
balcony("balcony_rail", False)
balcony("balcony_solid", True)

p = piece("shop")                                 # ground floor shop: rolling shutter down, sign board
sw, sh = 3.1, 2.7
wall_with_opening(p, -sw / 2, sw / 2, 0, sh)
p.box(-sw / 2, sw / 2, -0.12, -0.06, 0.02, sh - 0.3, "ShopShutter")
p.box(-sw / 2, sw / 2, -T - 0.08, -0.04, sh - 0.32, sh, "Metal")                      # shutter box
p.box(-sw / 2 - 0.1, sw / 2 + 0.1, -T - 0.14, -T, sh + 0.03, H - 0.05, "Sign")

p = piece("shop_open")                            # shutter half up, dark shop behind
wall_with_opening(p, -sw / 2, sw / 2, 0, sh)
p.box(-sw / 2, sw / 2, -0.12, -0.06, 1.35, sh - 0.3, "ShopShutter")
p.box(-sw / 2, sw / 2, -0.03, -0.005, 0.0, 1.4, "Dark")
p.box(-sw / 2, sw / 2, -T - 0.08, -0.04, sh - 0.32, sh, "Metal")
p.box(-sw / 2 - 0.1, sw / 2 + 0.1, -T - 0.14, -T, sh + 0.03, H - 0.05, "Sign")

p = piece("door")                                 # entrance: recessed metal door, transom grille, step
dw, dh = 1.25, 2.4
wall_with_opening(p, -dw / 2, dw / 2, 0, dh + 0.45); surround(p, -dw / 2, dw / 2, 0, dh + 0.45)
p.box(-dw / 2, dw / 2, -0.1, -0.05, 0.12, dh, "Door")
p.box(-dw / 2, dw / 2, -0.06, -0.03, dh, dh + 0.45, "Glass")
for k in range(5): x = -dw / 2 + (k + 0.5) * dw / 5; p.box(x - 0.012, x + 0.012, -0.13, -0.1, dh, dh + 0.45, "Metal")
p.box(-dw / 2 - 0.2, dw / 2 + 0.2, -T - 0.4, -T + 0.01, 0, 0.14, "Stone")

p = piece("arch")                                 # arcade arch of the Italian blocks, a shop door deep inside
aw, spring = 2.3, 1.95; ar = aw / 2
p.box(-W2, -ar, -T, 0, 0, H, "Plaster"); p.box(ar, W2, -T, 0, 0, H, "Plaster")
n = 24
for k in range(n):
    x0 = -ar + k * aw / n; x1 = x0 + aw / n; xc = (x0 + x1) / 2
    z = spring + math.sqrt(max(0, ar * ar - xc * xc))
    p.box(x0, x1, -T, 0, z, H, "Plaster")
    p.box(x0, x1, -T - 0.05, -T + 0.01, z, min(H, z + 0.22), "Trim")         # the arch's stone ring
for x in (-ar - 0.22, ar): p.box(x, x + 0.22, -T - 0.05, -T + 0.01, 0, spring, "Trim")
p.box(-ar, ar, -0.04, -0.01, 0, spring + ar, "Dark")
p.box(-0.7, 0.7, -0.08, -0.04, 0, 2.3, "Door")

for i, (c, r) in enumerate([((0.3, 1.8), 0.85), ((-0.8, 1.2), 0.65), ((0.6, 2.2), 1.15)]):
    p = piece(f"dmg_hole{i}"); damaged_wall(p, 11 + i, c, r)

def collapse_profile(seed):
    rnd = random.Random(seed); a = rnd.uniform(0.6, 1.6); side = rnd.choice([-1, 1])
    def lim(z):
        top = H - 0.2 - a * (z / H) * 0
        cut = W2 - (z / H) ** 1.3 * (2 * W2) * 0.85 + rnd.uniform(-0.35, 0.35)   # less and less wall higher up, ragged
        if cut < -W2 + 0.3: return None
        return (-W2, cut) if side > 0 else (-cut, W2)
    return lim
for i in range(2):
    p = piece(f"dmg_top{i}"); damaged_wall(p, 21 + i, (0, 0), 0, top_cut=collapse_profile(31 + i))
    rnd = random.Random(41 + i)
    for k in range(14):                                                           # rebar sticking out of the broken edge
        z = rnd.uniform(0.4, H); x = rnd.uniform(-1.5, 1.5); L = rnd.uniform(0.4, 1.1)
        m = Matrix.Rotation(math.radians(rnd.uniform(-35, 35)), 3, "Y") @ Matrix.Rotation(math.radians(rnd.uniform(-20, 20)), 3, "X")
        p.box(x - 0.007, x + 0.007, -T * 0.5 - 0.007, -T * 0.5 + 0.007, z, z + L, "Rebar", m)

p = piece("parapet")                              # roof edge: cornice below the roof line, parapet wall, coping
p.box(-W2, W2, -T - 0.28, -T + 0.01, -0.32, -0.12, "Trim")
p.box(-W2, W2, -T - 0.18, -T + 0.01, -0.12, 0.0, "Trim")
p.box(-W2, W2, -T, -T + 0.2, 0.0, 0.95, "Plaster")
p.box(-W2 - 0.01, W2 + 0.01, -T - 0.05, -T + 0.25, 0.95, 1.05, "Trim")

p = piece("band"); p.box(-W2, W2, -T - 0.06, -T + 0.01, -0.06, 0.18, "Trim")
p = piece("base"); p.box(-W2, W2, -T - 0.05, -T + 0.01, 0.0, 0.55, "Stone")
p = piece("corner"); p.box(-0.32, 0.32, -0.32, 0.32, 0, H, "Trim")

objs = [p.finish() for p in pieces]
bpy.ops.object.select_all(action="DESELECT")
for o in objs: o.select_set(True)
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True, export_apply=True, export_yup=True, export_materials="EXPORT")
tris = sum(sum(len(f.vertices) - 2 for f in o.data.polygons) for o in objs)
print(f"[kit] {len(objs)} pieces, {tris} triangles -> {OUT}")
for o in objs: print(f"[kit]   {o.name}: {sum(len(f.vertices) - 2 for f in o.data.polygons)} tris, materials {[m.name for m in o.data.materials]}")
