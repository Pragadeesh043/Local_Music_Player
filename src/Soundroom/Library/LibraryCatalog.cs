using Microsoft.Data.Sqlite;

namespace Soundroom;

internal sealed record SmartFilter(string Codec = "", int MinimumRate = 0, int MinimumBits = 0,
    double MinimumDr = 0, string Genre = "", string Year = "", int Rating = 0, int Plays = 0);
internal sealed record LibraryQuery(string Search, SmartFilter Filter, string? Folder = null,
    string? Album = null, string? Edition = null);
internal sealed record LibraryPage(Track[] Tracks, int Total);

internal sealed partial class Library
{
    private const string SortColumns = "album, edition, disc, number, title, key";
    private const string Visibility = """
        c.missing = 0
        AND (c.source IS NOT NULL OR NOT EXISTS (
            SELECT 1 FROM track_catalog alt WHERE alt.file = c.file AND alt.cue = c.cue AND alt.missing = 0 AND alt.source IS NOT NULL))
        AND (c.cue > 0 OR c.source IS NULL OR NOT EXISTS (
            SELECT 1 FROM track_catalog cue WHERE cue.source = c.source AND cue.relative = c.relative AND cue.missing = 0 AND cue.cue > 0))
        """;

    public void EnsureCatalog(CancellationToken cancellation)
    {
        using var db = Open();
        using var state = db.CreateCommand();
        state.CommandText = "SELECT 1 FROM settings WHERE name = 'track-catalog-v1'";
        if (state.ExecuteScalar() is not null)
            return;

        string? cursor = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            using var transaction = db.BeginTransaction();
            using var read = db.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT t.path, t.data FROM tracks t
                LEFT JOIN track_catalog c ON c.key = t.path
                WHERE c.key IS NULL
                """ + (cursor is null ? "" : " AND t.path > $cursor") + " ORDER BY t.path LIMIT 512";
            if (cursor is not null)
                read.Parameters.AddWithValue("$cursor", cursor);
            var batch = new List<(string Key, string Data)>();
            using (var reader = read.ExecuteReader())
                while (reader.Read())
                    batch.Add((reader.GetString(0), reader.GetString(1)));
            if (batch.Count == 0)
            {
                using var finish = db.CreateCommand();
                finish.Transaction = transaction;
                finish.CommandText = "INSERT OR IGNORE INTO settings VALUES ('track-catalog-v1', 'true')";
                finish.ExecuteNonQuery();
                transaction.Commit();
                return;
            }
            foreach (var row in batch)
                if (ReadRecord<Track>(row.Data, "track") is { Tags: not null, Path.Length: > 0 } track)
                    SaveCatalog(db, transaction, row.Key, track);
            transaction.Commit();
            cursor = batch[^1].Key;
        }
    }

    private static void SaveCatalog(SqliteConnection db, SqliteTransaction transaction, string key, Track track)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO track_catalog VALUES
            ($key,$file,$folder,$source,$relative,$index,$cue,$missing,$album,$edition,$hasAlbum,$disc,$number,$title,
             $search,$codec,$rate,$bits,$dr,$genre,$year,$rating,$plays)
            """;
        foreach (var (name, value) in new (string, object?)[]
        {
            ("key", key), ("file", track.Path), ("folder", track.Folder), ("source", track.SourceId),
            ("relative", track.RelativePath), ("index", track.IndexFile), ("cue", track.CueIndex), ("missing", track.Missing),
            ("album", track.AlbumGroup), ("edition", track.Edition), ("hasAlbum", track.Album.Length > 0),
            ("disc", NumberTag(track.Tag("disc", "discnumber"))), ("number", NumberTag(track.Tag("track", "tracknumber"))),
            ("title", track.Title),
            ("search", string.Join('\n', new[] { track.Title, track.Artist, track.Album, track.Folder }.Concat(track.Tags.Values)).ToUpperInvariant()),
            ("codec", track.Codec.ToUpperInvariant()), ("rate", track.Rate), ("bits", track.Bits),
            ("dr", track.Analysis?.DynamicRange), ("genre", track.Tag("genre").ToUpperInvariant()),
            ("year", track.Tag("date", "year")), ("rating", track.Rating), ("plays", track.PlayCount)
        })
            command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static int NumberTag(string value)
    {
        int slash = value.IndexOf('/');
        return int.TryParse(slash < 0 ? value.AsSpan() : value.AsSpan(0, slash), out int number)
            ? number : int.MaxValue;
    }

    private static string Where(SqliteCommand cmd, LibraryQuery query)
    {
        string where = Visibility;
        void Add(string condition, string parameter, object value)
        {
            where += " AND " + condition;
            cmd.Parameters.AddWithValue(parameter, value);
        }
        var filter = query.Filter;
        if (query.Search.Length > 0) Add("instr(c.search,$search)>0", "$search", query.Search.ToUpperInvariant());
        if (query.Folder is { } folder)
        {
            string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
            Add("(c.folder=$folder OR substr(c.folder,1,length($prefix))=$prefix COLLATE NOCASE)", "$folder", folder);
            cmd.Parameters.AddWithValue("$prefix", prefix);
        }
        if (query.Album is { } album) Add("c.album=$album", "$album", album);
        if (query.Edition is { } edition) Add("c.edition=$edition", "$edition", edition);
        if (filter.Codec.Length > 0) Add("instr(c.codec,$codec)>0", "$codec", filter.Codec.ToUpperInvariant());
        if (filter.Genre.Length > 0) Add("instr(c.genre,$genre)>0", "$genre", filter.Genre.ToUpperInvariant());
        if (filter.Year.Length > 0) Add("substr(c.year,1,length($year))=$year", "$year", filter.Year);
        if (filter.MinimumRate > 0) Add("c.rate >= $rate", "$rate", filter.MinimumRate);
        if (filter.MinimumBits > 0) Add("c.bits >= $bits", "$bits", filter.MinimumBits);
        if (filter.MinimumDr > 0) Add("c.dr >= $dr", "$dr", filter.MinimumDr);
        if (filter.Rating > 0) Add("c.rating >= $rating", "$rating", filter.Rating);
        if (filter.Plays > 0) Add("c.plays >= $plays", "$plays", filter.Plays);
        return where;
    }

    public LibraryPage Page(LibraryQuery query, int offset, int limit)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        string where = Where(cmd, query);
        cmd.CommandText = $"SELECT count(*) FROM track_catalog c WHERE {where}";
        int total = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = $"SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key WHERE {where} ORDER BY {SortColumns} LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return new(ReadTracks(cmd), total);
    }

    public Track[] Neighbors(LibraryQuery query, string key, bool backwards, int limit, string[]? onlineSources = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        string where = Where(cmd, query);
        if (onlineSources is not null)
        {
            where += onlineSources.Length == 0 ? " AND c.source IS NULL" :
                $" AND (c.source IS NULL OR c.source IN ({string.Join(",", onlineSources.Select((_, i) => "$source" + i))}))";
            for (int i = 0; i < onlineSources.Length; i++)
                cmd.Parameters.AddWithValue("$source" + i, onlineSources[i]);
        }
        string order = backwards ? SortColumns.Replace(", ", " DESC, ") + " DESC" : SortColumns;
        string comparison = backwards ? "<" : ">";
        cmd.CommandText = $"""
            SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key
            WHERE {where} AND ({SortColumns}) {comparison} (SELECT {SortColumns} FROM track_catalog WHERE key=$key)
            ORDER BY {order} LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadTracks(cmd);
    }

    public Track? FindTrack(string key) => SelectTracks(
        "SELECT data FROM tracks WHERE path=$key", ("$key", key)).FirstOrDefault();

    public Track[] IndexedFile(string source, string file) => SelectTracks(
        "SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key WHERE c.source=$source AND c.index_file=$file",
        ("$source", source), ("$file", file));

    public Track? LegacyTrack(string path, int cue) => SelectTracks(
        "SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key WHERE c.file=$file AND c.cue=$cue AND c.source IS NULL LIMIT 1",
        ("$file", path), ("$cue", cue)).FirstOrDefault();

    private Track[] SelectTracks(string sql, params (string Name, object Value)[] parameters)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return ReadTracks(cmd);
    }

    private static Track[] ReadTracks(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var result = new List<Track>();
        while (reader.Read())
            if (ReadRecord<Track>(reader.GetString(0), "track") is { Tags: not null, Path.Length: > 0 } track)
                result.Add(track);
        return result.ToArray();
    }

    public string[] Groups(int view, string? album = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        string field = album is not null ? "edition" : view == 1 ? "folder" : "album";
        string scope = album is not null ? " AND album=$album" : view == 2 ? " AND has_album=1" : "";
        cmd.CommandText = $"SELECT DISTINCT c.{field} FROM track_catalog c WHERE {Visibility}{scope} ORDER BY c.{field}";
        if (album is not null)
            cmd.Parameters.AddWithValue("$album", album);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result.ToArray();
    }

    public void RebaseSource(string id, string root, CancellationToken cancellation)
    {
        string cursor = "";
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            using var db = Open();
            using var transaction = db.BeginTransaction();
            using var read = db.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key
                WHERE c.source=$source AND c.key>$cursor ORDER BY c.key LIMIT 256
                """;
            read.Parameters.AddWithValue("$source", id);
            read.Parameters.AddWithValue("$cursor", cursor);
            var batch = ReadTracks(read);
            if (batch.Length == 0)
                return;
            foreach (var item in batch)
            {
                if (item.RelativePath is not { } relative)
                    continue;
                string path = Path.Combine(root, relative);
                if (string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase))
                    continue;
                var updated = item with { Path = path };
                using var write = db.CreateCommand();
                write.Transaction = transaction;
                write.CommandText = "UPDATE tracks SET data=$data WHERE path=$key";
                write.Parameters.AddWithValue("$key", item.Key);
                write.Parameters.AddWithValue("$data", System.Text.Json.JsonSerializer.Serialize(updated));
                write.ExecuteNonQuery();
                SaveCatalog(db, transaction, item.Key, updated);
            }
            transaction.Commit();
            cursor = batch[^1].Key;
        }
    }
}
