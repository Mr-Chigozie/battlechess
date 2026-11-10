"use strict";

// Territory. Purely for show: none of this touches the rules of chess.
// Squares an army has stood on are its territory (a faint tint in its colour) until the enemy steps on them.
// The stage reports every move here; the field report shows each side's share.
const Territory = (() => {
  const V3 = THREE.Vector3;
  let armies = { w: "knights", b: "undead" };
  let owner = Array(64).fill(null);
  let tints = [];
  let tintMats = {};

  const idx = sq => sq.row * 8 + sq.col;
  const world = (row, col) => new V3(col - 3.5, 0, row - 3.5);

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

  function init(scene) {
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

  // A new battle or a jump to a position (undo, reconnect): each side holds the squares its units stand on.
  function reset(pos, armyIds) {
    armies = { ...armyIds };
    setTints();
    owner = Array(64).fill(null);
    for (const tint of tints) tint.visible = false;
    for (let row = 0; row < 8; row += 1) {
      for (let col = 0; col < 8; col += 1) {
        const piece = pos.board[row][col];
        if (!piece) continue;
        const i = idx({ row, col });
        owner[i] = piece.color;
        tints[i].material = tintMats[piece.color];
        tints[i].visible = true;
        tints[i].scale.setScalar(1);
      }
    }
  }

  const arrive = (unit, sq) => claim(sq, unit.userData.color);

  function share() {
    const w = owner.filter(c => c === "w").length;
    const b = owner.filter(c => c === "b").length;
    return { w: Math.round((w / 64) * 100), b: Math.round((b / 64) * 100) };
  }

  return { init, reset, arrive, share };
})();
