# Chess Wars

Standard chess, fought by themed armies: Marines, Army of the Dead, Order of Knights, Asgardians, Vikings, Samurai Clans, Spartans and the Machine Legion.

- **`battle3d.html`**: the 3D battle (Three.js). Captures play out as short cinematic kill scenes, with kill combos and blood that fades. The board catches fire as the fight gets tense, and every battlefield has its own terrain. Includes a story campaign and skirmish mode.
- **`index.html`**: the 2D board, with the same rules and armies.

The rules are full FIDE chess: castling, en passant, promotion, check, checkmate, stalemate and draws. They are verified with perft tests. Both versions can be played against the built-in computer opponent.

No build step. Open `index.html` or `battle3d.html` in a browser. The 3D version loads Three.js from a CDN, so it needs an internet connection.

## Online battles

The 3D version has an **Online battle** mode, played against the Chess Wars server in [`server/`](server/README.md) (Node + MySQL).

- **Accounts**: register a commander, or play as a **guest** straight from an invite link and keep the record later by creating an account.
- **Invite links**: create a link in the War Room and send it on WhatsApp. It shows a preview card, and your friend can play in the browser with no sign-up.
- **War Room**: see who's online, challenge them, and resume battles in progress.
- **Refereed battles**: the server checks every move with the same rules file, runs the clocks (live 10 min + 5 s, or one move a day), and decides every result. Resign, offer a draw, and battle cries (preset only, no free chat). Block or report players.
- **Launch numbers**: sign-ups, invites opened and accepted, battles finished, day-1 and day-7 retention, at `/api/admin/stats`.

The game page finds the server through [`js/server-config.js`](js/server-config.js). On `localhost` it uses a local server; on the live site online play stays off ("coming soon") until `LIVE_SERVER` is set to the hosted server's address. Hosting steps are in [`server/README.md`](server/README.md). [Privacy](privacy.html) and [Terms](terms.html) are beta drafts.

| File | What it holds |
|---|---|
| `js/engine.js` | rules engine and computer opponent (shared) |
| `js/armies.js` | armies, unit names, taunts, story text (shared) |
| `js/models3d.js` | procedural 3D units |
| `js/stage3d.js` | scene, board, moves and capture scenes |
| `js/fx3d.js` | animation, particles, synthesized sound |
| `js/terrain3d.js` | battlefield terrain and weather |
| `js/war3d.js` | battle fury: fire, embers, burning sky |
| `js/game3d.js` | game flow, combos, story campaign |
| `js/online3d.js` | accounts, guests, War Room, invite links, live battles |
| `js/server-config.js` | where the online server is |
| `server/` | the online server (Node + MySQL) |
| `art/` | character generator (Blender) and the unit review page |
