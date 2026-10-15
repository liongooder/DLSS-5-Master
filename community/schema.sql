-- DLSS 5 Master community reports (Cloudflare D1 / SQLite).
-- One report per install per game; the install ID is stored only as a salted hash.

CREATE TABLE IF NOT EXISTS reports (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  game_key    TEXT    NOT NULL,           -- "steam:<appid>" or "name:<normalized name>"
  game_name   TEXT    NOT NULL,
  install     TEXT    NOT NULL,           -- SHA-256(install id + pepper)
  rating      TEXT    NOT NULL CHECK (rating IN ('works', 'partly', 'broken')),
  route       TEXT    NOT NULL,           -- native | multipass | feeder | optiscaler
  mfg         INTEGER NOT NULL DEFAULT 0, -- MFG unlock used
  gpu         TEXT    NOT NULL,           -- e.g. "NVIDIA GeForce RTX 4090"
  gpu_family  TEXT    NOT NULL,           -- e.g. "RTX 40"
  versions    TEXT    NOT NULL DEFAULT '',-- add-on versions used, short JSON
  app_version TEXT    NOT NULL DEFAULT '',
  note        TEXT    NOT NULL DEFAULT '',
  hidden      INTEGER NOT NULL DEFAULT 0, -- hidden by flags or by the moderator
  created_at  INTEGER NOT NULL,
  updated_at  INTEGER NOT NULL,
  UNIQUE (install, game_key)
);
CREATE INDEX IF NOT EXISTS reports_game ON reports (game_key, hidden, updated_at DESC);

CREATE TABLE IF NOT EXISTS flags (
  report_id  INTEGER NOT NULL,
  install    TEXT    NOT NULL,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (report_id, install)
);

-- Fixed-window rate limiting: bucket = "<kind>:<hash>", win = hour number.
CREATE TABLE IF NOT EXISTS hits (
  bucket TEXT    NOT NULL,
  win    INTEGER NOT NULL,
  n      INTEGER NOT NULL,
  PRIMARY KEY (bucket, win)
);
