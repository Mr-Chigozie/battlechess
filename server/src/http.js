"use strict";

const express = require("express");
const helmet = require("helmet");
const cors = require("cors");
const rateLimit = require("express-rate-limit");
const config = require("./config");
const accounts = require("./accounts");
const games = require("./games");
const analytics = require("./analytics");
const mailer = require("./mailer");
const { ARMIES } = require("./rules");

// The game page lives on another site (GitHub Pages), so the API only answers pages from allowed origins.
function allowOrigin(origin) {
  if (!origin) return true;  // same-origin pages and non-browser clients
  if (config.clientOrigins.includes(origin)) return true;
  return config.isDev && /^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(origin);
}

const esc = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const isBot = ua => /bot|crawl|spider|facebookexternalhit|whatsapp|telegram|slack|discord|preview/i.test(ua || "");

function page(title, body, meta = "") {
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)}</title>${meta}
<style>
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #0d0f0c; color: #ece7d8; font: 15px/1.5 "Segoe UI", system-ui, sans-serif; }
  .card { width: min(420px, calc(100% - 32px)); padding: 26px; border: 1px solid #3b3d33; border-radius: 6px; background: #1b1d18; text-align: center; }
  h1 { margin: 0 0 8px; font: 26px Georgia, serif; color: #f0d39a; }
  p { color: #b9b6a8; }
  input { box-sizing: border-box; width: 100%; margin: 6px 0; padding: 11px; border: 1px solid #45473c; border-radius: 3px; background: #131510; color: #ece7d8; font-size: 15px; }
  button, a.btn { display: inline-block; margin-top: 10px; padding: 11px 20px; border: 1px solid #d6ad66; border-radius: 3px; background: #d6ad66; color: #1b1608; font-weight: 600; font-size: 15px; text-decoration: none; cursor: pointer; }
  .msg { min-height: 20px; color: #ff9a8a; }
  .ok { color: #8fd99f; }
</style></head><body><div class="card">${body}</div></body></html>`;
}

function build() {
  const app = express();
  app.set("trust proxy", 1);  // behind the host's load balancer: use the real client IP for rate limits
  app.use(helmet({
    contentSecurityPolicy: { directives: { "script-src": ["'self'", "'unsafe-inline'"], "img-src": ["'self'", "data:", "https:"] } },
    crossOriginResourcePolicy: { policy: "cross-origin" }
  }));
  app.use("/api", cors({ origin: (origin, cb) => cb(null, allowOrigin(origin)), methods: ["GET", "POST", "PATCH", "DELETE"], maxAge: 600 }));
  app.use(express.json({ limit: "10kb" }));

  // Brute-force protection on sign-in style endpoints.
  const authLimit = rateLimit({ windowMs: 15 * 60 * 1000, limit: config.isTest ? 1000 : 30, standardHeaders: "draft-7", legacyHeaders: false,
    message: { error: "rate-limited", message: "Too many attempts. Wait a few minutes and try again." } });
  const mailLimit = rateLimit({ windowMs: 60 * 60 * 1000, limit: config.isTest ? 1000 : 5, standardHeaders: "draft-7", legacyHeaders: false,
    message: { error: "rate-limited", message: "Too many reset emails. Try again in an hour." } });

  const wrap = fn => async (req, res) => {
    try {
      res.json(await fn(req, res) ?? { ok: true });
    } catch (error) {
      if (error instanceof accounts.UserError) return res.status(error.status).json({ error: error.code, message: error.message });
      console.error(req.method, req.path, error);
      res.status(500).json({ error: "server-error", message: "Something went wrong on the server. Try again." });
    }
  };
  const tokenOf = req => (req.get("authorization") || "").replace(/^Bearer\s+/i, "") || null;
  const signedIn = fn => wrap(async (req, res) => {
    const user = await accounts.userFromToken(tokenOf(req));
    if (!user) throw new accounts.UserError("unauthorized", "Please sign in again.", 401);
    return fn(req, user, res);
  });

  app.get("/api/health", (req, res) => res.json({ ok: true }));

  // ---- accounts
  app.post("/api/auth/register", authLimit, wrap(req => accounts.register(req.body)));
  app.post("/api/auth/login", authLimit, wrap(req => accounts.login(req.body)));
  app.post("/api/auth/guest", authLimit, wrap(req => accounts.guest(req.body)));
  app.post("/api/auth/upgrade", authLimit, signedIn((req, user) => accounts.upgrade(user, req.body).then(u => ({ user: u }))));
  app.post("/api/auth/logout", wrap(req => accounts.logout(tokenOf(req))));
  app.post("/api/auth/forgot", mailLimit, wrap(async req => {
    await accounts.forgotPassword(req.body);
    return { ok: true, message: "If that email has an account, a reset link is on its way." };
  }));
  app.post("/api/auth/reset", authLimit, wrap(req => accounts.resetPassword(req.body)));
  app.get("/api/me", signedIn((req, user) => ({ user: accounts.publicUser(user) })));
  app.patch("/api/me", signedIn((req, user) => accounts.updateProfile(user, req.body).then(u => ({ user: u }))));
  app.delete("/api/me", authLimit, signedIn((req, user) => accounts.deleteAccount(user, req.body)));

  // ---- invites
  app.get("/api/invites/:code", wrap(async req => {
    const info = await games.inviteInfo(req.params.code);
    if (!info) throw new accounts.UserError("no-invite", "That invite link doesn't exist.", 404);
    return { invite: { code: info.code, fromId: info.fromId, fromName: info.fromName, fromArmy: info.fromArmy, clock: info.clock, open: info.open } };
  }));

  // ---- analytics
  app.post("/api/events", wrap(async req => {
    const user = await accounts.userFromToken(tokenOf(req));
    const name = String(req.body?.name || "");
    if (analytics.CLIENT_EVENTS.has(name)) analytics.track(user?.id, name, req.body.props && typeof req.body.props === "object" ? req.body.props : null);
    return { ok: true };
  }));
  app.get("/api/admin/stats", wrap(async req => {
    if (!config.adminKey || req.get("x-admin-key") !== config.adminKey) throw new accounts.UserError("forbidden", "Forbidden.", 403);
    return analytics.stats(req.query.days);
  }));
  if (config.isDev) app.get("/api/test/outbox", (req, res) => res.json(mailer.outbox));

  // ---- the invite link people share: a preview card for chat apps, then straight into the battle
  app.get("/i/:code", async (req, res) => {
    const info = await games.inviteInfo(req.params.code).catch(() => null);
    const target = `${config.gameUrl}?invite=${encodeURIComponent(info ? info.code : req.params.code)}`;
    if (info && !isBot(req.get("user-agent"))) games.inviteOpened(info.code).catch(() => {});
    const army = info && ARMIES[info.fromArmy];
    const title = info ? `${info.fromName} challenges you to Chess Wars` : "Chess Wars";
    const desc = info
      ? `${army ? army.name : "Their army"} awaits. Chess with armies that fight it out: tap the link and battle in your browser, no download.`
      : "Chess with armies that fight it out.";
    const image = new URL("art/previews/kings_hero.png", config.gameUrl).href;
    const meta = `
<meta name="description" content="${esc(desc)}">
<meta property="og:type" content="website"><meta property="og:title" content="${esc(title)}">
<meta property="og:description" content="${esc(desc)}"><meta property="og:image" content="${esc(image)}">
<meta property="og:url" content="${esc(`${config.publicUrl}/i/${req.params.code}`)}"><meta name="twitter:card" content="summary_large_image">
<meta http-equiv="refresh" content="0; url=${esc(target)}">`;
    res.send(page(title, `<h1>${esc(title)}</h1><p>${esc(desc)}</p><a class="btn" href="${esc(target)}">Accept the challenge</a>`, meta));
  });

  // ---- password reset page (linked from the email)
  app.get("/reset/:token", (req, res) => {
    res.send(page("Reset your password", `
<h1>New password</h1>
<p>Choose a new password for your Chess Wars commander.</p>
<form id="f"><input id="p1" type="password" autocomplete="new-password" placeholder="New password (8+ characters)" required>
<input id="p2" type="password" autocomplete="new-password" placeholder="Same again" required>
<p class="msg" id="m"></p><button type="submit">Save password</button></form>
<script>
  const token = ${JSON.stringify(req.params.token)};
  document.getElementById("f").addEventListener("submit", async e => {
    e.preventDefault();
    const m = document.getElementById("m"), a = document.getElementById("p1").value, b = document.getElementById("p2").value;
    if (a !== b) { m.textContent = "The two passwords don't match."; return; }
    const r = await fetch("/api/auth/reset", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token, password: a }) });
    const data = await r.json().catch(() => ({}));
    if (!r.ok) { m.textContent = data.message || "Something went wrong."; return; }
    document.querySelector(".card").innerHTML = '<h1>Password saved</h1><p class="ok">Sign in with your new password.</p><a class="btn" href=${JSON.stringify(config.gameUrl)}>Back to Chess Wars</a>';
  });
</script>`));
  });

  app.use((req, res) => res.status(404).json({ error: "not-found" }));
  return app;
}

module.exports = { build, allowOrigin };
