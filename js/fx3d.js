"use strict";

// Tweens, particles, debris and synthesized sound for the 3D battle. Everything advances from FX.update(dt).
const FX = (() => {
  let scene = null;
  const tasks = new Set();
  const bits = [];
  let shakeAmp = 0;
  let speed = 1;

  const ease = {
    linear: t => t,
    inOut: t => t * t * (3 - 2 * t),
    out: t => 1 - (1 - t) ** 3,
    in: t => t ** 3,
    back: t => 1 + 2.4 * (t - 1) ** 3 + 1.4 * (t - 1) ** 2
  };

  function init(targetScene) {
    scene = targetScene;
  }

  // Calls fn(easedProgress) every frame for `seconds`, resolving when done.
  function tween(seconds, fn, easing = ease.inOut) {
    return new Promise(resolve => {
      tasks.add({ elapsed: 0, seconds: Math.max(seconds, 0.0001), fn, easing, resolve });
    });
  }

  const wait = seconds => tween(seconds, () => {});
  const lerp = (a, b, t) => a + (b - a) * t;

  function update(dt) {
    dt *= speed;
    for (const task of [...tasks]) {
      task.elapsed += dt;
      const k = Math.min(1, task.elapsed / task.seconds);
      task.fn(task.easing(k));
      if (k >= 1) {
        tasks.delete(task);
        task.resolve();
      }
    }
    for (let i = bits.length - 1; i >= 0; i -= 1) {
      const bit = bits[i];
      bit.age += dt;
      if (bit.gravity) bit.vel.y -= bit.gravity * dt;
      bit.mesh.position.addScaledVector(bit.vel, dt);
      if (bit.spin) {
        bit.mesh.rotation.x += bit.spin.x * dt;
        bit.mesh.rotation.y += bit.spin.y * dt;
        bit.mesh.rotation.z += bit.spin.z * dt;
      }
      if (bit.floor !== undefined && bit.mesh.position.y < bit.floor) {
        bit.mesh.position.y = bit.floor;
        bit.vel.y *= -0.35;
        bit.vel.x *= 0.6;
        bit.vel.z *= 0.6;
        if (bit.spin) bit.spin.multiplyScalar(0.6);
      }
      if (bit.grow) bit.mesh.scale.multiplyScalar(1 + bit.grow * dt);
      const fadeStart = bit.life - bit.fade;
      if (bit.age > fadeStart) bit.mesh.material.opacity = Math.max(0, 1 - (bit.age - fadeStart) / bit.fade) * bit.alpha;
      if (bit.age >= bit.life) {
        scene.remove(bit.mesh);
        bit.mesh.material.dispose();
        bits.splice(i, 1);
      }
    }
    shakeAmp = Math.max(0, shakeAmp - dt * 2.2);
  }

  function addBit(mesh, opts) {
    mesh.material.transparent = true;
    scene.add(mesh);
    bits.push({ mesh, age: 0, vel: opts.vel || new THREE.Vector3(), gravity: opts.gravity || 0, spin: opts.spin, floor: opts.floor,
      grow: opts.grow, life: opts.life || 1, fade: opts.fade || 0.4, alpha: mesh.material.opacity ?? 1 });
  }

  const glowMat = (color, opacity = 1) => new THREE.MeshBasicMaterial({ color, transparent: true, opacity, blending: THREE.AdditiveBlending, depthWrite: false });
  const sparkGeo = new THREE.BoxGeometry(0.04, 0.04, 0.04);
  const orbGeo = new THREE.SphereGeometry(1, 10, 8);
  const ringGeo = new THREE.TorusGeometry(1, 0.06, 6, 32);

  function burst(pos, color, count = 24, power = 3, size = 1, life = 0.7) {
    for (let i = 0; i < count; i += 1) {
      const mesh = new THREE.Mesh(sparkGeo, glowMat(color));
      mesh.position.copy(pos);
      mesh.scale.setScalar(size * (0.6 + Math.random()));
      const dir = new THREE.Vector3(Math.random() - 0.5, Math.random() * 0.8, Math.random() - 0.5).normalize();
      addBit(mesh, { vel: dir.multiplyScalar(power * (0.4 + Math.random())), gravity: 4, life: life * (0.6 + Math.random() * 0.6), fade: 0.3 });
    }
  }

  function dust(pos, count = 14) {
    for (let i = 0; i < count; i += 1) {
      const mesh = new THREE.Mesh(orbGeo, new THREE.MeshStandardMaterial({ color: 0xb9ad94, transparent: true, opacity: 0.45, roughness: 1, depthWrite: false }));
      mesh.position.copy(pos).add(new THREE.Vector3((Math.random() - 0.5) * 0.4, 0.05, (Math.random() - 0.5) * 0.4));
      mesh.scale.setScalar(0.06 + Math.random() * 0.05);
      const a = Math.random() * Math.PI * 2;
      addBit(mesh, { vel: new THREE.Vector3(Math.cos(a), 0.25 + Math.random() * 0.4, Math.sin(a)).multiplyScalar(0.9), grow: 1.2, life: 1.1, fade: 0.8 });
    }
  }

  function shockwave(pos, color) {
    const mesh = new THREE.Mesh(ringGeo, glowMat(color, 0.9));
    mesh.position.copy(pos);
    mesh.position.y = 0.12;
    mesh.rotation.x = Math.PI / 2;
    mesh.scale.setScalar(0.2);
    addBit(mesh, { grow: 4.5, life: 0.55, fade: 0.45 });
  }

  // Breaks a unit into its meshes and flings them outward, like a stone piece smashed apart.
  // Breaks a unit into its parts. `spread` and `up` scale how far they fly (low values: a heap on the spot);
  // `life` is how long the pieces lie there.
  function shatter(unit, impactDir = new THREE.Vector3(), force = 1, { spread = 1, up = 1, life = 2.4 } = {}) {
    unit.updateMatrixWorld(true);
    const center = new THREE.Vector3();
    new THREE.Box3().setFromObject(unit).getCenter(center);
    const meshes = [];
    unit.traverse(obj => { if (obj.isMesh) meshes.push(obj); });
    for (const src of meshes) {
      const mesh = new THREE.Mesh(src.geometry, src.material.clone());
      src.matrixWorld.decompose(mesh.position, mesh.quaternion, mesh.scale);
      mesh.castShadow = true;
      const out = mesh.position.clone().sub(center).setY(0).normalize();
      const vel = out.multiplyScalar((1 + Math.random() * 2.2) * spread)
        .addScaledVector(impactDir, 2.4 * force * (0.5 + Math.random()))
        .add(new THREE.Vector3(0, (2 + Math.random() * 3 * force) * up, 0));
      const spin = new THREE.Vector3(Math.random() - 0.5, Math.random() - 0.5, Math.random() - 0.5).multiplyScalar(14 * Math.max(0.3, spread));
      addBit(mesh, { vel, gravity: 9.8, spin, floor: 0.04, life: life + Math.random() * 0.6, fade: 0.8 });
    }
    unit.parent?.remove(unit);
    dust(center.clone().setY(0.1), 18);
  }

  // A glowing orb or tracer that flies from `from` to `to`; resolves on arrival.
  async function projectile(from, to, color, kind = "orb") {
    const isTracer = kind === "tracer";
    const mesh = new THREE.Mesh(isTracer ? new THREE.BoxGeometry(0.035, 0.035, 0.32) : orbGeo, glowMat(color));
    if (!isTracer) mesh.scale.setScalar(0.09);
    mesh.position.copy(from);
    mesh.lookAt(to);
    scene.add(mesh);
    const dist = from.distanceTo(to);
    const seconds = isTracer ? dist / 22 : dist / 6;
    let trailTimer = 0;
    await tween(seconds, t => {
      mesh.position.lerpVectors(from, to, t);
      if (!isTracer) {
        mesh.position.y += Math.sin(t * Math.PI) * 0.35;
        trailTimer += 1;
        if (trailTimer % 2 === 0) {
          const trail = new THREE.Mesh(orbGeo, glowMat(color, 0.6));
          trail.position.copy(mesh.position);
          trail.scale.setScalar(0.06);
          addBit(trail, { grow: -1.5, life: 0.35, fade: 0.35 });
        }
      }
    }, ease.linear);
    scene.remove(mesh);
    mesh.material.dispose();
    if (isTracer) mesh.geometry.dispose();
  }

  async function beam(from, to, color, seconds = 0.7) {
    const dist = from.distanceTo(to);
    const geo = new THREE.CylinderGeometry(1, 1, 1, 10, 1, true);
    geo.translate(0, 0.5, 0);
    geo.rotateX(Math.PI / 2);
    const core = new THREE.Mesh(geo, glowMat(0xffffff, 0.9));
    const halo = new THREE.Mesh(geo, glowMat(color, 0.55));
    for (const mesh of [core, halo]) {
      mesh.position.copy(from);
      mesh.lookAt(to);
      scene.add(mesh);
    }
    await tween(seconds, t => {
      const grow = Math.min(1, t * 5);
      const width = Math.sin(Math.min(1, t * 1.2) * Math.PI) * (0.9 + Math.random() * 0.2);
      core.scale.set(0.025 * width, 0.025 * width, dist * grow);
      halo.scale.set(0.08 * width, 0.08 * width, dist * grow);
    }, ease.linear);
    for (const mesh of [core, halo]) { scene.remove(mesh); mesh.material.dispose(); }
    geo.dispose();
  }

  async function lightning(from, to, color, strikes = 4) {
    for (let s = 0; s < strikes; s += 1) {
      const points = [];
      for (let i = 0; i <= 10; i += 1) {
        const p = from.clone().lerp(to, i / 10);
        if (i > 0 && i < 10) p.add(new THREE.Vector3((Math.random() - 0.5) * 0.35, (Math.random() - 0.5) * 0.35, (Math.random() - 0.5) * 0.35));
        points.push(p);
      }
      const curve = new THREE.CatmullRomCurve3(points);
      const geo = new THREE.TubeGeometry(curve, 30, 0.025, 5, false);
      const bolt = new THREE.Mesh(geo, glowMat(s % 2 ? 0xffffff : color, 1));
      scene.add(bolt);
      await wait(0.07);
      scene.remove(bolt);
      geo.dispose();
      bolt.material.dispose();
    }
  }

  // Pools of blood (or ichor, or oil) that spread across a square, then fade after a few seconds.
  const discGeo = new THREE.CircleGeometry(1, 14);
  function splatter(pos, color, amount = 1) {
    const blobs = 5 + Math.round(amount * 5);
    for (let i = 0; i < blobs; i += 1) {
      const mesh = new THREE.Mesh(discGeo, new THREE.MeshStandardMaterial({
        color, roughness: 0.22, metalness: 0.1, transparent: true, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -2 - i
      }));
      mesh.rotation.x = -Math.PI / 2;
      mesh.rotation.z = Math.random() * Math.PI;
      const reach = i === 0 ? 0 : Math.min(0.36, 0.3 * amount);
      mesh.position.set(pos.x + (Math.random() - 0.5) * 2 * reach, 0.004 + i * 0.0005, pos.z + (Math.random() - 0.5) * 2 * reach);
      const size = (i === 0 ? 0.2 : 0.04 + Math.random() * 0.09) * Math.min(amount, 1.6);
      const stretch = 0.6 + Math.random() * 0.8;
      mesh.scale.set(0.001, 0.001, 1);
      addBit(mesh, { life: 6 + Math.random() * 1.5, fade: 2.6 });
      tween(0.2 + Math.random() * 0.35, t => mesh.scale.set(size * t, size * stretch * t, 1), ease.out);
    }
    for (let i = 0; i < 10 * amount; i += 1) {
      const drop = new THREE.Mesh(sparkGeo, new THREE.MeshStandardMaterial({ color, roughness: 0.3 }));
      drop.position.copy(pos).setY(0.45 + Math.random() * 0.3);
      const a = Math.random() * Math.PI * 2;
      addBit(drop, { vel: new THREE.Vector3(Math.cos(a) * 1.4, 1 + Math.random() * 2, Math.sin(a) * 1.4).multiplyScalar(0.5 + Math.random()),
        gravity: 9, floor: 0.01, life: 1.2, fade: 0.4 });
    }
  }

  // A glowing mote that drifts upward: embers, sparks, fireflies, data motes.
  function ember(pos, color = 0xff7a2a, rise = 1) {
    const mesh = new THREE.Mesh(sparkGeo, glowMat(color));
    mesh.position.copy(pos);
    mesh.scale.setScalar(0.5 + Math.random() * 0.8);
    addBit(mesh, { vel: new THREE.Vector3((Math.random() - 0.5) * 0.4, (0.5 + Math.random() * 0.9) * rise, (Math.random() - 0.5) * 0.4),
      spin: new THREE.Vector3(3, 3, 3), life: 1.6 + Math.random() * 1.6, fade: 0.9 });
  }

  // Generic short-lived particle for weather and terrain effects.
  const spawn = (mesh, opts) => addBit(mesh, opts);

  // Bullet time: slows every tween and particle for a stretch of real time.
  let slowTimer = null;
  // Hit-stop: everything holds still for a split second on contact, which makes a blow feel heavy.
  // Any slow motion that should follow it (combos) is passed in, so the two don't fight over the speed.
  function hitstop(realSeconds = 0.09, then = null) {
    slowmo(0.02, realSeconds);
    if (then) setTimeout(() => slowmo(then.factor, then.seconds), realSeconds * 1000 + 5);
  }

  function slowmo(factor, realSeconds) {
    speed = factor;
    clearTimeout(slowTimer);
    slowTimer = setTimeout(() => { speed = 1; }, realSeconds * 1000);
  }

  function flashScreen(strength = 0.5) {
    const el = document.getElementById("flash");
    el.style.transition = "none";
    el.style.opacity = String(strength);
    requestAnimationFrame(() => {
      el.style.transition = "opacity 450ms";
      el.style.opacity = "0";
    });
  }

  function shake(amount) {
    shakeAmp = Math.max(shakeAmp, amount);
  }

  // ---- Synthesized sound effects (no audio files needed).
  let audio = null;
  let muted = false;
  function ctx() {
    if (!audio) {
      const Ctor = window.AudioContext || window.webkitAudioContext;
      if (!Ctor) return null;
      audio = new Ctor();
    }
    if (audio.state === "suspended") audio.resume();
    return audio;
  }

  function tone(freq, seconds, type = "sine", volume = 0.2, slideTo = null, delay = 0) {
    const ac = ctx();
    if (!ac) return;
    const start = ac.currentTime + delay;
    const osc = ac.createOscillator();
    const gain = ac.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, start);
    if (slideTo) osc.frequency.exponentialRampToValueAtTime(slideTo, start + seconds);
    gain.gain.setValueAtTime(volume, start);
    gain.gain.exponentialRampToValueAtTime(0.001, start + seconds);
    osc.connect(gain).connect(ac.destination);
    osc.start(start);
    osc.stop(start + seconds + 0.02);
  }

  function noise(seconds, volume = 0.3, freq = 1000, filterType = "lowpass", sweepTo = null) {
    const ac = ctx();
    if (!ac) return;
    const length = Math.floor(ac.sampleRate * seconds);
    const buffer = ac.createBuffer(1, length, ac.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < length; i += 1) data[i] = Math.random() * 2 - 1;
    const src = ac.createBufferSource();
    src.buffer = buffer;
    const filter = ac.createBiquadFilter();
    filter.type = filterType;
    filter.frequency.setValueAtTime(freq, ac.currentTime);
    if (sweepTo) filter.frequency.exponentialRampToValueAtTime(sweepTo, ac.currentTime + seconds);
    const gain = ac.createGain();
    gain.gain.setValueAtTime(volume, ac.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ac.currentTime + seconds);
    src.connect(filter).connect(gain).connect(ac.destination);
    src.start();
  }

  const SOUNDS = {
    select: () => tone(660, 0.08, "triangle", 0.08),
    step: () => noise(0.06, 0.12, 500),
    whoosh: () => noise(0.35, 0.25, 400, "bandpass", 2500),
    hit: () => { noise(0.3, 0.45, 900); tone(110, 0.35, "sine", 0.4, 40); },
    clang: () => { tone(900, 0.25, "square", 0.06); tone(1350, 0.4, "triangle", 0.1); noise(0.12, 0.2, 3000, "highpass"); },
    shot: () => { noise(0.09, 0.4, 1500, "highpass"); tone(160, 0.1, "square", 0.12, 60); },
    spell: () => { tone(300, 0.55, "sine", 0.18, 1200); tone(600, 0.55, "triangle", 0.06, 2400); },
    zap: () => { noise(0.45, 0.35, 3000, "bandpass"); tone(120, 0.4, "sawtooth", 0.1, 60); },
    splash: () => { noise(0.4, 0.3, 1800, "bandpass", 500); },
    crack: () => { noise(0.15, 0.3, 4000, "highpass"); tone(1800, 0.12, "triangle", 0.05, 900); },
    rustle: () => { noise(0.3, 0.15, 2500, "bandpass", 1200); },
    sizzle: () => { noise(0.5, 0.2, 5000, "highpass"); },
    combo: level => [392, 494, 587, 784, 988, 1175].slice(0, Math.min(6, level + 2)).forEach((f, i) => tone(f, 0.28, "sawtooth", 0.07, null, i * 0.07)),
    drums: () => { tone(70, 0.35, "sine", 0.45, 45); tone(70, 0.35, "sine", 0.35, 45, 0.22); },
    boom: () => { noise(0.9, 0.55, 380); tone(65, 0.8, "sine", 0.5, 28); },
    check: () => { tone(440, 0.18, "square", 0.06); tone(330, 0.3, "square", 0.06, null, 0.15); },
    fanfare: () => [523, 659, 784, 1046].forEach((f, i) => tone(f, 0.45, "triangle", 0.14, null, i * 0.16)),
    defeat: () => [392, 330, 262, 196].forEach((f, i) => tone(f, 0.55, "sine", 0.14, null, i * 0.22))
  };

  function sfx(name, arg) {
    if (muted || !SOUNDS[name]) return;
    try { SOUNDS[name](arg); } catch (error) { /* audio is optional */ }
  }

  return {
    init, update, tween, wait, lerp, ease, burst, dust, shockwave, shatter, projectile, beam, lightning,
    splatter, ember, spawn, slowmo, hitstop, glowMat, flashScreen, shake, sfx,
    get shakeAmp() { return shakeAmp; },
    get muted() { return muted; },
    set muted(value) { muted = value; },
    set speed(value) { speed = value; }
  };
})();
