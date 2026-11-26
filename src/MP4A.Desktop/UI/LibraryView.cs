using System.Text.Json;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Soundroom;

public sealed partial class MainWindow
{
    private SmartFilter smartFilter = new();
    private readonly CancellationTokenSource libraryLifetime = new();
    private bool catalogReady;
    private bool scanning;
    private int moveRevision;

    private async Task InitializeLibraryAsync()
    {
        var cancellation = libraryLifetime.Token;
        try
        {
            await Task.Run(() => library.EnsureCatalog(cancellation), cancellation);
            if (!ready)
                return;
            LoadLibraryOrder();
            catalogReady = true;
            StartSourceMonitoring();
            RefreshLibrary();
            _ = RunUiAction("Preparing stored artwork", () => WarmLibraryArtworkAsync());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { ShowError("Preparing the library index", error); }
    }

    private void RefreshLibrary() => webCatalogRevision++;

    private void SaveTrack(Track value)
    {
        value = WithMountedPath(value);
        library.Save(value);
        if (track?.Key == value.Key)
            track = value;
    }

    private void CountPlay(Track playing)
    {
        try
        {
            var latest = library.FindTrack(playing.Key) ?? playing;
            SaveTrack(latest with { PlayCount = latest.PlayCount + 1 });
        }
        catch (Exception error) { ShowError("Saving playback history", error); }
    }

    private void SyncTrack(Playback session)
    {
        while (session.TryTakeTransition(out var played))
        {
            RememberPlayed(played.Key);
            CountPlay(played);
        }
        if (track?.Key == session.CurrentTrack.Key)
            return;
        DisplayTrack(session.CurrentTrack);
        session.Inspect(channel);
    }

    private Dictionary<string, SmartFilter> SavedFilters() => library.Get<Dictionary<string, SmartFilter>>("playlists") ?? new();

    private async Task<object> ReadLibrarySettings()
    {
        await libraryInitialization;
        var sourceInfo = musicSources.Select(source => new { source.Id, source.Name, online = mountedSources.ContainsKey(source.Id) }).ToArray();
        var folders = await Task.Run(() => library.Groups(1).SelectMany(Ancestors)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), libraryLifetime.Token);
        return new { filter = smartFilter, sort = (int)libraryOrder.Field, descending = libraryOrder.Descending,
            saved = SavedFilters(), folders, sources = sourceInfo };
    }

    private static IEnumerable<string> Ancestors(string folder)
    {
        for (string? path = folder; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            yield return path;
    }

    private void SaveLibrarySettings(JsonElement args)
    {
        var filter = args.GetProperty("filter").Deserialize<SmartFilter>(WebJson)
            ?? throw new InvalidOperationException("Invalid library filter.");
        if (filter.Codec is null || filter.Genre is null || filter.Year is null || filter.Year.Length > 4 ||
            filter.MinimumRate is < 0 or > 5_000_000 || filter.MinimumBits is < 0 or > 64 ||
            filter.MinimumDr is < 0 or > 80 || filter.Rating is < 0 or > 5 || filter.Plays is < 0 or > 100_000)
            throw new InvalidOperationException("One or more library filters are out of range.");
        int sort = args.GetProperty("sort").GetInt32();
        if (!Enum.IsDefined((LibrarySort)sort))
            throw new InvalidOperationException("Unknown library sort order.");
        string name = WebText(args, "name").Trim();
        if (name.Length > 0)
        {
            var saved = SavedFilters();
            saved[name] = filter;
            library.Put("playlists", saved);
        }
        smartFilter = filter;
        libraryOrder = new((LibrarySort)sort, args.GetProperty("descending").GetBoolean());
        SavePlaybackOrder();
        RefreshLibrary();
    }

    private async Task<object> ReadTrackDetails(string id)
    {
        var item = WithMountedPath(await FindWebTrackAsync(id));
        var cover = await Task.Run(() => thumbnails.ReadCovers([id]).GetValueOrDefault(id), libraryLifetime.Token);
        return new { item.Title, item.Artist, item.Album, item.Path, item.Format, item.FileSize, item.Codec,
            item.SourceRate, item.Bits, item.BitRate, item.Channels, item.Layout, item.Duration, item.Rating,
            item.PlayCount, item.Analysis, item.Tags, artwork = WebArtwork(item, cover), artworkPreview = cover?.Preview };
    }

    private async Task RateTrack(string id, int rating)
    {
        if (rating is < 0 or > 5)
            throw new InvalidOperationException("Choose a rating between zero and five.");
        SaveTrack((await FindWebTrackAsync(id)) with { Rating = rating });
        RefreshLibrary();
    }

    private async Task AnalyzeTrack(string id, bool edition)
    {
        if (scanning)
            throw new InvalidOperationException("An analysis is already running.");
        scanning = true;
        try
        {
            var selected = await FindWebTrackAsync(id);
            if (edition)
            {
                var items = selected.Album.Length == 0 ? [selected] :
                    (await Task.Run(() => library.Page(new("", new(), Album: selected.AlbumGroup, Edition: selected.Edition), 0, 101),
                        libraryLifetime.Token)).Tracks;
                var album = items.Select(ResolvePlaybackTrack).ToArray();
                status = $"Scanning {album.Length} tracks in this edition…";
                var albumStats = await FileAnalysis.ScanAlbumAsync(album);
                if (!ready)
                    return;
                foreach (var item in album)
                {
                    var latest = library.FindTrack(item.Key) ?? item;
                    var analysis = latest.Analysis ?? new TrackAnalysis(null, null, null, null, "", "");
                    SaveTrack(latest with { Analysis = analysis with { AlbumLufs = albumStats.Lufs, AlbumReplayGain = albumStats.ReplayGain } });
                }
                status = "Edition loudness scan complete.";
            }
            else
            {
                status = $"Analyzing {selected.Title}…";
                var analysis = await FileAnalysis.ScanAsync(ResolvePlaybackTrack(selected));
                if (!ready)
                    return;
                var latest = library.FindTrack(selected.Key) ?? selected;
                SaveTrack(latest with { Analysis = analysis });
                status = "Track analysis complete.";
            }
            RefreshLibrary();
        }
        catch
        {
            status = "Analysis did not complete.";
            throw;
        }
        finally { scanning = false; }
    }

    private async Task<StorageFile?> PickFile(string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFileAsync();
    }
}
