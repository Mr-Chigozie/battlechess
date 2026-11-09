"use strict";

// Fortifications and territory. Purely for show: none of this touches the rules of chess.
//  - Squares an army has stood on are its territory (a faint tint in its colour) until the enemy steps on them.
//  - Pawns dig in the moment they arrive; other units dig in after holding a square for two of their turns.
//    Hold longer and the defences grow. Each army builds its own kind (sandbags, stakes, shield walls...).
//  - A unit cut down behind its defences blows them apart and leaves a crater for the rest of the battle.
const Fort = (() => {
  const V3 = THREE.Vector3;
  const coarse = window.matchMedia("(pointer: coarse)").matches;
  const STYLE = {
    marines: "sandbags", knights: "stakes", undead: "bones", vikings: "shields",
    spartans: "stones", samurai: "bamboo", machines: "steel", asgardians: "runes"
  };
  const MAX_ABANDONED = 12;  // empty emplacements left behind; the oldest sink away
  const MAX_CRATERS = 20;

  let scene = null;
  let root = null;
  let armies = { w: "knights", b: "undead" };
  let cells = new Map();      // square index -> { color, army, level, occupied, stay, group, leftAt }
  let owner = Array(64).fill(null);
  let tints = [];
  let tintMats = {};
  let craters = [];
  let clock = 0;              // counts moves, to find the oldest abandoned emplacement

  const idx = sq => sq.row * 8 + sq.col;
  // An army's own two starting rows are its camp: nobody digs in there, only on ground they've advanced onto.
  const inCamp = (row, color) => (color === "w" ? row >= 6 : row <= 1);
  const world = (row, col) => new V3(col - 3.5, 0, row - 3.5);

  // ---------------------------------------------------------------- shared parts
  const geoCache = {};
  const matCache = {};
  const geo = (name, make) => geoCache[name] || (geoCache[name] = make());
  const mat = (color, extra = {}) => {
    const k = `${color}:${JSON.stringify(extra)}`;
    return matCache[k] || (matCache[k] = new THREE.MeshStandardMaterial({ color, roughness: 0.9, flatShading: true, ...extra }));
  };

  // Places `count` parts along the front arc of a square, facing the enemy (`facing` = -1 for Ivory, +1 for Onyx).
  function arc(group, count, spread, radius, facing, place) {
    for (let i = 0; i < count; i += 1) {
      const a = count === 1 ? 0 : -spread + (2 * spread * i) / (count - 1);
      const radial = new V3(Math.sin(a), 0, Math.cos(a) * facing);
      const pos = radial.clone().multiplyScalar(radius);
      const rotY = Math.atan2(radial.x, radial.z);
      place(pos, rotY, i, a);
    }
  }

  function part(group, geometry, material, pos, rotY = 0, extra = null) {
    const mesh = new THREE.Mesh(geometry, material);
    mesh.position.copy(pos);
    mesh.rotation.y = rotY;
    if (extra) extra(mesh);
    mesh.castShadow = !coarse;
    group.add(mesh);
    return mesh;
  }

  const jitter = (s = 0.02) => (Math.random() - 0.5) * s;

  // ---------------------------------------------------------------- one builder per army
  const BUILD = {
    sandbags(g, level, f) {
      const bag = geo("bag", () => new THREE.BoxGeometry(0.18, 0.075, 0.1));
      const tan = mat(0xb8a47a), tan2 = mat(0xa8936a);
      arc(g, 6, 1.25, 0.4, f, (p, r, i) => part(g, bag, i % 2 ? tan : tan2, p.setY(0.038), r + jitter(0.2)));
      if (level > 1) {
        arc(g, 5, 1.0, 0.4, f, (p, r, i) => part(g, bag, i % 2 ? tan2 : tan, p.setY(0.11), r + jitter(0.2)));
        const coil = geo("wire", () => new THREE.TorusGeometry(0.05, 0.006, 4, 10));
        arc(g, 7, 1.1, 0.47, f, (p, r) => part(g, coil, mat(0x3a3a3a, { metalness: 0.6 }), p.setY(0.2), r, m => { m.rotation.x = Math.PI / 2; m.rotation.z = Math.PI / 2; }));
      }
    },
    stakes(g, level, f) {
      const stake = geo("stake", () => new THREE.ConeGeometry(0.03, 0.34, 5));
      const wood = mat(0x7a5532), log = geo("log", () => new THREE.CylinderGeometry(0.035, 0.035, 0.5, 6));
      part(g, log, mat(0x5e4026), new V3(0, 0.035, 0.38 * f), 0, m => { m.rotation.z = Math.PI / 2; });
      arc(g, level > 1 ? 7 : 4, level > 1 ? 1.2 : 0.7, 0.42, f, (p, r) => part(g, stake, wood, p.setY(0.12), r, m => { m.rotation.x = -0.65; }));
      if (level > 1) part(g, log, mat(0x5e4026), new V3(0, 0.09, 0.38 * f), 0, m => { m.rotation.z = Math.PI / 2; });
    },
    bones(g, level, f) {
      const stone = geo("tomb", () => new THREE.BoxGeometry(0.13, 0.17, 0.04));
      const bone = geo("bone", () => new THREE.CylinderGeometry(0.012, 0.012, 0.2, 4));
      const grey = mat(0x8c8a80), ivory = mat(0xe9e2c8);
      arc(g, level > 1 ? 4 : 2, level > 1 ? 1.1 : 0.6, 0.41, f, (p, r) => part(g, stone, grey, p.setY(0.085), r, m => { m.rotation.z = jitter(0.3); }));
      arc(g, level > 1 ? 6 : 3, 1.2, 0.36, f, (p, r) => part(g, bone, ivory, p.setY(0.02), r + 0.7, m => { m.rotation.z = Math.PI / 2; }));
      if (level > 1) part(g, geo("skull", () => new THREE.SphereGeometry(0.045, 6, 5)), ivory, new V3(0.12, 0.2, 0.41 * f));
    },
    shields(g, level, f, look) {
      const disc = geo("shield", () => new THREE.CylinderGeometry(0.1, 0.1, 0.02, 10));
      const boss = geo("boss", () => new THREE.SphereGeometry(0.025, 6, 4));
      const a = mat(look.cloth), b = mat(look.accent), metal = mat(0x9a9a9a, { metalness: 0.6 });
      const row = (n, spread, y) => arc(g, n, spread, 0.42, f, (p, r, i) => {
        part(g, disc, i % 2 ? a : b, p.clone().setY(y), r, m => { m.rotation.x = Math.PI / 2; });
        part(g, boss, metal, p.clone().multiplyScalar(1.05).setY(y), r);
      });
      row(level > 1 ? 6 : 4, level > 1 ? 1.2 : 0.8, 0.1);
      if (level > 1) row(4, 0.8, 0.24);
    },
    stones(g, level, f) {
      const stone = geo("stone", () => new THREE.DodecahedronGeometry(0.07));
      const s1 = mat(0xa89a80), s2 = mat(0x8e826c);
      const course = (n, spread, y) => arc(g, n, spread, 0.4, f, (p, r, i) => part(g, stone, i % 2 ? s1 : s2, p.setY(y), r, m => {
        m.scale.set(1.3, 0.75, 0.9);
        m.rotation.x = jitter(0.6);
      }));
      course(6, 1.2, 0.05);
      if (level > 1) { course(5, 1.0, 0.14); course(3, 0.6, 0.23); }
    },
    bamboo(g, level, f) {
      const cane = geo("cane", () => new THREE.CylinderGeometry(0.016, 0.016, 0.3, 5));
      const green = mat(0xb5a45a), dark = mat(0x8d8040);
      arc(g, level > 1 ? 13 : 7, level > 1 ? 1.2 : 0.75, 0.42, f, (p, r, i) => part(g, cane, i % 3 ? green : dark, p.setY(0.15 + jitter(0.06)), r));
      const tie = geo(`tie${level}`, () => new THREE.TorusGeometry(0.42, 0.008, 3, 24, (level > 1 ? 2.4 : 1.5)));
      part(g, tie, mat(0x5e4026), new V3(0, 0.2, 0), 0, m => {
        m.rotation.x = -Math.PI / 2;
        m.rotation.z = (f > 0 ? Math.PI / 2 : -Math.PI / 2) - (level > 1 ? 1.2 : 0.75);
      });
    },
    steel(g, level, f) {
      const bar = geo("bar", () => new THREE.BoxGeometry(0.03, 0.03, 0.26));
      const steel = mat(0x4a5058, { metalness: 0.7, roughness: 0.4 });
      const hedgehog = pos => {
        for (const [x, y] of [[0.6, 0], [-0.6, 1.2], [0.6, 2.4]]) part(g, bar, steel, pos.clone().setY(0.09), y, m => { m.rotation.x = x; });
      };
      arc(g, level > 1 ? 3 : 2, level > 1 ? 1.0 : 0.6, 0.38, f, p => hedgehog(p));
      if (level > 1) {
        const plate = geo("plate", () => new THREE.BoxGeometry(0.22, 0.14, 0.02));
        arc(g, 2, 1.2, 0.44, f, (p, r) => part(g, plate, mat(0x5b626b, { metalness: 0.6 }), p.setY(0.07), r));
      }
    },
    runes(g, level, f, look) {
      const stone = geo("menhir", () => new THREE.BoxGeometry(0.06, 0.2, 0.05));
      const glow = new THREE.MeshStandardMaterial({ color: 0x4a4790, emissive: look.magic, emissiveIntensity: 0.6, flatShading: true });
      arc(g, level > 1 ? 5 : 3, level > 1 ? 1.1 : 0.7, 0.41, f, (p, r) => part(g, stone, glow, p.setY(0.1), r));
      const ring = geo(`rune${level}`, () => new THREE.TorusGeometry(0.4, 0.012, 3, 28, level > 1 ? Math.PI * 2 : Math.PI));
      part(g, ring, FX.glowMat(look.magic, 0.55), new V3(0, 0.01, 0), 0, m => {
        m.rotation.x = -Math.PI / 2;
        m.rotation.z = f > 0 ? 0 : Math.PI;
      });
    }
  };

  function build(cell, sq, animate) {
    const army = ARMIES[cell.army] ? cell.army : "marines";
    const group = new THREE.Group();
    BUILD[STYLE[army] || "sandbags"](group, cell.level, cell.color === "w" ? -1 : 1, ARMIES[army].look);
    group.position.copy(world(sq.row, sq.col));
    root.add(group);
    if (animate) {
      group.scale.set(1, 0.01, 1);
      FX.tween(0.45, t => group.scale.set(1, Math.max(0.01, t), 1), FX.ease.back);
      FX.dust(group.position.clone().setY(0.05), 5);
    }
    return group;
  }

  function setLevel(cell, sq, level, animate = true) {
    if (level <= cell.level) return;
    cell.level = level;
    if (cell.group) root.remove(cell.group);
    cell.group = build(cell, sq, animate);
  }

  function sink(cell) {
    const group = cell.group;
    cell.group = null;
    if (!group) return;
    FX.tween(0.8, t => { group.position.y = -0.3 * t; }).then(() => root.remove(group));
  }

  // ---------------------------------------------------------------- territory
  function claim(sq, color) {
    const i = idx(sq);
    if (owner[i] === color) return;
    owner[i] = color;
    const tint = tints[i];
    tint.material = tintMats[color];
    tint.visible = true;
    tint.scale.setScalar(0.2);
    FX.tween(0.35, t => tint.scale.setScalar(0.2 + 0.8 * t), FX.ease.out);
  }

  function setTints() {
    const a = new THREE.Color(ARMIES[armies.w]?.look.magic ?? 0xffffff);
    const b = new THREE.Color(ARMIES[armies.b]?.look.magic ?? 0x000000);
    // Two armies with nearly the same colour: fall back to ivory and onyx so the sides stay readable.
    const close = Math.abs(a.r - b.r) + Math.abs(a.g - b.g) + Math.abs(a.b - b.b) < 0.45;
    const make = (color, opacity) => new THREE.MeshBasicMaterial({ color, transparent: true, opacity, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -1 });
    tintMats = close ? { w: make(0xfff1cc, 0.16), b: make(0x14101c, 0.3) } : { w: make(a, 0.13), b: make(b, 0.13) };
  }

  // ---------------------------------------------------------------- craters
  function crater(sq) {
    const group = new THREE.Group();
    const pit = new THREE.Mesh(geo("pit", () => new THREE.CircleGeometry(0.34, 16)),
      new THREE.MeshStandardMaterial({ color: 0x2a2218, roughness: 1, transparent: true, opacity: 0.6, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -2 }));
    pit.rotation.x = -Math.PI / 2;
    pit.position.y = 0.004;
    const rim = new THREE.Mesh(geo("rim", () => new THREE.TorusGeometry(0.34, 0.04, 4, 16)), mat(0x5a4a36));
    rim.rotation.x = Math.PI / 2;
    rim.scale.z = 0.5;
    group.add(pit, rim);
    group.position.copy(world(sq.row, sq.col)).add(new V3(jitter(0.12), 0, jitter(0.12)));
    group.rotation.y = Math.random() * Math.PI;
    root.add(group);
    craters.push(group);
    if (craters.length > MAX_CRATERS) root.remove(craters.shift());
  }

  // ---------------------------------------------------------------- what the stage tells us
  function init(sceneRef) {
    scene = sceneRef;
    root = new THREE.Group();
    scene.add(root);
    const plane = new THREE.PlaneGeometry(0.98, 0.98);
    for (let i = 0; i < 64; i += 1) {
      const tint = new THREE.Mesh(plane, new THREE.MeshBasicMaterial({ visible: false }));
      tint.rotation.x = -Math.PI / 2;
      tint.position.copy(world(Math.floor(i / 8), i % 8)).setY(0.003);
      tint.visible = false;
      tint.renderOrder = -1;
      scene.add(tint);
      tints.push(tint);
    }
  }

  // A new battle or a jump to a position (undo, reconnect): no animation, defences only for units
  // that have clearly left their starting squares.
  function reset(pos, armyIds) {
    armies = { ...armyIds };
    setTints();
    root.clear();
    cells = new Map();
    craters = [];
    owner = Array(64).fill(null);
    for (const tint of tints) tint.visible = false;
    for (let row = 0; row < 8; row += 1) {
      for (let col = 0; col < 8; col += 1) {
        const piece = pos.board[row][col];
        if (!piece) continue;
        const sq = { row, col };
        const i = idx(sq);
        owner[i] = piece.color;
        tints[i].material = tintMats[piece.color];
        tints[i].visible = true;
        tints[i].scale.setScalar(1);
        const cell = { color: piece.color, army: armies[piece.color], level: 0, occupied: true, stay: 0, group: null, leftAt: 0, early: piece.type === "p" };
        cells.set(i, cell);
        if (!inCamp(row, piece.color)) setLevel(cell, sq, 1, false);
      }
    }
  }

  function leave(sq) {
    const i = idx(sq);
    const cell = cells.get(i);
    if (!cell) return;
    cell.occupied = false;
    cell.leftAt = clock;
    if (!cell.group) { cells.delete(i); return; }
    const empty = [...cells.values()].filter(c => !c.occupied && c.group).sort((a, b) => a.leftAt - b.leftAt);
    while (empty.length > MAX_ABANDONED) {
      const old = empty.shift();
      sink(old);
      for (const [k, c] of cells) if (c === old) cells.delete(k);
    }
  }

  function arrive(unit, sq) {
    const i = idx(sq);
    const color = unit.userData.color;
    let cell = cells.get(i);
    if (cell && cell.color !== color) {
      // Marching onto an enemy emplacement kicks it over.
      if (cell.group) FX.shatter(cell.group, new V3(0, 0, color === "w" ? -1 : 1), 0.45);
      cell = null;
    }
    if (cell) {
      cell.occupied = true;
      cell.stay = 0;
    } else {
      cell = { color, army: armies[color], level: 0, occupied: true, stay: 0, group: null, leftAt: 0 };
      cells.set(i, cell);
      if (unit.userData.type === "p" && !inCamp(sq.row, color)) {
        cell.early = true;
        setLevel(cell, sq, 1);
      }
    }
    claim(sq, color);
  }

  // A unit is struck down on `sq`: its defences blow apart and leave a crater.
  function hit(sq, dir) {
    const i = idx(sq);
    const cell = cells.get(i);
    if (!cell) return;
    cells.delete(i);
    if (!cell.group) return;
    FX.shatter(cell.group, dir || new V3(), 0.8);
    FX.burst(world(sq.row, sq.col).setY(0.15), 0xc9b48a, 18, 2.2, 1, 0.7);
    crater(sq);
  }

  // After a side's move: everyone of that side who held their ground digs in a little deeper.
  function turnDone(color, moved) {
    clock += 1;
    const movedSet = new Set(moved.map(idx));
    for (const [i, cell] of cells) {
      if (!cell.occupied || cell.color !== color || movedSet.has(i) || inCamp(Math.floor(i / 8), color)) continue;
      cell.stay += 1;
      // Pawns dug in on arrival and finish the job after two turns; the rest need two turns to start and four to finish.
      const target = cell.early ? (cell.stay >= 2 ? 2 : 1) : cell.stay >= 4 ? 2 : cell.stay >= 2 ? 1 : 0;
      setLevel(cell, { row: Math.floor(i / 8), col: i % 8 }, target);
    }
  }

  function share() {
    const w = owner.filter(c => c === "w").length;
    const b = owner.filter(c => c === "b").length;
    return { w: Math.round((w / 64) * 100), b: Math.round((b / 64) * 100) };
  }

  return { init, reset, leave, arrive, hit, turnDone, share };
})();
