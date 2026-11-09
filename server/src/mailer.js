"use strict";

const config = require("./config");

// Last few emails, for automated tests (only kept outside production).
const outbox = [];

async function send({ to, subject, text }) {
  if (config.isDev) outbox.push({ to, subject, text, at: Date.now() });
  if (outbox.length > 20) outbox.shift();
  if (!config.resendKey) {
    console.log(`\n--- email (no RESEND_API_KEY set, so it's printed instead) ---\nTo: ${to}\nSubject: ${subject}\n\n${text}\n---\n`);
    return;
  }
  const response = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: { Authorization: `Bearer ${config.resendKey}`, "Content-Type": "application/json" },
    body: JSON.stringify({ from: config.mailFrom, to: [to], subject, text })
  });
  if (!response.ok) console.error("email failed:", response.status, await response.text().catch(() => ""));
}

module.exports = { send, outbox };
