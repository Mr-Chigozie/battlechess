"use strict";

// The generated character models (art/export/*.glb, built by art/blender/build_units.py) on the battlefield.
// An army's six models load the first time it takes the field; until then (or if a file fails) its units keep
// the hand-built look from models3d.js. Each model plays its own clips through an AnimationMixer:
// Idle, Walk, Attack, Shoot, Death, and Behead for the humanoids.
const Units = (() => {
  const PREFIX = { knights: "Knights", undead: "Undead", machines: "Machines", samurai: "Samurai",
    asgardians: "Asgard", vikings: "Vikings", spartans: "Spartans", marines: "Marines" };
  const PIECE = { p: "Pawn", n: "Knight", b: "Bishop", r: "Rook", q: "Queen", k: "King" };
  const SCALE = 0.8;                           // the models are built for 1.2-unit squares; the board's are 1.0
  const TEAM = { w: 0xefe6d2, b: 0x2a2522 };   // base colour of each side
  const HIT = 14 / 30;                         // seconds into the Attack clip when the blow lands
  const LOOPS = new Set(["Idle", "Walk"]);
  const ENDINGS = new Set(["Death", "Behead"]);

  const loader = new GLTFLoader();
  const models = new Map();    // "army/type" -> loaded glTF, or null if it could not load
  const loading = new Map();   // army -> Promise
  const teamMats = new Map();  // material uuid + side -> recoloured copy
  const live = new Set();      // built units whose mixers we drive

  // Start (or reuse) the download of one army's six models.
  function preload(army) {
    if (!PREFIX[army]) return Promise.resolve();
    if (!loading.has(army)) {
      loading.set(army, Promise.all(Object.entries(PIECE).map(([type, piece]) =>
        loader.loadAsync(`art/export/${PREFIX[army]}_${piece}.glb`)
          .then(gltf => {
            // The battlefield has no reflections for metal to pick up, so full metal reads as black: tone it down.
            gltf.scene.traverse(obj => {
              if (!obj.isMesh || obj.material.userData.toned) return;
              obj.material.metalness *= 0.35;
              obj.material.roughness = Math.max(obj.material.roughness, 0.45);
              obj.material.userData.toned = true;
            });
            models.set(`${army}/${type}`, gltf);
          })
          .catch(error => {
            console.warn(`No model for ${army}/${type}; keeping the built-in look.`, error);
            models.set(`${army}/${type}`, null);
          }))));
    }
    return loading.get(army);
  }

  const ready = (army, type) => !!models.get(`${army}/${type}`);

  function teamMaterial(material, color) {
    const key = material.uuid + color;
    if (!teamMats.has(key)) {
      const copy = material.clone();
      copy.color.setHex(TEAM[color]);
      teamMats.set(key, copy);
    }
    return teamMats.get(key);
  }

  // glTF node names lose their dots ("hand.R" -> "handR").
  const bone = (model, name) => model.getObjectByName(name) || model.getObjectByName(name.replace(".", ""));

  function build(army, type, color) {
    const gltf = models.get(`${army}/${type}`);
    const model = SkeletonUtils.clone(gltf.scene);
    model.scale.setScalar(SCALE);
    model.traverse(obj => {
      if (!obj.isMesh) return;
      obj.castShadow = true;
      obj.receiveShadow = true;
      obj.frustumCulled = false;  // the bind-pose bounds don't follow the animation
      if (obj.material.name.startsWith("Team_")) obj.material = teamMaterial(obj.material, color);
    });
    // Same shape as a hand-built unit: unit > actor (leans and topples) > body (bobs) > model.
    const unit = new THREE.Group();
    const actor = new THREE.Group();
    const body = new THREE.Group();
    unit.add(actor);
    actor.add(body);
    body.add(model);
    const mixer = new THREE.AnimationMixer(model);
    const actions = {};
    for (const clip of gltf.animations) actions[clip.name.split("|").pop()] = mixer.clipAction(clip);
    // Where shots and spells leave from: the weapon hand, a beast's jaws, or the top of a tower.
    const beast = (army === "undead" || army === "machines") && type === "n";
    const tip = bone(model, beast ? "head_q" : type === "r" ? "neck" : "hand.R") || model;
    const rig = { actor, body, baseY: 0, head: actions.Behead ? model : null, tip, mixer, actions, current: null };
    unit.updateMatrixWorld(true);
    const height = new THREE.Box3().setFromObject(unit).max.y;
    unit.userData = { army, type, color, rig, style: Models.attackStyle(army, type), height, model: true };
    // One-off clips (attacks, shots) settle back into idle; deaths hold their last frame.
    mixer.addEventListener("finished", event => {
      if (event.action === rig.current && !ENDINGS.has(event.action.getClip().name)) play(unit, "Idle");
    });
    play(unit, "Idle", { from: Math.random() * 2, fade: 0 });
    live.add(unit);
    return unit;
  }

  // Cross-fade a unit into a clip. Returns the clip length in seconds (0 if the unit has no such clip).
  function play(unit, name, { from = 0, fade = 0.18 } = {}) {
    const rig = unit?.userData.rig;
    const next = rig?.actions?.[name];
    if (!next) return 0;
    const loop = LOOPS.has(name);
    if (rig.current === next && loop) return next.getClip().duration;
    next.reset();
    next.setLoop(loop ? THREE.LoopRepeat : THREE.LoopOnce, Infinity);
    next.clampWhenFinished = !loop;
    next.time = from;
    if (rig.current && rig.current !== next) rig.current.fadeOut(fade);
    next.fadeIn(fade).play();
    rig.current = next;
    return next.getClip().duration;
  }

  // Swing so the blow lands `seconds` from now (to meet the impact effects).
  function strikeAt(unit, seconds) {
    if (!unit.userData.model) return;
    if (seconds >= HIT) FX.wait(seconds - HIT).then(() => play(unit, "Attack"));
    else play(unit, "Attack", { from: HIT - seconds });
  }

  // Extra wind-up before a unit's first shot: the sniper has to get down on his belly.
  const aimDelay = unit => (unit.userData.model && unit.userData.army === "marines" && unit.userData.type === "b" ? 0.6 : 0);

  // Advance every unit's animation by game time (`dt` already scaled by slow motion and hit-stop).
  function update(dt) {
    for (const unit of live) {
      if (unit.parent) {
        unit.userData.seen = true;
        unit.userData.rig.mixer.update(dt);
      } else if (unit.userData.seen) {
        live.delete(unit);
      }
    }
  }

  return { preload, ready, build, play, strikeAt, aimDelay, update };
})();
