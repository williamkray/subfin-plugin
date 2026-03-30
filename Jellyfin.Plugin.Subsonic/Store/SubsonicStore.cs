using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Store;

/// <summary>Row returned from linked_devices for auth resolution.</summary>
public record LinkedDevice(
    long Id,
    string SubsonicUsername,
    string AppPasswordHash,
    byte[] AppPasswordEncrypted,
    string JellyfinUserId,
    string DeviceLabel,
    string? JellyfinDeviceId,
    string? JellyfinDeviceName,
    string CreatedAt);

/// <summary>Play queue record.</summary>
public record PlayQueueRecord(
    string SubsonicUsername,
    List<string> EntryIds,
    string? CurrentId,
    int CurrentIndex,
    long PositionMs,
    string? ChangedAt,
    string ChangedBy);

/// <summary>Share record.</summary>
public record ShareRecord(
    string ShareUid,
    long LinkedDeviceId,
    List<string> EntryIds,
    List<string> EntryIdsFlat,
    string? Description,
    byte[]? ShareSecretEncrypted,
    string? ExpiresAt,
    int VisitCount,
    string CreatedAt);

/// <summary>
/// All SQLite access goes through this class.
/// Thread-safe: serializes all access via a static lock over a single WAL-mode connection.
/// </summary>
public static class SubsonicStore
{
    private static SqliteConnection? _db;
    private static string _salt = string.Empty;
    private static readonly object _lock = new();

    public static void Initialize(string dbPath, string salt)
    {
        _salt = salt;
        Crypto.SetSalt(salt);

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();

        using var pragma = _db.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        RunSchema();
    }

    private static SqliteConnection Db => _db ?? throw new InvalidOperationException("Store not initialized");

    private static void RunSchema()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.Subsonic.Store.Schema.sql")
            ?? throw new FileNotFoundException("Embedded Schema.sql not found");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        using var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── Linked Devices ──────────────────────────────────────────────────────

    public static LinkedDevice? GetDeviceByUsernameAndPassword(string username, string password)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM linked_devices WHERE subsonic_username = @u";
            cmd.Parameters.AddWithValue("@u", username);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var device = ReadDevice(reader);
                bool match;
                try { match = new PasswordHasher<object>().VerifyHashedPassword(null!, device.AppPasswordHash, password) != PasswordVerificationResult.Failed; }
                catch (FormatException) { match = false; }
                if (match)
                    return device;
            }
            return null;
        }
    }

    public static List<LinkedDevice> GetDevicesForUser(string username)
    {
        lock (_lock)
        {
            var list = new List<LinkedDevice>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM linked_devices WHERE subsonic_username = @u ORDER BY id";
            cmd.Parameters.AddWithValue("@u", username);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadDevice(reader));
            return list;
        }
    }

    public static List<LinkedDevice> GetDevicesByJellyfinUserId(string jellyfinUserId)
    {
        lock (_lock)
        {
            var list = new List<LinkedDevice>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM linked_devices WHERE jellyfin_user_id = @jid ORDER BY id";
            cmd.Parameters.AddWithValue("@jid", jellyfinUserId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadDevice(reader));
            return list;
        }
    }

    public static LinkedDevice? GetDeviceById(long id)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM linked_devices WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDevice(reader) : null;
        }
    }

    public static long InsertDevice(
        string subsonicUsername,
        string jellyfinUserId,
        string plainPassword,
        string deviceLabel,
        string? deviceId,
        string? deviceName)
    {
        var hash = new PasswordHasher<object>().HashPassword(null!, plainPassword);
        var encrypted = Crypto.Encrypt(plainPassword, _salt);

        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO linked_devices
                  (subsonic_username, jellyfin_user_id, app_password_hash, app_password_encrypted,
                   device_label, jellyfin_device_id, jellyfin_device_name)
                VALUES (@u, @jid, @hash, @enc, @label, @did, @dname);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@u", subsonicUsername);
            cmd.Parameters.AddWithValue("@jid", jellyfinUserId);
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@enc", encrypted);
            cmd.Parameters.AddWithValue("@label", deviceLabel);
            cmd.Parameters.AddWithValue("@did", (object?)deviceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dname", (object?)deviceName ?? DBNull.Value);
            return (long)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("Insert failed"));
        }
    }

    public static void UpdateDeviceLabel(long id, string label)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE linked_devices SET device_label = @label WHERE id = @id";
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public static void UpdateDevicePassword(long id, string newPassword)
    {
        var hash = new PasswordHasher<object>().HashPassword(null!, newPassword);
        var encrypted = Crypto.Encrypt(newPassword, _salt);
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE linked_devices SET app_password_hash = @hash, app_password_encrypted = @enc WHERE id = @id";
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@enc", encrypted);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public static void DeleteDevice(long id)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM linked_devices WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Token auth helpers ───────────────────────────────────────────────────

    /// <summary>Returns all (device_id, plaintext_password) pairs for token auth (t+s).</summary>
    public static List<(long DeviceId, string Label, string JellyfinUserId, string PlainPassword)> GetDevicePlaintextPasswords(string username)
    {
        lock (_lock)
        {
            var list = new List<(long, string, string, string)>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT id, device_label, jellyfin_user_id, app_password_encrypted FROM linked_devices WHERE subsonic_username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var label = reader.GetString(1);
                var jellyfinUserId = reader.GetString(2);
                var blob = (byte[])reader["app_password_encrypted"];
                try
                {
                    var plain = Crypto.Decrypt(blob, _salt);
                    list.Add((id, label, jellyfinUserId, plain));
                }
                catch { /* skip devices with unrecoverable passwords */ }
            }
            return list;
        }
    }

    // ── Pending QuickConnect ─────────────────────────────────────────────────

    public static void UpsertPendingQuickConnect(string secret, string jellyfinUserId)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO pending_quickconnect (secret, jellyfin_user_id)
                VALUES (@s, @jid)
                ON CONFLICT(secret) DO UPDATE SET jellyfin_user_id = @jid, created_at = datetime('now')";
            cmd.Parameters.AddWithValue("@s", secret);
            cmd.Parameters.AddWithValue("@jid", jellyfinUserId);
            cmd.ExecuteNonQuery();
        }
    }

    public static string? ConsumePendingQuickConnect(string secret)
    {
        lock (_lock)
        {
            using var tx = Db.BeginTransaction();
            try
            {
                using var sel = Db.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = "SELECT jellyfin_user_id FROM pending_quickconnect WHERE secret = @s AND datetime(created_at, '+5 minutes') > datetime('now')";
                sel.Parameters.AddWithValue("@s", secret);
                var userId = sel.ExecuteScalar() as string;
                if (userId != null)
                {
                    using var del = Db.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM pending_quickconnect WHERE secret = @s";
                    del.Parameters.AddWithValue("@s", secret);
                    del.ExecuteNonQuery();
                }
                tx.Commit();
                return userId;
            }
            catch { tx.Rollback(); throw; }
        }
    }

    // ── Play Queue ───────────────────────────────────────────────────────────

    public static PlayQueueRecord? GetPlayQueue(string username)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM play_queue WHERE subsonic_username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new PlayQueueRecord(
                reader.GetString(reader.GetOrdinal("subsonic_username")),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("entry_ids"))) ?? [],
                reader.IsDBNull(reader.GetOrdinal("current_id")) ? null : reader.GetString(reader.GetOrdinal("current_id")),
                reader.GetInt32(reader.GetOrdinal("current_index")),
                reader.GetInt64(reader.GetOrdinal("position_ms")),
                reader.IsDBNull(reader.GetOrdinal("changed_at")) ? null : reader.GetString(reader.GetOrdinal("changed_at")),
                reader.GetString(reader.GetOrdinal("changed_by")));
        }
    }

    public static void SavePlayQueue(string username, List<string> entryIds, string? currentId, int currentIndex, long positionMs, string changedBy)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO play_queue (subsonic_username, entry_ids, current_id, current_index, position_ms, changed_at, changed_by)
                VALUES (@u, @ids, @cid, @cidx, @pos, datetime('now'), @by)
                ON CONFLICT(subsonic_username) DO UPDATE SET
                  entry_ids = @ids, current_id = @cid, current_index = @cidx,
                  position_ms = @pos, changed_at = datetime('now'), changed_by = @by";
            cmd.Parameters.AddWithValue("@u", username);
            cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(entryIds));
            cmd.Parameters.AddWithValue("@cid", (object?)currentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cidx", currentIndex);
            cmd.Parameters.AddWithValue("@pos", positionMs);
            cmd.Parameters.AddWithValue("@by", changedBy);
            cmd.ExecuteNonQuery();
        }
    }

    public static void ClearPlayQueue(string username)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM play_queue WHERE subsonic_username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            cmd.ExecuteNonQuery();
        }
    }

    // ── User Library Settings ────────────────────────────────────────────────

    public static List<string> GetUserLibrarySettings(string username)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT selected_ids FROM user_library_settings WHERE subsonic_username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            var json = cmd.ExecuteScalar() as string;
            if (json == null) return [];
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
    }

    public static void SetUserLibrarySettings(string username, List<string> selectedIds)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO user_library_settings (subsonic_username, selected_ids, updated_at)
                VALUES (@u, @ids, datetime('now'))
                ON CONFLICT(subsonic_username) DO UPDATE SET selected_ids = @ids, updated_at = datetime('now')";
            cmd.Parameters.AddWithValue("@u", username);
            cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(selectedIds));
            cmd.ExecuteNonQuery();
        }
    }

    // ── Shares ───────────────────────────────────────────────────────────────

    public static ShareRecord? GetShare(string shareUid)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM shares WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadShare(reader) : null;
        }
    }

    public static List<ShareRecord> GetSharesForDevice(long linkedDeviceId)
    {
        lock (_lock)
        {
            var list = new List<ShareRecord>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM shares WHERE linked_device_id = @id ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue("@id", linkedDeviceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadShare(reader));
            return list;
        }
    }

    public static List<ShareRecord> GetSharesForUser(string username)
    {
        lock (_lock)
        {
            var list = new List<ShareRecord>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                SELECT s.* FROM shares s
                JOIN linked_devices d ON d.id = s.linked_device_id
                WHERE d.subsonic_username = @u
                ORDER BY s.created_at DESC";
            cmd.Parameters.AddWithValue("@u", username);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadShare(reader));
            return list;
        }
    }

    public static string InsertShare(long linkedDeviceId, List<string> entryIds, List<string> entryIdsFlat, string? description, string? expiresAt, string secret)
    {
        var uid = Guid.NewGuid().ToString("N");
        var secretEncrypted = Crypto.Encrypt(secret, _salt);
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO shares (share_uid, linked_device_id, entry_ids, entry_ids_flat, description, expires_at, share_secret_encrypted)
                VALUES (@uid, @lid, @eids, @flat, @desc, @exp, @sec)";
            cmd.Parameters.AddWithValue("@uid", uid);
            cmd.Parameters.AddWithValue("@lid", linkedDeviceId);
            cmd.Parameters.AddWithValue("@eids", JsonSerializer.Serialize(entryIds));
            cmd.Parameters.AddWithValue("@flat", JsonSerializer.Serialize(entryIdsFlat));
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@exp", (object?)expiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sec", secretEncrypted);
            cmd.ExecuteNonQuery();
            return uid;
        }
    }

    public static void UpdateShare(string shareUid, string? description, string? expiresAt)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE shares SET description = @desc, expires_at = @exp WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@exp", (object?)expiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static void UpdateShareDescription(string shareUid, string? description)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE shares SET description = @desc WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static void DeleteShare(string shareUid)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM shares WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static void IncrementShareVisitCount(string shareUid)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE shares SET visit_count = visit_count + 1 WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static List<(ShareRecord Share, string SubsonicUsername)> GetAllShares()
    {
        lock (_lock)
        {
            var list = new List<(ShareRecord, string)>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                SELECT s.*, d.subsonic_username FROM shares s
                JOIN linked_devices d ON d.id = s.linked_device_id
                ORDER BY s.created_at DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var share = ReadShare(reader);
                var username = reader.GetString(reader.GetOrdinal("subsonic_username"));
                list.Add((share, username));
            }
            return list;
        }
    }

    public static string? GetShareSecret(string shareUid)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT share_secret_encrypted FROM shares WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            var blob = cmd.ExecuteScalar() as byte[];
            if (blob == null) return null;
            try { return Crypto.Decrypt(blob, _salt); }
            catch { return null; }
        }
    }

    // ── Derived Cache ────────────────────────────────────────────────────────

    public record DerivedCacheEntry(string CacheKey, string ValueJson, string CachedAt, string? LastSourceChangeAt);

    public static DerivedCacheEntry? GetDerivedCache(string cacheKey)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM derived_cache WHERE cache_key = @k";
            cmd.Parameters.AddWithValue("@k", cacheKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new DerivedCacheEntry(
                reader.GetString(reader.GetOrdinal("cache_key")),
                reader.GetString(reader.GetOrdinal("value_json")),
                reader.GetString(reader.GetOrdinal("cached_at")),
                reader.IsDBNull(reader.GetOrdinal("last_source_change_at")) ? null : reader.GetString(reader.GetOrdinal("last_source_change_at")));
        }
    }

    public static void SetDerivedCache(string cacheKey, string valueJson, string? lastSourceChangeAt)
    {
        lock (_lock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO derived_cache (cache_key, value_json, cached_at, last_source_change_at)
                VALUES (@k, @v, datetime('now'), @lsc)
                ON CONFLICT(cache_key) DO UPDATE SET value_json = @v, cached_at = datetime('now'), last_source_change_at = @lsc";
            cmd.Parameters.AddWithValue("@k", cacheKey);
            cmd.Parameters.AddWithValue("@v", valueJson);
            cmd.Parameters.AddWithValue("@lsc", (object?)lastSourceChangeAt ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static LinkedDevice ReadDevice(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("subsonic_username")),
        r.GetString(r.GetOrdinal("app_password_hash")),
        (byte[])r["app_password_encrypted"],
        r.GetString(r.GetOrdinal("jellyfin_user_id")),
        r.GetString(r.GetOrdinal("device_label")),
        r.IsDBNull(r.GetOrdinal("jellyfin_device_id")) ? null : r.GetString(r.GetOrdinal("jellyfin_device_id")),
        r.IsDBNull(r.GetOrdinal("jellyfin_device_name")) ? null : r.GetString(r.GetOrdinal("jellyfin_device_name")),
        r.GetString(r.GetOrdinal("created_at")));

    private static ShareRecord ReadShare(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("share_uid")),
        r.GetInt64(r.GetOrdinal("linked_device_id")),
        JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("entry_ids"))) ?? [],
        JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("entry_ids_flat"))) ?? [],
        r.IsDBNull(r.GetOrdinal("description")) ? null : r.GetString(r.GetOrdinal("description")),
        r.IsDBNull(r.GetOrdinal("share_secret_encrypted")) ? null : (byte[])r["share_secret_encrypted"],
        r.IsDBNull(r.GetOrdinal("expires_at")) ? null : r.GetString(r.GetOrdinal("expires_at")),
        r.GetInt32(r.GetOrdinal("visit_count")),
        r.GetString(r.GetOrdinal("created_at")));
}
