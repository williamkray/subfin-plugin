#!/usr/bin/env node
/**
 * Print decrypted Subfin-plugin credentials from the plugin SQLite DB.
 * Uses the salt from the plugin's XML config (or SUBFIN_SALT env var).
 *
 * Usage:
 *   node scripts/get-creds.js
 *   node scripts/get-creds.js /path/to/jellyfin-data/config
 *
 * Defaults to ../subfin/localenv/jellyfin-data/config relative to repo root.
 *
 * Key derivation: PBKDF2-SHA256, 100k iterations (matches Store/Crypto.cs).
 */
import { pbkdf2Sync, createDecipheriv } from "node:crypto";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { createRequire } from "node:module";

// better-sqlite3 lives in the adjacent subfin (Node.js) project's node_modules.
const _require = createRequire(
  resolve(dirname(new URL(import.meta.url).pathname), "../../subfin/package.json")
);
const Database = _require("better-sqlite3");

const repoRoot = resolve(dirname(new URL(import.meta.url).pathname), "..");
const jellyfinDataDir = resolve(
  process.argv[2] ?? resolve(repoRoot, "../subfin/localenv/jellyfin-data/config")
);

// Read salt from plugin XML config
const xmlPath = resolve(jellyfinDataDir, "plugins/configurations/Jellyfin.Plugin.Subsonic.xml");
const saltMatch = readFileSync(xmlPath, "utf-8").match(/<Salt>([^<]+)<\/Salt>/);
if (!saltMatch) { console.error("Could not find <Salt> in", xmlPath); process.exit(1); }
const saltRaw = process.env.SUBFIN_SALT ?? saltMatch[1].trim();

// PBKDF2-SHA256 with UTF-8 "subfin-db-encryption-v1" as salt (matches Crypto.cs)
const password = Buffer.from(saltRaw, "base64");
const kdfSalt = Buffer.from("subfin-db-encryption-v1", "utf8");
const key = pbkdf2Sync(password, kdfSalt, 100_000, 32, "sha256");

function decrypt(blob) {
  const buf = Buffer.from(blob);
  const iv = buf.subarray(0, 12);
  const tag = buf.subarray(12, 28);
  const ct = buf.subarray(28);
  const dec = createDecipheriv("aes-256-gcm", key, iv);
  dec.setAuthTag(tag);
  return dec.update(ct).toString("utf8") + dec.final("utf8");
}

const dbPath = resolve(jellyfinDataDir, "data/SubsonicPlugin/subsonic.db");
const db = new Database(dbPath, { readonly: true });

const devices = db.prepare(
  "SELECT id, subsonic_username, device_label, app_password_encrypted FROM linked_devices ORDER BY subsonic_username, id DESC"
).all();

if (!devices.length) { console.log("No linked devices."); process.exit(0); }

console.log(`\nLinked devices (${devices.length}):`);
for (const d of devices) {
  try {
    const pw = decrypt(d.app_password_encrypted);
    const label = d.device_label?.trim() ? ` [${d.device_label}]` : "";
    console.log(`  id=${d.id}  u=${d.subsonic_username}  p=${pw}${label}`);
  } catch {
    console.log(`  id=${d.id}  u=${d.subsonic_username}  p=<decrypt failed>`);
  }
}
console.log();
console.log(`Subsonic REST base: http://localhost:8096/rest`);
console.log(`Example: curl -s "http://localhost:8096/rest/ping.view?u=<user>&p=<pass>&v=1.16.1&c=test&f=json"`);
console.log();
