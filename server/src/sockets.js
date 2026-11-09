"use strict";

// Live connection (Socket.IO): the War Room, challenges and battles arrive instantly.
// Every message from a player is answered through an acknowledgement: { ok, ... } or { error, message }.
const { Server } = require("socket.io");
const accounts = require("./accounts");
const games = require("./games");
const config = require("./config");

function attach(httpServer, allowOrigin) {
  const io = new Server(httpServer, {
    cors: { origin: (origin, cb) => cb(null, allowOrigin(origin)), credentials: false },
    maxHttpBufferSize: 16 * 1024
  });
  const sockets = new Map();  // userId -> Set of sockets

  const emitToUser = (userId, event, data) => {
    for (const s of sockets.get(userId) || []) s.emit(event, data);
  };
  const isOnline = userId => Boolean(sockets.get(userId)?.size);
  const isPlaying = userId => [...(sockets.get(userId) || [])].some(s => s.data.gameId && games.activeGameOf(userId) === s.data.gameId);

  // Lobby refreshes are coalesced: a burst of changes sends one update per player.
  const pending = new Set();
  let flushTimer = null;
  const pushLobby = userId => {
    if (!isOnline(userId)) return;
    pending.add(userId);
    if (!flushTimer) flushTimer = setTimeout(flush, config.isTest ? 30 : 250);
  };
  async function flush() {
    flushTimer = null;
    const ids = [...pending];
    pending.clear();
    for (const id of ids) {
      const first = [...(sockets.get(id) || [])][0];
      if (!first) continue;
      try {
        emitToUser(id, "lobby", await games.lobby(first.data.user));
      } catch (error) {
        console.error("lobby:", error.message);
      }
    }
  }
  // Someone came online or went offline: everyone's commander list changes.
  const presenceChanged = () => { for (const id of sockets.keys()) pushLobby(id); };

  games.init({ emitGame: (id, view) => io.to(`game:${id}`).emit("game", view), emitToUser, pushLobby, isOnline, isPlaying });

  io.use(async (socket, next) => {
    try {
      const user = await accounts.userFromToken(socket.handshake.auth?.token);
      if (!user) return next(new Error("unauthorized"));
      socket.data.user = user;
      next();
    } catch (error) {
      next(new Error("server-error"));
    }
  });

  io.on("connection", socket => {
    const user = socket.data.user;
    if (!sockets.has(user.id)) sockets.set(user.id, new Set());
    sockets.get(user.id).add(socket);
    accounts.touch(user.id).catch(() => {});
    presenceChanged();
    const heartbeat = setInterval(() => accounts.touch(user.id).catch(() => {}), 60 * 1000);

    // Simple flood guard: at most 30 messages in any 10 seconds per connection.
    let window = Date.now();
    let count = 0;
    const handle = (event, fn) => socket.on(event, async (payload, ack) => {
      const reply = typeof ack === "function" ? ack : () => {};
      if (Date.now() - window > 10000) { window = Date.now(); count = 0; }
      if (++count > 30) return reply({ error: "slow-down", message: "Too many actions at once. Slow down a little." });
      try {
        reply(await fn(payload || {}));
      } catch (error) {
        if (error instanceof accounts.UserError) return reply({ error: error.code, message: error.message });
        console.error(`${event}:`, error);
        reply({ error: "server-error", message: "Something went wrong on the server. Try again." });
      }
    });

    handle("lobby:get", () => games.lobby(user));
    handle("challenge:send", p => games.challenge(user, p));
    handle("challenge:answer", p => games.answerChallenge(user, p));
    handle("invite:create", p => games.createInvite(user, p));
    handle("invite:cancel", p => games.cancelInvite(user, p));
    handle("invite:accept", p => games.acceptInvite(user, p));
    handle("game:join", async p => {
      const game = await games.join(user, p.id);
      if (socket.data.gameId) socket.leave(`game:${socket.data.gameId}`);
      socket.data.gameId = game.id;
      socket.join(`game:${game.id}`);
      presenceChanged();
      return { ok: true, game };
    });
    handle("game:leave", () => {
      if (socket.data.gameId) socket.leave(`game:${socket.data.gameId}`);
      socket.data.gameId = null;
      presenceChanged();
      return { ok: true };
    });
    handle("game:move", p => games.move(user, p));
    handle("game:resign", p => games.resign(user, p.id));
    handle("game:draw", p => games.draw(user, p));
    handle("game:taunt", p => games.taunt(user, p));
    handle("user:block", p => games.block(user, p));
    handle("user:report", p => games.report(user, p));

    socket.on("disconnect", () => {
      clearInterval(heartbeat);
      const set = sockets.get(user.id);
      set?.delete(socket);
      if (set && !set.size) sockets.delete(user.id);
      accounts.touch(user.id).catch(() => {});
      presenceChanged();
    });
  });

  return io;
}

module.exports = { attach };
