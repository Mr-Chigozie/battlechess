"use strict";

// Tension: as the battle heats up (captures, checks, combos) the sky reddens, embers rise,
// fires catch along the board's frame, and the squares where units fell begin to burn.
const War = (() => {
  const V3 = THREE.Vector3;
  const WAR_SKY = new THREE.Color(0x3a0f06);
  const FIRE_LIGHT = new THREE.Color(0xff8a3a);
  let scene, hemi, sun;
  let level = 0;
  let target = 0;
  let emberTimer = 0;
  const base = { sky: new THREE.Color(), fog: new THREE.Color(), hemi: new THREE.Color(), sun: new THREE.Color(0xfff1dc) };
  const fires = [];
  const scorches = [];

  const flameGeo = new THREE.ConeGeometry(1, 1, 7);
  flameGeo.translate(0, 0.5, 0);
  const scorchGeo = new THREE.CircleGeometry(1, 16);

  function makeFlame(size) {
    const g = new THREE.Group();
    const layers = [[0xff3a10, 1, 0.55], [0xff8a1a, 0.7, 0.7], [0xffe070, 0.4, 0.85]];
    for (const [color, scale, opacity] of layers) {
      const cone = new THREE.Mesh(flameGeo, FX.glowMat(color, opacity));
      cone.scale.set(0.16 * scale * size, 0.55 * scale * size, 0.16 * scale * size);
      g.add(cone);
    }
    g.visible = false;
    scene.add(g);
    return g;
  }

  function init(sceneRef, hemiLight, sunLight) {
    scene = sceneRef;
    hemi = hemiLight;
    sun = sunLight;
    // Fire spots ring the board's frame; each catches at its own tension threshold so the blaze spreads.
    let seed = 3;
    const rand = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
    for (let i = 0; i < 40; i += 1) {
      const side = i % 4;
      const along = -4.4 + (Math.floor(i / 4) / 9) * 8.8 + (rand() - 0.5) * 0.3;
      const edge = 4.5 + rand() * 0.1;
      const pos = [new V3(along, -0.03, -edge), new V3(edge, -0.03, along), new V3(along, -0.03, edge), new V3(-edge, -0.03, along)][side];
      fires.push({ group: makeFlame(0.8 + rand() * 0.6), pos, threshold: 0.35 + rand() * 0.6, phase: rand() * 10, heat: 0 });
      fires[fires.length - 1].group.position.copy(pos);
    }
  }

  function setBase(sky, fog, hemiColor) {
    base.sky.set(sky);
    base.fog.set(fog);
    base.hemi.set(hemiColor);
  }

  function setTension(value) {
    target = Math.max(0, Math.min(1, value));
  }

  // A unit fell here: leave a burn mark that ignites once the battle is hot enough.
  function ignite(sq) {
    const pos = new V3(sq.col - 3.5 + (Math.random() - 0.5) * 0.6, 0.002, sq.row - 3.5 + (Math.random() - 0.5) * 0.6);
    const mark = new THREE.Mesh(scorchGeo, new THREE.MeshStandardMaterial({ color: 0x14100c, transparent: true, opacity: 0, roughness: 1, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -1 }));
    mark.rotation.x = -Math.PI / 2;
    mark.position.copy(pos);
    mark.scale.setScalar(0.18 + Math.random() * 0.08);
    scene.add(mark);
    const flame = makeFlame(0.55);
    flame.position.copy(pos);
    scorches.push({ group: flame, mark, pos, threshold: 0.45, phase: Math.random() * 10, heat: 0 });
    // Only the most recent fields of battle keep burning.
    while (scorches.length > 10) {
      const old = scorches.shift();
      scene.remove(old.group, old.mark);
    }
  }

  function reset() {
    level = target = 0;
    for (const s of scorches) scene.remove(s.group, s.mark);
    scorches.length = 0;
    for (const f of fires) { f.heat = 0; f.group.visible = false; }
  }

  function burn(fire, dt, time) {
    const want = Math.max(0, Math.min(1, (level - fire.threshold) / 0.2));
    fire.heat += (want - fire.heat) * Math.min(1, dt * 1.5);
    fire.group.visible = fire.heat > 0.02;
    if (!fire.group.visible) return;
    const flicker = 0.75 + Math.sin(time * 13 + fire.phase) * 0.12 + Math.sin(time * 23 + fire.phase * 2) * 0.1;
    fire.group.scale.set(fire.heat, fire.heat * flicker, fire.heat);
    fire.group.rotation.y = time * 0.5 + fire.phase;
    if (Math.random() < dt * 3 * fire.heat) FX.ember(fire.pos.clone().setY(0.35 * fire.heat), 0xff8a2a, 1.3);
  }

  function update(dt, time) {
    level += (target - level) * Math.min(1, dt * 0.6);
    const heat = Math.max(0, level - 0.2) / 0.8;
    if (scene.background?.isColor) scene.background.copy(base.sky).lerp(WAR_SKY, heat * 0.65);
    if (scene.fog) scene.fog.color.copy(base.fog).lerp(WAR_SKY, heat * 0.55);
    hemi.color.copy(base.hemi).lerp(FIRE_LIGHT, heat * 0.4);
    sun.color.copy(base.sun).lerp(FIRE_LIGHT, heat * 0.5);
    for (const fire of fires) burn(fire, dt, time);
    for (const s of scorches) {
      s.mark.material.opacity = Math.min(0.75, s.mark.material.opacity + dt * 0.5);
      burn(s, dt, time);
    }
    emberTimer += dt * level * level * 30;
    while (emberTimer > 1) {
      emberTimer -= 1;
      FX.ember(new V3((Math.random() - 0.5) * 9, 0.05, (Math.random() - 0.5) * 9), Math.random() < 0.7 ? 0xff7a2a : 0xffd070, 1.1);
    }
  }

  return {
    init, setBase, setTension, ignite, reset, update,
    get level() { return level; }
  };
})();
