using Microsoft.Data.Sqlite;
using System.Text;

namespace Soundroom;

internal sealed record SmartFilter(string Codec = "", int MinimumRate = 0, int MinimumBits = 0,
    double MinimumDr = 0, string Genre = "", string Year = "", int Rating = 0, int Plays = 0);
internal sealed record LibraryQuery(string Search, SmartFilter Filter, string? Folder = null,
    string? Album = null, string? Edition = null, LibraryOrder Order = default);
internal enum LibrarySort { AlbumOrder, Title, Artist, AlbumArtist, Album, TrackNumber, Year, Added,
    FileName, Duration, Codec, SampleRate, BitDepth, Rating, PlayCount }
internal readonly record struct LibraryOrder(LibrarySort Field, bool Descending = false);
internal sealed record LibraryPage(Track[] Tracks, int Total);

internal sealed partial class Library
{
    private static readonly string[] AlbumOrder = ["c.album", "c.edition", "c.disc", "c.number", "c.title", "c.key"];
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
        EnsureBrowseIndex(db, cancellation);
        EnsureSortIndex(db, cancellation);
    }

    private static void EnsureBrowseIndex(SqliteConnection db, CancellationToken cancellation)
    {
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
                    SaveCatalog(db, transaction, row.Key, track, added: 0);
            transaction.Commit();
            cursor = batch[^1].Key;
        }
    }

    private static void EnsureSortIndex(SqliteConnection db, CancellationToken cancellation)
    {
        using var state = db.CreateCommand();
        state.CommandText = "SELECT 1 FROM settings WHERE name = 'track-sort-v1'";
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
                SELECT t.path, t.data FROM tracks t JOIN track_catalog c ON c.key=t.path
                LEFT JOIN track_sort s ON s.key=t.path WHERE s.key IS NULL
                """ + (cursor is null ? "" : " AND t.path > $cursor") + " ORDER BY t.path LIMIT 512";
            if (cursor is not null)
                read.Parameters.AddWithValue("$cursor", cursor);
            var batch = new List<(string Key, Track Track)>();
            int readCount = 0;
            using (var reader = read.ExecuteReader())
                while (reader.Read())
                {
                    readCount++;
                    cursor = reader.GetString(0);
                    if (ReadRecord<Track>(reader.GetString(1), "track") is { Tags: not null, Path.Length: > 0 } track)
                        batch.Add((cursor, track));
                }
            foreach (var row in batch)
                SaveSort(db, transaction, row.Key, row.Track, added: 0);
            if (readCount < 512)
            {
                using var finish = db.CreateCommand();
                finish.Transaction = transaction;
                finish.CommandText = "INSERT OR IGNORE INTO settings VALUES ('track-sort-v1', 'true')";
                finish.ExecuteNonQuery();
                transaction.Commit();
                return;
            }
            transaction.Commit();
        }
    }

    private static void SaveCatalog(SqliteConnection db, SqliteTransaction transaction, string key, Track track, long? added = null)
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
        SaveSort(db, transaction, key, track, added ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static void SaveSort(SqliteConnection db, SqliteTransaction transaction, string key, Track track, long added)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO track_sort VALUES ($key,$artist,$albumArtist,$album,$filename,$duration,$added)
            ON CONFLICT(key) DO UPDATE SET artist=excluded.artist, album_artist=excluded.album_artist,
                album_name=excluded.album_name, filename=excluded.filename, duration=excluded.duration
            WHERE artist IS NOT excluded.artist OR album_artist IS NOT excluded.album_artist OR
                album_name IS NOT excluded.album_name OR filename IS NOT excluded.filename OR duration IS NOT excluded.duration
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$artist", track.Artist);
        string albumArtist = track.Tag("albumartist", "album_artist", "artist");
        command.Parameters.AddWithValue("$albumArtist", albumArtist.Length == 0 ? track.Artist : albumArtist);
        command.Parameters.AddWithValue("$album", track.Album);
        command.Parameters.AddWithValue("$filename", NaturalFileName(Path.GetFileName(track.Path)));
        command.Parameters.AddWithValue("$duration", track.Duration);
        command.Parameters.AddWithValue("$added", added);
        command.ExecuteNonQuery();
    }

    private static string NaturalFileName(string name)
    {
        var key = new StringBuilder(name.Length);
        for (int start = 0; start < name.Length;)
        {
            if (!char.IsAsciiDigit(name[start]))
            {
                key.Append(name[start++]);
                continue;
            }
            int end = start + 1;
            while (end < name.Length && char.IsAsciiDigit(name[end]))
                end++;
            var digits = name.AsSpan(start, end - start).TrimStart('0');
            key.Append((char)('0' + 10)).Append(digits.Length.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).Append(digits);
            start = end;
        }
        return key.ToString();
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
        var (columns, from) = Sort(query.Order.Field);
        cmd.CommandText = WithOrderedKeys(columns, query.Order, false,
            $"FROM {from} WHERE {where}", "LIMIT $limit OFFSET $offset");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return new(ReadTracks(cmd), total);
    }

    public LibraryPage AlbumPage(LibraryQuery query, int offset, int limit)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        string where = Where(cmd, query) + " AND c.has_album=1";
        cmd.CommandText = $"SELECT count(*) FROM (SELECT 1 FROM track_catalog c WHERE {where} GROUP BY c.album,c.edition)";
        int total = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = $"""
            WITH ranked AS (
                SELECT c.key, c.album, c.edition,
                    row_number() OVER (PARTITION BY c.album,c.edition ORDER BY c.disc,c.number,c.title,c.key) AS slot
                FROM track_catalog c WHERE {where}
            )
            SELECT t.data FROM ranked r JOIN tracks t ON t.path=r.key
            WHERE r.slot=1 ORDER BY r.album COLLATE NOCASE,r.edition COLLATE NOCASE,r.key
            LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return new(ReadTracks(cmd), total);
    }

    public Track[] Neighbors(LibraryQuery query, string key, bool backwards, int limit, string[]? onlineSources = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        string where = Where(cmd, query) + Online(cmd, onlineSources);
        var (columns, from) = Sort(query.Order.Field);
        var anchors = columns.Select((_, i) => $"p{i}").ToArray();
        string primaryComparison = backwards != query.Order.Descending ? "<" : ">";
        string tieComparison = backwards ? "<" : ">";
        int primaryCount = PrimaryCount(query.Order.Field);
        string leftPrimary = Row(columns.Take(primaryCount));
        string rightPrimary = Row(anchors.Take(primaryCount));
        string relative = !query.Order.Descending
            ? $"{Row(columns)} {tieComparison} {Row(anchors)}"
            : $"({leftPrimary} {primaryComparison} {rightPrimary} OR ({leftPrimary} = {rightPrimary} AND " +
                $"{Row(columns.Skip(primaryCount))} {tieComparison} {Row(anchors.Skip(primaryCount))}))";
        string anchor = $"anchor({string.Join(",", anchors)}) AS (SELECT {string.Join(",", columns)} FROM {from} WHERE c.key=$key), ";
        cmd.CommandText = WithOrderedKeys(columns, query.Order, backwards,
            $"FROM anchor CROSS JOIN {from} WHERE {where} AND {relative}", "LIMIT $limit", anchor);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadTracks(cmd);
    }

    public string[] QueueKeys(LibraryQuery query, string[] onlineSources, bool ordered)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        string where = Where(cmd, query) + Online(cmd, onlineSources);
        var (columns, from) = Sort(ordered ? query.Order.Field : LibrarySort.AlbumOrder);
        string order = ordered ? " ORDER BY " + Ordering(columns, query.Order) : "";
        cmd.CommandText = $"SELECT c.key FROM {from} WHERE {where}{order}";
        using var reader = cmd.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys.ToArray();
    }

    private static string Online(SqliteCommand cmd, string[]? onlineSources)
    {
        if (onlineSources is null)
            return "";
        for (int i = 0; i < onlineSources.Length; i++)
            cmd.Parameters.AddWithValue("$source" + i, onlineSources[i]);
        return onlineSources.Length == 0 ? " AND c.source IS NULL" :
            $" AND (c.source IS NULL OR c.source IN ({string.Join(",", onlineSources.Select((_, i) => "$source" + i))}))";
    }

    private static (string[] Columns, string From) Sort(LibrarySort field)
    {
        string primary = field switch
        {
            LibrarySort.Title => "c.title",
            LibrarySort.Artist => "s.artist",
            LibrarySort.AlbumArtist => "s.album_artist",
            LibrarySort.Album => "s.album_name",
            LibrarySort.TrackNumber => "c.disc",
            LibrarySort.Year => "c.year",
            LibrarySort.Added => "s.added",
            LibrarySort.FileName => "s.filename",
            LibrarySort.Duration => "s.duration",
            LibrarySort.Codec => "c.codec",
            LibrarySort.SampleRate => "(CASE WHEN c.bits=1 THEN c.rate*8 ELSE c.rate END)",
            LibrarySort.BitDepth => "c.bits",
            LibrarySort.Rating => "c.rating",
            LibrarySort.PlayCount => "c.plays",
            _ => AlbumOrder[0]
        };
        string[] columns = field == LibrarySort.TrackNumber
            ? [primary, "c.number", "c.album", "c.edition", "c.title", "c.key"]
            : [primary, .. AlbumOrder.Where(column => column != primary)];
        return (columns, primary.StartsWith("s.", StringComparison.Ordinal)
            ? "track_catalog c JOIN track_sort s ON s.key=c.key" : "track_catalog c");
    }

    private static string Ordering(string[] columns, LibraryOrder order, bool backwards = false) =>
        string.Join(", ", columns.Select((column, index) =>
            column + ((backwards ^ (index < PrimaryCount(order.Field) && order.Descending)) ? " DESC" : " ASC")));

    private static int PrimaryCount(LibrarySort field) => field == LibrarySort.TrackNumber ? 2 : 1;
    private static string Row(IEnumerable<string> columns) => $"({string.Join(",", columns)})";

    private static string WithOrderedKeys(string[] columns, LibraryOrder order, bool backwards, string source,
        string limit, string prefix = "")
    {
        string[] aliases = columns.Select((_, i) => $"k{i}").ToArray();
        return $"""
            WITH {prefix} matches(track_key,{string.Join(",", aliases)}) AS MATERIALIZED (
                SELECT c.key,{string.Join(",", columns)} {source} ORDER BY {Ordering(columns, order, backwards)} {limit})
            SELECT t.data FROM matches JOIN tracks t ON t.path=matches.track_key
            ORDER BY {Ordering(aliases, order, backwards)}
            """;
    }

    public Track? FindTrack(string key) => SelectTracks(
        "SELECT data FROM tracks WHERE path=$key", ("$key", key)).FirstOrDefault();

    public Track[] IndexedFile(string source, string file) => SelectTracks(
        "SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key WHERE c.source=$source AND c.index_file=$file",
        ("$source", source), ("$file", file));

    public Track? LegacyTrack(string path, int cue) => SelectTracks(
        "SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key WHERE c.file=$file AND c.cue=$cue AND c.source IS NULL LIMIT 1",
        ("$file", path), ("$cue", cue)).FirstOrDefault();

    public int HideMissingFiles(IReadOnlyDictionary<string, string> mounts, CancellationToken cancellation)
    {
        var entries = SelectTracks("SELECT t.data FROM track_catalog c JOIN tracks t ON t.path=c.key WHERE c.missing=0");
        var missing = new List<string>();
        var checkedPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool IsMissing(string path)
        {
            if (!Path.IsPathFullyQualified(path))
                return false;
            if (!checkedPaths.TryGetValue(path, out bool result))
                checkedPaths[path] = result = ConfirmMissing(path);
            return result;
        }

        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.SourceId is { } source)
            {
                if (!mounts.TryGetValue(source, out var root) || entry.RelativePath is not { } relative)
                    continue;
                if ((IsMissing(Path.Combine(root, relative)) ||
                     (entry.IndexFile is { } index && IsMissing(Path.Combine(root, index)))) && Directory.Exists(root))
                    missing.Add(entry.Key);
            }
            else if (IsMissing(entry.Path) || IsMissing(entry.Tag("sacd_image")) ||
                     (entry.CueIndex > 0 && IsMissing(entry.Tag("cuesheet"))))
                missing.Add(entry.Key);
        }

        if (missing.Count == 0)
            return 0;
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var read = db.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT data FROM tracks WHERE path=$key";
        var readKey = read.Parameters.Add("$key", SqliteType.Text);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tracks SET data=$data WHERE path=$key;
            UPDATE track_catalog SET missing=1 WHERE key=$key;
            """;
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var data = command.Parameters.Add("$data", SqliteType.Text);
        int hidden = 0;
        foreach (string item in missing)
        {
            cancellation.ThrowIfCancellationRequested();
            readKey.Value = item;
            if (read.ExecuteScalar() is not string json || ReadRecord<Track>(json, "track") is not { Missing: false } current)
                continue;
            key.Value = item;
            data.Value = System.Text.Json.JsonSerializer.Serialize(current with { Missing = true });
            command.ExecuteNonQuery();
            hidden++;
        }
        transaction.Commit();
        return hidden;
    }

    private static bool ConfirmMissing(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Directory.Exists(Path.GetPathRoot(path));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

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
