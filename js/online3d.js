"use strict";

// Online play over Firebase: accounts, the commander list with presence, challenges, and live games.
// A game is a Firestore doc holding the move list; each client replays new moves through the normal
// rules engine, so both boards always agree. The game flow itself lives in game3d.js (see init hooks).
const Online = (() => {
  const $ = id => document.getElementById(id);
  const ONLINE_WINDOW_MS = 75000;
  const HEARTBEAT_MS = 25000;

  let hooks = null;
  let auth = null;
  let db = null;
  let FV = null;
  let user = null;
  let profile = null;
  let pendingProfile = null;
  let users = [];
  let incoming = [];
  let outgoing = [];
  let games = [];
  let lobbySubs = [];
  let gameSub = null;
  let heartbeat = null;
  let mode = "login";
  let toastTimer = null;
  const handledChallenges = new Set();

  const configured = () => typeof FIREBASE_CONFIG !== "undefined" && Boolean(FIREBASE_CONFIG) && Boolean(window.firebase);
  const isOpen = id => $(id).classList.contains("open");
  const millis = value => value?.toMillis ? value.toMillis() : value instanceof Date ? value.getTime() : typeof value === "number" ? value : 0;
  const isOnline = u => u.id === user?.uid || Date.now() - millis(u.lastSeen) < ONLINE_WINDOW_MS;
  const armyLabel = id => ARMIES[id] ? `${ARMIES[id].mark} ${ARMIES[id].name}` : "";

  function toast(text) {
    const el = $("toast");
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove("show"), 3200);
  }

  // ---------------------------------------------------------------- setup
  function init(gameHooks) {
    hooks = gameHooks;
    for (const id of ["auth-army", "me-army"]) {
      for (const [key, info] of Object.entries(ARMIES)) $(id).add(new Option(`${info.mark} ${info.name}`, key));
    }
    $("auth-army").value = "knights";
    $("auth-tab-login").addEventListener("click", () => setMode("login"));
    $("auth-tab-register").addEventListener("click", () => setMode("register"));
    $("auth-form").addEventListener("submit", event => { event.preventDefault(); submitAuth(); });
    $("btn-logout").addEventListener("click", () => auth?.signOut().then(() => hooks.showScreen("menu-screen")));
    $("me-army").addEventListener("change", () => updateProfile({ army: $("me-army").value }));
    $("user-search").addEventListener("input", renderLobby);
    setMode("login");
    if (!configured()) return;
    firebase.initializeApp(FIREBASE_CONFIG);
    auth = firebase.auth();
    db = firebase.firestore();
    FV = firebase.firestore.FieldValue;
    auth.onAuthStateChanged(u => { onAuth(u).catch(error => { console.error(error); toast("Couldn't load your profile. Check your connection."); }); });
    window.addEventListener("beforeunload", () => { if (user) db.collection("users").doc(user.uid).update({ lastSeen: new Date(0), playing: null }); });
    setInterval(() => { if (isOpen("lobby-screen")) renderLobby(); }, 30000);
  }

  function open() {
    if (!configured()) {
      return hooks.showCard({
        eyebrow: "Online battle",
        title: "Online play isn't switched on yet",
        paragraphs: [
          "Accounts, the commander list and live matches need a free Firebase project.",
          "The site owner adds the project's details to js/firebase-config.js; the steps are written at the top of that file. Story mode and skirmish work without it."
        ],
        buttons: [{ label: "Main menu", primary: true, action: () => hooks.showScreen("menu-screen") }]
      });
    }
    if (user && profile) return openLobby();
    setMode("login");
    hooks.showScreen("auth-screen");
  }

  // ---------------------------------------------------------------- accounts
  function setMode(next) {
    mode = next;
    $("auth-form").classList.toggle("login", mode === "login");
    $("auth-tab-login").classList.toggle("active", mode === "login");
    $("auth-tab-register").classList.toggle("active", mode === "register");
    $("auth-title").textContent = mode === "login" ? "Report for duty" : "Enlist as a commander";
    $("auth-submit").textContent = mode === "login" ? "Sign in" : "Create account";
    $("auth-pass").autocomplete = mode === "login" ? "current-password" : "new-password";
    $("auth-error").textContent = "";
  }

  const AUTH_ERRORS = {
    "auth/email-already-in-use": "That email already has an account. Sign in instead.",
    "auth/invalid-email": "That email address doesn't look right.",
    "auth/weak-password": "Passwords need at least 6 characters.",
    "auth/invalid-credential": "Wrong email or password.",
    "auth/wrong-password": "Wrong email or password.",
    "auth/user-not-found": "No account with that email. Register first.",
    "auth/too-many-requests": "Too many attempts. Wait a minute and try again.",
    "auth/network-request-failed": "Can't reach the server. Check your connection.",
    "auth/operation-not-allowed": "Email sign-in isn't enabled in the Firebase project yet."
  };

  async function submitAuth() {
    const email = $("auth-email").value.trim();
    const password = $("auth-pass").value;
    const name = $("auth-name").value.trim();
    const error = $("auth-error");
    error.textContent = "";
    if (mode === "register" && (name.length < 2 || name.length > 20)) { error.textContent = "Pick a commander name of 2 to 20 characters."; return; }
    if (!email || !password) { error.textContent = "Enter your email and password."; return; }
    $("auth-submit").disabled = true;
    try {
      if (mode === "register") {
        pendingProfile = { name, army: $("auth-army").value };
        await auth.createUserWithEmailAndPassword(email, password);
      } else {
        await auth.signInWithEmailAndPassword(email, password);
      }
      $("auth-pass").value = "";
    } catch (e) {
      error.textContent = AUTH_ERRORS[e.code] || e.message || "Something went wrong. Try again.";
    } finally {
      $("auth-submit").disabled = false;
    }
  }

  async function onAuth(u) {
    user = u;
    stopLobby();
    clearInterval(heartbeat);
    if (!u) { profile = null; return; }
    const ref = db.collection("users").doc(u.uid);
    let snap = await ref.get();
    if (!snap.exists) {
      const fresh = pendingProfile || { name: (u.email || "Commander").split("@")[0].slice(0, 20), army: "knights" };
      await ref.set({
        name: fresh.name, nameLower: fresh.name.toLowerCase(), army: fresh.army, email: u.email || "",
        wins: 0, losses: 0, draws: 0, createdAt: FV.serverTimestamp(), lastSeen: FV.serverTimestamp(), playing: null
      });
      snap = await ref.get();
    }
    pendingProfile = null;
    profile = { id: u.uid, ...snap.data() };
    beat();
    heartbeat = setInterval(beat, HEARTBEAT_MS);
    startLobby();
    if (isOpen("auth-screen")) {
      toast(`Welcome, ${profile.name}.`);
      openLobby();
    }
  }

  function beat() {
    if (!user) return;
    db.collection("users").doc(user.uid).update({ lastSeen: FV.serverTimestamp(), playing: hooks.currentGameId() || null }).catch(() => {});
  }

  function updateProfile(changes) {
    if (!user) return;
    Object.assign(profile, changes);
    db.collection("users").doc(user.uid).update(changes).catch(() => toast("Couldn't save that change."));
  }

  // ---------------------------------------------------------------- lobby
  function startLobby() {
    const uid = user.uid;
    let firstOutgoing = true;
    let firstIncoming = true;
    lobbySubs.push(db.collection("users").orderBy("lastSeen", "desc").limit(100).onSnapshot(snap => {
      users = snap.docs.map(d => ({ id: d.id, ...d.data() }));
      const mine = users.find(u => u.id === uid);
      if (mine) profile = mine;
      renderLobby();
    }, () => {}));
    lobbySubs.push(db.collection("challenges").where("to", "==", uid).onSnapshot(snap => {
      const known = new Set(incoming.map(c => c.id));
      incoming = snap.docs.map(d => ({ id: d.id, ...d.data() })).filter(c => c.status === "pending");
      for (const c of incoming) {
        if (!firstIncoming && !known.has(c.id)) {
          toast(`⚔ ${c.fromName} challenges you! Open the War Room to answer.`);
          FX.sfx("drums");
        }
      }
      firstIncoming = false;
      renderLobby();
    }, () => {}));
    lobbySubs.push(db.collection("challenges").where("from", "==", uid).onSnapshot(snap => {
      outgoing = snap.docs.map(d => ({ id: d.id, ...d.data() }));
      for (const c of outgoing) {
        if (handledChallenges.has(c.id) || c.status === "pending") continue;
        handledChallenges.add(c.id);
        if (firstOutgoing) continue; // answered before this session: don't replay old news
        if (c.status === "accepted" && c.gameId) {
          toast(`${c.toName} accepted your challenge!`);
          if (!hooks.inGame()) joinGame(c.gameId);
        } else if (c.status === "declined") {
          toast(`${c.toName} declined your challenge.`);
        }
      }
      firstOutgoing = false;
      renderLobby();
    }, () => {}));
    lobbySubs.push(db.collection("games").where("players", "array-contains", uid).onSnapshot(snap => {
      games = snap.docs.map(d => ({ id: d.id, ...d.data() }));
      renderLobby();
    }, () => {}));
  }

  function stopLobby() {
    lobbySubs.forEach(unsubscribe => unsubscribe());
    lobbySubs = [];
    users = incoming = outgoing = games = [];
  }

  function openLobby() {
    renderLobby();
    hooks.showScreen("lobby-screen");
  }

  function row(dotClass, name, meta, actions) {
    const item = document.createElement("div");
    item.className = "row-item";
    const dot = document.createElement("span");
    dot.className = `dot ${dotClass}`;
    const who = document.createElement("div");
    who.className = "who";
    const title = document.createElement("div");
    title.className = "name";
    title.textContent = name;
    const sub = document.createElement("div");
    sub.className = "meta";
    sub.textContent = meta;
    who.append(title, sub);
    const buttons = document.createElement("div");
    buttons.className = "row-actions";
    for (const { label, action, primary, disabled } of actions) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `btn small${primary ? " primary" : ""}`;
      button.textContent = label;
      button.disabled = Boolean(disabled);
      button.addEventListener("click", action);
      buttons.appendChild(button);
    }
    item.append(dot, who, buttons);
    return item;
  }

  function empty(text) {
    const el = document.createElement("div");
    el.className = "empty";
    el.textContent = text;
    return el;
  }

  function lastSeenText(u) {
    if (u.playing) return "in battle";
    if (isOnline(u)) return "online";
    const ms = millis(u.lastSeen);
    if (!ms) return "offline";
    const minutes = Math.round((Date.now() - ms) / 60000);
    if (minutes < 60) return `seen ${minutes} min ago`;
    const hours = Math.round(minutes / 60);
    return hours < 48 ? `seen ${hours} h ago` : `seen ${Math.round(hours / 24)} days ago`;
  }

  function renderLobby() {
    if (!profile || !user) return;
    const me = $("me-card");
    me.textContent = `${ARMIES[profile.army]?.mark || ""} ${profile.name}`;
    const record = document.createElement("small");
    record.textContent = `${profile.wins || 0} wins · ${profile.losses || 0} losses · ${profile.draws || 0} draws`;
    me.appendChild(record);
    if (document.activeElement !== $("me-army")) $("me-army").value = profile.army;

    const challengeList = $("challenge-list");
    challengeList.replaceChildren();
    for (const c of incoming) {
      challengeList.appendChild(row("online", `${c.fromName} challenges you`, `They lead ${armyLabel(c.fromArmy)} as Ivory; you'd be Onyx`, [
        { label: "Accept", primary: true, action: () => accept(c) },
        { label: "Decline", action: () => answer(c, "declined") }
      ]));
    }
    for (const c of outgoing.filter(c => c.status === "pending")) {
      challengeList.appendChild(row("", `Waiting for ${c.toName}…`, "Challenge sent", [{ label: "Cancel", action: () => answer(c, "cancelled") }]));
    }
    if (!challengeList.children.length) challengeList.appendChild(empty("No challenges yet. Pick a commander to challenge."));

    const gameList = $("game-list");
    gameList.replaceChildren();
    const sorted = [...games].sort((a, b) => millis(b.updatedAt) - millis(a.updatedAt));
    for (const g of sorted.filter(g => g.status === "active")) {
      const myColor = g.white === user.uid ? "w" : "b";
      const rival = myColor === "w" ? g.blackName : g.whiteName;
      const myTurn = (g.moves.length % 2 === 0) === (myColor === "w");
      gameList.appendChild(row(myTurn ? "online" : "playing", `vs ${rival}`, `${myTurn ? "Your move" : "Their move"} · move ${Math.floor(g.moves.length / 2) + 1}`, [
        { label: "Resume", primary: myTurn, action: () => joinGame(g.id) }
      ]));
    }
    for (const g of sorted.filter(g => g.status === "finished").slice(0, 5)) {
      const myColor = g.white === user.uid ? "w" : "b";
      const rival = myColor === "w" ? g.blackName : g.whiteName;
      const outcome = g.result === "draw" ? "Draw" : g.result === myColor ? "Victory" : "Defeat";
      gameList.appendChild(row("", `${outcome} vs ${rival}`, g.reason === "resign" ? "by resignation" : g.reason || "", []));
    }
    if (!gameList.children.length) gameList.appendChild(empty("No battles yet."));

    const filter = $("user-search").value.trim().toLowerCase();
    const others = users.filter(u => u.id !== user.uid && (!filter || (u.nameLower || u.name.toLowerCase()).includes(filter)))
      .sort((a, b) => Number(isOnline(b)) - Number(isOnline(a)) || millis(b.lastSeen) - millis(a.lastSeen));
    $("online-count").textContent = `(${users.filter(u => u.id !== user.uid && isOnline(u)).length} online)`;
    const userList = $("user-list");
    userList.replaceChildren();
    for (const u of others) {
      const pending = outgoing.some(c => c.to === u.id && c.status === "pending");
      userList.appendChild(row(u.playing && isOnline(u) ? "playing" : isOnline(u) ? "online" : "",
        `${ARMIES[u.army]?.mark || ""} ${u.name}`,
        `${ARMIES[u.army]?.name || ""} · ${u.wins || 0}W ${u.losses || 0}L ${u.draws || 0}D · ${lastSeenText(u)}`,
        [{ label: pending ? "Sent" : "Challenge", primary: !pending && isOnline(u), disabled: pending, action: () => challenge(u) }]));
    }
    if (!others.length) userList.appendChild(empty(filter ? "No commanders match that name." : "No other commanders yet. Share the link with a friend!"));
  }

  // ---------------------------------------------------------------- challenges
  async function challenge(target) {
    try {
      await db.collection("challenges").add({
        from: user.uid, fromName: profile.name, fromArmy: profile.army,
        to: target.id, toName: target.name, status: "pending", gameId: null, createdAt: FV.serverTimestamp()
      });
      toast(`Challenge sent to ${target.name}.`);
    } catch (e) {
      toast("Couldn't send the challenge. Try again.");
    }
  }

  async function answer(c, status) {
    try {
      await db.collection("challenges").doc(c.id).update({ status });
    } catch (e) {
      toast("Couldn't update the challenge.");
    }
  }

  // The challenger plays Ivory (moves first) with their army; whoever accepts plays Onyx with theirs.
  async function accept(c) {
    try {
      const ref = await db.collection("games").add({
        players: [c.from, user.uid], white: c.from, black: user.uid,
        whiteName: c.fromName, blackName: profile.name, whiteArmy: c.fromArmy, blackArmy: profile.army,
        moves: [], status: "active", result: null, reason: null, taunt: null,
        createdAt: FV.serverTimestamp(), updatedAt: FV.serverTimestamp()
      });
      await db.collection("challenges").doc(c.id).update({ status: "accepted", gameId: ref.id });
      joinGame(ref.id);
    } catch (e) {
      console.error(e);
      toast("Couldn't start the battle. Try again.");
    }
  }

  // ---------------------------------------------------------------- live game
  async function joinGame(id) {
    const snap = await db.collection("games").doc(id).get();
    if (!snap.exists) return toast("That battle no longer exists.");
    const game = { id, ...snap.data() };
    leaveGame();
    hooks.startGame(game, game.white === user.uid ? "w" : "b");
    gameSub = db.collection("games").doc(id).onSnapshot(s => { if (s.exists) hooks.onGame({ id, ...s.data() }); }, () => toast("Lost connection to the battle."));
    beat();
  }

  function leaveGame() {
    if (gameSub) gameSub();
    gameSub = null;
  }

  // Append a move only if the server's list is exactly as long as ours; otherwise the boards diverged.
  async function sendMove(gameId, ply, move) {
    const ref = db.collection("games").doc(gameId);
    try {
      await db.runTransaction(async tx => {
        const snap = await tx.get(ref);
        const data = snap.data();
        if (data.status !== "active") throw new Error("finished");
        if (data.moves.length !== ply) throw new Error("out-of-sync");
        tx.update(ref, { moves: [...data.moves, move], updatedAt: FV.serverTimestamp() });
      });
      return true;
    } catch (e) {
      toast(e.message === "finished" ? "This battle is already over." : "Your move didn't reach the server. Resyncing the board…");
      hooks.resync();
      return false;
    }
  }

  function sendTaunt(gameId, text) {
    db.collection("games").doc(gameId).update({ taunt: { by: user.uid, text, at: Date.now() } }).catch(() => {});
  }

  function resign(gameId, myColor) {
    return db.collection("games").doc(gameId).update({ status: "finished", result: myColor === "w" ? "b" : "w", reason: "resign", updatedAt: FV.serverTimestamp() });
  }

  async function finishGame(gameId, result, reason) {
    const ref = db.collection("games").doc(gameId);
    await db.runTransaction(async tx => {
      const snap = await tx.get(ref);
      if (snap.data().status === "active") tx.update(ref, { status: "finished", result, reason, updatedAt: FV.serverTimestamp() });
    }).catch(() => {});
  }

  function recordResult(outcome) {
    const field = { win: "wins", loss: "losses", draw: "draws" }[outcome];
    if (user && field) db.collection("users").doc(user.uid).update({ [field]: FV.increment(1) }).catch(() => {});
  }

  return {
    init, open, joinGame, leaveGame, sendMove, sendTaunt, resign, finishGame, recordResult, toast,
    get uid() { return user?.uid || null; }
  };
})();
