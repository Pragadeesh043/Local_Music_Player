using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using NAudio.Wave;

namespace Soundroom;

public sealed partial class MainWindow
{
    private const string GlassroomUrl = "https://mp4a.glassroom.test/index.html";
    private const int RuntimeFileNotFound = unchecked((int)0x80070002);
    private const int RuntimePathNotFound = unchecked((int)0x80070003);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly DispatcherTimer glassroomTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private Task libraryInitialization = Task.CompletedTask;
    private WebView2? glassroom;
    private bool glassroomConnected;
    private WebStatus? publishedWebStatus;
    private int webCatalogRevision;

    private sealed record WebStatus(string? Key, bool Playing, double Volume, bool VolumeEnabled, string Status,
        int CatalogRevision, string Output, bool Shuffled, bool Fullscreen);
    private sealed record WebTrack(string Id, string Title, string Artist, string AlbumId, string Album,
        string? Artwork, double Duration, int Number);

    private async void InitializeGlassroom()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Glassroom");
        if (!File.Exists(Path.Combine(folder, "index.html")))
        {
            GlassroomFailed(new FileNotFoundException("Glassroom assets are missing. Rebuild or reinstall MP4A."));
            return;
        }

        var view = new WebView2
        {
            DefaultBackgroundColor = Colors.Transparent,
            Opacity = 0,
            IsHitTestVisible = false
        };
        glassroom = view;
        Exception? initializationError = null;
        void RecordInitialization(WebView2 sender, CoreWebView2InitializedEventArgs args) => initializationError = args.Exception;
        view.CoreWebView2Initialized += RecordInitialization;
        try
        {
            WindowSurface.Children.Add(view);
            CoreWebView2Environment environment;
            try
            {
                environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                    null, Path.Combine(Diagnostics.DataDirectory, "WebView2"), null);
            }
            catch (Exception error) when (error.HResult is RuntimeFileNotFound or RuntimePathNotFound)
            {
                if (ready && glassroom == view)
                    GlassroomFailed(new InvalidOperationException(
                        "Install the Microsoft Edge WebView2 Runtime, then select Reload Glassroom.", error),
                        needsRuntime: true);
                return;
            }
            if (!ready || glassroom != view)
                return;
            await view.EnsureCoreWebView2Async(environment);
            if (!ready || glassroom != view)
                return;

            var core = view.CoreWebView2 ?? throw new InvalidOperationException(
                "WebView2 could not create its browser controller.", initializationError);
            core.SetVirtualHostNameToFolderMapping("mp4a.glassroom.test", folder, CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != GlassroomUrl;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += (_, args) =>
            {
                if (glassroom == view && !args.IsSuccess)
                    GlassroomFailed(new InvalidOperationException($"Navigation failed ({args.WebErrorStatus})."));
            };
            core.ProcessFailed += (_, args) =>
            {
                if (glassroom == view)
                    GlassroomFailed(new InvalidOperationException($"WebView2 stopped ({args.ProcessFailedKind})."));
            };
            core.WebMessageReceived += ReceiveGlassroomMessage;
            ConfigureWebArtwork(core);
            core.Navigate(GlassroomUrl);
        }
        catch (Exception error)
        {
            if (ready && glassroom == view)
                GlassroomFailed(error);
        }
        finally
        {
            view.CoreWebView2Initialized -= RecordInitialization;
        }
    }

    private void GlassroomFailed(Exception error, bool needsRuntime = false)
    {
        glassroomConnected = false;
        glassroomTimer.Stop();
        if (glassroom is { } view)
        {
            view.Opacity = 0;
            view.IsHitTestVisible = false;
        }
        StartupPanel.Visibility = Visibility.Visible;
        StartupStatus.Text = Diagnostics.Report("Opening Glassroom", error);
        GetWebViewRuntime.Visibility = needsRuntime ? Visibility.Visible : Visibility.Collapsed;
        RestartGlassroom.Visibility = Visibility.Visible;
    }

    private void ReloadGlassroom(object sender, RoutedEventArgs args)
    {
        RestartGlassroom.Visibility = Visibility.Collapsed;
        GetWebViewRuntime.Visibility = Visibility.Collapsed;
        StartupStatus.Text = "Opening Glassroom…";
        if (glassroom is { } view)
        {
            glassroom = null;
            view.Close();
            WindowSurface.Children.Remove(view);
        }
        InitializeGlassroom();
    }

    private void DisplayGlassroom()
    {
        if (glassroom is null || !glassroomConnected)
            return;
        StartupPanel.Visibility = Visibility.Collapsed;
        glassroom.Opacity = 1;
        glassroom.IsHitTestVisible = true;
        glassroom.Focus(FocusState.Programmatic);
    }

    private async void ReceiveGlassroomMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs message)
    {
        if (!ready || message.Source != GlassroomUrl || message.WebMessageAsJson.Length > 32_768)
            return;
        int id = 0;
        try
        {
            using var document = JsonDocument.Parse(message.WebMessageAsJson);
            var request = document.RootElement;
            id = request.GetProperty("id").GetInt32();
            if (id <= 0 || request.GetProperty("v").GetInt32() != 1)
                return;
            var args = request.GetProperty("args");
            string method = request.GetProperty("method").GetString() ?? "";
            object? data = await HandleGlassroomRequest(method, args);
            if (!ready || glassroom is null)
                return;
            if (method is "play" or "pause" or "seek" or "next" or "previous" or "volume" or "enqueue" or "remove" or "jump" or "selectOutput" or "saveOutput")
                PublishWebState(force: true);
            PostGlassroom(new { v = 1, id, ok = true, result = data });
        }
        catch (Exception error)
        {
            if (!ready || id <= 0)
                return;
            string detail = error is InvalidOperationException or IOException
                ? error.Message : Diagnostics.Report("Handling the player command", error);
            PostGlassroom(new { v = 1, id, ok = false, error = detail });
        }
    }

    private async Task<object?> HandleGlassroomRequest(string method, JsonElement args)
    {
        switch (method)
        {
            case "state":
                if (!glassroomConnected)
                {
                    glassroomConnected = true;
                    DisplayGlassroom();
                    glassroomTimer.Start();
                }
                return WebPlayback(ReadWebStatus());
            case "library":
                await libraryInitialization;
                return await ReadWebLibraryAsync(args);
            case "lyrics":
                return await ReadWebLyricsAsync(WebText(args, "id"));
            case "play":
                await PlayFromGlassroom(args);
                break;
            case "pause":
                PausePlayback();
                break;
            case "seek":
                await SeekAsync(WebText(args, "id"), args.GetProperty("position").GetDouble());
                break;
            case "volume":
                UpdateOutputVolume(Math.Clamp(args.GetProperty("value").GetDouble(), 0, 1) * 100);
                break;
            case "next":
            case "previous":
                await MoveAsync(method == "next" ? 1 : -1);
                break;
            case "enqueue":
            case "remove":
            case "jump":
                await EditGlassroomQueue(method, args);
                break;
            case "theme":
                ApplyGlassroomTheme(args.GetProperty("dark").GetBoolean());
                break;
            case "outputSettings":
                return await ReadOutput();
            case "selectOutput":
                await SelectOutput(WebText(args, "id"));
                break;
            case "saveOutput":
                await SaveOutput(WebText(args, "id"), args.GetProperty("profile").Deserialize<OutputProfile>(WebJson)
                    ?? throw new InvalidOperationException("Invalid output profile."));
                break;
            case "librarySettings":
                return await ReadLibrarySettings();
            case "saveLibrary":
                SaveLibrarySettings(args);
                break;
            case "trackDetails":
                return await ReadTrackDetails(WebText(args, "id"));
            case "rateTrack":
                await RateTrack(WebText(args, "id"), args.GetProperty("rating").GetInt32());
                break;
            case "analyzeTrack":
                await AnalyzeTrack(WebText(args, "id"), args.GetProperty("edition").GetBoolean());
                break;
            case "booklet":
                return await ReadBooklet(WebText(args, "id"));
            case "bookletPage":
                return await ReadBookletPage(args);
            case "dsp":
                return ReadDsp();
            case "editDsp":
                return await EditDsp(args);
            case "listenSignal":
                return ListenSignal(args.GetProperty("enabled").GetBoolean());
            case "inspectChannel":
                channel = Math.Clamp(args.GetProperty("channel").GetInt32(), 0, Math.Max(0, (playback?.SourceChannels.Length ?? 1) - 1));
                playback?.Inspect(channel);
                break;
            case "signalReport":
                return await ReadSignalReport();
            case "hardwareMute":
                SetHardwareMute(args.GetProperty("muted").GetBoolean());
                break;
            case "desktop":
                OpenDesktopAction(WebText(args, "action"));
                break;
            default:
                throw new InvalidOperationException("Unknown player command.");
        }
        return null;
    }

    private void OpenDesktopAction(string action)
    {
        switch (action)
        {
            case "addFiles": AddFiles(this, new RoutedEventArgs()); break;
            case "watchFolder": AddFolder(this, new RoutedEventArgs()); break;
            case "reindex": ReindexLibrary(this, new RoutedEventArgs()); break;
            case "fullscreen": SwitchFullscreen(); break;
            case "shuffle":
                ToggleShuffle();
                PublishWebState(force: true);
                break;
            default: throw new InvalidOperationException("Unknown desktop action.");
        }
    }

    private void ApplyGlassroomTheme(bool dark)
    {
        WindowSurface.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        SystemBackdrop ??= Environment.GetCommandLineArgs().Contains("--acrylic")
            ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
        AppWindow.TitleBar.BackgroundColor = dark ? ColorHelper.FromArgb(255, 8, 8, 8) : ColorHelper.FromArgb(255, 242, 242, 242);
        AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.BackgroundColor;
        AppWindow.TitleBar.ForegroundColor = dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = AppWindow.TitleBar.ForegroundColor;
    }

    private void TickGlassroom()
    {
        if (!ready || !glassroomConnected)
            return;
        PublishWebState(force: false);
        if (presentationVisible && playback?.State == PlaybackState.Playing && track is { } current)
            PostGlassroom(new { v = 1, @event = "progress", data = new { id = current.Key, position = playback.Position } });
    }

    private WebStatus ReadWebStatus() =>
        new(track?.Key, playback?.State == PlaybackState.Playing, outputVolume / 100, volumeEnabled, status,
            webCatalogRevision, outputDevice?.Name ?? "", shuffleEnabled, AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);

    private double WebPosition() => playback?.Position ?? resumePosition;

    private object WebPlayback(WebStatus state)
    {
        var current = track;
        WebTrack[] queue = WebQueue();
        return new
        {
            track = current is null ? null : ToWebTrack(current),
            state.Playing,
            position = WebPosition(),
            state.Volume,
            queue,
            queueIndex = current is null ? -1 : 0,
            desktop = new { state.Status, state.VolumeEnabled, state.CatalogRevision, state.Output, state.Shuffled, state.Fullscreen }
        };
    }

    private void PublishWebState(bool force)
    {
        UpdateMediaControls(force);
        if (!glassroomConnected)
            return;
        var state = ReadWebStatus();
        if (!force && state == publishedWebStatus)
            return;
        publishedWebStatus = state;
        PostGlassroom(new { v = 1, @event = "state", data = WebPlayback(state) });
    }

    private void PostGlassroom(object value)
    {
        try { glassroom?.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, WebJson)); }
        catch (Exception error) { Diagnostics.Write("Sending a Glassroom message", error); }
    }

    private static string WebText(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
