"""Chess Wars character generator (Blender 5.x).

Builds units from code: one shared humanoid skeleton, modular rigid parts bound
to bones, shared animation clips, FBX export for Unity and preview renders.

Run:
  blender -b -P art/blender/build_units.py -- [--render] [--engine eevee|cycles]
"""
import bpy, bmesh, math, os, random, sys
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


def build_armature(name, x, rig=None):
    data = bpy.data.armatures.new(name + "_Rig")
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location.x = x
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    eb = {}
    for bname, head, tail, parent, *roll in rig or BONES:
        b = data.edit_bones.new(bname)
        b.head, b.tail = V(*head), V(*tail)
        # Z axis faces forward on every humanoid bone, so +X rotation always means "swing forward".
        # Beast bones that point forward roll to face up instead (see beast_rig).
        b.align_roll(Vector(roll[0] if roll else (0, -1, 0)))
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
    def __init__(self, army, palette, team, rig=None):
        self.army, self.palette, self.team = army, palette, team
        self.rig = rig or BONES
        self.index = {b[0]: i for i, b in enumerate(self.rig)}
        self.xf = Matrix()  # extra placement for every part (a rider uses it to sit in the saddle)
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
        bmesh.ops.transform(bm, matrix=self.xf @ M, verts=list(new))
        gi, mi = self.index[bone], self._mat_index(slot)
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
        for bname, *_ in self.rig:
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
        "Steel": (0xE4E9EF, 0.7, 0.25), "Skin": (0xE0B394, 0.0, 0.6), "Hair": (0x6B4A30, 0.0, 0.8), "Beard": (0xA9A39A, 0.0, 0.85),
        "Ermine": (0xF4F1EA, 0.0, 0.9), "Lips": (0xB8485A, 0.0, 0.5),
        "Mail": (0x8A9099, 0.65, 0.55), "Holy": (0xFFE7A0, 0.0, 0.5, 2.5), "Wood": (0x5A3A22, 0.0, 0.7),
        "Stone": (0xBDB5A4, 0.0, 0.85), "StoneDark": (0x8A8274, 0.0, 0.9), "Horse": (0xD6D0C6, 0.0, 0.7),
    },
    "Mafia": {
        "Suit": (0x2E2F36, 0.0, 0.7), "Stripe": (0x8C8E99, 0.0, 0.6), "Lapel": (0x1C1D22, 0.0, 0.6),
        "Shirt": (0xF1EEE6, 0.0, 0.6), "Tie": (0x9A1B1B, 0.0, 0.5), "Hat": (0x232327, 0.0, 0.75),
        "Band": (0xC9A24A, 0.3, 0.5), "Skin": (0xE0B394, 0.0, 0.6), "Dark": (0x111114, 0.2, 0.4),
        "Wood": (0x6A3D1F, 0.0, 0.55), "Metal": (0x3C3F45, 0.85, 0.35), "Accent": (0xC9A24A, 0.6, 0.4),
        "Coat": (0xA88458, 0.0, 0.85), "Trench": (0x8A7350, 0.0, 0.8), "WhiteHat": (0xECE6D8, 0.0, 0.6),
        "Grey": (0x6E7178, 0.0, 0.7), "Cigar": (0x5A3A22, 0.0, 0.8), "Ember": (0xFF6A1A, 0.0, 0.5, 4.0),
        "Hair": (0x2A2420, 0.0, 0.8), "GreyHair": (0x9A9894, 0.0, 0.85), "Leather": (0x4A3324, 0.0, 0.8),
        "Stubble": (0x9E7E68, 0.0, 0.8), "Scar": (0xB0675A, 0.0, 0.6),
        "Brick": (0x8C3B2A, 0.0, 0.9), "BrickDark": (0x5A281E, 0.0, 0.9), "Awning": (0x1F5A3A, 0.0, 0.8),
        "Neon": (0xFF4FA3, 0.0, 0.4, 4.0), "Lamp": (0xFFC870, 0.0, 0.5, 3.0), "Glass": (0x2A3A48, 0.3, 0.15),
        "Car": (0x1A1B20, 0.6, 0.25), "Chrome": (0xD8DCE0, 1.0, 0.15),
    },
    "Marines": {
        "Fatigue": (0x5B6B45, 0.0, 0.85), "FatigueDark": (0x434F33, 0.0, 0.9), "Canvas": (0xB6A57E, 0.0, 0.85),
        "Helmet": (0x4E5A3A, 0.2, 0.7), "Skin": (0xD9A47E, 0.0, 0.6), "Hair": (0x3A2A1E, 0.0, 0.8),
        "Dark": (0x1A1A18, 0.2, 0.6), "Metal": (0x3C3F45, 0.85, 0.35), "Steel": (0xD9DEE3, 0.8, 0.25),
        "Wood": (0x6A4526, 0.0, 0.6), "Brass": (0xC8A048, 0.8, 0.3), "Accent": (0xC9A24A, 0.6, 0.4),
        "Khaki": (0xC7B48A, 0.0, 0.75), "Navy": (0x1F2A44, 0.0, 0.7), "Stubble": (0xA27E64, 0.0, 0.8),
        "Lens": (0x101418, 0.9, 0.1), "Ghillie": (0x56633A, 0.0, 0.95), "GhillieDark": (0x3A4426, 0.0, 0.95),
        "Concrete": (0x9A968C, 0.0, 0.9), "ConcreteDark": (0x6E6A62, 0.0, 0.92), "Sand": (0xC2A873, 0.0, 0.95),
        "Lamp": (0xFFD58A, 0.0, 0.5, 3.0), "Glass": (0x2A3A48, 0.3, 0.15), "Ermine": (0xF2F0EA, 0.0, 0.7),
    },
    "Vikings": {
        "Tunic": (0x4A5F7A, 0.0, 0.85), "TunicRed": (0x8B2E24, 0.0, 0.85), "Trousers": (0x6B5E4A, 0.0, 0.9),
        "Fur": (0x7A6248, 0.0, 0.95), "FurDark": (0x4A3A2C, 0.0, 0.95), "Leather": (0x5A3E28, 0.0, 0.8),
        "Iron": (0x7D828A, 0.7, 0.45), "Steel": (0xD9DEE3, 0.8, 0.25), "Wood": (0x7A5232, 0.0, 0.7),
        "WoodDark": (0x4E3420, 0.0, 0.75), "Skin": (0xE3B596, 0.0, 0.6), "Beard": (0xB5562A, 0.0, 0.85),
        "Blond": (0xD8B56A, 0.0, 0.75), "GreyBeard": (0xD8D4CC, 0.0, 0.85), "Gold": (0xD4A940, 0.9, 0.3),
        "Rune": (0x7FD8FF, 0.0, 0.5, 3.0), "Sail": (0xE8E0CC, 0.0, 0.85), "SailRed": (0xA8322A, 0.0, 0.85),
        "Water": (0x2F6E8C, 0.1, 0.2), "Foam": (0xE8F2F5, 0.0, 0.6), "Dark": (0x161514, 0.0, 0.7),
        "Accent": (0x5FA8D3, 0.2, 0.5), "Bear": (0x5C4330, 0.0, 0.95), "BearDark": (0x3A2A1E, 0.0, 0.9),
        "Bone": (0xE8E2CC, 0.0, 0.75), "Lips": (0xB8485A, 0.0, 0.5), "Raven": (0x15161A, 0.2, 0.4),
        "Robe": (0x55606E, 0.0, 0.9), "Stone": (0x8A8A84, 0.0, 0.9),
    },
    "Undead": {
        "Bone": (0xE8E2CC, 0.0, 0.75), "Armor": (0x7D7A62, 0.6, 0.65), "Dark": (0x0B0D0B, 0.0, 0.9),
        "Cloth": (0x2F3A2C, 0.0, 0.9), "Accent": (0x7DFF9A, 0.0, 0.5), "Glow": (0x2EF06A, 0.0, 0.5, 2.2),
        "Leather": (0x3A3026, 0.0, 0.85), "Steel": (0x8F978C, 0.8, 0.45), "Trim": (0x8A6A3A, 0.8, 0.5),
        "Royal": (0x3B2350, 0.0, 0.85), "Gold": (0xB8963A, 0.9, 0.35), "Robe": (0x24182E, 0.0, 0.9),
        "Stone": (0x5A5D55, 0.0, 0.9), "StoneDark": (0x34372F, 0.0, 0.95), "Fur": (0x403B35, 0.0, 0.95), "FurDark": (0x25221F, 0.0, 0.95),
    },
}


def plinth(k):
    k.cyl("Accent", "root", Matrix.Translation((0, 0, 0.012)), 0.45, 0.45, 0.024, seg=28)
    k.cyl("Team", "root", Matrix.Translation((0, 0, 0.052)), 0.43, 0.41, 0.056, seg=28, bevel=0.008)


# ---------------------------------------------------------------- shared shapes
def cape(k, slot, top_w, bottom_w, top_z, bottom_z, back=0.17, tilt=8, jagged=0):
    """A cloak hanging from the shoulders behind the body (bound to the chest)."""
    hw, bw = top_w / 2, bottom_w / 2
    pts = [(-hw, top_z), (hw, top_z), (bw, bottom_z)]
    if jagged:
        for i in range(1, jagged * 2):
            u = bw - (2 * bw) * i / (jagged * 2)
            pts.append((u, bottom_z + (0.09 if i % 2 else 0.0)))
    pts.append((-bw, bottom_z))
    M = T(0, back, top_z) @ R(x=tilt) @ Matrix.Translation((0, 0, -top_z))
    k.prism(slot, "chest", M, pts, 0.03)


def sword(k, slot_blade="Steel", slot_hilt="Trim", slot_grip="Leather", length=0.85, width=0.065, glow=None):
    """A sword in the right hand, pointing forward and up."""
    h = Vector(mid("hand.R", dy=-0.02))
    d = Vector((0, -1, 0.55)).normalized()
    P = lambda t: tuple(h + d * t)
    M, L = along(P(-0.08), P(0.08)); k.cyl(slot_grip, "hand.R", M, 0.022, 0.022, L, seg=6)
    k.sphere(slot_hilt, "hand.R", T(*P(-0.11)), (0.035, 0.035, 0.035), seg=8)
    M, L = along(P(0.09), P(0.11)); k.box(slot_hilt, "hand.R", M, (width * 3.7, 0.045, L + 0.02))
    M, L = along(P(0.11), P(length)); k.box(slot_blade, "hand.R", M, (width, 0.016, L))
    if glow:
        M, L = along(P(0.16), P(length - 0.06)); k.box(glow, "hand.R", M, (width * 0.28, 0.022, L))
    M, L = along(P(length), P(length + 0.1)); k.cyl(slot_blade, "hand.R", M, width * 0.54, 0.0, L, seg=4)


def crown(k, slot, gem, z=1.885, r=0.148, points=5, tall=0.1, jag=0.0, gem_glow=None):
    k.cyl(slot, "head", T(0, 0.0, z), r, r, 0.07, seg=12, bevel=0.006)
    for i in range(points):
        a = math.pi / 2 + 2 * math.pi * i / points  # first point at the front
        h = tall * (1 + (jag if i % 2 else -jag * 0.5))
        k.cyl(slot, "head", T(r * 0.93 * math.cos(a), -r * 0.93 * math.sin(a), z + 0.035 + h / 2), 0.036, 0.0, h, seg=5)
    k.sphere(gem_glow or gem, "head", T(0, -r - 0.004, z), (0.024, 0.014, 0.024), seg=8)


# ---------------------------------------------------------------- Knights
def knight_legs(k, knee="Armor"):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box("Armor", f"thigh.{x}", T(0.12 * s, 0, 0.78), (0.16, 0.17, 0.36), bevel=0.03)
        k.sphere(knee, f"shin.{x}", T(0.12 * s, -0.05, 0.56), (0.075, 0.075, 0.07), seg=8)
        M, L = along((0.12 * s, 0.02, 0.12), (0.12 * s, 0.0, 0.53))
        k.cyl("Armor", f"shin.{x}", M, 0.065, 0.08, L, seg=8, bevel=0.01)
        k.box("Dark", f"foot.{x}", T(0.12 * s, -0.04, 0.055), (0.12, 0.26, 0.11), bevel=0.03)


def knight_torso(k):
    k.cyl("Leather", "hips", T(0, 0, 0.99), 0.215, 0.215, 0.07, seg=10)
    k.box("Trim", "hips", T(0, -0.215, 0.99), (0.08, 0.03, 0.07), bevel=0.01)
    k.box("Armor", "spine", T(0, 0, 1.12), (0.34, 0.24, 0.22), bevel=0.05)
    k.box("Armor", "chest", T(0, 0, 1.36), (0.44, 0.28, 0.30), bevel=0.07)
    k.box("Cloth", "chest", T(0, -0.145, 1.18), (0.30, 0.03, 0.52), bevel=0.01)
    k.box("Cloth", "chest", T(0, 0.145, 1.18), (0.30, 0.03, 0.52), bevel=0.01)
    k.box("Trim", "chest", T(0, -0.165, 1.22), (0.055, 0.02, 0.32))
    k.box("Trim", "chest", T(0, -0.165, 1.30), (0.20, 0.02, 0.055))


def knight_arms(k, pauldron=(0.13, 0.13, 0.10), trim=False):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.sphere("Armor", f"upper_arm.{x}", T(0.23 * s, 0, 1.46), pauldron, seg=10, smooth=False)
        if trim:
            k.cyl("Trim", f"upper_arm.{x}", T(0.23 * s, 0, 1.43), pauldron[0] + 0.006, pauldron[0] + 0.006, 0.025, seg=10)
        M, L = along(*B[f"upper_arm.{x}"])
        k.cyl("Armor", f"upper_arm.{x}", M, 0.055, 0.06, L, seg=8)
        k.sphere("Armor", f"forearm.{x}", T(0.30 * s, 0, 1.17), (0.06, 0.06, 0.06), seg=8)
        M, L = along(*B[f"forearm.{x}"])
        k.cyl("Armor", f"forearm.{x}", M, 0.07, 0.055, L, seg=8, bevel=0.01)
        k.box("Dark", f"hand.{x}", T(*mid(f"hand.{x}")), (0.09, 0.10, 0.11), bevel=0.02)


def knight_pawn(k):
    """Squire: great helm, tabard, arming sword and kite shield."""
    plinth(k)
    knight_legs(k)
    k.cyl("Cloth", "hips", T(0, 0, 0.80), 0.27, 0.2, 0.36, seg=8)
    knight_torso(k)
    k.cyl("Armor", "chest", T(0, 0, 1.53), 0.13, 0.11, 0.09, seg=10)
    knight_arms(k)
    # great helm with visor cross and crest
    k.cyl("Armor", "head", T(0, 0, 1.76), 0.155, 0.15, 0.30, seg=12, bevel=0.01)
    k.cyl("Armor", "head", T(0, 0, 1.935), 0.15, 0.08, 0.07, seg=12)
    k.box("Dark", "head", T(0, -0.145, 1.78), (0.25, 0.04, 0.035))
    k.box("Trim", "head", T(0, -0.155, 1.71), (0.035, 0.03, 0.18))
    k.box("Accent", "head", T(0, 0.02, 2.0), (0.035, 0.28, 0.12), bevel=0.015)
    sword(k)
    # kite shield, left forearm
    kite = [(0, 0.28), (0.19, 0.22), (0.2, 0.02), (0.0, -0.36), (-0.2, 0.02), (-0.19, 0.22)]
    SM = T(0.44, -0.07, 1.02) @ R(z=37) @ R(x=-8)
    k.prism("Trim", "forearm.L", SM, [(u * 1.08, v * 1.06) for u, v in kite], 0.03)
    k.prism("Accent", "forearm.L", SM @ Matrix.Translation((0, -0.014, 0)), kite, 0.03)
    k.box("Trim", "forearm.L", SM @ Matrix.Translation((0, -0.032, -0.02)), (0.045, 0.012, 0.46))
    k.box("Trim", "forearm.L", SM @ Matrix.Translation((0, -0.032, 0.1)), (0.3, 0.012, 0.045))


def knight_king(k):
    """King: bareheaded and bearded under a gold crown, royal surcoat, ermine-collared cape, longsword."""
    plinth(k)
    knight_legs(k)
    # long royal surcoat to the knees, gold hem
    k.cyl("Cloth", "hips", T(0, 0, 0.78), 0.29, 0.21, 0.4, seg=10)
    k.cyl("Trim", "hips", T(0, 0, 0.595), 0.295, 0.295, 0.03, seg=10)
    knight_torso(k)
    k.box("Trim", "chest", T(0, -0.162, 0.97), (0.3, 0.02, 0.03))  # surcoat belt line
    # ermine collar with black tail spots, and a long cape
    k.cyl("Ermine", "chest", T(0, 0.01, 1.52), 0.24, 0.17, 0.1, seg=14, bevel=0.02)
    for i in range(7):
        a = math.radians(-30 - i * 20)
        k.box("Dark", "chest", T(0.235 * math.cos(a), 0.235 * math.sin(a), 1.51) @ R(z=math.degrees(a)), (0.02, 0.012, 0.035))
    cape(k, "Cloth", 0.5, 0.82, 1.5, 0.22, tilt=9)
    knight_arms(k, pauldron=(0.14, 0.14, 0.11), trim=True)
    # face, beard, hair and crown
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.06, 0.06, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.12, 0.13, 0.14), seg=12)
    k.sphere("Hair", "head", T(0, 0.03, 1.79), (0.128, 0.13, 0.12), seg=12)
    for s in (1, -1):
        k.box("Dark", "head", T(0.045 * s, -0.122, 1.755), (0.03, 0.02, 0.018))
        k.box("Hair", "head", T(0.048 * s, -0.125, 1.785) @ R(y=-10 * s), (0.05, 0.02, 0.015))
    k.box("Skin", "head", T(0, -0.135, 1.72), (0.035, 0.03, 0.05), bevel=0.01)
    k.box("Hair", "head", T(0, -0.085, 1.645), (0.2, 0.1, 0.12), bevel=0.04)          # beard
    k.cyl("Hair", "head", T(0, -0.1, 1.57) @ R(x=200), 0.07, 0.0, 0.1, seg=6)        # beard point
    k.box("Hair", "head", T(0, -0.133, 1.69), (0.12, 0.025, 0.025), bevel=0.008)     # moustache
    crown(k, "Trim", "Accent", z=1.875, r=0.14, tall=0.09)
    sword(k, length=1.0, width=0.07)


def knight_queen(k):
    """Queen: warrior queen, steel breastplate over a long royal gown, braided hair under a tall crown, slim longsword."""
    plinth(k)
    # armoured shins and boots under the gown
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        M, L = along((0.11 * s, 0.02, 0.12), (0.11 * s, 0.01, 0.26))
        k.cyl("Armor", f"shin.{x}", M, 0.055, 0.06, L, seg=8, bevel=0.01)
        k.box("Dark", f"foot.{x}", T(0.11 * s, -0.05, 0.05), (0.1, 0.23, 0.09), bevel=0.03)
    # long gown flaring to the ankles, gold hem and a front panel
    k.cyl("Cloth", "hips", T(0, 0, 0.62), 0.33, 0.2, 0.8, seg=14)
    k.cyl("Trim", "hips", T(0, 0, 0.235), 0.336, 0.336, 0.03, seg=14)
    k.box("Accent", "hips", T(0, -0.268, 0.6) @ R(x=-9), (0.15, 0.02, 0.74))
    for z in (0.4, 0.62, 0.84):
        k.box("Trim", "hips", T(0, -0.248 - (0.62 - z) * 0.16, z) @ R(x=-9), (0.155, 0.022, 0.02))
    k.cyl("Trim", "hips", T(0, 0, 1.02), 0.205, 0.205, 0.05, seg=12)
    # fitted breastplate with a gold cross, high gorget
    k.box("Armor", "spine", T(0, 0, 1.13), (0.3, 0.21, 0.2), bevel=0.05)
    k.box("Armor", "chest", T(0, 0, 1.35), (0.38, 0.25, 0.27), bevel=0.08)
    k.box("Trim", "chest", T(0, -0.13, 1.31), (0.04, 0.02, 0.26))
    k.box("Trim", "chest", T(0, -0.13, 1.37), (0.16, 0.02, 0.04))
    k.cyl("Armor", "chest", T(0, 0, 1.52), 0.115, 0.095, 0.08, seg=10)
    cape(k, "Cloth", 0.42, 0.72, 1.5, 0.32, tilt=10)
    knight_arms(k, pauldron=(0.11, 0.11, 0.085), trim=True)
    # face: finer jaw, red lips, hair framing the face, a long braid down the back
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.05, 0.05, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.11, 0.12, 0.13), seg=12)
    k.sphere("Hair", "head", T(0, 0.025, 1.8), (0.13, 0.13, 0.125), seg=12)
    for s in (1, -1):
        k.box("Dark", "head", T(0.042 * s, -0.112, 1.752), (0.03, 0.02, 0.026), bevel=0.006)
        k.box("Dark", "head", T(0.05 * s, -0.116, 1.768), (0.034, 0.018, 0.006))
        k.box("Hair", "head", T(0.042 * s, -0.114, 1.786), (0.04, 0.014, 0.007))
        k.box("Hair", "head", T(0.128 * s, 0.025, 1.64), (0.04, 0.1, 0.3), bevel=0.018)
    k.box("Skin", "head", T(0, -0.124, 1.72), (0.02, 0.02, 0.03), bevel=0.006)
    k.box("Lips", "head", T(0, -0.114, 1.68), (0.036, 0.012, 0.012), bevel=0.004)
    k.box("Hair", "head", T(0, 0.085, 1.66) @ R(x=-6), (0.21, 0.07, 0.26), bevel=0.04)
    for i in range(6):
        k.sphere("Hair", "chest", T(0, 0.15 + i * 0.006, 1.5 - i * 0.065), (0.045 - i * 0.003,) * 3, seg=8)
    k.sphere("Trim", "chest", T(0, 0.19, 1.1), (0.025, 0.025, 0.025), seg=8)
    crown(k, "Trim", "Accent", z=1.89, r=0.124, points=9, tall=0.06, jag=0.7)
    sword(k, length=0.95, width=0.055)


def knight_bishop(k):
    """Templar: warrior-priest in mail under a white surcoat with a red cross, mail coif, tall mitre, holy staff."""
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        M, L = along((0.11 * s, 0.02, 0.12), (0.11 * s, 0.01, 0.26))
        k.cyl("Mail", f"shin.{x}", M, 0.055, 0.06, L, seg=8)
        k.box("Leather", f"foot.{x}", T(0.11 * s, -0.05, 0.05), (0.1, 0.23, 0.09), bevel=0.03)
    # long white robe, gold hem
    k.cyl("Ermine", "hips", T(0, 0, 0.6), 0.29, 0.21, 0.78, seg=12)
    k.cyl("Trim", "hips", T(0, 0, 0.225), 0.295, 0.295, 0.03, seg=12)
    # mail body, white surcoat front and back with a big red cross, belt
    k.box("Mail", "spine", T(0, 0, 1.13), (0.34, 0.24, 0.22), bevel=0.05)
    k.box("Mail", "chest", T(0, 0, 1.36), (0.42, 0.27, 0.29), bevel=0.07)
    for y in (-0.142, 0.142):
        k.box("Ermine", "chest", T(0, y, 1.2), (0.33, 0.03, 0.58), bevel=0.01)
    k.box("Accent", "chest", T(0, -0.16, 1.2), (0.075, 0.02, 0.42))
    k.box("Accent", "chest", T(0, -0.16, 1.31), (0.26, 0.02, 0.075))
    k.cyl("Leather", "hips", T(0, 0, 1.0), 0.218, 0.218, 0.05, seg=12)
    k.box("Trim", "hips", T(0, -0.218, 1.0), (0.06, 0.02, 0.05), bevel=0.008)
    # mail arms and leather gloves
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.sphere("Mail", f"upper_arm.{x}", T(0.22 * s, 0, 1.45), (0.1, 0.11, 0.09), seg=10)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl("Mail", f"upper_arm.{x}", M, 0.058, 0.062, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Mail", f"forearm.{x}", M, 0.06, 0.055, L, seg=8)
        k.cyl("Leather", f"forearm.{x}", T(*B[f"forearm.{x}"][1]) @ Matrix.Translation((0, 0, 0.02)), 0.066, 0.06, 0.06, seg=8)
        k.box("Leather", f"hand.{x}", T(*mid(f"hand.{x}")), (0.085, 0.095, 0.105), bevel=0.02)
    # face inside a mail coif, grey beard
    k.cyl("Mail", "chest", T(0, 0.01, 1.54), 0.16, 0.12, 0.1, seg=12)
    k.sphere("Mail", "head", T(0, 0.04, 1.76), (0.145, 0.15, 0.16), seg=12)
    k.sphere("Skin", "head", T(0, -0.03, 1.74), (0.1, 0.105, 0.11), seg=12)
    for s in (1, -1):
        k.box("Dark", "head", T(0.04 * s, -0.125, 1.76), (0.028, 0.02, 0.016))
        k.box("Hair", "head", T(0.042 * s, -0.128, 1.785) @ R(y=-8 * s), (0.045, 0.018, 0.012))
    k.box("Skin", "head", T(0, -0.137, 1.73), (0.03, 0.025, 0.045), bevel=0.008)
    k.box("Beard", "head", T(0, -0.09, 1.655), (0.17, 0.09, 0.11), bevel=0.035)
    k.box("Beard", "head", T(0, -0.135, 1.693), (0.1, 0.02, 0.02), bevel=0.006)
    # tall mitre with gold bands and a red cross
    mitre = [(-0.11, 0.0), (0.11, 0.0), (0.125, 0.13), (0.0, 0.28), (-0.125, 0.13)]
    MM = T(0, 0.03, 1.87)
    k.prism("Ermine", "head", MM, mitre, 0.17)
    k.box("Trim", "head", MM @ Matrix.Translation((0, 0, 0.02)), (0.235, 0.18, 0.035))
    k.box("Trim", "head", MM @ Matrix.Translation((0, -0.088, 0.13)), (0.03, 0.01, 0.24))
    k.box("Accent", "head", MM @ Matrix.Translation((0, -0.092, 0.15)), (0.03, 0.01, 0.1))
    k.box("Accent", "head", MM @ Matrix.Translation((0, -0.092, 0.165)), (0.08, 0.01, 0.028))
    # holy staff with a glowing cross
    hx, hy, hz = mid("hand.R")
    M, L = along((hx, hy - 0.01, 0.3), (hx, hy - 0.06, 1.98)); k.cyl("Wood", "hand.R", M, 0.022, 0.022, L, seg=6)
    k.cyl("Trim", "hand.R", T(hx, hy - 0.06, 1.99), 0.035, 0.035, 0.05, seg=8)
    k.box("Trim", "hand.R", T(hx, hy - 0.06, 2.13), (0.045, 0.045, 0.26), bevel=0.008)
    k.box("Trim", "hand.R", T(hx, hy - 0.06, 2.17), (0.18, 0.045, 0.045), bevel=0.008)
    k.box("Holy", "hand.R", T(hx, hy - 0.085, 2.13), (0.02, 0.01, 0.22))
    k.box("Holy", "hand.R", T(hx, hy - 0.085, 2.17), (0.15, 0.01, 0.02))
    k.sphere("Holy", "hand.R", T(hx, hy - 0.06, 2.17), (0.035, 0.035, 0.035), seg=8)


# ---------------------------------------------------------------- Undead
def undead_legs(k):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        M, L = along((0.11 * s, 0, 0.94), (0.12 * s, 0, 0.6)); k.cyl("Bone", f"thigh.{x}", M, 0.032, 0.036, L, seg=6)
        k.sphere("Bone", f"shin.{x}", T(0.12 * s, -0.01, 0.56), (0.052, 0.052, 0.05), seg=8)
        M, L = along((0.12 * s, 0.0, 0.52), (0.12 * s, 0.02, 0.12)); k.cyl("Bone", f"shin.{x}", M, 0.028, 0.026, L, seg=6)
        k.sphere("Bone", f"foot.{x}", T(0.12 * s, 0.02, 0.095), (0.036, 0.036, 0.036), seg=8)
        k.box("Bone", f"foot.{x}", T(0.12 * s, -0.05, 0.035), (0.09, 0.2, 0.06), bevel=0.015)


def undead_torso(k):
    k.box("Bone", "hips", T(0, 0, 0.97), (0.26, 0.14, 0.11), bevel=0.04)
    M, L = along((0, 0.04, 1.0), (0, 0.045, 1.3)); k.cyl("Bone", "spine", M, 0.028, 0.028, L, seg=6)
    for z in (1.06, 1.14, 1.22):
        k.cyl("Bone", "spine", T(0, 0.04, z), 0.042, 0.042, 0.022, seg=8)
    M, L = along((0, 0.05, 1.28), (0, 0.05, 1.5)); k.cyl("Bone", "chest", M, 0.026, 0.026, L, seg=6)
    for z, rad in ((1.44, 0.145), (1.38, 0.155), (1.32, 0.15), (1.26, 0.13)):
        k.torus("Bone", "chest", T(0, 0, z) @ S(1, 0.75, 1), rad, 0.017)
    k.box("Bone", "chest", T(0, -0.112, 1.36), (0.04, 0.02, 0.2), bevel=0.008)
    M, L = along((-0.2, -0.02, 1.48), (0.2, -0.02, 1.48)); k.cyl("Bone", "chest", M, 0.02, 0.02, L, seg=6)


def undead_arms(k):
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


def undead_skull(k):
    M, L = along((0, 0.01, 1.5), (0, 0.01, 1.63)); k.cyl("Bone", "neck", M, 0.03, 0.03, L, seg=6)
    k.sphere("Bone", "head", T(0, 0.015, 1.8), (0.14, 0.16, 0.14), seg=12)
    k.box("Bone", "head", T(0, -0.075, 1.70), (0.17, 0.13, 0.10), bevel=0.03)
    k.box("Bone", "head", T(0, -0.06, 1.635), (0.14, 0.12, 0.045), bevel=0.015)
    for s in (1, -1):
        k.box("Dark", "head", T(0.055 * s, -0.12, 1.765), (0.065, 0.05, 0.055), bevel=0.01)
        k.sphere("Glow", "head", T(0.055 * s, -0.14, 1.765), (0.021, 0.021, 0.021), seg=8)
    k.box("Dark", "head", T(0, -0.135, 1.71), (0.03, 0.03, 0.035))
    k.box("Dark", "head", T(0, -0.14, 1.662), (0.12, 0.012, 0.008))


def spiked_pauldron(k, s, spikes=1):
    x = "L" if s > 0 else "R"
    k.sphere("Armor", f"upper_arm.{x}", T(0.22 * s, 0, 1.47), (0.12, 0.12, 0.09), seg=10, smooth=False)
    for i in range(spikes):
        k.cyl("Armor", f"upper_arm.{x}", T((0.25 - 0.05 * i) * s, 0.03 * i, 1.57 - 0.02 * i) @ R(y=20 * s + 15 * i * s), 0.035, 0.0, 0.12 - 0.02 * i, seg=6)


def undead_pawn(k):
    """Skeleton: rusted skullcap, rags, one pauldron, scythe."""
    plinth(k)
    undead_legs(k)
    undead_torso(k)
    k.cyl("Leather", "hips", T(0, 0, 1.0), 0.16, 0.16, 0.035, seg=8)
    k.box("Cloth", "hips", T(0, -0.09, 0.84) @ R(x=8), (0.17, 0.015, 0.26))
    k.box("Cloth", "hips", T(0.02, 0.09, 0.82) @ R(x=-8), (0.2, 0.015, 0.3))
    for px, pz in ((-0.05, 0.69), (0.045, 0.7)):
        k.cyl("Cloth", "hips", T(px, -0.105, pz) @ R(x=180), 0.035, 0.0, 0.08, seg=4)
    k.box("Cloth", "chest", T(0, -0.15, 1.32) @ R(y=40), (0.06, 0.02, 0.5))
    spiked_pauldron(k, 1)
    undead_arms(k)
    undead_skull(k)
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


def undead_king(k):
    """Bone King: tall jagged crown with a soul gem, tattered royal robe and cloak, rune greatsword, soulfire in hand."""
    plinth(k)
    undead_legs(k)
    undead_torso(k)
    # tattered royal robe from the waist to the knees
    k.cyl("Royal", "hips", T(0, 0, 0.79), 0.28, 0.19, 0.42, seg=10)
    k.cyl("Gold", "hips", T(0, 0, 0.995), 0.2, 0.2, 0.04, seg=10)
    for i in range(10):
        a = 2 * math.pi * (i + 0.5) / 10
        k.cyl("Royal", "hips", T(0.255 * math.cos(a), 0.255 * math.sin(a), 0.55) @ R(x=180), 0.05, 0.0, 0.1 + 0.05 * (i % 2), seg=4)
    # rusted breastplate rim over the ribs, gold chain of office
    k.torus("Gold", "chest", T(0, -0.01, 1.4) @ S(1, 0.78, 1) @ R(x=-20), 0.16, 0.014, 18, 5)
    k.sphere("Glow", "chest", T(0, -0.135, 1.3), (0.028, 0.018, 0.035), seg=8)
    # cloak with jagged hem and a fan of collar spikes
    cape(k, "Cloth", 0.52, 0.85, 1.5, 0.2, tilt=9, jagged=4)
    for i, a in enumerate((-50, -25, 0, 25, 50)):
        k.cyl("Armor", "chest", T(0.16 * math.sin(math.radians(a)), 0.13, 1.62) @ R(x=-15, y=a * 0.8), 0.03, 0.0, 0.2 - 0.04 * abs(i - 2), seg=5)
    spiked_pauldron(k, 1, spikes=2)
    spiked_pauldron(k, -1, spikes=2)
    undead_arms(k)
    undead_skull(k)
    crown(k, "Gold", "Glow", z=1.9, r=0.16, points=7, tall=0.16, jag=0.45)
    # rune greatsword with a soul-green fuller
    sword(k, slot_blade="Steel", slot_hilt="Gold", slot_grip="Leather", length=1.05, width=0.08, glow="Glow")
    # soulfire burning in the left palm
    lx, ly, lz = mid("hand.L")
    k.sphere("Glow", "hand.L", T(lx, ly - 0.05, lz - 0.02), (0.05, 0.05, 0.05), seg=8)


def undead_queen(k):
    """Wraith Queen: a legless spectre in a tattered hooded shroud, spiked crown over the hood, claws, a soul-lantern staff."""
    plinth(k)
    # she floats: no legs, just a pool of soulfire under a shroud that stops short of the ground
    k.cyl("Glow", "root", T(0, 0, 0.005), 0.22, 0.22, 0.01, seg=18)
    k.cyl("Cloth", "hips", T(0, 0, 0.67), 0.3, 0.17, 0.7, seg=12)
    for i in range(14):
        a = 2 * math.pi * (i + 0.5) / 14
        k.cyl("Cloth", "hips", T(0.27 * math.cos(a), 0.27 * math.sin(a), 0.29) @ R(x=180), 0.055, 0.0, 0.1 + 0.09 * (i % 3 == 0), seg=4)
    k.torus("Glow", "hips", T(0, 0, 0.34), 0.262, 0.01, 20, 5)
    for px, py, pz, r in ((0.14, 0.2, 0.42, 0.03), (-0.18, 0.16, 0.3, 0.024), (0.04, 0.24, 0.22, 0.02), (-0.06, -0.24, 0.24, 0.02)):
        k.sphere("Glow", "hips", T(px, py, pz), (r, r, r), seg=6)
    k.torus("Gold", "hips", T(0, 0, 1.0), 0.19, 0.014, 16, 5)
    # bare ribs above the shroud, a torn shawl and hanging strips
    undead_torso(k)
    k.box("Cloth", "chest", T(0, 0.05, 1.47), (0.46, 0.22, 0.06), bevel=0.02)
    for s in (1, -1):
        k.box("Cloth", "chest", T(0.15 * s, -0.11, 1.28) @ R(x=6, y=-8 * s), (0.07, 0.015, 0.36))
        k.cyl("Cloth", "chest", T(0.15 * s, -0.125, 1.08) @ R(x=180), 0.035, 0.0, 0.07, seg=4)
    cape(k, "Cloth", 0.5, 0.9, 1.52, 0.18, tilt=10, jagged=5)
    undead_arms(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        tx, ty, tz = B[f"hand.{x}"][1]
        for oy in (-0.03, 0.03):
            k.cyl("Bone", f"hand.{x}", T(tx + 0.01 * s, ty + oy, tz - 0.04) @ R(x=180 + oy * 300), 0.01, 0.0, 0.14, seg=4)
    undead_skull(k)
    # hood, drapes and a crown of spikes over it
    k.sphere("Cloth", "head", T(0, 0.07, 1.8), (0.19, 0.15, 0.2), seg=12)
    k.cyl("Cloth", "head", T(0, 0.08, 1.98) @ R(x=-28), 0.1, 0.0, 0.17, seg=8)
    for s in (1, -1):
        k.box("Cloth", "head", T(0.15 * s, 0.03, 1.65), (0.06, 0.2, 0.24), bevel=0.02)
    crown(k, "Gold", "Glow", z=1.95, r=0.175, points=9, tall=0.14, jag=0.5)
    # soul-lantern staff in the right hand
    hx, hy, hz = mid("hand.R")
    M, L = along((hx, hy - 0.01, 0.3), (hx, hy - 0.06, 2.0)); k.cyl("Leather", "hand.R", M, 0.02, 0.02, L, seg=6)
    k.torus("Gold", "hand.R", T(hx, hy - 0.06, 2.07) @ R(y=90), 0.07, 0.014, 14, 5)
    ly = hy - 0.13
    k.box("Gold", "hand.R", T(hx, ly, 2.035), (0.008, 0.008, 0.07))
    k.cyl("Gold", "hand.R", T(hx, ly, 2.0), 0.03, 0.05, 0.02, seg=8)
    k.sphere("Glow", "hand.R", T(hx, ly, 1.93), (0.038, 0.038, 0.055), seg=8)
    k.cyl("Gold", "hand.R", T(hx, ly, 1.86), 0.05, 0.03, 0.02, seg=8)
    for dx, dy in ((0.04, 0), (-0.04, 0), (0, 0.04), (0, -0.04)):
        k.box("Gold", "hand.R", T(hx + dx, ly + dy, 1.93), (0.008, 0.008, 0.14))
    # soulfire in the left palm
    lx, ly2, lz = mid("hand.L")
    k.sphere("Glow", "hand.L", T(lx, ly2 - 0.05, lz - 0.02), (0.05, 0.05, 0.05), seg=8)


def undead_bishop(k):
    """Necromancer: robed skeleton in a tall pointed cowl, glowing runes, skull pauldrons, skull staff, chained spellbook."""
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box("Bone", f"foot.{x}", T(0.11 * s, -0.07, 0.035), (0.08, 0.18, 0.05), bevel=0.015)
    # robe to the ground with a torn hem and a column of runes
    k.cyl("Robe", "hips", T(0, 0, 0.58), 0.3, 0.2, 0.84, seg=12)
    for i in range(12):
        a = 2 * math.pi * (i + 0.5) / 12
        k.cyl("Robe", "hips", T(0.275 * math.cos(a), 0.275 * math.sin(a), 0.14) @ R(x=180), 0.05, 0.0, 0.07 + 0.05 * (i % 2), seg=4)
    for i, z in enumerate((0.3, 0.42, 0.54, 0.66, 0.78)):
        r = 0.2 + (1.0 - z) / 0.84 * 0.1
        size = (0.055, 0.01, 0.014) if i % 2 else (0.014, 0.01, 0.055)
        k.box("Glow", "hips", T(0, -r - 0.004, z) @ R(x=-7), size)
    k.cyl("Leather", "hips", T(0, 0, 1.0), 0.21, 0.21, 0.05, seg=12)
    k.sphere("Bone", "hips", T(0, -0.215, 1.0), (0.035, 0.025, 0.035), seg=8)
    # chained spellbook at the left hip
    k.box("Leather", "hips", T(0.22, -0.06, 0.82) @ R(z=-15), (0.04, 0.13, 0.17), bevel=0.01)
    k.box("Bone", "hips", T(0.24, -0.065, 0.82) @ R(z=-15), (0.012, 0.11, 0.14))
    k.sphere("Glow", "hips", T(0.248, -0.07, 0.82), (0.012, 0.02, 0.02), seg=6)
    M, L = along((0.2, -0.1, 0.99), (0.22, -0.08, 0.9)); k.cyl("Gold", "hips", M, 0.008, 0.008, L, seg=4)
    # robed body with bone-trimmed front and skull pauldrons
    k.box("Robe", "spine", T(0, 0, 1.13), (0.34, 0.24, 0.22), bevel=0.05)
    k.box("Robe", "chest", T(0, 0, 1.35), (0.4, 0.26, 0.28), bevel=0.07)
    for s in (1, -1):
        k.box("Bone", "chest", T(0.05 * s, -0.133, 1.3) @ R(y=-10 * s), (0.025, 0.012, 0.36))
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        P = T(0.23 * s, -0.01, 1.48)
        k.sphere("Bone", f"upper_arm.{x}", P, (0.085, 0.095, 0.08), seg=10)
        k.box("Bone", f"upper_arm.{x}", P @ Matrix.Translation((0, -0.06, -0.06)), (0.11, 0.07, 0.05), bevel=0.015)
        for e in (1, -1):
            k.box("Dark", f"upper_arm.{x}", P @ Matrix.Translation((0.03 * e, -0.085, -0.005)), (0.035, 0.02, 0.03))
            k.sphere("Glow", f"upper_arm.{x}", P @ Matrix.Translation((0.03 * e, -0.095, -0.005)), (0.01, 0.01, 0.01), seg=6)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl("Robe", f"upper_arm.{x}", M, 0.062, 0.068, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Robe", f"forearm.{x}", M, 0.07, 0.115, L - 0.02, seg=8)
    undead_arms(k)
    undead_skull(k)
    # tall pointed cowl
    k.sphere("Robe", "head", T(0, 0.07, 1.8), (0.19, 0.15, 0.2), seg=12)
    k.cyl("Robe", "head", T(0, 0.06, 2.04) @ R(x=-10), 0.135, 0.0, 0.32, seg=8)
    for s in (1, -1):
        k.box("Robe", "head", T(0.15 * s, 0.03, 1.66), (0.06, 0.2, 0.24), bevel=0.02)
    k.sphere("Glow", "head", T(0, -0.065, 1.98), (0.022, 0.012, 0.03), seg=8)
    # skull staff with a green flame and bone horns
    hx, hy, hz = mid("hand.R")
    sy = hy - 0.06
    M, L = along((hx, hy - 0.01, 0.3), (hx, sy, 2.04)); k.cyl("Leather", "hand.R", M, 0.022, 0.022, L, seg=6)
    for e in (1, -1):
        k.cyl("Bone", "hand.R", T(hx + 0.07 * e, sy, 2.1) @ R(y=-35 * e), 0.018, 0.0, 0.16, seg=5)
    k.sphere("Bone", "hand.R", T(hx, sy, 2.1), (0.06, 0.065, 0.06), seg=10)
    k.box("Bone", "hand.R", T(hx, sy - 0.03, 2.05), (0.07, 0.05, 0.04), bevel=0.01)
    for e in (1, -1):
        k.sphere("Glow", "hand.R", T(hx + 0.022 * e, sy - 0.058, 2.105), (0.014, 0.01, 0.014), seg=6)
    k.cyl("Glow", "hand.R", T(hx, sy, 2.23), 0.04, 0.0, 0.13, seg=6)
    # soulfire in the left palm
    lx, ly, lz = mid("hand.L")
    k.sphere("Glow", "hand.L", T(lx, ly - 0.05, lz - 0.02), (0.05, 0.05, 0.05), seg=8)


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
    tommy_gun(k)


# ---------------------------------------------------------------- Rooks: buildings on the same skeleton
# hips = lower tower, spine = middle, chest = battlements, neck = the catapult arm on the roof.
def jut_blocks(k, slot, bone, rad, z0, z1, count, seed, skip_front=0.55):
    """Stones jutting slightly out of a round wall, for texture (kept clear of the front)."""
    rnd = random.Random(seed)
    placed = 0
    while placed < count:
        a = rnd.uniform(0, 2 * math.pi)
        if abs(a - 1.5 * math.pi) < skip_front:
            continue
        z = rnd.uniform(z0, z1)
        r = rad(z)
        k.box(slot, bone, T(r * math.cos(a), r * math.sin(a), z) @ R(z=math.degrees(a) + 90),
              (0.1 * rnd.uniform(0.8, 1.25), 0.03, 0.065), bevel=0.008)
        placed += 1


def tangent(r, a, z):
    """Matrix for a part sitting on a round wall at angle a, its local -y facing outward."""
    return T(r * math.cos(a), r * math.sin(a), z) @ R(z=math.degrees(a) + 90)


def catapult(k, beam, bucket, payload, weight, post):
    """Roof catapult. The arm is bound to the neck bone (pivot 1.50) and flings forward over the front."""
    for s in (1, -1):
        k.box(post, "chest", T(0.085 * s, 0, 1.52), (0.03, 0.06, 0.08), bevel=0.006)
    k.cyl("Dark", "chest", T(0, 0, 1.5) @ R(y=90), 0.016, 0.016, 0.21, seg=6)
    for s in (1, -1):  # rest for the loaded arm
        k.box(post, "chest", T(0.06 * s, 0.3, 1.51), (0.025, 0.025, 0.06))
    k.box(post, "chest", T(0, 0.3, 1.54), (0.15, 0.025, 0.025))
    M, L = along((0, -0.1, 1.495), (0, 0.34, 1.565))
    beam(M, L)
    k.cyl(bucket, "neck", T(0, 0.35, 1.585), 0.045, 0.066, 0.05, seg=8)
    payload((0, 0.35, 1.63))
    k.box(weight, "neck", T(0, -0.105, 1.515), (0.1, 0.075, 0.07), bevel=0.01)


def knight_rook(k):
    """Keep: round stone tower, oak gate, red banner, lit window, crenellated top with a catapult."""
    plinth(k)
    wall = lambda z: 0.36 - 0.04 * z / 1.28
    k.cyl("StoneDark", "hips", T(0, 0, 0.04), 0.385, 0.375, 0.08, seg=20)
    k.cyl("Stone", "hips", T(0, 0, 0.55), wall(0), wall(1.1), 1.1, seg=20)
    k.cyl("Stone", "spine", T(0, 0, 1.19), wall(1.1), wall(1.28), 0.18, seg=20)
    for z in (0.62, 1.1):
        k.cyl("StoneDark", "hips", T(0, 0, z), wall(z) + 0.018, wall(z) + 0.018, 0.045, seg=20)
    jut_blocks(k, "StoneDark", "hips", wall, 0.14, 1.02, 22, seed=7)
    jut_blocks(k, "StoneDark", "spine", wall, 1.15, 1.25, 5, seed=8)
    # gate: stone arch, oak door with iron bands
    k.box("StoneDark", "hips", T(0, -0.345, 0.2), (0.25, 0.06, 0.4))
    k.cyl("StoneDark", "hips", T(0, -0.345, 0.4) @ R(x=90), 0.125, 0.125, 0.06, seg=14)
    k.box("Wood", "hips", T(0, -0.378, 0.165), (0.17, 0.02, 0.33))
    k.cyl("Wood", "hips", T(0, -0.378, 0.33) @ R(x=90), 0.085, 0.085, 0.02, seg=12)
    for z in (0.1, 0.25):
        k.box("Dark", "hips", T(0, -0.39, z), (0.18, 0.012, 0.022))
    k.sphere("Trim", "hips", T(0.05, -0.395, 0.19), (0.014, 0.01, 0.014), seg=6)
    # arrow slits, one lit
    for a, z, slot in ((0.0, 0.74, "Holy"), (math.pi, 0.74, "Dark"), (math.pi / 2, 0.74, "Dark"),
                       (0.3 * math.pi, 0.36, "Dark"), (0.7 * math.pi, 0.36, "Dark")):
        k.box(slot, "hips", tangent(wall(z) - 0.004, a, z), (0.028, 0.04, 0.13))
    for a in (0.25 * math.pi, 0.75 * math.pi):
        k.box("Dark", "spine", tangent(wall(1.19) - 0.004, a, 1.19), (0.028, 0.04, 0.1))
    # banner hanging down the front (on the spine, so it swings when the tower moves)
    Mb = T(0, -0.335, 1.25) @ R(x=-3)
    k.cyl("Wood", "spine", Mb @ Matrix.Translation((0, -0.012, 0.005)) @ R(y=90), 0.012, 0.012, 0.3, seg=6)
    k.prism("Accent", "spine", Mb, [(-0.12, 0), (0.12, 0), (0.12, -0.5), (0, -0.6), (-0.12, -0.5)], 0.012)
    k.box("Trim", "spine", Mb @ Matrix.Translation((0, -0.008, -0.26)), (0.035, 0.006, 0.26))
    k.box("Trim", "spine", Mb @ Matrix.Translation((0, -0.008, -0.2)), (0.16, 0.006, 0.035))
    k.box("Trim", "spine", Mb @ Matrix.Translation((0, -0.004, -0.02)), (0.25, 0.018, 0.03))
    # overhanging battlements
    k.cyl("StoneDark", "chest", T(0, 0, 1.33), wall(1.28), 0.41, 0.1, seg=20)
    for i in range(10):
        a = 2 * math.pi * (i + 0.5) / 10
        k.box("StoneDark", "chest", tangent(0.335, a, 1.27), (0.05, 0.05, 0.08))
    k.cyl("Stone", "chest", T(0, 0, 1.43), 0.41, 0.41, 0.1, seg=20)
    k.cyl("Wood", "chest", T(0, 0, 1.483), 0.355, 0.355, 0.01, seg=20)
    for i in range(8):
        a = 2 * math.pi * (i + 0.5) / 8
        k.box("Stone", "chest", tangent(0.37, a, 1.54), (0.15, 0.08, 0.12), bevel=0.01)
    # pennant on a back merlon
    a = 2 * math.pi * 3.5 / 8
    px, py = 0.37 * math.cos(a), 0.37 * math.sin(a)
    M, L = along((px, py, 1.6), (px, py, 2.06)); k.cyl("Wood", "chest", M, 0.012, 0.012, L, seg=6)
    k.sphere("Trim", "chest", T(px, py, 2.07), (0.022, 0.022, 0.022), seg=8)
    k.prism("Accent", "chest", T(px, py, 2.03), [(0, 0), (0.24, -0.055), (0, -0.12)], 0.01)
    # catapult
    catapult(k, lambda M, L: k.box("Wood", "neck", M, (0.04, 0.035, L), bevel=0.006),
             "Wood", lambda p: k.sphere("StoneDark", "neck", T(*p), (0.05, 0.05, 0.048), seg=8),
             "Dark", "Wood")


def undead_rook(k):
    """Crypt: square mausoleum of dark stone, glowing barred gate, skulls, tattered banner, bone catapult."""
    plinth(k)
    sq = lambda z: 0.43 - 0.04 * z / 1.28          # half-diagonal of the square wall
    hs = lambda z: sq(z) / math.sqrt(2)            # half-side
    Q = R(z=45)
    k.cyl("StoneDark", "hips", T(0, 0, 0.05) @ Q, 0.45, 0.44, 0.1, seg=4)
    k.cyl("Stone", "hips", T(0, 0, 0.55) @ Q, sq(0), sq(1.1), 1.1, seg=4)
    k.cyl("Stone", "spine", T(0, 0, 1.19) @ Q, sq(1.1), sq(1.28), 0.18, seg=4)
    k.cyl("StoneDark", "hips", T(0, 0, 1.1) @ Q, sq(1.1) + 0.025, sq(1.1) + 0.025, 0.05, seg=4)
    # corner buttresses with pyramid caps
    for sx in (1, -1):
        for sy in (1, -1):
            c = hs(0.5)
            k.box("StoneDark", "hips", T(sx * c, sy * c, 0.48), (0.09, 0.09, 0.86))
            k.cyl("StoneDark", "hips", T(sx * c, sy * c, 0.97) @ Q, 0.068, 0.0, 0.12, seg=4)
    # moss at the foot
    for x, y in ((0.2, -0.31), (-0.27, 0.2), (0.3, 0.12)):
        k.sphere("Cloth", "hips", T(x, y, 0.1), (0.07, 0.04, 0.035), seg=8)
    # pointed gate glowing green behind iron bars, skull keystone
    y0 = -hs(0.2)
    arch = lambda w, h, top: [(-w, 0), (w, 0), (w, h), (0, top), (-w, h)]
    k.prism("StoneDark", "hips", T(0, y0 - 0.01, 0.0), arch(0.14, 0.28, 0.48), 0.05)
    k.prism("Dark", "hips", T(0, y0 - 0.035, 0.0), arch(0.1, 0.25, 0.4), 0.006)
    k.prism("Glow", "hips", T(0, y0 - 0.039, 0.02), arch(0.065, 0.2, 0.3), 0.004)
    for x in (-0.06, -0.02, 0.02, 0.06):
        top = 0.39 - abs(x) * 0.9
        M, L = along((x, y0 - 0.045, 0.0), (x, y0 - 0.045, top)); k.cyl("Steel", "hips", M, 0.009, 0.009, L, seg=5)
    k.box("Steel", "hips", T(0, y0 - 0.045, 0.19), (0.2, 0.012, 0.016))
    k.sphere("Bone", "hips", T(0, y0 - 0.035, 0.53), (0.05, 0.04, 0.05), seg=10)
    k.box("Bone", "hips", T(0, y0 - 0.045, 0.49), (0.05, 0.03, 0.025), bevel=0.006)
    for e in (1, -1):
        k.sphere("Glow", "hips", T(0.018 * e, y0 - 0.07, 0.535), (0.012, 0.008, 0.012), seg=6)
    # glowing pointed windows on the sides and back
    win = arch(0.026, 0.1, 0.145)
    for z, bone in ((0.68, "hips"), (1.15, "spine")):
        for s in (1, -1):
            k.prism("Glow", bone, T(s * (hs(z) + 0.002), 0, z) @ R(z=90), win, 0.01)
        k.prism("Glow", bone, T(0, hs(z) + 0.002, z), win, 0.01)
    # cracks
    for x, z, rot, ln in ((0.16, 0.86, 30, 0.14), (-0.18, 0.4, -40, 0.12), (0.22, 0.25, 70, 0.1)):
        k.box("Dark", "hips", T(x, -hs(z) - 0.004, z) @ R(y=rot), (ln, 0.01, 0.012))
    # tattered banner with a glowing rune
    Mb = T(0, -hs(1.25) - 0.012, 1.25) @ R(x=-3)
    jag = [(-0.12, 0), (0.12, 0), (0.12, -0.46), (0.07, -0.4), (0.03, -0.55), (-0.02, -0.44), (-0.07, -0.58), (-0.12, -0.47)]
    k.prism("Royal", "spine", Mb, jag, 0.012)
    k.box("Bone", "spine", Mb @ Matrix.Translation((0, -0.004, -0.01)), (0.27, 0.025, 0.025), bevel=0.006)
    k.prism("Glow", "spine", Mb @ Matrix.Translation((0, -0.008, -0.27)), [(0, -0.07), (0.05, 0), (0, 0.07), (-0.05, 0)], 0.004)
    # overhanging parapet, spiked merlons, skull corners, glowing pit on the roof
    k.cyl("StoneDark", "chest", T(0, 0, 1.33) @ Q, sq(1.28), 0.47, 0.1, seg=4)
    k.cyl("Stone", "chest", T(0, 0, 1.43) @ Q, 0.47, 0.47, 0.1, seg=4)
    k.cyl("Glow", "chest", T(0, 0, 1.483) @ Q, 0.2, 0.2, 0.006, seg=4)
    e = 0.47 / math.sqrt(2) - 0.04
    for u in (-0.17, 0.17):
        for (mx, my, rot) in ((u, -e, 0), (u, e, 0), (e, u, 90), (-e, u, 90)):
            k.box("Stone", "chest", T(mx, my, 1.53) @ R(z=rot), (0.1, 0.06, 0.1), bevel=0.008)
            k.cyl("StoneDark", "chest", T(mx, my, 1.62) @ R(z=rot + 45), 0.055, 0.0, 0.09, seg=4)
    for sx in (1, -1):
        for sy in (1, -1):
            cx, cy = sx * e, sy * e
            k.box("StoneDark", "chest", T(cx, cy, 1.53), (0.11, 0.11, 0.1), bevel=0.01)
            k.sphere("Bone", "chest", T(cx, cy, 1.635), (0.06, 0.055, 0.058), seg=10)
            k.box("Bone", "chest", T(cx, cy - 0.03, 1.595), (0.055, 0.035, 0.028), bevel=0.006)
            for ex in (1, -1):
                k.box("Dark", "chest", T(cx + 0.02 * ex, cy - 0.05, 1.64), (0.022, 0.016, 0.02))
                k.sphere("Glow", "chest", T(cx + 0.02 * ex, cy - 0.056, 1.64), (0.007, 0.006, 0.007), seg=6)
    # bone catapult flinging a burning skull
    def femur(M, L):
        k.cyl("Bone", "neck", M, 0.022, 0.02, L, seg=6)
        for p in ((0, -0.1, 1.495), (0, 0.34, 1.565)):
            k.sphere("Bone", "neck", T(*p), (0.032, 0.032, 0.032), seg=8)

    def burning_skull(p):
        x, y, z = p
        k.sphere("Bone", "neck", T(x, y, z), (0.045, 0.042, 0.045), seg=8)
        for ex in (1, -1):
            k.sphere("Glow", "neck", T(x + 0.016 * ex, y - 0.036, z + 0.004), (0.01, 0.008, 0.01), seg=6)
        k.cyl("Glow", "neck", T(x, y, z + 0.08), 0.035, 0.0, 0.1, seg=6)

    catapult(k, femur, "Leather", burning_skull, "Steel", "StoneDark")
    # a scatter of bones at the foot
    k.sphere("Bone", "root", T(0.21, -0.35, 0.03), (0.04, 0.036, 0.038), seg=8)
    for (x, y, a) in ((-0.2, -0.355, 20), (-0.12, -0.38, -35)):
        k.cyl("Bone", "root", T(x, y, 0.012) @ R(y=90, z=a), 0.012, 0.012, 0.14, seg=5)


def anchored(rx=0.0, rz=0.0, lift=0.0, pivot=0.98):
    """Hips location that keeps the tower's base in place while the hips bone tilts (lean rx, roll rz)."""
    return {"hips": (-pivot * math.sin(math.radians(rz)), lift, pivot * math.sin(math.radians(rx)))}


TOWER_CLIPS = {
    "Idle": (60, True, [
        (0, {}, {}),
        (30, {"spine": (1.5, 0, 0), "chest": (-1, 0, 0), "neck": (-4, 0, 0)}, anchored(lift=0.008)),
        (60, {}, {}),
    ]),
    # a heavy waddle: rock side to side on the base, hopping between
    "Walk": (24, True, [
        (0, {"hips": (0, 0, 4), "chest": (0, 0, -2)}, anchored(rz=4)),
        (6, {"hips": (3, 0, 0), "chest": (2, 0, 0), "neck": (-8, 0, 0)}, anchored(rx=3, lift=0.07)),
        (12, {"hips": (0, 0, -4), "chest": (0, 0, 2)}, anchored(rz=-4)),
        (18, {"hips": (3, 0, 0), "chest": (2, 0, 0), "neck": (-8, 0, 0)}, anchored(rx=3, lift=0.07)),
        (24, {"hips": (0, 0, 4), "chest": (0, 0, -2)}, anchored(rz=4)),
    ]),
    # wind the catapult back, then fling it over the front
    "Attack": (32, False, [
        (0, {}, {}),
        (8, {"hips": (-3, 0, 0), "spine": (-5, 0, 0), "chest": (-5, 0, 0), "neck": (-22, 0, 0)}, anchored(rx=-3, lift=-0.03)),
        (13, {"hips": (4, 0, 0), "spine": (6, 0, 0), "chest": (5, 0, 0), "neck": (118, 0, 0)}, anchored(rx=4, lift=0.03)),
        (18, {"hips": (2, 0, 0), "spine": (3, 0, 0), "chest": (2, 0, 0), "neck": (100, 0, 0)}, anchored(rx=2)),
        (32, {}, {}),
    ]),
    # rapid volley
    "Shoot": (34, False, [
        (0, {}, {}),
        *[(f, {"chest": (-3 if i % 2 == 0 else 3, 0, 0), "neck": (-15 if i % 2 == 0 else 70, 0, 0)},
           anchored(lift=-0.015 if i % 2 == 0 else 0.01)) for i, f in enumerate(range(4, 26, 4))],
        (34, {}, {}),
    ]),
    # shudder, crack, topple and sink into the board
    "Death": (46, False, [
        (0, {}, {}),
        (6, {"chest": (-6, 0, 4), "neck": (-30, 0, 0)}, anchored(lift=0.02)),
        (12, {"hips": (3, 0, -3), "spine": (-6, 0, 5), "chest": (8, 0, -6), "neck": (40, 0, 10)}, anchored(3, -3, -0.05)),
        (24, {"hips": (6, 0, 4), "spine": (5, 0, 4), "chest": (24, 0, 10), "neck": (70, 0, 20)}, anchored(6, 4, -0.45)),
        (46, {"hips": (9, 0, 5), "spine": (7, 0, 5), "chest": (34, 0, 14), "neck": (95, 0, 25)}, anchored(9, 5, -1.0)),
    ]),
}


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


# ---------------------------------------------------------------- Knights (chess piece): four-legged rigs
# A beast skeleton: body (pivot at the rump, so +X rears up), neck, head, jaw, tail, two-part legs,
# plus a full rider skeleton sitting in the saddle (unused on a riderless beast, but every beast
# shares the same bones so they all share one clip set). Non-leg bones: +X lifts the tip.
RIDER = Matrix.Translation((0, 0.08, 0.32)) @ S(0.85, 0.85, 0.85)


def rider_bones(place):
    out = []
    for name, h, t, parent in BONES[1:]:
        hh, tt = place @ V(*h), place @ V(*t)
        out.append((name, (hh.x, hh.y, hh.z - H0), (tt.x, tt.y, tt.z - H0), "body" if parent == "root" else parent))
    return out


def beast_rig(body, neck, head, jaw, tail, fl, bl, rider=RIDER):
    up = (0, 0, 1)
    bones = [("root", (0, 0, -H0), (0, 0, 0.17), None),
             ("body", *body, "root", up), ("neck_q", *neck, "body", up), ("head_q", *head, "neck_q", up),
             ("jaw", *jaw, "head_q", up), ("tail", *tail, "body", (0, 1, 0))]
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        for pre, (a, b, c) in (("fl", fl), ("bl", bl)):
            m = lambda p: (p[0] * s, p[1], p[2])
            bones.append((f"{pre}_up.{x}", m(a), m(b), "body"))
            bones.append((f"{pre}_lo.{x}", m(b), m(c), f"{pre}_up.{x}"))
    return bones + rider_bones(rider)


HORSE = beast_rig(body=((0, 0.38, 0.92), (0, -0.32, 0.95)), neck=((0, -0.3, 1.0), (0, -0.5, 1.32)),
                  head=((0, -0.5, 1.32), (0, -0.74, 1.1)), jaw=((0, -0.56, 1.2), (0, -0.74, 1.06)),
                  tail=((0, 0.46, 0.98), (0, 0.6, 0.62)),
                  fl=((0.13, -0.27, 0.8), (0.13, -0.29, 0.42), (0.13, -0.27, 0.03)),
                  bl=((0.13, 0.33, 0.82), (0.13, 0.4, 0.42), (0.13, 0.35, 0.03)))
WOLF = beast_rig(body=((0, 0.38, 0.82), (0, -0.3, 0.92)), neck=((0, -0.28, 0.95), (0, -0.48, 1.12)),
                 head=((0, -0.48, 1.12), (0, -0.8, 1.02)), jaw=((0, -0.54, 1.02), (0, -0.82, 0.94)),
                 tail=((0, 0.44, 0.92), (0, 0.78, 0.72)),
                 fl=((0.15, -0.24, 0.76), (0.15, -0.21, 0.4), (0.15, -0.26, 0.03)),
                 bl=((0.14, 0.32, 0.8), (0.14, 0.44, 0.42), (0.14, 0.36, 0.03)))


def limb(k, slot, bone, rig_b, r1, r2):
    M, L = along(*rig_b[bone]); k.cyl(slot, bone, M, r1, r2, L, seg=8)


def knight_knight(k):
    """Paladin: armoured knight on a barded grey warhorse - red caparison with gold crosses, chanfron, kite shield."""
    RB = {n: (h, t) for n, h, t, *_ in HORSE}
    plinth(k)
    # legs (tops hidden under the caparison), hooves
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        limb(k, "Horse", f"fl_up.{x}", RB, 0.075, 0.055)
        limb(k, "Horse", f"fl_lo.{x}", RB, 0.045, 0.04)
        limb(k, "Horse", f"bl_up.{x}", RB, 0.09, 0.055)
        limb(k, "Horse", f"bl_lo.{x}", RB, 0.045, 0.04)
        for pre in ("fl", "bl"):
            kx, ky, kz = RB[f"{pre}_lo.{x}"][0]
            k.sphere("Horse", f"{pre}_lo.{x}", T(kx, ky, kz), (0.055, 0.055, 0.055), seg=8)
            hx, hy, hz = RB[f"{pre}_lo.{x}"][1]
            k.cyl("Hair", f"{pre}_lo.{x}", T(hx, hy, hz + 0.09), 0.06, 0.045, 0.06, seg=8)   # feathering
            k.cyl("Dark", f"{pre}_lo.{x}", T(hx, hy - 0.01, hz + 0.03), 0.065, 0.058, 0.06, seg=10)
    # body under a caparison: domed top, side skirts, gold hems and crosses
    k.sphere("Horse", "body", T(0, 0.03, 0.9), (0.2, 0.44, 0.21), seg=14)
    k.sphere("Horse", "body", T(0, -0.27, 0.95), (0.2, 0.2, 0.22), seg=12)
    k.cyl("Cloth", "body", T(0, 0.04, 0.92) @ R(x=90), 0.24, 0.24, 0.96, seg=16)
    for s in (1, -1):
        k.box("Cloth", "body", T(0.235 * s, 0.04, 0.76), (0.02, 0.96, 0.32))
        k.box("Trim", "body", T(0.245 * s, 0.04, 0.61), (0.012, 0.97, 0.03))
        for y in (-0.22, 0.28):
            k.box("Trim", "body", T(0.248 * s, y, 0.82), (0.008, 0.045, 0.2))
            k.box("Trim", "body", T(0.248 * s, y, 0.85), (0.008, 0.15, 0.045))
    for y, z in ((-0.44, 0.76), (0.52, 0.76)):
        k.box("Cloth", "body", T(0, y, z), (0.47, 0.02, 0.32))
        k.box("Trim", "body", T(0, y + (0.012 if y > 0 else -0.012), 0.61), (0.48, 0.012, 0.03))
    k.box("Trim", "body", T(0, -0.452, 0.84), (0.045, 0.008, 0.2))
    k.box("Trim", "body", T(0, -0.452, 0.87), (0.15, 0.008, 0.045))
    # saddle
    k.box("Leather", "body", T(0, 0.1, 1.17), (0.28, 0.34, 0.05), bevel=0.015)
    k.box("Leather", "body", T(0, 0.27, 1.22), (0.24, 0.04, 0.1), bevel=0.015)
    k.box("Trim", "body", T(0, -0.07, 1.21), (0.16, 0.04, 0.06), bevel=0.01)
    # neck with mane and crinet plates
    M, L = along((0, -0.27, 0.97), (0, -0.51, 1.34)); k.cyl("Horse", "neck_q", M, 0.14, 0.095, L, seg=10)
    M, L = along((0, -0.24, 1.07), (0, -0.46, 1.4)); k.box("Hair", "neck_q", M, (0.04, 0.07, L), bevel=0.01)
    for t in (0.25, 0.5, 0.75):
        p = (0, -0.27 - 0.24 * t, 0.97 + 0.37 * t)
        k.box("Armor", "neck_q", T(p[0], p[1] - 0.02, p[2] + 0.03) @ R(x=-33), (0.2, 0.14, 0.06), bevel=0.015)
    # head with chanfron, plume, ears, eyes
    M, L = along((0, -0.49, 1.36), (0, -0.76, 1.09))
    k.box("Horse", "head_q", M, (0.13, 0.15, L), bevel=0.04)
    k.box("Dark", "head_q", T(0, -0.78, 1.07), (0.1, 0.06, 0.08), bevel=0.02)
    k.box("Armor", "head_q", M @ Matrix.Translation((0, -0.07, 0.02)), (0.14, 0.02, L * 0.85), bevel=0.01)
    k.box("Trim", "head_q", M @ Matrix.Translation((0, -0.083, 0.02)), (0.025, 0.01, L * 0.7))
    k.cyl("Accent", "head_q", T(0, -0.52, 1.46) @ R(x=-20), 0.035, 0.07, 0.14, seg=8)
    for s in (1, -1):
        k.cyl("Horse", "head_q", T(0.045 * s, -0.47, 1.42) @ R(y=12 * s), 0.025, 0.0, 0.09, seg=5)
        k.sphere("Dark", "head_q", T(0.07 * s, -0.56, 1.29), (0.018, 0.022, 0.018), seg=6)
    # tail
    M, L = along(*RB["tail"]); k.cyl("Hair", "tail", M, 0.05, 0.075, L, seg=8)
    # the knight in the saddle: shared knight parts, scaled and placed by RIDER
    k.xf = RIDER
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        M, L = along((0.13 * s, 0, 0.95), (0.3 * s, -0.25, 0.78)); k.cyl("Armor", "hips", M, 0.08, 0.07, L, seg=8)
        k.sphere("Armor", "hips", T(0.3 * s, -0.27, 0.78), (0.07, 0.07, 0.07), seg=8)
        M, L = along((0.3 * s, -0.25, 0.78), (0.31 * s, -0.17, 0.42)); k.cyl("Armor", "hips", M, 0.065, 0.055, L, seg=8)
        k.box("Dark", "hips", T(0.31 * s, -0.21, 0.37), (0.1, 0.22, 0.09), bevel=0.025)
        k.box("Trim", "hips", T(0.31 * s, -0.19, 0.32), (0.11, 0.06, 0.015))
    knight_torso(k)
    k.cyl("Armor", "chest", T(0, 0, 1.53), 0.13, 0.11, 0.09, seg=10)
    knight_arms(k, trim=True)
    cape(k, "Cloth", 0.44, 0.6, 1.5, 0.9, tilt=30)
    k.cyl("Armor", "head", T(0, 0, 1.76), 0.155, 0.15, 0.30, seg=12, bevel=0.01)
    k.cyl("Trim", "head", T(0, 0, 1.935), 0.15, 0.08, 0.07, seg=12)
    k.box("Dark", "head", T(0, -0.145, 1.78), (0.25, 0.04, 0.035))
    k.box("Trim", "head", T(0, -0.155, 1.71), (0.035, 0.03, 0.18))
    k.box("Accent", "head", T(0, 0.03, 2.02), (0.04, 0.3, 0.14), bevel=0.02)
    k.cyl("Accent", "head", T(0, 0.2, 1.93) @ R(x=-30), 0.03, 0.0, 0.2, seg=6)
    sword(k, length=0.95)
    kite = [(0, 0.28), (0.19, 0.22), (0.2, 0.02), (0.0, -0.36), (-0.2, 0.02), (-0.19, 0.22)]
    SM = T(0.44, -0.07, 1.02) @ R(z=37) @ R(x=-8)
    k.prism("Trim", "forearm.L", SM, [(u * 1.08, v * 1.06) for u, v in kite], 0.03)
    k.prism("Accent", "forearm.L", SM @ Matrix.Translation((0, -0.014, 0)), kite, 0.03)
    k.box("Trim", "forearm.L", SM @ Matrix.Translation((0, -0.032, -0.02)), (0.045, 0.012, 0.46))
    k.box("Trim", "forearm.L", SM @ Matrix.Translation((0, -0.032, 0.1)), (0.3, 0.012, 0.045))
    k.xf = Matrix()


def undead_knight(k):
    """Dire Wolf: huge rotting wolf - bare skull muzzle, glowing eyes and maw, exposed ribs, bone spine, spiked collar."""
    RB = {n: (h, t) for n, h, t, *_ in WOLF}
    plinth(k)
    # legs: furred fronts, bony hind shanks, clawed paws
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        limb(k, "Fur", f"fl_up.{x}", RB, 0.09, 0.06)
        limb(k, "Fur", f"fl_lo.{x}", RB, 0.055, 0.045)
        k.sphere("Fur", f"bl_up.{x}", T(0.14 * s, 0.34, 0.74), (0.1, 0.15, 0.15), seg=10)
        limb(k, "Fur", f"bl_up.{x}", RB, 0.08, 0.05)
        limb(k, "Bone", f"bl_lo.{x}", RB, 0.03, 0.026)
        k.sphere("Bone", f"bl_lo.{x}", T(*RB[f"bl_lo.{x}"][0]), (0.045, 0.045, 0.045), seg=8)
        for pre in ("fl", "bl"):
            hx, hy, hz = RB[f"{pre}_lo.{x}"][1]
            k.box("FurDark", f"{pre}_lo.{x}", T(hx, hy - 0.03, hz + 0.03), (0.11, 0.15, 0.06), bevel=0.02)
            for c in (-0.03, 0.0, 0.03):
                k.cyl("Bone", f"{pre}_lo.{x}", T(hx + c, hy - 0.115, hz + 0.02) @ R(x=100), 0.011, 0.0, 0.05, seg=4)
    # body: deep chest, gaunt waist with exposed ribs, haunches
    k.sphere("Fur", "body", T(0, -0.16, 0.9), (0.23, 0.26, 0.27), seg=14)
    k.sphere("Fur", "body", T(0, 0.12, 0.86), (0.16, 0.24, 0.17), seg=12)
    k.sphere("Fur", "body", T(0, 0.33, 0.86), (0.17, 0.17, 0.18), seg=12)
    for s in (1, -1):
        k.box("Dark", "body", T(0.135 * s, 0.1, 0.85), (0.04, 0.24, 0.17))
        for y in (0.0, 0.065, 0.13, 0.195):
            k.box("Bone", "body", T(0.155 * s, y, 0.85) @ R(x=12), (0.025, 0.025, 0.2), bevel=0.008)
    for i in range(7):
        y = -0.32 + i * 0.11
        z = 1.17 - abs(y + 0.15) * 0.35
        k.cyl("Bone", "body", T(0, y, z) @ R(x=15), 0.025, 0.0, 0.1 - 0.008 * i, seg=5)
    # torn shroud over the back
    k.prism("Royal", "body", T(0, 0.15, 1.02) @ R(z=90), [(-0.17, 0.0), (0.17, 0.0), (0.15, -0.2), (0.07, -0.12), (0.0, -0.25), (-0.08, -0.13), (-0.16, -0.22)], 0.36)
    # spiked collar at the neck root
    k.torus("Leather", "neck_q", T(0, -0.3, 1.0) @ R(x=-40), 0.17, 0.03, su=14)
    for i in range(7):
        a = 2 * math.pi * i / 7
        k.cyl("Steel", "neck_q", T(0, -0.3, 1.0) @ R(x=-40) @ Matrix.Translation((0.19 * math.cos(a), 0.19 * math.sin(a), 0)) @ R(y=90) @ R(x=-math.degrees(a)),
              0.018, 0.0, 0.07, seg=4)
    # neck and mane spikes
    M, L = along((0, -0.26, 0.93), (0, -0.5, 1.13)); k.cyl("Fur", "neck_q", M, 0.17, 0.12, L, seg=10)
    for i in range(4):
        t = i / 3
        k.cyl("FurDark", "neck_q", T(0, -0.26 - 0.2 * t, 1.08 + 0.15 * t) @ R(x=35), 0.04, 0.0, 0.14, seg=5)
    # head: furred skull, bare bone muzzle, fangs, glowing eyes and maw
    k.box("Fur", "head_q", T(0, -0.56, 1.11), (0.22, 0.2, 0.17), bevel=0.05)
    M, L = along((0, -0.62, 1.08), (0, -0.84, 1.04)); k.box("Bone", "head_q", M, (0.11, 0.09, L), bevel=0.025)
    k.box("Dark", "head_q", T(0, -0.85, 1.06), (0.05, 0.02, 0.025))
    for s in (1, -1):
        k.box("Dark", "head_q", T(0.06 * s, -0.645, 1.135), (0.05, 0.03, 0.035), bevel=0.008)
        k.sphere("Glow", "head_q", T(0.06 * s, -0.66, 1.135), (0.018, 0.012, 0.014), seg=6)
        k.cyl("Fur", "head_q", T(0.075 * s, -0.5, 1.23) @ R(x=-15, y=15 * s), 0.045, 0.0, 0.13, seg=5)
        k.cyl("Bone", "head_q", T(0.035 * s, -0.8, 0.99) @ R(x=180), 0.012, 0.0, 0.06, seg=4)
    k.box("Glow", "head_q", T(0, -0.7, 1.0), (0.07, 0.17, 0.012))
    # lower jaw with fangs
    M, L = along((0, -0.56, 0.99), (0, -0.82, 0.94)); k.box("Bone", "jaw", M, (0.09, 0.05, L), bevel=0.015)
    for s in (1, -1):
        k.cyl("Bone", "jaw", T(0.03 * s, -0.79, 0.98), 0.01, 0.0, 0.05, seg=4)
    # tail fraying into bone
    M, L = along((0, 0.44, 0.92), (0, 0.66, 0.8)); k.cyl("FurDark", "tail", M, 0.06, 0.045, L, seg=8)
    for i in range(3):
        p = (0, 0.68 + 0.04 * i, 0.79 - 0.025 * i)
        k.sphere("Bone", "tail", T(*p), (0.02, 0.02, 0.02), seg=6)


# ---------------------------------------------------------------- beast clip set (horse and wolf, with or without a rider)
SEAT = dict(upperarm_L=(30, 0, 0), forearm_L=(50, 0, 0), upperarm_R=(25, 0, 0), forearm_R=(45, 0, 0), hips=(6, 0, 0))


def bpose(**bones):
    return pose(**{**SEAT, **bones})


STRIDE_A = dict(fl_up_L=(30, 0, 0), fl_lo_L=(-10, 0, 0), fl_up_R=(20, 0, 0), fl_lo_R=(-5, 0, 0),
                bl_up_L=(-25, 0, 0), bl_lo_L=(15, 0, 0), bl_up_R=(-18, 0, 0), bl_lo_R=(10, 0, 0),
                body=(4, 0, 0), neck_q=(-6, 0, 0), tail=(10, 0, 0), hips=(10, 0, 0))
STRIDE_B = dict(fl_up_L=(-25, 0, 0), fl_lo_L=(-45, 0, 0), fl_up_R=(-18, 0, 0), fl_lo_R=(-35, 0, 0),
                bl_up_L=(30, 0, 0), bl_lo_L=(-15, 0, 0), bl_up_R=(22, 0, 0), bl_lo_R=(-10, 0, 0),
                body=(-4, 0, 0), neck_q=(6, 0, 0), tail=(-5, 0, 0), hips=(2, 0, 0), upperarm_R=(35, 0, 0))
REAR = dict(body=(28, 0, 0), bl_up_L=(-28, 0, 0), bl_up_R=(-28, 0, 0), bl_lo_L=(10, 0, 0), bl_lo_R=(10, 0, 0),
            fl_up_L=(75, 0, 0), fl_lo_L=(-95, 0, 0), fl_up_R=(60, 0, 0), fl_lo_R=(-80, 0, 0),
            neck_q=(-10, 0, 0), head_q=(10, 0, 0), jaw=(-30, 0, 0), tail=(20, 0, 0))

BEAST_CLIPS = {
    "Idle": (60, True, [
        (0, bpose(), {}),
        (30, bpose(neck_q=(-4, 0, 0), head_q=(-5, 0, 0), tail=(0, 0, 10), chest=(3, 0, 0)), {"body": (0, 0, -0.008)}),
        (60, bpose(), {}),
    ]),
    # canter: two strides per loop
    "Walk": (24, True, [
        (0, bpose(**STRIDE_A), {"body": (0, 0, 0.06)}),
        (6, bpose(**STRIDE_B), {"body": (0, 0, 0)}),
        (12, bpose(**STRIDE_A), {"body": (0, 0, 0.06)}),
        (18, bpose(**STRIDE_B), {"body": (0, 0, 0)}),
        (24, bpose(**STRIDE_A), {"body": (0, 0, 0.06)}),
    ]),
    # rear up with the sword high, then crash down and strike (the wolf lunges and bites)
    "Attack": (32, False, [
        (0, bpose(), {}),
        (8, bpose(**REAR, hips=(24, 0, 0), upperarm_R=(150, 0, -15), forearm_R=(50, 0, 0)), {"body": (0, 0, 0.02)}),
        (14, bpose(body=(-6, 0, 0), bl_up_L=(8, 0, 0), bl_up_R=(8, 0, 0), fl_up_L=(-5, 0, 0), fl_up_R=(-8, 0, 0),
                   neck_q=(6, 0, 0), head_q=(-10, 0, 0), jaw=(-35, 0, 0), tail=(10, 0, 0),
                   hips=(12, 0, 0), spine=(15, -10, 0), upperarm_R=(45, 0, 0), forearm_R=(5, 0, 0)), {"body": (0, 0.14, -0.03)}),
        (20, bpose(body=(-5, 0, 0), bl_up_L=(6, 0, 0), bl_up_R=(6, 0, 0), neck_q=(4, 0, 0), head_q=(-8, 0, 0), jaw=(-10, 0, 0),
                   hips=(12, 0, 0), spine=(18, -12, 0), upperarm_R=(25, 0, 0), forearm_R=(0, 0, 0)), {"body": (0, 0.12, -0.03)}),
        (32, bpose(), {}),
    ]),
    # rear and roar / whinny, sword raised to the sky
    "Shoot": (34, False, [
        (0, bpose(), {}),
        (8, bpose(**{**REAR, "body": (18, 0, 0), "bl_up_L": (-18, 0, 0), "bl_up_R": (-18, 0, 0),
                     "neck_q": (15, 0, 0), "head_q": (25, 0, 0), "jaw": (-40, 0, 0), "hips": (16, 0, 0),
                     "upperarm_R": (165, 0, -10), "forearm_R": (20, 0, 0)}), {}),
        (22, bpose(**{**REAR, "body": (20, 0, 0), "bl_up_L": (-20, 0, 0), "bl_up_R": (-20, 0, 0), "fl_up_L": (45, 0, 0), "fl_up_R": (80, 0, 0),
                      "neck_q": (18, 0, 0), "head_q": (25, 0, 0), "jaw": (-35, 0, 0), "hips": (16, 0, 0),
                      "upperarm_R": (170, 0, -10), "forearm_R": (15, 0, 0)}), {}),
        (34, bpose(), {}),
    ]),
    # stagger, buckle and fall onto the side
    "Death": (46, False, [
        (0, bpose(), {}),
        (8, bpose(body=(-6, 4, 0), neck_q=(-10, 0, 0), head_q=(15, 0, 0), jaw=(-25, 0, 0), fl_up_L=(15, 0, 0),
                  hips=(-15, 0, 0), upperarm_R=(55, 0, -20), upperarm_L=(20, 0, 25)), {"body": (0, 0, -0.04)}),
        (20, bpose(body=(-4, 40, 0), fl_up_L=(40, 0, 0), fl_lo_L=(-90, 0, 0), fl_up_R=(40, 0, 0), fl_lo_R=(-90, 0, 0),
                   bl_up_L=(35, 0, 0), bl_lo_L=(-40, 0, 0), bl_up_R=(35, 0, 0), bl_lo_R=(-40, 0, 0),
                   neck_q=(-15, 0, 0), head_q=(-10, 0, 0), jaw=(-20, 0, 0), hips=(0, 0, -25), upperarm_R=(60, 0, -40)),
         {"body": (0, 0, -0.3)}),
        (46, bpose(body=(0, 85, 0), fl_up_L=(30, 0, 0), fl_lo_L=(-40, 0, 0), fl_up_R=(20, 0, 0), fl_lo_R=(-30, 0, 0),
                   bl_up_L=(20, 0, 0), bl_lo_L=(-20, 0, 0), bl_up_R=(10, 0, 0), bl_lo_R=(-15, 0, 0),
                   neck_q=(-15, 0, 0), head_q=(-20, 0, 0), jaw=(-15, 0, 0), tail=(-10, 0, 0),
                   hips=(0, 0, -20), chest=(10, 0, 0), upperarm_R=(70, 0, -45), upperarm_L=(30, 0, 40)),
         {"body": (0, 0, -0.7)}),
    ]),
}


# ---------------------------------------------------------------- Mafia: shared parts
def tommy_gun(k):
    """Tommy gun in the right hand. Modelled hanging down along the arm, so it is carried low at rest
    and comes level when the Shoot clip raises the arm. Local layout: -Y = muzzle, -Z = grip."""
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


def mafia_legs(k, slot="Suit", spats=True):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box(slot, f"thigh.{x}", T(0.12 * s, 0, 0.77), (0.15, 0.16, 0.42), bevel=0.03)
        M, L = along((0.12 * s, 0.02, 0.1), (0.12 * s, 0.0, 0.57))
        k.cyl(slot, f"shin.{x}", M, 0.068, 0.062, L, seg=8)
        if spats:
            k.cyl("Shirt", f"foot.{x}", T(0.12 * s, 0.005, 0.1), 0.066, 0.06, 0.06, seg=8)
        k.box("Dark", f"foot.{x}", T(0.12 * s, -0.04, 0.04), (0.11, 0.25, 0.08), bevel=0.03)


def mafia_jacket(k, slot="Suit", tie="Tie", lapel="Lapel", stripes=False):
    k.cyl(slot, "hips", T(0, 0, 0.92), 0.235, 0.215, 0.22, seg=10)
    k.box(slot, "spine", T(0, 0, 1.13), (0.36, 0.24, 0.26), bevel=0.05)
    k.box(slot, "chest", T(0, 0, 1.36), (0.44, 0.27, 0.30), bevel=0.07)
    k.box("Shirt", "chest", T(0, -0.13, 1.39), (0.13, 0.02, 0.2))
    if tie:
        k.box(tie, "chest", T(0, -0.145, 1.34), (0.045, 0.015, 0.24))
        k.box(tie, "chest", T(0, -0.145, 1.475), (0.055, 0.02, 0.04))
    for s in (1, -1):
        k.box(lapel, "chest", T(0.075 * s, -0.142, 1.37) @ R(y=-18 * s), (0.05, 0.015, 0.26))
        if stripes:
            for dx in (0.13, 0.18):
                k.box("Stripe", "chest", T(dx * s, -0.138, 1.3), (0.006, 0.006, 0.36))
    k.cyl("Shirt", "chest", T(0, 0, 1.53), 0.095, 0.09, 0.05, seg=10)


def mafia_arms(k, slot="Suit", pads=True):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        if pads:
            k.box(slot, f"upper_arm.{x}", T(0.21 * s, 0, 1.46), (0.13, 0.21, 0.1), bevel=0.04)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl(slot, f"upper_arm.{x}", M, 0.058, 0.064, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl(slot, f"forearm.{x}", M, 0.06, 0.054, L - 0.03, seg=8)
        h, t = B[f"forearm.{x}"]
        k.cyl("Shirt", f"forearm.{x}", T(*t) @ Matrix.Translation((0, 0, 0.02)), 0.05, 0.05, 0.035, seg=8)
        k.box("Skin", f"hand.{x}", T(*mid(f"hand.{x}")), (0.075, 0.09, 0.1), bevel=0.02)


def mafia_face(k, brow="Hat", jaw=True, temples=None, tache=None):
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.06, 0.06, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.125, 0.135, 0.14), seg=12)
    if jaw:
        k.box("Skin", "head", T(0, -0.035, 1.665), (0.18, 0.17, 0.09), bevel=0.035)
    for s in (1, -1):
        k.sphere("Skin", "head", T(0.125 * s, 0.0, 1.74), (0.025, 0.035, 0.04), seg=6)
        k.box("Dark", "head", T(0.048 * s, -0.125, 1.755), (0.035, 0.02, 0.018))
        k.box(brow, "head", T(0.05 * s, -0.128, 1.785) @ R(y=8 * s), (0.055, 0.02, 0.016))
        if temples:
            k.box(temples, "head", T(0.122 * s, 0.03, 1.78), (0.03, 0.13, 0.08), bevel=0.01)
    k.box("Skin", "head", T(0, -0.14, 1.72), (0.035, 0.03, 0.05), bevel=0.01)  # nose
    k.box("Lapel", "head", T(0, -0.12, 1.66), (0.06, 0.01, 0.008))             # mouth
    if tache:
        k.box(tache, "head", T(0, -0.135, 1.685), (0.1, 0.022, 0.022), bevel=0.006)


def fedora(k, hat="Hat", band="Band", tilt=6):
    HM = T(0, 0.0, 1.87) @ R(x=tilt, y=-5)
    k.cyl(hat, "head", HM @ S(1, 0.9, 1), 0.235, 0.235, 0.02, seg=16)
    k.cyl(hat, "head", HM @ Matrix.Translation((0, 0.01, 0.075)), 0.15, 0.125, 0.13, seg=12, bevel=0.012)
    k.cyl(band, "head", HM @ Matrix.Translation((0, 0.01, 0.03)), 0.153, 0.152, 0.035, seg=12)
    k.box(hat, "head", HM @ Matrix.Translation((0, -0.02, 0.145)), (0.05, 0.16, 0.02))


# ---------------------------------------------------------------- Mafia: The Boss (king)
def mafia_king(k):
    """The Boss: double-breasted pinstripes, camel overcoat over the shoulders, white fedora, cigar, gold-topped cane."""
    plinth(k)
    mafia_legs(k)
    mafia_jacket(k, tie="Accent", stripes=True)
    for s in (1, -1):
        for z in (1.2, 1.3):
            k.sphere("Accent", "chest", T(0.065 * s, -0.138, z), (0.014, 0.008, 0.014), seg=6)
    k.sphere("Accent", "chest", T(0, -0.155, 1.42), (0.012, 0.008, 0.012), seg=6)   # tie pin
    mafia_arms(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        hx, hy, hz = mid(f"hand.{x}")
        k.box("Accent", f"hand.{x}", T(hx, hy - 0.035, hz - 0.02), (0.08, 0.02, 0.022))  # rings
    # camel overcoat worn over the shoulders, arms free
    cape(k, "Coat", 0.6, 0.8, 1.52, 0.48, back=0.16, tilt=5)
    for s in (1, -1):
        k.box("Coat", "chest", T(0.275 * s, 0.03, 1.17), (0.08, 0.3, 0.66), bevel=0.03)
    k.cyl("Coat", "chest", T(0, 0.04, 1.51), 0.21, 0.17, 0.07, seg=12, bevel=0.015)
    mafia_face(k, temples="GreyHair", tache="GreyHair")
    k.box("Skin", "head", T(0, -0.05, 1.64), (0.2, 0.16, 0.06), bevel=0.025)        # heavy jowls
    M, L = along((0.035, -0.13, 1.665), (0.075, -0.25, 1.645)); k.cyl("Cigar", "head", M, 0.013, 0.013, L, seg=6)
    k.sphere("Ember", "head", T(0.077, -0.255, 1.645), (0.014, 0.012, 0.014), seg=6)
    fedora(k, hat="WhiteHat", band="Lapel", tilt=3)
    # gold-topped cane in the right hand
    hx, hy, hz = mid("hand.R")
    M, L = along((hx, hy - 0.02, hz + 0.06), (hx + 0.05, hy - 0.12, 0.03)); k.cyl("Dark", "hand.R", M, 0.014, 0.012, L, seg=6)
    k.sphere("Accent", "hand.R", T(hx, hy - 0.015, hz + 0.08), (0.03, 0.03, 0.03), seg=8)


# ---------------------------------------------------------------- Mafia: Underboss (queen)
def mafia_queen(k):
    """Underboss: belted trench coat with the collar up, hat brim low, scarred face, Tommy gun."""
    plinth(k)
    mafia_legs(k, spats=False)
    k.cyl("Trench", "hips", T(0, 0, 0.66), 0.31, 0.225, 0.64, seg=12)
    k.box("Trench", "hips", T(0, -0.25, 0.66), (0.025, 0.02, 0.6))                   # front opening
    mafia_jacket(k, slot="Trench", tie="Tie", lapel="Trench")
    k.cyl("Leather", "hips", T(0, 0, 0.99), 0.235, 0.235, 0.06, seg=12)
    k.box("Metal", "hips", T(0, -0.235, 0.99), (0.06, 0.02, 0.05), bevel=0.008)
    for s in (1, -1):
        k.box("Trench", "chest", T(0.115 * s, 0.03, 1.58) @ R(y=-12 * s), (0.05, 0.15, 0.14), bevel=0.015)   # collar up
        k.box("Trench", "chest", T(0.2 * s, 0, 1.5), (0.1, 0.18, 0.03))                                         # epaulettes
        k.sphere("Metal", "chest", T(0.08 * s, -0.14, 1.2), (0.015, 0.008, 0.015), seg=6)
    mafia_arms(k, slot="Trench", pads=False)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.cyl("Trench", f"forearm.{x}", T(*B[f"forearm.{x}"][1]) @ Matrix.Translation((0, 0, 0.07)), 0.068, 0.068, 0.04, seg=8)
    mafia_face(k, brow="Hair")
    k.box("Stubble", "head", T(0, -0.06, 1.645), (0.17, 0.14, 0.05), bevel=0.02)
    k.box("Scar", "head", T(-0.07, -0.122, 1.71) @ R(y=35), (0.08, 0.01, 0.01))
    fedora(k, tilt=12)
    tommy_gun(k)


# ---------------------------------------------------------------- Mafia: Consigliere (bishop)
def mafia_bishop(k):
    """Consigliere: grey three-piece with a watch chain and bow tie, bowler hat, round spectacles, briefcase, revolver."""
    plinth(k)
    mafia_legs(k, slot="Grey", spats=False)
    mafia_jacket(k, slot="Grey", tie=None, lapel="Grey")
    k.box("Suit", "chest", T(0, -0.137, 1.26), (0.17, 0.02, 0.26))                     # waistcoat
    for z in (1.17, 1.24, 1.31):
        k.sphere("Accent", "chest", T(0, -0.15, z), (0.01, 0.006, 0.01), seg=6)
    M, L = along((-0.07, -0.15, 1.22), (0.0, -0.152, 1.19)); k.box("Accent", "chest", M, (0.008, 0.006, L))
    M, L = along((0.0, -0.152, 1.19), (0.07, -0.15, 1.22)); k.box("Accent", "chest", M, (0.008, 0.006, L))
    for s in (1, -1):                                                                   # bow tie
        k.prism("Tie", "chest", T(0, -0.15, 1.475), [(0, 0), (0.05 * s, 0.025), (0.05 * s, -0.025)], 0.02)
    k.sphere("Tie", "chest", T(0, -0.152, 1.475), (0.014, 0.01, 0.014), seg=6)
    mafia_arms(k, slot="Grey")
    mafia_face(k, brow="GreyHair", jaw=False, temples="GreyHair", tache="GreyHair")
    k.box("Skin", "head", T(0, -0.03, 1.665), (0.16, 0.15, 0.08), bevel=0.035)
    for s in (1, -1):                                                                   # spectacles
        k.torus("Accent", "head", T(0.048 * s, -0.138, 1.755) @ R(x=90), 0.03, 0.004, su=12, sv=4)
        k.box("Accent", "head", T(0.1 * s, -0.07, 1.765), (0.005, 0.13, 0.005))
    k.box("Accent", "head", T(0, -0.14, 1.76), (0.03, 0.005, 0.005))
    # bowler hat
    k.sphere("Hat", "head", T(0, 0.0, 1.86), (0.145, 0.15, 0.12), seg=14)
    k.cyl("Hat", "head", T(0, 0.0, 1.85), 0.2, 0.2, 0.018, seg=16)
    k.cyl("Lapel", "head", T(0, 0.0, 1.87), 0.148, 0.146, 0.03, seg=14)
    # briefcase in the left hand
    hx, hy, hz = mid("hand.L")
    k.box("Leather", "hand.L", T(hx + 0.02, hy, hz - 0.17), (0.09, 0.32, 0.23), bevel=0.015)
    k.box("Accent", "hand.L", T(hx + 0.068, hy, hz - 0.1), (0.005, 0.05, 0.02))
    k.torus("Dark", "hand.L", T(hx + 0.02, hy, hz - 0.05) @ R(y=90), 0.035, 0.008, su=10, sv=4)
    # snub revolver in the right hand (hangs down at rest, levels when the arm comes up)
    gx, gy, gz = mid("hand.R")
    G = lambda dx, dy, dz: T(gx, gy, gz) @ R(x=90) @ Matrix.Translation((dx, dy, dz))
    k.box("Metal", "hand.R", G(0.0, -0.09, 0.04), (0.035, 0.13, 0.05), bevel=0.008)
    k.cyl("Metal", "hand.R", G(0.0, -0.06, 0.04) @ R(x=90), 0.03, 0.03, 0.05, seg=8)
    k.box("Wood", "hand.R", G(0.0, 0.0, -0.02) @ R(x=-15), (0.03, 0.04, 0.08), bevel=0.008)


# ---------------------------------------------------------------- Mafia: Speakeasy (rook)
def mafia_rook(k):
    """Speakeasy: narrow brick bar - green awning, peephole door, neon cocktail sign, lit windows, barrel catapult."""
    plinth(k)
    w = 0.3
    k.box("BrickDark", "hips", T(0, 0, 0.04), (0.66, 0.66, 0.08))
    k.box("Brick", "hips", T(0, 0, 0.59), (2 * w, 2 * w, 1.02))
    k.box("Brick", "spine", T(0, 0, 1.19), (2 * w - 0.02, 2 * w - 0.02, 0.18))
    k.box("BrickDark", "hips", T(0, 0, 1.1), (2 * w + 0.03, 2 * w + 0.03, 0.05))
    rnd = random.Random(11)
    for _ in range(26):                                     # brick texture
        side, u, z = rnd.choice((1, 2, 3)), rnd.uniform(-0.24, 0.24), rnd.uniform(0.15, 1.05)
        if side == 1:
            k.box("BrickDark", "hips", T(w, u, z), (0.016, 0.1, 0.045))
        elif side == 2:
            k.box("BrickDark", "hips", T(-w, u, z), (0.016, 0.1, 0.045))
        elif abs(u) > 0.13:
            k.box("BrickDark", "hips", T(u, -w, z + 0.3 if z < 0.5 else z), (0.1, 0.016, 0.045))
    # door with a peephole slit and a lamp, steps
    k.box("Dark", "hips", T(0, -w - 0.005, 0.24), (0.2, 0.02, 0.4))
    k.box("Wood", "hips", T(0, -w - 0.012, 0.23), (0.16, 0.02, 0.36))
    k.box("Dark", "hips", T(0, -w - 0.024, 0.33), (0.07, 0.01, 0.018))
    k.box("Lamp", "hips", T(0, -w - 0.02, 0.33), (0.05, 0.004, 0.01))
    k.sphere("Accent", "hips", T(0.055, -w - 0.03, 0.22), (0.012, 0.01, 0.012), seg=6)
    k.box("BrickDark", "root", T(0, -0.36, 0.02), (0.26, 0.1, 0.04))
    k.sphere("Lamp", "hips", T(-0.14, -w - 0.03, 0.47), (0.025, 0.025, 0.03), seg=8)
    # striped green awning over the door
    k.box("Awning", "hips", T(0, -w - 0.08, 0.5) @ R(x=-30), (0.34, 0.02, 0.2))
    for u in (-0.12, 0.0, 0.12):
        k.box("Shirt", "hips", T(u, -w - 0.085, 0.5) @ R(x=-30), (0.04, 0.025, 0.2))
    # lit windows with blinds
    for z, bone in ((0.75, "hips"), (1.17, "spine")):
        for u in (-0.13, 0.13):
            k.box("BrickDark", bone, T(u, -w - 0.006, z), (0.13, 0.02, 0.17))
            k.box("Lamp", bone, T(u, -w - 0.012, z), (0.1, 0.01, 0.14))
            for dz in (-0.04, 0.0, 0.04):
                k.box("Dark", bone, T(u, -w - 0.02, z + dz), (0.1, 0.006, 0.012))
        k.box("Lamp", bone, T(w + 0.006, 0, z), (0.01, 0.1, 0.14))
    # neon cocktail-glass sign on a bracket at the front corner
    k.box("Metal", "spine", T(w - 0.02, -w - 0.06, 1.2), (0.02, 0.14, 0.02))
    SM = T(w - 0.02, -w - 0.13, 0.98)
    k.box("Dark", "spine", SM, (0.02, 0.13, 0.4), bevel=0.01)
    k.prism("Neon", "spine", SM @ Matrix.Translation((0.013, 0, 0.06)) @ R(z=90), [(-0.05, 0.07), (0.05, 0.07), (0.0, 0.0)], 0.006)
    k.box("Neon", "spine", SM @ Matrix.Translation((0.013, 0, -0.01)), (0.006, 0.01, 0.07))
    k.box("Neon", "spine", SM @ Matrix.Translation((0.013, 0, -0.05)), (0.006, 0.06, 0.01))
    k.sphere("Neon", "spine", SM @ Matrix.Translation((0.013, 0.03, 0.12)), (0.006, 0.015, 0.015), seg=6)
    # barrels by the door
    for (x, y, z) in ((-0.36, -0.3, 0.1), (-0.36, -0.12, 0.1), (-0.36, -0.21, 0.27)):
        k.cyl("Wood", "root", T(x, y, z) @ R(x=90), 0.075, 0.075, 0.15, seg=10)
        for e in (-0.05, 0.05):
            k.cyl("Metal", "root", T(x, y + e, z) @ R(x=90), 0.078, 0.078, 0.012, seg=10)
    # cornice, parapet, roof
    k.box("BrickDark", "chest", T(0, 0, 1.33), (2 * w + 0.06, 2 * w + 0.06, 0.1))
    k.box("Brick", "chest", T(0, 0, 1.43), (2 * w + 0.08, 2 * w + 0.08, 0.1))
    k.box("Dark", "chest", T(0, 0, 1.481), (2 * w, 2 * w, 0.004))
    e = w + 0.04
    for u in (-0.2, 0.2):
        for (mx, my) in ((u, -e + 0.02), (u, e - 0.02), (e - 0.02, u), (-e + 0.02, u)):
            k.box("BrickDark", "chest", T(mx, my, 1.52), (0.1, 0.1, 0.08), bevel=0.008)
    # rooftop catapult throwing a whiskey barrel
    def barrel(p):
        x, y, z = p
        k.cyl("Wood", "neck", T(x, y, z + 0.01), 0.045, 0.045, 0.08, seg=8)
        for dz in (-0.02, 0.04):
            k.cyl("Metal", "neck", T(x, y, z + dz), 0.048, 0.048, 0.01, seg=8)
    catapult(k, lambda M, L: k.box("Wood", "neck", M, (0.04, 0.035, L), bevel=0.006),
             "Wood", barrel, "Metal", "Wood")


# ---------------------------------------------------------------- Mafia: Getaway Car (knight)
# A 1920s open tourer on the beast rig: legs are wheels (each wheel spins about its *_lo bone head),
# the rider bones are the gangster at the wheel.
CAR_RIDER = Matrix.Translation((0, 0.08, -0.17)) @ S(0.72, 0.72, 0.72)
CAR = beast_rig(body=((0, 0.46, 0.36), (0, -0.46, 0.36)), neck=((0, -0.5, 0.5), (0, -0.55, 0.55)),
                head=((0, -0.55, 0.55), (0, -0.62, 0.55)), jaw=((0, -0.56, 0.5), (0, -0.62, 0.48)),
                tail=((0, 0.5, 0.4), (0, 0.55, 0.3)),
                fl=((0.27, -0.36, 0.36), (0.27, -0.36, 0.15), (0.27, -0.36, 0.0)),
                bl=((0.27, 0.32, 0.36), (0.27, 0.32, 0.15), (0.27, 0.32, 0.0)), rider=CAR_RIDER)


def mafia_knight(k):
    """Getaway Car: black open tourer with chrome grille and lamps, whitewall wheels, spare tyre, gangster with a Tommy gun."""
    plinth(k)
    # wheels: tyre, whitewall, hub and spokes so the spin reads
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        for pre, y in (("fl", -0.36), ("bl", 0.32)):
            bone = f"{pre}_lo.{x}"
            W = T(0.27 * s, y, 0.15) @ R(y=90)
            k.cyl("Dark", bone, W, 0.15, 0.15, 0.09, seg=16)
            k.cyl("Shirt", bone, W, 0.11, 0.11, 0.094, seg=16)
            k.cyl("Car", bone, W, 0.085, 0.085, 0.098, seg=12)
            k.cyl("Chrome", bone, W, 0.035, 0.035, 0.11, seg=8)
            for a in (0, 45, 90, 135):
                k.box("Chrome", bone, W @ R(z=a), (0.16, 0.012, 0.1))
    # chassis, body, running boards, fenders
    k.box("Dark", "body", T(0, -0.02, 0.24), (0.42, 1.04, 0.06))
    k.box("Car", "body", T(0, 0.04, 0.43), (0.5, 0.86, 0.3), bevel=0.04)
    k.box("Car", "body", T(0, -0.42, 0.48), (0.36, 0.34, 0.26), bevel=0.04)           # hood
    for s in (1, -1):
        k.box("Accent", "body", T(0.252 * s, 0.04, 0.5), (0.008, 0.84, 0.02))
        k.box("Dark", "body", T(0.3 * s, -0.02, 0.27), (0.1, 0.42, 0.025))               # running board
        for y in (-0.36, 0.32):                                                           # arched fenders
            k.box("Car", "body", T(0.28 * s, y, 0.335), (0.12, 0.2, 0.025), bevel=0.008)
            for e in (1, -1):
                k.box("Car", "body", T(0.28 * s, y + 0.14 * e, 0.29) @ R(x=40 * e), (0.12, 0.13, 0.025), bevel=0.008)
        for i, y in enumerate((-0.5, -0.44, -0.38, -0.32)):
            k.box("Chrome", "body", T(0.182 * s, y, 0.5), (0.006, 0.02, 0.12))           # hood louvres
    k.box("Car", "body", T(0, 0.04, 0.585), (0.5, 0.86, 0.02), bevel=0.008)              # body top rim
    # grille, bumper, headlamps
    k.box("Chrome", "body", T(0, -0.6, 0.46), (0.28, 0.04, 0.28), bevel=0.02)
    for z in (0.39, 0.44, 0.49, 0.54):
        k.box("Dark", "body", T(0, -0.622, z), (0.2, 0.01, 0.012))
    k.box("Chrome", "body", T(0, -0.66, 0.24), (0.52, 0.04, 0.04), bevel=0.01)
    k.box("Chrome", "body", T(0, 0.52, 0.24), (0.5, 0.04, 0.04), bevel=0.01)
    for s in (1, -1):
        k.cyl("Chrome", "body", T(0.21 * s, -0.56, 0.6) @ R(x=90), 0.06, 0.06, 0.08, seg=12)
        k.cyl("Lamp", "body", T(0.21 * s, -0.6, 0.6) @ R(x=90), 0.048, 0.048, 0.006, seg=12)
    # windscreen, seats, folded hood, spare tyre
    k.box("Chrome", "body", T(0, -0.2, 0.7), (0.46, 0.02, 0.22), bevel=0.006)
    k.box("Glass", "body", T(0, -0.2, 0.7), (0.42, 0.024, 0.18))
    k.box("Leather", "body", T(0, 0.2, 0.66), (0.42, 0.08, 0.18), bevel=0.02)
    k.box("Leather", "body", T(0, 0.38, 0.66), (0.42, 0.08, 0.18), bevel=0.02)
    k.box("Hat", "body", T(0, 0.46, 0.63), (0.46, 0.08, 0.1), bevel=0.03)
    k.torus("Dark", "body", T(0, 0.56, 0.42) @ R(x=90), 0.11, 0.035, su=16, sv=6)
    k.cyl("Shirt", "body", T(0, 0.56, 0.42) @ R(x=90), 0.075, 0.075, 0.03, seg=12)
    # steering wheel
    k.torus("Dark", "body", T(0, -0.08, 0.66) @ R(x=-55), 0.065, 0.008, su=12, sv=4)
    # the gangster at the wheel (shared parts, placed by CAR_RIDER)
    k.xf = CAR_RIDER
    mafia_jacket(k, stripes=True)
    mafia_arms(k)
    mafia_face(k)
    fedora(k)
    tommy_gun(k)
    k.xf = Matrix()


# ---------------------------------------------------------------- vehicle clip set (beast rig: legs are wheels, rider is the driver)
WHEELS = ("fl_lo_L", "fl_lo_R", "bl_lo_L", "bl_lo_R")
DRIVE = dict(upperarm_L=(45, 0, 0), forearm_L=(45, 0, 0), upperarm_R=(45, 0, 0), forearm_R=(45, 0, 0))
GUN_UP = dict(upperarm_R=(35, 0, 0), forearm_R=(62, 0, 0), upperarm_L=(42, 0, -28), forearm_L=(72, 0, 0))


def vpose(spin=0.0, **bones):
    """Driver pose plus a wheel angle (degrees; negative rolls forward)."""
    return pose(**{**DRIVE, **{w: (spin, 0, 0) for w in WHEELS}, **bones})


VEHICLE_CLIPS = {
    # engine running: a fine shudder
    "Idle": (60, True, [
        *[(f, vpose(chest=(1.5 * (i % 2), 0, 0)), {"body": (0, 0, 0.005 * (i % 2))}) for i, f in enumerate(range(0, 60, 4))],
        (60, vpose(), {}),
    ]),
    # wheels roll a full turn per loop; the body bounces on its springs
    "Walk": (24, True, [
        (0, vpose(0, body=(1, 0, 0)), {"body": (0, 0, 0.0)}),
        (6, vpose(-90, body=(-1, 0, 1)), {"body": (0, 0, 0.02)}),
        (12, vpose(-180, body=(1, 0, 0)), {"body": (0, 0, 0.0)}),
        (18, vpose(-270, body=(-1, 0, -1)), {"body": (0, 0, 0.02)}),
        (24, vpose(-360, body=(1, 0, 0)), {"body": (0, 0, 0.0)}),
    ], "linear"),
    # wheelie, then slam down and ram forward, gun blazing
    "Attack": (32, False, [
        (0, vpose(0), {}),
        (8, vpose(-270, body=(14, 0, 0), hips=(-8, 0, 0), **GUN_UP), {"body": (0, 0, 0.02)}),
        (14, vpose(-540, body=(-4, 0, 0), spine=(8, 0, 0), **GUN_UP), {"body": (0, 0.22, -0.02)}),
        (20, vpose(-660, body=(-2, 0, 0), **GUN_UP), {"body": (0, 0.2, 0)}),
        (32, vpose(-720), {}),
    ]),
    # drive-by: the gangster rattles off a burst, the car rocks with each kick
    "Shoot": (34, False, [
        (0, vpose(), {}),
        (6, vpose(**GUN_UP, spine=(-4, -12, 0)), {}),
        *[(f, vpose(**{**GUN_UP, "upperarm_R": (35 - 4 * (i % 2), 0, 0)}, spine=(-4 - 3 * (i % 2), -12, 0),
                    body=(0, 1.5 * (i % 2), 0)), {}) for i, f in enumerate(range(8, 22, 2))],
        (24, vpose(**GUN_UP, spine=(-4, -12, 0)), {}),
        (34, vpose(), {}),
    ]),
    # skid, tip and roll onto its side
    "Death": (46, False, [
        (0, vpose(), {}),
        (6, vpose(-60, body=(0, 0, -15), chest=(-15, 0, 0), head=(-20, 0, 0)), {}),
        (18, vpose(-120, body=(5, 45, -20), chest=(10, 0, 10), head=(10, 0, 0)), {"body": (0, 0, 0.12)}),
        (30, vpose(-160, body=(0, 100, -25), chest=(25, 0, 10), head=(30, 0, 0)), {"body": (0, 0, -0.06)}),
        (46, vpose(-170, body=(0, 92, -25), chest=(20, 0, 10), head=(30, 0, 0), upperarm_R=(10, 0, -30)), {"body": (0, 0, -0.08)}),
    ]),
}


# ---------------------------------------------------------------- Behead: a second death for every humanoid
# "_head" = (world offset of the head bone's root, tumble in degrees about that root). make_clips poses
# the head in armature space, so it flies free of the falling body.
KNEEL = dict(thigh_L=(85, 0, 0), shin_L=(-105, 0, 0), thigh_R=(85, 0, 0), shin_R=(-105, 0, 0))
CLIPS["Behead"] = (46, False, [
    (0, pose(), {}),
    (3, pose(spine=(-10, 0, 0), chest=(-6, 0, 0), upperarm_R=(40, 0, -35), upperarm_L=(30, 0, 35)),
     {"hips": (0, 0.02, 0), "_head": ((0.02, 0.06, 0.14), (-25, 10, 0))}),
    (10, pose(thigh_L=(55, 0, 0), shin_L=(-80, 0, 0), thigh_R=(50, 0, 0), shin_R=(-85, 0, 0), spine=(-4, 0, 0),
              upperarm_R=(25, 0, -20), upperarm_L=(20, 0, 20)),
     {"hips": (0, -0.2, 0), "_head": ((0.2, 0.3, 0.42), (-110, 30, 0))}),
    (18, pose(**KNEEL, spine=(8, 0, 0), upperarm_R=(5, 0, -10), upperarm_L=(5, 0, 10)),
     {"hips": (0, -0.36, 0), "_head": ((0.38, 0.5, -0.5), (-200, 60, 0))}),
    (23, pose(**KNEEL, spine=(10, 0, 0)), {"hips": (0, -0.37, 0), "_head": ((0.46, 0.58, -1.5), (-250, 90, 0))}),
    (28, pose(**KNEEL, spine=(22, 0, 0), chest=(6, 0, 0)), {"hips": (0, -0.38, 0), "_head": ((0.52, 0.63, -1.4), (-300, 90, 0))}),
    (36, pose(thigh_L=(80, 0, 0), shin_L=(-100, 0, 0), thigh_R=(70, 0, 0), shin_R=(-110, 0, 0),
              spine=(50, 0, 0), chest=(20, 0, 0), upperarm_R=(-5, 0, -15), upperarm_L=(-5, 0, 15)),
     {"hips": (0, -0.42, 0), "_head": ((0.57, 0.67, -1.5), (-345, 90, 0))}),
    (46, pose(thigh_L=(80, 0, 0), shin_L=(-100, 0, 0), thigh_R=(70, 0, 0), shin_R=(-110, 0, 0),
              spine=(55, 0, 0), chest=(20, 0, 0), upperarm_R=(-5, 0, -15), upperarm_L=(-5, 0, 15),
              forearm_R=(10, 0, 0), forearm_L=(10, 0, 0)),
     {"hips": (0, -0.42, 0), "_head": ((0.59, 0.69, -1.5), (-360, 90, 0))}),
])

# ---------------------------------------------------------------- sniper: humanoid clips with a prone Shoot
PRONE = dict(hips=(88, 0, 0), thigh_L=(-4, 0, 8), thigh_R=(-4, 0, -8), spine=(-6, 0, 0), chest=(-10, 0, 0),
             neck=(-20, 0, 0), head=(-45, 0, 0), upperarm_R=(115, 0, 0), forearm_R=(65, 0, 0),
             upperarm_L=(105, 0, -18), forearm_L=(72, 0, 0))
DOWN = {"hips": (0, -0.82, 0)}
GET_DOWN = pose(thigh_L=(70, 0, 0), shin_L=(-90, 0, 0), thigh_R=(70, 0, 0), shin_R=(-90, 0, 0), spine=(25, 0, 0))
SNIPER_CLIPS = {**CLIPS, "Shoot": (60, False, [
    (0, pose(), {}),
    (8, GET_DOWN, {"hips": (0, -0.35, 0)}),
    (16, pose(**PRONE), DOWN),
    (22, pose(**{**PRONE, "chest": (-11, 0, 0)}), DOWN),
    (25, pose(**{**PRONE, "chest": (-18, 0, 0), "head": (-52, 0, 0), "upperarm_R": (106, 0, 0)}), DOWN),
    (30, pose(**PRONE), DOWN),
    (35, pose(**{**PRONE, "upperarm_L": (92, 0, -10), "forearm_L": (40, 0, 0)}), DOWN),     # work the bolt
    (40, pose(**PRONE), DOWN),
    (48, pose(**PRONE), DOWN),
    (54, GET_DOWN, {"hips": (0, -0.35, 0)}),
    (60, pose(), {}),
])}

# ---------------------------------------------------------------- bunker: tower clips with a recoiling gun instead of a catapult
BUNKER_CLIPS = {**TOWER_CLIPS,
    "Attack": (32, False, [
        (0, {}, {}),
        (6, {"neck": (3, 0, 0)}, {}),
        (8, {"neck": (-12, 0, 0), "chest": (-5, 0, 0), "spine": (-3, 0, 0)}, anchored(lift=-0.025)),
        (14, {"neck": (-5, 0, 0), "chest": (-2, 0, 0)}, {}),
        (32, {}, {}),
    ]),
    "Shoot": (34, False, [
        (0, {}, {}),
        *[(f, {"neck": (-9 if i % 2 == 0 else -2, 0, 0), "chest": (-3 if i % 2 == 0 else 0, 0, 0)},
           anchored(lift=-0.012 if i % 2 == 0 else 0.0)) for i, f in enumerate(range(4, 26, 3))],
        (34, {}, {}),
    ]),
}


# ---------------------------------------------------------------- Marines: shared parts
def long_gun(k, barrel=0.45, scope=False, bipod=False, bayonet=False, mg=False, bulk=1.0):
    """A rifle or machine gun in the right hand, hanging muzzle-down along the arm at rest (like the Tommy gun):
    level when the Shoot clip raises the arm, pointing ahead when the sniper lies prone. Local -Y = muzzle."""
    gx, gy, gz = mid("hand.R")
    G = lambda dx, dy, dz: T(gx, gy, gz) @ R(x=90) @ Matrix.Translation((dx, dy, dz))
    k.box("Metal", "hand.R", G(0, -0.12, 0.045), (0.045 * bulk, 0.28, 0.07 * bulk), bevel=0.008)
    k.box("Wood", "hand.R", G(0, 0.16, 0.01) @ R(x=8), (0.04, 0.3, 0.085), bevel=0.012)
    k.box("Wood", "hand.R", G(0, -0.34, 0.03), (0.042 * bulk, 0.2, 0.05), bevel=0.008)
    k.box("Wood", "hand.R", G(0, 0.0, -0.035) @ R(x=-15), (0.034, 0.045, 0.1), bevel=0.008)
    y0 = -0.26
    k.cyl("Metal", "hand.R", G(0, y0 - barrel / 2, 0.05) @ R(x=90), 0.014 * bulk, 0.014 * bulk, barrel, seg=6)
    if scope:
        k.cyl("Dark", "hand.R", G(0, -0.13, 0.115) @ R(x=90), 0.024, 0.024, 0.26, seg=8)
        for e in (-0.26, 0.0):
            k.cyl("Dark", "hand.R", G(0, e, 0.115) @ R(x=90), 0.032, 0.026, 0.05, seg=8)
        k.cyl("Lens", "hand.R", G(0, -0.287, 0.115) @ R(x=90), 0.029, 0.029, 0.004, seg=8)
    if bipod:
        for s in (1, -1):
            M = G(0.0, y0 - barrel + 0.06, 0.05) @ R(y=25 * s) @ Matrix.Translation((0, 0, -0.08))
            k.cyl("Metal", "hand.R", M, 0.007, 0.007, 0.16, seg=4)
    if bayonet:
        k.box("Steel", "hand.R", G(0, y0 - barrel - 0.08, 0.02), (0.012, 0.2, 0.03))
    if mg:
        k.box("Metal", "hand.R", G(0, -0.13, -0.06), (0.09, 0.12, 0.1), bevel=0.01)     # ammo box
        for i in range(5):
            k.cyl("Brass", "hand.R", G(0.055, -0.1 + 0.025 * i, -0.02) @ R(y=90), 0.009, 0.009, 0.05, seg=5)
        k.cyl("Dark", "hand.R", G(0, y0 - barrel * 0.45, 0.05) @ R(x=90), 0.03, 0.03, barrel * 0.6, seg=8)   # cooling jacket
        k.box("Metal", "hand.R", G(0, -0.06, 0.1), (0.02, 0.1, 0.03))                   # carry handle


def marine_legs(k, slot="Fatigue"):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box(slot, f"thigh.{x}", T(0.12 * s, 0, 0.77), (0.16, 0.17, 0.42), bevel=0.035)
        k.box("FatigueDark", f"thigh.{x}", T(0.2 * s, -0.01, 0.74), (0.04, 0.1, 0.12), bevel=0.01)   # cargo pocket
        M, L = along((0.12 * s, 0.02, 0.2), (0.12 * s, 0.0, 0.57)); k.cyl(slot, f"shin.{x}", M, 0.07, 0.065, L, seg=8)
        k.cyl("Canvas", f"shin.{x}", T(0.12 * s, 0.01, 0.2), 0.068, 0.064, 0.2, seg=8)              # leggings
        k.box("Dark", f"foot.{x}", T(0.12 * s, -0.04, 0.05), (0.12, 0.25, 0.1), bevel=0.03)


def marine_torso(k, slot="Fatigue", webbing=True, pack=True):
    k.cyl(slot, "hips", T(0, 0, 0.93), 0.225, 0.21, 0.2, seg=10)
    k.box(slot, "spine", T(0, 0, 1.13), (0.36, 0.24, 0.24), bevel=0.05)
    k.box(slot, "chest", T(0, 0, 1.36), (0.44, 0.27, 0.3), bevel=0.07)
    for s in (1, -1):
        k.box("FatigueDark", "chest", T(0.1 * s, -0.138, 1.36), (0.1, 0.012, 0.1), bevel=0.01)       # breast pockets
    k.box(slot, "chest", T(0, -0.1, 1.5), (0.22, 0.08, 0.05), bevel=0.02)                        # collar
    if webbing:
        k.cyl("Canvas", "hips", T(0, 0, 1.0), 0.225, 0.225, 0.06, seg=12)
        for a in (-60, -30, 30, 60, 160, 200):
            r = math.radians(a - 90)
            k.box("Canvas", "hips", T(0.23 * math.cos(r), 0.23 * math.sin(r), 0.99) @ R(z=a), (0.07, 0.04, 0.08), bevel=0.01)
        for s in (1, -1):
            k.box("Canvas", "chest", T(0.1 * s, -0.142, 1.28) @ R(y=-8 * s), (0.04, 0.012, 0.42))
            k.box("Canvas", "chest", T(0.1 * s, 0.142, 1.28) @ R(y=8 * s), (0.04, 0.012, 0.42))
    if pack:
        k.box("Canvas", "chest", T(0, 0.2, 1.3), (0.3, 0.14, 0.3), bevel=0.04)
        k.cyl("FatigueDark", "chest", T(0, 0.2, 1.48) @ R(y=90), 0.06, 0.06, 0.32, seg=8)          # bedroll
        k.box("Metal", "chest", T(0.12, 0.25, 1.12) @ R(x=10), (0.05, 0.03, 0.18), bevel=0.008)     # shovel


def marine_arms(k, slot="Fatigue", rolled=False):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.sphere(slot, f"upper_arm.{x}", T(0.21 * s, 0, 1.45), (0.09, 0.1, 0.08), seg=8)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl(slot, f"upper_arm.{x}", M, 0.06, 0.064, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Skin" if rolled else slot, f"forearm.{x}", M, 0.058, 0.05, L - 0.02, seg=8)
        if rolled:
            k.cyl(slot, f"forearm.{x}", T(*B[f"forearm.{x}"][0]) @ Matrix.Translation((0, 0, -0.04)), 0.07, 0.07, 0.06, seg=8)
        k.box("Skin", f"hand.{x}", T(*mid(f"hand.{x}")), (0.075, 0.09, 0.1), bevel=0.02)


def marine_face(k, brow="Hair", paint=None):
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.06, 0.06, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.122, 0.132, 0.14), seg=12)
    k.box("Skin", "head", T(0, -0.035, 1.665), (0.17, 0.16, 0.085), bevel=0.035)
    for s in (1, -1):
        k.sphere("Skin", "head", T(0.122 * s, 0.0, 1.74), (0.025, 0.035, 0.04), seg=6)
        k.box("Dark", "head", T(0.047 * s, -0.124, 1.755), (0.034, 0.02, 0.018))
        k.box(brow, "head", T(0.05 * s, -0.127, 1.785) @ R(y=6 * s), (0.055, 0.02, 0.015))
    k.box("Skin", "head", T(0, -0.14, 1.72), (0.035, 0.03, 0.05), bevel=0.01)
    k.box("Dark", "head", T(0, -0.118, 1.66), (0.055, 0.01, 0.008))
    if paint:
        for z in (1.735, 1.7):
            k.box(paint, "head", T(0, -0.128, z) @ R(y=8), (0.24, 0.012, 0.016))


def helmet(k, slot="Helmet", net=True, strap=True):
    k.sphere(slot, "head", T(0, 0.01, 1.83), (0.17, 0.18, 0.13), seg=14)
    k.cyl(slot, "head", T(0, 0.01, 1.8), 0.19, 0.185, 0.025, seg=16)
    if net:
        for a in range(0, 180, 30):
            k.box("FatigueDark", "head", T(0, 0.01, 1.84) @ R(z=a), (0.36, 0.008, 0.008))
            k.box("FatigueDark", "head", T(0, 0.01, 1.935) @ R(z=a), (0.18, 0.008, 0.008))
    if strap:
        for s in (1, -1):
            k.box("Canvas", "head", T(0.13 * s, -0.02, 1.71), (0.012, 0.025, 0.16))


# ---------------------------------------------------------------- Marines
def marine_pawn(k):
    """Rifleman: netted helmet, webbing and pack, leggings, rifle with a fixed bayonet."""
    plinth(k)
    marine_legs(k)
    marine_torso(k)
    marine_arms(k)
    marine_face(k)
    helmet(k)
    long_gun(k, bayonet=True)


def marine_king(k):
    """Commander: khaki service uniform with ribbons, peaked cap, aviator glasses, binoculars, sidearm."""
    plinth(k)
    marine_legs(k, slot="Khaki")
    marine_torso(k, slot="Khaki", webbing=False, pack=False)
    k.cyl("Dark", "hips", T(0, 0, 1.0), 0.226, 0.226, 0.05, seg=12)
    k.box("Brass", "hips", T(0, -0.226, 1.0), (0.06, 0.02, 0.045), bevel=0.006)
    k.box("Dark", "hips", T(0.21, -0.06, 0.92), (0.06, 0.1, 0.14), bevel=0.015)            # holster
    k.box("Navy", "chest", T(0, -0.142, 1.37), (0.05, 0.014, 0.2))                        # tie
    for i, c in enumerate(("Accent", "Navy", "Brass", "Accent", "Fatigue", "Brass")):
        k.box(c, "chest", T(-0.13 + 0.035 * (i % 3), -0.142, 1.42 - 0.025 * (i // 3)), (0.032, 0.008, 0.02))
    for s in (1, -1):
        k.box("Brass", "chest", T(0.2 * s, 0, 1.5), (0.09, 0.14, 0.015))                 # shoulder boards
    M, L = along((-0.15, -0.12, 1.5), (0.12, -0.14, 1.12)); k.box("Dark", "chest", M, (0.015, 0.01, L))
    for s in (1, -1):
        k.cyl("Dark", "chest", T(0.12 + 0.035 * s, -0.17, 1.12) @ R(x=90), 0.025, 0.025, 0.09, seg=8)   # binoculars
    marine_arms(k, slot="Khaki")
    marine_face(k)
    for s in (1, -1):
        k.box("Lens", "head", T(0.048 * s, -0.132, 1.755), (0.06, 0.012, 0.042), bevel=0.01)
    k.box("Brass", "head", T(0, -0.134, 1.77), (0.04, 0.008, 0.006))
    # peaked cap
    k.cyl("Khaki", "head", T(0, 0.01, 1.86), 0.15, 0.17, 0.1, seg=14, bevel=0.01)
    k.cyl("Dark", "head", T(0, 0.01, 1.81), 0.152, 0.152, 0.03, seg=14)
    k.box("Dark", "head", T(0, -0.15, 1.8) @ R(x=-15), (0.22, 0.1, 0.015), bevel=0.006)
    k.box("Brass", "head", T(0, -0.172, 1.86), (0.05, 0.01, 0.045))
    for s in (1, -1):
        k.box("Brass", "head", T(0.04 * s, -0.19, 1.805) @ R(x=-15), (0.06, 0.02, 0.01))
    # pistol (hangs at rest, levels when the arm comes up)
    gx, gy, gz = mid("hand.R")
    G = lambda dx, dy, dz: T(gx, gy, gz) @ R(x=90) @ Matrix.Translation((dx, dy, dz))
    k.box("Metal", "hand.R", G(0, -0.08, 0.035), (0.032, 0.15, 0.045), bevel=0.006)
    k.box("Dark", "hand.R", G(0, 0.0, -0.025) @ R(x=-15), (0.03, 0.04, 0.08), bevel=0.006)


def marine_queen(k):
    """Heavy Gunner: sleeves rolled, crossed ammo belts, goggles on the helmet, belt-fed machine gun."""
    plinth(k)
    marine_legs(k)
    marine_torso(k, pack=False)
    for s in (1, -1):                                                         # crossed ammo belts
        M, L = along((0.2 * s, -0.12, 1.48), (-0.18 * s, -0.12, 1.02))
        k.box("Canvas", "chest", M @ Matrix.Translation((0, -0.025, 0)), (0.05, 0.015, L))
        for i in range(9):
            t = (i + 0.5) / 9
            p = (0.2 * s - 0.38 * s * t, -0.165, 1.48 - 0.46 * t)
            k.cyl("Brass", "chest", T(*p) @ R(y=-50 * s), 0.008, 0.008, 0.05, seg=5)
    marine_arms(k, rolled=True)
    marine_face(k)
    k.box("Stubble", "head", T(0, -0.06, 1.645), (0.16, 0.13, 0.05), bevel=0.02)
    helmet(k, net=False)
    k.box("Dark", "head", T(0, -0.13, 1.86), (0.26, 0.04, 0.03))                 # goggle strap
    for s in (1, -1):
        k.cyl("Lens", "head", T(0.05 * s, -0.17, 1.865) @ R(x=90), 0.032, 0.032, 0.03, seg=8)
    long_gun(k, barrel=0.5, bipod=True, mg=True, bulk=1.35)


def marine_bishop(k):
    """Sniper: ghillie hood and shoulders, painted face, long scoped rifle on a bipod. Shoots lying prone."""
    plinth(k)
    marine_legs(k)
    marine_torso(k, pack=False)
    marine_arms(k)
    marine_face(k, paint="FatigueDark")
    helmet(k, slot="Ghillie", net=False, strap=False)
    rnd = random.Random(5)
    for i in range(22):                                                       # shaggy strips over hood and shoulders
        a = rnd.uniform(0, 2 * math.pi)
        if abs(a - 1.5 * math.pi) < 0.6:
            continue
        r, z = rnd.uniform(0.15, 0.19), rnd.uniform(1.72, 1.94)
        k.cyl(rnd.choice(("Ghillie", "GhillieDark")), "head", T(r * math.cos(a), r * math.sin(a), z) @ R(x=180 + rnd.uniform(-25, 25), y=rnd.uniform(-25, 25)),
              0.025, 0.0, rnd.uniform(0.1, 0.18), seg=4)
    for i in range(16):
        a = rnd.uniform(0, 2 * math.pi)
        r = rnd.uniform(0.18, 0.24)
        k.cyl(rnd.choice(("Ghillie", "GhillieDark")), "chest", T(r * math.cos(a), r * math.sin(a) * 0.7 + 0.02, 1.5) @ R(x=180 + rnd.uniform(-30, 30), y=rnd.uniform(-30, 30)),
              0.03, 0.0, rnd.uniform(0.14, 0.24), seg=4)
    long_gun(k, barrel=0.65, scope=True, bipod=True)


def marine_rook(k):
    """Bunker: round concrete tower ringed with sandbags, firing slits, searchlight, radio mast, roof gun."""
    plinth(k)
    wall = lambda z: 0.34 - 0.03 * z / 1.28
    k.cyl("ConcreteDark", "hips", T(0, 0, 0.04), 0.39, 0.38, 0.08, seg=18)
    k.cyl("Concrete", "hips", T(0, 0, 0.55), wall(0), wall(1.1), 1.1, seg=18)
    k.cyl("Concrete", "spine", T(0, 0, 1.19), wall(1.1), wall(1.28), 0.18, seg=18)
    for z in (0.5, 1.1):
        k.cyl("ConcreteDark", "hips", T(0, 0, z), wall(z) + 0.012, wall(z) + 0.012, 0.03, seg=18)
    # sandbag ring at the foot (front left open for the door)
    for row, z in enumerate((0.06, 0.15)):
        for i in range(14):
            a = 2 * math.pi * (i + 0.5 * row) / 14
            if abs(a - 1.5 * math.pi) < 0.35:
                continue
            k.box("Sand", "hips", tangent(wall(z) + 0.05, a, z), (0.15, 0.1, 0.085), bevel=0.03)
    k.box("Metal", "hips", T(0, -wall(0.2) - 0.005, 0.2), (0.16, 0.02, 0.34), bevel=0.01)   # steel door
    for z in (0.12, 0.28):
        k.box("Dark", "hips", T(0, -wall(0.2) - 0.016, z), (0.17, 0.008, 0.02))
    # firing slits with a glow of gunfire inside
    for a, z, bone in ((1.5 * math.pi + 0.7, 0.72, "hips"), (1.5 * math.pi - 0.7, 0.72, "hips"),
                       (0.0, 0.72, "hips"), (math.pi, 0.72, "hips"), (1.5 * math.pi, 1.2, "spine")):
        k.box("Dark", bone, tangent(wall(z) - 0.005, a, z), (0.16, 0.04, 0.04))
        k.box("Lamp", bone, tangent(wall(z) - 0.022, a, z), (0.1, 0.01, 0.02))
    # marine pennant down the front: navy with a gold anchor
    Mb = T(0, -wall(1.25) - 0.012, 1.25) @ R(x=-3)
    k.prism("Navy", "spine", Mb, [(-0.11, 0), (0.11, 0), (0.11, -0.4), (0, -0.48), (-0.11, -0.4)], 0.012)
    k.box("Brass", "spine", Mb @ Matrix.Translation((0, -0.008, -0.2)), (0.02, 0.006, 0.16))
    k.box("Brass", "spine", Mb @ Matrix.Translation((0, -0.008, -0.14)), (0.08, 0.006, 0.018))
    k.torus("Brass", "spine", Mb @ Matrix.Translation((0, -0.008, -0.27)) @ R(x=90), 0.045, 0.008, su=12, sv=4)
    k.box("Brass", "spine", Mb @ Matrix.Translation((0, -0.004, -0.01)), (0.24, 0.018, 0.025))
    # roof: overhang, sandbag parapet, searchlight, radio mast
    k.cyl("ConcreteDark", "chest", T(0, 0, 1.33), wall(1.28), 0.4, 0.1, seg=18)
    k.cyl("Concrete", "chest", T(0, 0, 1.42), 0.4, 0.4, 0.08, seg=18)
    for i in range(12):
        a = 2 * math.pi * (i + 0.5) / 12
        if abs(a - 1.5 * math.pi) < 0.3:
            continue
        k.box("Sand", "chest", tangent(0.35, a, 1.5), (0.17, 0.1, 0.085), bevel=0.03)
    a = 2 * math.pi * 0.62
    k.cyl("Metal", "chest", T(0.3 * math.cos(a), 0.3 * math.sin(a), 1.6), 0.06, 0.06, 0.1, seg=10)
    k.cyl("Lamp", "chest", T(0.3 * math.cos(a), 0.3 * math.sin(a) - 0.05, 1.6) @ R(x=90), 0.05, 0.05, 0.01, seg=10)
    M, L = along((0.25, 0.22, 1.46), (0.25, 0.22, 2.2)); k.cyl("Metal", "chest", M, 0.008, 0.004, L, seg=4)
    k.sphere("Accent", "chest", T(0.25, 0.22, 2.2), (0.015, 0.015, 0.015), seg=6)
    # roof gun on the neck bone: shield, breech and barrel pointing forward; it recoils in the bunker clips
    k.cyl("Metal", "chest", T(0, 0, 1.485), 0.12, 0.12, 0.03, seg=12)
    k.box("Helmet", "neck", T(0, -0.1, 1.56), (0.24, 0.025, 0.14), bevel=0.01)
    k.box("Metal", "neck", T(0, 0.02, 1.53), (0.1, 0.18, 0.08), bevel=0.012)
    M, L = along((0, -0.08, 1.54), (0, -0.62, 1.54)); k.cyl("Metal", "neck", M, 0.022, 0.02, L, seg=8)
    k.cyl("Dark", "neck", T(0, -0.62, 1.54) @ R(x=90), 0.032, 0.032, 0.05, seg=8)


JEEP_RIDER = Matrix.Translation((0, 0.12, -0.17)) @ S(0.72, 0.72, 0.72)
JEEP = beast_rig(body=((0, 0.46, 0.36), (0, -0.46, 0.36)), neck=((0, -0.5, 0.5), (0, -0.55, 0.55)),
                 head=((0, -0.55, 0.55), (0, -0.62, 0.55)), jaw=((0, -0.56, 0.5), (0, -0.62, 0.48)),
                 tail=((0, 0.5, 0.4), (0, 0.55, 0.3)),
                 fl=((0.27, -0.34, 0.36), (0.27, -0.34, 0.15), (0.27, -0.34, 0.0)),
                 bl=((0.27, 0.3, 0.36), (0.27, 0.3, 0.15), (0.27, 0.3, 0.0)), rider=JEEP_RIDER)


def marine_knight(k):
    """Jeep: open olive four-by-four, flat hood with a white star, mounted machine gun, jerrycans, helmeted driver."""
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        for pre, y in (("fl", -0.34), ("bl", 0.3)):
            bone = f"{pre}_lo.{x}"
            W = T(0.27 * s, y, 0.15) @ R(y=90)
            k.cyl("Dark", bone, W, 0.155, 0.155, 0.1, seg=14)
            for a in range(0, 180, 30):                                    # tread blocks so the spin reads
                k.box("Dark", bone, W @ R(z=a), (0.33, 0.03, 0.105))
            k.cyl("Fatigue", bone, W, 0.08, 0.08, 0.104, seg=10)
            k.cyl("Metal", bone, W, 0.03, 0.03, 0.11, seg=6)
            k.box("FatigueDark", bone, W @ R(z=45), (0.12, 0.02, 0.108))
    k.box("Dark", "body", T(0, -0.02, 0.24), (0.42, 1.0, 0.06))
    k.box("Fatigue", "body", T(0, 0.08, 0.41), (0.5, 0.74, 0.26), bevel=0.02)
    k.box("Fatigue", "body", T(0, -0.42, 0.45), (0.44, 0.34, 0.2), bevel=0.02)          # flat hood
    k.prism("Ermine", "body", T(0, -0.42, 0.553) @ R(x=90), [(0, 0.07), (0.02, 0.022), (0.067, 0.022), (0.03, -0.008), (0.042, -0.056), (0, -0.026), (-0.042, -0.056), (-0.03, -0.008), (-0.067, 0.022), (-0.02, 0.022)], 0.004)
    for s in (1, -1):
        k.box("FatigueDark", "body", T(0.28 * s, -0.34, 0.33), (0.1, 0.36, 0.025), bevel=0.006)   # fenders
        k.box("FatigueDark", "body", T(0.28 * s, 0.3, 0.33), (0.1, 0.36, 0.025), bevel=0.006)
        k.cyl("Lamp", "body", T(0.14 * s, -0.6, 0.48) @ R(x=90), 0.035, 0.035, 0.02, seg=10)
    k.box("FatigueDark", "body", T(0, -0.6, 0.42), (0.36, 0.03, 0.18))                       # grille
    for x in (-0.12, -0.06, 0.0, 0.06, 0.12):
        k.box("Dark", "body", T(x, -0.617, 0.42), (0.018, 0.01, 0.14))
    k.box("Dark", "body", T(0, -0.64, 0.25), (0.5, 0.04, 0.05))
    # folded windscreen, seats, jerrycans, spare
    k.box("FatigueDark", "body", T(0, -0.22, 0.6), (0.48, 0.03, 0.14), bevel=0.006)
    k.box("Glass", "body", T(0, -0.225, 0.6), (0.42, 0.034, 0.1))
    k.box("Canvas", "body", T(0, 0.18, 0.6), (0.42, 0.08, 0.16), bevel=0.02)
    for s in (1, -1):
        k.box("Fatigue", "body", T(0.12 * s, 0.4, 0.62), (0.12, 0.08, 0.18), bevel=0.012)
    k.cyl("Dark", "body", T(0, 0.5, 0.42) @ R(x=90), 0.14, 0.14, 0.08, seg=14)
    # pintle-mounted machine gun on a post behind the seats
    M, L = along((0.0, 0.3, 0.5), (0.0, 0.3, 0.86)); k.cyl("Metal", "body", M, 0.02, 0.02, L, seg=6)
    k.box("Metal", "body", T(0, 0.22, 0.9), (0.06, 0.24, 0.07), bevel=0.008)
    M, L = along((0, 0.1, 0.91), (0, -0.28, 0.93)); k.cyl("Dark", "body", M, 0.025, 0.02, L, seg=8)
    k.box("Metal", "body", T(0.06, 0.22, 0.86), (0.07, 0.1, 0.08), bevel=0.008)
    k.torus("Dark", "body", T(0, -0.08, 0.66) @ R(x=-55), 0.065, 0.008, su=12, sv=4)
    # helmeted driver with a carbine
    k.xf = JEEP_RIDER
    marine_torso(k, pack=False)
    marine_arms(k)
    marine_face(k)
    helmet(k)
    long_gun(k, barrel=0.3)
    k.xf = Matrix()



# ---------------------------------------------------------------- Vikings: shared parts
def axe(k, length=0.72, double=False):
    """An axe in the right hand on the sword line (forward and up); bearded blade facing forward-down."""
    h = Vector(mid("hand.R", dy=-0.02))
    d = Vector((0, -1, 0.55)).normalized()
    n = Vector((0, -0.55, -1)).normalized()
    P = lambda t: tuple(h + d * t)
    M, L = along(P(-0.14), P(length)); k.cyl("Wood", "hand.R", M, 0.019, 0.019, L, seg=6)
    k.cyl("Iron", "hand.R", T(*P(length)) @ d.to_track_quat("Z", "Y").to_matrix().to_4x4(), 0.024, 0.024, 0.06, seg=6)
    p = V(*P(length - 0.07))
    for side in ((1, -1) if double else (1,)):
        u = n * side
        Mb = Matrix(((u.x, 1, d.x, p.x), (u.y, 0, d.y, p.y), (u.z, 0, d.z, p.z), (0, 0, 0, 1)))
        blade = [(0.0, -0.03), (0.1, -0.1), (0.17, -0.11), (0.19, 0.0), (0.16, 0.11), (0.08, 0.08), (0.0, 0.04)]
        if double:
            blade = [(a * 1.15, b * 1.2) for a, b in blade]
        k.prism("Iron", "hand.R", Mb, blade, 0.014)
        k.prism("Steel", "hand.R", Mb @ Matrix.Translation((0.012, 0, 0)), [(0.155, -0.1), (0.19 * (1.15 if double else 1), -0.01), (0.16, 0.1), (0.175, 0.0)], 0.018)


def round_shield(k, face="TunicRed", ring="Sail"):
    SM = T(0.44, -0.07, 1.02) @ R(z=37) @ R(x=-8)
    k.cyl("Wood", "forearm.L", SM @ R(x=90), 0.24, 0.24, 0.03, seg=18)
    k.cyl(face, "forearm.L", SM @ Matrix.Translation((0, -0.016, 0)) @ R(x=90), 0.225, 0.225, 0.004, seg=18)
    k.cyl(ring, "forearm.L", SM @ Matrix.Translation((0, -0.019, 0)) @ R(x=90), 0.14, 0.14, 0.004, seg=18)
    k.cyl(face, "forearm.L", SM @ Matrix.Translation((0, -0.022, 0)) @ R(x=90), 0.09, 0.09, 0.004, seg=14)
    k.torus("Iron", "forearm.L", SM @ R(x=90), 0.24, 0.012, su=20, sv=4)
    k.sphere("Iron", "forearm.L", SM @ Matrix.Translation((0, -0.03, 0)), (0.05, 0.035, 0.05), seg=10)


def viking_legs(k):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box("Trousers", f"thigh.{x}", T(0.12 * s, 0, 0.77), (0.17, 0.18, 0.42), bevel=0.04)
        M, L = along((0.12 * s, 0.02, 0.14), (0.12 * s, 0.0, 0.57)); k.cyl("Trousers", f"shin.{x}", M, 0.072, 0.066, L, seg=8)
        for z in (0.22, 0.3, 0.38, 0.46):
            k.cyl("Leather", f"shin.{x}", T(0.12 * s, 0.01, z) @ R(x=8 if z % 0.16 else -8), 0.074, 0.074, 0.022, seg=8)
        k.box("Leather", f"foot.{x}", T(0.12 * s, -0.04, 0.06), (0.12, 0.24, 0.12), bevel=0.035)
        k.cyl("Fur", f"shin.{x}", T(0.12 * s, 0.01, 0.15), 0.082, 0.078, 0.06, seg=8)


def viking_torso(k, tunic="Tunic", mail=False, cloak=True):
    k.cyl(tunic, "hips", T(0, 0, 0.83), 0.27, 0.215, 0.34, seg=10)
    k.box("Iron" if mail else tunic, "spine", T(0, 0, 1.13), (0.36, 0.25, 0.25), bevel=0.05)
    k.box("Iron" if mail else tunic, "chest", T(0, 0, 1.36), (0.46, 0.28, 0.31), bevel=0.07)
    k.cyl("Leather", "hips", T(0, 0, 0.99), 0.225, 0.225, 0.07, seg=12)
    k.box("Gold", "hips", T(0, -0.227, 0.99), (0.07, 0.02, 0.06), bevel=0.01)
    k.box("Leather", "hips", T(0.17, -0.12, 0.88), (0.08, 0.04, 0.12), bevel=0.015)         # pouch
    if cloak:
        k.torus("Fur", "chest", T(0, 0.02, 1.5) @ S(1, 0.82, 1), 0.2, 0.065, su=14, sv=6)
        cape(k, "FurDark", 0.5, 0.7, 1.5, 0.45, tilt=8)


def viking_arms(k, sleeve="Tunic"):
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.sphere(sleeve, f"upper_arm.{x}", T(0.22 * s, 0, 1.45), (0.1, 0.11, 0.09), seg=8)
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl(sleeve, f"upper_arm.{x}", M, 0.065, 0.068, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Skin", f"forearm.{x}", M, 0.06, 0.052, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Leather", f"forearm.{x}", M @ Matrix.Translation((0, 0, 0.04)), 0.066, 0.06, L * 0.5, seg=8)
        k.torus("Gold", f"upper_arm.{x}", T(*mid(f"upper_arm.{x}")) @ R(y=17 * s), 0.07, 0.012, su=10, sv=4)
        k.box("Skin", f"hand.{x}", T(*mid(f"hand.{x}")), (0.08, 0.095, 0.105), bevel=0.02)


def viking_face(k, beard="Beard", hair="Beard", long_beard=False):
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.065, 0.065, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.125, 0.135, 0.14), seg=12)
    k.sphere(hair, "head", T(0, 0.04, 1.77), (0.13, 0.13, 0.14), seg=12)
    for s in (1, -1):
        k.sphere("Skin", "head", T(0.124 * s, 0.0, 1.74), (0.025, 0.035, 0.04), seg=6)
        k.box("Dark", "head", T(0.047 * s, -0.125, 1.755), (0.034, 0.02, 0.018))
        k.box(beard, "head", T(0.05 * s, -0.128, 1.787) @ R(y=-10 * s), (0.056, 0.022, 0.018))
        k.box(hair, "head", T(0.13 * s, 0.05, 1.62), (0.04, 0.09, 0.28), bevel=0.015)              # long hair at the sides
    k.box("Skin", "head", T(0, -0.14, 1.72), (0.036, 0.032, 0.05), bevel=0.01)
    k.box(beard, "head", T(0, -0.085, 1.645), (0.21, 0.11, 0.13), bevel=0.04)
    k.box(beard, "head", T(0, -0.135, 1.69), (0.13, 0.026, 0.026), bevel=0.008)                   # moustache
    drop = 0.22 if long_beard else 0.12
    for s in (1, -1):                                                                              # braided beard tails
        for i in range(3):
            k.sphere(beard, "head", T(0.04 * s, -0.11, 1.585 - i * drop / 3), (0.026, 0.024, 0.032), seg=6)
        k.cyl("Gold", "head", T(0.04 * s, -0.11, 1.585 - drop + 0.03), 0.022, 0.022, 0.02, seg=6)


def viking_helm(k, horns=False, nose=True):
    k.sphere("Iron", "head", T(0, 0.01, 1.82), (0.15, 0.16, 0.13), seg=12, smooth=False)
    k.cyl("Iron", "head", T(0, 0.01, 1.92), 0.1, 0.02, 0.1, seg=8)
    k.cyl("Leather", "head", T(0, 0.01, 1.79), 0.155, 0.155, 0.035, seg=14)
    for a in (0, 90):
        k.box("Gold" if horns else "Iron", "head", T(0, 0.01, 1.87) @ R(z=a), (0.3, 0.02, 0.012))
    if nose:
        k.box("Iron", "head", T(0, -0.155, 1.75), (0.03, 0.02, 0.12), bevel=0.006)
    if horns:
        for s in (1, -1):
            prev = (0.14 * s, 0.0, 1.84)
            for i in range(4):
                t = (i + 1) / 4
                nxt = (s * (0.14 + 0.13 * t), -0.04 * t, 1.84 + 0.2 * t * t + 0.04 * t)
                M, L = along(prev, nxt)
                k.cyl("Bone", "head", M, 0.04 * (1 - t) + 0.012, 0.04 * (1 - t) + 0.004, L + 0.01, seg=6)
                prev = nxt


# ---------------------------------------------------------------- Vikings
def viking_pawn(k):
    """Raider: nose-guard helm, fur collar, leg wraps, axe and painted round shield."""
    plinth(k)
    viking_legs(k)
    viking_torso(k, cloak=False)
    k.torus("Fur", "chest", T(0, 0.02, 1.5) @ S(1, 0.82, 1), 0.2, 0.06, su=14, sv=6)
    viking_arms(k)
    viking_face(k)
    viking_helm(k)
    axe(k)
    round_shield(k)


def viking_king(k):
    """Jarl: horned helm, long braided beard, fur mantle and cloak, gold arm rings, double-bitted war axe."""
    plinth(k)
    viking_legs(k)
    viking_torso(k, tunic="TunicRed")
    k.box("Gold", "chest", T(0, -0.145, 1.36), (0.08, 0.012, 0.08), bevel=0.01)            # brooch
    for s in (1, -1):
        k.sphere("Fur", "chest", T(0.22 * s, 0.01, 1.5), (0.13, 0.15, 0.08), seg=10)       # fur mantle on the shoulders
    viking_arms(k, sleeve="TunicRed")
    viking_face(k, long_beard=True)
    viking_helm(k, horns=True, nose=False)
    axe(k, length=0.9, double=True)


def viking_queen(k):
    """Shieldmaiden: mail shirt, long blond braids under a light helm, painted shield, spear."""
    plinth(k)
    viking_legs(k)
    viking_torso(k, tunic="Tunic", mail=True, cloak=False)
    cape(k, "TunicRed", 0.42, 0.58, 1.5, 0.6, tilt=8)
    viking_arms(k, sleeve="Iron")
    # face: finer features, lips, braids (no beard)
    M, L = along((0, 0, 1.5), (0, 0, 1.62)); k.cyl("Skin", "neck", M, 0.05, 0.05, L, seg=8)
    k.sphere("Skin", "head", T(0, 0.0, 1.75), (0.11, 0.12, 0.13), seg=12)
    k.sphere("Blond", "head", T(0, 0.025, 1.79), (0.125, 0.13, 0.125), seg=12)
    for s in (1, -1):
        k.box("Dark", "head", T(0.042 * s, -0.112, 1.752), (0.03, 0.02, 0.026), bevel=0.006)
        k.box("Dark", "head", T(0.05 * s, -0.116, 1.768), (0.034, 0.018, 0.006))
        k.box("Blond", "head", T(0.042 * s, -0.114, 1.786), (0.04, 0.014, 0.007))
        for i in range(6):                                                                    # braids over the shoulders
            k.sphere("Blond", "chest", T(0.105 * s, -0.07 - 0.004 * i, 1.6 - i * 0.07), (0.035 - 0.002 * i,) * 3, seg=8)
        k.cyl("Leather", "chest", T(0.105 * s, -0.095, 1.16), 0.022, 0.022, 0.025, seg=6)
    k.box("Skin", "head", T(0, -0.124, 1.72), (0.02, 0.02, 0.03), bevel=0.006)
    k.box("Lips", "head", T(0, -0.114, 1.68), (0.036, 0.012, 0.012), bevel=0.004)
    k.box("Blond", "head", T(0, 0.085, 1.66) @ R(x=-6), (0.2, 0.07, 0.24), bevel=0.04)
    k.sphere("Iron", "head", T(0, 0.015, 1.83), (0.13, 0.14, 0.1), seg=12)
    k.cyl("Leather", "head", T(0, 0.015, 1.81), 0.135, 0.135, 0.03, seg=14)
    # spear held upright, leaf blade
    hx, hy, hz = mid("hand.R")
    M, L = along((hx, hy - 0.01, 0.25), (hx, hy - 0.06, 2.0)); k.cyl("Wood", "hand.R", M, 0.018, 0.018, L, seg=6)
    k.cyl("Iron", "hand.R", T(hx, hy - 0.06, 2.0), 0.022, 0.022, 0.05, seg=6)
    k.prism("Steel", "hand.R", T(hx, hy - 0.06, 2.02), [(0, 0), (0.04, 0.07), (0, 0.22), (-0.04, 0.07)], 0.012)
    round_shield(k, face="Tunic", ring="Gold")


def viking_bishop(k):
    """Seer: hooded old rune-caster, long white beard, rune stones on the belt, glowing rune staff, raven on the shoulder."""
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        k.box("Leather", f"foot.{x}", T(0.11 * s, -0.05, 0.05), (0.1, 0.22, 0.09), bevel=0.03)
    k.cyl("Robe", "hips", T(0, 0, 0.58), 0.3, 0.2, 0.84, seg=12)
    for i in range(10):                                                     # ragged hem
        a = 2 * math.pi * (i + 0.5) / 10
        k.cyl("Robe", "hips", T(0.275 * math.cos(a), 0.275 * math.sin(a), 0.15) @ R(x=180), 0.05, 0.0, 0.06 + 0.04 * (i % 2), seg=4)
    k.box("Robe", "spine", T(0, 0, 1.13), (0.34, 0.24, 0.22), bevel=0.05)
    k.box("Robe", "chest", T(0, 0, 1.35), (0.42, 0.26, 0.28), bevel=0.07)
    k.cyl("Leather", "hips", T(0, 0, 1.0), 0.212, 0.212, 0.05, seg=12)
    for i, x in enumerate((-0.12, -0.04, 0.05, 0.13)):                     # rune stones
        k.box("Stone", "hips", T(x, -0.215, 0.93), (0.045, 0.02, 0.06), bevel=0.01)
        k.box("Rune", "hips", T(x, -0.226, 0.93), (0.012, 0.004, 0.035))
    k.torus("Fur", "chest", T(0, 0.02, 1.5) @ S(1, 0.82, 1), 0.2, 0.06, su=14, sv=6)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        M, L = along(*B[f"upper_arm.{x}"]); k.cyl("Robe", f"upper_arm.{x}", M, 0.065, 0.07, L, seg=8)
        M, L = along(*B[f"forearm.{x}"]); k.cyl("Robe", f"forearm.{x}", M, 0.075, 0.105, L - 0.02, seg=8)
        k.box("Skin", f"hand.{x}", T(*mid(f"hand.{x}")), (0.075, 0.09, 0.1), bevel=0.02)
        k.sphere("Robe", f"upper_arm.{x}", T(0.22 * s, 0, 1.45), (0.1, 0.11, 0.09), seg=8)
    # old face in a deep hood, long white beard
    viking_face(k, beard="GreyBeard", hair="GreyBeard", long_beard=True)
    k.box("GreyBeard", "head", T(0, -0.1, 1.5), (0.16, 0.08, 0.2), bevel=0.04)
    k.sphere("Robe", "head", T(0, 0.04, 1.8), (0.175, 0.17, 0.19), seg=12)
    k.cyl("Robe", "head", T(0, 0.1, 1.97) @ R(x=-30), 0.07, 0.0, 0.14, seg=6)
    for s in (1, -1):
        k.box("Robe", "head", T(0.145 * s, -0.02, 1.68), (0.05, 0.2, 0.26), bevel=0.02)
    # rune staff with a glowing stone held in roots
    hx, hy, hz = mid("hand.R")
    sy = hy - 0.06
    M, L = along((hx, hy - 0.01, 0.3), (hx, sy, 1.95)); k.cyl("Wood", "hand.R", M, 0.024, 0.02, L, seg=6)
    for a in range(0, 360, 72):
        r = math.radians(a)
        M, L = along((hx, sy, 1.93), (hx + 0.07 * math.cos(r), sy + 0.07 * math.sin(r), 2.12)); k.cyl("Wood", "hand.R", M, 0.012, 0.006, L, seg=4)
    k.sphere("Rune", "hand.R", T(hx, sy, 2.06), (0.055, 0.055, 0.065), seg=8, smooth=False)
    for z in (1.4, 1.55, 1.7):
        k.box("Rune", "hand.R", T(hx, sy - 0.022, z), (0.012, 0.006, 0.05))
    # raven on the left shoulder
    rx, ry, rz = 0.2, 0.02, 1.56
    k.sphere("Raven", "chest", T(rx, ry, rz + 0.05), (0.05, 0.08, 0.05), seg=8)
    k.sphere("Raven", "chest", T(rx, ry - 0.07, rz + 0.11), (0.035, 0.035, 0.035), seg=8)
    k.cyl("Gold", "chest", T(rx, ry - 0.12, rz + 0.105) @ R(x=-95), 0.012, 0.0, 0.05, seg=4)
    k.sphere("Rune", "chest", T(rx + 0.02, ry - 0.09, rz + 0.125), (0.006, 0.006, 0.006), seg=4)
    k.prism("Raven", "chest", T(rx, ry + 0.09, rz + 0.04) @ R(z=90) @ R(y=10), [(0, 0.02), (0.1, 0.0), (0, -0.02)], 0.06)


# ---------------------------------------------------------------- Vikings: Longship (rook) on the tower rig
# hips = hull, spine/chest = mast and sail (the sail billows on the chest bone), neck = masthead vane.
def viking_rook(k):
    """Longship: clinker hull with a row of painted shields, dragon prow, curled stern, striped square sail."""
    plinth(k)
    for (x, y, sx, sy) in ((0.3, -0.25, 0.12, 0.08), (-0.32, 0.18, 0.13, 0.09), (0.28, 0.3, 0.1, 0.07), (-0.25, -0.33, 0.1, 0.07)):
        k.sphere("Water", "root", T(x, y, 0.0), (sx, sy, 0.035), seg=10)
        k.sphere("Foam", "root", T(x, y, 0.02), (sx * 0.5, sy * 0.5, 0.02), seg=8)
    # side profile with the sheer sweeping up at bow and stern
    prof = [(-0.52, 0.52), (-0.36, 0.43), (-0.15, 0.4), (0.15, 0.4), (0.36, 0.43), (0.52, 0.52),
            (0.46, 0.3), (0.32, 0.14), (-0.32, 0.14), (-0.46, 0.3)]
    k.prism("Wood", "hips", T(0, 0, 0) @ R(z=90), [(u * 0.92, v - 0.03) for u, v in prof], 0.26)   # solid hull
    for s in (1, -1):                                                                              # flared side planks
        for j, dz in enumerate((0.0, -0.08, -0.16)):
            pts = [(u * (1 - 0.06 * j), v + dz * 0.3) for u, v in prof]
            k.prism("WoodDark" if j % 2 else "Wood", "hips", T(0.17 * s - 0.01 * j * s, 0, dz * 0.2) @ R(z=90), pts, 0.025)
        for i in range(7):                                                                         # shields along the rail
            y = -0.33 + i * 0.11
            k.cyl(("TunicRed", "Gold", "Sail")[i % 3], "hips", T(0.19 * s, y, 0.38) @ R(y=90), 0.055, 0.055, 0.015, seg=12)
            k.sphere("Iron", "hips", T(0.2 * s, y, 0.38), (0.012, 0.018, 0.018), seg=6)
        for i in range(4):                                                                         # oars
            M, L = along((0.16 * s, -0.25 + i * 0.16, 0.33), (0.42 * s, -0.25 + i * 0.16 - 0.04, 0.08)); k.cyl("Wood", "hips", M, 0.01, 0.01, L, seg=4)
    k.box("WoodDark", "hips", T(0, 0, 0.405), (0.3, 0.9, 0.02))                                    # deck
    k.box("WoodDark", "hips", T(0, 0, 0.1), (0.06, 0.66, 0.06))                                    # keel
    # dragon prow and curled stern
    prev = (0, -0.48, 0.4)
    for i in range(5):
        t = (i + 1) / 5
        nxt = (0, -0.48 - 0.16 * math.sin(t * 1.4), 0.4 + 0.42 * t)
        M, L = along(prev, nxt); k.cyl("Wood", "hips", M, 0.04 - 0.004 * i, 0.036 - 0.004 * i, L + 0.01, seg=6)
        prev = nxt
    hx, hy, hz = prev
    k.box("Wood", "hips", T(hx, hy - 0.04, hz + 0.02) @ R(x=15), (0.07, 0.14, 0.07), bevel=0.015)
    k.box("WoodDark", "hips", T(hx, hy - 0.08, hz - 0.03) @ R(x=-10), (0.05, 0.1, 0.03), bevel=0.008)
    for s in (1, -1):
        k.sphere("Rune", "hips", T(hx + 0.034 * s, hy - 0.05, hz + 0.04), (0.01, 0.012, 0.01), seg=6)
        k.cyl("Wood", "hips", T(hx + 0.02 * s, hy + 0.02, hz + 0.09) @ R(x=-30), 0.012, 0.0, 0.08, seg=4)
    for i in range(3):
        k.cyl("TunicRed", "hips", T(0, hy + 0.04 + 0.03 * i, hz - 0.04 - 0.07 * i) @ R(x=60), 0.012, 0.0, 0.06, seg=4)
    prev = (0, 0.48, 0.4)
    for i in range(6):
        t = (i + 1) / 6
        a = t * 3.6
        nxt = (0, 0.48 + 0.12 * math.sin(a) + 0.06 * t, 0.4 + 0.32 * t - 0.05 * math.sin(a * 0.8) * t)
        M, L = along(prev, nxt); k.cyl("Wood", "hips", M, 0.035 - 0.004 * i, 0.032 - 0.004 * i, L + 0.01, seg=6)
        prev = nxt
    # mast, yard and striped sail
    M, L = along((0, 0.02, 0.4), (0, 0.02, 1.1)); k.cyl("Wood", "hips", M, 0.025, 0.024, L, seg=6)
    M, L = along((0, 0.02, 1.1), (0, 0.02, 1.28)); k.cyl("Wood", "spine", M, 0.024, 0.023, L, seg=6)
    M, L = along((0, 0.02, 1.28), (0, 0.02, 1.62)); k.cyl("Wood", "chest", M, 0.023, 0.02, L, seg=6)
    M, L = along((-0.42, 0.0, 1.5), (0.42, 0.0, 1.5)); k.cyl("Wood", "chest", M, 0.017, 0.017, L, seg=6)
    for i in range(7):
        x = -0.36 + i * 0.12
        k.box("SailRed" if i % 2 else "Sail", "chest", T(x, -0.02, 1.08) @ R(x=-4), (0.12, 0.012, 0.82))
    k.prism("Raven", "chest", T(0, -0.03, 1.12), [(0, 0.12), (0.12, 0.0), (0, -0.12), (-0.12, 0.0)], 0.006)
    M, L = along((0, 0.02, 1.62), (0, 0.02, 1.9)); k.cyl("Wood", "neck", M, 0.018, 0.012, L, seg=6)
    k.prism("Gold", "neck", T(0, 0.02, 1.84) @ R(z=90), [(0, 0.06), (0.16, 0.02), (0.12, -0.03), (0, -0.04)], 0.01)
    k.sphere("Gold", "neck", T(0, 0.02, 1.91), (0.018, 0.018, 0.018), seg=6)


def ram(rx, lift, ahead):
    """Like anchored(), plus a surge forward (+) or back (-) along the keel."""
    loc = anchored(rx=rx, lift=lift)["hips"]
    return {"hips": (loc[0], loc[1], loc[2] + ahead)}


SHIP_CLIPS = {
    "Idle": (60, True, [
        (0, {"hips": (0, 0, 2)}, anchored(rz=2)),
        (30, {"hips": (1.5, 0, -2), "chest": (2, 0, 0), "neck": (0, 0, 5)}, anchored(1.5, -2, 0.01)),
        (60, {"hips": (0, 0, 2)}, anchored(rz=2)),
    ]),
    # riding the swell
    "Walk": (24, True, [
        (0, {"hips": (-4, 0, 5), "chest": (4, 0, 0)}, anchored(-4, 5, 0.0)),
        (6, {"hips": (3, 0, 0), "chest": (8, 0, 0)}, anchored(3, 0, 0.05)),
        (12, {"hips": (-4, 0, -5), "chest": (4, 0, 0)}, anchored(-4, -5, 0.0)),
        (18, {"hips": (3, 0, 0), "chest": (8, 0, 0)}, anchored(3, 0, 0.05)),
        (24, {"hips": (-4, 0, 5), "chest": (4, 0, 0)}, anchored(-4, 5, 0.0)),
    ]),
    # rear on a wave, then ram bow-first
    "Attack": (32, False, [
        (0, {}, {}),
        (8, {"hips": (-10, 0, 0), "chest": (-4, 0, 0), "neck": (-10, 0, 0)}, ram(-10, 0.06, -0.1)),
        (13, {"hips": (9, 0, 0), "chest": (14, 0, 0), "neck": (15, 0, 0)}, ram(9, -0.02, 0.28)),
        (18, {"hips": (4, 0, 0), "chest": (6, 0, 0)}, ram(4, 0.0, 0.22)),
        (32, {}, {}),
    ]),
    # broadside: heel over as the crew lets fly
    "Shoot": (34, False, [
        (0, {}, {}),
        *[(f, {"hips": (0, 0, 7 if i % 2 == 0 else 4), "chest": (3 if i % 2 == 0 else 0, 0, 0)},
           anchored(rz=7 if i % 2 == 0 else 4)) for i, f in enumerate(range(5, 26, 4))],
        (34, {}, {}),
    ]),
    # list, bow up and sink below the board
    "Death": (46, False, [
        (0, {}, {}),
        (8, {"hips": (0, 0, 15), "chest": (0, 0, 8)}, anchored(0, 15, -0.03)),
        (22, {"hips": (-18, 0, 30), "chest": (5, 0, 15), "neck": (0, 0, 20)}, anchored(-18, 30, -0.3)),
        (46, {"hips": (-28, 0, 38), "chest": (10, 0, 20), "neck": (0, 0, 25)}, anchored(-28, 38, -0.95)),
    ]),
}


# ---------------------------------------------------------------- Vikings: War Bear (knight) on the beast rig
BEAR = beast_rig(body=((0, 0.36, 0.8), (0, -0.3, 0.88)), neck=((0, -0.3, 0.9), (0, -0.48, 0.98)),
                 head=((0, -0.48, 0.98), (0, -0.76, 0.9)), jaw=((0, -0.52, 0.9), (0, -0.78, 0.84)),
                 tail=((0, 0.44, 0.86), (0, 0.52, 0.78)),
                 fl=((0.18, -0.24, 0.72), (0.18, -0.24, 0.36), (0.18, -0.28, 0.03)),
                 bl=((0.17, 0.3, 0.74), (0.17, 0.36, 0.38), (0.17, 0.32, 0.03)))


def viking_knight(k):
    """War Bear: huge brown bear in iron barding and a fur-lined saddle, a Viking rider with axe and shield."""
    RB = {n: (h, t) for n, h, t, *_ in BEAR}
    plinth(k)
    for s in (1, -1):
        x = "L" if s > 0 else "R"
        limb(k, "Bear", f"fl_up.{x}", RB, 0.12, 0.095)
        limb(k, "Bear", f"fl_lo.{x}", RB, 0.095, 0.085)
        k.sphere("Bear", f"bl_up.{x}", T(0.17 * s, 0.3, 0.68), (0.13, 0.17, 0.17), seg=10)
        limb(k, "Bear", f"bl_up.{x}", RB, 0.11, 0.09)
        limb(k, "Bear", f"bl_lo.{x}", RB, 0.09, 0.08)
        for pre in ("fl", "bl"):
            hx, hy, hz = RB[f"{pre}_lo.{x}"][1]
            k.box("BearDark", f"{pre}_lo.{x}", T(hx, hy - 0.03, hz + 0.035), (0.15, 0.19, 0.07), bevel=0.025)
            for c in (-0.045, -0.015, 0.015, 0.045):
                k.cyl("Bone", f"{pre}_lo.{x}", T(hx + c, hy - 0.13, hz + 0.025) @ R(x=100), 0.011, 0.0, 0.05, seg=4)
    # big body with a shoulder hump, iron barding and saddle
    k.sphere("Bear", "body", T(0, 0.04, 0.82), (0.27, 0.46, 0.3), seg=14)
    k.sphere("Bear", "body", T(0, -0.2, 0.98), (0.24, 0.24, 0.22), seg=12)
    for s in (1, -1):
        k.box("Iron", "body", T(0.255 * s, -0.12, 0.86) @ R(y=-12 * s), (0.03, 0.3, 0.26), bevel=0.02)
        for y in (-0.22, -0.12, -0.02):
            k.sphere("Gold", "body", T(0.275 * s, y, 0.92), (0.012, 0.015, 0.015), seg=6)
        k.box("TunicRed", "body", T(0.27 * s, 0.14, 0.78), (0.02, 0.3, 0.3))
        k.box("Gold", "body", T(0.281 * s, 0.14, 0.64), (0.006, 0.3, 0.025))
    k.box("Leather", "body", T(0, 0.1, 1.11), (0.34, 0.36, 0.06), bevel=0.02)
    k.box("Fur", "body", T(0, 0.1, 1.15), (0.3, 0.32, 0.03), bevel=0.01)
    k.box("Leather", "body", T(0, 0.27, 1.17), (0.26, 0.04, 0.1), bevel=0.015)
    # neck with an iron collar, head with chanfron, round ears, snarling jaw
    M, L = along((0, -0.27, 0.88), (0, -0.5, 1.0)); k.cyl("Bear", "neck_q", M, 0.2, 0.16, L, seg=10)
    k.torus("Iron", "neck_q", T(0, -0.36, 0.93) @ R(x=-65), 0.19, 0.025, su=14, sv=4)
    k.sphere("Bear", "head_q", T(0, -0.56, 0.97), (0.15, 0.15, 0.13), seg=12)
    M, L = along((0, -0.62, 0.94), (0, -0.8, 0.9)); k.box("BearDark", "head_q", M, (0.11, 0.1, L), bevel=0.03)
    k.box("Dark", "head_q", T(0, -0.81, 0.93), (0.06, 0.025, 0.035), bevel=0.008)
    k.box("Iron", "head_q", T(0, -0.6, 1.05) @ R(x=-30), (0.13, 0.16, 0.025), bevel=0.01)
    k.box("Gold", "head_q", T(0, -0.6, 1.065) @ R(x=-30), (0.02, 0.14, 0.01))
    for s in (1, -1):
        k.sphere("Bear", "head_q", T(0.1 * s, -0.5, 1.08), (0.045, 0.025, 0.045), seg=8)
        k.sphere("Dark", "head_q", T(0.07 * s, -0.66, 1.0), (0.016, 0.012, 0.016), seg=6)
        k.cyl("Bone", "head_q", T(0.035 * s, -0.78, 0.86) @ R(x=180), 0.011, 0.0, 0.05, seg=4)
    k.box("Lips", "head_q", T(0, -0.7, 0.875), (0.08, 0.14, 0.012))
    M, L = along((0, -0.56, 0.88), (0, -0.78, 0.84)); k.box("BearDark", "jaw", M, (0.09, 0.05, L), bevel=0.015)
    for s in (1, -1):
        k.cyl("Bone", "jaw", T(0.03 * s, -0.75, 0.88), 0.01, 0.0, 0.05, seg=4)
    k.sphere("Bear", "tail", T(0, 0.48, 0.84), (0.05, 0.05, 0.05), seg=6)
    # the rider
    k.xf = RIDER
    for s in (1, -1):
        M, L = along((0.13 * s, 0, 0.95), (0.34 * s, -0.25, 0.8)); k.cyl("Trousers", "hips", M, 0.08, 0.07, L, seg=8)
        M, L = along((0.34 * s, -0.25, 0.8), (0.36 * s, -0.17, 0.45)); k.cyl("Trousers", "hips", M, 0.066, 0.06, L, seg=8)
        k.box("Leather", "hips", T(0.36 * s, -0.21, 0.4), (0.11, 0.22, 0.1), bevel=0.03)
    viking_torso(k)
    viking_arms(k)
    viking_face(k)
    viking_helm(k)
    axe(k)
    round_shield(k, face="Sail", ring="TunicRed")
    k.xf = Matrix()

RIG_OF = {knight_knight: HORSE, undead_knight: WOLF, mafia_knight: CAR, marine_knight: JEEP, viking_knight: BEAR}


def set_action(arm, act):
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = act
    if hasattr(ad, "action_slot") and ad.action_slot is None and len(act.slots):
        ad.action_slot = act.slots[0]


def pose_matrix(arm, name, rots, locs):
    """Armature-space matrix of a bone for a pose given as {bone: degrees} and {bone: location},
    computed from the rest pose (independent of whatever the scene last evaluated)."""
    bone = arm.data.bones[name]
    basis = Matrix.Translation(locs.get(name, (0, 0, 0))) @         Euler([math.radians(a) for a in rots.get(name, (0, 0, 0))], "XYZ").to_matrix().to_4x4()
    if bone.parent:
        return pose_matrix(arm, bone.parent.name, rots, locs) @ bone.parent.matrix_local.inverted() @ bone.matrix_local @ basis
    return bone.matrix_local @ basis


def make_clips(arm, clips=None, prefix="", loc_bones=("hips",)):
    acts = {}
    for name, (end, cyclic, keys, *opts) in (clips or CLIPS).items():
        act = bpy.data.actions.new(prefix + name)
        act.use_fake_user = True
        set_action(arm, act)
        # Key every bone in every clip so each clip is self-contained (no pose bleeding between clips).
        rot_bones = [pb.name for pb in arm.pose.bones]
        prev_head = Euler((0, 0, 0), "XYZ")
        for frame, p, l in keys:
            for b in rot_bones:
                arm.pose.bones[b].rotation_euler = [math.radians(a) for a in p.get(b, (0, 0, 0))]
            for b in loc_bones:
                arm.pose.bones[b].location = l.get(b, (0, 0, 0))
            if "_head" in l:
                off, tumble = l["_head"]
                hb = arm.pose.bones["head"]
                h = hb.bone.head_local
                spin = Euler([math.radians(a) for a in tumble], "XYZ").to_matrix().to_4x4()
                want = Matrix.Translation(h + Vector(off)) @ spin @ Matrix.Translation(-h) @ hb.bone.matrix_local
                parent = hb.bone.parent
                base = pose_matrix(arm, parent.name, p, l) @ parent.matrix_local.inverted() @ hb.bone.matrix_local
                basis = base.inverted() @ want
                hb.location = basis.to_translation()
                # keep the tumble turning the same way between keys
                hb.rotation_euler = prev_head = basis.to_euler("XYZ", prev_head)
            for b in rot_bones:
                arm.pose.bones[b].keyframe_insert("rotation_euler", frame=frame, group=b)
            for b in loc_bones:
                arm.pose.bones[b].keyframe_insert("location", frame=frame, group=b)
        if "linear" in opts:  # constant speed, e.g. wheels turning
            for layer in act.layers:
                for strip in layer.strips:
                    for cb in strip.channelbags:
                        for fc in cb.fcurves:
                            for kp in fc.keyframe_points:
                                kp.interpolation = "LINEAR"
        act.use_frame_range = True
        act.frame_start, act.frame_end = 0, end
        act.use_cyclic = cyclic
        acts[name] = act
    return acts


# ---------------------------------------------------------------- scene
def clipset(arm, kind, prefix=""):
    """Make one clip set, removing any other first: both exporters put every armature action in the
    file into each export, so humanoid and tower clips must never exist at the same time."""
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)
    clips, loc = CLIPSETS[kind]
    return make_clips(arm, clips, prefix, loc)


CLIPSETS = {"human": (CLIPS, ("hips", "head")), "sniper": (SNIPER_CLIPS, ("hips", "head")),
            "tower": (TOWER_CLIPS, ("hips",)), "bunker": (BUNKER_CLIPS, ("hips",)), "ship": (SHIP_CLIPS, ("hips",)),
            "beast": (BEAST_CLIPS, ("body",)), "vehicle": (VEHICLE_CLIPS, ("body",))}


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.fps = 30


def unit(army, team, builder, name, x):
    rig = RIG_OF.get(builder)
    arm = build_armature(name, x, rig)
    k = Kit(army, PALETTES[army], team, rig)
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


def show_only(arms, all_arms):
    for a in all_arms:
        hidden = a not in arms
        for o in (a, *a.children):
            o.hide_render = hidden
            o.hide_viewport = hidden


def render_sheet(arms, acts, tag, shots):
    import numpy as np
    sc = bpy.context.scene
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
    out.filepath_raw = os.path.join(PREVIEW, f"{tag}_poses.png")
    out.file_format = "PNG"
    out.save()
    # hero shot
    for a in arms:
        set_action(a, acts["Idle"])
    sc.frame_set(0)
    sc.render.resolution_x, sc.render.resolution_y = 1200, 900
    sc.render.filepath = os.path.join(PREVIEW, f"{tag}_hero.png")
    bpy.ops.render.render(write_still=True)


def main():
    os.makedirs(EXPORT, exist_ok=True)
    os.makedirs(PREVIEW, exist_ok=True)
    clear()
    # Preview groups: units of a group stand side by side; groups are rendered separately.
    groups = {
        "pawns": [("Knights", "Ivory", knight_pawn, "Knights_Pawn", -1.2),
                  ("Mafia", "Ivory", mafia_pawn, "Mafia_Pawn", 0.0),
                  ("Undead", "Onyx", undead_pawn, "Undead_Pawn", 1.2)],
        "kings": [("Knights", "Ivory", knight_king, "Knights_King", -0.65),
                  ("Undead", "Onyx", undead_king, "Undead_King", 0.65)],
        "queens": [("Knights", "Ivory", knight_queen, "Knights_Queen", -0.65),
                   ("Undead", "Onyx", undead_queen, "Undead_Queen", 0.65)],
        "bishops": [("Knights", "Ivory", knight_bishop, "Knights_Bishop", -0.65),
                    ("Undead", "Onyx", undead_bishop, "Undead_Bishop", 0.65)],
        "rooks": [("Knights", "Ivory", knight_rook, "Knights_Rook", -0.65),
                  ("Undead", "Onyx", undead_rook, "Undead_Rook", 0.65)],
        "knights": [("Knights", "Ivory", knight_knight, "Knights_Knight", -0.7),
                    ("Undead", "Onyx", undead_knight, "Undead_Knight", 0.7)],
        "mafia": [("Mafia", "Ivory", mafia_king, "Mafia_King", -1.2),
                  ("Mafia", "Ivory", mafia_queen, "Mafia_Queen", 0.0),
                  ("Mafia", "Ivory", mafia_bishop, "Mafia_Bishop", 1.2)],
        "mafia_rook": [("Mafia", "Ivory", mafia_rook, "Mafia_Rook", 0.0)],
        "mafia_knight": [("Mafia", "Ivory", mafia_knight, "Mafia_Knight", 0.0)],
        "marines": [("Marines", "Ivory", marine_pawn, "Marines_Pawn", -1.2),
                    ("Marines", "Ivory", marine_king, "Marines_King", 0.0),
                    ("Marines", "Ivory", marine_queen, "Marines_Queen", 1.2)],
        "marines_sniper": [("Marines", "Ivory", marine_bishop, "Marines_Bishop", 0.0)],
        "marines_rook": [("Marines", "Ivory", marine_rook, "Marines_Rook", 0.0)],
        "marines_knight": [("Marines", "Ivory", marine_knight, "Marines_Knight", 0.0)],
        "vikings": [("Vikings", "Onyx", viking_pawn, "Vikings_Pawn", -1.8),
                    ("Vikings", "Onyx", viking_king, "Vikings_King", -0.6),
                    ("Vikings", "Onyx", viking_queen, "Vikings_Queen", 0.6),
                    ("Vikings", "Onyx", viking_bishop, "Vikings_Bishop", 1.8)],
        "vikings_rook": [("Vikings", "Onyx", viking_rook, "Vikings_Rook", 0.0)],
        "vikings_knight": [("Vikings", "Onyx", viking_knight, "Vikings_Knight", 0.0)],
    }
    kind_of = {"rooks": "tower", "mafia_rook": "tower", "knights": "beast", "mafia_knight": "vehicle",
               "marines_sniper": "sniper", "marines_rook": "bunker", "marines_knight": "vehicle",
               "vikings_rook": "ship", "vikings_knight": "beast"}  # everything else is humanoid
    built = {g: [unit(*spec) for spec in specs] for g, specs in groups.items()}
    every = [a for arms in built.values() for a in arms]
    kinds = {kd: [g for g in built if kind_of.get(g, "human") == kd] for kd in CLIPSETS}
    render = "--render" in ARGS
    engine = ARGS[ARGS.index("--engine") + 1] if "--engine" in ARGS else "eevee"
    only = ARGS[ARGS.index("--group") + 1] if "--group" in ARGS else None
    if render:
        stage(engine)
    shots = {
            "pawns": [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Shoot", 10), ("Death", 46)],
            "kings": [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Death", 20), ("Death", 46)],
            "queens": [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Death", 20), ("Death", 46)],
            "bishops": [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Death", 20), ("Death", 46)],
            "rooks": [("Idle", 0), ("Walk", 6), ("Attack", 8), ("Attack", 13), ("Death", 24), ("Death", 46)],
            "knights": [("Idle", 0), ("Walk", 0), ("Walk", 6), ("Attack", 8), ("Attack", 14), ("Death", 46)],
            "mafia": [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Shoot", 10), ("Death", 46)],
            "mafia_rook": [("Idle", 0), ("Walk", 6), ("Attack", 8), ("Attack", 13), ("Death", 24), ("Death", 46)],
            "mafia_knight": [("Idle", 0), ("Walk", 3), ("Attack", 8), ("Attack", 14), ("Shoot", 10), ("Death", 46)],
            "marines": [("Idle", 0), ("Walk", 0), ("Shoot", 10), ("Behead", 10), ("Behead", 23), ("Behead", 46)],
            "marines_sniper": [("Idle", 0), ("Shoot", 8), ("Shoot", 22), ("Shoot", 25), ("Behead", 18), ("Behead", 46)],
            "marines_rook": [("Idle", 0), ("Walk", 6), ("Attack", 8), ("Shoot", 7), ("Death", 24), ("Death", 46)],
            "marines_knight": [("Idle", 0), ("Walk", 3), ("Attack", 8), ("Attack", 14), ("Shoot", 10), ("Death", 46)],
            "vikings": [("Idle", 0), ("Walk", 0), ("Attack", 9), ("Attack", 14), ("Behead", 23), ("Death", 46)],
            "vikings_rook": [("Idle", 0), ("Walk", 6), ("Attack", 8), ("Attack", 13), ("Death", 22), ("Death", 46)],
            "vikings_knight": [("Idle", 0), ("Walk", 0), ("Attack", 8), ("Attack", 14), ("Shoot", 8), ("Death", 46)],
        }
    for kind, gs in kinds.items():
        arms = [a for g in gs for a in built[g]]
        if not arms:
            continue
        acts = clipset(arms[0], kind)
        show_only(every, every)  # a render of an earlier group hides the rest, and hidden units export empty
        for arm in arms:
            set_action(arm, acts["Idle"])
            export_fbx(arm, os.path.join(EXPORT, arm.name + ".fbx"))
        if render:
            for g in gs:
                if not only or g in only.split(","):  # --group takes a comma-separated list
                    show_only(built[g], every)
                    render_sheet(built[g], acts, g, shots[g])
    # keep every clip set in the .blend for reference (non-humanoid sets prefixed there)
    for a in list(bpy.data.actions):
        bpy.data.actions.remove(a)
    for kind, gs in kinds.items():
        arms = [a for g in gs for a in built[g]]
        if not arms:
            continue
        clips, loc = CLIPSETS[kind]
        acts = make_clips(arms[0], clips, "" if kind == "human" else kind.capitalize() + "_", loc)
        for arm in arms:
            set_action(arm, acts["Idle"])
    show_only(every, every)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(EXPORT, "units.blend"))
    print("CHESSWARS DONE")


main()
