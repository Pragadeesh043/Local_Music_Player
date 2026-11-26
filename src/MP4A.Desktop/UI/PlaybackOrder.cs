namespace Soundroom;

public sealed partial class MainWindow
{
    private sealed record PlaybackPreferences(LibraryOrder Order, bool Shuffle);
    private LibraryOrder libraryOrder;
    private LibraryQuery? currentQueueQuery;
    private int queueRevision;
    private Lazy<TrackSequence>? trackSequence;
    private readonly List<string> shuffleHistory = [];
    private readonly HashSet<string> heardInShuffle = new(StringComparer.OrdinalIgnoreCase);

    private bool shuffleEnabled;

    private void LoadLibraryOrder()
    {
        var saved = library.Get<PlaybackPreferences>("playback-order");
        libraryOrder = saved is not null && Enum.IsDefined(saved.Order.Field) ? saved.Order : default;
        shuffleEnabled = saved?.Shuffle == true;
    }

    private void ToggleShuffle()
    {
        if (playback is { } session)
            SyncTrack(session);
        shuffleEnabled = !shuffleEnabled;
        ResetPlaybackOrder();
        SavePlaybackOrder();
    }

    private void SavePlaybackOrder()
    {
        try { library.Put("playback-order", new PlaybackPreferences(libraryOrder, shuffleEnabled)); }
        catch (Exception error) { ShowError("Saving playback order", error); }
    }

    private void ResetPlaybackOrder(bool keepCurrent = true)
    {
        trackSequence = null;
        currentQueueQuery = null;
        shuffleHistory.Clear();
        heardInShuffle.Clear();
        Interlocked.Increment(ref queueRevision);
        if (keepCurrent && playback is not null && track is { } anchor)
            RememberPlayed(anchor.Key);
    }

    private void RefreshQueueScope(LibraryQuery query)
    {
        var scope = shuffleEnabled ? query with { Order = default } : query;
        if (currentQueueQuery is not null && currentQueueQuery != scope)
        {
            ResetPlaybackOrder();
        }
        currentQueueQuery = scope;
    }

    private void RememberPlayed(string key)
    {
        if (shuffleEnabled && heardInShuffle.Add(key))
            shuffleHistory.Add(key);
    }

    private Func<Track, bool, Track?> CaptureQueue(Track anchor)
    {
        var catalog = CaptureLibraryOrder(anchor);
        var queued = webQueued.Select(WithMountedPath).ToArray();
        return (current, backwards) =>
        {
            if (!backwards)
            {
                int index = Array.FindIndex(queued, item => item.Key == current.Key);
                if (index >= 0 || current.Key == anchor.Key)
                    foreach (var item in queued.Skip(index + 1))
                        if (File.Exists(item.Path))
                            return item;
            }
            return catalog(current, backwards);
        };
    }

    private Func<Track, bool, Track?> CaptureLibraryOrder(Track anchor)
    {
        var query = webQueueScope ?? new("", smartFilter, Order: libraryOrder);
        RefreshQueueScope(query);
        bool shuffled = shuffleEnabled;
        var mounts = new Dictionary<string, string>(mountedSources, StringComparer.OrdinalIgnoreCase);
        string[] online = mounts.Keys.ToArray();
        if ((shuffled || query.Order.Field == LibrarySort.PlayCount) && trackSequence is null)
            trackSequence = new(() => new TrackSequence(library.QueueKeys(query, online, ordered: !shuffled), anchor.Key, shuffled));
        var sequence = trackSequence;
        var prior = shuffled ? shuffleHistory.ToArray() : [];
        return (current, backwards) =>
        {
            Track? Available(Track? item)
            {
                if (item is null || item.Missing)
                    return null;
                if (item.SourceId is { } source)
                {
                    if (item.RelativePath is not { } path || !mounts.TryGetValue(source, out var root))
                        return null;
                    item = item with { Path = Path.Combine(root, path) };
                }
                return File.Exists(item.Path) ? item : null;
            }

            if (shuffled && backwards)
            {
                int start = Array.FindLastIndex(prior, key => key.Equals(current.Key, StringComparison.OrdinalIgnoreCase));
                for (int index = (start < 0 ? prior.Length : start) - 1; index >= 0; index--)
                    if (Available(library.FindTrack(prior[index])) is { } previous)
                        return previous;
                return null;
            }

            if (sequence is not null)
            {
                foreach (string key in sequence.Value.After(current.Key, backwards))
                    if (Available(library.FindTrack(key)) is { } next)
                        return next;
                return null;
            }

            string cursor = current.Key;
            while (true)
            {
                var neighbors = library.Neighbors(query, cursor, backwards, 8, online);
                foreach (var neighbor in neighbors)
                    if (Available(neighbor) is { } next)
                        return next;
                if (neighbors.Length < 8)
                    return null;
                cursor = neighbors[^1].Key;
            }
        };
    }
}
