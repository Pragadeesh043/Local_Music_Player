using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Soundroom;

internal sealed record SmartFilter(string Codec = "", int MinimumRate = 0, int MinimumBits = 0,
    double MinimumDr = 0, string Genre = "", string Year = "", int Rating = 0, int Plays = 0)
{
    public bool Matches(Track track) =>
        (Codec.Length == 0 || track.Codec.Contains(Codec, StringComparison.OrdinalIgnoreCase)) &&
        track.Rate >= MinimumRate && track.Bits >= MinimumBits &&
        (MinimumDr == 0 || track.Analysis?.DynamicRange >= MinimumDr) &&
        (Genre.Length == 0 || track.Tag("genre").Contains(Genre, StringComparison.OrdinalIgnoreCase)) &&
        (Year.Length == 0 || track.Tag("date", "year").StartsWith(Year, StringComparison.Ordinal)) &&
        track.Rating >= Rating && track.PlayCount >= Plays;
}

public sealed partial class MainWindow
{
    private SmartFilter smartFilter = new();
    private bool changingGroups, scanning;

    private IEnumerable<Track> FilterTracks(string query)
    {
        string? group = LibraryGroup.SelectedItem as string;
        string? edition = Editions.SelectedItem as string;
        var coveredPaths = tracks.Where(item => !item.Missing && item.SourceId is not null && item.CueIndex > 0)
            .Select(item => (item.SourceId, item.RelativePath)).ToHashSet();
        var registeredPaths = tracks.Where(item => !item.Missing && item.SourceId is not null)
            .Select(item => (item.Path.ToUpperInvariant(), item.CueIndex)).ToHashSet();
        var result = tracks.Where(item => !item.Missing &&
            (item.SourceId is not null || !registeredPaths.Contains((item.Path.ToUpperInvariant(), item.CueIndex))) &&
            (item.CueIndex > 0 || !coveredPaths.Contains((item.SourceId, item.RelativePath))) && smartFilter.Matches(item) &&
            (query.Length == 0 || new[] { item.Title, item.Artist, item.Album, item.Folder }.Concat(item.Tags.Values)
                .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase))));
        if (group is not null && group != "All" && LibraryView.SelectedIndex == 1)
            result = result.Where(item => item.Folder.Equals(group, StringComparison.OrdinalIgnoreCase) ||
                Path.GetRelativePath(group, item.Folder) is string relative && !relative.StartsWith("..", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
        if (group is not null && group != "All" && LibraryView.SelectedIndex == 2)
            result = result.Where(item => item.AlbumGroup == group);
        if (edition is not null && edition != "All" && LibraryView.SelectedIndex == 2)
            result = result.Where(item => item.Edition == edition);
        return result.OrderBy(item => item.AlbumGroup).ThenBy(item => item.Edition)
            .ThenBy(item => TagNumber(item.Tag("disc", "discnumber")))
            .ThenBy(item => TagNumber(item.Tag("track", "tracknumber"))).ThenBy(item => item.Title);
    }

    private static int TagNumber(string tag) => int.TryParse(tag.Split('/')[0], out int value) ? value : int.MaxValue;

    private Track[] QueueTracks() => FilterTracks(Search.Text.Trim()).ToArray();

    private Track[] Following(Track current)
    {
        var list = QueueTracks();
        int index = Array.FindIndex(list, item => item.Key == current.Key);
        return index >= 0 ? list[(index + 1)..].Where(IsSourceOnline).ToArray() : [];
    }

    private void SaveTrack(Track value)
    {
        library.Save(value);
        if (trackIndices.TryGetValue(value.Key, out int index))
            tracks[index] = value;
        else
        {
            trackIndices[value.Key] = tracks.Count;
            tracks.Add(value);
        }
        if (track?.Key == value.Key)
            track = value;
    }

    private void CountPlay(Track playing)
    {
        Track latest = tracks.FirstOrDefault(item => item.Key == playing.Key) ?? playing;
        try
        {
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
        if (!ready)
            return;
        RefreshGroups(reset: true);
        RefreshLibrary();
    }

    private void LibraryGroupChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ready || changingGroups)
            return;
        if (ReferenceEquals(sender, LibraryGroup))
            RefreshGroups();
        RefreshLibrary();
    }

    private void RefreshGroups(bool reset = false)
    {
        changingGroups = true;
        int view = LibraryView.SelectedIndex;
        var oldGroup = reset ? null : LibraryGroup.SelectedItem as string;
        var oldEdition = reset ? null : Editions.SelectedItem as string;
        var groups = view switch
        {
            1 => tracks.SelectMany(item => Ancestors(item.Folder)),
            2 => tracks.Where(item => item.Album.Length > 0).Select(item => item.AlbumGroup),
            _ => Enumerable.Empty<string>()
        };
        var names = groups.Distinct(StringComparer.OrdinalIgnoreCase).Order().Prepend("All").ToArray();
        LibraryGroup.Visibility = view > 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryGroup.ItemsSource = names;
        LibraryGroup.SelectedItem = oldGroup is not null && names.Contains(oldGroup) ? oldGroup : "All";
        string? album = LibraryGroup.SelectedItem as string;
        bool showEditions = view == 2 && album is not null && album != "All";
        var editions = showEditions
            ? tracks.Where(item => item.AlbumGroup == album).Select(item => item.Edition).Distinct().Order().Prepend("All").ToArray()
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
        RefreshLibrary();
    }

    private async void ScanTrack(object sender, RoutedEventArgs args)
    {
        if (track is not { } selected || scanning)
            return;
        scanning = true;
        Status.Text = $"Analyzing {selected.Title}…";
        try
        {
            var analysis = await FileAnalysis.ScanAsync(selected);
            var latest = tracks.FirstOrDefault(item => item.Key == selected.Key) ?? selected;
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
            var album = selected.Album.Length == 0 ? [selected] : tracks.Where(item =>
                item.AlbumGroup == selected.AlbumGroup && item.Edition == selected.Edition).ToArray();
            Status.Text = $"Scanning {album.Length} tracks in this edition…";
            var albumStats = await FileAnalysis.ScanAlbumAsync(album);
            foreach (var item in album)
            {
                var latest = tracks.FirstOrDefault(t => t.Key == item.Key) ?? item;
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
            var latest = tracks.FirstOrDefault(item => item.Key == selected.Key) ?? selected;
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
