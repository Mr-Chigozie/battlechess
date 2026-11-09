-- Chess Wars: first schema. Never edit a migration that has run on the live database;
-- add a new numbered file instead.

CREATE TABLE users (
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  name          VARCHAR(20)  NOT NULL,
  name_lower    VARCHAR(40)  NOT NULL,
  email         VARCHAR(190) NULL,
  password_hash VARCHAR(100) NULL,
  is_guest      TINYINT(1)   NOT NULL DEFAULT 0,
  army          VARCHAR(20)  NOT NULL DEFAULT 'knights',
  wins          INT UNSIGNED NOT NULL DEFAULT 0,
  losses        INT UNSIGNED NOT NULL DEFAULT 0,
  draws         INT UNSIGNED NOT NULL DEFAULT 0,
  created_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  last_seen_at  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  deleted_at    DATETIME(3)  NULL,
  UNIQUE KEY users_name (name_lower),
  UNIQUE KEY users_email (email),
  KEY users_seen (last_seen_at)
);

-- Sign-in tokens. Only a SHA-256 of the token is stored, so a database leak can't be used to sign in.
CREATE TABLE sessions (
  token_hash CHAR(64)        NOT NULL PRIMARY KEY,
  user_id    BIGINT UNSIGNED NOT NULL,
  created_at DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  expires_at DATETIME(3)     NOT NULL,
  KEY sessions_user (user_id),
  CONSTRAINT sessions_user_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);

CREATE TABLE password_resets (
  token_hash CHAR(64)        NOT NULL PRIMARY KEY,
  user_id    BIGINT UNSIGNED NOT NULL,
  expires_at DATETIME(3)     NOT NULL,
  used_at    DATETIME(3)     NULL,
  CONSTRAINT resets_user_fk FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE
);

-- A battle. Clocks are kept in milliseconds; turn_started_at is when the side to move started thinking.
CREATE TABLE games (
  id               CHAR(12)        NOT NULL PRIMARY KEY,
  white_id         BIGINT UNSIGNED NOT NULL,
  black_id         BIGINT UNSIGNED NOT NULL,
  white_name       VARCHAR(40)     NOT NULL,
  black_name       VARCHAR(40)     NOT NULL,
  white_army       VARCHAR(20)     NOT NULL,
  black_army       VARCHAR(20)     NOT NULL,
  status           ENUM('active', 'finished') NOT NULL DEFAULT 'active',
  result           ENUM('w', 'b', 'draw') NULL,
  reason           VARCHAR(20)     NULL,
  clock_type       ENUM('live', 'daily') NOT NULL,
  clock_initial_ms INT UNSIGNED    NOT NULL,
  clock_inc_ms     INT UNSIGNED    NOT NULL DEFAULT 0,
  white_ms         BIGINT          NOT NULL,
  black_ms         BIGINT          NOT NULL,
  turn_started_at  BIGINT          NOT NULL,
  ply              SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  draw_offer       CHAR(1)         NULL,
  source           ENUM('challenge', 'invite') NOT NULL,
  created_at       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  updated_at       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  finished_at      DATETIME(3)     NULL,
  KEY games_white (white_id, status),
  KEY games_black (black_id, status),
  KEY games_status (status)
);

CREATE TABLE moves (
  game_id    CHAR(12)          NOT NULL,
  ply        SMALLINT UNSIGNED NOT NULL,
  from_row   TINYINT UNSIGNED  NOT NULL,
  from_col   TINYINT UNSIGNED  NOT NULL,
  to_row     TINYINT UNSIGNED  NOT NULL,
  to_col     TINYINT UNSIGNED  NOT NULL,
  promotion  CHAR(1)           NULL,
  notation   VARCHAR(12)       NOT NULL,
  played_at  DATETIME(3)       NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (game_id, ply),
  CONSTRAINT moves_game_fk FOREIGN KEY (game_id) REFERENCES games (id) ON DELETE CASCADE
);

-- A challenge from one commander to another in the War Room.
CREATE TABLE challenges (
  id               BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  from_id          BIGINT UNSIGNED NOT NULL,
  to_id            BIGINT UNSIGNED NOT NULL,
  clock_type       ENUM('live', 'daily') NOT NULL,
  status           ENUM('pending', 'accepted', 'declined', 'cancelled', 'expired') NOT NULL DEFAULT 'pending',
  game_id          CHAR(12)        NULL,
  created_at       DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  KEY challenges_to (to_id, status),
  KEY challenges_from (from_id, status)
);

-- A shareable invite link. Whoever opens it first (guest or member) plays the battle.
CREATE TABLE invites (
  code        CHAR(8)         NOT NULL PRIMARY KEY,
  from_id     BIGINT UNSIGNED NOT NULL,
  clock_type  ENUM('live', 'daily') NOT NULL,
  status      ENUM('open', 'used', 'cancelled') NOT NULL DEFAULT 'open',
  game_id     CHAR(12)        NULL,
  opened      INT UNSIGNED    NOT NULL DEFAULT 0,
  created_at  DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  expires_at  DATETIME(3)     NOT NULL,
  KEY invites_from (from_id, status)
);

CREATE TABLE blocks (
  blocker_id BIGINT UNSIGNED NOT NULL,
  blocked_id BIGINT UNSIGNED NOT NULL,
  created_at DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (blocker_id, blocked_id)
);

CREATE TABLE reports (
  id          BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  reporter_id BIGINT UNSIGNED NOT NULL,
  reported_id BIGINT UNSIGNED NOT NULL,
  game_id     CHAR(12)        NULL,
  reason      VARCHAR(200)    NOT NULL,
  created_at  DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
);

-- Product analytics: sign-ups, invites, battles. Answers "are people taking to the platform?"
CREATE TABLE events (
  id         BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  user_id    BIGINT UNSIGNED NULL,
  name       VARCHAR(40)     NOT NULL,
  props      JSON            NULL,
  created_at DATETIME(3)     NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  KEY events_name (name, created_at),
  KEY events_user (user_id, created_at)
);
