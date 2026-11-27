using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Soundroom;

public sealed partial class MainWindow : Window
{
    private readonly Library library = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer visibilityTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private Track? track;
    private Playback? playback;
    private CancellationTokenSource? preparingPlayback;
    private bool playbackRequested;
    private int channel;
    private double resumePosition;
    private string status = "";
    private bool ready, importing, choosingMusic, presentationVisible = true;
    private bool mediaToolsAvailable, choosingFFmpeg;
    private DspSettings dsp = DspSettings.Flat();

    public MainWindow()
    {
        thumbnails = new(Path.Combine(Diagnostics.DataDirectory, "ArtworkThumbnails"), lifetime: libraryLifetime.Token);
        InitializeComponent();
        Media.FFmpegFolder = library.Get<string>("ffmpeg-folder");
        mediaToolsAvailable = Media.HasFFmpeg;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "MP4A.ico"));
        ApplyGlassroomTheme(dark: true);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = WindowPresentation.DpiScale(WindowNative.GetWindowHandle(this));
        int Pixels(int units) => (int)Math.Round(units * scale);
        int width = Math.Clamp(work.Width - Pixels(32), Pixels(320), Pixels(1440));
        int height = Math.Clamp(work.Height - Pixels(32), Pixels(250), Pixels(960));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        Closed += (_, _) =>
        {
            ready = false;
            CloseMediaControls();
            deviceTimer.Stop();
            glassroomTimer.Stop();
            glassroom?.Close();
            visibilityTimer.Stop();
            generation++;
            preparingPlayback?.Cancel();
            libraryLifetime.Cancel();
            Stop();
        };

        ready = true;
        timer.Tick += (_, _) =>
        {
            if (playback is not { } session)
                return;
            SyncTrack(session);
            if (session.Underruns > 0 && status.Length == 0)
                status = "Audio buffer ran low during playback.";
            PublishLiveSignal(session);
        };
        visibilityTimer.Tick += (_, _) => RefreshPresentation();
        InitializeOutput();
        libraryInitialization = InitializeLibraryAsync();
        glassroomTimer.Tick += (_, _) => TickGlassroom();
        WindowSurface.Loaded += OpenGlassroomOnLoad;
    }

    private void OpenGlassroomOnLoad(object sender, RoutedEventArgs args)
    {
        WindowSurface.Loaded -= OpenGlassroomOnLoad;
        if (ready)
        {
            InitializeMediaControls();
            InitializeGlassroom();
        }
    }

    private void RefreshPresentation()
    {
        if (!ready)
            return;
        presentationVisible = WindowPresentation.IsExposed(WindowNative.GetWindowHandle(this));
        playback?.SetAnalysisEnabled(presentationVisible && liveSignal);
        timer.Interval = TimeSpan.FromMilliseconds(presentationVisible ? 45 : 450);
        if (playback is not null)
        {
            if (!timer.IsEnabled) timer.Start();
            if (!visibilityTimer.IsEnabled) visibilityTimer.Start();
        }
    }

    private void ShowError(string operation, Exception error)
    {
        string message = Diagnostics.Report(operation, error);
        if (ready)
            status = message;
    }

    private async Task RunUiAction(string operation, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { ShowError(operation, error); }
    }

    private async Task<bool> EnsureFFmpegAsync()
    {
        mediaToolsAvailable = Media.HasFFmpeg;
        if (mediaToolsAvailable || !ready || choosingFFmpeg || WindowSurface.XamlRoot is null)
            return mediaToolsAvailable;

        choosingFFmpeg = true;
        try
        {
            var prompt = new ContentDialog
            {
                XamlRoot = WindowSurface.XamlRoot,
                Title = "Set up FFmpeg",
                Content = "Choose an existing FFmpeg bin folder containing ffmpeg.exe and ffprobe.exe. " +
                    "You can also install both tools in " + Media.UserFFmpegFolder + ".",
                PrimaryButtonText = "Choose folder",
                CloseButtonText = "Cancel"
            };
            if (await prompt.ShowAsync() != ContentDialogResult.Primary)
                return false;

            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is not { } folder || !ready)
                return false;
            if (!File.Exists(Path.Combine(folder.Path, "ffmpeg.exe")) || !File.Exists(Path.Combine(folder.Path, "ffprobe.exe")))
            {
                status = "The selected folder must contain both ffmpeg.exe and ffprobe.exe. Choose the FFmpeg bin folder.";
                return false;
            }

            library.Put("ffmpeg-folder", folder.Path);
            Media.FFmpegFolder = folder.Path;
            mediaToolsAvailable = true;
            status = "";
            _ = RunUiAction("Preparing stored artwork", () => WarmLibraryArtworkAsync());
            return true;
        }
        finally { choosingFFmpeg = false; }
    }

    private void SwitchFullscreen() => AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
        ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);

    private void DisplayTrack(Track selected)
    {
        ConsumeQueuedTrack(selected.Key);
        track = selected;
        resumePosition = 0;
        channel = 0;
    }

    private Task RestartPlaybackAsync() => track is null ? Task.CompletedTask :
        StartAsync(WebPosition(), playbackRequested);

    private Task SeekAsync(string key, double seconds)
    {
        if (playback is { } session)
            SyncTrack(session);
        if (track is not { } current || current.Key != key)
            return Task.CompletedTask;
        if (!double.IsFinite(seconds))
            throw new InvalidOperationException("The seek position is invalid.");
        return StartAsync(Math.Clamp(seconds, 0, current.Duration), playbackRequested, countPlay: false);
    }

    private Task PausePlaybackAsync()
    {
        playbackRequested = false;
        var pause = playback?.PauseAsync() ?? Task.CompletedTask;
        UpdateMediaControls(force: false);
        return pause;
    }

    private async Task ResumePlaybackAsync()
    {
        if (track is not { } selected || playbackRequested)
            return;
        if (preparingPlayback is not null)
        {
            playbackRequested = true;
            return;
        }
        if (playback is not { } session)
        {
            await StartAsync(resumePosition >= selected.Duration ? 0 : resumePosition);
            return;
        }
        playbackRequested = true;
        try
        {
            if (session.State != PlaybackState.Playing)
                await session.WaitUntilBufferedAsync(libraryLifetime.Token);
            if (!ready || !playbackRequested || !ReferenceEquals(playback, session))
                return;
            await session.ResumeAsync();
            if (ready && playbackRequested && ReferenceEquals(playback, session) && session.State == PlaybackState.Playing)
                RememberPlayed(session.CurrentTrack.Key);
        }
        catch (Exception error)
        {
            if (ready && ReferenceEquals(session, playback))
            {
                Stop();
                ShowError($"Playing {selected.Title}", error);
            }
        }
    }

    private async void AddFiles(object sender, RoutedEventArgs args)
    {
        if (importing || reindexing || choosingMusic)
            return;
        choosingMusic = true;
        try
        {
            if (!await EnsureFFmpegAsync())
                return;
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            foreach (string extension in Media.Extensions)
                picker.FileTypeFilter.Add(extension);
            var result = await picker.PickMultipleFilesAsync();
            await Import(result.Select(file => file.Path));
        }
        catch (Exception error) { ShowError("Opening music files", error); }
        finally { choosingMusic = false; }
    }

    private async void AddFolder(object sender, RoutedEventArgs args)
    {
        if (!catalogReady)
        {
            status = "The library index is still opening.";
            return;
        }
        if (importing || reindexing || choosingMusic)
            return;
        choosingMusic = true;
        try
        {
            if (!await EnsureFFmpegAsync())
                return;
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                await RegisterMusicFolder(folder.Path);
        }
        catch (Exception error) { ShowError("Opening the music folder", error); }
        finally { choosingMusic = false; }
    }

    private async Task Import(IEnumerable<string> paths)
    {
        if (importing || reindexing)
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
                status = $"Reading {System.IO.Path.GetFileName(path)}";
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
                        var prior = library.FindTrack(item.Key);
                        SaveTrack(prior is null ? item : item with
                        {
                            Rating = prior.Rating, PlayCount = prior.PlayCount, Analysis = prior.Analysis
                        });
                        await WarmCoverAsync(item, refresh: true);
                        imported++;
                    }
                }
                catch (Exception error) { failed++; failure = Diagnostics.Report($"Importing {System.IO.Path.GetFileName(path)}", error); }
            }
            RefreshLibrary();
            status = failed == 0 ? $"Added {imported} tracks." : $"Added {imported} · {failed} failed. {failure}";
        }
        finally { importing = false; }
    }

    private async Task StartAsync(double seconds, bool play = true, bool countPlay = true)
    {
        int request = ++generation;
        preparingPlayback?.Cancel();
        Stop();
        resumePosition = seconds;
        if (track is not { } selected)
            return;
        if (outputDevice is not { } device)
        {
            status = "Select a Windows audio output.";
            return;
        }
        playbackRequested = play;
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
            var queue = CaptureQueue(selected);
            int revision = Volatile.Read(ref queueRevision);
            var options = new PlaybackOptions(outputProfile.Memory, outputProfile.TargetRate, outputProfile.OutputBits,
                outputProfile.Sharper, ImpulsePath: settings.ImpulsePath, MinimumPhase: outputProfile.MinimumPhase,
                AsioDriver: outputProfile.AsioDriver, Preparation: preparation.Token, NextTrack: current => queue(current, false),
                MemoryLimitMiB: outputProfile.MemoryLimitMiB, CanAdvance: () => Volatile.Read(ref queueRevision) == revision,
                PauseFadeMilliseconds: outputProfile.PauseFadeMilliseconds);
            status = options.Memory ? "Decoding track to RAM…" : "Opening audio stream…";
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
                resumePosition = session.Position;
                Stop();
                if (args.Exception is { } error)
                {
                    ShowError("Playback stopped", error);
                }
                else _ = MoveAsync(1);
            });
            session.SetAnalysisEnabled(presentationVisible && liveSignal);
            session.Open(device, WindowNative.GetWindowHandle(this));
            session.Update(dsp, DigitalVolume());
            session.Inspect(channel);
            RefreshPresentation();
            status = "";
            RefreshVolumeMode();
            if (playbackRequested)
            {
                status = "Buffering audio…";
                await session.WaitUntilBufferedAsync(preparation.Token);
                if (request != generation || !ready)
                    return;
                status = "";
                if (!playbackRequested)
                    return;
                session.Play();
                RememberPlayed(selected.Key);
                if (countPlay && seconds == 0)
                    CountPlay(selected);
            }
        }
        catch (Exception error)
        {
            if (request != generation || !ready)
                return;
            Stop();
            ShowError($"Playing {selected.Title}", error);
        }
        finally
        {
            if (ReferenceEquals(preparingPlayback, preparation))
                preparingPlayback = null;
        }
    }

    private void Stop()
    {
        playbackRequested = false;
        var session = playback;
        playback = null;
        timer.Stop();
        visibilityTimer.Stop();
        session?.Dispose();
    }

    private async Task MoveAsync(int direction)
    {
        if (playback is { } session)
            SyncTrack(session);
        if (track is not { } current)
        {
            if (direction > 0)
            {
                if (webQueued.FirstOrDefault() is { } pending)
                {
                    DisplayTrack(ResolvePlaybackTrack(pending));
                    await StartAsync(0);
                }
            }
            return;
        }
        int request = ++moveRevision;
        int currentGeneration = generation;
        var resolve = CaptureQueue(current);
        int revision = Volatile.Read(ref queueRevision);
        await RunUiAction("Finding the next track", async () =>
        {
            var selected = await Task.Run(() => resolve(current, direction < 0));
            if (!ready || request != moveRevision || currentGeneration != generation)
                return;
            if (revision != Volatile.Read(ref queueRevision))
            {
                await MoveAsync(direction);
                return;
            }
            if (selected is not null)
            {
                DisplayTrack(selected);
                await StartAsync(0);
            }
            else if (shuffleEnabled && direction > 0 && playback is null)
                status = "Shuffle finished. Select a track to start a new shuffle.";
        });
    }

}
