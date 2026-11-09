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

## Deploy (free: Render + TiDB Cloud)

1. **Database:** on tidbcloud.com create a free Starter (serverless) cluster in Frankfurt (eu-central-1). In the
   SQL Editor run `CREATE DATABASE chesswars;`. Under **Connect**, generate a password and copy the connection
   string, changing the database at the end to `/chesswars`.
2. **Server:** on render.com, **New → Blueprint** → this repo. `render.yaml` sets everything up; paste the
   connection string into `DATABASE_URL` (leave `DB_SSL_CA` empty), and set `REGISTRATION_CODE` for a closed beta.
   Tables are created on first start. Check `https://<server>/api/health` returns `{"ok":true,"db":true}`.
3. In `js/server-config.js` set `LIVE_SERVER` to the Render address and push. The live site switches online play on.
4. Free Render servers sleep after 15 idle minutes; a free UptimeRobot monitor on `/api/health` every 5 minutes
   keeps it awake.

Other MySQL hosts work too: Aiven (set `DB_SSL_CA` to its CA certificate), Railway, or any MySQL 8 with `DATABASE_URL`.
Tests against a remote database: set `DB_HOST/DB_PORT/DB_USER/DB_PASSWORD`, `DB_SSL=true` and `TEST_SLOW=5`.

Notes: it's built for **one server instance** (clocks and live battles are held in memory). That is plenty for the
beta; scaling out later means moving that state to Redis. Turn on the host's daily database backups.
