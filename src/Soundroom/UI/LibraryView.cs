using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Soundroom;

public sealed partial class MainWindow
{
    private SmartFilter smartFilter = new();
    private bool changingGroups, scanning;

    private const int PageSize = 80;
    private readonly CancellationTokenSource libraryLifetime = new();
    private Track[] visibleTracks = [];
    private int pageIndex, pageRevision, moveRevision;
    private bool catalogReady;

    private async Task InitializeLibraryAsync()
    {
        var cancellation = libraryLifetime.Token;
        Closed += (_, _) => libraryLifetime.Cancel();
        LibraryCount.Text = "Opening library…";
        try
        {
            await Task.Run(() => library.EnsureCatalog(cancellation), cancellation);
            if (!ready)
                return;
            catalogReady = true;
            StartSourceMonitoring();
            RefreshGroups();
            RefreshLibrary();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { ShowError("Preparing the library index", error); }
    }

    private LibraryQuery CurrentLibraryQuery()
    {
        string? group = LibraryGroup.SelectedItem as string;
        string? edition = Editions.SelectedItem as string;
        return new(Search.Text.Trim(), smartFilter, LibraryView.SelectedIndex == 1 && group != "All" ? group : null,
            LibraryView.SelectedIndex == 2 && group != "All" ? group : null,
            LibraryView.SelectedIndex == 2 && edition != "All" ? edition : null);
    }

    private async void RefreshLibrary(bool reset = false, bool debounce = false)
    {
        if (!catalogReady)
            return;
        if (reset)
            pageIndex = 0;
        int revision = ++pageRevision, requestedPage = pageIndex;
        var query = CurrentLibraryQuery();
        await RunUiAction("Loading library tracks", async () =>
        {
            if (debounce)
                await Task.Delay(150);
            if (!ready || revision != pageRevision)
                return;
            var page = await Task.Run(() => library.Page(query, requestedPage * PageSize, PageSize));
            if (!ready || revision != pageRevision)
                return;
            int pages = Math.Max(1, (page.Total + PageSize - 1) / PageSize);
            if (requestedPage >= pages)
            {
                pageIndex = pages - 1;
                RefreshLibrary();
                return;
            }
            visibleTracks = page.Tracks.Select(WithMountedPath).ToArray();
            TrackList.ItemsSource = visibleTracks;
            RefreshTrackSelection();
            LibraryCount.Text = $"{page.Total:N0} {(page.Total == 1 ? "track" : "tracks")}";
            PageLabel.Text = pages == 1 ? "" : $"{requestedPage + 1:N0} / {pages:N0}";
            PreviousPageButton.IsEnabled = requestedPage > 0;
            NextPageButton.IsEnabled = requestedPage + 1 < pages;
        });
    }

    private void PreviousPage(object sender, RoutedEventArgs args)
    {
        if (pageIndex == 0) return;
        pageIndex--;
        RefreshLibrary();
    }

    private void NextPage(object sender, RoutedEventArgs args)
    {
        pageIndex++;
        RefreshLibrary();
    }

    private void RefreshTrackSelection() => TrackList.SelectedItem =
        visibleTracks.FirstOrDefault(item => item.Key == track?.Key);

    private Func<Track, Track?> CreatePlaybackQueue()
    {
        var query = CurrentLibraryQuery();
        var mounts = new Dictionary<string, string>(mountedSources, StringComparer.OrdinalIgnoreCase);
        string[] ids = mounts.Keys.ToArray();
        return current =>
        {
            var next = library.Neighbors(query, current.Key, backwards: false, limit: 1, onlineSources: ids).FirstOrDefault();
            return next is { SourceId: { } id, RelativePath: { } relative } && mounts.TryGetValue(id, out var folder)
                ? next with { Path = Path.Combine(folder, relative) } : next;
        };
    }

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
            Track latest = library.FindTrack(playing.Key) ?? playing;
            SaveTrack(latest with { PlayCount = latest.PlayCount + 1 });
            RefreshTrackInfo();
        }
        catch (Exception error) { ShowError("Saving playback history", error); }
    }

    private void SyncTrack(Playback session)
    {
        if (track?.Key == session.CurrentTrack.Key)
            return;
        DisplayTrack(session.CurrentTrack);
        CountPlay(session.CurrentTrack);
        session.Inspect(channel);
        ShowSignalPath(session);
    }

    private void LibraryViewChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!catalogReady)
            return;
        RefreshGroups(reset: true);
        RefreshLibrary(reset: true);
    }

    private void LibraryGroupChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!catalogReady || changingGroups)
            return;
        if (ReferenceEquals(sender, LibraryGroup))
            RefreshGroups();
        RefreshLibrary(reset: true);
    }

    private void RefreshGroups(bool reset = false)
    {
        changingGroups = true;
        int view = LibraryView.SelectedIndex;
        var oldGroup = reset ? null : LibraryGroup.SelectedItem as string;
        var oldEdition = reset ? null : Editions.SelectedItem as string;
        var groups = view switch
        {
            1 => library.Groups(1).SelectMany(Ancestors),
            2 => library.Groups(2),
            _ => Enumerable.Empty<string>()
        };
        var names = groups.Distinct(StringComparer.OrdinalIgnoreCase).Order().Prepend("All").ToArray();
        LibraryGroup.Visibility = view > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryGroup.ItemsSource = names;
        LibraryGroup.SelectedItem = oldGroup is not null && names.Contains(oldGroup) ? oldGroup : "All";
        string? album = LibraryGroup.SelectedItem as string;
        bool showEditions = view == 2 && album is not null && album != "All";
        var editions = showEditions
            ? library.Groups(2, album).Prepend("All").ToArray()
            : ["All"];
        Editions.ItemsSource = editions;
        Editions.SelectedItem = oldEdition is not null && editions.Contains(oldEdition) ? oldEdition : "All";
        Editions.Visibility = showEditions ? Visibility.Visible : Visibility.Collapsed;
        changingGroups = false;
    }

    private static IEnumerable<string> Ancestors(string folder)
    {
        for (string? path = folder; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            yield return path;
    }

    private async void EditFilters(object sender, RoutedEventArgs args) =>
        await RunUiAction("Updating library filters", EditFiltersAsync);

    private async Task EditFiltersAsync()
    {
        var playlists = library.Get<Dictionary<string, SmartFilter>>("playlists") ?? new();
        var controls = new StackPanel { Spacing = 8 };
        var picker = new ComboBox { Header = "Smart playlist", ItemsSource = playlists.Keys.Order().ToArray(),
            PlaceholderText = "Choose a saved filter", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Save as (optional)" };
        var codec = new TextBox { Header = "Codec" };
        var rate = new NumberBox { Header = "Minimum sample rate (Hz)", Minimum = 0, Maximum = 5000000 };
        var bits = new NumberBox { Header = "Minimum bit depth", Minimum = 0, Maximum = 64 };
        var dr = new NumberBox { Header = "Minimum scanned DR", Minimum = 0, Maximum = 80 };
        var genre = new TextBox { Header = "Genre" };
        var year = new TextBox { Header = "Year", MaxLength = 4 };
        var rating = new NumberBox { Header = "Minimum rating", Minimum = 0, Maximum = 5 };
        var plays = new NumberBox { Header = "Minimum plays", Minimum = 0, Maximum = 100000 };
        foreach (var control in new FrameworkElement[] { picker, name, codec, rate, bits, dr, genre, year, rating, plays })
            controls.Children.Add(control);
        void Set(SmartFilter filter)
        {
            codec.Text = filter.Codec;
            rate.Value = filter.MinimumRate;
            bits.Value = filter.MinimumBits;
            dr.Value = filter.MinimumDr;
            genre.Text = filter.Genre;
            year.Text = filter.Year;
            rating.Value = filter.Rating;
            plays.Value = filter.Plays;
        }
        Set(smartFilter);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is string chosen && playlists.TryGetValue(chosen, out var filter))
            {
                name.Text = chosen;
                Set(filter);
            }
        };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Library filters",
            Content = new ScrollViewer { Content = controls, MaxHeight = 540 },
            PrimaryButtonText = "Apply", SecondaryButtonText = "Clear", CloseButtonText = "Cancel" };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return;
        smartFilter = result == ContentDialogResult.Secondary ? new() :
            new(codec.Text.Trim(), (int)Value(rate, 0), (int)Value(bits, 0), Value(dr, 0), genre.Text.Trim(),
                year.Text.Trim(), (int)Value(rating, 0), (int)Value(plays, 0));
        if (result == ContentDialogResult.Primary && name.Text.Trim() is { Length: > 0 } title)
        {
            playlists[title] = smartFilter;
            library.Put("playlists", playlists);
        }
        RefreshLibrary(reset: true);
    }

    private async void ScanTrack(object sender, RoutedEventArgs args)
    {
        if (track is not { } selected || scanning)
            return;
        scanning = true;
        Status.Text = $"Analyzing {selected.Title}…";
        try
        {
            var analysis = await FileAnalysis.ScanAsync(ResolvePlaybackTrack(selected));
            var latest = library.FindTrack(selected.Key) ?? selected;
            SaveTrack(latest with { Analysis = analysis });
            RefreshTrackInfo();
            RefreshLibrary();
            Status.Text = "Track analysis complete.";
        }
        catch (Exception error) { ShowError("Analyzing the track", error); }
        finally { scanning = false; }
    }

    private async void ScanAlbum(object sender, RoutedEventArgs args)
    {
        if (track is not { } selected || scanning)
            return;
        scanning = true;
        try
        {
            var album = selected.Album.Length == 0 ? [selected] : library.Page(new("", new(), Album: selected.AlbumGroup, Edition: selected.Edition), 0, 101).Tracks
                .Select(ResolvePlaybackTrack).ToArray();
            Status.Text = $"Scanning {album.Length} tracks in this edition…";
            var albumStats = await FileAnalysis.ScanAlbumAsync(album);
            foreach (var item in album)
            {
                var latest = library.FindTrack(item.Key) ?? item;
                TrackAnalysis analysis = latest.Analysis ?? new(null, null, null, null, "", "");
                SaveTrack(latest with { Analysis = analysis with { AlbumLufs = albumStats.Lufs, AlbumReplayGain = albumStats.ReplayGain } });
            }
            RefreshTrackInfo();
            Status.Text = "Edition loudness scan complete.";
        }
        catch (Exception error) { ShowError("Analyzing the album edition", error); }
        finally { scanning = false; }
    }

    private async void RateTrack(object sender, RoutedEventArgs args) =>
        await RunUiAction("Saving the track rating", RateTrackAsync);

    private async Task RateTrackAsync()
    {
        if (track is not { } selected)
            return;
        var rating = new ComboBox { ItemsSource = new[] { "Unrated", "1", "2", "3", "4", "5" }, SelectedIndex = selected.Rating };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = selected.Title, Content = rating,
            PrimaryButtonText = "Save rating", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var latest = library.FindTrack(selected.Key) ?? selected;
            SaveTrack(latest with { Rating = rating.SelectedIndex });
            RefreshTrackInfo();
            RefreshLibrary();
        }
    }

    private async Task<StorageFile?> PickFile(string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFileAsync();
    }

    private static IEnumerable<string> FindArtwork(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder).Where(item =>
            new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }.Contains(Path.GetExtension(item), StringComparer.OrdinalIgnoreCase))
            .OrderBy(item => Path.GetFileNameWithoutExtension(item).ToLowerInvariant() switch
            {
                "cover" or "folder" or "front" => 0, "back" or "disc" or "artist" or "booklet" => 1, _ => 2
            });
    }

    private async void OpenBooklet(object sender, RoutedEventArgs args)
    {
        if (track is not { } selected)
            return;
        try
        {
            var assets = FindArtwork(selected.Folder).Concat(Directory.Exists(selected.Folder)
                ? Directory.EnumerateFiles(selected.Folder, "*.pdf") : []).ToArray();
            string[] embedded = await Media.ArtworkNamesAsync(selected.Path);
            string[] names = embedded.Concat(assets.Select(item => Path.GetFileName(item))).ToArray();
            var controls = new StackPanel { Spacing = 8 };
            var select = new ComboBox { ItemsSource = names, HorizontalAlignment = HorizontalAlignment.Stretch };
            var image = new Image { MaxHeight = 465, MinWidth = 350 };
            var pageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
            var previous = new Button { Content = "Previous page" };
            var next = new Button { Content = "Next page" };
            var pageLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            foreach (var control in new FrameworkElement[] { previous, pageLabel, next })
                pageRow.Children.Add(control);
            PdfDocument? pdf = null;
            uint page = 0;
            int revision = 0;
            async Task RenderPage()
            {
                if (pdf is null)
                    return;
                int current = ++revision;
                previous.IsEnabled = page > 0;
                next.IsEnabled = page + 1 < pdf.PageCount;
                pageLabel.Text = $"{page + 1} / {pdf.PageCount}";
                using var documentPage = pdf.GetPage(page);
                using var data = new InMemoryRandomAccessStream();
                await documentPage.RenderToStreamAsync(data, new PdfPageRenderOptions { DestinationWidth = 850 });
                data.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(data);
                if (revision == current) image.Source = bitmap;
            }
            async Task SelectAsset()
            {
                int current = ++revision;
                int index = select.SelectedIndex;
                if (index < 0)
                    return;
                try
                {
                    image.Source = null;
                    pdf = null;
                    pageRow.Visibility = Visibility.Collapsed;
                    var bitmap = new BitmapImage();
                    if (index < embedded.Length)
                    {
                        using var stream = new InMemoryRandomAccessStream();
                        using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                        writer.WriteBytes(await Media.CoverAsync(selected.Path, index));
                        await writer.StoreAsync();
                        stream.Seek(0);
                        await bitmap.SetSourceAsync(stream);
                    }
                    else
                    {
                        string path = assets[index - embedded.Length];
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            var document = await PdfDocument.LoadFromFileAsync(file);
                            if (current != revision) return;
                            pdf = document;
                            page = 0;
                            pageRow.Visibility = Visibility.Visible;
                            await RenderPage();
                            return;
                        }
                        using var stream = await file.OpenReadAsync();
                        await bitmap.SetSourceAsync(stream);
                    }
                    if (current == revision) image.Source = bitmap;
                }
                catch (Exception error) { ShowError("Opening the artwork or booklet", error); }
            }
            select.SelectionChanged += async (_, _) => await SelectAsset();
            previous.Click += async (_, _) =>
            {
                if (page > 0) { page--; await RunUiAction("Rendering the previous booklet page", RenderPage); }
            };
            next.Click += async (_, _) =>
            {
                if (pdf is not null && page + 1 < pdf.PageCount) { page++; await RunUiAction("Rendering the next booklet page", RenderPage); }
            };
            if (names.Length == 0)
                controls.Children.Add(new TextBlock { Text = "No embedded artwork or folder booklets found." });
            else
            {
                controls.Children.Add(select);
                controls.Children.Add(pageRow);
                controls.Children.Add(image);
                select.SelectedIndex = 0;
            }
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Artwork and booklet",
                Content = new ScrollViewer { Content = controls, MaxHeight = 580 }, CloseButtonText = "Close" };
            await dialog.ShowAsync();
        }
        catch (Exception error) { ShowError("Opening artwork and booklet", error); }
    }
}
