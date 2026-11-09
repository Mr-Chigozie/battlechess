"use strict";

// The 3D battlefield: scene, board, units, input and every move/capture animation.
// Square (row, col) sits at world (col - 3.5, 0, row - 3.5); Ivory's home rows (6-7) are at +z.
const Stage = (() => {
  const V3 = THREE.Vector3;
  const SIDE_FACING = { w: Math.PI, b: 0 };
  // Phones and tablets: touch input, lighter rendering.
  const coarse = window.matchMedia("(pointer: coarse)").matches;
  let framedAspect = 0;
  let renderer, scene, camera, controls, raycaster, clock;
  let markerGroup, hoverMesh, ground, hemi, sun;
  const units = Array.from({ length: 8 }, () => Array(8).fill(null));
  const tiles = [];
  const scenery = [];
  let clickHandler = () => {};
  let hoverHandler = () => {};
  let viewColor = "w";
  let cinematics = true;
  let blood = true;
  const cine = { active: false, target: new V3() };

  const squareToWorld = (row, col) => new V3(col - 3.5, 0, row - 3.5);
  const angleTo = (from, to) => Math.atan2(to.x - from.x, to.z - from.z);
  const unitAt = sq => units[sq.row][sq.col];

  // ---------------------------------------------------------------- setup
  function init(container, onClick, onHover = () => {}) {
    clickHandler = onClick;
    hoverHandler = onHover;
    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, coarse ? 1.5 : 2));
    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    container.appendChild(renderer.domElement);

    scene = new THREE.Scene();
    camera = new THREE.PerspectiveCamera(42, window.innerWidth / window.innerHeight, 0.1, 120);
    controls = new OrbitControls(camera, renderer.domElement);
    controls.enablePan = false;
    controls.enableDamping = true;
    controls.minDistance = 4;
    controls.maxPolarAngle = 1.38;
    // On touch screens one finger only taps units; two fingers pinch to zoom and twist to turn.
    if (coarse) controls.touches = { ONE: null, TWO: THREE.TOUCH.DOLLY_ROTATE };
    applyView("w");
    raycaster = new THREE.Raycaster();
    clock = new THREE.Clock();
    FX.init(scene);

    hemi = new THREE.HemisphereLight(0xffffff, 0x444444, 1.1);
    scene.add(hemi);
    sun = new THREE.DirectionalLight(0xfff1dc, 2.4);
    sun.position.set(6, 12, 5);
    sun.castShadow = true;
    sun.shadow.mapSize.set(coarse ? 1024 : 2048, coarse ? 1024 : 2048);
    Object.assign(sun.shadow.camera, { left: -7, right: 7, top: 7, bottom: -7, near: 1, far: 30 });
    sun.shadow.bias = -0.0005;
    scene.add(sun);

    buildBoard();
    buildScenery();
    Terrain.init(scene, tiles);
    War.init(scene, hemi, sun);
    markerGroup = new THREE.Group();
    scene.add(markerGroup);
    hoverMesh = new THREE.Mesh(new THREE.PlaneGeometry(0.96, 0.96), new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.12, depthWrite: false }));
    hoverMesh.rotation.x = -Math.PI / 2;
    hoverMesh.visible = false;
    scene.add(hoverMesh);

    // A tap is a press and release that barely moved and wasn't part of a two-finger gesture.
    let down = null;
    const pressed = new Set();
    let gesture = false;
    // Unit info: mouse hover on desktop, press-and-hold on touch screens.
    let holdTimer = 0;
    let held = false;
    const cancelHold = () => { clearTimeout(holdTimer); holdTimer = 0; };
    const showInfo = (sq, e) => hoverHandler(sq, e ? e.clientX : 0, e ? e.clientY : 0);
    renderer.domElement.addEventListener("pointerdown", e => {
      pressed.add(e.pointerId);
      if (pressed.size > 1) { gesture = true; cancelHold(); showInfo(null); }
      down = { x: e.clientX, y: e.clientY };
      held = false;
      if (e.pointerType !== "mouse" && pressed.size === 1 && !cine.active) {
        const point = { clientX: e.clientX, clientY: e.clientY };
        holdTimer = setTimeout(() => {
          holdTimer = 0;
          const sq = pickUnit(point);
          if (sq) { held = true; showInfo(sq, point); }
        }, 450);
      }
    });
    const release = e => {
      pressed.delete(e.pointerId);
      cancelHold();
      const wasGesture = gesture;
      if (!pressed.size) gesture = false;
      return wasGesture;
    };
    renderer.domElement.addEventListener("pointercancel", e => { release(e); if (held) { held = false; showInfo(null); } });
    renderer.domElement.addEventListener("pointerup", e => {
      const wasGesture = release(e);
      if (held) { held = false; showInfo(null); return; }  // a hold reads the unit, it doesn't give an order
      if (wasGesture || !down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > (coarse ? 14 : 6) || cine.active) return;
      // Report both what was hit (a unit standing in front wins) and the ground square under the pointer,
      // so a click on a target square partly hidden behind another unit can still give the order.
      const sq = pick(e);
      if (sq) clickHandler(sq, pickTile(e));
    });
    renderer.domElement.addEventListener("pointermove", e => {
      if (holdTimer && down && Math.hypot(e.clientX - down.x, e.clientY - down.y) > 14) cancelHold();
      if (e.pointerType !== "mouse") return;
      if (cine.active) { hoverMesh.visible = false; showInfo(null); return; }
      const sq = pickTile(e);
      hoverMesh.visible = Boolean(sq);
      if (sq) hoverMesh.position.copy(squareToWorld(sq.row, sq.col)).setY(0.012);
      const unitSq = pressed.size ? null : pickUnit(e);  // no tooltip while dragging the camera
      showInfo(unitSq, e);
      renderer.domElement.style.cursor = sq || unitSq ? "pointer" : "grab";
    });
    renderer.domElement.addEventListener("pointerleave", () => { hoverMesh.visible = false; if (!held) showInfo(null); });
    window.addEventListener("resize", () => {
      camera.aspect = window.innerWidth / window.innerHeight;
      camera.updateProjectionMatrix();
      renderer.setSize(window.innerWidth, window.innerHeight);
      // Re-frame on rotation, but not when a mobile URL bar merely slides in or out.
      if (!cine.active && Math.abs(camera.aspect - framedAspect) > 0.15) applyView(viewColor);
    });
    renderer.setAnimationLoop(loop);
  }

  function buildBoard() {
    const frame = new THREE.Mesh(new THREE.BoxGeometry(9.3, 0.5, 9.3), new THREE.MeshStandardMaterial({ color: 0x3a2c22, roughness: 0.7 }));
    frame.position.y = -0.28;
    frame.receiveShadow = true;
    scene.add(frame);

    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = 1024;
    const g = canvas.getContext("2d");
    g.fillStyle = "#3a2c22";
    g.fillRect(0, 0, 1024, 1024);
    g.fillStyle = "#d9bf8a";
    g.font = "bold 34px Georgia";
    g.textAlign = "center";
    g.textBaseline = "middle";
    const px = v => ((v + 4.65) / 9.3) * 1024;
    for (let i = 0; i < 8; i += 1) {
      g.fillText(FILES[i], px(i - 3.5), px(4.32));
      g.fillText(FILES[i], px(i - 3.5), px(-4.32));
      g.fillText(String(8 - i), px(-4.32), px(i - 3.5));
      g.fillText(String(8 - i), px(4.32), px(i - 3.5));
    }
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    const labels = new THREE.Mesh(new THREE.PlaneGeometry(9.3, 9.3), new THREE.MeshStandardMaterial({ map: texture, roughness: 0.8 }));
    labels.rotation.x = -Math.PI / 2;
    labels.position.y = -0.025;
    labels.receiveShadow = true;
    scene.add(labels);

    const light = new THREE.MeshStandardMaterial({ color: 0xd8cfb6, roughness: 0.45, metalness: 0.05 });
    const dark = new THREE.MeshStandardMaterial({ color: 0x46503c, roughness: 0.5, metalness: 0.05 });
    const tileGeo = new THREE.BoxGeometry(1, 0.12, 1);
    for (let row = 0; row < 8; row += 1) {
      for (let col = 0; col < 8; col += 1) {
        const tile = new THREE.Mesh(tileGeo, (row + col) % 2 === 0 ? light : dark);
        tile.position.copy(squareToWorld(row, col)).setY(-0.06);
        tile.receiveShadow = true;
        tile.userData.square = { row, col };
        scene.add(tile);
        tiles.push(tile);
      }
    }

    ground = new THREE.Mesh(new THREE.CircleGeometry(60, 48), new THREE.MeshStandardMaterial({ color: 0x5d7d3c, roughness: 1 }));
    ground.rotation.x = -Math.PI / 2;
    ground.position.y = -0.53;
    ground.receiveShadow = true;
    scene.add(ground);
  }

  // Four braziers mark the corners; the rest of the scenery comes from the battlefield's terrain.
  function buildScenery() {
    const pillarMat = new THREE.MeshStandardMaterial({ color: 0x8d877a, roughness: 0.9, flatShading: true });
    for (const [x, z] of [[-6.5, -6.5], [6.5, -6.5], [-6.5, 6.5], [6.5, 6.5]]) {
      const pillar = new THREE.Mesh(new THREE.CylinderGeometry(0.35, 0.45, 3, 8), pillarMat);
      pillar.position.set(x, 1, z);
      pillar.castShadow = true;
      scene.add(pillar);
      const flame = new THREE.Mesh(new THREE.SphereGeometry(0.22, 8, 6), new THREE.MeshBasicMaterial({ color: 0xffb347 }));
      flame.position.set(x, 2.7, z);
      flame.userData.flame = true;
      scene.add(flame);
      scenery.push(pillar, flame);
    }
  }

  function setTheme(army) {
    const look = ARMIES[army].look;
    scene.background = new THREE.Color(look.sky);
    scene.fog = new THREE.Fog(look.fog, 16, 46);
    ground.material.color.setHex(look.ground);
    hemi.color.setHex(look.sky).lerp(new THREE.Color(0xffffff), 0.5);
    hemi.groundColor.setHex(look.ground);
    for (const item of scenery) {
      if (item.userData.flame) item.material.color.setHex(look.magic);
      else item.material.color.setHex(look.ground).lerp(new THREE.Color(0x9a958a), 0.5);
    }
    Terrain.apply(army);
    War.reset();
    War.setBase(look.sky, look.fog, hemi.color);
  }

  // ---------------------------------------------------------------- units & markers
  function placeUnit(unit, row, col) {
    unit.position.copy(squareToWorld(row, col));
    unit.rotation.set(0, SIDE_FACING[unit.userData.color], 0);
    unit.userData.square = { row, col };
    unit.userData.phase = Math.random() * Math.PI * 2;
    units[row][col] = unit;
    scene.add(unit);
  }

  function syncAll(pos, armies) {
    for (let row = 0; row < 8; row += 1) {
      for (let col = 0; col < 8; col += 1) {
        if (units[row][col]) scene.remove(units[row][col]);
        units[row][col] = null;
        const piece = pos.board[row][col];
        if (piece) placeUnit(Models.buildUnit(armies[piece.color], piece.type, piece.color), row, col);
      }
    }
  }

  const markerMats = {
    selected: new THREE.MeshBasicMaterial({ color: 0xf0c060, transparent: true, opacity: 0.9, depthWrite: false }),
    move: new THREE.MeshBasicMaterial({ color: 0xf6e3a8, transparent: true, opacity: 0.55, depthWrite: false }),
    capture: new THREE.MeshBasicMaterial({ color: 0xe0503c, transparent: true, opacity: 0.85, depthWrite: false }),
    last: new THREE.MeshBasicMaterial({ color: 0xe9c264, transparent: true, opacity: 0.22, depthWrite: false }),
    check: new THREE.MeshBasicMaterial({ color: 0xff3020, transparent: true, opacity: 0.55, depthWrite: false })
  };
  const markerGeo = {
    ring: new THREE.RingGeometry(0.4, 0.47, 32),
    dot: new THREE.CircleGeometry(0.14, 20),
    square: new THREE.PlaneGeometry(0.98, 0.98),
    glow: new THREE.CircleGeometry(0.48, 32)
  };

  function setMarkers({ selected = null, targets = [], lastMove = null, check = null } = {}) {
    markerGroup.clear();
    const mark = (geo, mat, row, col, y = 0.006) => {
      const mesh = new THREE.Mesh(geo, mat);
      mesh.rotation.x = -Math.PI / 2;
      mesh.position.copy(squareToWorld(row, col)).setY(y);
      markerGroup.add(mesh);
      return mesh;
    };
    if (lastMove) {
      mark(markerGeo.square, markerMats.last, lastMove.fromRow, lastMove.fromCol);
      mark(markerGeo.square, markerMats.last, lastMove.toRow, lastMove.toCol);
    }
    if (check) mark(markerGeo.glow, markerMats.check, check.row, check.col, 0.008).userData.pulse = true;
    if (selected) mark(markerGeo.ring, markerMats.selected, selected.row, selected.col, 0.1);
    for (const move of targets) {
      const capture = move.enPassant || units[move.toRow][move.toCol];
      mark(capture ? markerGeo.ring : markerGeo.dot, capture ? markerMats.capture : markerMats.move, move.toRow, move.toCol, capture ? 0.1 : 0.01);
    }
  }

  function pickTile(event) {
    setRay(event);
    const hit = raycaster.intersectObjects(tiles, false)[0];
    return hit ? hit.object.userData.square : null;
  }

  // The square of the unit under the pointer, or null when the pointer is not on a unit.
  function pickUnit(event) {
    setRay(event);
    const all = units.flat().filter(Boolean);
    const hit = raycaster.intersectObjects(all, true)[0];
    if (hit) {
      let obj = hit.object;
      while (obj && !obj.userData.square) obj = obj.parent;
      if (obj) return { ...obj.userData.square };
    }
    return null;
  }

  function pick(event) {
    return pickUnit(event) || pickTile(event);
  }

  function setRay(event) {
    const rect = renderer.domElement.getBoundingClientRect();
    raycaster.setFromCamera(new THREE.Vector2(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1), camera);
  }

  // ---------------------------------------------------------------- render loop
  function loop() {
    const dt = Math.min(clock.getDelta(), 0.05);
    const time = clock.elapsedTime;
    FX.update(dt);
    Terrain.update(dt, time);
    War.update(dt, time);
    idle(time);
    if (cine.active) camera.lookAt(cine.target);
    else controls.update();
    const amp = FX.shakeAmp;
    const offset = new V3((Math.random() - 0.5) * amp, (Math.random() - 0.5) * amp, (Math.random() - 0.5) * amp);
    camera.position.add(offset);
    renderer.render(scene, camera);
    camera.position.sub(offset);
  }

  function idle(time) {
    for (const unit of units.flat()) {
      if (!unit) continue;
      const rig = unit.userData.rig;
      const phase = unit.userData.phase;
      if (!unit.userData.busy) rig.body.position.y = rig.baseY + Math.sin(time * 2 + phase) * 0.008 + (rig.hover ? Math.sin(time * 1.6 + phase) * rig.hover + rig.hover : 0);
      if (rig.rotor) rig.rotor.rotation.y = time * 22;
      if (rig.props) rig.props.forEach((prop, i) => { prop.rotation.y = time * 30 + i; });
      if (rig.spin) rig.spin.rotation.y = time * 0.8;
      if (rig.wings) rig.wings.forEach((wing, i) => { wing.rotation.z = (i ? -1 : 1) * (0.35 + Math.sin(time * 3 + phase) * 0.15); });
      if (rig.chute) rig.chute.rotation.z = Math.sin(time * 1.3 + phase) * 0.06;
    }
    for (const marker of markerGroup.children) {
      if (marker.userData.pulse) marker.material.opacity = 0.35 + Math.sin(time * 6) * 0.2;
    }
  }

  // ---------------------------------------------------------------- camera
  // Camera spot that fits the whole board on screen for the current aspect ratio:
  // wide screens get the classic three-quarter view, phones in portrait a steeper, more top-down one.
  function viewFor(color) {
    const aspect = window.innerWidth / window.innerHeight;
    const portrait = aspect < 0.8;
    const elevation = portrait ? 1.2 : aspect < 1.25 ? 0.98 : 0.8;
    const halfV = THREE.MathUtils.degToRad(camera.fov / 2);
    const halfH = Math.atan(Math.tan(halfV) * aspect);
    // Portrait is width-bound, so let the frame's outer rim touch the screen edges to keep squares big.
    const fitWidth = (portrait ? 4.85 : 5.2) / Math.tan(halfH);
    // Depth fit leaves room for the HUD above and the narration bar below the board.
    const fitDepth = (4.9 * Math.sin(elevation) + 1.2) / Math.tan(halfV);
    const distance = Math.max(fitWidth, fitDepth, 11);
    const side = color === "w" ? 1 : -1;
    const target = new V3(0, 0, 0.3 * side);
    return { target, position: target.clone().add(new V3(0, Math.sin(elevation) * distance, Math.cos(elevation) * distance * side)), distance };
  }

  function applyView(color) {
    viewColor = color;
    const view = viewFor(color);
    camera.position.copy(view.position);
    controls.target.copy(view.target);
    controls.maxDistance = view.distance * 1.5;
    framedAspect = window.innerWidth / window.innerHeight;
  }

  async function setView(color, animate = true) {
    if (!animate) return applyView(color);
    viewColor = color;
    const view = viewFor(color);
    const to = view.position;
    const target = view.target;
    controls.maxDistance = view.distance * 1.5;
    const from = camera.position.clone();
    const fromTarget = controls.target.clone();
    // Swing around the board rather than through it.
    const startAngle = Math.atan2(from.x, from.z);
    let endAngle = Math.atan2(to.x, to.z);
    if (Math.abs(endAngle - startAngle) > Math.PI) endAngle += endAngle < startAngle ? Math.PI * 2 : -Math.PI * 2;
    const startRadius = Math.hypot(from.x, from.z);
    const endRadius = Math.hypot(to.x, to.z);
    await FX.tween(1.1, t => {
      const angle = FX.lerp(startAngle, endAngle === startAngle ? startAngle + Math.PI : endAngle, t);
      const radius = FX.lerp(startRadius, endRadius, t);
      camera.position.set(Math.sin(angle) * radius, FX.lerp(from.y, to.y, t), Math.cos(angle) * radius);
      controls.target.lerpVectors(fromTarget, target, t);
    });
  }

  const toggleView = () => setView(viewColor === "w" ? "b" : "w");

  function showCaption(who, text) {
    document.getElementById("kill-who").textContent = who || "";
    document.getElementById("kill-text").textContent = text || "";
    document.getElementById("kill-caption").classList.add("show");
  }
  const hideCaption = () => document.getElementById("kill-caption").classList.remove("show");

  async function enterCinematic(A, V) {
    cine.saved = { position: camera.position.clone(), target: controls.target.clone() };
    cine.active = true;
    hoverHandler(null, 0, 0);
    controls.enabled = false;
    cine.target.copy(controls.target);
    document.body.classList.add("cinematic");
    const mid = A.clone().add(V).multiplyScalar(0.5);
    const look = mid.clone().setY(0.55);
    const shot = chooseShot(A, V, mid);
    const p0 = camera.position.clone();
    const t0 = cine.target.clone();
    await FX.tween(0.85, t => {
      camera.position.lerpVectors(p0, shot, t);
      cine.target.lerpVectors(t0, look, t);
    });
  }

  // Try camera spots around the duel and keep the one where the fewest bystanders block the view,
  // preferring side-on angles and staying near the player's current side of the board.
  function chooseShot(A, V, mid) {
    const dir = V.clone().sub(A).setY(0).normalize();
    const bystanders = units.flat().filter(Boolean).map(u => u.position.clone().setY(u.userData.height * 0.55));
    const subjects = [A.clone().setY(0.5), V.clone().setY(0.5)];
    // Portrait screens are narrow, so film over the attacker's shoulder (the duel runs up the screen)
    // instead of side-on (where it would run across it and fall off the edges).
    const portrait = window.innerWidth / window.innerHeight < 0.9;
    const distance = portrait ? Math.max(3.2, A.distanceTo(V) * 0.5 + 3) : Math.max(2.8, A.distanceTo(V) * 0.75 + 2);
    const heights = portrait ? [1.9, 2.6] : [1.3, 2.1];
    const current = camera.position.clone().sub(mid).setY(0).normalize();
    const blocked = (from, to) => {
      const seg = to.clone().sub(from);
      const len = seg.length();
      seg.normalize();
      return bystanders.filter(p => {
        const along = p.clone().sub(from).dot(seg);
        if (along < 0.2 || along > len - 0.35) return false;
        return from.clone().addScaledVector(seg, along).distanceTo(p) < 0.42;
      }).length;
    };
    let best = null;
    let bestScore = Infinity;
    for (let i = 0; i < 16; i += 1) {
      const angle = (i / 16) * Math.PI * 2;
      const flat = new V3(Math.cos(angle), 0, Math.sin(angle));
      for (const height of heights) {
        const pos = mid.clone().addScaledVector(flat, distance).setY(height);
        const occluders = subjects.reduce((n, s) => n + blocked(pos, s), 0);
        const framing = portrait ? (1 + flat.dot(dir)) * 3 : Math.abs(flat.dot(dir)) * 3;
        const score = occluders * 10 + framing + (1 - flat.dot(current)) * 0.8 + (height === heights[1] ? 1.5 : 0);
        if (score < bestScore) {
          bestScore = score;
          best = pos;
        }
      }
    }
    return best;
  }

  async function exitCinematic() {
    hideCaption();
    document.body.classList.remove("cinematic");
    const p0 = camera.position.clone();
    const t0 = cine.target.clone();
    await FX.tween(0.8, t => {
      camera.position.lerpVectors(p0, cine.saved.position, t);
      cine.target.lerpVectors(t0, cine.saved.target, t);
    });
    controls.target.copy(cine.saved.target);
    cine.active = false;
    controls.enabled = true;
  }

  // ---------------------------------------------------------------- motion helpers
  async function turnTo(unit, angle, seconds = 0.22) {
    const from = unit.rotation.y;
    const delta = ((angle - from + Math.PI) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2) - Math.PI;
    if (Math.abs(delta) < 0.01) return;
    await FX.tween(seconds, t => { unit.rotation.y = from + delta * t; });
  }

  function gait(rig, phase, amount = 1) {
    rig.legs?.forEach((leg, i) => {
      leg.rotation.x = Math.sin(phase + (i % 2 ? Math.PI : 0) + (i >= 2 ? Math.PI / 2 : 0)) * 0.55 * amount;
    });
  }

  async function travel(unit, to, { seconds, arc = 0, cadence = 13, easing } = {}) {
    const rig = unit.userData.rig;
    const from = unit.position.clone();
    seconds ??= arc ? 0.55 + from.distanceTo(to) * 0.08 : 0.25 + from.distanceTo(to) * 0.38;
    unit.userData.busy = true;
    let steps = 0;
    if (arc) FX.sfx("whoosh");
    await FX.tween(seconds, t => {
      unit.position.lerpVectors(from, to, t);
      unit.position.y = Math.sin(t * Math.PI) * arc;
      const phase = t * seconds * cadence;
      if (arc) {
        gait(rig, 0.9, 1);
        if (rig.wings) rig.wings.forEach((wing, i) => { wing.rotation.z = (i ? -1 : 1) * Math.sin(phase * 1.5) * 0.9; });
      } else {
        gait(rig, phase);
        rig.body.position.y = rig.baseY + Math.abs(Math.sin(phase)) * 0.035;
        if (Math.floor(phase / Math.PI) > steps) { steps += 1; FX.sfx("step"); }
      }
    }, easing || (arc ? FX.ease.inOut : FX.ease.linear));
    gait(rig, 0, 0);
    unit.position.y = 0;
    if (arc) { FX.dust(unit.position.clone().setY(0.05), 6); FX.sfx("step"); }
    unit.userData.busy = false;
  }

  async function moveUnit(unit, to, { hop = false, sq = null } = {}) {
    await turnTo(unit, angleTo(unit.position, to));
    await travel(unit, to, { arc: hop ? 1.1 : 0 });
    if (sq) await Terrain.arrive(unit, sq);
    await turnTo(unit, SIDE_FACING[unit.userData.color]);
  }

  const center = unit => unit.position.clone().setY(unit.userData.height * 0.5);
  const tipOf = unit => {
    const rig = unit.userData.rig;
    return rig.tip ? rig.tip.getWorldPosition(new V3()) : unit.position.clone().setY(unit.userData.height * 0.7);
  };

  async function armTo(rig, angle, seconds, easing = FX.ease.out, arm = "armR") {
    if (!rig[arm]) return FX.wait(seconds);
    const from = rig[arm].rotation.x;
    await FX.tween(seconds, t => { rig[arm].rotation.x = FX.lerp(from, angle, t); }, easing);
  }

  async function nudge(unit, dir, distance, seconds, easing = FX.ease.out) {
    const from = unit.position.clone();
    await FX.tween(seconds, t => { unit.position.copy(from).addScaledVector(dir, distance * t); }, easing);
  }

  // `combo` is the attacker's kill streak level (0 = a single kill); higher levels hit harder and slower.
  async function impact(attacker, victim, dir, style, onImpact, victimSq, combo = 0) {
    const color = ARMIES[attacker.userData.army].look.magic;
    const hitPoint = center(victim);
    if (combo > 0) {
      FX.slowmo(0.28, 0.6 + combo * 0.25);
      FX.burst(hitPoint, 0xffffff, 20 + combo * 10, 4, 1.2, 0.8);
      FX.shockwave(victim.position, color);
    }
    FX.burst(hitPoint, color, 30, 3.2);
    FX.burst(hitPoint, 0xffffff, 10, 2.2, 0.8, 0.4);
    FX.sfx(style === "strike" || style === "thrust" ? "clang" : "hit");
    FX.sfx("hit");
    FX.shake((style === "slam" || style === "lightning" ? 0.4 : 0.18) + combo * 0.08);
    if (["spell", "beam", "lightning", "slam"].includes(style) || combo > 1) FX.flashScreen(0.3);
    if (style === "slam") FX.shockwave(victim.position, color);
    onImpact?.();
    await nudge(victim, dir, 0.12, 0.07);
    const fallen = victim.position.clone();
    FX.shatter(victim, dir, (style === "slam" || style === "charge" ? 1.4 : 1) + combo * 0.2);
    const spill = Terrain.fall(victimSq, fallen, ARMIES[victim.userData.army].look.blood);
    if (blood) {
      FX.burst(hitPoint, spill.color, 16, 2.4, 0.9, 0.6);
      FX.splatter(fallen, spill.color, spill.amount * (1 + combo * 0.25));
    }
    War.ignite(victimSq);
  }

  // ---------------------------------------------------------------- capture scenes
  async function playCapture(fromSq, toSq, victimSq, caption, combo = 0) {
    const attacker = unitAt(fromSq);
    const victim = unitAt(victimSq);
    units[fromSq.row][fromSq.col] = null;
    units[victimSq.row][victimSq.col] = null;
    const rig = attacker.userData.rig;
    const style = attacker.userData.style;
    const A = attacker.position.clone();
    const V = victim.position.clone();
    const dir = V.clone().sub(A).setY(0).normalize();
    const color = ARMIES[attacker.userData.army].look.magic;
    const useCine = cinematics;
    attacker.userData.busy = true;

    if (useCine) {
      showCaption(caption.who, "");
      await enterCinematic(A, V);
    }
    turnTo(victim, angleTo(V, A), 0.3);
    await turnTo(attacker, angleTo(A, V), 0.3);
    const onImpact = () => {
      showCaption(caption.who, caption.text);
      caption.onImpact?.();
    };

    switch (style) {
      case "strike": {
        await travel(attacker, V.clone().addScaledVector(dir, -0.62));
        const rest = rig.armR?.rotation.x ?? 0;
        FX.sfx("whoosh");
        await armTo(rig, -2.7, 0.34);
        await Promise.all([armTo(rig, 0.25, 0.12, FX.ease.in), nudge(attacker, dir, 0.14, 0.12, FX.ease.in)]);
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        armTo(rig, rest, 0.35);
        break;
      }
      case "thrust": {
        await travel(attacker, V.clone().addScaledVector(dir, -0.72));
        const rest = rig.armR?.rotation.x ?? 0;
        await armTo(rig, -1.5, 0.2);
        await nudge(attacker, dir, -0.12, 0.22);
        FX.sfx("whoosh");
        await nudge(attacker, dir, 0.34, 0.1, FX.ease.in);
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        armTo(rig, rest, 0.35);
        break;
      }
      case "charge": {
        await travel(attacker, A.clone().addScaledVector(dir, -0.25), { seconds: 0.35 });
        FX.sfx("whoosh");
        await travel(attacker, V.clone().addScaledVector(dir, -0.45), { seconds: 0.2 + A.distanceTo(V) * 0.16, cadence: 24, easing: FX.ease.in });
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        break;
      }
      case "slam": {
        await Promise.all([armTo(rig, -2.9, 0.3), armTo(rig, -2.9, 0.3, FX.ease.out, "armL")]);
        await travel(attacker, V, { arc: 1.7, seconds: 0.7 });
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        FX.sfx("boom");
        armTo(rig, -0.4, 0.3);
        armTo(rig, -0.3, 0.3, FX.ease.out, "armL");
        break;
      }
      case "shoot": {
        const tank = ARMIES[attacker.userData.army].look.rook === "tank" && attacker.userData.type === "r";
        await armTo(rig, -1.45, 0.2);
        const shots = tank ? 1 : 4;
        for (let i = 0; i < shots; i += 1) {
          const tip = tipOf(attacker);
          FX.burst(tip, 0xffd27a, 8, 1.4, 0.7, 0.18);
          FX.sfx(tank ? "boom" : "shot");
          const flight = FX.projectile(tip, center(victim), tank ? 0xffa23a : color, tank ? "orb" : "tracer");
          nudge(attacker, dir, tank ? -0.15 : -0.03, 0.06);
          if (i === shots - 1) await flight;
          else await FX.wait(0.1);
        }
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        armTo(rig, -0.55, 0.3);
        break;
      }
      case "spell": {
        FX.sfx("spell");
        await armTo(rig, -2.3, 0.35);
        for (let i = 0; i < 3; i += 1) {
          FX.burst(tipOf(attacker), color, 8, 0.8, 0.8, 0.3);
          await FX.wait(0.12);
        }
        await FX.projectile(tipOf(attacker), center(victim), color, "orb");
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        armTo(rig, -0.55, 0.35);
        break;
      }
      case "beam": {
        FX.sfx("spell");
        for (let i = 0; i < 3; i += 1) {
          FX.burst(tipOf(attacker), color, 8, 0.8, 0.8, 0.3);
          await FX.wait(0.12);
        }
        FX.sfx("zap");
        const ray = FX.beam(tipOf(attacker), center(victim), color, 0.9);
        await FX.wait(0.35);
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        await ray;
        break;
      }
      case "lightning": {
        await armTo(rig, -2.95, 0.4);
        FX.sfx("zap");
        FX.flashScreen(0.5);
        FX.lightning(tipOf(attacker), center(victim), color, 4);
        await FX.lightning(V.clone().setY(7), center(victim), color, 5);
        await impact(attacker, victim, dir, style, onImpact, victimSq, combo);
        armTo(rig, -0.55, 0.35);
        break;
      }
    }

    await FX.wait(useCine ? 0.9 : 0.35);
    const dest = squareToWorld(toSq.row, toSq.col);
    if (attacker.position.distanceTo(dest) > 0.05) {
      await turnTo(attacker, angleTo(attacker.position, dest), 0.15);
      await travel(attacker, dest);
    }
    attacker.position.copy(dest);
    await Terrain.arrive(attacker, toSq);
    await turnTo(attacker, SIDE_FACING[attacker.userData.color]);
    attacker.userData.busy = false;
    attacker.userData.square = { ...toSq };
    units[toSq.row][toSq.col] = attacker;
    if (useCine) await exitCinematic();
    else hideCaption();
  }

  // Animate one engine move. `victimSq` is set for captures; `promote` builds the promoted unit.
  async function playMove({ move, victimSq = null, caption = null, promote = null, combo = 0 }) {
    const from = { row: move.fromRow, col: move.fromCol };
    const to = { row: move.toRow, col: move.toCol };
    if (victimSq) {
      await playCapture(from, to, victimSq, caption, combo);
    } else {
      const unit = unitAt(from);
      units[from.row][from.col] = null;
      await moveUnit(unit, squareToWorld(to.row, to.col), { hop: unit.userData.type === "n", sq: to });
      unit.userData.square = to;
      units[to.row][to.col] = unit;
    }
    if (move.castle) {
      const rookFrom = { row: move.fromRow, col: move.castle === "king" ? 7 : 0 };
      const rookTo = { row: move.fromRow, col: move.castle === "king" ? 5 : 3 };
      const rook = unitAt(rookFrom);
      units[rookFrom.row][rookFrom.col] = null;
      await moveUnit(rook, squareToWorld(rookTo.row, rookTo.col), { hop: true, sq: rookTo });
      rook.userData.square = rookTo;
      units[rookTo.row][rookTo.col] = rook;
    }
    if (promote) await playPromotion(to, promote());
  }

  async function playPromotion(sq, newUnit) {
    const old = unitAt(sq);
    const top = old.position.clone().setY(8);
    const color = ARMIES[old.userData.army].look.magic;
    FX.sfx("spell");
    const ray = FX.beam(top, old.position.clone().setY(0.1), color, 1.1);
    await FX.wait(0.45);
    FX.burst(center(old), color, 40, 2.5);
    FX.flashScreen(0.35);
    scene.remove(old);
    placeUnit(newUnit, sq.row, sq.col);
    newUnit.scale.setScalar(0.01);
    await FX.tween(0.5, t => newUnit.scale.setScalar(Math.max(0.01, t)), FX.ease.back);
    await ray;
  }

  async function playCheckmate(sq) {
    const king = unitAt(sq);
    if (!king) return;
    units[sq.row][sq.col] = null;
    king.userData.busy = true;
    FX.sfx("boom");
    FX.shake(0.25);
    await FX.tween(0.9, t => { king.userData.rig.actor.rotation.x = t * 1.45; }, FX.ease.in);
    FX.dust(king.position.clone().setY(0.1), 14);
    await FX.wait(0.35);
    FX.shatter(king, new V3(0, 0, 0), 1.2);
    FX.flashScreen(0.4);
  }

  const KILL_VERBS = {
    strike: ["cuts down", "cleaves through", "strikes down", "hews down"],
    thrust: ["runs through", "impales", "skewers"],
    charge: ["tramples", "rides down", "smashes through"],
    slam: ["crushes", "flattens", "pulverises"],
    shoot: ["guns down", "blasts apart", "riddles"],
    spell: ["unmakes", "incinerates", "withers"],
    beam: ["disintegrates", "vaporises", "burns away"],
    lightning: ["smites", "thunderstrikes", "reduces to cinders"]
  };
  const killVerb = style => {
    const verbs = KILL_VERBS[style] || KILL_VERBS.strike;
    return verbs[Math.floor(Math.random() * verbs.length)];
  };

  return {
    init, setTheme, syncAll, setMarkers, setView, toggleView, playMove, playCheckmate, killVerb,
    set cinematics(value) { cinematics = value; },
    get cinematics() { return cinematics; },
    get touch() { return coarse; },
    set blood(value) { blood = value; }
  };
})();
