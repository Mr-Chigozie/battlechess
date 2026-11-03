"use strict";

// Battlefield terrain. Purely visual: the chess rules never change, but every battlefield dresses the board
// in its own surface, scatters special squares (water, ice, forest...) across the middle ranks, and those
// squares react when units arrive or fall on them. The battlefield is chosen by army id (the enemy in story mode).
const Terrain = (() => {
  const V3 = THREE.Vector3;
  const THEMES = {
    knights:    { name: "the Green Meadow", light: 0xd7dcae, dark: 0x5f7d3b, specials: ["forest", "water", "hill"], weather: "pollen", props: "meadow" },
    undead:     { name: "the Barrow Fields", light: 0x9a978a, dark: 0x3b3f36, specials: ["grave", "mire", "grave"], weather: "ash", props: "graveyard" },
    vikings:    { name: "the Frozen Fjord", light: 0xe8eef2, dark: 0x7d93a3, specials: ["ice", "water", "hill"], weather: "snow", props: "taiga" },
    samurai:    { name: "the Blossom Garden", light: 0xe6d6b8, dark: 0x7a6a52, specials: ["blossom", "water", "hill"], weather: "petals", props: "garden" },
    spartans:   { name: "the Hot Gates", light: 0xe2c995, dark: 0x9a7448, specials: ["sand", "hill", "sand"], weather: "dust", props: "pass" },
    asgardians: { name: "the Bifrost Realm", light: 0xc9c6e8, dark: 0x3f3b6b, specials: ["rune", "ice", "rune"], weather: "stars", props: "realm" },
    machines:   { name: "the Foundry", light: 0x9aa3ad, dark: 0x2d333a, specials: ["grate", "lava", "grate"], weather: "motes", props: "foundry" },
    marines:    { name: "the Beachhead", light: 0xe0cf9e, dark: 0x8a7a55, specials: ["water", "crater", "sand"], weather: "smoke", props: "beach" }
  };
  const TYPES = {
    water:   { color: 0x3d7fa6, label: "in the shallows" },
    ice:     { color: 0xbfe3f2, label: "on the ice" },
    forest:  { color: 0x3f6b2c, label: "in the forest" },
    hill:    { color: 0x8a7d62, label: "on the high ground" },
    lava:    { color: 0x2a1a14, label: "over the molten cracks" },
    mire:    { color: 0x434a2c, label: "in the mire" },
    grave:   { color: 0x5a5a4c, label: "among the graves" },
    sand:    { color: 0xd9bf86, label: "in the dunes" },
    rune:    { color: 0x4a4790, label: "on the rune stones" },
    crater:  { color: 0x4a4236, label: "in the shell crater" },
    grate:   { color: 0x3a4048, label: "on the power grid" },
    blossom: { color: 0xc99aa8, label: "beneath the blossoms" }
  };

  let scene = null;
  let tiles = [];
  let decor = null;
  let props = null;
  let theme = THEMES.knights;
  let map = [];
  let animated = [];
  let weatherTimer = 0;

  // Particles are spawned constantly, so their geometries are shared rather than created per particle.
  const geoCache = new Map();
  const geo = (key, make) => geoCache.get(key) || geoCache.set(key, make()).get(key);
  const mat = (color, extra = {}) => new THREE.MeshStandardMaterial({ color, roughness: 0.7, flatShading: true, ...extra });
  const glow = (color, opacity = 0.9) => FX.glowMat(color, opacity);
  const squareCenter = (row, col) => new V3(col - 3.5, 0, row - 3.5);
  const CORNERS = [[-0.34, -0.34], [0.34, -0.34], [-0.34, 0.34], [0.34, 0.34]];

  function seeded(text) {
    let seed = [...text].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) % 2147483647, 7) || 1;
    return () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  }

  function add(geo, material, x, y, z, parent, shadow = true) {
    const mesh = new THREE.Mesh(geo, material);
    mesh.position.set(x, y, z);
    mesh.castShadow = shadow;
    mesh.receiveShadow = true;
    parent.add(mesh);
    return mesh;
  }

  function init(sceneRef, tileMeshes) {
    scene = sceneRef;
    tiles = tileMeshes;
    decor = new THREE.Group();
    props = new THREE.Group();
    scene.add(decor, props);
  }

  // ---------------------------------------------------------------- apply a battlefield
  function apply(army) {
    theme = THEMES[army] || THEMES.knights;
    const rand = seeded(army);
    decor.clear();
    props.clear();
    animated = [];
    map = Array.from({ length: 8 }, () => Array(8).fill(null));

    // Special squares come in small clusters on ranks 3-6, where the armies meet.
    for (const type of theme.specials) {
      let row = 2 + Math.floor(rand() * 4);
      let col = Math.floor(rand() * 8);
      const size = 2 + Math.floor(rand() * 3);
      for (let i = 0; i < size; i += 1) {
        if (!map[row][col]) map[row][col] = type;
        const [dr, dc] = [[1, 0], [-1, 0], [0, 1], [0, -1]][Math.floor(rand() * 4)];
        row = Math.min(5, Math.max(2, row + dr));
        col = Math.min(7, Math.max(0, col + dc));
      }
    }

    const light = mat(theme.light, { roughness: 0.55, flatShading: false });
    const dark = mat(theme.dark, { roughness: 0.6, flatShading: false });
    for (const tile of tiles) {
      const { row, col } = tile.userData.square;
      const parityLight = (row + col) % 2 === 0;
      const type = map[row][col];
      if (type) {
        // Keep the checker readable: special squares are tinted toward their light/dark parity.
        const color = new THREE.Color(TYPES[type].color).lerp(new THREE.Color(parityLight ? theme.light : theme.dark), 0.35);
        tile.material = mat(color, { roughness: type === "ice" ? 0.1 : 0.6, metalness: type === "grate" ? 0.6 : 0.05, flatShading: false });
        buildSquare(type, row, col, rand);
      } else {
        tile.material = parityLight ? light : dark;
      }
    }
    buildProps(rand);
  }

  function buildSquare(type, row, col, rand) {
    const c = squareCenter(row, col);
    const g = new THREE.Group();
    g.position.copy(c);
    decor.add(g);
    const flat = (geo, material, y = 0.006) => {
      const mesh = add(geo, material, 0, y, 0, g, false);
      mesh.rotation.x = -Math.PI / 2;
      return mesh;
    };
    switch (type) {
      case "water": {
        const surface = flat(new THREE.PlaneGeometry(0.98, 0.98), mat(0x6fb6dd, { transparent: true, opacity: 0.55, roughness: 0.05, metalness: 0.3, flatShading: false }));
        animated.push({ kind: "water", mesh: surface, phase: rand() * 6 });
        for (let i = 0; i < 2; i += 1) {
          const [x, z] = CORNERS[Math.floor(rand() * 4)];
          add(new THREE.CylinderGeometry(0.07, 0.07, 0.01, 8), mat(0x4f8f3a), x * 0.9, 0.012, z * 0.9, g);
        }
        break;
      }
      case "ice": {
        flat(new THREE.PlaneGeometry(0.98, 0.98), mat(0xe8f8ff, { transparent: true, opacity: 0.45, roughness: 0.02, metalness: 0.4, flatShading: false }));
        const crack = mat(0xffffff, { emissive: 0x9fdcff, emissiveIntensity: 0.4 });
        for (let i = 0; i < 4; i += 1) {
          const line = add(new THREE.BoxGeometry(0.01, 0.004, 0.25 + rand() * 0.3), crack, (rand() - 0.5) * 0.6, 0.009, (rand() - 0.5) * 0.6, g, false);
          line.rotation.y = rand() * Math.PI;
        }
        break;
      }
      case "forest":
        for (const [x, z] of CORNERS) if (rand() > 0.2) { const t = tree("round", 0.32 + rand() * 0.12); t.position.set(x, 0, z); g.add(t); }
        break;
      case "hill":
      case "sand":
        for (const [x, z] of CORNERS) if (rand() > 0.3) {
          const lump = add(new THREE.DodecahedronGeometry(0.09 + rand() * 0.06, 0), mat(type === "sand" ? 0xd9bf86 : 0x8a8270), x, 0.03, z, g);
          lump.scale.y = type === "sand" ? 0.35 : 0.8;
        }
        break;
      case "lava": {
        const crack = glow(0xff6a1a, 0.95);
        for (let i = 0; i < 5; i += 1) {
          const line = add(new THREE.BoxGeometry(0.03, 0.004, 0.3 + rand() * 0.4), crack, (rand() - 0.5) * 0.6, 0.008, (rand() - 0.5) * 0.6, g, false);
          line.rotation.y = rand() * Math.PI;
          animated.push({ kind: "lava", mesh: line, phase: rand() * 6 });
        }
        animated.push({ kind: "embers", pos: c.clone(), color: 0xff7a2a, phase: rand() });
        break;
      }
      case "mire": {
        flat(new THREE.CircleGeometry(0.44, 16), mat(0x2f3a1f, { transparent: true, opacity: 0.8, roughness: 0.2, flatShading: false }));
        animated.push({ kind: "bubbles", pos: c.clone(), phase: rand() });
        break;
      }
      case "grave": {
        const [x, z] = CORNERS[Math.floor(rand() * 4)];
        const stone = add(new THREE.BoxGeometry(0.16, 0.22, 0.05), mat(0x8a8a80), x, 0.11, z, g);
        stone.rotation.set(0, rand() - 0.5, (rand() - 0.5) * 0.3);
        add(new THREE.CylinderGeometry(0.08, 0.08, 0.05, 10, 1, false, 0, Math.PI), mat(0x8a8a80), x, 0.22, z, g).rotation.set(Math.PI / 2, 0, 0);
        animated.push({ kind: "wisp", pos: c.clone(), color: 0x7dff9a, phase: rand() });
        break;
      }
      case "rune": {
        const ring = flat(new THREE.RingGeometry(0.3, 0.36, 24), glow(0x9fdcff, 0.7), 0.008);
        const inner = flat(new THREE.RingGeometry(0.12, 0.15, 6), glow(0xc9a7ff, 0.7), 0.009);
        animated.push({ kind: "rune", mesh: ring, phase: rand() * 6 }, { kind: "rune", mesh: inner, phase: rand() * 6 });
        break;
      }
      case "crater": {
        flat(new THREE.CircleGeometry(0.4, 16), mat(0x2a241e, { flatShading: false }));
        const rim = add(new THREE.TorusGeometry(0.4, 0.05, 5, 16), mat(0x6e604c), 0, 0.01, 0, g);
        rim.rotation.x = Math.PI / 2;
        rim.scale.z = 0.5;
        animated.push({ kind: "smoke", pos: c.clone(), phase: rand() });
        break;
      }
      case "grate": {
        const seam = glow(0x4fd8ff, 0.8);
        for (const offset of [-0.25, 0, 0.25]) {
          add(new THREE.BoxGeometry(0.9, 0.004, 0.012), seam, 0, 0.008, offset, g, false);
          add(new THREE.BoxGeometry(0.012, 0.004, 0.9), seam, offset, 0.008, 0, g, false);
        }
        animated.push({ kind: "sparks", pos: c.clone(), phase: rand() });
        break;
      }
      case "blossom":
        for (const [x, z] of CORNERS) if (rand() > 0.4) { const t = tree("cherry", 0.3); t.position.set(x, 0, z); g.add(t); }
        for (let i = 0; i < 12; i += 1) {
          const petal = flat(new THREE.CircleGeometry(0.02, 5), mat(0xf6b8c8), 0.007);
          petal.position.set((rand() - 0.5) * 0.8, 0.007, (rand() - 0.5) * 0.8);
        }
        break;
    }
  }

  // ---------------------------------------------------------------- scenery around the board
  function tree(kind, scale = 1) {
    const g = new THREE.Group();
    const trunkColor = kind === "dead" ? 0x4a4038 : 0x5b3d26;
    add(new THREE.CylinderGeometry(0.06, 0.09, 0.6, 6), mat(trunkColor), 0, 0.3, 0, g);
    if (kind === "pine") {
      for (let i = 0; i < 3; i += 1) {
        add(new THREE.ConeGeometry(0.42 - i * 0.1, 0.5, 7), mat(0x2f4a33), 0, 0.6 + i * 0.3, 0, g);
        add(new THREE.ConeGeometry(0.2 - i * 0.05, 0.16, 7), mat(0xf4f8fa), 0, 0.82 + i * 0.3, 0, g);
      }
    } else if (kind === "dead") {
      for (let i = 0; i < 3; i += 1) {
        const branch = add(new THREE.CylinderGeometry(0.02, 0.035, 0.45, 4), mat(trunkColor), 0, 0.65, 0, g);
        branch.rotation.set(0.7, i * 2.1, 0);
        branch.position.set(Math.sin(i * 2.1) * 0.12, 0.7, Math.cos(i * 2.1) * 0.12);
      }
    } else {
      const leaf = kind === "cherry" ? 0xf2a7bd : 0x4f7d32;
      add(new THREE.DodecahedronGeometry(0.36, 0), mat(leaf), 0, 0.82, 0, g);
      add(new THREE.DodecahedronGeometry(0.26, 0), mat(leaf), 0.2, 0.7, 0.1, g);
      add(new THREE.DodecahedronGeometry(0.24, 0), mat(leaf), -0.18, 0.72, -0.08, g);
    }
    g.scale.setScalar(scale);
    return g;
  }

  function buildProps(rand) {
    const place = (object, minRadius = 7, maxRadius = 20) => {
      const angle = rand() * Math.PI * 2;
      const radius = minRadius + rand() * (maxRadius - minRadius);
      object.position.set(Math.cos(angle) * radius, -0.5, Math.sin(angle) * radius);
      object.rotation.y = rand() * Math.PI * 2;
      props.add(object);
      return object;
    };
    const rock = (size, color = 0x7a756a) => {
      const g = new THREE.Group();
      add(new THREE.DodecahedronGeometry(size, 0), mat(color), 0, size * 0.4, 0, g).rotation.set(rand() * 3, rand() * 3, rand() * 3);
      return g;
    };
    const style = theme.props;
    for (let i = 0; i < 40; i += 1) {
      const big = 1.4 + rand() * 1.6;
      switch (style) {
        case "meadow": place(i % 4 ? tree("round", big) : rock(0.5 + rand() * 0.6)); break;
        case "taiga": place(i % 5 ? tree("pine", big) : rock(0.6 + rand() * 0.8, 0xd8e2e8)); break;
        case "garden": place(i % 3 ? tree("cherry", big * 0.9) : lantern()); break;
        case "graveyard": place(i % 3 ? tombstone(1.5 + rand()) : tree("dead", big)); break;
        case "pass": place(i % 3 ? rock(0.8 + rand() * 1.4, 0xb08f62) : column(), 7, 18); break;
        case "realm": place(floatingRock(0.4 + rand() * 0.8), 7, 18); break;
        case "foundry": place(i % 2 ? pylon() : rock(0.6 + rand() * 0.6, 0x3a3f46)); break;
        case "beach": place(i % 3 ? sandbags() : hedgehog()); break;
      }
    }
  }

  function tombstone(scale) {
    const g = new THREE.Group();
    add(new THREE.BoxGeometry(0.32, 0.5, 0.1), mat(0x7d7d72), 0, 0.25, 0, g);
    add(new THREE.CylinderGeometry(0.16, 0.16, 0.1, 10, 1, false, 0, Math.PI), mat(0x7d7d72), 0, 0.5, 0, g).rotation.x = Math.PI / 2;
    g.rotation.z = (Math.random() - 0.5) * 0.3;
    g.scale.setScalar(scale);
    return g;
  }
  function lantern() {
    const g = new THREE.Group();
    add(new THREE.CylinderGeometry(0.12, 0.18, 0.7, 6), mat(0x8d877a), 0, 0.35, 0, g);
    add(new THREE.BoxGeometry(0.3, 0.25, 0.3), mat(0x8d877a), 0, 0.82, 0, g);
    add(new THREE.BoxGeometry(0.14, 0.12, 0.32), glow(0xffc070), 0, 0.82, 0, g, false);
    add(new THREE.ConeGeometry(0.32, 0.22, 4), mat(0x6d675a), 0, 1.05, 0, g).rotation.y = Math.PI / 4;
    g.scale.setScalar(1.6);
    return g;
  }
  function column() {
    const g = new THREE.Group();
    const h = 1.5 + Math.random() * 2.5;
    add(new THREE.CylinderGeometry(0.3, 0.34, h, 10), mat(0xd8cdb4), 0, h / 2, 0, g);
    add(new THREE.BoxGeometry(0.8, 0.18, 0.8), mat(0xd8cdb4), 0, h + 0.09, 0, g);
    return g;
  }
  function floatingRock(size) {
    const g = new THREE.Group();
    const r = add(new THREE.DodecahedronGeometry(size, 0), mat(0x5d5a7d), 0, 2 + Math.random() * 4, 0, g);
    add(new THREE.ConeGeometry(size * 0.8, size * 1.4, 6), mat(0x4a4766), 0, -size * 0.9, 0, r).rotation.x = Math.PI;
    animated.push({ kind: "float", mesh: r, base: r.position.y, phase: Math.random() * 6 });
    return g;
  }
  function pylon() {
    const g = new THREE.Group();
    const h = 2 + Math.random() * 3;
    add(new THREE.BoxGeometry(0.4, h, 0.4), mat(0x3a3f46, { metalness: 0.6 }), 0, h / 2, 0, g);
    add(new THREE.BoxGeometry(0.42, h * 0.8, 0.05), glow(0xff3b3b, 0.8), 0, h / 2, 0.2, g, false);
    return g;
  }
  function sandbags() {
    const g = new THREE.Group();
    for (let i = 0; i < 5; i += 1) {
      add(new THREE.SphereGeometry(0.22, 8, 6), mat(0xa89a6e), (i % 3) * 0.4 - 0.4, 0.15 + Math.floor(i / 3) * 0.22, 0, g).scale.set(1, 0.55, 0.7);
    }
    return g;
  }
  function hedgehog() {
    const g = new THREE.Group();
    for (let i = 0; i < 3; i += 1) {
      const beam = add(new THREE.BoxGeometry(0.08, 1.1, 0.08), mat(0x4a4038, { metalness: 0.6 }), 0, 0.4, 0, g);
      beam.rotation.set(i === 0 ? 0.8 : 0, i * 1.05, i === 2 ? 0.8 : 0.4);
    }
    return g;
  }

  // ---------------------------------------------------------------- per-frame life
  const WEATHER = {
    snow:   { rate: 30, make: () => fall(0xffffff, 0.035, -0.55, 0.15, 9) },
    ash:    { rate: 18, make: () => fall(0x6b6b66, 0.03, -0.3, 0.25, 9, true) },
    petals: { rate: 14, make: () => fall(0xf6b8c8, 0.035, -0.35, 0.45, 10, true) },
    pollen: { rate: 10, make: () => mote(0xfff0a0, 0.15) },
    dust:   { rate: 10, make: () => drift(0xd9c08e) },
    stars:  { rate: 16, make: () => twinkle() },
    motes:  { rate: 12, make: () => mote(0xff4a4a, 0.5) },
    smoke:  { rate: 5, make: () => drift(0x777777, true) }
  };

  function randomPoint(spread = 10) {
    return new V3((Math.random() - 0.5) * 2 * spread, 0, (Math.random() - 0.5) * 2 * spread);
  }
  function fall(color, size, speed, sway, life, tumble = false) {
    const mesh = new THREE.Mesh(geo(`fall${size}${tumble}`, () => new THREE.BoxGeometry(size, tumble ? size * 0.3 : size, size)), mat(color, { transparent: true }));
    mesh.position.copy(randomPoint()).setY(5 + Math.random() * 3);
    FX.spawn(mesh, { vel: new V3((Math.random() - 0.5) * sway, speed, (Math.random() - 0.5) * sway), spin: tumble ? new V3(2, 3, 2) : undefined, life, fade: 1.5 });
  }
  function mote(color, rise) {
    const p = randomPoint(7).setY(0.3 + Math.random() * 1.5);
    FX.ember(p, color, rise);
  }
  function drift(color, rising = false) {
    const mesh = new THREE.Mesh(geo(`drift${rising}`, () => new THREE.SphereGeometry(rising ? 0.35 : 0.2, 6, 5)), mat(color, { transparent: true, opacity: rising ? 0.18 : 0.22, depthWrite: false }));
    mesh.position.copy(randomPoint(rising ? 12 : 9)).setY(rising ? 0 : 0.2 + Math.random() * 0.6);
    FX.spawn(mesh, { vel: rising ? new V3(0.2, 0.45, 0) : new V3(0.9, 0.05, 0.2), grow: rising ? 0.4 : 0.2, life: 5, fade: 2 });
  }
  function twinkle() {
    const mesh = new THREE.Mesh(geo("star", () => new THREE.SphereGeometry(0.05, 4, 3)), glow(0xffffff, 1));
    const angle = Math.random() * Math.PI * 2;
    mesh.position.set(Math.cos(angle) * (14 + Math.random() * 10), 3 + Math.random() * 10, Math.sin(angle) * (14 + Math.random() * 10));
    FX.spawn(mesh, { life: 2.5, fade: 1.2 });
  }

  function update(dt, time) {
    weatherTimer += dt;
    const weather = WEATHER[theme.weather];
    while (weather && weatherTimer > 1 / weather.rate) {
      weatherTimer -= 1 / weather.rate;
      weather.make();
    }
    for (const item of animated) {
      const wave = Math.sin(time * 2 + item.phase);
      switch (item.kind) {
        case "water": item.mesh.material.opacity = 0.5 + wave * 0.08; break;
        case "lava": item.mesh.material.opacity = 0.65 + wave * 0.3; break;
        case "rune": item.mesh.material.opacity = 0.45 + wave * 0.35; item.mesh.rotation.z += dt * 0.3; break;
        case "float": item.mesh.position.y = item.base + Math.sin(time * 0.6 + item.phase) * 0.3; break;
        case "embers": if (Math.random() < dt * 2) FX.ember(item.pos.clone().add(new V3((Math.random() - 0.5) * 0.7, 0.05, (Math.random() - 0.5) * 0.7)), item.color, 0.6); break;
        case "wisp": if (Math.random() < dt * 0.8) FX.ember(item.pos.clone().add(new V3((Math.random() - 0.5) * 0.6, 0.1, (Math.random() - 0.5) * 0.6)), item.color, 0.3); break;
        case "sparks": if (Math.random() < dt * 0.5) FX.burst(item.pos.clone().add(new V3((Math.random() - 0.5) * 0.8, 0.02, (Math.random() - 0.5) * 0.8)), 0x4fd8ff, 4, 0.8, 0.5, 0.3); break;
        case "bubbles": if (Math.random() < dt * 1.5) bubble(item.pos); break;
        case "smoke": if (Math.random() < dt * 0.6) puff(item.pos, 0x555049); break;
      }
    }
  }

  function bubble(pos) {
    const mesh = new THREE.Mesh(geo("bubble", () => new THREE.SphereGeometry(0.03, 6, 4)), mat(0x8fae5a, { transparent: true, opacity: 0.8, roughness: 0.1 }));
    mesh.position.copy(pos).add(new V3((Math.random() - 0.5) * 0.6, 0.01, (Math.random() - 0.5) * 0.6));
    FX.spawn(mesh, { vel: new V3(0, 0.08, 0), grow: 1.5, life: 0.8, fade: 0.2 });
  }
  function puff(pos, color) {
    const mesh = new THREE.Mesh(geo("puff", () => new THREE.SphereGeometry(0.1, 6, 5)), mat(color, { transparent: true, opacity: 0.3, depthWrite: false }));
    mesh.position.copy(pos).setY(0.05);
    FX.spawn(mesh, { vel: new V3(0.05, 0.35, 0), grow: 0.8, life: 3, fade: 2 });
  }
  function petals(pos, color, count = 14) {
    for (let i = 0; i < count; i += 1) {
      const mesh = new THREE.Mesh(geo("petal", () => new THREE.BoxGeometry(0.05, 0.01, 0.04)), mat(color, { transparent: true }));
      mesh.position.copy(pos).setY(0.15 + Math.random() * 0.3);
      const a = Math.random() * Math.PI * 2;
      FX.spawn(mesh, { vel: new V3(Math.cos(a) * 0.8, 0.9 + Math.random(), Math.sin(a) * 0.8), gravity: 1.6, spin: new V3(5, 5, 5), floor: 0.01, life: 2, fade: 0.8 });
    }
  }

  // ---------------------------------------------------------------- flavour hooks
  const at = (row, col) => {
    const type = map[row]?.[col];
    return type ? { type, label: TYPES[type].label } : null;
  };

  // A unit has just stepped onto a square.
  async function arrive(unit, sq) {
    const t = at(sq.row, sq.col);
    if (!t) return;
    const pos = unit.position.clone();
    switch (t.type) {
      case "water":
        FX.sfx("splash");
        FX.burst(pos.clone().setY(0.05), 0x9fd8ff, 16, 1.6, 0.8, 0.6);
        FX.shockwave(pos, 0x9fd8ff);
        break;
      case "ice": {
        // Skid past the centre and slide back.
        FX.sfx("crack");
        FX.burst(pos.clone().setY(0.05), 0xffffff, 10, 1, 0.6, 0.4);
        const forward = new V3(Math.sin(unit.rotation.y), 0, Math.cos(unit.rotation.y));
        await FX.tween(0.22, k => unit.position.copy(pos).addScaledVector(forward, Math.sin(k * Math.PI) * 0.22), FX.ease.out);
        unit.position.copy(pos);
        break;
      }
      case "forest": case "blossom":
        FX.sfx("rustle");
        petals(pos, t.type === "forest" ? 0x5f9a3a : 0xf6b8c8, 10);
        break;
      case "hill": case "sand": case "crater":
        FX.dust(pos.clone().setY(0.05), 10);
        break;
      case "lava":
        FX.sfx("sizzle");
        for (let i = 0; i < 10; i += 1) FX.ember(pos.clone().setY(0.1), 0xff7a2a, 1.2);
        break;
      case "mire":
        FX.sfx("splash");
        for (let i = 0; i < 6; i += 1) bubble(pos);
        break;
      case "rune":
        FX.shockwave(pos, 0x9fdcff);
        FX.burst(pos.clone().setY(0.1), 0xc9a7ff, 12, 1.4, 0.7, 0.5);
        break;
      case "grate":
        FX.burst(pos.clone().setY(0.05), 0x4fd8ff, 14, 1.8, 0.6, 0.4);
        break;
      case "grave":
        for (let i = 0; i < 6; i += 1) FX.ember(pos.clone().setY(0.2), 0x7dff9a, 0.4);
        break;
    }
  }

  // A unit has just died on a square. Returns how its blood should look there.
  function fall_(sq, pos, blood) {
    const t = at(sq.row, sq.col);
    if (!t) return { color: blood, amount: 1 };
    switch (t.type) {
      case "water":
        FX.sfx("splash");
        FX.burst(pos.clone().setY(0.1), 0x9fd8ff, 30, 2.6, 1, 0.8);
        return { color: new THREE.Color(blood).lerp(new THREE.Color(0x3d7fa6), 0.35).getHex(), amount: 1.6 };
      case "ice":
        FX.sfx("crack");
        FX.burst(pos.clone().setY(0.2), 0xe8f8ff, 26, 2.4, 1.1, 0.7);
        return { color: blood, amount: 1.2 };
      case "lava":
        FX.sfx("sizzle");
        for (let i = 0; i < 24; i += 1) FX.ember(pos.clone().setY(0.15), 0xffa040, 1.6);
        return { color: 0x1a0d08, amount: 0.6 };
      case "forest": case "blossom":
        petals(pos, t.type === "forest" ? 0x5f9a3a : 0xf6b8c8, 24);
        return { color: blood, amount: 1 };
      case "mire":
        for (let i = 0; i < 10; i += 1) bubble(pos);
        return { color: new THREE.Color(blood).lerp(new THREE.Color(0x2f3a1f), 0.5).getHex(), amount: 1.2 };
      case "rune":
        FX.shockwave(pos, 0xc9a7ff);
        return { color: blood, amount: 1 };
      case "grate":
        FX.burst(pos.clone().setY(0.1), 0x4fd8ff, 24, 2.5, 0.8, 0.6);
        return { color: blood, amount: 1 };
      default:
        FX.dust(pos.clone().setY(0.05), 12);
        return { color: blood, amount: 1 };
    }
  }

  return {
    init, apply, update, at, arrive, fall: fall_,
    nameFor: army => (THEMES[army] || THEMES.knights).name,
    get name() { return theme.name; }
  };
})();
