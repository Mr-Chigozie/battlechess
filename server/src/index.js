"use strict";

const http = require("http");
const config = require("./config");
const db = require("./db");
const games = require("./games");
const { build, allowOrigin } = require("./http");
const { attach } = require("./sockets");

async function start({ port = config.port, log = console.log } = {}) {
  await db.migrate(log);
  const server = http.createServer(build());
  const io = attach(server, allowOrigin);
  const restored = await games.restore();
  await new Promise(resolve => server.listen(port, resolve));
  log(`Chess Wars server on port ${server.address().port} (${config.env}); ${restored} battle(s) in progress`);
  const stop = async () => {
    games.shutdown();
    io.close();
    await new Promise(resolve => server.close(resolve));
    await db.close();
  };
  return { server, io, stop, port: server.address().port };
}

if (require.main === module) {
  start().then(({ stop }) => {
    const quit = () => stop().then(() => process.exit(0));
    process.on("SIGINT", quit);
    process.on("SIGTERM", quit);
  }).catch(error => {
    console.error("Failed to start:", error);
    process.exit(1);
  });
}

module.exports = { start };
