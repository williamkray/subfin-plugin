-- Subfin Plugin SQLite schema.
-- No jellyfin_url anywhere — single-instance scope.

CREATE TABLE IF NOT EXISTS linked_devices (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  subsonic_username TEXT NOT NULL,
  app_password_hash TEXT NOT NULL,
  app_password_encrypted BLOB NOT NULL,
  jellyfin_user_id TEXT NOT NULL,
  device_label TEXT NOT NULL DEFAULT '',
  jellyfin_device_id TEXT,
  jellyfin_device_name TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_linked_devices_username ON linked_devices(subsonic_username);
CREATE INDEX IF NOT EXISTS idx_linked_devices_jellyfin_user ON linked_devices(jellyfin_user_id);

CREATE TABLE IF NOT EXISTS pending_quickconnect (
  secret TEXT PRIMARY KEY,
  jellyfin_user_id TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS play_queue (
  subsonic_username TEXT PRIMARY KEY,
  entry_ids TEXT NOT NULL DEFAULT '[]',
  current_id TEXT,
  current_index INTEGER NOT NULL DEFAULT 0,
  position_ms INTEGER NOT NULL DEFAULT 0,
  changed_at TEXT,
  changed_by TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS user_library_settings (
  subsonic_username TEXT PRIMARY KEY,
  selected_ids TEXT NOT NULL DEFAULT '[]',
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS shares (
  share_uid TEXT PRIMARY KEY,
  linked_device_id INTEGER NOT NULL REFERENCES linked_devices(id) ON DELETE CASCADE,
  entry_ids TEXT NOT NULL DEFAULT '[]',
  entry_ids_flat TEXT NOT NULL DEFAULT '[]',
  description TEXT,
  share_secret_encrypted BLOB,
  expires_at TEXT,
  visit_count INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_shares_linked_device ON shares(linked_device_id);

CREATE TABLE IF NOT EXISTS derived_cache (
  cache_key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  cached_at TEXT NOT NULL,
  last_source_change_at TEXT
);
