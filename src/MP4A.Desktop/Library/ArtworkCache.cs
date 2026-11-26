using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Soundroom;

internal sealed partial class ArtworkCache(string directory, int memoryLimit = 16 * 1024 * 1024,
    int diskLimit = 64 * 1024 * 1024, CancellationToken lifetime = default)
{
    private const int MaxThumbnail = 2 * 1024 * 1024;
    private const int MaxMemoryEntries = 128;
    private const int MaxDiskImages = 4000;
    private const int MaxDiskMisses = 12_000;
    private const long MissingLifetime = 5 * 60 * 1000;
    private const long TouchInterval = 60 * 60 * 1000;

    private sealed record Entry(byte[] Bytes, long Expires);
    private readonly object memoryGate = new();
    private readonly object databaseGate = new();
    private readonly SemaphoreSlim decoders = new(2, 2);
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> pending = new();
    private readonly LinkedList<(string Key, Entry Artwork)> recent = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, Entry Artwork)>> memory = new();
    private int memoryBytes;
    private int reportedDatabaseError;
    private bool initialized;

    public Task<byte[]> GetAsync(string path, bool embedded, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var source = new FileInfo(path);
        if (!source.Exists)
            return Task.FromResult(Array.Empty<byte>());
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"v1|{embedded}|{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}")));

        if (ReadMemory(key) is { } entry)
            return Task.FromResult(entry.Bytes);

        var work = pending.GetOrAdd(key, _ => new(() => Task.Run(() => LoadAsync(key, source.FullName, embedded))));
        return work.Value.WaitAsync(cancellation);
    }

    private async Task<byte[]> LoadAsync(string key, string path, bool embedded)
    {
        try
        {
            lifetime.ThrowIfCancellationRequested();
            if (ReadMemory(key) is { } cached)
                return cached.Bytes;
            if (ReadDatabase(key) is { } stored)
            {
                Remember(key, stored);
                return stored.Bytes;
            }

            byte[]? bytes = ReadLegacy(key);
            bool confirmedMiss = false;
            if (bytes is null)
            {
                await decoders.WaitAsync(lifetime).ConfigureAwait(false);
                try { (bytes, confirmedMiss) = await Media.ThumbnailAsync(path, embedded, lifetime).ConfigureAwait(false); }
                finally { decoders.Release(); }
            }
            var entry = IsThumbnail(bytes) ? new Entry(bytes, 0) : new Entry([], confirmedMiss ? 0 : Now + MissingLifetime);
            WriteDatabase(key, entry);
            Remember(key, entry);
            return entry.Bytes;
        }
        finally { pending.TryRemove(key, out _); }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private Entry? ReadMemory(string key)
    {
        lock (memoryGate)
        {
            if (!memory.TryGetValue(key, out var cached))
                return null;
            if (cached.Value.Artwork is { Expires: > 0 } entry && entry.Expires <= Now)
            {
                Remove(cached);
                return null;
            }
            recent.Remove(cached);
            recent.AddFirst(cached);
            return cached.Value.Artwork;
        }
    }

    private void Remember(string key, Entry entry)
    {
        if (memoryLimit <= 0 || entry.Bytes.Length > memoryLimit)
            return;
        lock (memoryGate)
        {
            if (memory.TryGetValue(key, out var previous))
                Remove(previous);
            memory.Add(key, recent.AddFirst((key, entry)));
            memoryBytes += entry.Bytes.Length;
            while (memoryBytes > memoryLimit || memory.Count > MaxMemoryEntries)
                Remove(recent.Last!);
        }
    }

    private void Remove(LinkedListNode<(string Key, Entry Artwork)> entry)
    {
        recent.Remove(entry);
        memory.Remove(entry.Value.Key);
        memoryBytes -= entry.Value.Artwork.Bytes.Length;
    }

    private SqliteConnection OpenDatabase()
    {
        if (!initialized)
            Directory.CreateDirectory(directory);
        var db = SoundroomSqlite.Open(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "artwork.db"),
            DefaultTimeout = 5
        }.ToString());
        if (initialized)
            return db;
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA auto_vacuum = FULL;
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS thumbnails(
                    key TEXT PRIMARY KEY, image BLOB, expires INTEGER NOT NULL, accessed INTEGER NOT NULL
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS thumbnail_access ON thumbnails(accessed, key);
                CREATE INDEX IF NOT EXISTS thumbnail_miss_access ON thumbnails(accessed, key) WHERE image IS NULL;
                CREATE INDEX IF NOT EXISTS thumbnail_expiry ON thumbnails(expires) WHERE expires > 0;
                CREATE TABLE IF NOT EXISTS cover_assets(
                    id TEXT PRIMARY KEY, image BLOB NOT NULL, preview TEXT NOT NULL
                ) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS cover_links(
                    track TEXT PRIMARY KEY COLLATE NOCASE, asset TEXT, retry_after INTEGER NOT NULL
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS cover_links_asset ON cover_links(asset);
                DELETE FROM cover_assets WHERE NOT EXISTS (SELECT 1 FROM cover_links WHERE asset=cover_assets.id);
                """;
            command.ExecuteNonQuery();
            initialized = true;
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private Entry? ReadDatabase(string key)
    {
        if (diskLimit <= 0)
            return null;
        lock (databaseGate)
        {
            try
            {
                using var db = OpenDatabase();
                using var command = db.CreateCommand();
                command.CommandText = """
                    SELECT image, expires, accessed FROM thumbnails
                    WHERE key = $key AND (expires = 0 OR expires > $now)
                    """;
                long now = Now;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$now", now);
                Entry entry;
                long accessed;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;
                    entry = new(reader.IsDBNull(0) ? [] : (byte[])reader.GetValue(0), reader.GetInt64(1));
                    accessed = reader.GetInt64(2);
                }
                if (entry.Bytes.Length > 0 && !IsThumbnail(entry.Bytes))
                    return null;
                if (now - accessed < TouchInterval)
                    return entry;
                try
                {
                    command.CommandText = "UPDATE thumbnails SET accessed = $now WHERE key = $key AND accessed = $accessed";
                    command.Parameters.AddWithValue("$accessed", accessed);
                    command.ExecuteNonQuery();
                }
                catch (SqliteException error) { ReportDatabaseError(error); }
                return entry;
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                ReportDatabaseError(error);
                return null;
            }
        }
    }

    private void WriteDatabase(string key, Entry entry)
    {
        if (diskLimit <= 0 || entry.Bytes.Length > diskLimit)
            return;
        lock (databaseGate)
        {
            try
            {
                using var db = OpenDatabase();
                using var transaction = db.BeginTransaction();
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO thumbnails(key, image, expires, accessed)
                    VALUES ($key, $image, $expires, max($now, coalesce((SELECT max(accessed) + 1 FROM thumbnails), $now)));
                    DELETE FROM thumbnails WHERE expires > 0 AND expires <= $now;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.Add("$image", SqliteType.Blob).Value = entry.Bytes.Length == 0 ? DBNull.Value : entry.Bytes;
                command.Parameters.AddWithValue("$expires", entry.Expires);
                command.Parameters.AddWithValue("$now", Now);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                if (entry.Bytes.Length == 0)
                {
                    command.CommandText = """
                        DELETE FROM thumbnails WHERE key IN (
                            SELECT key FROM thumbnails WHERE image IS NULL
                            ORDER BY accessed DESC, key LIMIT -1 OFFSET $entries
                        );
                        """;
                    command.Parameters.AddWithValue("$entries", MaxDiskMisses);
                }
                else
                {
                    command.CommandText = """
                        WITH ranked AS (
                            SELECT key, row_number() OVER recency AS entries,
                                sum(length(image)) OVER recency AS bytes
                            FROM thumbnails WHERE image IS NOT NULL
                            WINDOW recency AS (ORDER BY accessed DESC, key ROWS UNBOUNDED PRECEDING)
                        )
                        DELETE FROM thumbnails WHERE key IN (
                            SELECT key FROM ranked WHERE entries > $entries OR bytes > $limit
                        );
                        """;
                    command.Parameters.AddWithValue("$entries", MaxDiskImages);
                    command.Parameters.AddWithValue("$limit", diskLimit);
                }
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                ReportDatabaseError(error);
            }
        }
    }

    private byte[]? ReadLegacy(string key)
    {
        try
        {
            using var file = new FileStream(Path.Combine(directory, key + ".png"), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (file.Length is < 45 or > MaxThumbnail)
                return null;
            byte[] bytes = new byte[file.Length];
            file.ReadExactly(bytes);
            return IsThumbnail(bytes) ? bytes : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void ReportDatabaseError(Exception error)
    {
        if (Interlocked.Exchange(ref reportedDatabaseError, 1) == 0)
            Diagnostics.Write("Accessing the artwork cache", error);
    }

    internal static bool IsWebP(ReadOnlySpan<byte> bytes) => bytes.Length >= 20 &&
        bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8) &&
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == bytes.Length - 8;

    private static bool IsThumbnail(ReadOnlySpan<byte> bytes) => bytes.Length <= MaxThumbnail && (IsWebP(bytes) ||
        (bytes.Length >= 45 &&
        bytes[0] == 0x89 && bytes[1..].StartsWith("PNG\r\n\x1a\n"u8) &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) is > 0 and <= 640 &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) is > 0 and <= 640 &&
        bytes[^8..^4].SequenceEqual("IEND"u8)));
}
