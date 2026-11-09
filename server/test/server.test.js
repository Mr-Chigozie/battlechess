"use strict";

// End-to-end server tests against a real MySQL test database (DB_NAME=chesswars_test).
// Run: npm test   (needs the local MySQL running; uses the credentials in server/.env)
process.env.NODE_ENV = "test";
process.env.DB_NAME = process.env.TEST_DB_NAME || "chesswars_test";
process.env.ADMIN_KEY = "test-admin-key";

const test = require("node:test");
const assert = require("node:assert/strict");
const { io: connect } = require("socket.io-client");
const db = require("../src/db");
const games = require("../src/games");
const { start } = require("../src/index");

let srv;
let base;
const sockets = [];

async function resetDatabase() {
  const conn = await require("mysql2/promise").createConnection({
    host: process.env.DB_HOST || "127.0.0.1", port: Number(process.env.DB_PORT || 3306),
    user: process.env.DB_USER, password: process.env.DB_PASSWORD, database: process.env.DB_NAME, multipleStatements: true,
    ...(require("../src/config").dbSsl ? { ssl: require("../src/config").dbSsl } : {})  // hosted test databases
  });
  const [tables] = await conn.query("SELECT table_name AS t FROM information_schema.tables WHERE table_schema = ?", [process.env.DB_NAME]);
  await conn.query("SET FOREIGN_KEY_CHECKS = 0");
  for (const { t } of tables) await conn.query(`DROP TABLE \`${t}\``);
  await conn.query("SET FOREIGN_KEY_CHECKS = 1");
  await conn.end();
}

async function api(method, path, body, token) {
  const res = await fetch(base + path, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined
  });
  const text = await res.text();
  let data;
  try { data = JSON.parse(text); } catch { data = text; }
  return { status: res.status, data };
}

let counter = 0;
async function member(name) {
  counter += 1;
  const r = await api("POST", "/api/auth/register", { name, email: `${name.toLowerCase()}${counter}@test.dev`, password: "battle-ready-1", army: "knights" });
  assert.equal(r.status, 200, JSON.stringify(r.data));
  return { ...r.data, email: `${name.toLowerCase()}${counter}@test.dev` };
}

// A connected player: emit with acknowledgement, and wait for server pushes.
function player(token) {
  const socket = connect(base, { auth: { token }, transports: ["websocket"], forceNew: true });
  sockets.push(socket);
  const send = (event, payload) => new Promise(resolve => socket.emit(event, payload, resolve));
  const next = (event, predicate = () => true, ms = 4000) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => { socket.off(event, on); reject(new Error(`timed out waiting for ${event}`)); }, ms);
    const on = data => { if (predicate(data)) { clearTimeout(timer); socket.off(event, on); resolve(data); } };
    socket.on(event, on);
  });
  const ready = new Promise((resolve, reject) => { socket.on("connect", resolve); socket.on("connect_error", reject); });
  return { socket, send, next, ready };
}

const sq = s => ({ col: s.charCodeAt(0) - 97, row: 8 - Number(s[1]) });
const mv = (from, to, promotion) => ({ fromRow: sq(from).row, fromCol: sq(from).col, toRow: sq(to).row, toCol: sq(to).col, promotion });

async function playMoves(white, black, gameId, list) {
  for (let i = 0; i < list.length; i++) {
    const [from, to] = list[i].split("-");
    const who = i % 2 === 0 ? white : black;
    const r = await who.send("game:move", { id: gameId, ply: i, move: mv(from, to) });
    assert.equal(r.ok, true, `${list[i]}: ${JSON.stringify(r)}`);
  }
}

test.before(async () => {
  await resetDatabase();
  srv = await start({ port: 0, log: () => {} });
  base = `http://127.0.0.1:${srv.port}`;
});

test.after(async () => {
  for (const s of sockets) s.close();
  await srv.stop();
});

test("accounts: register, sign in, profile, and the obvious mistakes", async () => {
  const a = await member("Ada");
  assert.equal(a.user.name, "Ada");
  assert.equal(a.user.guest, false);
  assert.ok(a.token.length > 30);

  assert.equal((await api("GET", "/api/me", null, a.token)).data.user.name, "Ada");
  assert.equal((await api("GET", "/api/me", null, "nonsense")).status, 401);

  const dupName = await api("POST", "/api/auth/register", { name: "ada", email: "other@test.dev", password: "battle-ready-1" });
  assert.equal(dupName.data.error, "name-taken");
  const dupEmail = await api("POST", "/api/auth/register", { name: "Bea", email: a.email, password: "battle-ready-1" });
  assert.equal(dupEmail.data.error, "email-taken");
  assert.equal((await api("POST", "/api/auth/register", { name: "Shorty", email: "s@test.dev", password: "123" })).data.error, "weak-password");
  assert.equal((await api("POST", "/api/auth/register", { name: "x", email: "x@test.dev", password: "battle-ready-1" })).data.error, "bad-name");
  assert.equal((await api("POST", "/api/auth/register", { name: "Sh1tLord", email: "y@test.dev", password: "battle-ready-1" })).data.error, "bad-name");

  const bad = await api("POST", "/api/auth/login", { email: a.email, password: "wrong-password" });
  assert.equal(bad.status, 401);
  const good = await api("POST", "/api/auth/login", { email: a.email.toUpperCase(), password: "battle-ready-1" });
  assert.equal(good.status, 200);

  const changed = await api("PATCH", "/api/me", { army: "vikings" }, good.data.token);
  assert.equal(changed.data.user.army, "vikings");
  assert.equal((await api("PATCH", "/api/me", { army: "pirates" }, good.data.token)).data.error, "bad-army");

  await api("POST", "/api/auth/logout", null, good.data.token);
  assert.equal((await api("GET", "/api/me", null, good.data.token)).status, 401);
});

test("invite link: guest accepts, server referees fool's mate, records update", async () => {
  const host = await member("Inviter");
  const h = player(host.token);
  await h.ready;
  const inv = await h.send("invite:create", { clock: "live" });
  assert.equal(inv.ok, true);
  assert.match(inv.url, /\/i\/[A-Z2-9]{8}$/);

  // The link preview chat apps fetch
  const preview = await fetch(`${base}/i/${inv.code}`, { headers: { "user-agent": "WhatsApp/2.23" } }).then(r => r.text());
  assert.match(preview, /og:title" content="Inviter challenges you to Chess Wars"/);
  assert.match(preview, new RegExp(`invite=${inv.code}`));
  const info = await api("GET", `/api/invites/${inv.code}`);
  assert.equal(info.data.invite.fromName, "Inviter");
  assert.equal(info.data.invite.open, true);

  const g = await api("POST", "/api/auth/guest", { name: "Visitor", army: "undead" });
  assert.equal(g.data.user.guest, true);
  const v = player(g.data.token);
  await v.ready;
  const started = h.next("game:started");
  const acc = await v.send("invite:accept", { code: inv.code });
  assert.equal(acc.ok, true);
  const { gameId } = await started;
  assert.equal(gameId, acc.gameId);
  assert.equal((await v.send("invite:accept", { code: inv.code })).error, "invite-closed");

  const hj = await h.send("game:join", { id: gameId });
  const vj = await v.send("game:join", { id: gameId });
  assert.equal(hj.game.white, host.user.id);
  assert.equal(hj.game.whiteArmy, "knights");
  assert.equal(vj.game.blackArmy, "undead");

  // Referee checks
  assert.equal((await v.send("game:move", { id: gameId, ply: 0, move: mv("e7", "e5") })).error, "not-your-turn");
  assert.equal((await h.send("game:move", { id: gameId, ply: 0, move: mv("e2", "e5") })).error, "illegal");
  assert.equal((await h.send("game:move", { id: gameId, ply: 3, move: mv("e2", "e4") })).error, "out-of-sync");

  const outsider = player((await member("Nosy")).token);
  await outsider.ready;
  assert.equal((await outsider.send("game:join", { id: gameId })).error, "not-yours");

  const finished = v.next("game", s => s.status === "finished");
  await playMoves(h, v, gameId, ["f2-f3", "e7-e5", "g2-g4", "d8-h4"]);
  const end = await finished;
  assert.equal(end.result, "b");
  assert.equal(end.reason, "checkmate");
  assert.equal(end.moves.length, 4);
  assert.equal((await h.send("game:move", { id: gameId, ply: 4, move: mv("a2", "a3") })).error, "finished");

  assert.equal((await api("GET", "/api/me", null, host.token)).data.user.losses, 1);
  assert.equal((await api("GET", "/api/me", null, g.data.token)).data.user.wins, 1);

  // The guest keeps their record when they create an account
  const up = await api("POST", "/api/auth/upgrade", { email: "visitor@test.dev", password: "battle-ready-1" }, g.data.token);
  assert.equal(up.data.user.guest, false);
  assert.equal(up.data.user.wins, 1);
  assert.equal((await api("POST", "/api/auth/login", { email: "visitor@test.dev", password: "battle-ready-1" })).status, 200);
});

test("challenges: send, see in the War Room, accept, resign; taunts; draw by agreement", async () => {
  const a = await member("Arthur");
  const b = await member("Brienne");
  const pa = player(a.token);
  const pb = player(b.token);
  await Promise.all([pa.ready, pb.ready]);

  const incoming = pb.next("challenge:incoming");
  assert.equal((await pa.send("challenge:send", { to: b.user.id, clock: "daily" })).ok, true);
  assert.equal((await incoming).fromName, "Arthur");
  assert.equal((await pa.send("challenge:send", { to: b.user.id })).error, "already");

  const lobby = await pb.send("lobby:get");
  assert.equal(lobby.incoming.length, 1);
  assert.equal(lobby.incoming[0].fromName, "Arthur");
  assert.ok(lobby.users.find(u => u.id === a.user.id).online);

  const started = pa.next("game:started");
  const acc = await pb.send("challenge:answer", { id: lobby.incoming[0].id, action: "accept" });
  assert.equal(acc.ok, true);
  const { gameId } = await started;
  await pa.send("game:join", { id: gameId });
  const joined = await pb.send("game:join", { id: gameId });
  assert.equal(joined.game.clock.type, "daily");

  // Battle cries must be the army's own
  assert.equal((await pa.send("game:taunt", { id: gameId, text: "anything I like" })).error, "bad-taunt");
  const heard = pb.next("game", s => s.taunt);
  const cry = require("../src/rules").ARMIES.knights.taunts[0];
  assert.equal((await pa.send("game:taunt", { id: gameId, text: cry })).ok, true);
  assert.equal((await heard).taunt.text, cry);

  await playMoves(pa, pb, gameId, ["e2-e4", "e7-e5"]);
  const over = pa.next("game", s => s.status === "finished");
  assert.equal((await pb.send("game:resign", { id: gameId })).ok, true);
  const end = await over;
  assert.equal(end.result, "w");
  assert.equal(end.reason, "resign");

  // A second battle, settled by agreement
  const again = pb.next("challenge:incoming");
  await pa.send("challenge:send", { to: b.user.id, clock: "live" });
  await again;
  const l2 = await pb.send("lobby:get");
  const s2 = pa.next("game:started");
  await pb.send("challenge:answer", { id: l2.incoming[0].id, action: "accept" });
  const g2 = (await s2).gameId;
  await pa.send("game:join", { id: g2 });
  await pb.send("game:join", { id: g2 });
  await playMoves(pa, pb, g2, ["d2-d4", "d7-d5"]);
  assert.equal((await pb.send("game:draw", { id: g2, action: "accept" })).error, "bad-draw");
  const offered = pb.next("game", s => s.drawOffer === "w");
  await pa.send("game:draw", { id: g2, action: "offer" });
  await offered;
  const drawn = pa.next("game", s => s.status === "finished");
  await pb.send("game:draw", { id: g2, action: "accept" });
  const d = await drawn;
  assert.equal(d.result, "draw");
  assert.equal(d.reason, "agreement");
  const me = (await api("GET", "/api/me", null, a.token)).data.user;
  assert.deepEqual([me.wins, me.losses, me.draws], [1, 0, 1]);
});

test("clocks: no first move calls the battle off; running out of time loses", async () => {
  const saved = { first: games.FIRST_MOVE_MS.live, initial: games.CLOCKS.live.initialMs, inc: games.CLOCKS.live.incMs };
  const k = Number(process.env.TEST_SLOW || 1);  // stretch the timings for a far-away database (e.g. TEST_SLOW=5)
  games.FIRST_MOVE_MS.live = 600 * k;
  games.CLOCKS.live.initialMs = 1500 * k;
  games.CLOCKS.live.incMs = 0;
  try {
    const a = await member("Swift");
    const b = await member("Slow");
    const pa = player(a.token);
    const pb = player(b.token);
    await Promise.all([pa.ready, pb.ready]);
    const startGame = async () => {
      const inv = await pa.send("invite:create", { clock: "live" });
      const s = pa.next("game:started");
      await pb.send("invite:accept", { code: inv.code });
      const id = (await s).gameId;
      await pa.send("game:join", { id });
      await pb.send("game:join", { id });
      return id;
    };

    const g1 = await startGame();
    const aborted = await pa.next("game", s => s.id === g1 && s.status === "finished", 4000 * k);
    assert.equal(aborted.reason, "aborted");
    assert.equal(aborted.result, null);

    const g2 = await startGame();
    await playMoves(pa, pb, g2, ["e2-e4", "e7-e5"]);  // the clocks start now; White to move, and waits
    const flagged = await pb.next("game", s => s.id === g2 && s.status === "finished", 5000 * k);
    assert.equal(flagged.reason, "timeout");
    assert.equal(flagged.result, "b");
    const slow = (await api("GET", "/api/me", null, b.token)).data.user;
    assert.equal(slow.wins, 1);
    assert.equal(slow.losses + slow.draws, 0);
  } finally {
    games.FIRST_MOVE_MS.live = saved.first;
    games.CLOCKS.live.initialMs = saved.initial;
    games.CLOCKS.live.incMs = saved.inc;
  }
});

test("password reset by email link, blocking, reporting, account deletion, stats", async () => {
  const a = await member("Forgetful");
  const forgot = await api("POST", "/api/auth/forgot", { email: a.email });
  assert.equal(forgot.status, 200);
  assert.equal((await api("POST", "/api/auth/forgot", { email: "nobody@test.dev" })).status, 200);  // same answer either way
  const mail = (await api("GET", "/api/test/outbox")).data.at(-1);
  assert.equal(mail.to, a.email);
  const token = mail.text.match(/\/reset\/([\w-]+)/)[1];
  assert.match(await fetch(`${base}/reset/${token}`).then(r => r.text()), /New password/);
  assert.equal((await api("POST", "/api/auth/reset", { token, password: "fresh-password-2" })).status, 200);
  assert.equal((await api("POST", "/api/auth/reset", { token, password: "again-password-3" })).data.error, "bad-token");
  assert.equal((await api("GET", "/api/me", null, a.token)).status, 401);  // old sessions signed out
  const relog = await api("POST", "/api/auth/login", { email: a.email, password: "fresh-password-2" });
  assert.equal(relog.status, 200);

  const b = await member("Rude");
  const pa = player(relog.data.token);
  const pb = player(b.token);
  await Promise.all([pa.ready, pb.ready]);
  assert.equal((await pa.send("user:block", { id: b.user.id })).ok, true);
  assert.equal((await pb.send("challenge:send", { to: a.user.id })).error, "blocked");
  assert.ok(!(await pb.send("lobby:get")).users.some(u => u.id === a.user.id));
  assert.equal((await pa.send("user:report", { id: b.user.id, reason: "Rude name" })).ok, true);

  assert.equal((await api("DELETE", "/api/me", { password: "wrong" }, relog.data.token)).status, 401);
  assert.equal((await api("DELETE", "/api/me", { password: "fresh-password-2" }, relog.data.token)).status, 200);
  assert.equal((await api("POST", "/api/auth/login", { email: a.email, password: "fresh-password-2" })).status, 401);
  const row = await db.one("SELECT name, email, password_hash FROM users WHERE id = ?", [a.user.id]);
  assert.match(row.name, /^Deleted #/);
  assert.equal(row.email, null);
  assert.equal(row.password_hash, null);

  assert.equal((await fetch(`${base}/api/admin/stats`)).status, 403);
  const stats = await fetch(`${base}/api/admin/stats`, { headers: { "x-admin-key": "test-admin-key" } }).then(r => r.json());
  assert.ok(stats.accounts.registered >= 8);
  assert.ok(stats.invites.created >= 3);
  assert.ok(stats.battles.finished >= 4);
});

test("closed beta: registration can require a code, guests by invite still welcome", async () => {
  const config = require("../src/config");
  config.registrationCode = "LETMEIN";
  try {
    const no = await api("POST", "/api/auth/register", { name: "Latecomer", email: "late@test.dev", password: "battle-ready-1" });
    assert.equal(no.status, 403);
    const yes = await api("POST", "/api/auth/register", { name: "Latecomer", email: "late@test.dev", password: "battle-ready-1", code: "LETMEIN" });
    assert.equal(yes.status, 200);
    assert.equal((await api("POST", "/api/auth/guest", { name: "Drifter" })).status, 200);
  } finally {
    config.registrationCode = "";
  }
});
