"use strict";

// Where the Chess Wars server (server/ in this repo) is running.
//  - On this computer (localhost) the game talks to the local development server.
//  - On the live site, set LIVE_SERVER to the hosted server's address, e.g. "https://api.chesswars.app".
//    While it is null, the live site shows "online play is coming soon" and everything else still works.
const LIVE_SERVER = null;
const CHESSWARS_SERVER = /^(localhost|127\.0\.0\.1)$/.test(location.hostname)
  ? (new URLSearchParams(location.search).get("server") || "http://localhost:8080")
  : LIVE_SERVER;
