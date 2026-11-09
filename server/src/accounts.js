"use strict";

const crypto = require("crypto");
const bcrypt = require("bcryptjs");
const db = require("./db");
const config = require("./config");
const { isArmy } = require("./rules");
const { track } = require("./analytics");
const mailer = require("./mailer");

class UserError extends Error {
  constructor(code, message, status = 400) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

const sha256 = text => crypto.createHash("sha256").update(text).digest("hex");
const newToken = () => crypto.randomBytes(32).toString("base64url");

// Commander names: 2-20 letters, digits, spaces, _ . -   and nothing offensive.
const NAME_RE = /^[A-Za-z0-9][A-Za-z0-9 _.-]{0,18}[A-Za-z0-9]$/;
const BLOCKED_WORDS = ["fuck", "shit", "cunt", "nigg", "fag", "bitch", "whore", "rape", "nazi", "hitler", "admin", "moderator", "chesswars"];
function checkName(name) {
  const clean = String(name || "").trim().replace(/\s+/g, " ");
  if (!NAME_RE.test(clean)) throw new UserError("bad-name", "Pick a commander name of 2 to 20 letters or numbers.");
  // Undo look-alike digits first (sh1t, n1gg...), then ignore spaces and punctuation (f.u.c.k).
  const squashed = clean.toLowerCase().replace(/[1!|]/g, "i").replace(/0/g, "o").replace(/3/g, "e").replace(/4/g, "a")
    .replace(/5/g, "s").replace(/7/g, "t").replace(/[^a-z]/g, "");
  if (BLOCKED_WORDS.some(w => squashed.includes(w))) throw new UserError("bad-name", "That name isn't allowed. Pick another.");
  return clean;
}
function checkEmail(email) {
  const clean = String(email || "").trim().toLowerCase();
  if (clean.length > 190 || !/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(clean)) throw new UserError("bad-email", "That email address doesn't look right.");
  return clean;
}
function checkPassword(password) {
  const p = String(password || "");
  if (p.length < 8) throw new UserError("weak-password", "Passwords need at least 8 characters.");
  if (p.length > 200) throw new UserError("weak-password", "That password is too long.");
  return p;
}
const checkArmy = army => (isArmy(army) ? army : "knights");

function publicUser(row) {
  return {
    id: row.id, name: row.name, army: row.army, guest: Boolean(row.is_guest),
    wins: row.wins, losses: row.losses, draws: row.draws
  };
}

async function createSession(userId) {
  const token = newToken();
  await db.query("INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (?, ?, DATE_ADD(UTC_TIMESTAMP(3), INTERVAL ? DAY))",
    [sha256(token), userId, config.sessionDays]);
  return token;
}

async function userFromToken(token) {
  if (!token || typeof token !== "string" || token.length > 100) return null;
  return db.one(`SELECT u.* FROM sessions s JOIN users u ON u.id = s.user_id
                 WHERE s.token_hash = ? AND s.expires_at > UTC_TIMESTAMP(3) AND u.deleted_at IS NULL`, [sha256(token)]);
}

async function nameTaken(name, exceptId = 0) {
  return Boolean(await db.one("SELECT id FROM users WHERE name_lower = ? AND id <> ?", [name.toLowerCase(), exceptId]));
}

async function register({ name, email, password, army, code }) {
  if (config.registrationCode && code !== config.registrationCode) {
    throw new UserError("beta", "Chess Wars is in closed beta. You need an invite code to register, or accept a friend's battle link to play as a guest.", 403);
  }
  name = checkName(name);
  email = checkEmail(email);
  password = checkPassword(password);
  if (await nameTaken(name)) throw new UserError("name-taken", "That commander name is taken. Pick another.");
  if (await db.one("SELECT id FROM users WHERE email = ?", [email])) throw new UserError("email-taken", "That email already has an account. Sign in instead.");
  const hash = await bcrypt.hash(password, 10);
  const result = await db.query("INSERT INTO users (name, name_lower, email, password_hash, army) VALUES (?, ?, ?, ?, ?)",
    [name, name.toLowerCase(), email, hash, checkArmy(army)]);
  track(result.insertId, "register");
  return { token: await createSession(result.insertId), user: publicUser(await db.one("SELECT * FROM users WHERE id = ?", [result.insertId])) };
}

async function login({ email, password }) {
  const clean = String(email || "").trim().toLowerCase();
  const row = clean && await db.one("SELECT * FROM users WHERE email = ? AND deleted_at IS NULL", [clean]);
  // Compare against a dummy hash when the email is unknown, so timing doesn't reveal which emails exist.
  const ok = await bcrypt.compare(String(password || ""), row?.password_hash || "$2a$10$abcdefghijklmnopqrstuuJ7y0V5bQm1PZ4v8wF9bq7C3c7sF4lW2");
  if (!row || !ok) throw new UserError("bad-login", "Wrong email or password.", 401);
  track(row.id, "login");
  return { token: await createSession(row.id), user: publicUser(row) };
}

// Guests play straight from an invite link and can turn into a full account later, keeping their record.
async function guest({ name, army }) {
  name = checkName(name);
  if (await nameTaken(name)) throw new UserError("name-taken", "That commander name is taken. Pick another.");
  const result = await db.query("INSERT INTO users (name, name_lower, is_guest, army) VALUES (?, ?, 1, ?)", [name, name.toLowerCase(), checkArmy(army)]);
  track(result.insertId, "guest_created");
  return { token: await createSession(result.insertId), user: publicUser(await db.one("SELECT * FROM users WHERE id = ?", [result.insertId])) };
}

async function upgrade(user, { name, email, password }) {
  if (!user.is_guest) throw new UserError("not-guest", "You already have an account.");
  name = name ? checkName(name) : user.name;
  email = checkEmail(email);
  password = checkPassword(password);
  if (await nameTaken(name, user.id)) throw new UserError("name-taken", "That commander name is taken. Pick another.");
  if (await db.one("SELECT id FROM users WHERE email = ?", [email])) throw new UserError("email-taken", "That email already has an account. Sign in instead.");
  await db.query("UPDATE users SET name = ?, name_lower = ?, email = ?, password_hash = ?, is_guest = 0 WHERE id = ?",
    [name, name.toLowerCase(), email, await bcrypt.hash(password, 10), user.id]);
  track(user.id, "guest_upgraded");
  return publicUser(await db.one("SELECT * FROM users WHERE id = ?", [user.id]));
}

async function logout(token) {
  if (token) await db.query("DELETE FROM sessions WHERE token_hash = ?", [sha256(token)]);
}

async function updateProfile(user, { army }) {
  if (army !== undefined) {
    if (!isArmy(army)) throw new UserError("bad-army", "Unknown army.");
    await db.query("UPDATE users SET army = ? WHERE id = ?", [army, user.id]);
  }
  return publicUser(await db.one("SELECT * FROM users WHERE id = ?", [user.id]));
}

// Account deletion (a data-protection requirement): the person's details are wiped; finished battles
// stay in rivals' histories under an anonymous name.
async function deleteAccount(user, { password }) {
  if (!user.is_guest) {
    const ok = await bcrypt.compare(String(password || ""), user.password_hash || "");
    if (!ok) throw new UserError("bad-login", "That password isn't right.", 401);
  }
  const anon = `Deleted #${user.id}`;
  await db.tx(async q => {
    await q("UPDATE users SET name = ?, name_lower = ?, email = NULL, password_hash = NULL, deleted_at = UTC_TIMESTAMP(3) WHERE id = ?",
      [anon, `deleted-${user.id}`, user.id]);
    await q("UPDATE games SET white_name = ? WHERE white_id = ?", [anon, user.id]);
    await q("UPDATE games SET black_name = ? WHERE black_id = ?", [anon, user.id]);
    await q("DELETE FROM sessions WHERE user_id = ?", [user.id]);
    await q("DELETE FROM password_resets WHERE user_id = ?", [user.id]);
    await q("UPDATE challenges SET status = 'cancelled' WHERE (from_id = ? OR to_id = ?) AND status = 'pending'", [user.id, user.id]);
    await q("UPDATE invites SET status = 'cancelled' WHERE from_id = ? AND status = 'open'", [user.id]);
  });
  track(user.id, "account_deleted");
}

// Always answers the same way, so the form can't be used to find out which emails have accounts.
async function forgotPassword({ email }) {
  const clean = String(email || "").trim().toLowerCase();
  const row = clean && await db.one("SELECT * FROM users WHERE email = ? AND deleted_at IS NULL", [clean]);
  if (!row) return;
  const token = newToken();
  await db.query("INSERT INTO password_resets (token_hash, user_id, expires_at) VALUES (?, ?, DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 1 HOUR))",
    [sha256(token), row.id]);
  const link = `${config.publicUrl}/reset/${token}`;
  await mailer.send({
    to: row.email,
    subject: "Reset your Chess Wars password",
    text: `Commander ${row.name},\n\nSomeone asked to reset your Chess Wars password. If it was you, open this link within an hour:\n\n${link}\n\nIf it wasn't you, ignore this email; your password stays the same.`
  });
  track(row.id, "password_reset_requested");
}

async function resetPassword({ token, password }) {
  password = checkPassword(password);
  const row = await db.one("SELECT * FROM password_resets WHERE token_hash = ? AND used_at IS NULL AND expires_at > UTC_TIMESTAMP(3)", [sha256(String(token || ""))]);
  if (!row) throw new UserError("bad-token", "This reset link has expired or was already used. Ask for a new one.");
  await db.tx(async q => {
    await q("UPDATE users SET password_hash = ? WHERE id = ?", [await bcrypt.hash(password, 10), row.user_id]);
    await q("UPDATE password_resets SET used_at = UTC_TIMESTAMP(3) WHERE token_hash = ?", [row.token_hash]);
    await q("DELETE FROM sessions WHERE user_id = ?", [row.user_id]);  // sign out everywhere
  });
  track(row.user_id, "password_reset");
}

async function touch(userId) {
  await db.query("UPDATE users SET last_seen_at = UTC_TIMESTAMP(3) WHERE id = ?", [userId]);
}

module.exports = {
  UserError, publicUser, userFromToken, register, login, guest, upgrade, logout, updateProfile, deleteAccount,
  forgotPassword, resetPassword, touch
};
