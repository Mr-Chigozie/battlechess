"use strict";

// Procedural low-poly units. Every model faces +z, stands on a plinth of radius ~0.4 and fits a 1x1 square.
// buildUnit() returns a Group whose userData.rig exposes the parts the animations move.
const Models = (() => {
  const geoCache = new Map();
  const cached = (key, make) => {
    if (!geoCache.has(key)) geoCache.set(key, make());
    return geoCache.get(key);
  };
  const G = {
    box: (w, h, d) => cached(`b${w},${h},${d}`, () => new THREE.BoxGeometry(w, h, d)),
    cyl: (rt, rb, h, s = 10) => cached(`c${rt},${rb},${h},${s}`, () => new THREE.CylinderGeometry(rt, rb, h, s)),
    cone: (r, h, s = 8) => cached(`k${r},${h},${s}`, () => new THREE.ConeGeometry(r, h, s)),
    sphere: (r, w = 10, h = 8) => cached(`s${r},${w},${h}`, () => new THREE.SphereGeometry(r, w, h)),
    dome: r => cached(`d${r}`, () => new THREE.SphereGeometry(r, 10, 6, 0, Math.PI * 2, 0, Math.PI / 2)),
    torus: (r, t, arc = Math.PI * 2) => cached(`t${r},${t},${arc}`, () => new THREE.TorusGeometry(r, t, 6, 16, arc))
  };

  const std = (color, extra = {}) => new THREE.MeshStandardMaterial({ color, roughness: 0.62, metalness: 0.12, flatShading: true, ...extra });
  const paletteCache = new Map();

  // Ivory armies are lightened, Onyx armies darkened, so mirror matches stay readable.
  function palette(army, color) {
    const key = `${army}-${color}`;
    if (paletteCache.has(key)) return paletteCache.get(key);
    const look = ARMIES[army].look;
    const tint = (hex, amount) => {
      const c = new THREE.Color(hex);
      return color === "w" ? c.lerp(new THREE.Color(0xffffff), amount) : c.multiplyScalar(0.55);
    };
    const P = {
      armor: std(tint(look.armor, 0.22), { metalness: 0.55, roughness: 0.38 }),
      cloth: std(tint(look.cloth, 0.12)),
      stone: std(tint(0x8f8a7e, 0.25), { roughness: 0.9 }),
      accent: std(look.accent),
      skin: std(look.skin),
      dark: std(0x1d1c1f),
      metal: std(0xc9ced6, { metalness: 0.85, roughness: 0.25 }),
      wood: std(0x6b4a2e),
      gold: std(0xe0b84a, { metalness: 0.75, roughness: 0.3 }),
      bone: std(0xe9e0c8),
      glow: std(look.magic, { emissive: look.magic, emissiveIntensity: 1.8 }),
      glass: std(0xbfe8ff, { transparent: true, opacity: 0.35, metalness: 0.2, roughness: 0.05 }),
      base: std(color === "w" ? 0xe9e2cf : 0x1b1c20, { roughness: 0.35, metalness: 0.15 }),
      rim: std(color === "w" ? 0xd6ad66 : 0x8a3b32, { emissive: color === "w" ? 0xd6ad66 : 0x8a3b32, emissiveIntensity: 0.4 }),
      mane: std(army === "knights" ? 0xe8a33a : 0x2b211b),
      hide: std(army === "knights" ? 0x26262b : army === "asgardians" ? 0xf2f0ea : army === "undead" ? 0x5b6156 : army === "vikings" ? 0x5a3b24 : 0x7a5434)
    };
    paletteCache.set(key, P);
    return P;
  }

  function add(geo, mat, x, y, z, parent) {
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.set(x, y, z);
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    parent.add(mesh);
    return mesh;
  }

  function pivot(x, y, z, parent) {
    const group = new THREE.Group();
    group.position.set(x, y, z);
    parent.add(group);
    return group;
  }

  // ---- Heads: the group origin sits at the neck.
  function head(P, kind, parent, o = {}) {
    const face = () => add(G.sphere(0.1), P.skin, 0, 0.1, 0.01, parent);
    switch (kind) {
      case "helm": {
        add(G.cyl(0.12, 0.12, 0.22), P.armor, 0, 0.11, 0, parent);
        add(G.dome(0.12), P.armor, 0, 0.22, 0, parent);
        add(G.box(0.18, 0.025, 0.02), P.dark, 0, 0.13, 0.115, parent);
        for (let i = 0; i < 3; i += 1) add(G.sphere(0.07 - i * 0.012), P.accent, 0, 0.33 - i * 0.02, -0.02 - i * 0.07, parent);
        break;
      }
      case "skull": {
        add(G.sphere(0.11), P.bone, 0, 0.11, 0, parent).scale.set(1, 1.08, 1);
        add(G.box(0.12, 0.05, 0.08), P.bone, 0, 0.02, 0.04, parent);
        for (const s of [-1, 1]) add(G.sphere(0.025, 6, 4), P.glow, 0.04 * s, 0.12, 0.09, parent);
        break;
      }
      case "horned": {
        face();
        add(G.dome(0.115), P.armor, 0, 0.12, 0, parent);
        add(G.box(0.025, 0.1, 0.03), P.armor, 0, 0.08, 0.1, parent);
        for (const s of [-1, 1]) add(G.cone(0.03, 0.17), P.bone, 0.13 * s, 0.2, 0, parent).rotation.z = -1.05 * s;
        break;
      }
      case "winged": {
        face();
        add(G.dome(0.118), P.gold, 0, 0.12, 0, parent);
        for (const s of [-1, 1]) {
          add(G.box(0.02, 0.16, 0.1), P.bone, 0.12 * s, 0.2, -0.02, parent).rotation.z = -0.55 * s;
          add(G.box(0.02, 0.1, 0.08), P.bone, 0.15 * s, 0.15, -0.06, parent).rotation.z = -0.95 * s;
        }
        break;
      }
      case "kabuto": {
        face();
        add(G.dome(0.12), P.armor, 0, 0.13, 0, parent);
        add(G.cyl(0.13, 0.2, 0.05, 10), P.armor, 0, 0.11, -0.01, parent);
        const crest = add(G.torus(0.08, 0.013, Math.PI), P.gold, 0, 0.22, 0.09, parent);
        crest.rotation.z = Math.PI;
        crest.position.y = 0.3;
        break;
      }
      case "crested": {
        add(G.cyl(0.115, 0.12, 0.24), P.armor, 0, 0.11, 0, parent);
        add(G.dome(0.115), P.armor, 0, 0.23, 0, parent);
        add(G.box(0.03, 0.12, 0.02), P.dark, 0, 0.08, 0.115, parent);
        add(G.box(0.16, 0.025, 0.02), P.dark, 0, 0.14, 0.115, parent);
        const crest = add(G.torus(0.14, 0.04, Math.PI), P.accent, 0, 0.2, -0.02, parent);
        crest.rotation.y = Math.PI / 2;
        break;
      }
      case "visor": {
        add(G.box(0.2, 0.18, 0.2), P.armor, 0, 0.1, 0, parent);
        add(G.box(0.17, 0.035, 0.02), P.glow, 0, 0.12, 0.105, parent);
        add(G.cyl(0.008, 0.008, 0.14, 4), P.dark, 0.07, 0.25, 0, parent);
        add(G.sphere(0.02, 6, 4), P.glow, 0.07, 0.33, 0, parent);
        break;
      }
      case "combat": {
        face();
        add(G.dome(0.125), P.cloth, 0, 0.12, 0, parent).scale.set(1, 0.85, 1.1);
        add(G.cyl(0.14, 0.14, 0.02, 10), P.cloth, 0, 0.12, 0, parent);
        add(G.box(0.15, 0.03, 0.03), P.dark, 0, 0.17, 0.11, parent);
        break;
      }
      case "brain": {
        add(G.box(0.2, 0.08, 0.2), P.armor, 0, 0.04, 0, parent);
        add(G.sphere(0.1), std(0xe58aa8, { emissive: 0x7a1f3a, emissiveIntensity: 0.6 }), 0, 0.16, 0, parent);
        add(G.sphere(0.14), P.glass, 0, 0.15, 0, parent);
        break;
      }
      default:
        face();
    }
    if (o.hood) {
      add(G.cone(0.16, 0.34, 8), P.cloth, 0, 0.17, -0.02, parent);
      add(G.sphere(0.085), P.dark, 0, 0.09, 0.05, parent);
      for (const s of [-1, 1]) add(G.sphere(0.018, 6, 4), P.glow, 0.035 * s, 0.1, 0.12, parent);
    }
    if (o.mitre) {
      add(G.cone(0.11, 0.3, 4), P.bone, 0, 0.3, 0, parent).rotation.y = Math.PI / 4;
      add(G.box(0.025, 0.1, 0.01), P.gold, 0, 0.28, 0.06, parent);
      add(G.box(0.07, 0.022, 0.01), P.gold, 0, 0.3, 0.061, parent);
    }
    if (o.crown) {
      const y = kind === "helm" || kind === "crested" ? 0.33 : kind === "brain" ? 0.3 : 0.24;
      add(G.cyl(0.1, 0.1, 0.06, 10), P.gold, 0, y, 0, parent);
      for (let i = 0; i < 5; i += 1) {
        const a = (i / 5) * Math.PI * 2;
        add(G.cone(0.025, 0.07, 4), P.gold, Math.sin(a) * 0.09, y + 0.06, Math.cos(a) * 0.09, parent);
      }
      add(G.sphere(0.022, 6, 4), P.glow, 0, y + 0.01, 0.1, parent);
    }
  }

  // ---- Weapons extend along +z from the hand. Returns an Object3D at the business end.
  function weapon(P, kind, hand) {
    const tip = new THREE.Object3D();
    const along = (geo, mat, z, y = 0) => {
      const mesh = add(geo, mat, 0, y, z, hand);
      mesh.rotation.x = Math.PI / 2;
      return mesh;
    };
    switch (kind) {
      case "sword":
        add(G.box(0.035, 0.015, 0.46), P.metal, 0, 0, 0.29, hand);
        add(G.box(0.15, 0.03, 0.03), P.gold, 0, 0, 0.05, hand);
        tip.position.z = 0.52;
        break;
      case "katana":
        add(G.box(0.025, 0.012, 0.56), P.metal, 0, 0.02, 0.33, hand).rotation.x = -0.08;
        add(G.cyl(0.04, 0.04, 0.01, 8), P.gold, 0, 0, 0.05, hand).rotation.x = Math.PI / 2;
        tip.position.set(0, 0.04, 0.6);
        break;
      case "axe":
        along(G.cyl(0.016, 0.016, 0.55, 6), P.wood, 0.2);
        add(G.box(0.02, 0.16, 0.11), P.metal, 0, 0.06, 0.42, hand);
        tip.position.set(0, 0.08, 0.45);
        break;
      case "spear":
        along(G.cyl(0.014, 0.014, 1.0, 6), P.wood, 0.28);
        along(G.cone(0.035, 0.14, 6), P.metal, 0.84);
        tip.position.z = 0.9;
        break;
      case "lance":
        along(G.cone(0.05, 1.25, 8), P.dark, 0.62);
        along(G.cone(0.08, 0.12, 8), P.armor, 0.05).rotation.x = -Math.PI / 2;
        tip.position.z = 1.25;
        break;
      case "hammer":
        along(G.cyl(0.018, 0.018, 0.34, 6), P.wood, 0.15);
        add(G.box(0.17, 0.12, 0.12), P.metal, 0, 0, 0.33, hand);
        add(G.sphere(0.03, 6, 4), P.glow, 0, 0.065, 0.33, hand);
        tip.position.z = 0.36;
        break;
      case "scythe": {
        along(G.cyl(0.015, 0.015, 0.9, 6), P.wood, 0.3);
        const blade = add(G.torus(0.2, 0.018, Math.PI * 0.7), P.metal, 0, 0.18, 0.72, hand);
        blade.rotation.y = Math.PI / 2;
        tip.position.set(0, 0.2, 0.75);
        break;
      }
      case "rifle":
        add(G.box(0.05, 0.08, 0.38), P.dark, 0, 0, 0.14, hand);
        along(G.cyl(0.012, 0.012, 0.2, 6), P.metal, 0.42);
        tip.position.z = 0.53;
        break;
      case "blaster":
        add(G.box(0.07, 0.09, 0.32), P.armor, 0, 0, 0.13, hand);
        add(G.box(0.05, 0.05, 0.04), P.glow, 0, 0, 0.31, hand);
        tip.position.z = 0.34;
        break;
      case "staff": {
        const staff = add(G.cyl(0.015, 0.015, 1.0, 6), P.wood, 0, 0.3, 0.05, hand);
        staff.rotation.x = 0.5;
        add(G.sphere(0.055), P.glow, 0, 0.72, 0.3, hand);
        tip.position.set(0, 0.72, 0.3);
        break;
      }
      default:
        tip.position.z = 0.2;
    }
    hand.add(tip);
    return tip;
  }

  // ---- Humanoid body. Origin at the feet.
  function humanoid(P, look, o = {}) {
    const body = new THREE.Group();
    const rig = { legs: [], body };
    if (o.robe) {
      add(G.cyl(0.13, 0.26, 0.52, 10), P.cloth, 0, 0.26, 0, body);
    } else if (!o.seated) {
      for (const s of [-1, 1]) {
        const hip = pivot(0.075 * s, 0.34, 0, body);
        add(G.box(0.11, 0.3, 0.13), P.armor, 0, -0.15, 0, hip);
        add(G.box(0.12, 0.06, 0.17), P.dark, 0, -0.31, 0.02, hip);
        rig.legs.push(hip);
      }
    } else {
      for (const s of [-1, 1]) {
        const hip = pivot(0.09 * s, 0.36, 0, body);
        hip.rotation.set(-1.3, 0, 0.5 * s);
        add(G.box(0.1, 0.32, 0.12), P.armor, 0, -0.16, 0, hip);
      }
    }
    add(G.box(0.32, 0.3, 0.2), P.armor, 0, 0.5, 0, body);
    add(G.box(0.34, 0.07, 0.22), P.cloth, 0, 0.36, 0, body);
    add(G.box(0.18, 0.2, 0.02), P.cloth, 0, 0.5, 0.105, body);
    add(G.box(0.06, 0.06, 0.01), P.accent, 0, 0.53, 0.117, body);
    for (const s of [-1, 1]) add(G.sphere(0.085, 8, 6), P.armor, 0.19 * s, 0.63, 0, body).scale.set(1, 0.7, 1);

    for (const s of [-1, 1]) {
      const shoulder = pivot(0.2 * s, 0.62, 0, body);
      add(G.box(0.08, 0.28, 0.09), P.armor, 0, -0.14, 0, shoulder);
      add(G.sphere(0.045, 6, 4), o.robe ? P.skin : P.dark, 0, -0.3, 0, shoulder);
      const hand = pivot(0, -0.3, 0, shoulder);
      if (s < 0) { rig.armR = shoulder; rig.handR = hand; } else { rig.armL = shoulder; rig.handL = hand; }
    }
    rig.armR.rotation.x = -0.55;
    rig.armL.rotation.x = -0.3;

    const neck = pivot(0, 0.66, 0, body);
    rig.head = neck;
    head(P, o.head || look.head, neck, o);
    if (o.cape) add(G.box(0.36, 0.56, 0.025), P.cloth, 0, 0.4, -0.13, body).rotation.x = 0.12;
    if (o.weapon) rig.tip = weapon(P, o.weapon, rig.handR);
    if (o.shield) {
      const shield = add(G.cyl(0.16, 0.16, 0.03, 12), o.shieldMat || P.accent, 0.04, 0.02, 0.06, rig.handL);
      shield.rotation.z = Math.PI / 2;
      shield.rotation.y = 0.2;
      add(G.sphere(0.04, 6, 4), P.metal, 0.06, 0.02, 0.06, rig.handL);
    }
    return rig;
  }

  // ---- Mounts. Origin at the hooves; returns { group, legs, saddleY }.
  function mount(P, kind) {
    const group = new THREE.Group();
    const legs = [];
    const quadLegs = (x, y, zs, w, h, mat) => {
      for (const z of zs) for (const s of [-1, 1]) {
        const hip = pivot(x * s, y, z, group);
        add(G.box(w, h, w + 0.01), mat, 0, -h / 2, 0, hip);
        add(G.box(w + 0.01, 0.05, w + 0.02), P.dark, 0, -h + 0.02, 0, hip);
        legs.push(hip);
      }
    };
    if (kind === "horse" || kind === "pegasus") {
      add(G.box(0.24, 0.26, 0.62), P.hide, 0, 0.55, 0, group);
      const neck = add(G.box(0.14, 0.36, 0.16), P.hide, 0, 0.74, 0.3, group);
      neck.rotation.x = 0.5;
      add(G.box(0.12, 0.13, 0.3), P.hide, 0, 0.88, 0.44, group).rotation.x = 0.35;
      for (const s of [-1, 1]) add(G.cone(0.025, 0.07, 4), P.hide, 0.04 * s, 0.98, 0.34, group);
      const mane = add(G.box(0.04, 0.36, 0.08), P.mane, 0, 0.8, 0.22, group);
      mane.rotation.x = 0.5;
      const tail = add(G.box(0.06, 0.34, 0.06), P.mane, 0, 0.48, -0.36, group);
      tail.rotation.x = -0.4;
      if (kind === "horse") {
        // Barding plates, as on a tourney charger.
        add(G.box(0.27, 0.2, 0.3), P.armor, 0, 0.55, 0.17, group);
        for (const s of [-1, 1]) add(G.box(0.02, 0.2, 0.24), P.armor, 0.14 * s, 0.4, 0.15, group);
        add(G.box(0.29, 0.06, 0.3), P.cloth, 0, 0.7, -0.05, group);
      } else {
        for (const s of [-1, 1]) {
          const wing = pivot(0.13 * s, 0.68, 0.05, group);
          add(G.box(0.5, 0.025, 0.2), P.bone, 0.25 * s, 0, 0, wing);
          add(G.box(0.3, 0.02, 0.14), P.bone, 0.45 * s, 0.02, -0.06, wing);
          wing.rotation.z = 0.35 * s;
          (group.userData.wings ||= []).push(wing);
        }
      }
      quadLegs(0.08, 0.43, [0.22, -0.22], 0.07, 0.42, P.hide);
      return { group, legs, saddleY: 0.62 };
    }
    if (kind === "wolf") {
      add(G.box(0.2, 0.2, 0.52), P.hide, 0, 0.45, 0, group);
      add(G.box(0.16, 0.16, 0.18), P.hide, 0, 0.58, 0.3, group);
      add(G.cone(0.06, 0.16, 5), P.hide, 0, 0.55, 0.45, group).rotation.x = Math.PI / 2;
      for (const s of [-1, 1]) {
        add(G.cone(0.035, 0.09, 4), P.hide, 0.05 * s, 0.7, 0.28, group);
        add(G.sphere(0.018, 6, 4), P.glow, 0.05 * s, 0.61, 0.39, group);
      }
      add(G.box(0.05, 0.05, 0.3), P.hide, 0, 0.5, -0.36, group).rotation.x = 0.5;
      add(G.box(0.22, 0.04, 0.24), P.cloth, 0, 0.56, -0.02, group);
      quadLegs(0.07, 0.36, [0.18, -0.18], 0.06, 0.34, P.hide);
      return { group, legs, saddleY: 0.56 };
    }
    if (kind === "bear") {
      add(G.sphere(0.22, 10, 8), P.hide, 0, 0.5, 0, group).scale.set(1, 0.95, 1.5);
      add(G.sphere(0.15, 10, 8), P.hide, 0, 0.62, 0.34, group);
      add(G.box(0.1, 0.08, 0.12), P.hide, 0, 0.58, 0.48, group);
      add(G.sphere(0.025, 6, 4), P.dark, 0, 0.6, 0.55, group);
      for (const s of [-1, 1]) add(G.sphere(0.05, 6, 4), P.hide, 0.1 * s, 0.75, 0.32, group);
      add(G.box(0.3, 0.05, 0.26), P.cloth, 0, 0.7, -0.02, group);
      quadLegs(0.12, 0.38, [0.2, -0.2], 0.1, 0.34, P.hide);
      return { group, legs, saddleY: 0.7 };
    }
    if (kind === "spider") {
      add(G.sphere(0.2, 10, 8), P.armor, 0, 0.45, 0, group).scale.set(1.1, 0.7, 1.2);
      add(G.sphere(0.1, 8, 6), P.armor, 0, 0.45, 0.25, group);
      add(G.sphere(0.04, 6, 4), P.glow, 0, 0.47, 0.34, group);
      for (let i = 0; i < 4; i += 1) for (const s of [-1, 1]) {
        const hip = pivot(0.16 * s, 0.45, 0.18 - i * 0.12, group);
        hip.rotation.y = s * (0.3 - i * 0.2);
        const upper = add(G.box(0.3, 0.03, 0.03), P.dark, 0.14 * s, 0.08, 0, hip);
        upper.rotation.z = 0.55 * s;
        const lower = add(G.box(0.03, 0.42, 0.03), P.armor, 0.3 * s, -0.08, 0, hip);
        lower.rotation.z = -0.25 * s;
        legs.push(hip);
      }
      return { group, legs, saddleY: 0.55 };
    }
    return { group, legs, saddleY: 0 };
  }

  // ---- Chess roles.
  const SHIELD_ARMIES = ["knights", "vikings", "spartans", "asgardians"];

  function weaponFor(army, type) {
    const look = ARMIES[army].look;
    if (type === "b") return "staff";
    if (type === "n") return ["knights", "asgardians"].includes(army) ? "lance" : look.weapon;
    if (type === "k") return army === "marines" ? "sword" : army === "asgardians" ? "spear" : look.weapon;
    return look.weapon;
  }

  function buildPawn(P, look, army) {
    const rig = humanoid(P, look, { weapon: weaponFor(army, "p"), shield: SHIELD_ARMIES.includes(army), shieldMat: army === "spartans" ? P.armor : P.accent });
    rig.body.scale.setScalar(0.8);
    return rig;
  }

  function buildKing(P, look, army) {
    const rig = humanoid(P, look, { weapon: weaponFor(army, "k"), cape: true, crown: true, head: army === "machines" ? "brain" : look.head });
    rig.body.scale.setScalar(1.12);
    return rig;
  }

  function buildQueen(P, look, army) {
    if (look.queen === "ufo") {
      const body = new THREE.Group();
      const saucer = add(G.sphere(0.38, 16, 8), P.armor, 0, 0.75, 0, body);
      saucer.scale.set(1, 0.22, 1);
      add(G.sphere(0.17, 12, 8), P.glass, 0, 0.82, 0, body);
      add(G.sphere(0.07, 8, 6), P.glow, 0, 0.82, 0, body);
      for (let i = 0; i < 8; i += 1) {
        const a = (i / 8) * Math.PI * 2;
        add(G.sphere(0.03, 6, 4), P.glow, Math.sin(a) * 0.34, 0.74, Math.cos(a) * 0.34, body);
      }
      const beam = add(G.cyl(0.16, 0.34, 0.66, 16), std(look.magic, { transparent: true, opacity: 0.14, emissive: look.magic, emissiveIntensity: 0.5 }), 0, 0.36, 0, body);
      beam.castShadow = false;
      const tip = pivot(0, 0.62, 0.2, body);
      return { body, legs: [], tip, spin: saucer, hover: 0.06 };
    }
    if (look.queen === "wraith") {
      const rig = humanoid(P, look, { robe: true, head: "skull", hood: true, crown: true, weapon: "scythe" });
      rig.body.position.y = 0.12;
      rig.hover = 0.05;
      return rig;
    }
    const rig = humanoid(P, look, { weapon: army === "asgardians" ? "hammer" : weaponFor(army, "q"), cape: true, crown: true, shield: army === "vikings", shieldMat: P.accent });
    rig.body.scale.setScalar(1.05);
    return rig;
  }

  function buildBishop(P, look, army) {
    if (look.bishop === "heli") {
      const body = new THREE.Group();
      add(G.sphere(0.2, 10, 8), P.armor, 0, 0.62, 0, body).scale.set(0.9, 0.8, 1.5);
      add(G.sphere(0.13, 10, 8), P.glass, 0, 0.66, 0.16, body);
      add(G.cyl(0.04, 0.06, 0.5, 6), P.armor, 0, 0.66, -0.4, body).rotation.x = Math.PI / 2;
      add(G.box(0.02, 0.14, 0.1), P.armor, 0, 0.74, -0.62, body);
      for (const s of [-1, 1]) add(G.box(0.025, 0.025, 0.5), P.dark, 0.13 * s, 0.4, 0, body);
      add(G.cyl(0.015, 0.015, 0.12, 4), P.dark, 0, 0.84, 0, body);
      const rotor = pivot(0, 0.9, 0, body);
      add(G.box(1.0, 0.012, 0.05), P.dark, 0, 0, 0, rotor);
      add(G.box(0.05, 0.012, 1.0), P.dark, 0, 0, 0, rotor);
      const tip = pivot(0, 0.52, 0.32, body);
      return { body, legs: [], tip, rotor, hover: 0.05 };
    }
    if (look.bishop === "dish") {
      const body = new THREE.Group();
      add(G.box(0.3, 0.14, 0.3), P.armor, 0, 0.5, 0, body);
      const dish = add(new THREE.SphereGeometry(0.24, 12, 6, 0, Math.PI * 2, 0, 0.9), P.metal, 0, 0.58, 0.05, body);
      dish.rotation.x = -0.9;
      add(G.cyl(0.01, 0.01, 0.22, 4), P.dark, 0, 0.7, 0.12, body).rotation.x = 0.9;
      add(G.sphere(0.035, 6, 4), P.glow, 0, 0.78, 0.2, body);
      const props = [];
      for (const [x, z] of [[1, 1], [1, -1], [-1, 1], [-1, -1]]) {
        add(G.box(0.25, 0.02, 0.02), P.dark, 0.13 * x, 0.52, 0.13 * z, body).rotation.y = x * z > 0 ? -0.78 : 0.78;
        const prop = pivot(0.24 * x, 0.55, 0.24 * z, body);
        add(G.box(0.18, 0.008, 0.03), P.dark, 0, 0, 0, prop);
        props.push(prop);
      }
      const tip = pivot(0, 0.78, 0.2, body);
      return { body, legs: [], tip, props, hover: 0.05 };
    }
    const rig = humanoid(P, look, {
      robe: true, weapon: "staff",
      head: army === "undead" ? "skull" : army === "samurai" ? "default" : "default",
      hood: army !== "knights", mitre: army === "knights"
    });
    rig.body.scale.setScalar(0.95);
    return rig;
  }

  function buildKnight(P, look, army) {
    if (look.mount === "parachute") {
      const rig = humanoid(P, look, { weapon: weaponFor(army, "n") });
      rig.body.scale.setScalar(0.85);
      const chute = pivot(0, 1.25, 0, rig.body);
      add(G.dome(0.42), P.cloth, 0, 0, 0, chute).scale.set(1, 0.55, 1);
      add(G.dome(0.2), P.accent, 0, 0.02, 0, chute).scale.set(1, 0.7, 1);
      for (const [x, z] of [[1, 1], [1, -1], [-1, 1], [-1, -1]]) {
        const line = add(G.cyl(0.004, 0.004, 0.68, 3), P.dark, x * 0.2, -0.3, z * 0.2, chute);
        line.rotation.set(z * 0.45, 0, -x * 0.45);
      }
      rig.chute = chute;
      rig.hover = 0.04;
      return rig;
    }
    const beast = mount(P, look.mount);
    const rider = humanoid(P, look, { seated: true, weapon: weaponFor(army, "n"), shield: army === "knights", shieldMat: P.accent });
    rider.body.scale.setScalar(0.8);
    rider.body.position.set(0, beast.saddleY - 0.26, -0.04);
    beast.group.add(rider.body);
    rider.armR.rotation.x = -1.35;
    return { ...rider, body: beast.group, rider: rider.body, legs: beast.legs, wings: beast.group.userData.wings };
  }

  function buildRook(P, look, army) {
    const body = new THREE.Group();
    const rig = { body, legs: [] };
    switch (look.rook) {
      case "tower": {
        add(G.cyl(0.27, 0.3, 0.72, 12), P.stone, 0, 0.36, 0, body);
        add(G.cyl(0.33, 0.3, 0.12, 12), P.stone, 0, 0.78, 0, body);
        for (let i = 0; i < 6; i += 1) {
          const a = (i / 6) * Math.PI * 2;
          add(G.box(0.12, 0.1, 0.1), P.stone, Math.sin(a) * 0.28, 0.89, Math.cos(a) * 0.28, body).rotation.y = a;
        }
        add(G.box(0.14, 0.22, 0.02), P.dark, 0, 0.12, 0.29, body);
        add(G.box(0.06, 0.1, 0.02), P.glow, 0, 0.55, 0.28, body);
        add(G.cyl(0.01, 0.01, 0.4, 4), P.dark, 0, 1.0, 0, body);
        add(G.box(0.02, 0.12, 0.2), P.accent, 0, 1.12, 0.1, body);
        break;
      }
      case "crypt": {
        add(G.box(0.48, 0.5, 0.32), P.stone, 0, 0.6, 0, body);
        add(G.box(0.3, 0.06, 0.02), P.glow, 0, 0.72, 0.165, body);
        add(G.box(0.2, 0.18, 0.2), P.stone, 0, 0.94, 0.02, body);
        for (const s of [-1, 1]) add(G.sphere(0.025, 6, 4), P.glow, 0.05 * s, 0.96, 0.12, body);
        for (const s of [-1, 1]) {
          const leg = pivot(0.13 * s, 0.36, 0, body);
          add(G.box(0.16, 0.36, 0.2), P.stone, 0, -0.18, 0, leg);
          rig.legs.push(leg);
          const arm = pivot(0.32 * s, 0.8, 0, body);
          add(G.box(0.14, 0.42, 0.16), P.stone, 0, -0.22, 0, arm);
          add(G.box(0.18, 0.16, 0.18), P.stone, 0, -0.46, 0.02, arm);
          if (s < 0) rig.armR = arm; else rig.armL = arm;
        }
        rig.tip = pivot(0, 0.4, 0.3, body);
        break;
      }
      case "tank": {
        add(G.box(0.5, 0.18, 0.66), P.armor, 0, 0.2, 0, body);
        for (const s of [-1, 1]) add(G.box(0.12, 0.2, 0.72), P.dark, 0.3 * s, 0.13, 0, body);
        const turret = pivot(0, 0.36, -0.04, body);
        add(G.box(0.32, 0.15, 0.34), P.armor, 0, 0, 0, turret);
        add(G.cyl(0.035, 0.035, 0.5, 8), P.dark, 0, 0.01, 0.4, turret).rotation.x = Math.PI / 2;
        add(G.box(0.06, 0.06, 0.01), P.accent, 0, 0.03, 0.172, turret);
        rig.tip = pivot(0, 0.01, 0.66, turret);
        rig.turret = turret;
        break;
      }
      case "gate": {
        for (const s of [-1, 1]) add(G.cyl(0.06, 0.08, 0.72, 8), P.gold, 0.32 * s, 0.36, 0, body);
        const colors = [0xff5050, 0xffa040, 0xfff060, 0x60e070, 0x50a0ff, 0xa070ff];
        colors.forEach((hex, i) => add(G.torus(0.34 - i * 0.035, 0.02, Math.PI), std(hex, { emissive: hex, emissiveIntensity: 0.9 }), 0, 0.72, 0, body));
        add(G.sphere(0.07), P.glow, 0, 0.72, 0, body);
        rig.tip = pivot(0, 0.72, 0.1, body);
        break;
      }
      case "longship": {
        add(G.box(0.3, 0.16, 0.7), P.wood, 0, 0.16, 0, body);
        add(G.box(0.34, 0.06, 0.74), P.accent, 0, 0.25, 0, body);
        const prow = add(G.box(0.08, 0.4, 0.08), P.wood, 0, 0.38, 0.38, body);
        prow.rotation.x = 0.4;
        add(G.box(0.1, 0.1, 0.16), P.accent, 0, 0.58, 0.48, body);
        add(G.sphere(0.02, 6, 4), P.glow, 0, 0.6, 0.56, body);
        add(G.cyl(0.02, 0.02, 0.85, 6), P.wood, 0, 0.6, 0, body);
        for (let i = 0; i < 3; i += 1) add(G.box(0.56, 0.13, 0.02), i % 2 ? P.accent : P.cloth, 0, 0.82 - i * 0.13, 0.02, body);
        for (let i = 0; i < 3; i += 1) for (const s of [-1, 1]) {
          add(G.cyl(0.08, 0.08, 0.02, 10), i % 2 ? P.accent : P.cloth, 0.17 * s, 0.22, -0.2 + i * 0.2, body).rotation.z = Math.PI / 2;
        }
        rig.tip = pivot(0, 0.5, 0.5, body);
        break;
      }
      case "torii": {
        for (const s of [-1, 1]) {
          add(G.cyl(0.05, 0.06, 0.82, 8), P.cloth, 0.28 * s, 0.41, 0, body);
          add(G.box(0.14, 0.08, 0.14), P.dark, 0.28 * s, 0.04, 0, body);
        }
        add(G.box(0.86, 0.07, 0.12), P.dark, 0, 0.86, 0, body);
        add(G.box(0.7, 0.05, 0.08), P.cloth, 0, 0.7, 0, body);
        add(G.box(0.12, 0.12, 0.03), P.gold, 0, 0.78, 0.04, body);
        add(G.sphere(0.06), P.glow, 0, 0.42, 0, body);
        rig.tip = pivot(0, 0.42, 0.1, body);
        break;
      }
      case "wall": {
        add(G.box(0.74, 0.08, 0.32), P.stone, 0, 0.04, 0, body);
        for (const x of [-0.24, 0, 0.24]) {
          const neck = pivot(x, 0.5, -0.02, body);
          head(P, "crested", neck);
          add(G.box(0.16, 0.42, 0.14), P.armor, x, 0.28, -0.04, body);
          const spear = add(G.cyl(0.012, 0.012, 0.9, 4), P.wood, x + 0.06, 0.55, 0.12, body);
          spear.rotation.x = 0.7;
          add(G.cone(0.03, 0.1, 4), P.metal, x + 0.06, 0.88, 0.47, body).rotation.x = 0.7;
          add(G.cyl(0.15, 0.15, 0.03, 12), P.armor, x, 0.34, 0.1, body).rotation.x = Math.PI / 2;
        }
        rig.tip = pivot(0, 0.6, 0.45, body);
        break;
      }
      case "mech": {
        const mech = humanoid(P, look, { weapon: "blaster", head: "visor" });
        mech.body.scale.setScalar(1.3);
        for (const s of [-1, 1]) add(G.box(0.14, 0.12, 0.18), P.dark, 0.17 * s, 0.75, -0.06, mech.body);
        return mech;
      }
    }
    return rig;
  }

  function attackStyle(army, type) {
    const look = ARMIES[army].look;
    if (type === "n") return look.mount === "parachute" ? "slam" : "charge";
    if (type === "b") return look.bishop === "caster" ? "spell" : "shoot";
    if (type === "r") return look.rook === "tank" || look.rook === "mech" ? "shoot" : look.rook === "gate" || look.rook === "torii" ? "beam" : "slam";
    if (type === "q") {
      if (look.queen === "ufo") return "beam";
      if (look.queen === "wraith") return "spell";
      if (army === "asgardians") return "lightning";
    }
    const held = type === "q" && army === "asgardians" ? "hammer" : weaponFor(army, type);
    if (held === "rifle" || held === "blaster") return "shoot";
    return held === "spear" ? "thrust" : "strike";
  }

  const BUILDERS = { p: buildPawn, k: buildKing, q: buildQueen, b: buildBishop, n: buildKnight, r: buildRook };

  function buildUnit(army, type, color) {
    const P = palette(army, color);
    const look = ARMIES[army].look;
    const unit = new THREE.Group();
    add(G.cyl(0.38, 0.42, 0.07, 24), P.base, 0, 0.035, 0, unit);
    add(G.torus(0.39, 0.018), P.rim, 0, 0.07, 0, unit).rotation.x = Math.PI / 2;
    const actor = pivot(0, 0.07, 0, unit);
    const rig = BUILDERS[type](P, look, army);
    actor.add(rig.body);
    rig.actor = actor;
    rig.baseY = rig.body.position.y;
    const bounds = new THREE.Box3().setFromObject(unit);
    unit.userData = { army, type, color, rig, style: attackStyle(army, type), height: bounds.max.y };
    return unit;
  }

  return { buildUnit, attackStyle };
})();
