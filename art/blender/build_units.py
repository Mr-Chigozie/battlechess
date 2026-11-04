"""Chess Wars character generator (Blender 5.x).

Builds units from code: one shared humanoid skeleton, modular rigid parts bound
to bones, shared animation clips, FBX export for Unity and preview renders.

Run:
  blender -b -P art/blender/build_units.py -- [--render] [--engine eevee|cycles]
"""
import bpy, bmesh, math, os, sys
from mathutils import Vector, Matrix, Euler

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.dirname(HERE)
EXPORT = os.path.join(ART, "export")
PREVIEW = os.path.join(ART, "previews")
ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
H0 = 0.08  # top of the plinth; the body stands on it


# ---------------------------------------------------------------- helpers
def V(x, y, z):
    return Vector((x, y, z + H0))


def T(x, y, z):
    return Matrix.Translation(V(x, y, z))


def R(x=0, y=0, z=0):
    return Euler((math.radians(x), math.radians(y), math.radians(z)), "XYZ").to_matrix().to_4x4()


def S(x, y, z):
    return Matrix.Diagonal((x, y, z, 1))


def along(p1, p2):
    """Matrix placing a Z-aligned primitive between two body points, and the length."""
    a, b = V(*p1), V(*p2)
    d = b - a
    q = d.to_track_quat("Z", "Y")
    return Matrix.Translation((a + b) / 2) @ q.to_matrix().to_4x4(), d.length


def lin(hexcol):
    def ch(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return (ch(hexcol >> 16 & 255), ch(hexcol >> 8 & 255), ch(hexcol & 255))


def material(name, hexcol, metal=0.0, rough=0.6, emit=0.0):
    m = bpy.data.materials.get(name)
    if m:
        return m
    m = bpy.data.materials.new(name)
    try:
        m.use_nodes = True
    except Exception:
        pass
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    c = (*lin(hexcol), 1.0)
    bsdf.inputs["Base Color"].default_value = c
    bsdf.inputs["Metallic"].default_value = metal
    bsdf.inputs["Roughness"].default_value = rough
    if emit:
        bsdf.inputs["Emission Color"].default_value = c
        bsdf.inputs["Emission Strength"].default_value = emit
    m.diffuse_color = c
    return m


# ---------------------------------------------------------------- skeleton
def _side(s):
    x = "L" if s > 0 else "R"
    return [
        (f"shoulder.{x}", (0.05 * s, 0, 1.46), (0.19 * s, 0, 1.46), "chest"),
        (f"upper_arm.{x}", (0.21 * s, 0, 1.45), (0.30 * s, 0, 1.17), f"shoulder.{x}"),
        (f"forearm.{x}", (0.30 * s, 0, 1.17), (0.36 * s, -0.03, 0.92), f"upper_arm.{x}"),
        (f"hand.{x}", (0.36 * s, -0.03, 0.92), (0.38 * s, -0.05, 0.80), f"forearm.{x}"),
        (f"thigh.{x}", (0.11 * s, 0, 0.98), (0.12 * s, 0, 0.56), "hips"),
        (f"shin.{x}", (0.12 * s, 0, 0.56), (0.12 * s, 0.02, 0.10), f"thigh.{x}"),
        (f"foot.{x}", (0.12 * s, 0.02, 0.10), (0.12 * s, -0.14, 0.03), f"shin.{x}"),
    ]


BONES = [
    ("root", (0, 0, -H0), (0, 0, 0.17), None),
    ("hips", (0, 0, 0.98), (0, 0, 1.10), "root"),
    ("spine", (0, 0, 1.10), (0, 0, 1.28), "hips"),
    ("chest", (0, 0, 1.28), (0, 0, 1.50), "spine"),
    ("neck", (0, 0, 1.50), (0, 0, 1.60), "chest"),
    ("head", (0, 0, 1.60), (0, 0, 1.90), "neck"),
] + _side(1) + _side(-1)
B = {name: (h, t) for name, h, t, _ in BONES}
BONE_INDEX = {name: i for i, (name, *_rest) in enumerate(BONES)}


def mid(bone, dx=0, dy=0, dz=0):
    h, t = B[bone]
    return tuple((a + b) / 2 + o for a, b, o in zip(h, t, (dx, dy, dz)))


def build_armature(name, x):
    data = bpy.data.armatures.new(name + "_Rig")
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location.x = x
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    eb = {}
    for bname, head, tail, parent in BONES:
        b = data.edit_bones.new(bname)
        b.head, b.tail = V(*head), V(*tail)
        # Z axis faces forward on every bone, so +X rotation always means "swing forward".
        b.align_roll(Vector((0, -1, 0)))
        if parent:
            b.parent = eb[parent]
        eb[bname] = b
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.select_set(False)
    for pb in obj.pose.bones:
        pb.rotation_mode = "XYZ"
    return obj


# ---------------------------------------------------------------- kit: parts in one mesh
class Kit:
    def __init__(self, army, palette, team):
        self.army, self.palette, self.team = army, palette, team
        self.bm = bmesh.new()
        self.deform = self.bm.verts.layers.deform.verify()
        self.slots = []

    def _mat_index(self, slot):
        if slot not in self.slots:
            self.slots.append(slot)
        return self.slots.index(slot)

    def _finish(self, before, slot, bone, M, bevel=0.0, segs=2, smooth=False):
        # `before` is the set of vertices that existed before this part. BMesh reuses
        # freed slots, so a part's vertices are not guaranteed to sit at the end.
        bm = self.bm
        new = set(bm.verts) - before
        if bevel:
            edges = [e for e in bm.edges if e.verts[0] in new and e.verts[1] in new]
            bmesh.ops.bevel(bm, geom=list(new) + edges, offset=bevel, offset_type="OFFSET",
                            segments=segs, profile=0.5, affect="EDGES", clamp_overlap=True)
            new = set(bm.verts) - before
        bmesh.ops.transform(bm, matrix=M, verts=list(new))
        gi, mi = BONE_INDEX[bone], self._mat_index(slot)
        faces = {f for v in new for f in v.link_faces}
        for v in new:
            v[self.deform][gi] = 1.0
        for f in faces:
            f.material_index = mi
            f.smooth = smooth

    def box(self, slot, bone, M, size, bevel=0.0):
        n0 = set(self.bm.verts)
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        bmesh.ops.scale(self.bm, vec=size, verts=r["verts"])
        self._finish(n0, slot, bone, M, bevel=bevel)

    def cyl(self, slot, bone, M, r1, r2, h, seg=8, bevel=0.0):
        n0 = set(self.bm.verts)
        bmesh.ops.create_cone(self.bm, cap_ends=True, cap_tris=False, segments=seg,
                              radius1=r1, radius2=max(r2, 1e-4), depth=h)
        self._finish(n0, slot, bone, M, bevel=bevel if r2 > 0 else 0.0)

    def sphere(self, slot, bone, M, radii, seg=10, smooth=True):
        n0 = set(self.bm.verts)
        r = bmesh.ops.create_uvsphere(self.bm, u_segments=seg, v_segments=max(4, seg // 2 + 1), radius=1.0)
        bmesh.ops.scale(self.bm, vec=radii, verts=r["verts"])
        self._finish(n0, slot, bone, M, smooth=smooth)

    def torus(self, slot, bone, M, R_, r, su=16, sv=6):
        bm, n0 = self.bm, set(self.bm.verts)
        rings = []
        for i in range(su):
            a = 2 * math.pi * i / su
            ring = []
            for j in range(sv):
                b = 2 * math.pi * j / sv
                rr = R_ + r * math.cos(b)
                ring.append(bm.verts.new((rr * math.cos(a), rr * math.sin(a), r * math.sin(b))))
            rings.append(ring)
        for i in range(su):
            for j in range(sv):
                a, b = rings[i], rings[(i + 1) % su]
                bm.faces.new((a[j], b[j], b[(j + 1) % sv], a[(j + 1) % sv]))
        self._finish(n0, slot, bone, M, smooth=True)

    def prism(self, slot, bone, M, pts, depth):
        bm, n0 = self.bm, set(self.bm.verts)
        front = [bm.verts.new((u, -depth / 2, v)) for u, v in pts]
        back = [bm.verts.new((u, depth / 2, v)) for u, v in pts]
        faces = [bm.faces.new(front), bm.faces.new(back)]
        n = len(pts)
        for i in range(n):
            faces.append(bm.faces.new((front[i], front[(i + 1) % n], back[(i + 1) % n], back[i])))
        bmesh.ops.recalc_face_normals(bm, faces=faces)
        self._finish(n0, slot, bone, M)

    def build(self, name, armature):
        me = bpy.data.meshes.new(name)
        self.bm.normal_update()
        self.bm.to_mesh(me)
        self.bm.free()
        obj = bpy.data.objects.new(name + "_Mesh", me)
        bpy.context.scene.collection.objects.link(obj)
        for slot in self.slots:
            if slot == "Team":
                spec = TEAMS[self.team]
                me.materials.append(material(f"Team_{self.team}", *spec))
            else:
                me.materials.append(material(f"{self.army}_{slot}", *self.palette[slot]))
        for bname, *_ in BONES:
            obj.vertex_groups.new(name=bname)
        obj.parent = armature
        mod = obj.modifiers.new("Armature", "ARMATURE")
        mod.object = armature
        return obj


# ---------------------------------------------------------------- palettes (hex, metallic, roughness, emission)
TEAMS = {"Ivory": (0xEFE6D2, 0.0, 0.5), "Onyx": (0x2A2522, 0.0, 0.45)}
PALETTES = {
    "Knights": {
        "Armor": (0xB7C3D0, 0.55, 0.35), "Dark": (0x4A505A, 0.5, 0.45), "Cloth": (0x7A1F24, 0.0, 0.8),
        "Accent": (0xD8342C, 0.0, 0.6), "Trim": (0xD9B44A, 0.8, 0.3), "Leather": (0x4A3324, 0.0, 0.8),
        "Steel": (0xE4E9EF, 0.7, 0.25),
    },
    "Mafia": {
        "Suit": (0x2E2F36, 0.0, 0.7), "Stripe": (0x8C8E99, 0.0, 0.6), "Lapel": (0x1C1D22, 0.0, 0.6),
        "Shirt": (0xF1EEE6, 0.0, 0.6), "Tie": (0x9A1B1B, 0.0, 0.5), "Hat": (0x232327, 0.0, 0.75),
        "Band": (0xC9A24A, 0.3, 0.5), "Skin": (0xE0B394, 0.0, 0.6), "Dark": (0x111114, 0.2, 0.4),
        "Wood": (0x6A3D1F, 0.0, 0.55), "Metal": (0x3C3F45, 0.85, 0.35), "Accent": (0xC9A24A, 0.6, 0.4),
    },
    "Undead": {
        "Bone": (0xE8E2CC, 0.0, 0.75), "Armor": (0x7D7A62, 0.6, 0.65), "Dark": (0x0B0D0B, 0.0, 0.9),
        "Cloth": (0x2F3A2C, 0.0, 0.9), "Accent": (0x7DFF9A, 0.0, 0.5), "Glow": (0x7DFF9A, 0.0, 0.5, 3.0),
        "Leather": (0x3A3026, 0.0, 0.85), "Steel": (0x8F978C, 0.8, 0.45), "Trim": (0x8A6A3A, 0.8, 0.5),
    },
}


def plinth(k):
    k.cyl("Accent", "root", Matrix.Translation((0, 0, 0.012)), 0.45, 0.45, 0.024, seg=28)
    k.cyl("Team", "root", Matrix.Translation((0, 0, 0.052)), 0.43, 0.41, 0.056, seg=28, bevel=0.008)


# ---------------------------------------------------------------- Knights: Squire (pawn)
def knight_pawn(k):
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box("Armor", f"thigh.{x}", T(0.12 * s, 0, 0.78), (0.16, 0.17, 0.36), bevel=0.03)
        k.sphere("Armor", f"shin.{x}", T(0.12 * s, -0.05, 0.56), (0.075, 0.075, 0.07), seg=8)
        M, L = along((0.12 * s, 0.02, 0.12), (0.12 * s, 0.0, 0.53))
        k.cyl("Armor", f"shin.{x}", M, 0.065, 0.08, L, seg=8, bevel=0.01)
        k.box("Dark", f"foot.{x}", T(0.12 * s, -0.04, 0.055), (0.12, 0.26, 0.11), bevel=0.03)
    # waist
    k.cyl("Cloth", "hips", T(0, 0, 0.80), 0.27, 0.2, 0.36, seg=8)
    k.cyl("Leather", "hips", T(0, 0, 0.99), 0.215, 0.215, 0.07, seg=10)
    k.box("Trim", "hips", T(0, -0.215, 0.99), (0.08, 0.03, 0.07), bevel=0.01)
    # torso and tabard with cross
    k.box("Armor", "spine", T(0, 0, 1.12), (0.34, 0.24, 0.22), bevel=0.05)
    k.box("Armor", "chest", T(0, 0, 1.36), (0.44, 0.28, 0.30), bevel=0.07)
    k.box("Cloth", "chest", T(0, -0.145, 1.18), (0.30, 0.03, 0.52), bevel=0.01)
    k.box("Cloth", "chest", T(0, 0.145, 1.18), (0.30, 0.03, 0.52), bevel=0.01)
    k.box("Trim", "chest", T(0, -0.165, 1.22), (0.055, 0.02, 0.32))
    k.box("Trim", "chest", T(0, -0.165, 1.30), (0.20, 0.02, 0.055))
    k.cyl("Armor", "chest", T(0, 0, 1.53), 0.13, 0.11, 0.09, seg=10)
    # arms
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.sphere("Armor", f"upper_arm.{x}", T(0.23 * s, 0, 1.46), (0.13, 0.13, 0.10), seg=10, smooth=False)
        M, L = along(*B[f"upper_arm.{x}"])
        k.cyl("Armor", f"upper_arm.{x}", M, 0.055, 0.06, L, seg=8)
        k.sphere("Armor", f"forearm.{x}", T(0.30 * s, 0, 1.17), (0.06, 0.06, 0.06), seg=8)
        M, L = along(*B[f"forearm.{x}"])
        k.cyl("Armor", f"forearm.{x}", M, 0.07, 0.055, L, seg=8, bevel=0.01)
        k.box("Dark", f"hand.{x}", T(*mid(f"hand.{x}")), (0.09, 0.10, 0.11), bevel=0.02)
    # great helm with visor cross and crest
    k.cyl("Armor", "head", T(0, 0, 1.76), 0.155, 0.15, 0.30, seg=12, bevel=0.01)
    k.cyl("Armor", "head", T(0, 0, 1.935), 0.15, 0.08, 0.07, seg=12)
    k.box("Dark", "head", T(0, -0.145, 1.78), (0.25, 0.04, 0.035))
    k.box("Trim", "head", T(0, -0.155, 1.71), (0.035, 0.03, 0.18))
    k.box("Accent", "head", T(0, 0.02, 2.0), (0.035, 0.28, 0.12), bevel=0.015)
    # arming sword, right hand
    h = Vector(mid("hand.R", dy=-0.02))
    d = Vector((0, -1, 0.55)).normalized()
    P = lambda t: tuple(h + d * t)
    M, L = along(P(-0.08), P(0.08)); k.cyl("Leather", "hand.R", M, 0.022, 0.022, L, seg=6)
    k.sphere("Trim", "hand.R", T(*P(-0.11)), (0.035, 0.035, 0.035), seg=8)
    M, L = along(P(0.09), P(0.11)); k.box("Trim", "hand.R", M, (0.24, 0.045, L + 0.02))
    M, L = along(P(0.11), P(0.85)); k.box("Steel", "hand.R", M, (0.065, 0.016, L))
    M, L = along(P(0.85), P(0.95)); k.cyl("Steel", "hand.R", M, 0.035, 0.0, L, seg=4)
    # kite shield, left forearm
    kite = [(0, 0.28), (0.19, 0.22), (0.2, 0.02), (0.0, -0.36), (-0.2, 0.02), (-0.19, 0.22)]
    SM = T(0.44, -0.07, 1.02) @ R(z=37) @ R(x=-8)
    k.prism("Trim", "forearm.L", SM, [(u * 1.08, v * 1.06) for u, v in kite], 0.03)
    k.prism("Accent", "forearm.L", SM @ Matrix.Translation((0, -0.014, 0)), kite, 0.03)
    k.box("Trim", "forearm.L", SM @ Matrix.Translation((0, -0.032, -0.02)), (0.045, 0.012, 0.46))
    k.box("Trim", "forearm.L", SM @ Matrix.Translation((0, -0.032, 0.1)), (0.3, 0.012, 0.045))


# ---------------------------------------------------------------- Undead: Skeleton (pawn)
def undead_pawn(k):
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        M, L = along((0.11 * s, 0, 0.94), (0.12 * s, 0, 0.6)); k.cyl("Bone", f"thigh.{x}", M, 0.032, 0.036, L, seg=6)
        k.sphere("Bone", f"shin.{x}", T(0.12 * s, -0.01, 0.56), (0.052, 0.052, 0.05), seg=8)
        M, L = along((0.12 * s, 0.0, 0.52), (0.12 * s, 0.02, 0.12)); k.cyl("Bone", f"shin.{x}", M, 0.028, 0.026, L, seg=6)
        k.sphere("Bone", f"foot.{x}", T(0.12 * s, 0.02, 0.095), (0.036, 0.036, 0.036), seg=8)
        k.box("Bone", f"foot.{x}", T(0.12 * s, -0.05, 0.035), (0.09, 0.2, 0.06), bevel=0.015)
    # pelvis, rope belt, rags
    k.box("Bone", "hips", T(0, 0, 0.97), (0.26, 0.14, 0.11), bevel=0.04)
    k.cyl("Leather", "hips", T(0, 0, 1.0), 0.16, 0.16, 0.035, seg=8)
    k.box("Cloth", "hips", T(0, -0.09, 0.84) @ R(x=8), (0.17, 0.015, 0.26))
    k.box("Cloth", "hips", T(0.02, 0.09, 0.82) @ R(x=-8), (0.2, 0.015, 0.3))
    for px, pz in ((-0.05, 0.69), (0.045, 0.7)):
        k.cyl("Cloth", "hips", T(px, -0.105, pz) @ R(x=180), 0.035, 0.0, 0.08, seg=4)
    # spine and ribcage
    M, L = along((0, 0.04, 1.0), (0, 0.045, 1.3)); k.cyl("Bone", "spine", M, 0.028, 0.028, L, seg=6)
    for z in (1.06, 1.14, 1.22):
        k.cyl("Bone", "spine", T(0, 0.04, z), 0.042, 0.042, 0.022, seg=8)
    M, L = along((0, 0.05, 1.28), (0, 0.05, 1.5)); k.cyl("Bone", "chest", M, 0.026, 0.026, L, seg=6)
    for z, rad in ((1.44, 0.145), (1.38, 0.155), (1.32, 0.15), (1.26, 0.13)):
        k.torus("Bone", "chest", T(0, 0, z) @ S(1, 0.75, 1), rad, 0.017)
    k.box("Bone", "chest", T(0, -0.112, 1.36), (0.04, 0.02, 0.2), bevel=0.008)
    M, L = along((-0.2, -0.02, 1.48), (0.2, -0.02, 1.48)); k.cyl("Bone", "chest", M, 0.02, 0.02, L, seg=6)
    k.box("Cloth", "chest", T(0, -0.15, 1.32) @ R(y=40), (0.06, 0.02, 0.5))
    # rusty pauldron on one shoulder
    k.sphere("Armor", "upper_arm.L", T(0.22, 0, 1.47), (0.12, 0.12, 0.09), seg=10, smooth=False)
    k.cyl("Armor", "upper_arm.L", T(0.25, 0, 1.57) @ R(y=20), 0.035, 0.0, 0.12, seg=6)
    # arms
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.sphere("Bone", f"upper_arm.{x}", T(0.21 * s, 0, 1.45), (0.04, 0.04, 0.04), seg=8)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl("Bone", f"upper_arm.{x}", M, 0.025, 0.025, L, seg=6)
        k.sphere("Bone", f"forearm.{x}", T(0.30 * s, 0, 1.17), (0.035, 0.035, 0.035), seg=8)
        h, t = B[f"forearm.{x}"]
        for oy in (-0.015, 0.015):
            M, L = along((h[0], h[1] + oy, h[2]), (t[0], t[1] + oy, t[2]))
            k.cyl("Bone", f"forearm.{x}", M, 0.015, 0.013, L, seg=5)
        k.box("Bone", f"hand.{x}", T(*mid(f"hand.{x}", dz=0.02)), (0.06, 0.05, 0.07), bevel=0.01)
        tx, ty, tz = B[f"hand.{x}"][1]
        for oy in (-0.02, 0.0, 0.02):
            k.cyl("Bone", f"hand.{x}", T(tx, ty + oy, tz - 0.01) @ R(x=180), 0.012, 0.0, 0.08, seg=4)
    # neck and skull with glowing eyes
    M, L = along((0, 0.01, 1.5), (0, 0.01, 1.63)); k.cyl("Bone", "neck", M, 0.03, 0.03, L, seg=6)
    k.sphere("Bone", "head", T(0, 0.015, 1.8), (0.14, 0.16, 0.14), seg=12)
    k.box("Bone", "head", T(0, -0.075, 1.70), (0.17, 0.13, 0.10), bevel=0.03)
    k.box("Bone", "head", T(0, -0.06, 1.635), (0.14, 0.12, 0.045), bevel=0.015)
    for s in (1, -1):
        k.box("Dark", "head", T(0.055 * s, -0.12, 1.765), (0.065, 0.05, 0.055), bevel=0.01)
        k.sphere("Glow", "head", T(0.055 * s, -0.14, 1.765), (0.021, 0.021, 0.021), seg=8)
    k.box("Dark", "head", T(0, -0.135, 1.71), (0.03, 0.03, 0.035))
    k.box("Dark", "head", T(0, -0.14, 1.662), (0.12, 0.012, 0.008))
    # rusted skullcap with nose guard and a dented crest spike
    k.cyl("Armor", "head", T(0, 0.015, 1.89), 0.158, 0.11, 0.12, seg=10, bevel=0.01)
    k.cyl("Trim", "head", T(0, 0.015, 1.84), 0.162, 0.162, 0.025, seg=10)
    k.box("Armor", "head", T(0, -0.15, 1.79), (0.035, 0.025, 0.13), bevel=0.006)
    k.cyl("Armor", "head", T(0, 0.015, 1.98) @ R(x=-12), 0.03, 0.0, 0.1, seg=5)
    # scythe, right hand
    hx, hy, hz = mid("hand.R")
    M, L = along((hx, hy - 0.01, 0.3), (hx, hy - 0.07, 2.02)); k.cyl("Leather", "hand.R", M, 0.022, 0.022, L, seg=6)
    k.cyl("Trim", "hand.R", T(hx, hy - 0.066, 1.93), 0.032, 0.032, 0.06, seg=6)
    blade = [(-0.03, 0.06), (0.15, 0.09), (0.32, 0.06), (0.47, -0.02), (0.58, -0.14), (0.64, -0.3),
             (0.52, -0.16), (0.38, -0.06), (0.2, -0.02), (-0.03, -0.04)]
    k.prism("Steel", "hand.R", T(hx, hy - 0.066, 1.95) @ R(z=-90), blade, 0.02)


# ---------------------------------------------------------------- Mafia: Soldier (pawn)
def mafia_pawn(k):
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        # pinstripe trousers, white spats, black shoes
        k.box("Suit", f"thigh.{x}", T(0.12 * s, 0, 0.77), (0.15, 0.16, 0.42), bevel=0.03)
        M, L = along((0.12 * s, 0.02, 0.1), (0.12 * s, 0.0, 0.57))
        k.cyl("Suit", f"shin.{x}", M, 0.068, 0.062, L, seg=8)
        for dx in (-0.035, 0.035):
            k.box("Stripe", f"shin.{x}", T(0.12 * s + dx, -0.06, 0.34), (0.006, 0.01, 0.42))
        k.cyl("Shirt", f"foot.{x}", T(0.12 * s, 0.005, 0.1), 0.066, 0.06, 0.06, seg=8)
        k.box("Dark", f"foot.{x}", T(0.12 * s, -0.04, 0.04), (0.11, 0.25, 0.08), bevel=0.03)
    # jacket
    k.cyl("Suit", "hips", T(0, 0, 0.92), 0.235, 0.215, 0.22, seg=10)
    k.box("Suit", "spine", T(0, 0, 1.13), (0.36, 0.24, 0.26), bevel=0.05)
    k.box("Suit", "chest", T(0, 0, 1.36), (0.44, 0.27, 0.30), bevel=0.07)
    k.box("Shirt", "chest", T(0, -0.13, 1.39), (0.13, 0.02, 0.2))
    k.box("Tie", "chest", T(0, -0.145, 1.34), (0.045, 0.015, 0.24))
    k.box("Tie", "chest", T(0, -0.145, 1.475), (0.055, 0.02, 0.04))
    for s in (1, -1):
        k.box("Lapel", "chest", T(0.075 * s, -0.142, 1.37) @ R(y=-18 * s), (0.05, 0.015, 0.26))
        for dx in (0.13, 0.18):
            k.box("Stripe", "chest", T(dx * s, -0.138, 1.3), (0.006, 0.006, 0.36))
    k.box("Shirt", "chest", T(0.14, -0.142, 1.42), (0.05, 0.012, 0.03))  # pocket square
    for b in (1.18, 1.08):
        k.sphere("Lapel", "spine", T(0, -0.125, b), (0.015, 0.008, 0.015), seg=6)  # buttons
    k.cyl("Shirt", "chest", T(0, 0, 1.53), 0.095, 0.09, 0.05, seg=10)
    # arms: padded shoulders, sleeves, white cuffs, hands
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box("Suit", f"upper_arm.{x}", T(0.21 * s, 0, 1.46), (0.13, 0.21, 0.1), bevel=0.04)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl("Suit", f"upper_arm.{x}", M, 0.058, 0.064, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Suit", f"forearm.{x}", M, 0.06, 0.054, L - 0.03, seg=8)
        h, t = B[f"forearm.{x}"]
        k.cyl("Shirt", f"forearm.{x}", T(*t) @ Matrix.Translation((0, 0, 0.02)), 0.05, 0.05, 0.035, seg=8)
        k.box("Skin", f"hand.{x}", T(*mid(f"hand.{x}")), (0.075, 0.09, 0.1), bevel=0.02)
    # head: heavy jaw, ears, shadowed eyes, stubble line
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.06, 0.06, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.125, 0.135, 0.14), seg=12)
    k.box("Skin", "head", T(0, -0.035, 1.665), (0.18, 0.17, 0.09), bevel=0.035)
    for s in (1, -1):
        k.sphere("Skin", "head", T(0.125 * s, 0.0, 1.74), (0.025, 0.035, 0.04), seg=6)
        k.box("Dark", "head", T(0.048 * s, -0.125, 1.755), (0.035, 0.02, 0.018))
        k.box("Hat", "head", T(0.05 * s, -0.128, 1.785) @ R(y=8 * s), (0.055, 0.02, 0.016))
    k.box("Skin", "head", T(0, -0.14, 1.72), (0.035, 0.03, 0.05), bevel=0.01)  # nose
    k.box("Lapel", "head", T(0, -0.12, 1.66), (0.06, 0.01, 0.008))  # set mouth
    # fedora, tipped forward
    HM = T(0, 0.0, 1.87) @ R(x=6, y=-5)
    k.cyl("Hat", "head", HM @ S(1, 0.9, 1), 0.235, 0.235, 0.02, seg=16)
    k.cyl("Hat", "head", HM @ Matrix.Translation((0, 0.01, 0.075)), 0.15, 0.125, 0.13, seg=12, bevel=0.012)
    k.cyl("Band", "head", HM @ Matrix.Translation((0, 0.01, 0.03)), 0.153, 0.152, 0.035, seg=12)
    k.box("Hat", "head", HM @ Matrix.Translation((0, -0.02, 0.145)), (0.05, 0.16, 0.02))  # crown pinch
    # Tommy gun in the right hand. Modelled hanging down along the arm, so it is carried low at rest
    # and comes level when the Shoot clip raises the arm. Local layout: -Y = muzzle, -Z = grip.
    gx, gy, gz = mid("hand.R")
    G = lambda dx, dy, dz: T(gx, gy, gz) @ R(x=90) @ Matrix.Translation((dx, dy, dz))
    k.box("Metal", "hand.R", G(0.0, -0.17, 0.05), (0.05, 0.34, 0.07), bevel=0.01)          # receiver
    k.cyl("Metal", "hand.R", G(0.0, -0.4, 0.06) @ R(x=90), 0.03, 0.03, 0.16, seg=10)        # finned barrel
    for fy in (-0.35, -0.39, -0.43, -0.47):
        k.cyl("Dark", "hand.R", G(0.0, fy, 0.06) @ R(x=90), 0.036, 0.036, 0.012, seg=10)
    k.cyl("Metal", "hand.R", G(0.0, -0.53, 0.06) @ R(x=90), 0.014, 0.014, 0.08, seg=6)     # muzzle
    k.cyl("Metal", "hand.R", G(0.0, -0.2, -0.04) @ R(y=90), 0.085, 0.085, 0.055, seg=14)   # drum magazine
    k.cyl("Dark", "hand.R", G(0.0, -0.2, -0.04) @ R(y=90), 0.03, 0.03, 0.06, seg=8)
    k.box("Wood", "hand.R", G(0.0, -0.02, -0.03) @ R(x=-15), (0.035, 0.045, 0.1), bevel=0.01)   # pistol grip
    k.box("Wood", "hand.R", G(0.0, -0.33, -0.01), (0.03, 0.04, 0.09), bevel=0.01)              # fore grip
    k.box("Wood", "hand.R", G(0.0, 0.1, 0.02) @ R(x=8), (0.04, 0.22, 0.08), bevel=0.015)        # stock


# ---------------------------------------------------------------- animation (shared by every humanoid)
GUARD = {"forearm.R": (30, 0, 0), "forearm.L": (40, 0, 0)}


def pose(**bones):
    p = dict(GUARD)
    for k, v in bones.items():
        p[k.replace("_L", ".L").replace("_R", ".R").replace("upperarm", "upper_arm")] = v
    return p


CLIPS = {
    "Idle": (60, True, [
        (0, pose(), {}),
        (30, pose(chest=(3, 0, 0), head=(-2, 0, 0), upperarm_L=(0, 0, 3), upperarm_R=(0, 0, -3)), {"hips": (0, 0.008, 0)}),
        (60, pose(), {}),
    ]),
    "Walk": (24, True, [
        (0, pose(thigh_L=(25, 0, 0), shin_L=(-5, 0, 0), thigh_R=(-22, 0, 0), shin_R=(-30, 0, 0),
                 upperarm_L=(-15, 0, 0), upperarm_R=(15, 0, 0), spine=(4, 4, 0)), {"hips": (0, 0, 0)}),
        (6, pose(thigh_L=(0, 0, 0), thigh_R=(10, 0, 0), shin_R=(-50, 0, 0), spine=(4, 0, 0)), {"hips": (0, 0.03, 0)}),
        (12, pose(thigh_R=(25, 0, 0), shin_R=(-5, 0, 0), thigh_L=(-22, 0, 0), shin_L=(-30, 0, 0),
                  upperarm_R=(-15, 0, 0), upperarm_L=(15, 0, 0), spine=(4, -4, 0)), {"hips": (0, 0, 0)}),
        (18, pose(thigh_R=(0, 0, 0), thigh_L=(10, 0, 0), shin_L=(-50, 0, 0), spine=(4, 0, 0)), {"hips": (0, 0.03, 0)}),
        (24, pose(thigh_L=(25, 0, 0), shin_L=(-5, 0, 0), thigh_R=(-22, 0, 0), shin_R=(-30, 0, 0),
                  upperarm_L=(-15, 0, 0), upperarm_R=(15, 0, 0), spine=(4, 4, 0)), {"hips": (0, 0, 0)}),
    ]),
    "Attack": (32, False, [
        (0, pose(), {}),
        (9, pose(upperarm_R=(150, 0, -15), forearm_R=(50, 0, 0), spine=(-10, 15, 0), chest=(-10, 0, 0),
                 thigh_L=(25, 0, 0), shin_L=(-20, 0, 0), thigh_R=(-12, 0, 0),
                 upperarm_L=(30, 0, 0), forearm_L=(60, 0, 0), head=(-8, 0, 0)), {"hips": (0, 0.02, 0)}),
        (14, pose(upperarm_R=(55, 0, 0), forearm_R=(10, 0, 0), spine=(25, -15, 0), chest=(10, 0, 0),
                  thigh_L=(45, 0, 0), shin_L=(-45, 0, 0), thigh_R=(-28, 0, 0), shin_R=(-10, 0, 0),
                  upperarm_L=(-10, 0, 0), forearm_L=(40, 0, 0), head=(5, 0, 0)), {"hips": (0, -0.08, 0)}),
        (20, pose(upperarm_R=(25, 0, 0), forearm_R=(0, 0, 0), spine=(28, -18, 0), chest=(8, 0, 0),
                  thigh_L=(42, 0, 0), shin_L=(-42, 0, 0), thigh_R=(-26, 0, 0), shin_R=(-8, 0, 0),
                  head=(5, 0, 0)), {"hips": (0, -0.08, 0)}),
        (32, pose(), {"hips": (0, 0, 0)}),
    ]),
    # Ranged attack: raise the weapon to the hip-aim, rattle off a burst, lower it.
    "Shoot": (34, False, [
        (0, pose(), {}),
        (6, pose(upperarm_R=(35, 0, 0), forearm_R=(62, 0, 0), upperarm_L=(42, 0, -28), forearm_L=(72, 0, 0),
                 spine=(-4, -12, 0), thigh_L=(14, 0, 0), thigh_R=(-8, 0, 0)), {"hips": (0, -0.02, 0)}),
        *[(f, pose(upperarm_R=(35 - 4 * (i % 2), 0, 0), forearm_R=(62 - 5 * (i % 2), 0, 0),
                   upperarm_L=(42 - 3 * (i % 2), 0, -28), forearm_L=(72, 0, 0),
                   spine=(-4 - 3 * (i % 2), -12, 0), head=(-2 * (i % 2), 0, 0),
                   thigh_L=(14, 0, 0), thigh_R=(-8, 0, 0)), {"hips": (0, -0.02, 0)})
          for i, f in enumerate(range(8, 22, 2))],
        (24, pose(upperarm_R=(35, 0, 0), forearm_R=(62, 0, 0), upperarm_L=(42, 0, -28), forearm_L=(72, 0, 0),
                  spine=(-4, -12, 0), thigh_L=(14, 0, 0), thigh_R=(-8, 0, 0)), {"hips": (0, -0.02, 0)}),
        (34, pose(), {"hips": (0, 0, 0)}),
    ]),
    "Death": (46, False, [
        (0, pose(), {}),
        (7, pose(spine=(-20, 10, 0), head=(-25, 0, 0), upperarm_R=(55, 0, -20), upperarm_L=(20, 0, 25)), {"hips": (0, 0.02, 0)}),
        (20, pose(thigh_L=(80, 0, 0), shin_L=(-100, 0, 0), thigh_R=(70, 0, 0), shin_R=(-110, 0, 0),
                  spine=(15, 0, 0), head=(10, 0, 0), upperarm_R=(10, 0, -10), upperarm_L=(10, 0, 10)), {"hips": (0, -0.33, 0)}),
        (46, pose(thigh_L=(80, 0, 0), shin_L=(-100, 0, 0), thigh_R=(70, 0, 0), shin_R=(-110, 0, 0),
                  spine=(50, 0, 0), chest=(20, 0, 0), head=(35, 0, 0), upperarm_R=(-5, 0, -15),
                  upperarm_L=(-5, 0, 15), forearm_R=(10, 0, 0), forearm_L=(10, 0, 0)), {"hips": (0, -0.4, 0)}),
    ]),
}


def set_action(arm, act):
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = act
    if hasattr(ad, "action_slot") and ad.action_slot is None and len(act.slots):
        ad.action_slot = act.slots[0]


def make_clips(arm):
    acts = {}
    for name, (end, cyclic, keys) in CLIPS.items():
        act = bpy.data.actions.new(name)
        act.use_fake_user = True
        set_action(arm, act)
        # Key every bone in every clip so each clip is self-contained (no pose bleeding between clips).
        rot_bones = [b for b, *_ in BONES]
        loc_bones = ["hips"]
        for frame, p, l in keys:
            for b in rot_bones:
                pb = arm.pose.bones[b]
                pb.rotation_euler = [math.radians(a) for a in p.get(b, (0, 0, 0))]
                pb.keyframe_insert("rotation_euler", frame=frame, group=b)
            for b in loc_bones:
                pb = arm.pose.bones[b]
                pb.location = l.get(b, (0, 0, 0))
                pb.keyframe_insert("location", frame=frame, group=b)
        act.use_frame_range = True
        act.frame_start, act.frame_end = 0, end
        act.use_cyclic = cyclic
        acts[name] = act
    return acts


# ---------------------------------------------------------------- scene
def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.fps = 30


def unit(army, team, builder, name, x):
    arm = build_armature(name, x)
    k = Kit(army, PALETTES[army], team)
    builder(k)
    k.build(name, arm)
    return arm


def export_fbx(arm, path):
    for o in bpy.context.scene.objects:
        o.select_set(False)
    arm.select_set(True)
    for c in arm.children:
        c.select_set(True)
    loc = arm.location.copy()
    arm.location = (0, 0, 0)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"ARMATURE", "MESH"},
        add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=True,
        bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE")
    # GLB copy for the web review page (art/viewer.html)
    bpy.ops.export_scene.gltf(filepath=os.path.splitext(path)[0] + ".glb", export_format="GLB",
                              use_selection=True, export_animations=True)
    arm.location = loc


def stage(engine):
    sc = bpy.context.scene
    world = bpy.data.worlds.new("World")
    sc.world = world
    try:
        world.use_nodes = True
    except Exception:
        pass
    bg = world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (*lin(0x1C2230), 1)
    bg.inputs["Strength"].default_value = 1.0
    # a patch of chessboard
    for i in range(-2, 2):
        for j in range(-1, 2):
            bpy.ops.mesh.primitive_plane_add(size=1.2, location=(i * 1.2 + 0.6, j * 1.2, 0))
            sq = bpy.context.active_object
            light = (i + j) % 2 == 0
            sq.data.materials.append(material("Sq_Light" if light else "Sq_Dark", 0x9C8A68 if light else 0x3B3228, 0, 0.7))
    def lamp(kind, loc, target, energy, size=1.0):
        data = bpy.data.lights.new(kind, kind)
        data.energy = energy
        if kind == "AREA":
            data.size = size
        o = bpy.data.objects.new(kind, data)
        sc.collection.objects.link(o)
        o.location = loc
        o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
        return o
    lamp("SUN", (-3, -4, 6), (0, 0, 0), 3.5)
    lamp("AREA", (2.5, 4, 3), (0, 0, 1), 400, 3)
    lamp("AREA", (4, -3, 1.5), (0, 0, 1), 120, 4)
    cam = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    sc.collection.objects.link(cam)
    cam.data.lens = 50
    cam.location = (2.0, -7.4, 2.5)
    cam.rotation_euler = (Vector((0, 0, 1.0)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    sc.camera = cam
    if engine == "cycles":
        sc.render.engine = "CYCLES"
        sc.cycles.device = "CPU"
        sc.cycles.samples = 48
        sc.cycles.use_denoising = True
    else:
        sc.render.engine = "BLENDER_EEVEE"
    sc.render.image_settings.file_format = "PNG"


def render_sheet(arms, acts):
    import numpy as np
    sc = bpy.context.scene
    shots = [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Shoot", 10), ("Death", 46)]
    size = 600
    sc.render.resolution_x = sc.render.resolution_y = size
    tiles = []
    for i, (clip, frame) in enumerate(shots):
        for a in arms:
            set_action(a, acts[clip])
        sc.frame_set(frame)
        path = os.path.join(PREVIEW, f"_tile{i}.png")
        sc.render.filepath = path
        bpy.ops.render.render(write_still=True)
        img = bpy.data.images.load(path)
        px = np.empty(size * size * 4, dtype=np.float32)
        img.pixels.foreach_get(px)
        tiles.append(px.reshape(size, size, 4))
        bpy.data.images.remove(img)
        os.remove(path)
    cols, rows = 3, 2
    sheet = np.ones((rows * size, cols * size, 4), dtype=np.float32)
    for i, t in enumerate(tiles):
        r, c = divmod(i, cols)
        # image rows are stored bottom-up
        sheet[(rows - 1 - r) * size:(rows - r) * size, c * size:(c + 1) * size] = t
    out = bpy.data.images.new("sheet", cols * size, rows * size, alpha=True)
    out.pixels.foreach_set(sheet.ravel())
    out.filepath_raw = os.path.join(PREVIEW, "pawns_poses.png")
    out.file_format = "PNG"
    out.save()
    # hero shot
    for a in arms:
        set_action(a, acts["Idle"])
    sc.frame_set(0)
    sc.render.resolution_x, sc.render.resolution_y = 1200, 900
    sc.render.filepath = os.path.join(PREVIEW, "pawns_hero.png")
    bpy.ops.render.render(write_still=True)


def main():
    os.makedirs(EXPORT, exist_ok=True)
    os.makedirs(PREVIEW, exist_ok=True)
    clear()
    roster = [  # (army, team, builder, name, x on the preview board)
        ("Knights", "Ivory", knight_pawn, "Knights_Pawn", -1.2),
        ("Mafia", "Ivory", mafia_pawn, "Mafia_Pawn", 0.0),
        ("Undead", "Onyx", undead_pawn, "Undead_Pawn", 1.2),
    ]
    arms = [unit(army, team, build, name, x) for army, team, build, name, x in roster]
    acts = make_clips(arms[0])
    for arm in arms:
        set_action(arm, acts["Idle"])
        export_fbx(arm, os.path.join(EXPORT, arm.name + ".fbx"))
    if "--render" in ARGS:
        engine = ARGS[ARGS.index("--engine") + 1] if "--engine" in ARGS else "eevee"
        stage(engine)
        render_sheet(arms, acts)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(EXPORT, "pawns.blend"))
    print("CHESSWARS DONE")


main()
