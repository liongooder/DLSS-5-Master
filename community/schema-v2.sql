-- DLSS 5 Master community, part 2: nicknames, private line, gallery, chat, bans.
-- Every "install" column holds SHA-256(install id + pepper), never the raw ID.

CREATE TABLE IF NOT EXISTS profiles (
  install     TEXT    PRIMARY KEY,
  nickname    TEXT    NOT NULL,
  nickname_lc TEXT    NOT NULL UNIQUE,
  is_dev      INTEGER NOT NULL DEFAULT 0,
  banned      INTEGER NOT NULL DEFAULT 0,
  created_at  INTEGER NOT NULL,
  updated_at  INTEGER NOT NULL
);

-- Pictures (JPEG/PNG, shrunk by the app before upload). Shown only through what links to them.
CREATE TABLE IF NOT EXISTS images (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  install    TEXT    NOT NULL,
  kind       TEXT    NOT NULL CHECK (kind IN ('gallery', 'private', 'chat')),
  mime       TEXT    NOT NULL,
  data       BLOB    NOT NULL,
  created_at INTEGER NOT NULL
);

-- Private line between one install and the developer.
CREATE TABLE IF NOT EXISTS private_msgs (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  install      TEXT    NOT NULL,
  from_dev     INTEGER NOT NULL DEFAULT 0,
  text         TEXT    NOT NULL DEFAULT '',
  image_id     INTEGER,
  read_by_dev  INTEGER NOT NULL DEFAULT 0,
  read_by_user INTEGER NOT NULL DEFAULT 0,
  created_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS private_thread ON private_msgs (install, id);

-- Gallery: nothing is public until the developer approves it.
CREATE TABLE IF NOT EXISTS gallery (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  install    TEXT    NOT NULL,
  image_id   INTEGER NOT NULL,
  caption    TEXT    NOT NULL DEFAULT '',
  game_name  TEXT    NOT NULL DEFAULT '',
  status     TEXT    NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected')),
  created_at INTEGER NOT NULL,
  decided_at INTEGER
);
CREATE INDEX IF NOT EXISTS gallery_status ON gallery (status, id DESC);

-- Public chat.
CREATE TABLE IF NOT EXISTS chat (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  install    TEXT    NOT NULL,
  text       TEXT    NOT NULL,
  reply_to   INTEGER,
  hidden     INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS reactions (
  chat_id INTEGER NOT NULL,
  install TEXT    NOT NULL,
  emoji   TEXT    NOT NULL,
  PRIMARY KEY (chat_id, install, emoji)
);
