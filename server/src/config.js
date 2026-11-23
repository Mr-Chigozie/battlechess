"use strict";

// All settings come from environment variables (or server/.env). See server/.env.example.
const path = require("path");
require("dotenv").config({ path: path.join(__dirname, "..", ".env") });

const env = process.env;
const list = value => (value || "").split(",").map(s => s.trim()).filter(Boolean);

// Hosted MySQL (Aiven, TiDB Cloud...) needs encrypted connections. Hosts write that in different ways:
// "?ssl-mode=REQUIRED" on the URL, DB_SSL=true, or DB_SSL_CA holding the provider's CA certificate.
const rawUrl = env.DATABASE_URL || env.MYSQL_URL || "";
const urlWantsSsl = /[?&](ssl-?mode|sslmode)=(required|verify_ca|verify_identity)/i.test(rawUrl);
const dbUrl = rawUrl.replace(/\?.*$/, "");  // the driver doesn't understand those query options
const sslCa = (env.DB_SSL_CA || "").replace(/\\n/g, "\n").trim();
const dbSsl = sslCa
  ? { ca: sslCa, rejectUnauthorized: true }
  : urlWantsSsl || /^(1|true|required)$/i.test(env.DB_SSL || "")
    ? { rejectUnauthorized: true, minVersion: "TLSv1.2" }
    : null;

const config = {
  env: env.NODE_ENV || "development",
  port: Number(env.PORT || 8080),
  dbSsl,
  // Free database plans allow only a few connections.
  dbPoolSize: Number(env.DB_POOL_SIZE || 5),
  // MySQL: either one URL (as most hosts provide) or separate parts.
  db: dbUrl
    ? { uri: dbUrl }
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
  // (Render sets RENDER_EXTERNAL_URL by itself.)
  publicUrl: (env.PUBLIC_URL || env.RENDER_EXTERNAL_URL || `http://localhost:${env.PORT || 8080}`).replace(/\/$/, ""),
  // Closed beta: when set, registering a full account needs this code. Guests arriving by invite are always welcome.
  registrationCode: env.REGISTRATION_CODE || "",
  // Key for the /api/admin/stats dashboard.
  adminKey: env.ADMIN_KEY || "",
  // Email for password resets: Resend (resend.com). Without a key, emails are printed to the server log.
  resendKey: env.RESEND_API_KEY || "",
  mailFrom: env.MAIL_FROM || "Chess Wars <no-reply@chesswars.app>",
  sessionDays: Number(env.SESSION_DAYS || 60),
  // Android App Links: the Chess Wars app opens invite links (/i/CODE) itself when installed. Android checks
  // /.well-known/assetlinks.json for the app's package and the SHA-256 fingerprints of the keys that sign it.
  // Set ANDROID_CERT_SHA256 to the release fingerprints (Play Console → App integrity), comma-separated.
  // Until then: the development builds' debug key.
  androidPackage: env.ANDROID_PACKAGE || "com.chesswars.game",
  androidCerts: list(env.ANDROID_CERT_SHA256 ||
    "EB:23:5B:D7:0B:A9:B7:6D:49:0B:33:56:9E:A7:FE:58:2A:84:3D:CA:40:33:24:39:0E:2E:D1:3A:7F:2C:C6:B7"),
  enginePath: env.ENGINE_PATH || path.join(__dirname, "..", "..", "js", "engine.js"),
  armiesPath: env.ARMIES_PATH || path.join(__dirname, "..", "..", "js", "armies.js")
};

config.isDev = config.env !== "production";
config.isTest = config.env === "test";

module.exports = config;
