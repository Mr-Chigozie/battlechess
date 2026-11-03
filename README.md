# Chess Wars

Standard chess, fought by themed armies: Marines, Army of the Dead, Order of Knights, Asgardians, Vikings, Samurai Clans, Spartans and the Machine Legion.

- **`battle3d.html`**: the 3D battle (Three.js). Captures play out as short cinematic kill scenes, with kill combos and blood that fades. The board catches fire as the fight gets tense, and every battlefield has its own terrain. Includes a story campaign and skirmish mode.
- **`index.html`**: the 2D board, with the same rules and armies.

The rules are full FIDE chess: castling, en passant, promotion, check, checkmate, stalemate and draws. They are verified with perft tests. Both versions can be played against the built-in computer opponent.

No build step. Open `index.html` or `battle3d.html` in a browser. The 3D version loads Three.js from a CDN, so it needs an internet connection.

## Online battles

The 3D version has an **Online battle** mode. Players register a commander (name, email, password, army), see who else is online in the War Room, challenge each other, and play live. The challenger plays Ivory and moves first. Players can leave a battle and resume it later, taunt their opponent across the board, and resign. Wins, losses and draws are kept on each profile.

Online play runs on [Firebase](https://firebase.google.com) (free Spark plan), so the site can stay on GitHub Pages. To switch it on:

1. Create a project at the [Firebase console](https://console.firebase.google.com).
2. **Authentication** → Get started → enable **Email/Password**.
3. **Firestore Database** → Create database. Then paste [`firestore.rules`](firestore.rules) into its **Rules** tab and publish.
4. **Authentication → Settings → Authorized domains**: add your Pages domain (e.g. `mr-chigozie.github.io`).
5. **Project settings → Your apps → Web app** (`</>`): copy the config object into [`js/firebase-config.js`](js/firebase-config.js) in place of `null`.

Until that's done, everything else works and the Online button explains what's missing.

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
| `js/online3d.js` | accounts, War Room, challenges, live move sync |
| `js/firebase-config.js` | your Firebase project config (online play) |
| `firestore.rules` | database security rules for online play |
