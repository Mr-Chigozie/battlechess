"use strict";

// All settings come from environment variables (or server/.env). See server/.env.example.
const path = require("path");
require("dotenv").config({ path: path.join(__dirname, "..", ".env") });

const env = process.env;
const list = value => (value || "").split(",").map(s => s.trim()).filter(Boolean);

const config = {
  env: env.NODE_ENV || "development",
  port: Number(env.PORT || 8080),
  // MySQL: either one URL (as Railway/Render provide) or separate parts.
  db: env.DATABASE_URL || env.MYSQL_URL
    ? { uri: env.DATABASE_URL || env.MYSQL_URL }
    : {
        host: env.DB_HOST || "127.0.0.1",
        port: Number(env.DB_PORT || 3306),
        user: env.DB_USER || "chesswars",
        password: env.DB_PASSWORD || "",
        database: env.DB_NAME || "chesswars"
      },
  // Sites allowed to call the API (the game page). Localhost on any port is always allowed in development.
  clientOrigins: list(env.CLIENT_ORIGINS || "https://mr-chigozie.github.io"),
  // Public address of the game page, used for invite and password-reset links.
  gameUrl: env.GAME_URL || "https://mr-chigozie.github.io/battlechess/battle3d.html",
  // Public address of this server, used for invite link previews.
  publicUrl: (env.PUBLIC_URL || `http://localhost:${env.PORT || 8080}`).replace(/\/$/, ""),
  // Closed beta: when set, registering a full account needs this code. Guests arriving by invite are always welcome.
  registrationCode: env.REGISTRATION_CODE || "",
  // Key for the /api/admin/stats dashboard.
  adminKey: env.ADMIN_KEY || "",
  // Email for password resets: Resend (resend.com). Without a key, emails are printed to the server log.
  resendKey: env.RESEND_API_KEY || "",
  mailFrom: env.MAIL_FROM || "Chess Wars <no-reply@chesswars.app>",
  sessionDays: Number(env.SESSION_DAYS || 60),
  enginePath: env.ENGINE_PATH || path.join(__dirname, "..", "..", "js", "engine.js"),
  armiesPath: env.ARMIES_PATH || path.join(__dirname, "..", "..", "js", "armies.js")
};

config.isDev = config.env !== "production";
config.isTest = config.env === "test";

module.exports = config;
