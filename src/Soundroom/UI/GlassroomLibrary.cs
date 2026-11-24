using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace Soundroom;

public sealed partial class MainWindow
{
    private const string MediaOrigin = "https://media.mp4a.glassroom.test/";
    private const string CoverOrigin = MediaOrigin + "cover/";
    private LibraryQuery? webQueueScope;
    private Track[] webQueued = [];

    private sealed record WebAlbum(string Id, string Title, string Artist, int Year, string Genre, string Edition,
        string Artwork, string FirstTrackId);
    private sealed record WebPlaylist(string Id, string Title, string Description);
    private sealed record WebLyric(double At, string Text);
    private static readonly WebPlaylist[] SmartPlaylists =
    [
        new("rated", "Top rated", "Tracks rated four stars or higher"),
        new("played", "Frequently played", "Tracks played at least three times"),
        new("filtered", "Current smart filter", "Tracks matching the current library filters")
    ];

    private Task<object> ReadWebLibraryAsync(JsonElement args)
    {
        if (!catalogReady)
            return Task.FromResult<object>(new { items = Array.Empty<object>(), total = 0 });
        string kind = WebText(args, "kind");
        int offset = Math.Max(0, args.GetProperty("offset").GetInt32());
        int limit = Math.Clamp(args.GetProperty("limit").GetInt32(), 1, 200);
        string search = WebText(args, "query").Trim();
        if (kind == "playlists")
        {
            var saved = SavedFilters().Keys.Order().Select(name => new WebPlaylist("saved:" + name, name, "Saved smart playlist"));
            var lists = SmartPlaylists.Concat(saved).Where(item => $"{item.Title} {item.Description}".Contains(search,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            return Task.FromResult<object>(new { items = lists.Skip(offset).Take(limit), total = lists.Length });
        }
        var query = MakeWebQuery(search, WebText(args, "albumId"), WebText(args, "playlistId"), WebText(args, "folder"));
        return Task.Run<object>(() =>
        {
            if (kind == "albums")
            {
                var page = library.AlbumPage(query, offset, limit);
                return new { items = page.Tracks.Select(ToWebAlbum).ToArray(), page.Total };
            }
            if (kind != "tracks")
                throw new InvalidOperationException("Unknown library view.");
            var tracks = library.Page(query, offset, limit);
            return new { items = tracks.Tracks.Select(ToWebTrack).ToArray(), tracks.Total };
        }, libraryLifetime.Token);
    }

    private LibraryQuery MakeWebQuery(string search, string albumId, string playlist, string folder)
    {
        var filter = playlist switch
        {
            "rated" => new SmartFilter(Rating: 4),
            "played" => new SmartFilter(Plays: 3),
            "filtered" => smartFilter,
            "" => smartFilter,
            _ when playlist.StartsWith("saved:", StringComparison.Ordinal) &&
                SavedFilters().TryGetValue(playlist[6..], out var saved) => saved,
            _ => throw new InvalidOperationException("This smart playlist is unavailable.")
        };
        if (albumId.Length == 0)
            return new(search, filter, Folder: folder.Length == 0 ? null : folder,
                Order: playlist == "played" ? new(LibrarySort.PlayCount, true) : libraryOrder);
        var album = JsonSerializer.Deserialize<string[]>(DecodeWebKey(albumId));
        if (album?.Length != 2)
            throw new InvalidOperationException("This album is unavailable.");
        return new(search, filter, Album: album[0], Edition: album[1]);
    }

    private async Task PlayFromGlassroom(JsonElement args)
    {
        string key = WebText(args, "id");
        if (key.Length == 0)
        {
            await ResumePlaybackAsync();
            return;
        }
        await libraryInitialization;
        var query = MakeWebQuery(WebText(args, "query"), WebText(args, "albumId"), WebText(args, "playlistId"), WebText(args, "folder"));
        var item = await FindWebTrackAsync(key);
        var available = ResolvePlaybackTrack(item);
        webQueued = [];
        webQueueScope = query;
        ResetPlaybackOrder(keepCurrent: false);
        DisplayTrack(available);
        await StartAsync(0);
    }

    private async Task<Track> FindWebTrackAsync(string key)
    {
        var item = await Task.Run(() => library.FindTrack(key), libraryLifetime.Token);
        if (item is null || item.Missing)
            throw new InvalidOperationException("This track is unavailable.");
        return item;
    }

    private WebTrack[] WebQueue() =>
        (track is null ? webQueued : new[] { track }.Concat(webQueued)).Select(ToWebTrack).ToArray();

    private async Task EditGlassroomQueue(string method, JsonElement args)
    {
        string key = WebText(args, "id");
        if (method == "enqueue")
        {
            var item = ResolvePlaybackTrack(await FindWebTrackAsync(key));
            if (track?.Key == key || webQueued.Any(candidate => candidate.Key == key))
                throw new InvalidOperationException("This track is already in the queue.");
            if (webQueued.Length >= 100)
                throw new InvalidOperationException("The manual queue is full.");
            webQueued = [.. webQueued, item];
        }
        else
        {
            int index = args.GetProperty("index").GetInt32() - (track is null ? 0 : 1);
            if (index < 0 || index >= webQueued.Length || webQueued[index].Key != key)
                throw new InvalidOperationException("The queue has changed.");
            if (method == "jump")
            {
                DisplayTrack(ResolvePlaybackTrack(webQueued[index]));
                await StartAsync(0);
                return;
            }
            webQueued = webQueued.Where((_, position) => position != index).ToArray();
        }
        Interlocked.Increment(ref queueRevision);
    }

    private void ConsumeQueuedTrack(string key)
    {
        int index = Array.FindIndex(webQueued, item => item.Key == key);
        if (index >= 0)
            webQueued = webQueued[(index + 1)..];
    }

    private static string EncodeWebKey(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeWebKey(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
    }

    private static string AlbumId(Track item) => EncodeWebKey(JsonSerializer.Serialize(new[] { item.AlbumGroup, item.Edition }));
    private static string WebArtwork(Track item) => CoverOrigin + EncodeWebKey(item.Key);

    private static WebTrack ToWebTrack(Track item) =>
        new(item.Key, item.Title, item.Artist, AlbumId(item), item.Album, WebArtwork(item), item.Duration,
            int.TryParse(item.Tag("track", "tracknumber").Split('/')[0], out int number) ? number : 0);

    private static WebAlbum ToWebAlbum(Track item)
    {
        string artist = item.Tag("albumartist", "album_artist");
        var date = item.Tag("date", "year", "originaldate");
        return new(AlbumId(item), item.Album, artist.Length > 0 ? artist : item.Artist,
            date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), out int year) ? year : 0,
            item.Tag("genre"), item.Edition, WebArtwork(item), item.Key);
    }

    private void ConfigureWebArtwork(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter(MediaOrigin + "*", CoreWebView2WebResourceContext.Image);
        // WebView2 classifies requests from fetch() under XmlHttpRequest.
        core.AddWebResourceRequestedFilter(CoverOrigin + "*", CoreWebView2WebResourceContext.XmlHttpRequest);
        core.WebResourceRequested += async (sender, args) =>
        {
            var uri = args.Request.Uri;
            if (!uri.StartsWith(MediaOrigin, StringComparison.Ordinal))
                return;
            var deferral = args.GetDeferral();
            const string cors = "Access-Control-Allow-Origin: https://mp4a.glassroom.test\r\n";
            bool coverRequest = uri.StartsWith(CoverOrigin, StringComparison.Ordinal);
            try
            {
                IRandomAccessStream? stream = null;
                string type = "image/png";
                if (uri.Length <= 24_000 && args.Request.Method == "GET" && catalogReady)
                {
                    string path = uri[MediaOrigin.Length..];
                    if (path.StartsWith("booklet/", StringComparison.Ordinal))
                        (stream, type) = await ServeBooklet(path);
                    else
                    {
                        string key = DecodeWebKey(coverRequest ? uri[CoverOrigin.Length..] : path);
                        var item = await Task.Run(() => library.FindTrack(key), libraryLifetime.Token);
                        if (item is { Missing: false })
                        {
                            var mounted = WithMountedPath(item);
                            var bytes = await Task.Run(() => ReadCoverBytesAsync(mounted, libraryLifetime.Token), libraryLifetime.Token);
                            type = ArtworkCache.IsWebP(bytes) ? "image/webp" : "image/png";
                            stream = await ImageStream(bytes);
                        }
                    }
                }
                string headers = cors +
                    (stream is null ? "Cache-Control: no-store" : $"Content-Type: {type}\r\nCache-Control: max-age=60");
                args.Response = stream is not null
                    ? sender.Environment.CreateWebResourceResponse(stream, 200, "OK", headers)
                    : sender.Environment.CreateWebResourceResponse(null, coverRequest ? 204 : 404,
                        coverRequest ? "No Content" : "Not Found", headers);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or FormatException or InvalidOperationException)
            {
                if (ready)
                    args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", cors + "Cache-Control: no-store");
            }
            catch (Exception error) { Diagnostics.Write("Serving artwork", error); }
            finally { deferral.Complete(); }
        };
    }

    private async Task<WebLyric[]> ReadWebLyricsAsync(string key)
    {
        var item = WithMountedPath(await FindWebTrackAsync(key));
        return await Task.Run(async () =>
        {
            string lrc = item.Tag("syncedlyrics", "lyrics", "unsyncedlyrics");
            if (item.CueIndex == 0 && (lrc.Length == 0 || !ClipTime.IsMatch(lrc)))
            {
                var file = new FileInfo(Path.ChangeExtension(item.Path, ".lrc"));
                if (file.Exists && file.Length <= 256_000)
                    lrc = await File.ReadAllTextAsync(file.FullName, libraryLifetime.Token);
            }
            return ParseWebLyrics(lrc, item.Duration);
        }, libraryLifetime.Token);
    }

    private static readonly Regex ClipTime = new(@"\[(\d{1,3}):(\d{2}(?:\.\d{1,3})?)\]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex ClipOffset = new(@"\[offset:([+-]?\d{1,8})\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static WebLyric[] ParseWebLyrics(string lrc, double duration)
    {
        if (lrc.Length > 256_000)
            return [];
        var offset = ClipOffset.Match(lrc);
        double shift = offset.Success ? int.Parse(offset.Groups[1].Value, CultureInfo.InvariantCulture) / 1000d : 0;
        var lines = new List<WebLyric>();
        foreach (string line in lrc.Split('\n'))
        {
            var tags = ClipTime.Matches(line);
            string text = ClipTime.Replace(line, "").Trim();
            if (text.Length == 0)
                continue;
            foreach (Match tag in tags)
            {
                double at = Math.Max(0, int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                    double.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture) + shift);
                if (at <= duration)
                    lines.Add(new(at, text));
            }
        }
        return lines.OrderBy(line => line.At).ToArray();
    }
}
