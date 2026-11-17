"""Chess Wars battlefield props (Blender 5.x): low-poly scenery for the Unity battlefields.

Each prop is one mesh with a few flat-colour materials, exported as FBX into the Unity project
(unity/ChessWars/Assets/Resources/Props, loaded by name at run time). Kept cheap for low-end phones: a few hundred triangles each,
and Unity batches them because they never move.

Run:  blender -b -P art/blender/build_props.py
"""
import bpy, bmesh, math, os, random
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(os.path.dirname(HERE)), "unity", "ChessWars", "Assets", "Resources", "Props")


def lin(hexcol):
    def ch(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return (ch(hexcol >> 16 & 255), ch(hexcol >> 8 & 255), ch(hexcol & 255), 1.0)


def material(name, hexcol, rough=0.85, emit=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = lin(hexcol)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = 0.0
    if emit:
        bsdf.inputs["Emission Color"].default_value = lin(hexcol)
        bsdf.inputs["Emission Strength"].default_value = emit
    return m


MATS = {
    "Leaf": (0x5f8f3a,), "LeafDark": (0x3f6b2c,), "Pine": (0x2f5a3a,), "Snow": (0xeef3f6,), "Blossom": (0xf2a9c0,),
    "Bark": (0x5a3e28,), "BarkDark": (0x3a2c22,), "Rock": (0x8a8a84,), "RockSand": (0xb08f62,), "RockSnow": (0xd8e2e8,),
    "RockDark": (0x3a3f46,), "Grave": (0x6e6e66,), "Moss": (0x4a5a32,), "StoneLight": (0xd8d2c4,), "Paper": (0xf2e2c0,),
    "Lantern": (0xffc870, 0.5, 3.0), "Rune": (0x9fe6ff, 0.5, 3.0), "Metal": (0x4a5058,), "Hazard": (0xe8b21e,),
    "Glow": (0xff3b2e, 0.5, 3.0), "Sand": (0xc2a873,), "Iron": (0x3a3a3a,), "Fire": (0xff8a2a, 0.5, 4.0),
    "Ember": (0xffd27a, 0.5, 5.0), "Banner": (0x9a1e1e,), "Gold": (0xd4a940,), "Wood": (0x6a4526,),
}


class Prop:
    """Collects parts into one bmesh, each part with its own material slot."""

    def __init__(self):
        self.bm = bmesh.new()
        self.slots = []

    def _add(self, verts_before, slot, M):
        new = [v for v in self.bm.verts if v not in verts_before]
        bmesh.ops.transform(self.bm, matrix=M, verts=new)
        if slot not in self.slots:
            self.slots.append(slot)
        idx = self.slots.index(slot)
        for f in {f for v in new for f in v.link_faces}:
            f.material_index = idx

    def cone(self, slot, M, r1, r2, h, seg=8):
        before = set(self.bm.verts)
        bmesh.ops.create_cone(self.bm, cap_ends=True, segments=seg, radius1=r1, radius2=max(r2, 1e-4), depth=h)
        self._add(before, slot, M @ Matrix.Translation((0, 0, h / 2)))

    def box(self, slot, M, size):
        before = set(self.bm.verts)
        r = bmesh.ops.create_cube(self.bm, size=1.0)
        bmesh.ops.scale(self.bm, vec=size, verts=r["verts"])
        self._add(before, slot, M @ Matrix.Translation((0, 0, size[2] / 2)))

    def ball(self, slot, M, radii, seg=8):
        before = set(self.bm.verts)
        r = bmesh.ops.create_icosphere(self.bm, subdivisions=1, radius=1.0)
        bmesh.ops.scale(self.bm, vec=radii, verts=r["verts"])
        self._add(before, slot, M)

    def build(self, name):
        me = bpy.data.meshes.new(name)
        self.bm.to_mesh(me)
        self.bm.free()
        obj = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(obj)
        for slot in self.slots:
            me.materials.append(material(slot, *MATS[slot]))
        for poly in me.polygons:
            poly.use_smooth = False   # faceted, low-poly look
        return obj


T = lambda x=0, y=0, z=0: Matrix.Translation((x, y, z))
R = lambda x=0, y=0, z=0: Matrix.Rotation(math.radians(z), 4, "Z") @ Matrix.Rotation(math.radians(y), 4, "Y") @ Matrix.Rotation(math.radians(x), 4, "X")


def jitter(prop, amount, seed):
    rnd = random.Random(seed)
    for v in prop.bm.verts:
        v.co += Vector((rnd.uniform(-amount, amount), rnd.uniform(-amount, amount), rnd.uniform(-amount, amount) * 0.5))


# ---------------------------------------------------------------- props
def tree_round():
    p = Prop()
    p.cone("Bark", T(), 0.14, 0.09, 1.2, seg=6)
    p.ball("Leaf", T(0, 0, 1.6), (0.75, 0.75, 0.7))
    p.ball("LeafDark", T(0.35, 0.2, 1.3), (0.45, 0.45, 0.4))
    p.ball("Leaf", T(-0.3, -0.25, 1.95), (0.45, 0.45, 0.4))
    return p


def tree_pine(snow=False):
    p = Prop()
    p.cone("Bark", T(), 0.12, 0.08, 0.6, seg=6)
    for i, (z, r) in enumerate(((0.4, 0.85), (1.05, 0.65), (1.6, 0.45))):
        p.cone("Pine", T(0, 0, z), r, 0.0, 0.95 - i * 0.15, seg=7)
        if snow:
            p.cone("Snow", T(0, 0, z + 0.55 - i * 0.08), r * 0.42, 0.0, 0.4 - i * 0.06, seg=7)
    return p


def tree_cherry():
    p = Prop()
    p.cone("BarkDark", T() @ R(y=8), 0.12, 0.07, 1.3, seg=6)
    p.cone("BarkDark", T(0.12, 0, 1.0) @ R(y=-40), 0.06, 0.03, 0.6, seg=5)
    for (x, y, z, s) in ((0, 0, 1.55, 0.7), (0.45, 0.1, 1.35, 0.45), (-0.4, 0.2, 1.4, 0.45), (0.1, -0.35, 1.8, 0.4)):
        p.ball("Blossom", T(x, y, z), (s, s, s * 0.75))
    return p


def tree_dead():
    p = Prop()
    p.cone("BarkDark", T(), 0.15, 0.05, 1.8, seg=5)
    for (z, a, tilt, ln) in ((0.9, 0, 50, 0.7), (1.2, 140, 45, 0.6), (1.5, 250, 55, 0.5), (0.6, 300, 60, 0.5)):
        p.cone("BarkDark", T(0, 0, z) @ R(z=a) @ R(y=tilt), 0.05, 0.01, ln, seg=4)
    return p


def rock(slot, seed):
    p = Prop()
    p.ball(slot, T(0, 0, 0.25), (0.6, 0.5, 0.42))
    p.ball(slot, T(0.35, 0.15, 0.12), (0.32, 0.3, 0.25))
    jitter(p, 0.07, seed)
    return p


def tombstone(cross=False):
    p = Prop()
    p.box("Grave", R(x=-6), (0.5, 0.14, 0.7))
    p.cone("Grave", T(0, 0, 0.7) @ R(x=90), 0.25, 0.25, 0.14, seg=10) if not cross else None
    if cross:
        p.box("Grave", T(0, 0, 0.7), (0.12, 0.12, 0.45))
        p.box("Grave", T(0, 0, 0.85), (0.38, 0.12, 0.1))
    p.box("Moss", T(0, 0, 0), (0.6, 0.3, 0.06))
    return p


def lantern():
    p = Prop()
    p.box("StoneLight", T(), (0.4, 0.4, 0.12))
    p.cone("StoneLight", T(0, 0, 0.12), 0.1, 0.1, 0.6, seg=6)
    p.box("StoneLight", T(0, 0, 0.72), (0.38, 0.38, 0.08))
    p.box("Lantern", T(0, 0, 0.8), (0.24, 0.24, 0.24))
    p.cone("StoneLight", T(0, 0, 1.04), 0.36, 0.05, 0.22, seg=4)
    return p


def column():
    p = Prop()
    p.box("StoneLight", T(), (0.7, 0.7, 0.18))
    p.cone("StoneLight", T(0, 0, 0.18), 0.27, 0.24, 1.6, seg=10)
    p.box("StoneLight", T(0.5, 0.3, 0) @ R(z=30, x=85), (0.5, 0.5, 0.5))
    return p


def floating_rock():
    p = Prop()
    p.cone("RockDark", T(0, 0, 0.2), 0.5, 0.0, -0.9, seg=6)
    p.cone("Moss", T(0, 0, 0.12), 0.55, 0.5, 0.12, seg=6)
    p.box("Rune", T(0, 0, 0.24) @ R(z=20), (0.3, 0.05, 0.02))
    p.box("Rune", T(0, 0, 0.24) @ R(z=110), (0.2, 0.05, 0.02))
    return p


def pylon():
    p = Prop()
    p.box("Metal", T(), (0.6, 0.6, 0.2))
    p.cone("Metal", T(0, 0, 0.2), 0.16, 0.1, 1.8, seg=6)
    for z in (0.6, 1.1, 1.6):
        p.cone("Hazard", T(0, 0, z), 0.2, 0.2, 0.08, seg=6)
    p.ball("Glow", T(0, 0, 2.05), (0.14, 0.14, 0.14))
    return p


def sandbags():
    p = Prop()
    for row, z in enumerate((0.0, 0.17, 0.34)):
        for i in range(3 - row):
            x = (i - (2 - row) / 2) * 0.42
            p.ball("Sand", T(x, 0, z + 0.09), (0.22, 0.14, 0.1))
    return p


def hedgehog():
    p = Prop()
    for a in (0, 60, 120):
        p.box("Iron", T(0, 0, 0.35) @ R(z=a, y=55), (0.08, 0.08, 1.1))
    return p


def brazier():
    p = Prop()
    p.cone("Iron", T(), 0.22, 0.12, 0.5, seg=8)
    p.cone("Iron", T(0, 0, 0.5), 0.12, 0.3, 0.2, seg=8)
    p.cone("Ember", T(0, 0, 0.66), 0.25, 0.25, 0.04, seg=8)
    p.cone("Fire", T(0, 0, 0.68), 0.2, 0.0, 0.5, seg=6)
    p.cone("Fire", T(0.08, 0.05, 0.68), 0.11, 0.0, 0.35, seg=5)
    return p


def banner():
    p = Prop()
    p.cone("Wood", T(), 0.04, 0.035, 2.4, seg=6)
    p.ball("Gold", T(0, 0, 2.45), (0.06, 0.06, 0.06))
    p.box("Wood", T(0, 0, 2.2) @ R(y=90), (0.03, 0.03, 0.7))
    p.box("Banner", T(0, 0.01, 1.25), (0.62, 0.02, 0.95))
    return p


PROPS = {
    "TreeRound": tree_round, "TreePine": lambda: tree_pine(False), "TreePineSnow": lambda: tree_pine(True),
    "TreeCherry": tree_cherry, "TreeDead": tree_dead, "RockGrey": lambda: rock("Rock", 1),
    "RockSand": lambda: rock("RockSand", 2), "RockSnow": lambda: rock("RockSnow", 3), "RockDark": lambda: rock("RockDark", 4),
    "Tombstone": lambda: tombstone(False), "TombstoneCross": lambda: tombstone(True), "Lantern": lantern,
    "Column": column, "FloatingRock": floating_rock, "Pylon": pylon, "Sandbags": sandbags, "Hedgehog": hedgehog,
    "Brazier": brazier, "Banner": banner,
}


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    os.makedirs(OUT, exist_ok=True)
    for name, make in PROPS.items():
        for o in list(bpy.context.scene.objects):
            bpy.data.objects.remove(o)
        obj = make().build(name)
        for o in bpy.context.scene.objects:
            o.select_set(o == obj)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, name + ".fbx"), use_selection=True, object_types={"MESH"},
                                 apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE",
                                 bake_space_transform=True)   # Z-up into Unity's Y-up in the mesh itself, so props stand upright
        print(f"prop {name}: {len(obj.data.polygons)} faces")
    print("PROPS DONE")


main()
