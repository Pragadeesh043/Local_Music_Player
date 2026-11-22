using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using NAudio.Wave;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Soundroom;

public sealed partial class MainWindow : Window
{
    private readonly Library library = new();
    private readonly List<Track> tracks;
    private readonly Dictionary<string, int> trackIndices = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer seekTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Track? track;
    private Playback? playback;
    private CancellationTokenSource? preparingPlayback;
    private int channel;
    private bool ready, updatingSeek, dragging, importing, updatingDsp;
    private readonly SolidColorBrush accent = new(ColorHelper.FromArgb(255, 250, 113, 130));
    private readonly SolidColorBrush neutral = new(ColorHelper.FromArgb(255, 183, 176, 187));
    private readonly List<ProgressBar> sourceBars = [], outputBars = [];
    private readonly List<Button> speakers = [];
    private string[] channelLabels = [];
    private readonly Slider[] graphics = new Slider[DspSettings.Centers.Length];
    private Slider preamp = null!, bass = null!, width = null!, crossfeed = null!;
    private readonly ComboBox presetMenu = new() { PlaceholderText = "Load a preset", MinWidth = 135 };
    private DspSettings dsp = DspSettings.Flat();

    public MainWindow()
    {
        InitializeComponent();
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        int windowWidth = Math.Min(1440, work.Width - 32);
        int windowHeight = Math.Min(960, work.Height - 32);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - windowWidth) / 2, work.Y + (work.Height - windowHeight) / 2,
            windowWidth, windowHeight));
        AppWindow.TitleBar.BackgroundColor = ColorHelper.FromArgb(255, 30, 27, 35);
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.BackgroundColor;
        AppWindow.TitleBar.ForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        tracks = library.Tracks();
        for (int i = 0; i < tracks.Count; i++)
            trackIndices[tracks[i].Key] = i;
        LibraryView.SelectedIndex = 0;
        RefreshLibrary();
        BuildDsp();
        string? deviceError = null;
        try { Devices.ItemsSource = Playback.Devices(); }
        catch (Exception error)
        {
            Devices.ItemsSource = new List<OutputDevice>();
            deviceError = Diagnostics.Report("Detecting audio outputs", error);
        }
        Devices.SelectedIndex = Devices.Items.Count > 0 ? 0 : -1;
        Routes.SelectedIndex = Devices.SelectedItem is OutputDevice { Channels: 8 } device &&
            device.Name.Contains("Cable", StringComparison.OrdinalIgnoreCase) ? (int)Route.Cable : (int)Route.Binaural;
        LoadOutputProfile();
        Seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => dragging = true), true);
        Seek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            dragging = false;
            SeekTimerStart();
        }), true);
        Seek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            if (dragging)
            {
                dragging = false;
                SeekTimerStart();
            }
        }), true);
        Seek.ValueChanged += (_, _) => { if (!updatingSeek && !dragging) SeekTimerStart(); };
        seekTimer.Tick += (_, _) =>
        {
            seekTimer.Stop();
            if (track is not null)
                Start(Seek.Value, playback?.State == PlaybackState.Playing);
        };
        timer.Tick += (_, _) => RefreshPlayback();
        timer.Start();
        Closed += (_, _) => { ready = false; timer.Stop(); seekTimer.Stop(); deviceTimer.Stop(); generation++; preparingPlayback?.Cancel(); Stop(); };
        ready = true;
        deviceTimer.Tick += (_, _) => RefreshDevices();
        deviceTimer.Start();
        StartSourceMonitoring();
        Status.Text = deviceError ?? (Devices.Items.Count > 0 ? "" : "Windows reports no active audio output.");
    }

    private void ShowError(string operation, Exception error)
    {
        string message = Diagnostics.Report(operation, error);
        if (ready)
            Status.Text = message;
    }

    private async Task RunUiAction(string operation, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { ShowError(operation, error); }
    }

    private void SeekTimerStart() { seekTimer.Stop(); if (!updatingSeek) seekTimer.Start(); }

    private void RefreshLibrary()
    {
        var query = Search.Text.Trim();
        var visible = FilterTracks(query).ToList();
        TrackList.ItemsSource = visible;
        TrackList.SelectedItem = track;
        LibraryCount.Text = $"{visible.Count} {(visible.Count == 1 ? "track" : "tracks")}";
    }

    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        if (ready)
            RefreshLibrary();
    }

    private async void AddFiles(object sender, RoutedEventArgs args)
    {
        if (importing)
            return;
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            foreach (string extension in Media.Extensions)
                picker.FileTypeFilter.Add(extension);
            var result = await picker.PickMultipleFilesAsync();
            await Import(result.Select(file => file.Path));
        }
        catch (Exception error) { ShowError("Opening music files", error); }
    }

    private async void AddFolder(object sender, RoutedEventArgs args)
    {
        if (importing)
            return;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                await RegisterMusicFolder(folder.Path);
        }
        catch (Exception error) { ShowError("Opening the music folder", error); }
    }

    private async Task Import(IEnumerable<string> paths)
    {
        if (importing)
            return;
        importing = true;
        int imported = 0, failed = 0;
        string failure = "";
        try
        {
            var cueSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths.OrderBy(path => !System.IO.Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase)))
            {
                if (cueSources.Contains(path))
                    continue;
                Status.Text = $"Reading {System.IO.Path.GetFileName(path)}";
                try
                {
                    Track[] items = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".cue" => await CueSheet.ReadAsync(path),
                        ".iso" => await SacdImage.ImportAsync(path),
                        _ => [await Media.ProbeAsync(path)]
                    };
                    foreach (var item in items)
                    {
                        if (item.CueIndex > 0)
                            cueSources.Add(item.Path);
                        var prior = tracks.FirstOrDefault(t => t.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
                        SaveTrack(prior is null ? item : item with
                        {
                            Rating = prior.Rating, PlayCount = prior.PlayCount, Analysis = prior.Analysis
                        });
                        imported++;
                    }
                }
                catch (Exception error) { failed++; failure = Diagnostics.Report($"Importing {System.IO.Path.GetFileName(path)}", error); }
            }
            RefreshGroups();
            RefreshLibrary();
            Status.Text = failed == 0 ? $"Added {imported} tracks." : $"Added {imported} · {failed} failed. {failure}";
        }
        finally { importing = false; }
    }

    private void TrackClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is Track selected)
            SelectTrack(selected);
    }

    private void SelectTrack(Track selected)
    {
        DisplayTrack(selected);
        Start(0);
    }

    private void DisplayTrack(Track selected)
    {
        track = selected;
        TrackList.SelectedItem = track;
        TrackTitle.Text = track.Title;
        TrackSubtitle.Text = track.Subtitle;
        TrackFormat.Text = track.Summary;
        Duration.Text = Time(track.Duration);
        Elapsed.Text = "0:00";
        updatingSeek = true;
        Seek.Maximum = Math.Max(1, track.Duration);
        Seek.Value = 0;
        updatingSeek = false;
        channel = 0;
        RefreshTrackInfo();
        Cover.Source = null;
        ArtworkFallback.Visibility = Visibility.Visible;
        _ = LoadCover(selected);
    }

    private void RefreshTrackInfo()
    {
        if (track is null)
            return;
        var analysis = track.Analysis;
        TrackInfo.Text = string.Join("\n", new[]
        {
            track.Path, "", $"Container    {track.Format}", $"Size    {track.FileSize:N0} bytes",
            $"Codec    {track.Codec}", $"Sample rate    {track.SourceRate:N0} Hz",
            $"Bit depth    {(track.Bits > 0 ? $"{track.Bits} bit" : "—")}",
            $"Bit rate    {(track.BitRate > 0 ? $"{track.BitRate / 1000.0:0.#} kbps" : "—")}",
            $"Channels    {track.Channels}",
            $"Layout    {(track.Layout.Length > 0 ? track.Layout : "Unspecified")}",
            $"Duration    {Time(track.Duration)}", $"Rating    {track.Rating}/5 · Plays {track.PlayCount}", "",
            analysis is null ? "" :
                $"Integrated loudness    {Metric(analysis.Lufs, "LUFS")}\nTrue peak    {Metric(analysis.TruePeak, "dBTP")}\n" +
                $"Dynamic range (estimate)    {Metric(analysis.DynamicRange, "dB")}\nReplayGain    {Metric(analysis.ReplayGain, "dB")}\n" +
                $"Album loudness    {Metric(analysis.AlbumLufs, "LUFS")}\nAlbum ReplayGain    {Metric(analysis.AlbumReplayGain, "dB")}\n" +
                $"{analysis.Spectrum}\n{analysis.Integrity}\n"
        }.Concat(track.Tags.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}    {pair.Value}")));
    }

    private static string Metric(double? value, string unit) => value is double number ? $"{number:0.0} {unit}" : "—";

    private async Task LoadCover(Track selected)
    {
        try
        {
            byte[] bytes = await Media.CoverAsync(selected.Path);
            if (bytes.Length == 0 && FindArtwork(selected.Folder).FirstOrDefault() is { } artwork)
                bytes = await File.ReadAllBytesAsync(artwork);
            if (bytes.Length == 0 || selected.Key != track?.Key)
                return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (selected.Key != track?.Key)
                return;
            Cover.Source = image;
            ArtworkFallback.Visibility = Visibility.Collapsed;
            var visual = ElementCompositionPreview.GetElementVisual(Cover);
            using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1);
            fade.Duration = TimeSpan.FromMilliseconds(300);
            visual.StartAnimation("Opacity", fade);
        }
        catch (Exception error) { Diagnostics.Write($"Loading artwork for {selected.Title}", error); }
    }

    private Route SelectedRoute => (Route)Math.Max(0, Routes.SelectedIndex);

    private async void Start(double seconds, bool play = true)
    {
        int request = ++generation;
        preparingPlayback?.Cancel();
        Stop();
        if (track is not { } selected)
            return;
        if (Devices.SelectedItem is not OutputDevice device)
        {
            Status.Text = "Select a Windows audio output.";
            return;
        }
        using var preparation = new CancellationTokenSource();
        preparingPlayback = preparation;
        try
        {
            selected = ResolvePlaybackTrack(selected);
            var route = SelectedRoute;
            if (route == Route.Dop && !outputProfile.DopConfirmed)
                throw new InvalidOperationException("Confirm DoP support for this DAC in Output settings.");
            var settings = dsp;
            float volume = DigitalVolume();
            var options = new PlaybackOptions(outputProfile.Memory, outputProfile.TargetRate, outputProfile.OutputBits,
                outputProfile.Sharper, Following(selected), settings.ImpulsePath, outputProfile.MinimumPhase,
                outputProfile.AsioDriver, preparation.Token);
            Status.Text = options.Memory ? "Decoding track to RAM…" : "Opening audio stream…";
            var session = await Task.Run(() => new Playback(selected, device, route, seconds, settings, volume, options));
            if (request != generation || !ready)
            {
                session.Dispose();
                return;
            }
            playback = session;
            session.Stopped += (_, args) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(session, playback))
                    return;
                SyncTrack(session);
                if (session.CurrentTrack.SourceId is { } sourceId && !File.Exists(session.CurrentTrack.Path))
                {
                    StopForSource(sourceId, session.Position);
                    return;
                }
                Stop();
                if (args.Exception is { } error)
                {
                    updatingSeek = true;
                    Seek.Value = Math.Clamp(session.Position, 0, Seek.Maximum);
                    updatingSeek = false;
                    ShowError("Playback stopped", error);
                }
                else Move(1);
            });
            session.Open(device, WindowNative.GetWindowHandle(this));
            session.Update(dsp, DigitalVolume());
            session.Inspect(channel);
            SetMeters(session.SourceChannels, session.OutputChannels);
            ShowSignalPath(session);
            Status.Text = "";
            RefreshVolumeMode();
            if (play)
            {
                Status.Text = "Buffering audio…";
                await session.WaitUntilBufferedAsync(preparation.Token);
                if (request != generation || !ready)
                    return;
                Status.Text = "";
                session.Play();
                if (seconds == 0)
                    CountPlay(selected);
            }
        }
        catch (Exception error)
        {
            if (request != generation || !ready)
                return;
            Stop();
            SignalPath.Text = "";
            SetMeters(Speakers.From(selected), []);
            ShowError($"Playing {selected.Title}", error);
        }
        finally
        {
            if (ReferenceEquals(preparingPlayback, preparation))
                preparingPlayback = null;
        }
        UpdatePlayIcon();
    }

    private string RoutesText() => SelectedRoute switch
    {
        Route.Binaural => "HRTF",
        Route.Cable => "external processing",
        Route.Exclusive => "integer PCM · DSP bypass · fixed gain",
        Route.ExclusiveDsp => "64-bit DSP · integer output",
        Route.Dop => "DoP · fixed gain · DSP bypass",
        Route.NativeDsd => "native DSD · fixed gain · DSP bypass",
        _ => "stereo downmix"
    };

    private void Stop()
    {
        var session = playback;
        playback = null;
        session?.Dispose();
        UpdatePlayIcon();
    }

    private void OutputChanged(object sender, SelectionChangedEventArgs args)
    {
        if (changingDevice || !ready)
            return;
        try
        {
            if (ReferenceEquals(sender, Devices))
                LoadOutputProfile();
            else
                RefreshVolumeMode();
            if (track is not null)
                Start(playback?.Position ?? Seek.Value, playback?.State == PlaybackState.Playing);
        }
        catch (Exception error) { ShowError("Changing audio output", error); }
    }

    private void TogglePlay(object sender, RoutedEventArgs args)
    {
        if (preparingPlayback is not null)
            return;
        try
        {
            if (playback?.State == PlaybackState.Playing) playback.Pause();
            else if (playback is not null) playback.Play();
            else if (track is not null) Start(Seek.Value >= track.Duration ? 0 : Seek.Value);
            else if (tracks.FirstOrDefault() is { } first) SelectTrack(first);
        }
        catch (Exception error)
        {
            Stop();
            ShowError("Changing playback state", error);
        }
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon() => PlayIcon.Glyph = playback?.State == PlaybackState.Playing ? "\uE769" : "\uE768";
    private void Next(object sender, RoutedEventArgs args) => Move(1);
    private void Previous(object sender, RoutedEventArgs args) => Move(-1);

    private void Move(int direction)
    {
        var list = QueueTracks();
        int index = track is null ? -1 : Array.FindIndex(list, t => t.Key == track.Key);
        if (index + direction >= 0 && index + direction < list.Length)
            SelectTrack(list[index + direction]);
    }

    private void VolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!ready || changingDevice)
            return;
        UpdateOutputVolume();
    }

    private void RefreshPlayback()
    {
        if (playback is null || track is null)
            return;
        SyncTrack(playback);
        if (playback.Underruns > 0 && Status.Text.Length == 0)
            Status.Text = "Audio buffer ran low during playback.";
        double position = Math.Clamp(playback.Position, 0, Seek.Maximum);
        if (!dragging && !seekTimer.IsEnabled)
        {
            updatingSeek = true;
            Seek.Value = position;
            updatingSeek = false;
        }
        Elapsed.Text = Time(position);
        if (playback.State == PlaybackState.Playing && playback.Latest is { } signal)
            DrawSignal(signal);
    }

    private static string Time(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" :
            $"{time.Minutes}:{time.Seconds:00}";
    }
}
