"use strict";

// Battles, challenges and invite links. The server is the referee: it checks every move with the shared
// rules, runs the clocks, and decides every result. Built for one server process (plenty for the beta).
const crypto = require("crypto");
const db = require("./db");
const config = require("./config");
const rules = require("./rules");
const { track } = require("./analytics");
const { UserError, publicUser } = require("./accounts");

const CLOCKS = {
  live: { initialMs: 10 * 60 * 1000, incMs: 5 * 1000 },   // 10 minutes each, +5 seconds per move
  daily: { initialMs: 24 * 3600 * 1000, incMs: 0 }        // 24 hours for every move
};
// If a side never makes its first move, the battle is called off (no result for anyone).
const FIRST_MOVE_MS = { live: 3 * 60 * 1000, daily: 3 * 24 * 3600 * 1000 };
const MAX_PENDING_CHALLENGES = 10;
const MAX_OPEN_INVITES = 10;
const INVITE_DAYS = 7;

let hub = { emitGame() {}, emitToUser() {}, pushLobby() {}, isOnline: () => false, isPlaying: () => false };
const live = new Map();  // gameId -> { row, state, moves, taunt, lock, timer }

const randomId = (length, alphabet) => Array.from(crypto.randomBytes(length), b => alphabet[b % alphabet.length]).join("");
const newGameId = () => randomId(12, "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789");
const newInviteCode = () => randomId(8, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789");  // no 0/O or 1/I mix-ups
const checkClock = type => (CLOCKS[type] ? type : "live");
const inviteUrl = code => `${config.publicUrl}/i/${code}`;

function init(h) {
  hub = h;
}

// ---------------------------------------------------------------- loading and locking
async function load(id) {
  if (live.has(id)) return live.get(id);
  const row = await db.one("SELECT * FROM games WHERE id = ?", [id]);
  if (!row) return null;
  const rows = await db.query("SELECT from_row, from_col, to_row, to_col, promotion FROM moves WHERE game_id = ? ORDER BY ply", [id]);
  const moves = rows.map(r => ({ fromRow: r.from_row, fromCol: r.from_col, toRow: r.to_row, toCol: r.to_col, promotion: r.promotion }));
  const entry = { row, state: rules.replay(moves), moves, taunt: null, lock: Promise.resolve(), timer: null };
  if (live.has(id)) return live.get(id);  // loaded concurrently
  live.set(id, entry);
  schedule(entry);
  return entry;
}

// Everything that changes a battle runs one at a time per battle.
async function withGame(id, fn) {
  const entry = await load(String(id || ""));
  if (!entry) throw new UserError("no-game", "That battle doesn't exist.", 404);
  const run = entry.lock.then(() => fn(entry));
  entry.lock = run.catch(() => {});
  return run;
}

const colorOf = (row, userId) => (row.white_id === userId ? "w" : row.black_id === userId ? "b" : null);
const other = color => (color === "w" ? "b" : "w");

// ---------------------------------------------------------------- clocks
function clockState(row, now = Date.now()) {
  const turn = row.ply % 2 === 0 ? "w" : "b";
  const running = row.status === "active" && row.ply >= 2;
  const elapsed = row.status === "active" ? now - Number(row.turn_started_at) : 0;
  const whiteMs = Number(row.white_ms) - (running && turn === "w" ? elapsed : 0);
  const blackMs = Number(row.black_ms) - (running && turn === "b" ? elapsed : 0);
  const deadline = row.status !== "active" ? null
    : row.ply < 2 ? Number(row.turn_started_at) + FIRST_MOVE_MS[row.clock_type]
    : Number(row.turn_started_at) + Number(turn === "w" ? row.white_ms : row.black_ms);
  return { turn, running, whiteMs, blackMs, deadline };
}

function schedule(entry) {
  clearTimeout(entry.timer);
  entry.timer = null;
  const { deadline } = clockState(entry.row);
  if (deadline === null) return;
  const wait = Math.min(Math.max(deadline - Date.now() + 25, 0), 2 ** 31 - 1);
  entry.timer = setTimeout(() => {
    withGame(entry.row.id, flagIfExpired).catch(error => console.error("clock:", error.message));
  }, wait);
  entry.timer.unref();  // the listening server keeps the process alive; timers alone shouldn't
}

// Called by the timer and before every move: ends the battle if the side to move ran out of time.
async function flagIfExpired(entry) {
  const { row } = entry;
  if (row.status !== "active") return true;
  const { deadline, turn } = clockState(row);
  if (Date.now() < deadline) {
    schedule(entry);
    return false;
  }
  if (row.ply < 2) await finish(entry, null, "aborted");
  else await finish(entry, other(turn), "timeout");
  return true;
}

// ---------------------------------------------------------------- views sent to players
function view(entry) {
  const { row } = entry;
  const clock = clockState(row);
  return {
    id: row.id, white: row.white_id, black: row.black_id,
    whiteName: row.white_name, blackName: row.black_name, whiteArmy: row.white_army, blackArmy: row.black_army,
    moves: entry.moves, status: row.status, result: row.result, reason: row.reason,
    taunt: entry.taunt, drawOffer: row.draw_offer,
    clock: {
      type: row.clock_type, initialMs: row.clock_initial_ms, incMs: row.clock_inc_ms,
      whiteMs: clock.whiteMs, blackMs: clock.blackMs, running: clock.running, turn: clock.turn,
      deadline: clock.deadline, serverNow: Date.now()
    }
  };
}

function broadcast(entry) {
  hub.emitGame(entry.row.id, view(entry));
  hub.pushLobby(entry.row.white_id);
  hub.pushLobby(entry.row.black_id);
}

// ---------------------------------------------------------------- starting and finishing
async function createGame(q, white, black, clockType, source) {
  const id = newGameId();
  const clock = CLOCKS[clockType];
  await q(`INSERT INTO games (id, white_id, black_id, white_name, black_name, white_army, black_army, clock_type,
             clock_initial_ms, clock_inc_ms, white_ms, black_ms, turn_started_at, source)
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  [id, white.id, black.id, white.name, black.name, white.army, black.army, clockType,
    clock.initialMs, clock.incMs, clock.initialMs, clock.initialMs, Date.now(), source]);
  return id;
}

async function started(gameId, whiteId, blackId, source) {
  track(whiteId, "game_started", { gameId, source });
  await load(gameId);
  for (const userId of [whiteId, blackId]) {
    hub.emitToUser(userId, "game:started", { gameId });
    hub.pushLobby(userId);
  }
}

async function finish(entry, result, reason) {
  const { row } = entry;
  await db.tx(async q => {
    await q("UPDATE games SET status = 'finished', result = ?, reason = ?, draw_offer = NULL, finished_at = UTC_TIMESTAMP(3) WHERE id = ? AND status = 'active'",
      [result, reason, row.id]);
    if (result === "draw") {
      await q("UPDATE users SET draws = draws + 1 WHERE id IN (?, ?)", [row.white_id, row.black_id]);
    } else if (result) {
      const [winner, loser] = result === "w" ? [row.white_id, row.black_id] : [row.black_id, row.white_id];
      await q("UPDATE users SET wins = wins + 1 WHERE id = ?", [winner]);
      await q("UPDATE users SET losses = losses + 1 WHERE id = ?", [loser]);
    }
  });
  Object.assign(row, { status: "finished", result, reason, draw_offer: null });
  clearTimeout(entry.timer);
  track(row.white_id, "game_finished", { gameId: row.id, result, reason, plies: row.ply });
  broadcast(entry);
  // Finished battles don't need to stay in memory; they reload from the database on demand.
  setTimeout(() => { if (live.get(row.id) === entry) live.delete(row.id); }, 60 * 1000).unref();
}

// ---------------------------------------------------------------- playing
async function join(user, id) {
  return withGame(id, async entry => {
    if (!colorOf(entry.row, user.id)) throw new UserError("not-yours", "That battle isn't yours.", 403);
    await flagIfExpired(entry);
    return view(entry);
  });
}

async function move(user, { id, ply, move: m }) {
  return withGame(id, async entry => {
    const { row } = entry;
    const color = colorOf(row, user.id);
    if (!color) throw new UserError("not-yours", "That battle isn't yours.", 403);
    if (row.status !== "active") throw new UserError("finished", "This battle is already over.");
    if (await flagIfExpired(entry)) throw new UserError("finished", "Time ran out.");
    if (entry.state.pos.turn !== color) throw new UserError("not-your-turn", "It's not your move.");
    if (ply !== row.ply) throw new UserError("out-of-sync", "Your board was out of date.");
    const out = rules.play(entry.state, m);
    if (out.error) throw new UserError("illegal", "That move isn't legal.");

    // Clocks: the mover's time is charged from when their turn began; first moves are free.
    const now = Date.now();
    const clock = clockState(row, now);
    const spent = color === "w" ? clock.whiteMs : clock.blackMs;
    const next = row.clock_type === "daily" ? row.clock_initial_ms : spent + row.clock_inc_ms;
    const field = color === "w" ? "white_ms" : "black_ms";
    const remaining = row.ply >= 2 ? next : Number(row[field]);
    try {
      await db.tx(async q => {
        await q("INSERT INTO moves (game_id, ply, from_row, from_col, to_row, to_col, promotion, notation) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
          [row.id, row.ply, out.move.fromRow, out.move.fromCol, out.move.toRow, out.move.toCol, out.move.promotion, out.notation]);
        await q(`UPDATE games SET ply = ply + 1, ${field} = ?, turn_started_at = ?, draw_offer = NULL WHERE id = ?`, [remaining, now, row.id]);
      });
    } catch (error) {
      live.delete(row.id);  // the in-memory board moved ahead of the database: reload it next time
      throw error;
    }
    entry.moves.push(out.move);
    Object.assign(row, { ply: row.ply + 1, [field]: remaining, turn_started_at: now, draw_offer: null });
    if (out.end) await finish(entry, out.end.result, out.end.reason);
    else {
      schedule(entry);
      broadcast(entry);
    }
    return { ok: true };
  });
}

async function resign(user, id) {
  return withGame(id, async entry => {
    const color = colorOf(entry.row, user.id);
    if (!color) throw new UserError("not-yours", "That battle isn't yours.", 403);
    if (entry.row.status !== "active") throw new UserError("finished", "This battle is already over.");
    if (entry.row.ply < 2) await finish(entry, null, "aborted");
    else await finish(entry, other(color), "resign");
    return { ok: true };
  });
}

async function draw(user, { id, action }) {
  return withGame(id, async entry => {
    const { row } = entry;
    const color = colorOf(row, user.id);
    if (!color) throw new UserError("not-yours", "That battle isn't yours.", 403);
    if (row.status !== "active") throw new UserError("finished", "This battle is already over.");
    const theirs = row.draw_offer === other(color);
    if (action === "offer" || (action === "accept" && theirs)) {
      if (theirs) {
        await finish(entry, "draw", "agreement");
        return { ok: true };
      }
      if (row.draw_offer === color) return { ok: true };
      await db.query("UPDATE games SET draw_offer = ? WHERE id = ?", [color, row.id]);
      row.draw_offer = color;
    } else if (action === "decline" && theirs) {
      await db.query("UPDATE games SET draw_offer = NULL WHERE id = ?", [row.id]);
      row.draw_offer = null;
    } else {
      throw new UserError("bad-draw", "There's no draw offer to answer.");
    }
    broadcast(entry);
    return { ok: true };
  });
}

const lastTaunt = new Map();
async function taunt(user, { id, text }) {
  return withGame(id, async entry => {
    const { row } = entry;
    const color = colorOf(row, user.id);
    if (!color || row.status !== "active") return { ok: false };
    // Only the army's own battle cries: no free text, so nothing to moderate.
    if (!rules.isTaunt(color === "w" ? row.white_army : row.black_army, text)) throw new UserError("bad-taunt", "Unknown battle cry.");
    if (Date.now() - (lastTaunt.get(user.id) || 0) < 4000) return { ok: false };
    lastTaunt.set(user.id, Date.now());
    entry.taunt = { by: user.id, text, at: Date.now() };
    hub.emitGame(row.id, view(entry));
    return { ok: true };
  });
}

// ---------------------------------------------------------------- challenges
async function isBlocked(a, b) {
  return Boolean(await db.one("SELECT 1 FROM blocks WHERE (blocker_id = ? AND blocked_id = ?) OR (blocker_id = ? AND blocked_id = ?)", [a, b, b, a]));
}

async function challenge(user, { to, clock }) {
  const target = await db.one("SELECT * FROM users WHERE id = ? AND deleted_at IS NULL", [Number(to)]);
  if (!target || target.id === user.id) throw new UserError("no-user", "That commander can't be challenged.");
  if (await isBlocked(user.id, target.id)) throw new UserError("blocked", "That commander can't be challenged.");
  if (await db.one("SELECT id FROM challenges WHERE from_id = ? AND to_id = ? AND status = 'pending'", [user.id, target.id])) {
    throw new UserError("already", "You've already challenged them.");
  }
  const { n } = await db.one("SELECT COUNT(*) AS n FROM challenges WHERE from_id = ? AND status = 'pending'", [user.id]);
  if (n >= MAX_PENDING_CHALLENGES) throw new UserError("too-many", "You have too many challenges waiting. Cancel some first.");
  await db.query("INSERT INTO challenges (from_id, to_id, clock_type) VALUES (?, ?, ?)", [user.id, target.id, checkClock(clock)]);
  track(user.id, "challenge_sent", { to: target.id, clock: checkClock(clock) });
  hub.emitToUser(target.id, "challenge:incoming", { fromName: user.name });
  hub.pushLobby(user.id);
  hub.pushLobby(target.id);
  return { ok: true };
}

async function answerChallenge(user, { id, action }) {
  const result = await db.tx(async q => {
    const [c] = await q("SELECT * FROM challenges WHERE id = ? FOR UPDATE", [Number(id)]);
    if (!c || c.status !== "pending") throw new UserError("gone", "That challenge is no longer open.");
    if (action === "cancel") {
      if (c.from_id !== user.id) throw new UserError("not-yours", "That challenge isn't yours.", 403);
      await q("UPDATE challenges SET status = 'cancelled' WHERE id = ?", [c.id]);
      return { c };
    }
    if (c.to_id !== user.id) throw new UserError("not-yours", "That challenge isn't yours.", 403);
    if (action === "decline") {
      await q("UPDATE challenges SET status = 'declined' WHERE id = ?", [c.id]);
      return { c };
    }
    if (action !== "accept") throw new UserError("bad-action", "Unknown answer.");
    const [white] = await q("SELECT * FROM users WHERE id = ? AND deleted_at IS NULL", [c.from_id]);
    if (!white) throw new UserError("gone", "That commander has left.");
    const [me] = await q("SELECT * FROM users WHERE id = ?", [user.id]);  // fresh name and army
    // The challenger plays Ivory (moves first) with their army; whoever accepts plays Onyx with theirs.
    const gameId = await createGame(q, white, me, c.clock_type, "challenge");
    await q("UPDATE challenges SET status = 'accepted', game_id = ? WHERE id = ?", [gameId, c.id]);
    return { c, gameId };
  });
  const { c, gameId } = result;
  if (action === "decline") hub.emitToUser(c.from_id, "challenge:declined", { byName: user.name });
  if (gameId) await started(gameId, c.from_id, c.to_id, "challenge");
  hub.pushLobby(c.from_id);
  hub.pushLobby(c.to_id);
  return { ok: true, gameId: gameId || null };
}

// ---------------------------------------------------------------- invite links
async function createInvite(user, { clock }) {
  const { n } = await db.one("SELECT COUNT(*) AS n FROM invites WHERE from_id = ? AND status = 'open' AND expires_at > UTC_TIMESTAMP(3)", [user.id]);
  if (n >= MAX_OPEN_INVITES) throw new UserError("too-many", "You have too many open invite links. Cancel some first.");
  const code = newInviteCode();
  await db.query("INSERT INTO invites (code, from_id, clock_type, expires_at) VALUES (?, ?, ?, DATE_ADD(UTC_TIMESTAMP(3), INTERVAL ? DAY))",
    [code, user.id, checkClock(clock), INVITE_DAYS]);
  track(user.id, "invite_created", { code, clock: checkClock(clock) });
  hub.pushLobby(user.id);
  return { ok: true, code, url: inviteUrl(code) };
}

async function cancelInvite(user, { code }) {
  await db.query("UPDATE invites SET status = 'cancelled' WHERE code = ? AND from_id = ? AND status = 'open'", [String(code || ""), user.id]);
  hub.pushLobby(user.id);
  return { ok: true };
}

// Public details for the invite preview and the "you've been challenged" screen.
async function inviteInfo(code) {
  const row = await db.one(`SELECT i.*, u.name AS from_name, u.army AS from_army FROM invites i JOIN users u ON u.id = i.from_id
                            WHERE i.code = ? AND u.deleted_at IS NULL`, [String(code || "").toUpperCase()]);
  if (!row) return null;
  const open = row.status === "open" && new Date(row.expires_at).getTime() > Date.now();
  return { code: row.code, fromId: row.from_id, fromName: row.from_name, fromArmy: row.from_army, clock: row.clock_type, open, gameId: row.game_id };
}

async function inviteOpened(code) {
  await db.query("UPDATE invites SET opened = opened + 1 WHERE code = ?", [String(code || "").toUpperCase()]);
}

async function acceptInvite(user, { code }) {
  const clean = String(code || "").toUpperCase();
  const { gameId, fromId } = await db.tx(async q => {
    const [inv] = await q("SELECT * FROM invites WHERE code = ? FOR UPDATE", [clean]);
    if (!inv) throw new UserError("no-invite", "That invite link doesn't exist.", 404);
    if (inv.from_id === user.id) throw new UserError("own-invite", "That's your own invite link. Send it to a friend!");
    if (inv.status !== "open" || new Date(inv.expires_at).getTime() <= Date.now()) {
      throw new UserError("invite-closed", "That invite has already been used or has expired. Ask your friend for a new link.");
    }
    const [white] = await q("SELECT * FROM users WHERE id = ? AND deleted_at IS NULL", [inv.from_id]);
    if (!white) throw new UserError("invite-closed", "The commander who sent that invite has left.");
    if (await isBlocked(inv.from_id, user.id)) throw new UserError("invite-closed", "That invite can't be accepted.");
    const [me] = await q("SELECT * FROM users WHERE id = ?", [user.id]);  // fresh name and army
    const id = await createGame(q, white, me, inv.clock_type, "invite");
    await q("UPDATE invites SET status = 'used', game_id = ? WHERE code = ?", [id, clean]);
    return { gameId: id, fromId: inv.from_id };
  });
  track(user.id, "invite_accepted", { code: clean, guest: Boolean(user.is_guest) });
  await started(gameId, fromId, user.id, "invite");
  return { ok: true, gameId };
}

// ---------------------------------------------------------------- safety
async function block(user, { id }) {
  const target = Number(id);
  if (!target || target === user.id) throw new UserError("bad-user", "Can't block that commander.");
  await db.query("INSERT IGNORE INTO blocks (blocker_id, blocked_id) VALUES (?, ?)", [user.id, target]);
  await db.query("UPDATE challenges SET status = 'cancelled' WHERE status = 'pending' AND ((from_id = ? AND to_id = ?) OR (from_id = ? AND to_id = ?))",
    [user.id, target, target, user.id]);
  track(user.id, "user_blocked", { target });
  hub.pushLobby(user.id);
  hub.pushLobby(target);
  return { ok: true };
}

async function report(user, { id, gameId, reason }) {
  const target = Number(id);
  const text = String(reason || "").trim().slice(0, 200);
  if (!target || target === user.id || !text) throw new UserError("bad-report", "Say what happened.");
  await db.query("INSERT INTO reports (reporter_id, reported_id, game_id, reason) VALUES (?, ?, ?, ?)", [user.id, target, gameId || null, text]);
  track(user.id, "user_reported", { target });
  return { ok: true };
}

// ---------------------------------------------------------------- the War Room
async function lobby(user) {
  const me = await db.one("SELECT * FROM users WHERE id = ?", [user.id]);
  const people = await db.query(
    `SELECT id, name, army, is_guest, wins, losses, draws, last_seen_at FROM users
      WHERE deleted_at IS NULL AND id <> ?
        AND id NOT IN (SELECT blocked_id FROM blocks WHERE blocker_id = ?)
        AND id NOT IN (SELECT blocker_id FROM blocks WHERE blocked_id = ?)
      ORDER BY last_seen_at DESC LIMIT 100`, [user.id, user.id, user.id]);
  const incoming = await db.query(
    `SELECT c.id, c.clock_type, u.id AS from_id, u.name AS from_name, u.army AS from_army FROM challenges c JOIN users u ON u.id = c.from_id
      WHERE c.to_id = ? AND c.status = 'pending' ORDER BY c.created_at DESC`, [user.id]);
  const outgoing = await db.query(
    `SELECT c.id, c.clock_type, u.id AS to_id, u.name AS to_name FROM challenges c JOIN users u ON u.id = c.to_id
      WHERE c.from_id = ? AND c.status = 'pending' ORDER BY c.created_at DESC`, [user.id]);
  const games = await db.query(
    `(SELECT * FROM games WHERE (white_id = ? OR black_id = ?) AND status = 'active')
     UNION ALL
     (SELECT * FROM games WHERE (white_id = ? OR black_id = ?) AND status = 'finished' ORDER BY finished_at DESC LIMIT 5)`,
    [user.id, user.id, user.id, user.id]);
  const invites = await db.query(
    "SELECT code, clock_type, created_at FROM invites WHERE from_id = ? AND status = 'open' AND expires_at > UTC_TIMESTAMP(3) ORDER BY created_at DESC",
    [user.id]);
  return {
    me: publicUser(me),
    users: people.map(u => ({
      id: u.id, name: u.name, army: u.army, guest: Boolean(u.is_guest), wins: u.wins, losses: u.losses, draws: u.draws,
      lastSeen: new Date(u.last_seen_at).getTime(), online: hub.isOnline(u.id), playing: hub.isPlaying(u.id)
    })),
    incoming: incoming.map(c => ({ id: c.id, clock: c.clock_type, fromId: c.from_id, fromName: c.from_name, fromArmy: c.from_army })),
    outgoing: outgoing.map(c => ({ id: c.id, clock: c.clock_type, toId: c.to_id, toName: c.to_name })),
    games: games.map(g => {
      const entry = live.get(g.id);
      const row = entry ? entry.row : g;
      const myColor = colorOf(row, user.id);
      return {
        id: row.id, status: row.status, result: row.result, reason: row.reason, clock: row.clock_type, ply: row.ply,
        myColor, rival: myColor === "w" ? row.black_name : row.white_name,
        myTurn: row.status === "active" && (row.ply % 2 === 0) === (myColor === "w"),
        updatedAt: new Date(row.finished_at || row.updated_at || g.updated_at).getTime()
      };
    }),
    invites: invites.map(i => ({ code: i.code, url: inviteUrl(i.code), clock: i.clock_type, createdAt: new Date(i.created_at).getTime() }))
  };
}

// On start-up: pick the clocks of every unfinished battle back up.
async function restore() {
  const rows = await db.query("SELECT id FROM games WHERE status = 'active'");
  for (const { id } of rows) {
    const entry = await load(id);
    await withGame(id, flagIfExpired);
    if (entry.row.status !== "active") live.delete(id);
  }
  return rows.length;
}

function activeGameOf(userId) {
  for (const entry of live.values()) {
    if (entry.row.status === "active" && (entry.row.white_id === userId || entry.row.black_id === userId)) return entry.row.id;
  }
  return null;
}

function shutdown() {
  for (const entry of live.values()) clearTimeout(entry.timer);
  live.clear();
}

module.exports = {
  CLOCKS, FIRST_MOVE_MS, init, join, move, resign, draw, taunt, challenge, answerChallenge, createInvite, cancelInvite,
  inviteInfo, inviteOpened, acceptInvite, block, report, lobby, restore, activeGameOf, shutdown, inviteUrl
};
