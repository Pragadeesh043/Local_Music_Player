using System.Collections.Concurrent;
using Microsoft.UI.Xaml;

namespace Soundroom;

public sealed partial class MainWindow
{
    private List<MusicSource> musicSources = [];
    private readonly Dictionary<string, string> mountedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> watchers = new();
    private readonly ConcurrentDictionary<string, long> dirtySources = new();
    private readonly DispatcherTimer sourceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool checkingMounts, scanningSources;

    private void StartSourceMonitoring()
    {
        musicSources = library.Get<List<MusicSource>>("music-sources") ?? [];
        sourceTimer.Tick += async (_, _) => await RunUiAction("Checking music folders", CheckMusicFolders);
        Closed += (_, _) =>
        {
            sourceTimer.Stop();
            foreach (var watcher in watchers.Values)
                watcher.Dispose();
            watchers.Clear();
        };
        sourceTimer.Start();
        _ = RunUiAction("Checking music folders", CheckMusicFolders);
    }

    private async Task RegisterMusicFolder(string folder)
    {
        var source = await Task.Run(() => MusicSource.Register(folder));
        if (!ready)
            return;
        if (musicSources.FirstOrDefault(item => string.Equals(item.Volume, source.Volume, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Folder, source.Folder, StringComparison.OrdinalIgnoreCase)) is { } existing)
            source = existing;
        else
        {
            var updated = musicSources.Append(source).ToList();
            library.Put("music-sources", updated);
            musicSources = updated;
        }
        dirtySources[source.Id] = 0;
        await CheckMusicFolders();
    }

    private bool IsSourceOnline(Track item) => item.SourceId is null || mountedSources.ContainsKey(item.SourceId);

    private Track ResolvePlaybackTrack(Track item)
    {
        if (item.SourceId is not { } id)
            return item;
        if (!mountedSources.TryGetValue(id, out var folder) || !Directory.Exists(folder))
            throw new IOException(OfflineMessage(id));
        if (item.RelativePath is not { } relative || !File.Exists(Path.Combine(folder, relative)))
            throw new FileNotFoundException($"The track is missing from {SourceName(id)}.");
        return item with { Path = Path.Combine(folder, relative) };
    }

    private string SourceName(string id) => musicSources.FirstOrDefault(source => source.Id == id)?.Name ?? "the music drive";
    private string OfflineMessage(string id) => $"Music source is offline. Reconnect {SourceName(id)} to play.";

    private void StopForSource(string id, double position)
    {
        generation++;
        preparingPlayback?.Cancel();
        Stop();
        updatingSeek = true;
        Seek.Value = Math.Clamp(position, 0, Seek.Maximum);
        updatingSeek = false;
        Elapsed.Text = Time(position);
        Status.Text = OfflineMessage(id);
    }

    private async Task CheckMusicFolders()
    {
        if (!ready || checkingMounts || musicSources.Count == 0)
            return;
        checkingMounts = true;
        try
        {
            var volumes = await Task.Run(MusicSource.MountedVolumes);
            if (!ready)
                return;
            bool pathsChanged = false;
            foreach (var source in musicSources.ToArray())
            {
                string? root = source.Resolve(volumes);
                mountedSources.TryGetValue(source.Id, out var previous);
                if (!string.Equals(root, previous, StringComparison.OrdinalIgnoreCase))
                {
                    pathsChanged = true;
                    if (watchers.Remove(source.Id, out var old))
                        old.Dispose();
                    if (root is null)
                    {
                        mountedSources.Remove(source.Id);
                        if (track?.SourceId == source.Id && (playback is not null || preparingPlayback is not null))
                            StopForSource(source.Id, playback?.Position ?? Seek.Value);
                        continue;
                    }
                    mountedSources[source.Id] = root;
                    dirtySources[source.Id] = 0;
                    for (int i = 0; i < tracks.Count; i++)
                        if (tracks[i] is { RelativePath: { } relative } item && item.SourceId == source.Id)
                        {
                            tracks[i] = item with { Path = Path.Combine(root, relative) };
                            if (track?.Key == item.Key)
                                track = tracks[i];
                        }
                    try { watchers[source.Id] = Watch(source.Id, root); }
                    catch (Exception error) { ShowError($"Watching {source.Name}", error); }
                }
            }
            SourceStatus.Text = string.Join("\n", musicSources.Select(source =>
                $"{source.Name} · {(mountedSources.ContainsKey(source.Id) ? "Connected" : "Offline")}"));
            if (pathsChanged)
            {
                RefreshGroups();
                RefreshLibrary();
            }
            if (scanningSources)
                return;
            foreach (var source in musicSources.ToArray())
            {
                if (!ready)
                    break;
                if (!mountedSources.TryGetValue(source.Id, out string? root) ||
                    !dirtySources.TryGetValue(source.Id, out long changed) ||
                    Environment.TickCount64 - changed < 900 ||
                    !dirtySources.TryRemove(new KeyValuePair<string, long>(source.Id, changed)))
                    continue;
                scanningSources = true;
                _ = RunSourceScan(source, root);
                break;
            }
        }
        finally { checkingMounts = false; }
    }

    private async Task RunSourceScan(MusicSource source, string root)
    {
        try { await ScanMusicFolder(source, root); }
        catch (Exception error) { ShowError($"Scanning {source.Name}", error); }
        finally { scanningSources = false; }
    }

    private FileSystemWatcher Watch(string id, string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        void Change(object sender, FileSystemEventArgs args) => dirtySources[id] = Environment.TickCount64;
        watcher.Changed += Change;
        watcher.Created += Change;
        watcher.Deleted += Change;
        watcher.Renamed += Change;
        watcher.Error += (_, _) => dirtySources[id] = 0;
        try { watcher.EnableRaisingEvents = true; return watcher; }
        catch { watcher.Dispose(); throw; }
    }

    private async Task ScanMusicFolder(MusicSource source, string root)
    {
        var old = new Dictionary<string, FileStamp>(
            library.Get<Dictionary<string, FileStamp>>("scan:" + source.Id) ?? [], StringComparer.OrdinalIgnoreCase);
        var inaccessible = new List<string>();
        var snapshot = await Task.Run(() => SourceScanner.Snapshot(root, inaccessible));
        if (!ScanStillMounted(source, root))
            return;
        var changed = snapshot.Where(file => !old.TryGetValue(file.Key, out var stamp) || stamp != file.Value).ToArray();
        var deleted = old.Keys.Where(file => !snapshot.ContainsKey(file) &&
            !inaccessible.Any(folder => file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (changed.Length == 0 && deleted.Length == 0)
            return;
        Status.Text = $"Indexing {source.Name}…";
        int failures = 0;
        var indexed = tracks.Where(item => item.SourceId == source.Id && item.IndexFile is not null)
            .ToLookup(item => item.IndexFile!, StringComparer.OrdinalIgnoreCase);
        var legacy = tracks.Where(item => item.SourceId is null)
            .ToLookup(item => item.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in changed.OrderBy(file => !file.Key.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
        {
            if (!ScanStillMounted(source, root))
                return;
            try
            {
                string path = Path.Combine(root, file.Key);
                Track[] items = path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
                    ? await CueSheet.ReadAsync(path) : [await Media.ProbeAsync(path)];
                if (!ScanStillMounted(source, root))
                    return;
                var current = items.Select(item => item with
                {
                    SourceId = source.Id, IndexFile = file.Key, RelativePath = Path.GetRelativePath(root, item.Path)
                }).ToArray();
                foreach (var item in current)
                {
                    var prior = indexed[file.Key].FirstOrDefault(t => t.Key == item.Key) ??
                        legacy[item.Path].FirstOrDefault(t => t.CueIndex == item.CueIndex);
                    SaveTrack(prior is null ? item : item with { Rating = prior.Rating, PlayCount = prior.PlayCount });
                }
                foreach (var item in indexed[file.Key].Where(item => !current.Any(now => now.Key == item.Key)))
                    SaveTrack(item with { Missing = true });
                old[file.Key] = file.Value;
            }
            catch (Exception error)
            {
                failures++;
                Diagnostics.Write($"Indexing {file.Key}", error);
            }
        }
        if (!ScanStillMounted(source, root) || source.Resolve(MusicSource.MountedVolumes()) != root)
            return;
        foreach (string file in deleted)
        {
            foreach (var item in indexed[file])
                SaveTrack(item with { Missing = true });
            old.Remove(file);
        }
        library.Put("scan:" + source.Id, old);
        RefreshGroups();
        RefreshLibrary();
        Status.Text = failures == 0 ? $"{source.Name} is up to date." : $"{source.Name}: {failures} files could not be indexed. See the Soundroom log.";
    }

    private bool ScanStillMounted(MusicSource source, string root) => ready &&
        mountedSources.TryGetValue(source.Id, out string? current) &&
        string.Equals(current, root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root);
}
