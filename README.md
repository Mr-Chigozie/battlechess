# Chess Wars

Standard chess, fought by themed armies: Marines, Army of the Dead, Order of Knights, Asgardians, Vikings, Samurai Clans, Spartans and the Machine Legion.

- **`battle3d.html`**: the 3D battle (Three.js). Captures play out as short cinematic kill scenes, with kill combos and blood that fades. The board catches fire as the fight gets tense, and every battlefield has its own terrain. Includes a story campaign and skirmish mode.
- **`index.html`**: the 2D board, with the same rules and armies.

The rules are full FIDE chess: castling, en passant, promotion, check, checkmate, stalemate and draws. They are verified with perft tests. Both versions can be played against the built-in computer opponent.

No build step. Open `index.html` or `battle3d.html` in a browser. The 3D version loads Three.js from a CDN, so it needs an internet connection.

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
