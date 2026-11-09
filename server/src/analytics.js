"use strict";

const db = require("./db");

// Fire-and-forget product event. Never lets analytics break gameplay.
function track(userId, name, props = null) {
  db.query("INSERT INTO events (user_id, name, props) VALUES (?, ?, ?)", [userId || null, name, props ? JSON.stringify(props) : null])
    .catch(error => console.error("analytics:", error.message));
}

// Event names the game page may send itself (everything else is recorded by the server).
const CLIENT_EVENTS = new Set(["invite_shared", "invite_link_copied", "online_opened", "guest_prompt_seen", "upgrade_prompt_seen"]);

// The launch dashboard: is the platform catching on?
async function stats(days = 30) {
  const since = `DATE_SUB(UTC_TIMESTAMP(3), INTERVAL ${Math.max(1, Math.min(365, Number(days) || 30))} DAY)`;
  const count = async (name) => (await db.one(`SELECT COUNT(*) AS n FROM events WHERE name = ? AND created_at > ${since}`, [name])).n;
  const [registered, guests, upgraded, invitesCreated, invitesAccepted, gamesStarted, gamesFinished] = await Promise.all(
    ["register", "guest_created", "guest_upgraded", "invite_created", "invite_accepted", "game_started", "game_finished"].map(count));
  const opened = (await db.one(`SELECT COALESCE(SUM(opened), 0) AS n FROM invites WHERE created_at > ${since}`)).n;
  // Retention: of the accounts created 1+ / 7+ days ago in the window, how many came back on or after day 1 / day 7.
  const retention = async (d) => db.one(
    `SELECT COUNT(*) AS cohort, SUM(last_seen_at >= DATE_ADD(created_at, INTERVAL ? DAY)) AS returned
       FROM users WHERE deleted_at IS NULL AND created_at > ${since} AND created_at < DATE_SUB(UTC_TIMESTAMP(3), INTERVAL ? DAY)`, [d, d]);
  const [d1, d7] = await Promise.all([retention(1), retention(7)]);
  const pct = (a, b) => (b ? Math.round((Number(a) / Number(b)) * 100) : null);
  const reasons = await db.query(`SELECT reason, COUNT(*) AS n FROM games WHERE finished_at > ${since} GROUP BY reason ORDER BY n DESC`);
  return {
    windowDays: Number(days) || 30,
    accounts: { registered, guests, guestsUpgraded: upgraded, guestUpgradeRate: pct(upgraded, guests) },
    invites: { created: invitesCreated, opened: Number(opened), accepted: invitesAccepted, acceptRate: pct(invitesAccepted, invitesCreated) },
    battles: { started: gamesStarted, finished: gamesFinished, finishRate: pct(gamesFinished, gamesStarted), endings: reasons },
    retention: {
      day1: { cohort: d1.cohort, returned: Number(d1.returned || 0), rate: pct(d1.returned || 0, d1.cohort) },
      day7: { cohort: d7.cohort, returned: Number(d7.returned || 0), rate: pct(d7.returned || 0, d7.cohort) }
    }
  };
}

module.exports = { track, stats, CLIENT_EVENTS };
