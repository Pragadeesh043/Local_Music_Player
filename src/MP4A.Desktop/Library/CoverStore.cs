using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Soundroom;

internal sealed record StoredCover(string? Asset, string? Preview);

internal sealed partial class ArtworkCache
{
    public Dictionary<string, StoredCover> ReadCovers(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, StoredCover>(StringComparer.OrdinalIgnoreCase);
        var tracks = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tracks.Length == 0)
            return result;
        lock (databaseGate)
        {
            try
            {
                using var db = OpenDatabase();
                foreach (var batch in tracks.Chunk(200))
                {
                    using var command = db.CreateCommand();
                    string[] parameters = batch.Select((key, index) =>
                    {
                        string name = "$track" + index;
                        command.Parameters.AddWithValue(name, key);
                        return name;
                    }).ToArray();
                    command.Parameters.AddWithValue("$now", Now);
                    command.CommandText = $"""
                        SELECT l.track, a.id, a.preview FROM cover_links l LEFT JOIN cover_assets a ON a.id=l.asset
                        WHERE l.track IN ({string.Join(",", parameters)}) AND
                              (a.id IS NOT NULL OR (l.asset IS NULL AND l.retry_after > $now))
                        """;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        result[reader.GetString(0)] = reader.IsDBNull(1) ? new(null, null)
                            : new(reader.GetString(1), reader.GetString(2));
                }
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                ReportDatabaseError(error);
            }
        }
        return result;
    }

    public byte[] ReadCoverAsset(string id)
    {
        lock (databaseGate)
        {
            try
            {
                using var db = OpenDatabase();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT image FROM cover_assets WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteScalar() as byte[] ?? [];
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                ReportDatabaseError(error);
                return [];
            }
        }
    }

    public async Task<StoredCover> StoreCoverAsync(string track, byte[] image, Func<byte[], Task<string>> preview)
    {
        string? id = image.Length > 0 ? Convert.ToHexString(SHA256.HashData(image)) : null;
        string? dataUrl = id is null ? null : ReadCoverPreview(id) ?? await preview(image).ConfigureAwait(false);
        lock (databaseGate)
        {
            try
            {
                using var db = OpenDatabase();
                using var transaction = db.BeginTransaction();
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO cover_assets(id, image, preview)
                        SELECT $id, $image, $preview WHERE $id IS NOT NULL;
                    INSERT OR REPLACE INTO cover_links(track, asset, retry_after) VALUES($track, $id, $retry);
                    """;
                command.Parameters.AddWithValue("$track", track);
                command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
                command.Parameters.Add("$image", SqliteType.Blob).Value = image;
                command.Parameters.AddWithValue("$preview", (object?)dataUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$retry", id is null ? Now + (long)TimeSpan.FromDays(1).TotalMilliseconds : 0);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                ReportDatabaseError(error);
                return new(null, dataUrl);
            }
        }
        return new(id, dataUrl);
    }

    private string? ReadCoverPreview(string id)
    {
        lock (databaseGate)
        {
            try
            {
                using var db = OpenDatabase();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT preview FROM cover_assets WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteScalar() as string;
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                ReportDatabaseError(error);
                return null;
            }
        }
    }
}
