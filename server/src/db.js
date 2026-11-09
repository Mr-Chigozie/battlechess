"use strict";

const fs = require("fs");
const path = require("path");
const mysql = require("mysql2/promise");
const config = require("./config");

const base = { timezone: "Z", supportBigNumbers: true, bigNumberStrings: false, ...(config.dbSsl ? { ssl: config.dbSsl } : {}) };
const options = { ...base, waitForConnections: true, connectionLimit: config.dbPoolSize, enableKeepAlive: true };
let pool = null;

function getPool() {
  if (!pool) pool = config.db.uri ? mysql.createPool({ uri: config.db.uri, ...options }) : mysql.createPool({ ...config.db, ...options });
  return pool;
}

async function query(sql, params = []) {
  const [rows] = await getPool().query(sql, params);
  return rows;
}

async function one(sql, params = []) {
  const rows = await query(sql, params);
  return rows[0] || null;
}

// Runs fn inside a transaction; fn gets a query function bound to the transaction's connection.
async function tx(fn) {
  const conn = await getPool().getConnection();
  try {
    await conn.beginTransaction();
    const q = async (sql, params = []) => (await conn.query(sql, params))[0];
    const result = await fn(q);
    await conn.commit();
    return result;
  } catch (error) {
    await conn.rollback().catch(() => {});
    throw error;
  } finally {
    conn.release();
  }
}

// Applies every migrations/NNN_*.sql file that hasn't run yet, in order.
async function migrate(log = console.log) {
  const conn = config.db.uri
    ? await mysql.createConnection({ uri: config.db.uri, ...base, multipleStatements: true })
    : await mysql.createConnection({ ...config.db, ...base, multipleStatements: true });
  try {
    await conn.query("CREATE TABLE IF NOT EXISTS schema_migrations (version VARCHAR(100) PRIMARY KEY, applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3))");
    const [done] = await conn.query("SELECT version FROM schema_migrations");
    const applied = new Set(done.map(r => r.version));
    const dir = path.join(__dirname, "..", "migrations");
    for (const file of fs.readdirSync(dir).filter(f => /^\d+_.*\.sql$/.test(f)).sort()) {
      if (applied.has(file)) continue;
      log(`migrating: ${file}`);
      await conn.query(fs.readFileSync(path.join(dir, file), "utf8"));
      await conn.query("INSERT INTO schema_migrations (version) VALUES (?)", [file]);
    }
  } finally {
    await conn.end();
  }
}

async function close() {
  if (pool) await pool.end();
  pool = null;
}

module.exports = { query, one, tx, migrate, close };
