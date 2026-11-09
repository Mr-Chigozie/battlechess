# Chess Wars server

Node + MySQL backend for online play: accounts and guests, the War Room, challenges, shareable invite
links, and server-refereed battles with clocks. It runs the same rules file as the game (`../js/engine.js`),
so every move is checked on the server and results can't be faked.

## What it does

| Area | Details |
|---|---|
| Accounts | Register / sign in (bcrypt-hashed passwords, token sessions), guest play, guest → account upgrade keeping the record, password reset by email, account deletion, rate-limited sign-in, name filter |
| Invite links | `https://<server>/i/CODE`: a preview card for WhatsApp etc., then straight into the battle. A friend can play as a guest without signing up |
| Battles | Server validates every move, decides checkmate / stalemate / repetition / fifty-move / insufficient material, resign, draw offers, live clocks (10 min + 5 s) and daily (24 h per move), first-move abort, reconnect and resume |
| Safety | Preset battle cries only (no free chat), block, report |
| Launch numbers | `GET /api/admin/stats` (header `x-admin-key`): sign-ups, guests, guest→account rate, invites created / opened / accepted, battles started / finished, day-1 and day-7 retention |

## Run it locally

1. MySQL 8 running locally, with a `chesswars` database and user (see `.env.example`).
2. `cp .env.example .env` and fill in the database details (`NODE_ENV=development`, `PUBLIC_URL=http://localhost:8080`,
   `GAME_URL=http://localhost:<port>/battle3d.html`).
3. `npm install`, then `npm start`. Migrations run automatically on start.
4. Serve the repo root with any static server and open `battle3d.html` on `localhost`. The page talks to
   `http://localhost:8080` automatically (`js/server-config.js`).

Tests (need MySQL and a `chesswars_test` database the same user can use): `npm test`.

## Deploy (Railway, the simplest option)

1. New project → **Deploy from GitHub repo** → this repo. Set the service's **root directory** to the repo root
   and the **start command** to `node server/src/index.js` (the server reads `js/engine.js` and `js/armies.js`).
2. Add a **MySQL** database to the project; copy its connection URL into `DATABASE_URL`.
3. Set the other variables from `.env.example` (`NODE_ENV=production`, `PUBLIC_URL`, `GAME_URL`, `CLIENT_ORIGINS`,
   `ADMIN_KEY`, and `REGISTRATION_CODE` for a closed beta).
4. Give the service a public domain (ideally your own, e.g. `api.chesswars.ng`).
5. In `js/server-config.js` set `LIVE_SERVER` to that address and push. The live site switches online play on.

Notes: it's built for **one server instance** (clocks and live battles are held in memory). That is plenty for the
beta; scaling out later means moving that state to Redis. Turn on the host's daily database backups.
