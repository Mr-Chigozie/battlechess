"use strict";

// Online play against the Chess Wars server (server/ in this repo): accounts and guests, the War Room,
// challenges, shareable invite links, and live battles. The server referees every move and runs the clocks;
// each client replays the move list through the normal rules engine, so both boards always agree.
// The game flow itself lives in game3d.js (see the init hooks).
const Online = (() => {
  const $ = id => document.getElementById(id);
  const SERVER = typeof CHESSWARS_SERVER === "string" && CHESSWARS_SERVER ? CHESSWARS_SERVER.replace(/\/$/, "") : null;
  const TOKEN_KEY = "chesswars.token";

  let hooks = null;
  let token = null;
  let me = null;            // { id, name, army, guest, wins, losses, draws }
  let socket = null;
  let lobbyData = null;
  let onlineOnly = false;  // War Room: show only commanders who are online right now
  let mode = "login";
  let toastTimer = null;
  let pendingInvite = null; // invite code from the link this page was opened with
  let inviteInfo = null;
  let game = null;          // latest server view of the battle on screen
  let gameId = null;
  let receivedAt = 0;
  let clockTimer = null;

  const configured = () => Boolean(SERVER) && typeof io === "function";
  const isOpen = id => $(id).classList.contains("open");
  const armyLabel = id => (ARMIES[id] ? `${ARMIES[id].mark} ${ARMIES[id].name}` : "");
  const clockLabel = type => (type === "daily" ? "Daily · 1 move a day" : "Live · 10 min + 5 s");
  const store = value => { try { value ? localStorage.setItem(TOKEN_KEY, value) : localStorage.removeItem(TOKEN_KEY); } catch (e) { /* private mode */ } };
  const stored = () => { try { return localStorage.getItem(TOKEN_KEY); } catch (e) { return null; } };

  function toast(text) {
    const el = $("toast");
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove("show"), 3600);
  }

  // ---------------------------------------------------------------- talking to the server
  async function api(method, path, body) {
    let res;
    try {
      res = await fetch(SERVER + path, {
        method,
        headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
        body: body ? JSON.stringify(body) : undefined
      });
    } catch (e) {
      throw Object.assign(new Error("Can't reach the server. Check your connection."), { code: "offline" });
    }
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw Object.assign(new Error(data.message || "Something went wrong. Try again."), { code: data.error, status: res.status });
    return data;
  }

  // Socket messages are answered by the server: { ok, ... } or { error, message }.
  function emit(event, payload = {}) {
    return new Promise(resolve => {
      if (!socket) return resolve({ error: "offline", message: "Not connected to the server." });
      socket.timeout(8000).emit(event, payload, (err, reply) => {
        resolve(err ? { error: "timeout", message: "The server didn't answer. Check your connection." } : reply || {});
      });
    });
  }

  const track = (name, props) => {
    if (!configured()) return;
    fetch(`${SERVER}/api/events`, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      body: JSON.stringify({ name, props })
    }).catch(() => {});
  };

  function connect() {
    if (socket) socket.close();
    socket = io(SERVER, { auth: { token }, transports: ["websocket", "polling"] });
    socket.on("connect", () => {
      refreshLobby();
      // Back after a dropped connection: pick the battle on screen up where the server has it.
      if (gameId) rejoin(gameId);
    });
    socket.on("connect_error", error => {
      if (error.message === "unauthorized") signOutLocal("Please sign in again.");
    });
    socket.on("lobby", data => {
      lobbyData = data;
      me = data.me;
      renderAccount();
      renderLobby();
    });
    socket.on("game", view => {
      if (view.id !== gameId) return;
      setGame(view);
      hooks.onGame(view);
    });
    socket.on("game:started", ({ gameId: id }) => {
      FX.sfx("drums");
      if (!hooks.inGame()) {
        toast("Your challenge was accepted. To battle!");
        joinGame(id);
      } else {
        toast("A new battle is waiting for you in the War Room.");
      }
    });
    socket.on("challenge:incoming", c => {
      FX.sfx("drums");
      // In a menu: answer on the spot. Mid-battle (or already looking at a card): just a toast, nothing interrupted.
      const screen = document.querySelector(".screen.open");
      if (!c.id || !screen || screen.id === "card-screen") return toast(`⚔ ${c.fromName} challenges you! Open the War Room to answer.`);
      const back = () => (screen.id === "lobby-screen" ? openLobby() : hooks.showScreen(screen.id));
      hooks.showCard({
        eyebrow: "Challenge",
        title: `${c.fromName} challenges you!`,
        paragraphs: [`${armyLabel(c.fromArmy)} wants to battle you, and they lead as Ivory.`, clockLabel(c.clock)],
        buttons: [
          { label: "Decline", action: async () => { await answer(c.id, "decline"); back(); } },
          { label: "Later", action: back },
          { label: "Accept", primary: true, action: async () => { if (!(await answer(c.id, "accept"))) back(); } }
        ]
      });
    });
    socket.on("challenge:declined", ({ byName }) => toast(`${byName} declined your challenge.`));
  }

  async function refreshLobby() {
    const data = await emit("lobby:get");
    if (data.error) return;
    lobbyData = data;
    me = data.me;
    renderAccount();
    renderLobby();
  }

  // ---------------------------------------------------------------- setup
  function init(gameHooks) {
    hooks = gameHooks;
    for (const id of ["auth-army", "me-army"]) {
      for (const [key, info] of Object.entries(ARMIES)) $(id).add(new Option(`${info.mark} ${info.name}`, key));
    }
    $("auth-army").value = "knights";
    $("auth-tab-guest").addEventListener("click", () => setMode("guest"));
    $("auth-tab-login").addEventListener("click", () => setMode("login"));
    $("auth-tab-register").addEventListener("click", () => setMode("register"));
    $("auth-form").addEventListener("submit", event => { event.preventDefault(); submitAuth(); });
    $("auth-forgot").addEventListener("click", forgot);
    $("btn-logout").addEventListener("click", () => signOut().then(() => hooks.showScreen("menu-screen")));
    $("btn-upgrade").addEventListener("click", () => openAuth("upgrade"));
    $("btn-delete").addEventListener("click", () => openAuth("delete"));
    $("me-army").addEventListener("change", changeArmy);
    $("user-search").addEventListener("input", renderLobby);
    $("btn-online-only").addEventListener("click", () => { onlineOnly = !onlineOnly; renderLobby(); });
    $("btn-invite").addEventListener("click", createInvite);
    $("acct-login").addEventListener("click", () => openAuth("login"));
    $("acct-register").addEventListener("click", () => openAuth("register"));
    $("acct-logout").addEventListener("click", () => signOut());
    pendingInvite = new URLSearchParams(location.search).get("invite");
    setMode("login");
    renderAccount();
    if (!configured()) {
      if (pendingInvite) setTimeout(open, 600);
      return;
    }
    token = stored();
    start();
  }

  async function start() {
    if (token) {
      try {
        me = (await api("GET", "/api/me")).user;
        connect();
      } catch (e) {
        if (e.status === 401) signOutLocal();
      }
    }
    renderAccount();
    if (pendingInvite) handleInvite();
  }

  function open() {
    if (!configured()) {
      return hooks.showCard({
        eyebrow: "Online battle",
        title: "Online battles are coming soon",
        paragraphs: [
          "Accounts, the War Room and battles against friends are being switched on for the beta.",
          "Story mode and skirmish are ready to play right now."
        ],
        buttons: [{ label: "Main menu", primary: true, action: () => hooks.showScreen("menu-screen") }]
      });
    }
    track("online_opened");
    if (me) return openLobby();
    openAuth("login");
  }

  // ---------------------------------------------------------------- accounts
  function openAuth(next) {
    if (!configured()) return open();
    if (me && (next === "login" || next === "register")) return openLobby();
    setMode(next);
    hooks.showScreen("auth-screen");
  }

  // The main menu's account bar: buttons when signed out, the commander's name when signed in.
  function renderAccount() {
    const signedIn = Boolean(me);
    $("acct-login").hidden = signedIn;
    $("acct-register").hidden = signedIn;
    $("acct-logout").hidden = !signedIn;
    const name = $("account-name");
    name.replaceChildren();
    if (signedIn) {
      name.append(`${ARMIES[me.army]?.mark || ""} ${me.name}`);
      const record = document.createElement("small");
      record.textContent = `${me.guest ? "Guest · " : ""}${me.wins}W · ${me.losses}L · ${me.draws}D`;
      name.appendChild(record);
    }
  }

  // One form, several jobs: sign in, register, play as a guest, turn a guest into an account, delete.
  const MODES = {
    login:    { title: "Report for duty", submit: "Sign in", fields: ["email", "pass"], tabs: true, forgot: true },
    // (the beta code field appears only if the server says registration needs one)
    register: { title: "Enlist as a commander", submit: "Create account", fields: ["name", "email", "pass", "army"], tabs: true, terms: true },
    guest:    { title: "Accept the challenge", submit: "Play now", fields: ["name", "army"], tabs: true, terms: true },
    upgrade:  { title: "Keep your record", submit: "Create account", fields: ["name", "email", "pass"], terms: true },
    delete:   { title: "Delete your commander", submit: "Delete forever", fields: ["pass"] }
  };

  function setMode(next) {
    mode = next;
    const m = MODES[mode];
    const fields = m.fields.filter(f => !(mode === "delete" && f === "pass" && me?.guest));
    for (const f of ["name", "email", "pass", "code", "army"]) $(`f-${f}`).hidden = !fields.includes(f);
    $("auth-tabs").hidden = !m.tabs;
    $("auth-tab-guest").hidden = !pendingInvite;
    for (const t of ["guest", "login", "register"]) $(`auth-tab-${t}`).classList.toggle("active", mode === t);
    $("auth-title").textContent = m.title;
    $("auth-submit").textContent = m.submit;
    $("auth-submit").classList.toggle("danger", mode === "delete");
    $("auth-forgot").hidden = !m.forgot;
    $("auth-terms").hidden = !m.terms;
    $("auth-pass").autocomplete = mode === "login" || mode === "delete" ? "current-password" : "new-password";
    $("auth-pass").placeholder = mode === "login" || mode === "delete" ? "" : "At least 8 characters";
    if (mode === "upgrade" && me) $("auth-name").value = me.name;
    $("auth-error").textContent = "";
    $("auth-error").classList.remove("ok");
    const note = $("auth-invite");
    note.hidden = !(pendingInvite && inviteInfo && ["guest", "login", "register"].includes(mode));
    if (!note.hidden) {
      note.replaceChildren(`⚔ ${inviteInfo.fromName} challenges you to battle`);
      const small = document.createElement("small");
      small.textContent = `They lead ${armyLabel(inviteInfo.fromArmy)} · ${clockLabel(inviteInfo.clock)}. Pick a name to play as a guest, or sign in.`;
      note.appendChild(small);
    }
    if (mode === "delete") {
      note.hidden = false;
      note.replaceChildren("This wipes your name, email and record for good. Finished battles stay in your rivals' history under an anonymous name.");
    }
  }

  async function submitAuth() {
    const email = $("auth-email").value.trim();
    const password = $("auth-pass").value;
    const name = $("auth-name").value.trim();
    const error = $("auth-error");
    error.textContent = "";
    error.classList.remove("ok");
    const needs = MODES[mode].fields;
    if (needs.includes("name") && (name.length < 2 || name.length > 20)) { error.textContent = "Pick a commander name of 2 to 20 characters."; return; }
    if (needs.includes("email") && !email) { error.textContent = "Enter your email."; return; }
    if (needs.includes("pass") && !$("f-pass").hidden && !password) { error.textContent = "Enter your password."; return; }
    $("auth-submit").disabled = true;
    try {
      if (mode === "login") await signedIn(await api("POST", "/api/auth/login", { email, password }));
      else if (mode === "register") await signedIn(await api("POST", "/api/auth/register", { name, email, password, army: $("auth-army").value, code: $("auth-code").value.trim() }));
      else if (mode === "guest") await signedIn(await api("POST", "/api/auth/guest", { name, army: $("auth-army").value }));
      else if (mode === "upgrade") {
        me = (await api("POST", "/api/auth/upgrade", { name, email, password })).user;
        renderAccount();
        toast(`Account created. Welcome to the ranks, ${me.name}.`);
        openLobby();
      } else if (mode === "delete") {
        await api("DELETE", "/api/me", { password });
        signOutLocal();
        toast("Your commander has been deleted.");
        hooks.showScreen("menu-screen");
      }
      $("auth-pass").value = "";
    } catch (e) {
      if (e.code === "beta") $("f-code").hidden = false;
      error.textContent = e.message;
    } finally {
      $("auth-submit").disabled = false;
    }
  }

  async function signedIn({ token: t, user }) {
    token = t;
    store(t);
    me = user;
    renderAccount();
    connect();
    toast(`Welcome, ${me.name}.`);
    if (pendingInvite) return acceptInvite(pendingInvite);
    openLobby();
  }

  async function forgot() {
    const error = $("auth-error");
    const email = $("auth-email").value.trim();
    if (!email) { error.classList.remove("ok"); error.textContent = "Type your email above first, then tap “Forgot your password?” again."; return; }
    try {
      const r = await api("POST", "/api/auth/forgot", { email });
      error.classList.add("ok");
      error.textContent = r.message;
    } catch (e) {
      error.classList.remove("ok");
      error.textContent = e.message;
    }
  }

  function signOutLocal(message) {
    if (socket) socket.close();
    socket = null;
    token = null;
    me = null;
    lobbyData = null;
    store(null);
    renderAccount();
    if (message) toast(message);
  }

  async function signOut() {
    if (token) await api("POST", "/api/auth/logout").catch(() => {});
    signOutLocal();
  }

  async function changeArmy() {
    try {
      me = (await api("PATCH", "/api/me", { army: $("me-army").value })).user;
      renderAccount();
      renderLobby();
    } catch (e) {
      toast(e.message);
    }
  }

  // ---------------------------------------------------------------- invite links
  async function handleInvite() {
    const code = pendingInvite;
    try {
      inviteInfo = (await api("GET", `/api/invites/${encodeURIComponent(code)}`)).invite;
    } catch (e) {
      return inviteProblem(e.status === 404 ? "That invite link doesn't exist. Check you copied all of it." : e.message);
    }
    if (!inviteInfo.open) return inviteProblem("That invite has already been used or has expired. Ask your friend to send a new link.");
    if (me && inviteInfo.fromId === me.id) {
      clearInvite();
      return toast("That's your own invite link. Send it to a friend!");
    }
    if (me) {
      return hooks.showCard({
        eyebrow: "You've been challenged",
        title: `${inviteInfo.fromName} challenges you`,
        paragraphs: [`They lead ${armyLabel(inviteInfo.fromArmy)} as Ivory and move first. You'll command ${armyLabel(me.army)} as Onyx.`, clockLabel(inviteInfo.clock)],
        buttons: [
          { label: "Not now", action: () => { clearInvite(); hooks.showScreen("menu-screen"); } },
          { label: "To battle!", primary: true, action: () => acceptInvite(code) }
        ]
      });
    }
    track("guest_prompt_seen", { code });
    openAuth("guest");
  }

  function inviteProblem(text) {
    clearInvite();
    hooks.showCard({
      eyebrow: "Invite link",
      title: "This challenge can't be accepted",
      paragraphs: [text, "You can still play story mode and skirmish, or create your own invite from the War Room."],
      buttons: [{ label: "Main menu", primary: true, action: () => hooks.showScreen("menu-screen") }]
    });
  }

  function clearInvite() {
    pendingInvite = null;
    inviteInfo = null;
    const params = new URLSearchParams(location.search);
    params.delete("invite");
    const rest = params.toString();
    history.replaceState(null, "", location.pathname + (rest ? `?${rest}` : "") + location.hash);
  }

  async function acceptInvite(code) {
    const r = await emit("invite:accept", { code });
    if (r.error) {
      if (r.error === "own-invite") { clearInvite(); toast(r.message); return openLobby(); }
      return inviteProblem(r.message);
    }
    clearInvite();
    joinGame(r.gameId);
  }

  async function createInvite() {
    const r = await emit("invite:create", { clock: $("clock-select").value });
    if (r.error) return toast(r.message);
    shareInvite(r.url, "share");
  }

  // On phones the system share sheet (WhatsApp, SMS…); elsewhere copy the link.
  async function shareInvite(url, how) {
    const text = `${me.name} challenges you to a battle in Chess Wars! Tap to play, no download:`;
    if (how === "whatsapp") {
      track("invite_shared", { via: "whatsapp" });
      return window.open(`https://wa.me/?text=${encodeURIComponent(`${text} ${url}`)}`, "_blank", "noopener");
    }
    if (how === "share" && navigator.share && hooks.touch()) {
      try {
        await navigator.share({ title: "Chess Wars", text, url });
        track("invite_shared", { via: "share-sheet" });
        return;
      } catch (e) {
        if (e.name === "AbortError") return;
      }
    }
    try {
      await navigator.clipboard.writeText(url);
      track("invite_link_copied");
      toast("Invite link copied. Paste it to a friend!");
    } catch (e) {
      window.prompt("Copy this link and send it to a friend:", url);
    }
  }

  // ---------------------------------------------------------------- War Room
  function openLobby() {
    refreshLobby();
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
    if (u.online) return "online";
    const minutes = Math.round((Date.now() - u.lastSeen) / 60000);
    if (minutes < 60) return `seen ${Math.max(1, minutes)} min ago`;
    const hours = Math.round(minutes / 60);
    return hours < 48 ? `seen ${hours} h ago` : `seen ${Math.round(hours / 24)} days ago`;
  }

  const REASONS = {
    checkmate: "by checkmate", resign: "by resignation", timeout: "on time", agreement: "by agreement",
    stalemate: "stalemate", repetition: "by repetition", "fifty-move": "fifty-move rule", insufficient: "not enough pieces", aborted: "called off"
  };

  function renderLobby() {
    if (!me) return;
    const card = $("me-card");
    card.textContent = `${ARMIES[me.army]?.mark || ""} ${me.name}`;
    const record = document.createElement("small");
    record.textContent = `${me.wins} wins · ${me.losses} losses · ${me.draws} draws`;
    card.appendChild(record);
    if (document.activeElement !== $("me-army")) $("me-army").value = me.army;
    $("guest-note").hidden = !me.guest;
    $("btn-upgrade").hidden = !me.guest;
    if (!lobbyData) return;

    const challengeList = $("challenge-list");
    challengeList.replaceChildren();
    for (const c of lobbyData.incoming) {
      challengeList.appendChild(row("online", `${c.fromName} challenges you`, `${armyLabel(c.fromArmy)} as Ivory · ${clockLabel(c.clock)}`, [
        { label: "Accept", primary: true, action: () => answer(c.id, "accept") },
        { label: "Decline", action: () => answer(c.id, "decline") }
      ]));
    }
    for (const c of lobbyData.outgoing) {
      challengeList.appendChild(row("", `Waiting for ${c.toName}…`, `Challenge sent · ${clockLabel(c.clock)}`, [{ label: "Cancel", action: () => answer(c.id, "cancel") }]));
    }
    if (!challengeList.children.length) challengeList.appendChild(empty("No challenges yet. Challenge a commander, or invite a friend with a link."));

    const inviteList = $("invite-list");
    inviteList.replaceChildren();
    for (const inv of lobbyData.invites) {
      inviteList.appendChild(row("playing", "Open invite link", `${clockLabel(inv.clock)} · waiting for a friend`, [
        { label: "WhatsApp", action: () => shareInvite(inv.url, "whatsapp") },
        { label: "Copy", action: () => shareInvite(inv.url, "copy") },
        { label: "✕", action: () => emit("invite:cancel", { code: inv.code }).then(refreshLobby) }
      ]));
    }

    const gameList = $("game-list");
    gameList.replaceChildren();
    const sorted = [...lobbyData.games].sort((a, b) => b.updatedAt - a.updatedAt);
    for (const g of sorted.filter(g => g.status === "active")) {
      gameList.appendChild(row(g.myTurn ? "online" : "playing", `vs ${g.rival}`,
        `${g.myTurn ? "Your move" : "Their move"} · move ${Math.floor(g.ply / 2) + 1} · ${g.clock === "daily" ? "daily" : "live"}`,
        [{ label: "Resume", primary: g.myTurn, action: () => joinGame(g.id) }]));
    }
    for (const g of sorted.filter(g => g.status === "finished")) {
      const outcome = g.reason === "aborted" ? "Called off" : g.result === "draw" ? "Draw" : g.result === g.myColor ? "Victory" : "Defeat";
      gameList.appendChild(row("", `${outcome} vs ${g.rival}`, REASONS[g.reason] || g.reason || "", []));
    }
    if (!gameList.children.length) gameList.appendChild(empty("No battles yet."));

    const filter = $("user-search").value.trim().toLowerCase();
    const onlineNow = lobbyData.users.filter(u => u.online).length;
    const others = lobbyData.users.filter(u => (!filter || u.name.toLowerCase().includes(filter)) && (!onlineOnly || u.online))
      .sort((a, b) => Number(b.online) - Number(a.online) || Number(a.playing) - Number(b.playing) || b.lastSeen - a.lastSeen);
    $("online-count").textContent = `(${onlineNow} online)`;
    $("btn-online-only").textContent = onlineOnly ? "Everyone" : `Online now (${onlineNow})`;
    $("btn-online-only").classList.toggle("primary", onlineOnly);
    const userList = $("user-list");
    userList.replaceChildren();
    for (const u of others) {
      const pending = lobbyData.outgoing.some(c => c.toId === u.id);
      userList.appendChild(row(u.playing ? "playing" : u.online ? "online" : "",
        `${ARMIES[u.army]?.mark || ""} ${u.name}${u.guest ? " (guest)" : ""}`,
        `${ARMIES[u.army]?.name || ""} · ${u.wins}W ${u.losses}L ${u.draws}D · ${lastSeenText(u)}`,
        [
          { label: pending ? "Sent" : "Challenge", primary: !pending && u.online, disabled: pending, action: () => challenge(u) },
          { label: "⋯", action: () => userMenu(u) }
        ]));
    }
    if (!others.length) userList.appendChild(empty(filter ? "No commanders match that name."
      : onlineOnly ? "Nobody else is online right now. Invite a friend with a link!" : "No other commanders yet. Invite a friend with a link!"));
  }

  async function challenge(target) {
    const r = await emit("challenge:send", { to: target.id, clock: $("clock-select").value });
    toast(r.error ? r.message : `Challenge sent to ${target.name}.`);
  }

  async function answer(id, action) {
    const r = await emit("challenge:answer", { id, action });
    if (r.error) { toast(r.message); return false; }
    if (r.gameId) joinGame(r.gameId);
    return Boolean(r.gameId);
  }

  function userMenu(u) {
    hooks.showCard({
      eyebrow: "Commander",
      title: u.name,
      paragraphs: [`${armyLabel(u.army)} · ${u.wins}W ${u.losses}L ${u.draws}D`],
      buttons: [
        { label: "Back", action: openLobby },
        { label: "Report", action: () => reportUser(u.id, u.name, null, openLobby) },
        { label: "Block", primary: true, action: async () => {
          const r = await emit("user:block", { id: u.id });
          toast(r.error ? r.message : `${u.name} is blocked. They can't see or challenge you.`);
          openLobby();
        } }
      ]
    });
  }

  function reportUser(id, name, game, back) {
    const send = reason => async () => {
      const r = await emit("user:report", { id, gameId: game, reason });
      toast(r.error ? r.message : "Thanks. The report has been sent to the moderators.");
      back();
    };
    hooks.showCard({
      eyebrow: "Report",
      title: `Report ${name}`,
      paragraphs: ["What's the problem?"],
      buttons: [
        { label: "Offensive name", action: send("Offensive name") },
        { label: "Stalling or abandoning", action: send("Stalling or abandoning battles") },
        { label: "Something else", action: send("Other") },
        { label: "Cancel", primary: true, action: back }
      ]
    });
  }

  // ---------------------------------------------------------------- battles
  function setGame(view) {
    game = view;
    receivedAt = Date.now();
    renderClocks();
  }

  async function joinGame(id) {
    const r = await emit("game:join", { id });
    if (r.error) return toast(r.message);
    gameId = id;
    setGame(r.game);
    hooks.startGame(r.game, r.game.white === me.id ? "w" : "b");
    clearInterval(clockTimer);
    clockTimer = setInterval(renderClocks, 250);
  }

  async function rejoin(id) {
    const r = await emit("game:join", { id });
    if (r.error || id !== gameId) return;
    setGame(r.game);
    hooks.onGame(r.game);
  }

  function leaveGame() {
    if (gameId) emit("game:leave");
    gameId = null;
    game = null;
    clearInterval(clockTimer);
    $("clocks").hidden = true;
  }

  async function sendMove(id, ply, move) {
    const r = await emit("game:move", { id, ply, move });
    if (!r.error) return true;
    toast(r.error === "finished" ? r.message : `${r.message} Resyncing the board…`);
    hooks.resync();
    return false;
  }

  function sendTaunt(id, text) {
    emit("game:taunt", { id, text });
  }

  async function resign(id) {
    const r = await emit("game:resign", { id });
    if (r.error) throw new Error(r.message);
  }

  async function draw(id, action) {
    const r = await emit("game:draw", { id, action });
    if (r.error) toast(r.message);
    else if (action === "offer") toast("Draw offered. Waiting for your rival's answer.");
  }

  function clockText(ms, type) {
    const t = Math.max(0, ms);
    if (type === "daily" && t >= 3600000) return `${Math.floor(t / 3600000)}h ${Math.floor((t % 3600000) / 60000)}m`;
    const s = Math.ceil(t / 1000);
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
  }

  function renderClocks() {
    const box = $("clocks");
    if (!game || !me) { box.hidden = true; return; }
    box.hidden = false;
    const c = game.clock;
    const mine = game.white === me.id ? "w" : "b";
    const active = game.status === "active";
    const elapsed = active ? Date.now() - receivedAt : 0;
    const left = color => (color === "w" ? c.whiteMs : c.blackMs) - (c.running && c.turn === color ? elapsed : 0);
    const firstMoveLeft = active && !c.running && c.deadline ? c.deadline - (c.serverNow + elapsed) : null;
    for (const [el, color, who] of [[$("clock-me"), mine, "You"], [$("clock-rival"), mine === "w" ? "b" : "w", "Rival"]]) {
      const toMove = active && c.turn === color;
      const ms = toMove && firstMoveLeft !== null ? firstMoveLeft : left(color);
      el.textContent = toMove && firstMoveLeft !== null ? `${who}: first move ${clockText(ms, "live")}` : `${who} ${clockText(ms, c.type)}`;
      el.classList.toggle("active", toMove);
      el.classList.toggle("low", toMove && ms < (c.type === "daily" ? 3600000 : 30000));
    }
  }

  return {
    init, open, joinGame, leaveGame, sendMove, sendTaunt, resign, draw, reportUser, toast,
    get uid() { return me?.id ?? null; },
    get guest() { return Boolean(me?.guest); },
    openUpgrade: () => openAuth("upgrade")
  };
})();
