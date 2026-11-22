using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Soundroom;

internal sealed record Track(string Path, string Title, string Artist, string Album,
    string Codec, int Rate, int Channels, string Layout, int Bits, long BitRate,
    double Duration, Dictionary<string, string> Tags)
{
    public double Start { get; init; }
    public int CueIndex { get; init; }
    public string? SourceId { get; init; }
    public string? RelativePath { get; init; }
    public string? IndexFile { get; init; }
    public bool Missing { get; init; }
    public string Format { get; init; } = "";
    public long FileSize { get; init; }
    public string Key
    {
        get
        {
            if (SourceId is null)
                return CueIndex > 0 ? $"{Path}|cue:{CueIndex}|{Start:R}" : Path;
            string location = $"{SourceId}|{RelativePath}";
            return CueIndex > 0 ? $"{location}|cue:{IndexFile}|{CueIndex}|{Start:R}" : location;
        }
    }
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string AlbumGroup => $"{Tag("albumartist", "album_artist", "artist")} · {Album}";
    public string Edition => string.Join(" · ", new[] { Tag("edition", "version", "releasecomment"),
        Tag("date", "year", "originaldate"), Tag("label", "publisher"), Tag("catalognumber", "catalog_number"),
        $"{Codec.ToUpperInvariant()} {Rate / 1000.0:0.###}k", Folder }.Where(value => value.Length > 0));
    public string Tag(params string[] keys) =>
        keys.Select(key => Tags.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
    public int Rating { get; init; }
    public int PlayCount { get; init; }
    public TrackAnalysis? Analysis { get; init; }
    public string Subtitle => string.Join("  ·  ", new[] { Artist, Album }.Where(x => x.Length > 0));
    public int SourceRate => Bits == 1 && (Codec.StartsWith("dsd_", StringComparison.Ordinal) || Codec == "dst") ? Rate * 8 : Rate;
    public string Summary => $"{(Bits == 1 ? $"DSD{SourceRate / 44100}" : Codec.ToUpperInvariant())}  ·  " +
        $"{SourceRate / 1000.0:0.###} kHz  ·  {Channels} ch";
}

internal static class Media
{
    internal static readonly string[] Extensions = [".mp3", ".flac", ".wav", ".m4a", ".aif", ".aiff",
        ".aifc", ".wv", ".ape", ".tak", ".opus", ".ogg", ".oga", ".aac", ".alac", ".mpc", ".dsf", ".dff", ".cue", ".iso"];

    public static Process Start(string tool, params string[] arguments)
    {
        if (tool is "ffmpeg" or "ffprobe")
        {
            string local = Path.Combine(AppContext.BaseDirectory, "Tools", "FFmpeg", tool + ".exe");
            if (File.Exists(local))
                tool = local;
        }
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);
        try { return Process.Start(info) ?? throw new IOException($"Could not start {tool}."); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2)
        {
            string name = System.IO.Path.GetFileName(tool);
            throw new IOException($"Could not find {name}. Check that the executable is installed and available on PATH.", error);
        }
    }

    private static string Read(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) ? value.ToString() : "";

    private static int Number(JsonElement element, string key) =>
        int.TryParse(Read(element, key), out int value) ? value : 0;

    private static void AddTags(JsonElement element, Dictionary<string, string> tags)
    {
        if (!element.TryGetProperty("tags", out var values))
            return;
        foreach (var tag in values.EnumerateObject())
            tags[tag.Name] = tag.Value.ToString();
    }

    public static async Task<Track> ProbeAsync(string path)
    {
        using var process = Start("ffprobe", "-v", "error", "-protocol_whitelist", "file,pipe", "-select_streams", "a:0",
            "-show_streams", "-show_format", "-of", "json", "-i", path);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw ToolFailure("ffprobe", process.ExitCode, await error);
        using var json = JsonDocument.Parse(await output);
        var root = json.RootElement;
        var streams = root.GetProperty("streams");
        if (streams.GetArrayLength() == 0)
            throw new IOException("This file has no audio stream.");
        var stream = streams[0];
        var format = root.GetProperty("format");
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddTags(format, tags);
        AddTags(stream, tags);
        var durationText = Read(stream, "duration");
        if (!double.TryParse(durationText, CultureInfo.InvariantCulture, out double duration))
            double.TryParse(Read(format, "duration"), CultureInfo.InvariantCulture, out duration);
        var codec = Read(stream, "codec_name");
        int bits = Read(stream, "bits_per_raw_sample") is { } raw &&
            int.TryParse(raw, out int precision) && precision > 0 ? precision : Number(stream, "bits_per_sample");
        if (codec.StartsWith("dsd", StringComparison.Ordinal) || codec == "dst")
            bits = 1;
        int sampleRate = Number(stream, "sample_rate"), channelCount = Number(stream, "channels");
        if (sampleRate is <= 0 or > 6000000 || channelCount is < 1 or > 32 || !double.IsFinite(duration) || duration < 0)
            throw new IOException("The file reports an unsupported sample rate, channel count, or duration.");
        return new(path, tags.GetValueOrDefault("title", System.IO.Path.GetFileNameWithoutExtension(path)),
            tags.GetValueOrDefault("artist", ""), tags.GetValueOrDefault("album", ""), codec,
            sampleRate, channelCount, Read(stream, "channel_layout"),
            bits, Number(stream, "bit_rate"), duration, tags)
        { Format = Read(format, "format_long_name"), FileSize = new FileInfo(path).Length };
    }

    public static IOException ToolFailure(string tool, int exitCode, string error)
    {
        string detail = error.Trim();
        return new IOException(detail.Length > 0 ? detail : $"{tool} exited with code {exitCode}.");
    }

    public static async Task<string[]> ArtworkNamesAsync(string path)
    {
        using var process = Start("ffprobe", "-v", "error", "-protocol_whitelist", "file,pipe",
            "-select_streams", "v:disp:attached_pic", "-show_streams", "-of", "json", "-i", path);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await error;
        if (process.ExitCode != 0)
            return [];
        using var json = JsonDocument.Parse(await output);
        var streams = json.RootElement.GetProperty("streams");
        return streams.EnumerateArray().Select((stream, index) =>
        {
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddTags(stream, tags);
            var label = tags.GetValueOrDefault("title", tags.GetValueOrDefault("comment", ""));
            return label.Length > 0 ? label : $"Embedded image {index + 1}";
        }).ToArray();
    }

    public static async Task<byte[]> CoverAsync(string path, int index = 0)
    {
        using var process = Start("ffmpeg", "-v", "error", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", path,
            "-map", $"0:v:disp:attached_pic:{index}", "-frames:v", "1", "-vf",
            "scale=800:800:force_original_aspect_ratio=decrease", "-c:v", "png", "-f", "image2pipe", "pipe:1");
        var error = process.StandardError.ReadToEndAsync();
        using var data = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(data);
        await process.WaitForExitAsync();
        await error;
        return process.ExitCode == 0 ? data.ToArray() : [];
    }
}

internal sealed class Library
{
    private readonly string connectionString;

    static Library() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public Library(string? dataDirectory = null)
    {
        string folder = dataDirectory ?? Diagnostics.DataDirectory;
        Directory.CreateDirectory(folder);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = System.IO.Path.Combine(folder, "library.db")
        }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS tracks(path TEXT PRIMARY KEY COLLATE NOCASE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS presets(name TEXT PRIMARY KEY COLLATE NOCASE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(name TEXT PRIMARY KEY, data TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public List<Track> Tracks()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT data FROM tracks ORDER BY path COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var tracks = new List<Track>();
        while (reader.Read())
        {
            if (ReadRecord<Track>(reader.GetString(0), "track") is { Tags: not null, Path.Length: > 0 } track)
                tracks.Add(track);
        }
        return tracks;
    }

    public void Save(Track track)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO tracks VALUES ($path, $data)";
        command.Parameters.AddWithValue("$path", track.Key);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(track));
        command.ExecuteNonQuery();
    }

    public void SavePreset(string name, DspSettings settings)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO presets VALUES ($name, $data)";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(settings));
        command.ExecuteNonQuery();
    }

    public Dictionary<string, DspSettings> Presets()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name, data FROM presets ORDER BY name";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, DspSettings>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (ReadRecord<DspSettings>(reader.GetString(1), "DSP preset") is
                { Parametric: not null, Graphic.Length: 10 } settings)
                result[reader.GetString(0)] = settings;
        }
        return result;
    }

    public void Put<T>(string name, T data)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings VALUES ($name, $data)";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data));
        command.ExecuteNonQuery();
    }

    public T? Get<T>(string name) where T : class
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT data FROM settings WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is string data ? ReadRecord<T>(data, name) : null;
    }

    private static T? ReadRecord<T>(string data, string label) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(data); }
        catch (JsonException error)
        {
            Diagnostics.Write($"Reading saved {label}", error);
            return null;
        }
    }
}
