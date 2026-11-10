"use strict";

// Fortifications and territory. Purely for show: none of this touches the rules of chess.
//  - Squares an army has stood on are its territory (a faint tint in its colour) until the enemy steps on them.
//  - Pawns dig in the moment they advance; other units dig in after holding a square for two of their turns,
//    and the sandbag walls grow higher by turn four. Nobody digs in inside their own starting rows.
//  - A fort is a sandbag wall on three sides of the square (front and both flanks, open to the rear)
//    with a little flag in the owner's colour. It stays as a territory marker after its unit moves on.
//  - Slay the unit holding a fort, or march onto an empty one, and the fort is taken over: it turns to face
//    the other way and flies the new owner's flag.
const Fort = (() => {
  const V3 = THREE.Vector3;
  const coarse = window.matchMedia("(pointer: coarse)").matches;

  // One sandbag, about 19 x 6 x 10.5 cm on a 1 m square: a squashed, pillowy box.
  const BAG = { len: 0.19, high: 0.062, deep: 0.105 };
  const COURSE = 0.052;      // height of one layer, bags settle into the one below
  const WALL = 0.43;         // distance from the square's centre to the middle of a wall
  const SAND = [0xc2b48f, 0xb8a983, 0xcbbd98, 0xae9f7a, 0xbfae86];

  let scene = null;
  let root = null;
  let armies = { w: "knights", b: "undead" };
  let cells = new Map();      // square index -> { color, army, level, occupied, stay, early, group }
  let owner = Array(64).fill(null);
  let tints = [];
  let tintMats = {};

  const idx = sq => sq.row * 8 + sq.col;
  const world = (row, col) => new V3(col - 3.5, 0, row - 3.5);
  // An army's own two starting rows are its camp: nobody digs in there, only on ground they've advanced onto.
  const inCamp = (row, color) => (color === "w" ? row >= 6 : row <= 1);
  const rand = (a, b) => a + Math.random() * (b - a);

  // ---------------------------------------------------------------- the sandbag
  // A low-poly sphere pushed out towards a box (a "superellipsoid"): flat-ish faces, soft rounded edges.
  const bagGeo = (() => {
    const g = new THREE.SphereGeometry(1, 10, 6);
    const p = g.attributes.position;
    for (let i = 0; i < p.count; i += 1) {
      const shape = (v, e) => Math.sign(v) * Math.pow(Math.abs(v), e);
      const x = shape(p.getX(i), 0.45), y = shape(p.getY(i), 0.7), z = shape(p.getZ(i), 0.5);
      // Bags bulge in the middle and pinch at the tied ends.
      const pinch = 1 - 0.18 * Math.pow(Math.abs(x), 6);
      p.setXYZ(i, x * BAG.len / 2, y * BAG.high / 2 * pinch, z * BAG.deep / 2 * pinch);
    }
    g.computeVertexNormals();
    return g;
  })();
  const bagMat = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 1, metalness: 0 });

  // Merges many transformed copies of the bag into one mesh: one draw call per fort, cheap on phones.
  function mergeBags(placements) {
    const base = bagGeo;
    const per = base.attributes.position.count;
    const positions = new Float32Array(per * 3 * placements.length);
    const normals = new Float32Array(per * 3 * placements.length);
    const colors = new Float32Array(per * 3 * placements.length);
    const index = [];
    const v = new V3(), n = new V3(), c = new THREE.Color();
    const normalMatrix = new THREE.Matrix3();
    placements.forEach(({ matrix, color }, k) => {
      normalMatrix.getNormalMatrix(matrix);
      c.setHex(color);
      for (let i = 0; i < per; i += 1) {
        v.fromBufferAttribute(base.attributes.position, i).applyMatrix4(matrix);
        n.fromBufferAttribute(base.attributes.normal, i).applyMatrix3(normalMatrix).normalize();
        const o = (k * per + i) * 3;
        positions.set([v.x, v.y, v.z], o);
        normals.set([n.x, n.y, n.z], o);
        // A little shading towards the bottom of each bag, like the creases in the sacking.
        const shade = 0.82 + 0.18 * Math.min(1, Math.max(0, (base.attributes.position.getY(i) / BAG.high) + 0.5));
        colors.set([c.r * shade, c.g * shade, c.b * shade], o);
      }
      for (let i = 0; i < base.index.count; i += 1) index.push(base.index.getX(i) + k * per);
    });
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.BufferAttribute(positions, 3));
    g.setAttribute("normal", new THREE.BufferAttribute(normals, 3));
    g.setAttribute("color", new THREE.BufferAttribute(colors, 3));
    g.setIndex(index);
    return g;
  }

  // Lays one wall of bags in staggered courses (like bricks), from `start` to `end` along the ground.
  function wall(placements, start, end, courses) {
    const along = end.clone().sub(start);
    const length = along.length();
    along.normalize();
    const yaw = Math.atan2(-along.z, along.x);
    for (let k = 0; k < courses; k += 1) {
      const offset = (k % 2) * BAG.len * 0.5;
      // Each course is a little shorter at the ends, so the wall steps down like a real stack.
      const inset = k * 0.02;
      for (let s = -offset; s < length; s += BAG.len * 0.93) {
        const mid = s + BAG.len / 2;
        if (mid < inset || mid > length - inset) continue;
        const pos = start.clone().addScaledVector(along, Math.min(mid, length - BAG.len * 0.35));
        pos.y = BAG.high / 2 + k * COURSE;
        const m = new THREE.Matrix4().compose(
          pos.add(new V3(rand(-0.008, 0.008), 0, rand(-0.008, 0.008))),
          new THREE.Quaternion().setFromEuler(new THREE.Euler(rand(-0.06, 0.06), yaw + rand(-0.09, 0.09), rand(-0.05, 0.05))),
          new V3(rand(0.94, 1.06), rand(0.9, 1.08), rand(0.95, 1.05))
        );
        placements.push({ matrix: m, color: SAND[Math.floor(Math.random() * SAND.length)] });
      }
    }
  }

  // The fort for one square. `facing` is the enemy's direction along z: -1 for Ivory, +1 for Onyx.
  function build(cell, sq, animate) {
    const facing = cell.color === "w" ? -1 : 1;
    const courses = cell.level > 1 ? 4 : 2;
    const front = WALL * facing;
    const placements = [];
    wall(placements, new V3(-0.47, 0, front), new V3(0.47, 0, front), courses);                       // facing the enemy
    for (const side of [-1, 1]) {
      // the flanks run from just behind the front wall back towards the rear, one course lower
      wall(placements, new V3(WALL * side, 0, front - facing * BAG.deep), new V3(WALL * side, 0, -0.12 * facing), Math.max(1, courses - 1));
    }
    const group = new THREE.Group();
    const bags = new THREE.Mesh(mergeBags(placements), bagMat);
    bags.castShadow = !coarse;
    bags.receiveShadow = true;
    group.add(bags);

    // A small pennant in the owner's colour at the front corner.
    const look = ARMIES[cell.army]?.look || ARMIES.marines.look;
    const top = BAG.high + (courses - 1) * COURSE;
    const pole = new THREE.Mesh(poleGeo, poleMat);
    const corner = -WALL * 0.9 * facing;  // the flag hangs inwards, over the fort
    pole.position.set(corner, top + 0.17, front);
    const flag = new THREE.Mesh(flagGeo, new THREE.MeshStandardMaterial({ color: look.magic, emissive: look.magic, emissiveIntensity: 0.25, side: THREE.DoubleSide, roughness: 0.8 }));
    flag.position.set(corner, top + 0.3, front);
    flag.rotation.y = facing > 0 ? Math.PI : 0;
    flag.userData.yaw = flag.rotation.y;
    flag.userData.wave = Math.random() * 6;
    group.add(pole, flag);
    group.userData.flag = flag;

    group.position.copy(world(sq.row, sq.col));
    root.add(group);
    if (animate) {
      group.scale.set(1, 0.01, 1);
      FX.tween(0.45, t => group.scale.set(1, Math.max(0.01, t), 1), FX.ease.back);
      FX.dust(group.position.clone().setY(0.05), 5);
    }
    return group;
  }
  const poleGeo = new THREE.CylinderGeometry(0.007, 0.007, 0.34, 5);
  const poleMat = new THREE.MeshStandardMaterial({ color: 0x5e4026, roughness: 0.9 });
  const flagGeo = (() => {
    const shape = new THREE.Shape();
    shape.moveTo(0, 0.06);
    shape.lineTo(-0.15, 0);
    shape.lineTo(0, -0.06);
    return new THREE.ShapeGeometry(shape);
  })();

  function remove(group, sinkAway = false) {
    if (!group) return;
    const done = () => {
      root.remove(group);
      group.children[0].geometry.dispose();
      group.userData.flag.material.dispose();
    };
    if (!sinkAway) return done();
    FX.tween(0.35, t => group.scale.set(1, Math.max(0.01, 1 - t), 1)).then(done);
  }

  function setLevel(cell, sq, level, animate = true) {
    if (level <= cell.level) return;
    cell.level = level;
    remove(cell.group);
    cell.group = build(cell, sq, animate);
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

  // A new battle or a jump to a position (undo, reconnect): no animation, forts only for units
  // standing outside their own camp.
  function reset(pos, armyIds) {
    armies = { ...armyIds };
    setTints();
    for (const cell of cells.values()) remove(cell.group);
    root.clear();
    cells = new Map();
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
        const cell = { color: piece.color, army: armies[piece.color], level: 0, occupied: true, stay: 0, early: piece.type === "p", group: null };
        cells.set(i, cell);
        if (!inCamp(row, piece.color)) setLevel(cell, sq, 1, false);
      }
    }
  }

  // The unit moves on; its fort stays behind as a marker.
  function leave(sq) {
    const i = idx(sq);
    const cell = cells.get(i);
    if (!cell) return;
    cell.occupied = false;
    if (!cell.group) cells.delete(i);
  }

  function arrive(unit, sq) {
    const i = idx(sq);
    const color = unit.userData.color;
    const isPawn = unit.userData.type === "p";
    let cell = cells.get(i);
    if (cell && cell.color !== color) {
      if (cell.group) {
        // Taken over: the old fort comes down, the new owner's goes up facing the other way.
        const level = Math.max(1, cell.level);
        remove(cell.group, true);
        cell = { color, army: armies[color], level: 0, occupied: true, stay: 0, early: true, group: null };
        cells.set(i, cell);
        setLevel(cell, sq, level);
        FX.burst(world(sq.row, sq.col).setY(0.12), 0xc9b48a, 12, 1.6, 1, 0.6);
        claim(sq, color);
        return;
      }
      cell = null;
    }
    if (cell) {
      cell.occupied = true;
      cell.stay = 0;
    } else {
      cell = { color, army: armies[color], level: 0, occupied: true, stay: 0, early: isPawn, group: null };
      cells.set(i, cell);
      if (isPawn && !inCamp(sq.row, color)) setLevel(cell, sq, 1);
    }
    claim(sq, color);
  }

  // A unit is struck down on `sq`: sand sprays from its fort, which the victor takes over on arrival.
  function hit(sq, dir) {
    const cell = cells.get(idx(sq));
    if (!cell) return;
    cell.occupied = false;
    if (!cell.group) return;
    const at = world(sq.row, sq.col).setY(0.15);
    FX.burst(at, 0xc9b48a, 22, 2.4, 1.1, 0.8);
    FX.dust(at.setY(0.08), 8);
  }

  // After a side's move: everyone of that side who held their ground digs in a little deeper.
  function turnDone(color, moved) {
    const movedSet = new Set(moved.map(idx));
    for (const [i, cell] of cells) {
      if (!cell.occupied || cell.color !== color || movedSet.has(i) || inCamp(Math.floor(i / 8), color)) continue;
      cell.stay += 1;
      // Pawns dug in on arrival and finish the job after two turns; the rest need two turns to start and four to finish.
      const target = cell.early ? (cell.stay >= 2 ? 2 : 1) : cell.stay >= 4 ? 2 : cell.stay >= 2 ? 1 : 0;
      setLevel(cell, { row: Math.floor(i / 8), col: i % 8 }, target);
    }
  }

  // Flags flutter a little.
  function update(time) {
    for (const cell of cells.values()) {
      const flag = cell.group?.userData.flag;
      if (flag) flag.rotation.y = flag.userData.yaw + Math.sin(time * 3 + flag.userData.wave) * 0.35;
    }
  }

  function share() {
    const w = owner.filter(c => c === "w").length;
    const b = owner.filter(c => c === "b").length;
    return { w: Math.round((w / 64) * 100), b: Math.round((b / 64) * 100) };
  }

  return { init, reset, leave, arrive, hit, turnDone, update, share };
})();
